using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;

namespace RenkuPorts.Server.Services;

/// <summary>
/// Decides Verifying jobs: computes the content digest (<see cref="PackageDigest"/>) of each completed build's port packages,
/// then, with <c>Verification:RequiredMatches</c> (default 2) = k:
/// <list type="number">
/// <item>k builds with the same digest: the earliest of them is published (<see cref="Verification.ApproveAsync"/>).</item>
/// <item>Fewer than k builds: the job is queued for the next build.</item>
/// <item>The newest build differs from an earlier one only in timestamp-like bytes: published at once if the port is flagged
/// (<see cref="DateEmbeddingPort"/>), else AwaitingOverride and the admins are emailed.</item>
/// <item>Otherwise one more build (up to k + 1 builds; with k = 2 the third build decides), then Failed and the admins are emailed.</item>
/// </list>
/// Runs every few seconds and right after a build completes. Instances may run it concurrently: digests are deterministic, and each
/// decision is one SaveChanges guarded by the job's row version.
/// </summary>
public sealed class BuildVerifier(IServiceScopeFactory scopes, BlobContainerClient blobs, RepoIndexer indexer, PriorityComputer priorities,
    IConfiguration cfg, TimeProvider time, ILogger<BuildVerifier> log) : BackgroundService
{
    private readonly SemaphoreSlim _kick = new(0);

    public int RequiredMatches => Math.Clamp(cfg.GetValue("Verification:RequiredMatches", 2), 1, 5);

    /// <summary>Runs a pass soon (a build completed).</summary>
    public void Kick() { if (_kick.CurrentCount == 0) _kick.Release(); }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!cfg.GetValue("Verification:Schedule", true)) return; // tests call RunOnceAsync
        while (!stop.IsCancellationRequested)
        {
            try { await RunOnceAsync(stop); }
            catch (Exception e) when (!stop.IsCancellationRequested) { log.LogError(e, "Verifying builds failed"); }
            try { await _kick.WaitAsync(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Verifies every Verifying job once. A job whose verification throws is logged and tried again next pass.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        List<long> ids;
        using (var scope = scopes.CreateScope())
            ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.Where(j => j.Status == JobStatus.Verifying).Select(j => j.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await VerifyAsync(id, ct); }
            catch (DbUpdateConcurrencyException) { log.LogInformation("Job {Job} changed while being verified; skipped", id); }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogError(e, "Verifying job {Job} failed", id); }
        }
    }

    private async Task VerifyAsync(long id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.Include(j => j.Recipe).Include(j => j.Builds).ThenInclude(b => b.Packages).FirstAsync(j => j.Id == id, ct);
        if (job.Status != JobStatus.Verifying) return;
        var done = job.Builds.Where(b => b.Status == JobBuildStatus.Completed).OrderBy(b => b.Number).ToList();

        var work = Directory.CreateTempSubdirectory("verify-").FullName;
        try
        {
            foreach (var b in done.Where(b => b.Digest is null))
            {
                foreach (var p in b.Packages.Where(p => !p.Extra && p.Digest is null))
                {
                    var dir = Path.Combine(work, p.Id.ToString());
                    var file = Path.Combine(work, $"{p.Id}.hpkg");
                    await blobs.GetBlobClient(p.BlobName).DownloadToAsync(file, ct);
                    var listing = await PackageDigest.ListAsync(indexer.Tools, file, p.FileName, dir, ct);
                    if (listing.Raw) log.LogWarning("Job {Job}: {Package} is compared as raw bytes: {Reason}", job.Id, p.FileName, listing.RawReason);
                    (p.ListingJson, p.Digest) = (JsonSerializer.Serialize(listing, PackageDigest.Json), PackageDigest.Hash(listing));
                    File.Delete(file);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                b.Digest = PackageDigest.BuildHash(b.Packages.Where(p => !p.Extra).Select(p => (p.FileName, p.Digest!)));
                await db.SaveChangesAsync(ct); // digests are kept even if the decision below has to be retried
            }

            var k = RequiredMatches;
            var notifier = sp.GetRequiredService<INotifier>();
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            var logs = sp.GetRequiredService<ILoggerFactory>();
            var name = $"{job.Recipe!.PortName} {job.Recipe.Version} ({job.Platform})";
            var now = time.GetUtcNow().UtcDateTime;

            if (done.GroupBy(b => b.Digest).FirstOrDefault(g => g.Count() >= k) is { } match)
            {
                await ApproveOrFailAsync(db, job, match.First(), "Match", null, null, OneClient(match.Take(k)), now, users, notifier, logs, ct);
                return;
            }
            if (done.Count < k)
            {
                job.Status = JobStatus.Queued;
                await db.SaveChangesAsync(ct);
                return;
            }

            var latest = done[^1];
            List<BuildDiff> diffs;
            if (latest.DiffJson is null)
            {
                diffs = [];
                foreach (var earlier in done[..^1]) diffs.Add(await DiffAsync(earlier, latest, work, ct));
                latest.DiffJson = JsonSerializer.Serialize(diffs, PackageDigest.Json);
            }
            else diffs = JsonSerializer.Deserialize<List<BuildDiff>>(latest.DiffJson, PackageDigest.Json)!;

            if (diffs.FirstOrDefault(d => d.TimestampLike) is { } ts)
            {
                if (await db.DateEmbeddingPorts.AnyAsync(p => p.PortName == job.Recipe.PortName, ct))
                {
                    var partner = done.First(b => b.Number == ts.AgainstBuild);
                    await ApproveOrFailAsync(db, job, partner, "EmbedsDates", null,
                        $"builds {ts.AgainstBuild} and {latest.Number} differ only in timestamps; the port is flagged as embedding dates",
                        OneClient([partner, latest]), now, users, notifier, logs, ct);
                    return;
                }
                job.Status = JobStatus.AwaitingOverride;
                await db.SaveChangesAsync(ct);
                await BuildClientApi.NotifyAdmins(users, notifier, logs, $"[RenkuPorts] {name} needs an override",
                    $"Job {job.Id}: builds {ts.AgainstBuild} and {latest.Number} of {name} differ, and every difference looks like an embedded date or time. " +
                    "Review the differences on the Ports dashboard (Jobs) and approve one build, or cancel the job.");
                return;
            }
            if (done.Count < k + 1)
            {
                job.Status = JobStatus.Queued;
                await db.SaveChangesAsync(ct);
                log.LogInformation("Job {Job}: build {Build} differs from the earlier builds; queued for another build", job.Id, latest.Number);
                return;
            }
            await Verification.FailAsync(db, job, $"Builds {string.Join(", ", done.Select(b => b.Number))} all differ.", now, users, notifier, logs);
        }
        finally
        {
            Directory.Delete(work, true);
            ClearExtractedAttributes();
        }
    }

    /// <summary>
    /// <c>package extract</c> on Linux keeps the files' Haiku attributes in a directory compiled into the tools (libroot_build),
    /// keyed by inode, and never removes them. The digest reads attributes from <c>package list -a</c>, not from there, so they are
    /// deleted after each job; the directory must exist and be writable or every extraction fails (Dockerfile).
    /// </summary>
    private void ClearExtractedAttributes()
    {
        var dir = cfg["Verification:AttributesDir"] ?? "/b/haiku/generated/attributes";
        if (!Directory.Exists(dir)) return;
        foreach (var e in Directory.EnumerateDirectories(dir))
            try { Directory.Delete(e, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The agreeing builds all came from one client (a deleted client is known by its recorded name).</summary>
    private static bool OneClient(IEnumerable<JobBuild> builds) => builds.Select(b => b.ClientId?.ToString() ?? b.ClientName).Distinct().Count() == 1;

    private async Task ApproveOrFailAsync(AppDbContext db, Job job, JobBuild build, string kind, string? by, string? reason, bool selfVerified, DateTime now,
        UserManager<AppUser> users, INotifier notifier, ILoggerFactory logs, CancellationToken ct)
    {
        if (await Verification.ApproveAsync(db, blobs, job, build, kind, by, reason, now, selfVerified) is { } conflict)
            await Verification.FailAsync(db, job, conflict, now, users, notifier, logs);
        else
        {
            indexer.MarkDirty(job.Platform);
            priorities.MarkDirty();
            log.LogInformation("Job {Job}: build {Build} published ({Kind})", job.Id, build.Number, kind);
        }
    }

    /// <summary>The differences of <paramref name="b"/> from <paramref name="a"/>; packages are downloaded and extracted again only where file bytes differ.</summary>
    private async Task<BuildDiff> DiffAsync(JobBuild a, JobBuild b, string work, CancellationToken ct)
    {
        Dictionary<string, PackageListing> Listings(JobBuild x) => x.Packages.Where(p => !p.Extra)
            .ToDictionary(p => p.FileName, p => JsonSerializer.Deserialize<PackageListing>(p.ListingJson!, PackageDigest.Json)!);
        var (la, lb) = (Listings(a), Listings(b));
        var dirs = new Dictionary<long, string>();
        async Task<string> Dir(JobBuild x, PackageListing l, string fileName)
        {
            var p = x.Packages.First(p => p.FileName == fileName && !p.Extra);
            if (dirs.TryGetValue(p.Id, out var known)) return known;
            var dir = Path.Combine(work, $"diff-{p.Id}");
            if (Directory.Exists(dir)) return dirs[p.Id] = dir; // extracted by the comparison with another build
            Directory.CreateDirectory(dir);
            var file = Path.Combine(work, $"diff-{p.Id}.hpkg");
            await blobs.GetBlobClient(p.BlobName).DownloadToAsync(file, ct);
            if (l.Raw) File.Move(file, Path.Combine(dir, fileName));
            else if (await indexer.Tools.TryRunAsync("package", ["extract", "-C", dir, file], ct) is null)
                throw new InvalidOperationException($"package extract {fileName} failed");
            return dirs[p.Id] = dir;
        }
        return await PackageDigest.CompareAsync(a.Number, la, lb, f => Dir(a, la[f], f), f => Dir(b, lb[f], f), ct);
    }
}

/// <summary>Publishing and failing a verified job; shared by <see cref="BuildVerifier"/> and the admin override.</summary>
public static class Verification
{
    /// <summary>
    /// Publishes <paramref name="build"/>'s port packages as <see cref="Package"/> rows and marks the job Succeeded, in one SaveChanges
    /// (the job's row version guards it), then deletes the other builds' staged blobs. A name already published with the same bytes
    /// is skipped. Returns null, or why nothing was published (a name already published with other bytes).
    /// </summary>
    /// <param name="selfVerified">The agreeing builds came from one client; recorded on the job and its packages. Never for an admin override.</param>
    public static async Task<string?> ApproveAsync(AppDbContext db, BlobContainerClient blobs, Job job, JobBuild build, string kind, string? by, string? reason,
        DateTime now, bool selfVerified = false)
    {
        var pkgs = build.Packages.Where(p => !p.Extra).OrderBy(p => p.Id).ToList();
        var names = pkgs.Select(p => p.FileName).ToList();
        var existing = await db.Packages.Where(p => p.Platform == job.Platform && names.Contains(p.FileName)).ToDictionaryAsync(p => p.FileName, p => p.Sha256);
        if (pkgs.FirstOrDefault(p => existing.TryGetValue(p.FileName, out var sha) && sha != p.Sha256) is { } clash)
            return $"{clash.FileName} is already published with different content; build {build.Number} was not published.";
        var recipeDate = job.Recipe?.RecipeUpdatedAt ?? await db.Recipes.Where(r => r.Id == job.RecipeId).Select(r => r.RecipeUpdatedAt).FirstAsync();
        var port = job.Recipe?.PortName ?? await db.Recipes.Where(r => r.Id == job.RecipeId).Select(r => r.PortName).FirstAsync();
        db.Packages.AddRange(pkgs.Where(p => !existing.ContainsKey(p.FileName)).Select(p => new Package
        {
            FileName = p.FileName, Platform = job.Platform, BlobName = p.BlobName, Size = p.Size, Sha256 = p.Sha256, BuildDate = p.BuildDate,
            UploadedAt = p.UploadedAt, RecipeUpdatedAt = recipeDate, PortName = port, BuiltByUserId = build.OwnerId, ClientId = build.ClientId, JobId = job.Id,
            SelfVerified = selfVerified,
        }));
        job.SelfVerified = selfVerified;
        job.Status = JobStatus.Succeeded;
        job.CompletedAt = now;
        job.ApprovedBuildId = build.Id;
        job.Approval = kind;
        job.ApprovedBy = by;
        job.ApprovedAt = now;
        job.ApprovalReason = reason;
        await db.SaveChangesAsync();
        await DropBlobsAsync(db, blobs, job.Builds.Where(b => b.Id != build.Id).Select(b => b.Id).ToList());
        return null;
    }

    /// <summary>
    /// Fails the job, files it under a (Recipe) build issue and emails the admins. The builds' staged blobs are kept, so an admin can still
    /// accept one of them (approve endpoint); they are deleted when a build is accepted or the job is requeued.
    /// </summary>
    public static async Task FailAsync(AppDbContext db, Job job, string summary, DateTime now,
        UserManager<AppUser> users, INotifier notifier, ILoggerFactory logs)
    {
        job.Status = JobStatus.Failed;
        job.FailureSummary = summary;
        job.CompletedAt = now;
        var (issue, _) = await BuildIssues.CaptureAsync(db, job, summary, BuildIssueKind.Recipe, now);
        await db.SaveChangesAsync();
        var name = await db.Recipes.Where(r => r.Id == job.RecipeId).Select(r => r.PortName + " " + r.Version).FirstAsync();
        // Mailed per failure, not per issue: an admin may still accept one of the builds, which is specific to this job.
        await BuildClientApi.NotifyAdmins(users, notifier, logs, $"[RenkuPorts] {name} failed verification on {job.Platform}",
            $"Job {job.Id} ({name}, {job.Platform}): {summary}\n\nFiled under build issue #{issue.Id}; the port is not queued again on {job.Platform} until it is resolved.\n\n" +
            "The builds and their differences are on the Ports dashboard (Jobs).");
    }

    /// <summary>Discards a job's builds that still count (cancel, requeue): status Discarded, staged blobs deleted.</summary>
    public static async Task DiscardBuildsAsync(AppDbContext db, BlobContainerClient blobs, long jobId)
    {
        await db.JobBuilds.Where(b => b.JobId == jobId && (b.Status == JobBuildStatus.Completed || b.Status == JobBuildStatus.InProgress))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, JobBuildStatus.Discarded));
        await DropBlobsAsync(db, blobs, await db.JobBuilds.Where(b => b.JobId == jobId).Select(b => b.Id).ToListAsync());
    }

    /// <summary>
    /// Deletes the staged blobs of <paramref name="buildIds"/>: rows first (BlobName cleared), then each blob no published package and
    /// no other staged package still references (identical bytes share one content-addressed blob).
    /// </summary>
    public static async Task DropBlobsAsync(AppDbContext db, BlobContainerClient blobs, List<long> buildIds)
    {
        var names = await db.StagedPackages.Where(p => buildIds.Contains(p.BuildId) && p.BlobName != "").Select(p => p.BlobName).Distinct().ToListAsync();
        if (names.Count == 0) return;
        await db.StagedPackages.Where(p => buildIds.Contains(p.BuildId) && p.BlobName != "").ExecuteUpdateAsync(s => s.SetProperty(p => p.BlobName, ""));
        // shortcut: an upload of identical bytes between this check and the delete would lose its blob; identical uploads across builds are rare, add a blob lease if it matters.
        foreach (var n in names)
            if (!await db.Packages.AnyAsync(p => p.BlobName == n) && !await db.StagedPackages.AnyAsync(p => p.BlobName == n))
                await blobs.GetBlobClient(n).DeleteIfExistsAsync();
    }
}
