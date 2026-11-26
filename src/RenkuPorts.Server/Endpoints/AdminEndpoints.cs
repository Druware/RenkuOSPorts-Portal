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
    /// <summary>Body of <c>POST /api/jobs</c>; <see cref="Force"/> queues platforms where the port is blocked by a build issue too.</summary>
    public record QueueRequest(string PortName, string? Version, string[] Platforms, bool Force = false);
    /// <summary>Body of <c>POST /api/issues/{id}/resolve</c>; <see cref="Requeue"/> requeues the issue's failed jobs.</summary>
    public record ResolveIssueRequest(string? Note, bool Requeue);
    public record EscalateIssueRequest(string? Note);
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
        // What an admin's issues-scoped API token may also call (ApiTokenEndpoints): the build issues and job logs.
        var issues = app.MapGroup("/api").RequireAuthorization("IssueWorker");

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

        // The highest-priority outstanding ports with nothing published, queued or building on the platform yet, and not blocked by a build issue.
        g.MapPost("/priorities/{platform}/queue-top", async (string platform, QueueTopRequest? req, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db, PriorityComputer priorities) =>
        {
            if (!Validate.IsPlatform(platform)) return Results.NotFound();
            var count = req?.Count ?? 20;
            if (count is < 1 or > 200) return Results.ValidationProblem(new Dictionary<string, string[]> { ["count"] = ["From 1 to 200."] });
            if (!await db.PortPriorities.AnyAsync(p => p.Platform == platform)) return NotCalculated(platform, priorities);
            var top = await db.PortPriorities.Include(p => p.Recipe)
                .Where(p => p.Platform == platform && p.Outstanding
                    && !PublicEndpoints.Published(db).Any(k => k.Platform == platform && k.PortName == p.PortName)
                    && !db.Jobs.Any(j => j.Platform == platform && j.Recipe!.PortName == p.PortName && JobStatuses.Active.Contains(j.Status))
                    && !BuildIssues.Blocking(db).Any(j => j.Platform == platform && j.Recipe!.PortName == p.PortName))
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
            // Nothing is queued while any platform is blocked: the admin decides (force) rather than getting a partial queue.
            if (!req.Force && await BuildIssues.BlockingAsync(db, recipe.PortName, req.Platforms) is { Count: > 0 } blocked)
                return Results.Problem(statusCode: 409, title: "Blocked by build issues",
                    detail: $"{recipe.PortName} is blocked by unresolved build issue(s) {string.Join(", ", blocked.Select(b => $"#{b.Value} ({b.Key})"))}. Resolve them, or queue with force.",
                    extensions: new Dictionary<string, object?> { ["issueIds"] = blocked.Values.Distinct().Order().ToList() });

            var ids = (await QueueAsync(db, recipe, req.Platforms, users.GetUserId(user)!, req.Force)).Select(j => j.Id).ToList();
            return Results.Ok(await db.Jobs.Where(j => ids.Contains(j.Id)).OrderBy(j => j.Id).Select(Project.Job).ToListAsync());
        });

        g.MapGet("/jobs", async (JobStatus? status, string? platform, int? page, int? pageSize, AppDbContext db) =>
        {
            var q = db.Jobs.AsQueryable();
            if (status is not null) q = q.Where(j => j.Status == status);
            if (!string.IsNullOrEmpty(platform)) q = q.Where(j => j.Platform == platform);
            return await Paged<JobDto>.FromAsync(q, o => o.OrderByDescending(j => j.Id), Project.Job, page, pageSize);
        });

        issues.MapGet("/jobs/{id:long}/log", async (long id, AppDbContext db, BlobContainerClient blobs) =>
        {
            var job = await db.Jobs.FindAsync(id);
            if (job is null || !job.HasLog) return Results.NotFound();
            var blob = await blobs.GetBlobClient(BuildClientApi.LogBlob(id)).DownloadStreamingAsync();
            return Results.Stream(blob.Value.Content, "text/plain; charset=utf-8");
        });

        g.MapPost("/jobs/{id:long}/requeue", (long id, AppDbContext db, BlobContainerClient blobs) => RequeueAsync(db, blobs, id));

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

        // Build issues (Services/BuildIssues.cs). status: open, escalated, resolved or all; without it, the open and escalated ones.
        issues.MapGet("/issues", async (string? status, AppDbContext db) =>
        {
            if (status is not (null or "open" or "escalated" or "resolved" or "all"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["open, escalated, resolved or all."] });
            var q = db.BuildIssues.AsQueryable();
            if (status is null) q = q.Where(i => i.Status != BuildIssueStatus.Resolved);
            else if (status != "all")
            {
                var only = Enum.Parse<BuildIssueStatus>(status, ignoreCase: true);
                q = q.Where(i => i.Status == only);
            }
            // shortcut: no paging, the 500 last seen; page like /jobs if issues pile up.
            return Results.Ok(await IssueDtosAsync(db, q.OrderByDescending(i => i.LastSeenAt).ThenByDescending(i => i.Id).Take(500)));
        });

        issues.MapGet("/issues/{id:long}", async (long id, AppDbContext db) =>
            (await IssueDtosAsync(db, db.BuildIssues.Where(i => i.Id == id))).FirstOrDefault() is { } issue ? Results.Ok(issue) : Results.NotFound());

        // Resolving unblocks the issue's ports; requeue also requeues its failed jobs (as /jobs/{id}/requeue does), which leave the issue.
        issues.MapPost("/issues/{id:long}/resolve", async (long id, ResolveIssueRequest req, ClaimsPrincipal user, AppDbContext db, BlobContainerClient blobs,
            TimeProvider time, ILoggerFactory logs) =>
        {
            var issue = await db.BuildIssues.FindAsync(id);
            if (issue is null) return Results.NotFound();
            if (issue.Status == BuildIssueStatus.Resolved) return Results.Problem(statusCode: 409, title: "Issue is Resolved", detail: "It was resolved already.");
            if (InvalidNote(req.Note) is { } invalid) return invalid;
            var by = ApiTokenEndpoints.By(user);
            var now = time.GetUtcNow().UtcDateTime;
            (issue.Status, issue.ResolvedBy, issue.ResolvedAt) = (BuildIssueStatus.Resolved, by, now);
            AppendNote(issue, now, by, "resolved", req.Note!.Trim());
            await db.SaveChangesAsync();
            var requeued = new List<long>();
            if (req.Requeue)
                foreach (var jobId in await db.Jobs.Where(j => j.IssueId == id && j.Status == JobStatus.Failed).Select(j => j.Id).ToListAsync())
                    if (await RequeueAsync(db, blobs, jobId) is IStatusCodeHttpResult { StatusCode: 200 }) requeued.Add(jobId);
            logs.CreateLogger("AdminEndpoints").LogInformation("{User} resolved build issue {Issue}; requeued jobs {Jobs}", by, id, requeued);
            return Results.Ok(new { Issue = (await IssueDtosAsync(db, db.BuildIssues.Where(i => i.Id == id))).Single(), Requeued = requeued });
        });

        issues.MapPost("/issues/{id:long}/escalate", async (long id, EscalateIssueRequest req, ClaimsPrincipal user, AppDbContext db, TimeProvider time,
            UserManager<AppUser> users, INotifier notifier, ILoggerFactory logs) =>
        {
            var issue = await db.BuildIssues.FindAsync(id);
            if (issue is null) return Results.NotFound();
            if (issue.Status != BuildIssueStatus.Open) return Results.Problem(statusCode: 409, title: $"Issue is {issue.Status}", detail: "Only an open issue can be escalated.");
            if (InvalidNote(req.Note) is { } invalid) return invalid;
            var by = ApiTokenEndpoints.By(user);
            var note = req.Note!.Trim();
            issue.Status = BuildIssueStatus.Escalated;
            AppendNote(issue, time.GetUtcNow().UtcDateTime, by, "escalated", note);
            await db.SaveChangesAsync();
            var dto = (await IssueDtosAsync(db, db.BuildIssues.Where(i => i.Id == id))).Single();
            await BuildClientApi.NotifyAdmins(users, notifier, logs, $"[RenkuPorts] Build issue #{id} on {issue.Platform} escalated",
                $"{by} escalated build issue #{id} ({issue.Kind}, {issue.Platform}): {note}\n\n{issue.Title}\n\n" +
                $"Seen {issue.Count} time(s) since {issue.FirstSeenAt:u}; ports blocked: {string.Join(", ", dto.Ports.Select(p => p.PortName))}.\n\n" +
                "The issue and its build logs are on the Ports dashboard (Issues).");
            return Results.Ok(dto);
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
    /// Queues <paramref name="recipe"/> for each of <paramref name="platforms"/> that has no active job for it yet and, unless
    /// <paramref name="force"/>, where its port is not blocked by a build issue (<see cref="BuildIssues.Blocking"/>).
    /// <paramref name="userId"/> null means queued by <see cref="AutoUpdater"/>. Returns the jobs created.
    /// </summary>
    internal static async Task<List<Job>> QueueAsync(AppDbContext db, Recipe recipe, IEnumerable<string> platforms, string? userId, bool force = false)
    {
        var busy = await db.Jobs.Where(j => j.RecipeId == recipe.Id && JobStatuses.Active.Contains(j.Status))
            .Select(j => j.Platform).ToListAsync();
        if (!force) busy.AddRange((await BuildIssues.BlockingAsync(db, recipe.PortName, platforms)).Keys);
        var jobs = platforms.Distinct().Except(busy).Select(p => new Job
        {
            RecipeId = recipe.Id, Platform = p, Status = JobStatus.Queued, QueuedAt = DateTime.UtcNow, QueuedByUserId = userId,
        }).ToList();
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync();
        return jobs;
    }

    /// <summary>The issues with their affected ports: each port with a job still filed under the issue, and its latest such job.</summary>
    private static async Task<List<BuildIssueDto>> IssueDtosAsync(AppDbContext db, IQueryable<BuildIssue> issues)
    {
        var list = await issues.AsNoTracking().ToListAsync();
        var ids = list.Select(i => i.Id).ToList();
        var jobs = (await db.Jobs.Where(j => j.IssueId != null && ids.Contains(j.IssueId.Value))
            .Select(j => new { IssueId = j.IssueId!.Value, j.Recipe!.PortName, j.Id }).ToListAsync()).ToLookup(j => j.IssueId);
        return list.Select(i => new BuildIssueDto(i.Id, i.Platform, i.Kind, i.Status, i.Title, i.Signature, i.Count, i.FirstSeenAt, i.LastSeenAt,
            i.Notes, i.ExampleJobId, i.ResolvedBy, i.ResolvedAt,
            jobs[i.Id].GroupBy(j => j.PortName).Select(g => new AffectedPortDto(g.Key, g.Max(j => j.Id))).OrderBy(p => p.PortName).ToList())).ToList();
    }

    /// <summary>A note is required, at most 1000 characters (as the approve endpoint's reason).</summary>
    private static IResult? InvalidNote(string? note) => string.IsNullOrWhiteSpace(note) || note.Trim().Length > 1000
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["A note is required (at most 1000 characters)."] })
        : null;

    private static void AppendNote(BuildIssue issue, DateTime now, string by, string action, string note) =>
        issue.Notes = (issue.Notes is null ? "" : issue.Notes + "\n") + $"{now:yyyy-MM-dd HH:mm} UTC, {by} ({action}): {note}";

    /// <summary>
    /// Requeues a Failed or Cancelled job: it starts over (its earlier builds no longer count) and leaves its build issue, so it no
    /// longer blocks its port (the issue itself stays as it is).
    /// </summary>
    private static Task<IResult> RequeueAsync(AppDbContext db, BlobContainerClient blobs, long id) =>
        Transition(db, id, [JobStatus.Failed, JobStatus.Cancelled], j =>
        {
            j.Status = JobStatus.Queued;
            j.QueuedAt = DateTime.UtcNow;
            j.CompletedAt = null;
            j.FailureSummary = null;
            j.IssueId = null;
        }, () => Verification.DiscardBuildsAsync(db, blobs, id));

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
