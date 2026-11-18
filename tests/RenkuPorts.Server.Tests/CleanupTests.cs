using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>The admin maintenance tools: log cleanup and the file purge (orphans, superseded versions, stale staged uploads).</summary>
public partial class ApiTests
{
    private record TallyView(int Count, long Bytes);
    private record FilesView(TallyView? Orphans, TallyView? Superseded, TallyView? Staged, TallyView Total);

    private static async Task<TallyView> CleanLogsAsync(HttpClient admin, int days, bool dryRun)
    {
        var res = await admin.PostAsJsonAsync("/api/admin/cleanup/logs", new { olderThanDays = days, dryRun });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<TallyView>())!;
    }

    private static async Task<FilesView> PurgeAsync(HttpClient admin, object body)
    {
        var res = await admin.PostAsJsonAsync("/api/admin/cleanup/files", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<FilesView>())!;
    }

    private Task PutBlobAsync(string name, string content) => Blobs.GetBlobClient(name).UploadAsync(BinaryData.FromString(content), overwrite: true);

    private async Task<string> BlobOfAsync(string fileName)
    {
        var blob = "";
        await app.WithDbAsync(async db => blob = await db.Packages.Where(p => p.FileName == fileName).Select(p => p.BlobName).SingleAsync());
        return blob;
    }

    [Fact]
    public async Task Log_cleanup_deletes_only_old_logs_of_finished_jobs()
    {
        var admin = await app.SignedInAsync();
        var cases = new (string Port, string Platform, JobStatus Status, int DaysAgo, bool Deleted)[]
        {
            ("zlib", "arm64", JobStatus.Succeeded, 100, true), ("zlib", "x86", JobStatus.Failed, 100, true),
            ("zlib", "x86_64", JobStatus.Cancelled, 100, true), ("zlib", "riscv64", JobStatus.Succeeded, 10, false),
            ("foo", "arm64", JobStatus.Queued, 100, false), ("foo", "x86", JobStatus.InProgress, 100, false),
            ("foo", "x86_64", JobStatus.Verifying, 100, false), ("foo", "riscv64", JobStatus.AwaitingOverride, 100, false),
        };
        var ids = new List<long>();
        foreach (var c in cases)
        {
            var id = (await QueueAsync(admin, c.Port, c.Platform)).Id;
            ids.Add(id);
            await app.WithDbAsync(db => db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, c.Status).SetProperty(j => j.HasLog, true).SetProperty(j => j.CompletedAt, DateTime.UtcNow.AddDays(-c.DaysAgo))));
            await PutBlobAsync($"logs/{id}.log", $"log of job {id}");
        }
        var doomed = ids.Where((_, i) => cases[i].Deleted).ToList();
        long bytes = doomed.Sum(id => (long)$"log of job {id}".Length);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/cleanup/logs", new { olderThanDays = 0, dryRun = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/cleanup/logs", new { dryRun = true })).StatusCode);

        // A dry run, and a request without dryRun, report and delete nothing.
        Assert.Equal(new TallyView(3, bytes), await CleanLogsAsync(admin, 90, dryRun: true));
        var omitted = await admin.PostAsJsonAsync("/api/admin/cleanup/logs", new { olderThanDays = 90 });
        Assert.Equal(new TallyView(3, bytes), await omitted.Content.ReadFromJsonAsync<TallyView>());
        foreach (var id in ids) Assert.True(await BlobExists($"logs/{id}.log"));

        Assert.Equal(new TallyView(3, bytes), await CleanLogsAsync(admin, 90, dryRun: false));
        await app.WithDbAsync(async db =>
        {
            var hasLog = await db.Jobs.Where(j => ids.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.HasLog);
            foreach (var id in ids) Assert.Equal(!doomed.Contains(id), hasLog[id]);
        });
        foreach (var id in ids) Assert.Equal(!doomed.Contains(id), await BlobExists($"logs/{id}.log"));
        Assert.Equal(new TallyView(0, 0), await CleanLogsAsync(admin, 90, dryRun: false));
    }

    [Fact]
    public async Task Orphan_purge_deletes_only_old_unreferenced_package_and_log_blobs()
    {
        var admin = await app.SignedInAsync();
        var published = Hpkg("zlib");
        await PublishAsync(admin, published);
        var publishedBlob = await BlobOfAsync(published);
        var (_, client) = await app.BuildClientAsync(admin);
        var foo = await QueueAsync(admin, "foo");
        Assert.Equal(foo.Id, (await ClaimAsync(client)).JobId);
        var stagedName = Hpkg("foo");
        var stagedBytes = Guid.NewGuid().ToByteArray();
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, foo.Id, stagedName, stagedBytes)).StatusCode);
        var stagedBlob = $"arm64/{Sha(stagedBytes)}/{stagedName}";

        var tag = Guid.NewGuid().ToString("N");
        string[] orphans = [$"arm64/{Sha([1, 2, 3])}/orphan-{tag}-1-arm64.hpkg", "logs/999999999999.log"];
        string[] kept =
        [
            publishedBlob, stagedBlob, $"logs/{foo.Id}.log", $"arm64/index/cleanup-{tag}", $"priorities/cleanup-{tag}.json", $"dataprotection/cleanup-{tag}.xml",
            $"logs/cleanup-{tag}.log", $"sparc/{Sha([4])}/x-{tag}.hpkg", $"arm64/{Sha([5])}/sub/x-{tag}.hpkg", $"arm64/x-{tag}-1-1-arm64.hpkg",
        ];
        foreach (var n in orphans.Concat(kept.Skip(2))) await PutBlobAsync(n, "x");
        var everything = new { orphans = true, superseded = (object?)null, staged = false, dryRun = false };

        // Younger than the minimum age: nothing of ours is touched.
        await PurgeAsync(admin, everything);
        foreach (var n in orphans.Concat(kept)) Assert.True(await BlobExists(n), n);

        app.Clock.Offset = Cleanup.OrphanMinAge + TimeSpan.FromHours(1);
        var dry = await PurgeAsync(admin, everything with { dryRun = true });
        Assert.True(dry.Orphans!.Count >= orphans.Length); // the shared test container may hold blobs of earlier runs
        Assert.Equal((dry.Orphans.Count, dry.Orphans.Bytes), (dry.Total.Count, dry.Total.Bytes));
        Assert.Null(dry.Superseded);
        foreach (var n in orphans.Concat(kept)) Assert.True(await BlobExists(n), n);

        Assert.True((await PurgeAsync(admin, everything)).Orphans!.Count >= orphans.Length);
        foreach (var n in orphans) Assert.False(await BlobExists(n), n);
        foreach (var n in kept) Assert.True(await BlobExists(n), n);
        Assert.Equal(0, (await PurgeAsync(admin, everything with { dryRun = true })).Orphans!.Count);
        Assert.Equal(16, (await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{published}")).Length);
    }

    [Fact]
    public async Task Superseded_purge_keeps_the_newest_versions_and_blobs_still_referenced()
    {
        var admin = await app.SignedInAsync();
        var (_, first) = await app.BuildClientAsync(admin);
        string oldMain = "zlib-1.3.1-1-arm64.hpkg", oldDevel = "zlib_devel-1.3.1-1-arm64.hpkg", newMain = "zlib-1.3.2-1-arm64.hpkg", newDevel = "zlib_devel-1.3.2-1-arm64.hpkg";
        var files = new[] { oldMain, oldDevel, newMain, newDevel }.Select(n => (n, Guid.NewGuid().ToByteArray())).ToArray();
        await QueueAsync(admin, "zlib");
        await BuildTwiceAsync(admin, first, files);
        var blobs = new Dictionary<string, string>();
        foreach (var (n, _) in files) blobs[n] = await BlobOfAsync(n);

        // A new build of zlib, still running, uploaded the same bytes of 1.3.1: that blob is shared and must stay.
        var (_, again) = await app.BuildClientAsync(admin);
        var rebuild = await QueueAsync(admin, "zlib");
        Assert.Equal(rebuild.Id, (await ClaimAsync(again)).JobId);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(again, rebuild.Id, oldMain, files[0].Item2)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/cleanup/files", new { superseded = new { keep = 0 }, dryRun = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/cleanup/files", new { dryRun = true })).StatusCode);

        var body = new { orphans = false, superseded = new { keep = 1 }, staged = false, dryRun = true };
        var dry = await PurgeAsync(admin, body);
        Assert.Equal(new TallyView(2, 16), dry.Superseded); // two packages; only the devel blob is freed
        Assert.Equal(4, (await PageAsync("pageSize=200")).Total);
        foreach (var b in blobs.Values) Assert.True(await BlobExists(b));

        Assert.Equal(new TallyView(2, 16), (await PurgeAsync(admin, body with { dryRun = false })).Superseded);
        Assert.Equal([newDevel, newMain], (await PageAsync("pageSize=200")).Items.Select(p => p.FileName).Order());
        Assert.True(await BlobExists(blobs[oldMain]));
        Assert.False(await BlobExists(blobs[oldDevel]));
        Assert.True(await BlobExists(blobs[newMain]));
        Assert.True(await BlobExists(blobs[newDevel]));
        await app.WithDbAsync(async db =>
            Assert.Equal([newDevel, newMain], RepoIndexInput.Select(await RepoIndexer.CandidatesAsync(db, "arm64")).Selected.Select(p => p.FileName).Order()));
        Assert.Equal(new TallyView(0, 0), (await PurgeAsync(admin, body with { dryRun = false })).Superseded);
    }

    [Fact]
    public async Task Staged_purge_drops_blobs_of_dead_builds_and_keeps_published_and_active_ones()
    {
        var admin = await app.SignedInAsync();
        var (_, first) = await app.BuildClientAsync(admin);
        var published = Hpkg("zlib");
        var zlib = await QueueAsync(admin, "zlib");
        await BuildTwiceAsync(admin, first, (published, Guid.NewGuid().ToByteArray()));
        var publishedBlob = await BlobOfAsync(published);

        // foo: a build left Discarded with its blob still there; openexr30: a build under way.
        async Task<(long Job, string Blob)> StageAsync(string port)
        {
            var (_, c) = await app.BuildClientAsync(admin);
            var job = await QueueAsync(admin, port);
            Assert.Equal(job.Id, (await ClaimAsync(c)).JobId);
            var (name, bytes) = (Hpkg(port), Guid.NewGuid().ToByteArray());
            Assert.Equal(HttpStatusCode.OK, (await UploadAsync(c, job.Id, name, bytes)).StatusCode);
            return (job.Id, $"arm64/{Sha(bytes)}/{name}");
        }
        var dead = await StageAsync("foo");
        var active = await StageAsync("openexr30");
        await app.WithDbAsync(db => db.JobBuilds.Where(b => b.JobId == dead.Job).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, JobBuildStatus.Discarded)));

        var body = new { orphans = false, superseded = (object?)null, staged = true, dryRun = true };
        // The published job's approved build row (its blob is the package's) and the discarded build's row.
        Assert.Equal(new TallyView(2, 16), (await PurgeAsync(admin, body)).Staged);
        foreach (var b in new[] { publishedBlob, dead.Blob, active.Blob }) Assert.True(await BlobExists(b));

        Assert.Equal(new TallyView(2, 16), (await PurgeAsync(admin, body with { dryRun = false })).Staged);
        Assert.True(await BlobExists(publishedBlob));
        Assert.False(await BlobExists(dead.Blob));
        Assert.True(await BlobExists(active.Blob));
        await app.WithDbAsync(async db =>
        {
            var rows = await db.StagedPackages.Where(s => !s.Extra).Select(s => new { s.Build!.JobId, s.BlobName }).ToListAsync();
            Assert.All(rows.Where(r => r.JobId == zlib.Id || r.JobId == dead.Job), r => Assert.Equal("", r.BlobName));
            Assert.Equal(active.Blob, Assert.Single(rows, r => r.JobId == active.Job).BlobName);
        });
        Assert.Equal(16, (await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{published}")).Length);
        Assert.Equal(new TallyView(0, 0), (await PurgeAsync(admin, body with { dryRun = false })).Staged);
    }

    [Fact]
    public async Task Cleanup_is_admin_only_and_one_at_a_time()
    {
        var builder = await app.BuilderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsJsonAsync("/api/admin/cleanup/logs", new { olderThanDays = 90, dryRun = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsJsonAsync("/api/admin/cleanup/files", new { orphans = true, dryRun = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsJsonAsync("/api/admin/cleanup/logs", new { olderThanDays = 90, dryRun = true })).StatusCode);

        var admin = await app.SignedInAsync();
        Cleanup.Lock.Wait();
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/cleanup/logs", new { olderThanDays = 90, dryRun = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/cleanup/files", new { orphans = true, dryRun = true })).StatusCode);
        }
        finally { Cleanup.Lock.Release(); }
    }

    [Theory]
    [InlineData("zlib-1.3.2-1-arm64.hpkg", "1.3.2")]
    [InlineData("zlib_devel-1.4~rc5-2-x86_64.hpkg", "1.4~rc5")]
    [InlineData("zlib-1.3.2-arm64.hpkg", null)]
    [InlineData("zlib.hpkg", null)]
    public void Version_of_a_package_file_name(string fileName, string? version) => Assert.Equal(version, Cleanup.VersionOf(fileName));
}
