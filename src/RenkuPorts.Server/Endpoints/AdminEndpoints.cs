using System.Security.Claims;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Admin-only: recipes, the job queue and the dashboard.</summary>
public static class AdminEndpoints
{
    public record QueueRequest(string PortName, string? Version, string[] Platforms);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization("Admin");

        g.MapPost("/recipes/sync", async (AppDbContext db, IConfiguration cfg, ILoggerFactory logs) =>
        {
            if (!SyncLock.Wait(0)) return Results.Problem(statusCode: 409, title: "A recipe sync is already running");
            try
            {
                var root = cfg["PortsTree:Path"];
                if (string.IsNullOrEmpty(root)) return Results.Problem(statusCode: 500, title: "PortsTree:Path is not set");
                // With PortsTree:GitUrl the server keeps its own checkout (App Service has no bind mount).
                if (cfg["PortsTree:GitUrl"] is { Length: > 0 } url)
                    await RecipeScanner.UpdateCheckoutAsync(root, url, cfg["PortsTree:Branch"] ?? "main");
                else if (!Directory.Exists(root))
                    return Results.Problem(statusCode: 500, title: "Ports tree not found", detail: $"PortsTree:Path '{root}' does not exist on the server.");
                return Results.Ok(await RecipeScanner.SyncAsync(db, root, logs.CreateLogger("RecipeScanner")));
            }
            finally
            {
                SyncLock.Release();
            }
        });

        g.MapGet("/recipes", async (string? search, int? take, AppDbContext db) =>
        {
            var q = db.Recipes.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(r => r.PortName.Contains(search));
            return await q.OrderBy(r => r.PortName).ThenByDescending(r => r.Version).Take(Take(take))
                .Select(r => new { r.Id, r.PortName, r.Category, r.Version, r.RecipePath, r.RecipeUpdatedAt }).ToListAsync();
        });

        g.MapPost("/jobs", async (QueueRequest req, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db) =>
        {
            if (req.Platforms is not { Length: > 0 } || req.Platforms.Any(p => !Validate.IsPlatform(p)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["platforms"] = [$"One or more of: {string.Join(", ", Validate.Platforms)}."] });
            var candidates = await db.Recipes.Where(r => r.PortName == req.PortName).ToListAsync();
            var recipe = string.IsNullOrEmpty(req.Version)
                ? candidates.OrderByDescending(r => r.Version, VersionComparer.Instance).FirstOrDefault()
                : candidates.FirstOrDefault(r => r.Version == req.Version);
            if (recipe is null) return Results.Problem(statusCode: 404, title: "Recipe not found", detail: $"{req.PortName} {req.Version}".Trim());

            var busy = await db.Jobs.Where(j => j.RecipeId == recipe.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.InProgress))
                .Select(j => j.Platform).ToListAsync();
            var jobs = req.Platforms.Distinct().Except(busy).Select(p => new Job
            {
                RecipeId = recipe.Id, Platform = p, Status = JobStatus.Queued, QueuedAt = DateTime.UtcNow, QueuedByUserId = users.GetUserId(user)!,
            }).ToList();
            db.Jobs.AddRange(jobs);
            await db.SaveChangesAsync();
            var ids = jobs.Select(j => j.Id).ToList();
            return Results.Ok(await db.Jobs.Where(j => ids.Contains(j.Id)).OrderBy(j => j.Id).Select(Project.Job).ToListAsync());
        });

        g.MapGet("/jobs", async (JobStatus? status, string? platform, int? take, AppDbContext db) =>
        {
            var q = db.Jobs.AsQueryable();
            if (status is not null) q = q.Where(j => j.Status == status);
            if (!string.IsNullOrEmpty(platform)) q = q.Where(j => j.Platform == platform);
            return await q.OrderByDescending(j => j.Id).Take(Take(take)).Select(Project.Job).ToListAsync();
        });

        g.MapGet("/jobs/{id:long}/log", async (long id, AppDbContext db, BlobContainerClient blobs) =>
        {
            var job = await db.Jobs.FindAsync(id);
            if (job is null || !job.HasLog) return Results.NotFound();
            var blob = await blobs.GetBlobClient(BuildClientApi.LogBlob(id)).DownloadStreamingAsync();
            return Results.Stream(blob.Value.Content, "text/plain; charset=utf-8");
        });

        g.MapPost("/jobs/{id:long}/requeue", (long id, AppDbContext db) =>
            Transition(db, id, [JobStatus.Failed, JobStatus.Cancelled], j =>
            {
                j.Status = JobStatus.Queued;
                j.QueuedAt = DateTime.UtcNow;
                j.CompletedAt = null;
            }));

        g.MapPost("/jobs/{id:long}/cancel", (long id, AppDbContext db) =>
            Transition(db, id, [JobStatus.Queued], j =>
            {
                j.Status = JobStatus.Cancelled;
                j.CompletedAt = DateTime.UtcNow;
            }));

        g.MapGet("/dashboard", async (AppDbContext db) =>
        {
            var counts = await db.Jobs.GroupBy(j => new { j.Platform, j.Status }).Select(x => new { x.Key.Platform, x.Key.Status, N = x.Count() }).ToListAsync();
            int N(string p, JobStatus s) => counts.FirstOrDefault(c => c.Platform == p && c.Status == s)?.N ?? 0;
            return new
            {
                Counts = Validate.Platforms.Select(p => new
                {
                    Platform = p, Queued = N(p, JobStatus.Queued), InProgress = N(p, JobStatus.InProgress),
                    Succeeded = N(p, JobStatus.Succeeded), Failed = N(p, JobStatus.Failed),
                }),
                RecentFailures = await db.Jobs.Where(j => j.Status == JobStatus.Failed).OrderByDescending(j => j.CompletedAt).Take(20).Select(Project.Job).ToListAsync(),
                Clients = await db.BuildClients.OrderBy(c => c.Name).Select(Project.Client).ToListAsync(),
            };
        });
    }

    /// <summary>One recipe sync at a time per instance (it may run git in the shared checkout).</summary>
    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    private static int Take(int? take) => Math.Clamp(take ?? 100, 1, 1000);

    /// <summary>Applies a status change if the job is in one of the allowed states; 409 otherwise.</summary>
    private static async Task<IResult> Transition(AppDbContext db, long id, JobStatus[] from, Action<Job> apply)
    {
        var job = await db.Jobs.FindAsync(id);
        if (job is null) return Results.NotFound();
        if (!from.Contains(job.Status))
            return Results.Problem(statusCode: 409, title: $"Job is {job.Status}", detail: $"Allowed from: {string.Join(", ", from)}.");
        apply(job);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: 409, title: "Job changed concurrently", detail: "Reload and try again.");
        }
        return Results.Ok(await db.Jobs.Where(j => j.Id == id).Select(Project.Job).FirstAsync());
    }
}
