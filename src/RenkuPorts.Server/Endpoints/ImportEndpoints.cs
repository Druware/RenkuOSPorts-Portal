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
    /// <summary>Starts an import. The optional settings keep it gentle on the source; omitted, it runs 4 downloads at once, unpaced.</summary>
    /// <param name="Parallelism">Downloads at once, 1 to 4.</param>
    /// <param name="DelayMinSeconds">Shortest random pause before each download, 0 to 300 seconds.</param>
    /// <param name="DelayMaxSeconds">Longest random pause before each download, 0 to 300 seconds, at least <paramref name="DelayMinSeconds"/>.</param>
    /// <param name="MaxKilobytesPerSecond">Limit for the whole import in KiB/s, 16 to 1,000,000; 0 for none.</param>
    public record ImportRequest(string? SourceUrl, string? Platform, bool DryRun, int? Parallelism = null, double? DelayMinSeconds = null,
        double? DelayMaxSeconds = null, int? MaxKilobytesPerSecond = null);

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
            var options = new RepoImporter.Options(req.Parallelism ?? 4, req.DelayMinSeconds ?? 0, req.DelayMaxSeconds ?? req.DelayMinSeconds ?? 0, req.MaxKilobytesPerSecond ?? 0);
            var bad = new Dictionary<string, string[]>();
            if (options.Parallelism is < 1 or > 4) bad["parallelism"] = ["From 1 to 4."];
            if (options.DelayMinSeconds is not (>= 0 and <= 300)) bad["delayMinSeconds"] = ["From 0 to 300 seconds."];
            if (options.DelayMaxSeconds is not (>= 0 and <= 300)) bad["delayMaxSeconds"] = ["From 0 to 300 seconds."];
            else if (options.DelayMaxSeconds < options.DelayMinSeconds) bad["delayMaxSeconds"] = ["At least the minimum pause."];
            if (options.MaxKilobytesPerSecond is not (0 or (>= 16 and <= 1_000_000))) bad["maxKilobytesPerSecond"] = ["0 for no limit, or from 16 to 1000000."];
            if (bad.Count > 0) return Results.ValidationProblem(bad);
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
            importer.Start(imp.Id, options);
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
