using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;

namespace RenkuPorts.Server.Services;

/// <summary>What a cleanup category deleted, or would delete in a dry run.</summary>
/// <remarks>Count is logs, package rows, staged rows or blobs (see each method); Bytes is always the size of the blobs freed.</remarks>
public sealed class Tally
{
    public int Count { get; set; }
    public long Bytes { get; set; }
}

/// <summary>
/// Admin maintenance (<c>/api/admin/cleanup/*</c>): old build logs and package blobs nothing needs any more. Every method takes
/// <c>dryRun</c>, which runs the same queries and size lookups but changes nothing.
/// </summary>
/// <remarks>
/// Package blobs are content-addressed and shared: one blob can back a <see cref="Package"/> and the <see cref="StagedPackage"/>s of
/// the builds that produced it. A blob is deleted only when no package row and no staged row that is still needed names it
/// (<see cref="DeleteIfUnreferencedAsync"/>).
/// </remarks>
public static partial class Cleanup
{
    /// <summary>One cleanup at a time per instance (409 when busy).</summary>
    public static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>
    /// A blob modified more recently than this is never treated as an orphan: an upload writes the package blob before its
    /// database row, and a build writes its log blob while completing, so a young blob without a row may just be in flight.
    /// </summary>
    public static readonly TimeSpan OrphanMinAge = TimeSpan.FromHours(24);

    private const int Batch = 500;

    private static readonly JobStatus[] Finished = [JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled];
    private static readonly JobBuildStatus[] DeadBuilds = [JobBuildStatus.Failed, JobBuildStatus.Expired, JobBuildStatus.Discarded];

    /// <summary>Staged rows whose bytes are no longer needed for verification or an admin's approval: the build no longer counts, or the job is finished.</summary>
    private static readonly Expression<Func<StagedPackage, bool>> Stale = s =>
        s.BlobName != "" && (DeadBuilds.Contains(s.Build!.Status) || Finished.Contains(s.Build!.Job!.Status));

    /// <summary>The complement of <see cref="Stale"/>: staged rows of a counting build of an unfinished job, whose blob must stay.</summary>
    private static readonly Expression<Func<StagedPackage, bool>> Needed = s =>
        s.BlobName != "" && !DeadBuilds.Contains(s.Build!.Status) && !Finished.Contains(s.Build!.Job!.Status);

    [GeneratedRegex(@"^logs/(\d{1,18})\.log$")]
    private static partial Regex LogName();

    /// <summary>A content-addressed package blob: <c>{platform}/{sha256}/{fileName}</c> (the platform is checked separately).</summary>
    [GeneratedRegex(@"^([^/]+)/[0-9a-f]{64}/[^/]+$")]
    private static partial Regex PackageBlobName();

    /// <summary>
    /// Deletes blob <paramref name="name"/> unless a package row or a still-needed staged row names it. Returns the bytes freed
    /// (would be freed), 0 if it is kept or already gone.
    /// </summary>
    /// <param name="removing">In a dry run, how many package rows naming this blob the caller would have removed (they still exist).</param>
    /// <remarks>shortcut: an upload that reuses this blob between the check and the delete would lose its bytes; uploads of identical bytes are rare, add a blob lease if it matters.</remarks>
    public static async Task<long> DeleteIfUnreferencedAsync(AppDbContext db, BlobContainerClient blobs, string name, bool dryRun = false, int removing = 0)
    {
        if (await db.Packages.CountAsync(p => p.BlobName == name) > (dryRun ? removing : 0)) return 0;
        if (await db.StagedPackages.Where(Needed).AnyAsync(s => s.BlobName == name)) return 0;
        var blob = blobs.GetBlobClient(name);
        long size;
        try { size = (await blob.GetPropertiesAsync()).Value.ContentLength; }
        catch (RequestFailedException e) when (e.Status == 404) { return 0; }
        if (!dryRun && !await blob.DeleteIfExistsAsync()) return 0;
        return size;
    }

