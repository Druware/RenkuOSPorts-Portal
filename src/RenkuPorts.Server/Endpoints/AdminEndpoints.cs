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
    public record AutoUpdateRequest(bool Enabled, int IntervalHours, string[]? Platforms);
    public record AutoUpdateDto(bool Enabled, int IntervalHours, string[] Platforms, bool Running, DateTime? LastRunAt, string? LastCommit, AutoUpdateResult? LastResult);

    private static async Task<AutoUpdateDto> AutoUpdateAsync(AppDbContext db)
    {
        var s = await db.AutoUpdateSettings.AsNoTracking().SingleAsync(x => x.Id == AutoUpdateSettings.SingletonId);
        return new(s.Enabled, s.IntervalHours, s.Platforms.Split(',', StringSplitOptions.RemoveEmptyEntries),
            s.RunningSince > DateTime.UtcNow - AutoUpdater.StaleAfter, s.LastRunAt, s.LastCommit, AutoUpdater.ReadResult(s));
    }

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

        g.MapGet("/recipes", async (string? search, int? page, int? pageSize, AppDbContext db) =>
        {
            var q = db.Recipes.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(r => r.PortName.Contains(search));
            return await Paged<RecipeDto>.FromAsync(q, o => o.OrderBy(r => r.PortName).ThenByDescending(r => r.Version).ThenBy(r => r.Id),
                r => new RecipeDto(r.Id, r.PortName, r.Category, r.Version, r.RecipePath, r.RecipeUpdatedAt), page, pageSize);
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

            var ids = (await QueueAsync(db, recipe, req.Platforms, users.GetUserId(user)!)).Select(j => j.Id).ToList();
            return Results.Ok(await db.Jobs.Where(j => ids.Contains(j.Id)).OrderBy(j => j.Id).Select(Project.Job).ToListAsync());
        });

        g.MapGet("/jobs", async (JobStatus? status, string? platform, int? page, int? pageSize, AppDbContext db) =>
        {
            var q = db.Jobs.AsQueryable();
            if (status is not null) q = q.Where(j => j.Status == status);
            if (!string.IsNullOrEmpty(platform)) q = q.Where(j => j.Platform == platform);
            return await Paged<JobDto>.FromAsync(q, o => o.OrderByDescending(j => j.Id), Project.Job, page, pageSize);
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

        g.MapPost("/repo/{platform}/reindex", (string platform, RepoIndexer indexer) =>
        {
            if (!Validate.IsPlatform(platform)) return Results.NotFound();
            if (!indexer.Tools.Available) return Results.Problem(statusCode: 503, title: indexer.DisabledReason);
            indexer.RequestReindex(platform);
            return Results.Accepted();
        });

        // Only published packages (what the listings show); uploads of a running job are left to its client.
        g.MapDelete("/repo/{platform}/packages/{fileName}", async (string platform, string fileName, ClaimsPrincipal user, AppDbContext db,
            BlobContainerClient blobs, RepoIndexer indexer, ILoggerFactory logs) =>
        {
            if (!Validate.IsPlatform(platform) || !Validate.IsHpkgName(fileName)) return Results.NotFound();
            var pkg = await PublicEndpoints.Published(db).FirstOrDefaultAsync(p => p.Platform == platform && p.FileName == fileName);
            if (pkg is null) return Results.NotFound();
            db.Packages.Remove(pkg);
            await db.SaveChangesAsync();
            indexer.MarkDirty(platform);
            var log = logs.CreateLogger("AdminEndpoints");
            log.LogInformation("{User} deleted package {Platform}/{FileName} (sha256 {Sha256}, job {JobId}, import {ImportId})",
                user.Identity?.Name, platform, fileName, pkg.Sha256, pkg.JobId, pkg.ImportId);
            // shortcut: an upload that reuses this blob between this check and the delete would lose its bytes; uploads of an existing name are rare, add a blob lease if it matters.
            if (!await db.Packages.AnyAsync(p => p.BlobName == pkg.BlobName))
            {
                try { await blobs.GetBlobClient(pkg.BlobName).DeleteIfExistsAsync(); }
                catch (Azure.RequestFailedException e)
                {
                    // The package is gone (row deleted); only its bytes are left behind.
                    log.LogError(e, "Deleting blob {Blob} of deleted package {Platform}/{FileName} failed; the blob is orphaned", pkg.BlobName, platform, fileName);
                }
            }
            return Results.NoContent();
        });

        g.MapGet("/settings/auto-update", (AppDbContext db) => AutoUpdateAsync(db));

        g.MapPut("/settings/auto-update", async (AutoUpdateRequest req, ClaimsPrincipal user, AppDbContext db, ILoggerFactory logs) =>
        {
            var platforms = req.Platforms ?? [];
            if (req.IntervalHours is < 1 or > 24 * 30)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["intervalHours"] = ["From 1 to 720 hours."] });
            if (platforms.Any(p => !Validate.IsPlatform(p)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["platforms"] = [$"Any of: {string.Join(", ", Validate.Platforms)}."] });
            var list = string.Join(',', Validate.Platforms.Where(platforms.Contains)); // canonical order, no duplicates
            await db.AutoUpdateSettings.Where(s => s.Id == AutoUpdateSettings.SingletonId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Enabled, req.Enabled).SetProperty(x => x.IntervalHours, req.IntervalHours).SetProperty(x => x.Platforms, list));
            logs.CreateLogger("AdminEndpoints").LogInformation("{User} set auto-update: enabled {Enabled}, every {Hours} h, platforms {Platforms}",
                user.Identity?.Name, req.Enabled, req.IntervalHours, list);
            return Results.Ok(await AutoUpdateAsync(db));
        });

        g.MapPost("/settings/auto-update/run", async (AutoUpdater updater, AppDbContext db, CancellationToken ct) =>
            await updater.RunNowAsync(ct) is null
                ? Results.Problem(statusCode: 409, title: "An auto-update is already running")
                : Results.Ok(await AutoUpdateAsync(db)));

        g.MapGet("/dashboard", async (AppDbContext db, RepoIndexer indexer) =>
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
                RepoIndex = await Task.WhenAll(Validate.Platforms.Select(async p =>
                {
                    var s = await indexer.StateAsync(p);
                    return new
                    {
                        Platform = p, s?.BuiltAt, PackageCount = s?.Packages.Count ?? 0,
                        Excluded = s?.Excluded.Select(x => new { x.FileName, x.Reason }) ?? [],
                        Error = indexer.Tools.Available ? s?.Error : indexer.DisabledReason,
                    };
                })),
            };
        });
    }

    /// <summary>One recipe sync at a time per instance (it may run git in the shared checkout); <see cref="AutoUpdater"/> takes it too.</summary>
    internal static readonly SemaphoreSlim SyncLock = new(1, 1);

    /// <summary>
    /// Queues <paramref name="recipe"/> for each of <paramref name="platforms"/> that has no Queued or InProgress job for it yet.
    /// <paramref name="userId"/> null means queued by <see cref="AutoUpdater"/>. Returns the jobs created.
    /// </summary>
    internal static async Task<List<Job>> QueueAsync(AppDbContext db, Recipe recipe, IEnumerable<string> platforms, string? userId)
    {
        var busy = await db.Jobs.Where(j => j.RecipeId == recipe.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.InProgress))
            .Select(j => j.Platform).ToListAsync();
        var jobs = platforms.Distinct().Except(busy).Select(p => new Job
        {
            RecipeId = recipe.Id, Platform = p, Status = JobStatus.Queued, QueuedAt = DateTime.UtcNow, QueuedByUserId = userId,
        }).ToList();
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync();
        return jobs;
    }

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
