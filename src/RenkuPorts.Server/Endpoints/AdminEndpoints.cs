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
    public record QueueTopRequest(int? Count);
    public record ApproveRequest(long BuildId, string? Reason, bool EmbedsDates);
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

        g.MapPost("/recipes/sync", async (AppDbContext db, IConfiguration cfg, ILoggerFactory logs, PriorityComputer priorities) =>
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
                var synced = await RecipeScanner.SyncAsync(db, root, logs.CreateLogger("RecipeScanner"));
                priorities.RequestNow();
                return Results.Ok(synced);
            }
            finally
            {
                SyncLock.Release();
            }
        });

        // With a platform each recipe carries its port's priority there; sort=priority (needs a platform) puts the highest first.
        g.MapGet("/recipes", async (string? search, string? platform, string? sort, int? page, int? pageSize, AppDbContext db) =>
        {
            if (platform is not null && !Validate.IsPlatform(platform))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["platform"] = [$"One of: {string.Join(", ", Validate.Platforms)}."] });
            if (sort is not (null or "name") && !(sort == "priority" && platform is not null))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = ["name, or priority with a platform."] });
            var recipes = db.Recipes.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search)) recipes = recipes.Where(r => r.PortName.Contains(search));
            var q = from r in recipes
                    join pp in db.PortPriorities.Where(p => p.Platform == platform) on r.PortName equals pp.PortName into g
                    from pp in g.DefaultIfEmpty()
                    select new { r, pp };
            return Results.Ok(await Paged<RecipeDto>.FromAsync(q, o => (sort == "priority" ? o.OrderByDescending(x => x.pp == null ? 0 : x.pp.Priority).ThenBy(x => x.r.PortName) : o.OrderBy(x => x.r.PortName))
                    .ThenByDescending(x => x.r.Version).ThenBy(x => x.r.Id),
                x => new RecipeDto(x.r.Id, x.r.PortName, x.r.Category, x.r.Version, x.r.RecipePath, x.r.RecipeUpdatedAt,
                    x.pp == null ? null : x.pp.Priority, x.pp == null ? null : x.pp.Dependents, x.pp == null ? null : x.pp.Outstanding,
                    db.DateEmbeddingPorts.Any(d => d.PortName == x.r.PortName)), page, pageSize));
        });

        // Clears a port's "embeds dates" flag (set by an admin override): its timestamp-only mismatches need an admin again.
        g.MapDelete("/ports/{portName}/embeds-dates", async (string portName, ClaimsPrincipal user, AppDbContext db, ILoggerFactory logs) =>
        {
            if (await db.DateEmbeddingPorts.Where(d => d.PortName == portName).ExecuteDeleteAsync() == 0) return Results.NotFound();
            logs.CreateLogger("AdminEndpoints").LogInformation("{User} cleared the embeds-dates flag of {Port}", user.Identity?.Name, portName);
            return Results.NoContent();
        });

        g.MapGet("/priorities", async (AppDbContext db, PriorityComputer priorities) =>
        {
            var stats = await db.PortPriorities.GroupBy(p => p.Platform)
                .Select(x => new { Platform = x.Key, ComputedAt = (DateTime?)x.Max(p => p.ComputedAt), Outstanding = x.Count(p => p.Outstanding) })
                .ToDictionaryAsync(x => x.Platform);
            return new
            {
                priorities.Running, priorities.LastError,
                Platforms = Validate.Platforms.Select(p => stats.TryGetValue(p, out var s) ? s : new { Platform = p, ComputedAt = (DateTime?)null, Outstanding = 0 }),
            };
        });

        g.MapPost("/priorities/recompute", (PriorityComputer priorities) =>
        {
            priorities.RequestNow();
            return Results.Accepted();
        });

        // The highest-priority outstanding ports with nothing published, queued or building on the platform yet.
        g.MapPost("/priorities/{platform}/queue-top", async (string platform, QueueTopRequest? req, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db, PriorityComputer priorities) =>
        {
            if (!Validate.IsPlatform(platform)) return Results.NotFound();
            var count = req?.Count ?? 20;
            if (count is < 1 or > 200) return Results.ValidationProblem(new Dictionary<string, string[]> { ["count"] = ["From 1 to 200."] });
            // No rows yet means "not computed", not "nothing outstanding"; existing rows stay usable during a recompute.
            if (!await db.PortPriorities.AnyAsync(p => p.Platform == platform))
                return Results.Problem(statusCode: 409, title: "Build priorities not calculated yet", detail: priorities.Running
                    ? $"Build priorities for {platform} are being calculated now; try again in a few minutes."
                    : $"Build priorities for {platform} have not been calculated; sync recipes (or recompute priorities) first.");
            var top = await db.PortPriorities.Include(p => p.Recipe)
                .Where(p => p.Platform == platform && p.Outstanding
                    && !PublicEndpoints.Published(db).Any(k => k.Platform == platform && k.PortName == p.PortName)
                    && !db.Jobs.Any(j => j.Platform == platform && j.Recipe!.PortName == p.PortName && JobStatuses.Active.Contains(j.Status)))
                .OrderByDescending(p => p.Priority).ThenBy(p => p.PortName).Take(count).ToListAsync();
            var ids = new List<long>();
            foreach (var p in top) ids.AddRange((await QueueAsync(db, p.Recipe!, [platform], users.GetUserId(user)!)).Select(j => j.Id));
            return Results.Ok(await db.Jobs.Where(j => ids.Contains(j.Id)).OrderBy(j => j.Id).Select(Project.Job).ToListAsync());
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

        // A requeued job starts over: its earlier builds no longer count.
        g.MapPost("/jobs/{id:long}/requeue", (long id, AppDbContext db, BlobContainerClient blobs) =>
            Transition(db, id, [JobStatus.Failed, JobStatus.Cancelled], j =>
            {
                j.Status = JobStatus.Queued;
                j.QueuedAt = DateTime.UtcNow;
                j.CompletedAt = null;
                j.FailureSummary = null;
            }, () => Verification.DiscardBuildsAsync(db, blobs, id)));

        g.MapPost("/jobs/{id:long}/cancel", (long id, AppDbContext db, BlobContainerClient blobs) =>
            Transition(db, id, [JobStatus.Queued, JobStatus.AwaitingOverride], j =>
            {
                j.Status = JobStatus.Cancelled;
                j.CompletedAt = DateTime.UtcNow;
            }, () => Verification.DiscardBuildsAsync(db, blobs, id)));

        // "Approve anyway": publishes any completed build of an unpublished job, recording who, when and why. For a job awaiting an
        // override (timestamp-only differences), one that failed verification (all builds differ) or one with a completed build still
        // waiting for its next build; an open next build is discarded, so its client can no longer upload or complete it.
        // embedsDates flags the port, so its later timestamp-only mismatches are approved automatically.
        g.MapPost("/jobs/{id:long}/approve", async (long id, ApproveRequest req, ClaimsPrincipal user, AppDbContext db, BlobContainerClient blobs,
            RepoIndexer indexer, PriorityComputer priorities, TimeProvider time, ILoggerFactory logs) =>
        {
            var job = await db.Jobs.Include(j => j.Recipe).Include(j => j.Builds).ThenInclude(b => b.Packages).FirstOrDefaultAsync(j => j.Id == id);
            if (job is null) return Results.NotFound();
            if (job.Status is not (JobStatus.Queued or JobStatus.InProgress or JobStatus.AwaitingOverride or JobStatus.Failed))
                return Results.Problem(statusCode: 409, title: $"Job is {job.Status}", detail: "Only an unpublished job that is queued, building, awaiting an override or failed can be approved.");
            var reason = req.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > 1000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["A reason is required (at most 1000 characters)."] });
            if (job.Builds.FirstOrDefault(b => b.Id == req.BuildId && b.Status == JobBuildStatus.Completed) is not { } build)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["buildId"] = ["One of the job's completed builds."] });
            if (build.Packages.Any(p => !p.Extra && p.BlobName == ""))
                return Results.Problem(statusCode: 409, title: "Build no longer staged", detail: $"The packages of build {build.Number} were deleted; it cannot be published.");
            var by = user.Identity?.Name ?? "admin";
            var now = time.GetUtcNow().UtcDateTime;
            var was = job.Status;
            if (req.EmbedsDates && !await db.DateEmbeddingPorts.AnyAsync(d => d.PortName == job.Recipe!.PortName))
                db.DateEmbeddingPorts.Add(new DateEmbeddingPort { PortName = job.Recipe!.PortName, SetBy = by, SetAt = now, Reason = reason });
            // Saved with the approval (the row version guards it): a claim or completion racing in makes it a 409.
            foreach (var open in job.Builds.Where(b => b.Status == JobBuildStatus.InProgress)) open.Status = JobBuildStatus.Discarded;
            (job.ClaimedByClientId, job.ClaimedAt, job.LeaseExpiresAt, job.FailureSummary) = (null, null, null, null);
            try
            {
                if (await Verification.ApproveAsync(db, blobs, job, build, "Override", by, reason, now) is { } conflict)
                    return Results.Problem(statusCode: 409, title: "Cannot publish this build", detail: conflict);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(statusCode: 409, title: "Job changed concurrently", detail: "Reload and try again.");
            }
            indexer.MarkDirty(job.Platform);
            priorities.MarkDirty();
            logs.CreateLogger("AdminEndpoints").LogInformation("{User} approved build {Build} of job {Job} ({Status}) without matching builds: {Reason} (embeds dates: {Flag})",
                by, build.Number, id, was, reason, req.EmbedsDates);
            return Results.Ok(await db.Jobs.Where(j => j.Id == id).Select(Project.Job).FirstAsync());
        });

        g.MapPost("/repo/{platform}/reindex", (string platform, RepoIndexer indexer) =>
        {
            if (!Validate.IsPlatform(platform)) return Results.NotFound();
            if (!indexer.Tools.Available) return Results.Problem(statusCode: 503, title: indexer.DisabledReason);
            indexer.RequestReindex(platform);
            return Results.Accepted();
        });

        // Only published packages (what the listings show); uploads of a running job are left to its client.
        g.MapDelete("/repo/{platform}/packages/{fileName}", async (string platform, string fileName, ClaimsPrincipal user, AppDbContext db,
            BlobContainerClient blobs, RepoIndexer indexer, PriorityComputer priorities, ILoggerFactory logs) =>
        {
            if (!Validate.IsPlatform(platform) || !Validate.IsHpkgName(fileName)) return Results.NotFound();
            var pkg = await PublicEndpoints.Published(db).FirstOrDefaultAsync(p => p.Platform == platform && p.FileName == fileName);
            if (pkg is null) return Results.NotFound();
            db.Packages.Remove(pkg);
            await db.SaveChangesAsync();
            indexer.MarkDirty(platform);
            priorities.MarkDirty();
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
                    Platform = p, Queued = N(p, JobStatus.Queued), InProgress = N(p, JobStatus.InProgress), Verifying = N(p, JobStatus.Verifying),
                    AwaitingOverride = N(p, JobStatus.AwaitingOverride), Succeeded = N(p, JobStatus.Succeeded), Failed = N(p, JobStatus.Failed),
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
    /// Queues <paramref name="recipe"/> for each of <paramref name="platforms"/> that has no active job for it yet.
    /// <paramref name="userId"/> null means queued by <see cref="AutoUpdater"/>. Returns the jobs created.
    /// </summary>
    internal static async Task<List<Job>> QueueAsync(AppDbContext db, Recipe recipe, IEnumerable<string> platforms, string? userId)
    {
        var busy = await db.Jobs.Where(j => j.RecipeId == recipe.Id && JobStatuses.Active.Contains(j.Status))
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
    /// <param name="after">Runs once the change is saved.</param>
    private static async Task<IResult> Transition(AppDbContext db, long id, JobStatus[] from, Action<Job> apply, Func<Task>? after = null)
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
        if (after is not null) await after();
        return Results.Ok(await db.Jobs.Where(j => j.Id == id).Select(Project.Job).FirstAsync());
    }
}