    /// <summary>
    /// Deletes the build logs of finished jobs (Succeeded, Failed, Cancelled) completed before <paramref name="cutoff"/> and clears
    /// <see cref="Job.HasLog"/>. Count: logs.
    /// </summary>
    public static async Task<Tally> LogsAsync(AppDbContext db, BlobContainerClient blobs, DateTime cutoff, bool dryRun)
    {
        var tally = new Tally();
        long after = 0;
        while (true)
        {
            var ids = await db.Jobs.Where(j => j.HasLog && Finished.Contains(j.Status) && j.CompletedAt < cutoff && j.Id > after)
                .OrderBy(j => j.Id).Select(j => j.Id).Take(Batch).ToListAsync();
            if (ids.Count == 0) return tally;
            after = ids[^1];
            foreach (var id in ids)
            {
                var blob = blobs.GetBlobClient(BuildClientApi.LogBlob(id));
                long size = 0;
                try { size = (await blob.GetPropertiesAsync()).Value.ContentLength; }
                catch (RequestFailedException e) when (e.Status == 404) { } // flag set without a blob: only the flag is cleared
                // Flag first, re-checked in the WHERE: a job requeued meanwhile keeps its log (its next build overwrites the same blob).
                if (!dryRun && await db.Jobs.Where(j => j.Id == id && j.HasLog && Finished.Contains(j.Status))
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.HasLog, false)) == 0)
                    continue;
                if (!dryRun && size > 0) await blob.DeleteIfExistsAsync();
                tally.Count++;
                tally.Bytes += size;
            }
        }
    }

    /// <summary>
    /// Removes the published packages of all but the newest <paramref name="keep"/> versions of each port on each platform, like the
    /// admin package delete (row, then the blob unless still referenced). Count: package rows. Returns the platforms changed.
    /// </summary>
    /// <remarks>
    /// The version is the second '-'-separated field of the file name (<c>name-version-revision-arch.hpkg</c>; Haiku versions contain
    /// no '-'); all revisions and subpackages of a version count as that version. File names that do not have four fields are kept.
    /// shortcut: loads id, platform, port and names of every published package at once (a few MB for 100k packages); page it if the repository grows far beyond that.
    /// </remarks>
    public static async Task<(Tally Tally, List<string> Platforms)> SupersededAsync(AppDbContext db, BlobContainerClient blobs, int keep, bool dryRun, ILogger log, string? user)
    {
        var rows = await PublicEndpoints.Published(db).Select(p => new { p.Id, p.Platform, p.PortName, p.FileName, p.BlobName }).ToListAsync();
        var doomed = rows.Select(r => (Row: r, Version: VersionOf(r.FileName))).Where(x => x.Version is not null)
            .GroupBy(x => (x.Row.Platform, x.Row.PortName))
            .SelectMany(g =>
            {
                var kept = g.Select(x => x.Version!).Distinct().OrderByDescending(v => v, VersionComparer.Instance).Take(keep).ToHashSet();
                return g.Where(x => !kept.Contains(x.Version!)).Select(x => x.Row);
            }).ToList();
        if (!dryRun)
            foreach (var chunk in doomed.Chunk(Batch))
            {
                var ids = chunk.Select(d => d.Id).ToList();
                await db.Packages.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync();
                foreach (var d in chunk) log.LogInformation("{User} deleted superseded package {Platform}/{FileName}", user, d.Platform, d.FileName);
            }
        var tally = new Tally { Count = doomed.Count };
        foreach (var g in doomed.GroupBy(d => d.BlobName))
            tally.Bytes += await DeleteIfUnreferencedAsync(db, blobs, g.Key, dryRun, g.Count());
        return (tally, doomed.Select(d => d.Platform).Distinct().ToList());
    }

    /// <summary>The version field of a package file name, or null if it is not <c>name-version-revision-arch.hpkg</c>.</summary>
    public static string? VersionOf(string fileName) =>
        fileName.EndsWith(".hpkg", StringComparison.Ordinal) && fileName[..^5].Split('-') is { Length: 4 } f && f[1].Length > 0 ? f[1] : null;

    /// <summary>
    /// Clears <see cref="StagedPackage.BlobName"/> of stale staged rows (<see cref="Stale"/>) and deletes each of their blobs that
    /// nothing else references (an approved build's blob is the published package's, so it stays). Count: staged rows.
    /// </summary>
    public static async Task<Tally> StagedAsync(AppDbContext db, BlobContainerClient blobs, bool dryRun)
    {
        var tally = new Tally();
        var seen = new HashSet<string>();
        long after = 0;
        while (true)
        {
            var batch = await db.StagedPackages.Where(Stale).Where(s => s.Id > after).OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.BlobName }).Take(Batch).ToListAsync();
            if (batch.Count == 0) return tally;
            after = batch[^1].Id;
            var ids = batch.Select(b => b.Id).ToList();
            // Rows first, the predicate re-applied, then the blobs: a blob is never gone while a row still names it as needed.
            tally.Count += dryRun ? batch.Count
                : await db.StagedPackages.Where(Stale).Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.BlobName, ""));
            foreach (var name in batch.Select(b => b.BlobName).Distinct())
                if (seen.Add(name)) tally.Bytes += await DeleteIfUnreferencedAsync(db, blobs, name, dryRun);
        }
    }

    /// <summary>
    /// Deletes blobs older than <paramref name="cutoff"/> that look like ours but nothing references: <c>logs/{id}.log</c> of a job that no
    /// longer exists, and <c>{platform}/{sha256}/{file}</c> named by no package and no staged row. Every other blob (repository index,
    /// priorities, data protection keys, legacy names) is never touched. Count: blobs.
    /// </summary>
    public static async Task<Tally> OrphansAsync(AppDbContext db, BlobContainerClient blobs, DateTimeOffset cutoff, bool dryRun)
    {
        var tally = new Tally();
        await foreach (var page in blobs.GetBlobsAsync().AsPages(pageSizeHint: Batch))
        {
            var old = page.Values.Where(b => b.Properties.LastModified is { } m && m < cutoff).ToList();
            var logs = old.Select(b => (Blob: b, M: LogName().Match(b.Name))).Where(x => x.M.Success)
                .Select(x => (x.Blob, Id: long.Parse(x.M.Groups[1].Value))).ToList();
            var pkgs = old.Where(b => PackageBlobName().Match(b.Name) is { Success: true } m && Validate.IsPlatform(m.Groups[1].Value)).ToList();

            var logIds = logs.Select(x => x.Id).ToList();
            var jobs = logIds.Count == 0 ? [] : (await db.Jobs.Where(j => logIds.Contains(j.Id)).Select(j => j.Id).ToListAsync()).ToHashSet();
            var names = pkgs.Select(b => b.Name).ToList();
            var named = names.Count == 0 ? [] : (await db.Packages.Where(p => names.Contains(p.BlobName)).Select(p => p.BlobName)
                .Concat(db.StagedPackages.Where(s => names.Contains(s.BlobName)).Select(s => s.BlobName)).ToListAsync()).ToHashSet();

            var orphans = logs.Where(x => !jobs.Contains(x.Id)).Select(x => x.Blob).Concat(pkgs.Where(b => !named.Contains(b.Name)));
            foreach (var b in orphans)
            {
                if (!dryRun)
                {
                    // Only the version listed: a blob rewritten since (a log re-uploaded, say) is left alone.
                    try { await blobs.GetBlobClient(b.Name).DeleteAsync(conditions: new BlobRequestConditions { IfMatch = b.Properties.ETag }); }
                    catch (RequestFailedException e) when (e.Status is 404 or 412) { continue; }
                }
                tally.Count++;
                tally.Bytes += b.Properties.ContentLength ?? 0;
            }
        }
        return tally;
    }
}
