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
    public record LogCleanupRequest(int? OlderThanDays, bool? DryRun);
    public record KeepVersions(int Keep);
    public record FileCleanupRequest(bool Orphans, KeepVersions? Superseded, bool Staged, bool? DryRun);
    public record FileCleanupResult(Tally? Orphans, Tally? Superseded, Tally? Staged, Tally Total);
    /// <summary>Body of <c>POST /api/admin/reset</c>: a platform or <c>all</c>, and <see cref="Confirm"/> repeating it exactly.</summary>
    public record ResetRequest(string Platform, string? Confirm);
    public record EgressRequest(int MonthlyCapGb);
    /// <summary>The egress cap and this UTC month's (<see cref="Month"/>, "yyyy-MM") package and index bytes served.</summary>
    public record EgressDto(int MonthlyCapGb, string Month, long BytesServed);
    public record AutoUpdateDto(bool Enabled, int IntervalHours, string[] Platforms, bool Running, DateTime? LastRunAt, string? LastCommit, AutoUpdateResult? LastResult);

    private static async Task<AutoUpdateDto> AutoUpdateAsync(AppDbContext db)
    {
        var s = await db.AutoUpdateSettings.AsNoTracking().SingleAsync(x => x.Id == AutoUpdateSettings.SingletonId);
        return new(s.Enabled, s.IntervalHours, s.Platforms.Split(',', StringSplitOptions.RemoveEmptyEntries),
            s.RunningSince > DateTime.UtcNow - AutoUpdater.StaleAfter, s.LastRunAt, s.LastCommit, AutoUpdater.ReadResult(s));
    }

    private static async Task<EgressDto> EgressAsync(AppDbContext db, TimeProvider time)
    {
        var month = Egress.Month(time);
        var cap = await db.EgressSettings.Where(s => s.Id == EgressSettings.SingletonId).Select(s => s.MonthlyCapGb).SingleAsync();
        return new(cap, month, await db.EgressUsage.Where(u => u.Month == month).Select(u => u.BytesServed).SingleOrDefaultAsync());
    }

    /// <summary>409 for a platform with no <see cref="PortPriority"/> rows: that means "not computed", not "nothing outstanding"
    /// (existing rows stay usable during a recompute).</summary>
    private static IResult NotCalculated(string platform, PriorityComputer priorities) =>
        Results.Problem(statusCode: 409, title: "Build priorities not calculated yet", detail: priorities.Running
            ? $"Build priorities for {platform} are being calculated now; try again in a few minutes."
            : $"Build priorities for {platform} have not been calculated; sync recipes (or recompute priorities) first.");

    /// <summary>Sets <see cref="PortPriority.PinnedAt"/> of <paramref name="portName"/> on <paramref name="platform"/> to
    /// <paramref name="pinnedAt"/> (null unpins). 204; 404 for an unknown platform or port, 409 before priorities are computed.</summary>
    private static async Task<IResult> PinAsync(string platform, string portName, DateTime? pinnedAt, ClaimsPrincipal user, AppDbContext db,
        PriorityComputer priorities, ILoggerFactory logs)
    {
        if (!Validate.IsPlatform(platform)) return Results.NotFound();
        if (await db.PortPriorities.Where(p => p.Platform == platform && p.PortName == portName).ExecuteUpdateAsync(s => s.SetProperty(p => p.PinnedAt, pinnedAt)) == 0)
            return await db.PortPriorities.AnyAsync(p => p.Platform == platform) ? Results.NotFound() : NotCalculated(platform, priorities);
        logs.CreateLogger("AdminEndpoints").LogInformation("{User} {Action} {Port} on {Platform}", user.Identity?.Name, pinnedAt is null ? "unpinned" : "pinned", portName, platform);
        return Results.NoContent();
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

        // With a platform each recipe carries its port's priority there; sort=priority (needs a platform) puts pinned ports (newest
        // pin first), then the highest priority first.
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
            return Results.Ok(await Paged<RecipeDto>.FromAsync(q, o => (sort == "priority" ? o.OrderByDescending(x => x.pp == null ? null : x.pp.PinnedAt).ThenByDescending(x => x.pp == null ? 0 : x.pp.Priority).ThenBy(x => x.r.PortName) : o.OrderBy(x => x.r.PortName))
                    .ThenByDescending(x => x.r.Version).ThenBy(x => x.r.Id),
                x => new RecipeDto(x.r.Id, x.r.PortName, x.r.Category, x.r.Version, x.r.RecipePath, x.r.RecipeUpdatedAt,
                    x.pp == null ? null : x.pp.Priority, x.pp == null ? null : x.pp.Dependents, x.pp == null ? null : x.pp.Outstanding,
                    db.DateEmbeddingPorts.Any(d => d.PortName == x.r.PortName), x.pp == null ? null : x.pp.PinnedAt), page, pageSize));
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
            if (!await db.PortPriorities.AnyAsync(p => p.Platform == platform)) return NotCalculated(platform, priorities);
            var top = await db.PortPriorities.Include(p => p.Recipe)
                .Where(p => p.Platform == platform && p.Outstanding
                    && !PublicEndpoints.Published(db).Any(k => k.Platform == platform && k.PortName == p.PortName)
                    && !db.Jobs.Any(j => j.Platform == platform && j.Recipe!.PortName == p.PortName && JobStatuses.Active.Contains(j.Status)))
                .OrderByDescending(p => p.PinnedAt).ThenByDescending(p => p.Priority).ThenBy(p => p.PortName).Take(count).ToListAsync();
            var ids = new List<long>();
            foreach (var p in top) ids.AddRange((await QueueAsync(db, p.Recipe!, [platform], users.GetUserId(user)!)).Select(j => j.Id));
            return Results.Ok(await db.Jobs.Where(j => ids.Contains(j.Id)).OrderBy(j => j.Id).Select(Project.Job).ToListAsync());
        });

        // Pins a port to the top of the platform's build order (again, if pinned already: the newest pin comes first), or unpins it.
        g.MapPut("/priorities/{platform}/{portName}/pin", (string platform, string portName, ClaimsPrincipal user, AppDbContext db, PriorityComputer priorities, ILoggerFactory logs) =>
            PinAsync(platform, portName, DateTime.UtcNow, user, db, priorities, logs));
        g.MapDelete("/priorities/{platform}/{portName}/pin", (string platform, string portName, ClaimsPrincipal user, AppDbContext db, PriorityComputer priorities, ILoggerFactory logs) =>
            PinAsync(platform, portName, null, user, db, priorities, logs));

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
            try { await Cleanup.DeleteIfUnreferencedAsync(db, blobs, pkg.BlobName); }
            catch (Azure.RequestFailedException e)
            {
                // The package is gone (row deleted); only its bytes are left behind.
                log.LogError(e, "Deleting blob {Blob} of deleted package {Platform}/{FileName} failed; the blob is orphaned", pkg.BlobName, platform, fileName);
            }
            return Results.NoContent();
        });

        // Maintenance (Services/Cleanup.cs). dryRun defaults to true, so a request that omits it deletes nothing.
        g.MapPost("/admin/cleanup/logs", async (LogCleanupRequest req, ClaimsPrincipal user, AppDbContext db, BlobContainerClient blobs, TimeProvider time, ILoggerFactory logs) =>
        {
            if (req.OlderThanDays is not (>= 1 and <= 36500))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["olderThanDays"] = ["From 1 to 36500 days."] });
            var dryRun = req.DryRun ?? true;
            if (!Cleanup.Lock.Wait(0)) return Results.Problem(statusCode: 409, title: "A cleanup is already running");
            try
            {
                var tally = await Cleanup.LogsAsync(db, blobs, time.GetUtcNow().UtcDateTime.AddDays(-req.OlderThanDays.Value), dryRun);
                if (!dryRun) logs.CreateLogger("AdminEndpoints").LogInformation("{User} deleted {Count} build logs ({Bytes} bytes) older than {Days} days",
                    user.Identity?.Name, tally.Count, tally.Bytes, req.OlderThanDays);
                return Results.Ok(tally);
            }
            finally { Cleanup.Lock.Release(); }
        });

        g.MapPost("/admin/cleanup/files", async (FileCleanupRequest req, ClaimsPrincipal user, AppDbContext db, BlobContainerClient blobs, TimeProvider time,
            RepoIndexer indexer, PriorityComputer priorities, ILoggerFactory logs) =>
        {
            if (req.Superseded is { Keep: < 1 or > 1000 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["superseded.keep"] = ["From 1 to 1000 versions."] });
            if (!req.Orphans && req.Superseded is null && !req.Staged)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["categories"] = ["Choose orphans, superseded or staged."] });
            var dryRun = req.DryRun ?? true;
            if (!Cleanup.Lock.Wait(0)) return Results.Problem(statusCode: 409, title: "A cleanup is already running");
            try
            {
                var log = logs.CreateLogger("AdminEndpoints");
                // Superseded and staged first: the blobs they free are then gone (or still referenced, in a dry run), never counted again as orphans.
                Tally? superseded = null, staged = null, orphans = null;
                if (req.Superseded is { } s)
                {
                    (superseded, var platforms) = await Cleanup.SupersededAsync(db, blobs, s.Keep, dryRun, log, user.Identity?.Name);
                    if (!dryRun && platforms.Count > 0)
                    {
                        foreach (var p in platforms) indexer.MarkDirty(p);
                        priorities.MarkDirty();
                    }
                }
                if (req.Staged) staged = await Cleanup.StagedAsync(db, blobs, dryRun);
                if (req.Orphans) orphans = await Cleanup.OrphansAsync(db, blobs, time.GetUtcNow() - Cleanup.OrphanMinAge, dryRun);
                Tally[] all = [.. new[] { superseded, staged, orphans }.OfType<Tally>()];
                var total = new Tally { Count = all.Sum(t => t.Count), Bytes = all.Sum(t => t.Bytes) };
                if (!dryRun) log.LogInformation("{User} purged files: superseded {Superseded}, staged {Staged}, orphans {Orphans}; {Bytes} bytes freed",
                    user.Identity?.Name, superseded?.Count, staged?.Count, orphans?.Count, total.Bytes);
                return Results.Ok(new FileCleanupResult(orphans, superseded, staged, total));
            }
            finally { Cleanup.Lock.Release(); }
        });

        // Danger zone: wipes what was built or imported for one platform, or all ("all"); confirm must repeat platform.
        g.MapPost("/admin/reset", async (ResetRequest req, ClaimsPrincipal user, AppDbContext db, BlobContainerClient blobs,
            RepoIndexer indexer, PriorityComputer priorities, ILoggerFactory logs) =>
        {
            if (req.Platform != "all" && !Validate.IsPlatform(req.Platform))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["platform"] = [$"One of: {string.Join(", ", Validate.Platforms)}, all."] });
            if (req.Confirm != req.Platform)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = [$"Type {req.Platform} to confirm."] });
            string[] platforms = req.Platform == "all" ? Validate.Platforms : [req.Platform];
            var stale = DateTime.UtcNow - RepoImporter.StaleAfter;
            if (await db.Imports.Where(i => platforms.Contains(i.Platform) && i.Status == ImportStatus.Running && i.UpdatedAt >= stale)
                    .Select(i => i.Platform).FirstOrDefaultAsync() is { } busy)
                return Results.Problem(statusCode: 409, title: $"An import for {busy} is running", detail: "Cancel it or wait for it to finish.");
            if (!Cleanup.Lock.Wait(0)) return Results.Problem(statusCode: 409, title: "A cleanup is already running");
            try
            {
                var log = logs.CreateLogger("AdminEndpoints");
                var result = new List<Cleanup.ResetTally>();
                foreach (var p in platforms)
                {
                    var t = await Cleanup.ResetAsync(db, blobs, p);
                    indexer.MarkDirty(p);
                    result.Add(t);
                    log.LogWarning("{User} reset {Platform}: {Jobs} jobs, {Builds} builds, {Staged} staged, {Packages} packages, {Imports} imports, {Blobs} blobs ({Bytes} bytes)",
                        user.Identity?.Name, p, t.Jobs, t.Builds, t.StagedPackages, t.Packages, t.Imports, t.Blobs, t.Bytes);
                }
                priorities.MarkDirty();
                return Results.Ok(result);
            }
            finally { Cleanup.Lock.Release(); }
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

        g.MapGet("/settings/egress", (AppDbContext db, TimeProvider time) => EgressAsync(db, time));

        g.MapPut("/settings/egress", async (EgressRequest req, ClaimsPrincipal user, AppDbContext db, TimeProvider time, ILoggerFactory logs) =>
        {
            if (req.MonthlyCapGb is < 1 or > 1_000_000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["monthlyCapGb"] = ["From 1 to 1000000 GB."] });
            await db.EgressSettings.Where(s => s.Id == EgressSettings.SingletonId).ExecuteUpdateAsync(s => s.SetProperty(x => x.MonthlyCapGb, req.MonthlyCapGb));
            logs.CreateLogger("AdminEndpoints").LogInformation("{User} set the egress cap to {Gb} GB a month", user.Identity?.Name, req.MonthlyCapGb);
            return Results.Ok(await EgressAsync(db, time));
        });

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
