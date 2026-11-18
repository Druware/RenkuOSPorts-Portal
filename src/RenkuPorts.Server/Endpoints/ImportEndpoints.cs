using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Admin-only: import the packages another pkgman repository has and this one lacks (see <see cref="RepoImporter"/>).</summary>
public static class ImportEndpoints
{
    public record ImportRequest(string? SourceUrl, string? Platform, bool DryRun);

    public record ImportDto(long Id, string SourceUrl, string Platform, bool DryRun, ImportStatus Status, DateTime StartedAt, DateTime? FinishedAt,
        string StartedBy, int Total, int Missing, int Imported, List<SkippedPackage> Skipped, string? Error);

    private static readonly Expression<Func<Import, ImportDto>> Dto = i => new ImportDto(i.Id, i.SourceUrl, i.Platform, i.DryRun, i.Status,
        i.StartedAt, i.FinishedAt, i.StartedBy!.DisplayName, i.Total, i.Missing, i.Imported, i.Skipped, i.Error);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/imports").RequireAuthorization("Admin");

        g.MapPost("", async (ImportRequest req, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db, RepoImporter importer, RepoIndexer indexer) =>
        {
            if (!Uri.TryCreate(req.SourceUrl?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceUrl"] = ["An http or https repository base URL."] });
            if (!Validate.IsPlatform(req.Platform))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["platform"] = [$"One of: {string.Join(", ", Validate.Platforms)}."] });
            if (!indexer.Tools.Available) return Results.Problem(statusCode: 503, title: indexer.DisabledReason);

            var now = DateTime.UtcNow;
            var stale = now - RepoImporter.StaleAfter;
            await db.Imports.Where(i => i.Platform == req.Platform && i.Status == ImportStatus.Running && i.UpdatedAt < stale)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ImportStatus.Failed).SetProperty(i => i.FinishedAt, now)
                    .SetProperty(i => i.Error, "Interrupted: the server running it stopped. Run it again to continue."));
            var imp = new Import
            {
                SourceUrl = url.AbsoluteUri.TrimEnd('/'), SourceHost = url.Authority, Platform = req.Platform, DryRun = req.DryRun,
                Status = ImportStatus.Running, StartedAt = now, UpdatedAt = now, StartedByUserId = users.GetUserId(user)!,
            };
            db.Imports.Add(imp);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                // The filtered unique index allows one Running import per platform.
                return Results.Problem(statusCode: 409, title: $"An import for {req.Platform} is already running");
            }
            importer.Start(imp.Id);
            return Results.Ok(new { imp.Id });
        });

        g.MapGet("", async (AppDbContext db) => await db.Imports.AsNoTracking().OrderByDescending(i => i.Id).Take(20).Select(Dto).ToListAsync());

        g.MapGet("/{id:long}", async (long id, AppDbContext db) =>
            await db.Imports.AsNoTracking().Where(i => i.Id == id).Select(Dto).FirstOrDefaultAsync() is { } dto ? Results.Ok(dto) : Results.NotFound());

        g.MapPost("/{id:long}/cancel", async (long id, AppDbContext db) =>
        {
            var imp = await db.Imports.FindAsync(id);
            if (imp is null) return Results.NotFound();
            if (imp.Status != ImportStatus.Running) return Results.Problem(statusCode: 409, title: $"Import is {imp.Status}");
            imp.CancelRequested = true;
            if (imp.UpdatedAt < DateTime.UtcNow - RepoImporter.StaleAfter)
            {
                // Nobody is running it any more.
                imp.Status = ImportStatus.Cancelled;
                imp.FinishedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            return Results.Accepted();
        });
    }
}
