using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>
/// Build verification over HTTP: two builds by different clients must match, the independence window, the third build,
/// timestamp-only mismatches and the admin override, extras. Without the Haiku tools (the host) the fake packages are compared
/// byte for byte, which is what the raw fallback does for files the package tool cannot read.
/// </summary>
public partial class ApiTests
{
    private record BuildFile(string FileName, bool Extra);
    private record BuildRow(long Id, int Number, string ClientName, string Status, string? Digest, List<BuildFile> Packages, List<JsonElement>? Diffs);
    private record VerifiedJob(long Id, string Status, string? Approval, string? ApprovedBy, string? ApprovalReason, long? ApprovedBuildId,
        bool SelfVerified, List<string> Packages, List<BuildRow> Builds, string? FailureSummary);
    private record VerifiedPackage(string FileName, string? Verification, int VerifiedClients);

    private async Task<VerifiedJob> VerifiedJobAsync(long id) =>
        (await (await app.SignedInAsync()).GetFromJsonAsync<PageView<VerifiedJob>>("/api/jobs?pageSize=200"))!.Items.Single(j => j.Id == id);

    /// <summary>Claims the next job with <paramref name="client"/>, uploads one file and completes; the clock moves <paramref name="took"/> during the build.</summary>
    private async Task<VerifiedJob> BuildOnceAsync(HttpClient client, string name, byte[] bytes, TimeSpan? took = null, long? expect = null)
    {
        var job = await ClaimAsync(client);
        if (expect is not null) Assert.Equal(expect, job.JobId);
        app.Clock.Offset += took ?? TimeSpan.Zero;
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "" })).StatusCode);
        await app.VerifyAsync();
        return await VerifiedJobAsync(job.JobId);
    }

    private async Task<HttpStatusCode> TryClaimAsync(HttpClient client) => (await client.PostAsync("/api/client/jobs/next", null)).StatusCode;

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public async Task Second_build_waits_out_the_window_for_the_same_client_and_is_then_self_verified()
    {
        var admin = await app.SignedInAsync();
        var (_, c1) = await app.BuildClientAsync(admin);
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");

        var first = await BuildOnceAsync(c1, name, [1, 2, 3], took: TimeSpan.FromHours(1));
        Assert.Equal("Queued", first.Status);
        // The build took an hour: c1 may not take the second build for an hour after completing it.
        Assert.Equal(HttpStatusCode.NoContent, await TryClaimAsync(c1));
        app.Clock.Offset += TimeSpan.FromMinutes(59);
        Assert.Equal(HttpStatusCode.NoContent, await TryClaimAsync(c1));
        app.Clock.Offset += TimeSpan.FromMinutes(2);

        var done = await BuildOnceAsync(c1, name, [1, 2, 3], expect: job.Id);
        Assert.Equal(("Succeeded", "Match", true), (done.Status, done.Approval, done.SelfVerified));
        var pkg = Assert.Single((await app.CreateClient().GetFromJsonAsync<PageView<VerifiedPackage>>("/api/repo/arm64/packages"))!.Items);
        Assert.Equal(("self-verified", 1), (pkg.Verification, pkg.VerifiedClients));
        var seen = (await c1.GetFromJsonAsync<JsonElement>("/api/client/jobs?status=Succeeded")).GetProperty("items")[0];
        Assert.Equal((job.Id, true), (seen.GetProperty("id").GetInt64(), seen.GetProperty("selfVerified").GetBoolean()));
    }

    [Fact]
    public async Task Another_client_takes_the_second_build_at_once_and_the_job_is_verified_by_two_clients()
    {
        var admin = await app.SignedInAsync();
        var (_, c1) = await app.BuildClientAsync(admin);
        var (_, c2) = await app.BuildClientAsync(admin);
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");

        Assert.Equal("Queued", (await BuildOnceAsync(c1, name, [7, 7], took: TimeSpan.FromHours(3))).Status);
        Assert.Equal(HttpStatusCode.NoContent, await TryClaimAsync(c1));
        var done = await BuildOnceAsync(c2, name, [7, 7], expect: job.Id);
        Assert.Equal(("Succeeded", "Match", false), (done.Status, done.Approval, done.SelfVerified));
        Assert.Equal([name], done.Packages);
        Assert.Equal(done.Builds[0].Id, done.ApprovedBuildId);
        Assert.Equal(done.Builds[0].Digest, done.Builds[1].Digest);
        var pkg = Assert.Single((await app.CreateClient().GetFromJsonAsync<PageView<VerifiedPackage>>("/api/repo/arm64/packages"))!.Items);
        Assert.Equal(("verified", 2), (pkg.Verification, pkg.VerifiedClients));
        Assert.Equal([7, 7], await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{name}"));
    }

    [Fact]
    public async Task A_real_mismatch_gets_a_third_build_and_two_of_three_publish_with_the_loser_cleaned_up()
    {
        var admin = await app.SignedInAsync();
        var clients = new List<HttpClient>();
        for (var i = 0; i < 3; i++) clients.Add((await app.BuildClientAsync(admin)).Http);
        await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        byte[] good = Bytes("good build"), bad = Bytes("bad build, longer");

        Assert.Equal("Queued", (await BuildOnceAsync(clients[0], name, good)).Status);
        var second = await BuildOnceAsync(clients[1], name, bad);
        Assert.Equal("Queued", second.Status); // not timestamp-like (sizes differ): no override, a third build
        var diff = Assert.Single(second.Builds[1].Diffs!);
        Assert.False(diff.GetProperty("timestampLike").GetBoolean());
        Assert.Matches("^compared as raw bytes: .+; size differs$", diff.GetProperty("files")[0].GetProperty("note").GetString()); // mock packages: no tools, or the tools reject them

        var done = await BuildOnceAsync(clients[2], name, good);
        Assert.Equal(("Succeeded", false), (done.Status, done.SelfVerified));
        Assert.Equal(done.Builds[0].Id, done.ApprovedBuildId);
        Assert.Equal(good, await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{name}"));
        Assert.False(await BlobExists($"arm64/{Sha(bad)}/{name}"));
        Assert.True(await BlobExists($"arm64/{Sha(good)}/{name}"));
    }

    [Fact]
    public async Task Two_of_three_from_one_client_are_self_verified()
    {
        var admin = await app.SignedInAsync();
        var (_, c1) = await app.BuildClientAsync(admin);
        var (_, c2) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        await BuildOnceAsync(c1, name, Bytes("same"), took: TimeSpan.FromMinutes(10));
        Assert.Equal("Queued", (await BuildOnceAsync(c2, name, Bytes("other bytes"))).Status);
        Assert.Equal(HttpStatusCode.NoContent, await TryClaimAsync(c1)); // still inside c1's window (10 min after its build)
        app.Clock.Offset += TimeSpan.FromMinutes(11);
        var done = await BuildOnceAsync(c1, name, Bytes("same"));
        Assert.Equal(("Succeeded", true), (done.Status, done.SelfVerified));
    }

    [Fact]
    public async Task Three_different_builds_fail_the_job_and_email_admins()
    {
        var admin = await app.SignedInAsync();
        var clients = new List<HttpClient>();
        for (var i = 0; i < 3; i++) clients.Add((await app.BuildClientAsync(admin)).Http);
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        var before = app.Mail.Sent.Count;
        await BuildOnceAsync(clients[0], name, Bytes("a"));
        await BuildOnceAsync(clients[1], name, Bytes("bb"));
        var done = await BuildOnceAsync(clients[2], name, Bytes("ccc"));
        Assert.Equal(("Failed", "Builds 1, 2, 3 all differ."), (done.Status, done.FailureSummary));
        Assert.Empty(done.Packages);
        var mail = app.Mail.Sent.Skip(before).Single();
        Assert.Contains("failed verification", mail.Subject);
        // The staged builds are kept, so an admin can still accept one.
        foreach (var b in new[] { "a", "bb", "ccc" }) Assert.True(await BlobExists($"arm64/{Sha(Bytes(b))}/{name}"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/repo/arm64/packages/{name}")).StatusCode);

        // Requeued, it starts over: the three builds no longer count and their blobs are deleted.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/jobs/{job.Id}/requeue", null)).StatusCode);
        Assert.All((await VerifiedJobAsync(job.Id)).Builds, b => Assert.Equal("Discarded", b.Status));
        foreach (var b in new[] { "a", "bb", "ccc" }) Assert.False(await BlobExists($"arm64/{Sha(Bytes(b))}/{name}"));
    }

    [Fact]
    public async Task An_admin_accepts_a_build_of_a_job_that_failed_verification_and_the_others_are_deleted()
    {
        var admin = await app.SignedInAsync();
        var clients = new List<HttpClient>();
        for (var i = 0; i < 3; i++) clients.Add((await app.BuildClientAsync(admin)).Http);
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        await BuildOnceAsync(clients[0], name, Bytes("a"));
        await BuildOnceAsync(clients[1], name, Bytes("bb"));
        var failed = await BuildOnceAsync(clients[2], name, Bytes("ccc"));
        Assert.Equal("Failed", failed.Status);

        var ok = await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = failed.Builds[1].Id, reason = "build 2 is the release toolchain" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var done = (await ok.Content.ReadFromJsonAsync<VerifiedJob>())!;
        Assert.Equal(("Succeeded", "Override", AppFactory.AdminEmail, failed.Builds[1].Id, false, null),
            (done.Status, done.Approval, done.ApprovedBy, done.ApprovedBuildId, done.SelfVerified, done.FailureSummary));
        Assert.Equal([name], done.Packages);
        Assert.Equal(Bytes("bb"), await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{name}"));
        Assert.False(await BlobExists($"arm64/{Sha(Bytes("a"))}/{name}"));
        Assert.False(await BlobExists($"arm64/{Sha(Bytes("ccc"))}/{name}"));
        Assert.Equal("override", Assert.Single((await app.CreateClient().GetFromJsonAsync<PageView<VerifiedPackage>>("/api/repo/arm64/packages"))!.Items).Verification);
        // Published: a second acceptance is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = failed.Builds[0].Id, reason = "again" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_accepts_the_only_build_and_the_open_second_build_is_discarded()
    {
        var admin = await app.SignedInAsync();
        var (_, c1) = await app.BuildClientAsync(admin);
        var (_, c2) = await app.BuildClientAsync(admin);
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        var first = await BuildOnceAsync(c1, name, Bytes("first"));
        Assert.Equal("Queued", first.Status);
        // The second build is under way and has uploaded something when the admin accepts the first.
        Assert.Equal(job.Id, (await ClaimAsync(c2)).JobId);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(c2, job.Id, name, Bytes("second"))).StatusCode);

        var ok = await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = first.Builds[0].Id, reason = "urgent fix, single build" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var done = (await ok.Content.ReadFromJsonAsync<VerifiedJob>())!;
        Assert.Equal(("Succeeded", "Override", first.Builds[0].Id), (done.Status, done.Approval, done.ApprovedBuildId));
        Assert.Equal(["Completed", "Discarded"], done.Builds.Select(b => b.Status));
        Assert.False(await BlobExists($"arm64/{Sha(Bytes("second"))}/{name}"));

        // The second build can no longer upload or complete, and nothing more is claimable.
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(c2, job.Id, name, Bytes("second"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c2.PostAsJsonAsync($"/api/client/jobs/{job.Id}/complete", new { log = "" })).StatusCode);
        await app.VerifyAsync();
        Assert.Equal(HttpStatusCode.NoContent, await TryClaimAsync((await app.BuildClientAsync(admin)).Http));
        Assert.Equal(Bytes("first"), await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{name}"));
        Assert.Equal("Succeeded", (await VerifiedJobAsync(job.Id)).Status);
    }

    [Fact]
    public async Task Accepting_a_build_is_refused_without_a_reason_for_another_jobs_build_or_once_its_packages_are_gone()
    {
        var admin = await app.SignedInAsync();
        var (_, c1) = await app.BuildClientAsync(admin);
        var (_, c2) = await app.BuildClientAsync(admin);
        var zlib = await QueueAsync(admin, "zlib");
        var waiting = await BuildOnceAsync(c1, Hpkg("zlib"), Bytes("z"), took: TimeSpan.FromHours(1)); // c1 cannot take zlib's second build
        var foo = await QueueAsync(admin, "foo");
        var fooBuilt = await BuildOnceAsync(c1, Hpkg("foo"), Bytes("f"), expect: foo.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/jobs/{zlib.Id}/approve", new { buildId = waiting.Builds[0].Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/jobs/{zlib.Id}/approve", new { buildId = fooBuilt.Builds[0].Id, reason = "x" })).StatusCode);

        // A failed second build (either job) fails the job and deletes its staged packages: the first build can no longer be published.
        var claimed = await ClaimAsync(c2);
        Assert.Equal(HttpStatusCode.OK, (await c2.PostAsJsonAsync($"/api/client/jobs/{claimed.JobId}/fail", new { summary = "boom" })).StatusCode);
        var gone = await VerifiedJobAsync(claimed.JobId);
        Assert.Equal("Failed", gone.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/jobs/{gone.Id}/approve", new { buildId = gone.Builds[0].Id, reason = "x" })).StatusCode);
        Assert.Equal("Failed", (await VerifiedJobAsync(gone.Id)).Status);
    }

    [Fact]
    public async Task Timestamp_only_mismatch_waits_for_an_admin_who_approves_a_build_and_can_flag_the_port()
    {
        var admin = await app.SignedInAsync();
        var (_, c1) = await app.BuildClientAsync(admin);
        var (_, c2) = await app.BuildClientAsync(admin);
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        byte[] a = Bytes("compiled Nov 17 2026 21:13:05 by gcc"), b = Bytes("compiled Nov 17 2026 21:14:22 by gcc");
        var before = app.Mail.Sent.Count;

        await BuildOnceAsync(c1, name, a);
        var waiting = await BuildOnceAsync(c2, name, b);
        Assert.Equal("AwaitingOverride", waiting.Status);
        Assert.Contains("override", app.Mail.Sent.Skip(before).Single().Subject);
        var diff = Assert.Single(waiting.Builds[1].Diffs!);
        Assert.True(diff.GetProperty("timestampLike").GetBoolean());
        var run = diff.GetProperty("files")[0].GetProperty("runs")[0];
        Assert.Contains("2026 21:13:05 by gcc", run.GetProperty("a").GetString());
        Assert.Contains("2026 21:14:22 by gcc", run.GetProperty("b").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/repo/arm64/packages/{name}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, await TryClaimAsync((await app.BuildClientAsync(admin)).Http)); // no third build

        var buildB = waiting.Builds[1].Id;
        var builder = await app.BuilderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = buildB, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = buildB, reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = -1, reason = "x" })).StatusCode);
        var ok = await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = buildB, reason = "__DATE__ in the banner", embedsDates = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var approved = (await ok.Content.ReadFromJsonAsync<VerifiedJob>())!;
        Assert.Equal(("Succeeded", "Override", AppFactory.AdminEmail, "__DATE__ in the banner", buildB, false),
            (approved.Status, approved.Approval, approved.ApprovedBy, approved.ApprovalReason, approved.ApprovedBuildId, approved.SelfVerified));
        Assert.Equal(b, await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{name}"));
        Assert.False(await BlobExists($"arm64/{Sha(a)}/{name}"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = buildB, reason = "again" })).StatusCode);
        var pkg = Assert.Single((await app.CreateClient().GetFromJsonAsync<PageView<VerifiedPackage>>("/api/repo/arm64/packages"))!.Items);
        Assert.Equal("override", pkg.Verification);

        // Flagged: the next timestamp-only mismatch of zlib is approved without an admin.
        var recipe = (await admin.GetFromJsonAsync<PageView<JsonElement>>("/api/recipes?search=zlib"))!.Items[0];
        Assert.True(recipe.GetProperty("embedsDates").GetBoolean());
        var next = await QueueAsync(admin, "zlib");
        var name2 = Hpkg("zlib");
        await BuildOnceAsync(c1, name2, Bytes("at 2026-11-17T21:13:05Z"));
        var auto = await BuildOnceAsync(c2, name2, Bytes("at 2026-11-18T08:00:59Z"));
        Assert.Equal((next.Id, "Succeeded", "EmbedsDates", false), (auto.Id, auto.Status, auto.Approval, auto.SelfVerified));

        // Cleared: the next one waits for an admin again.
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.DeleteAsync("/api/ports/zlib/embeds-dates")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/ports/zlib/embeds-dates")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/ports/zlib/embeds-dates")).StatusCode);
        var third = await QueueAsync(admin, "zlib");
        var name3 = Hpkg("zlib");
        await BuildOnceAsync(c1, name3, Bytes("t=1763413985"));
        Assert.Equal("AwaitingOverride", (await BuildOnceAsync(c2, name3, Bytes("t=1763414000"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/jobs/{third.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Packages_of_other_ports_are_recorded_as_extras_and_not_published()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        // A port whose name extends zlib_: its packages are not zlib's (longest port name wins).
        await app.WithDbAsync(async db => { db.Recipes.Add(new Recipe { PortName = "zlib_ng", Category = "x", Version = "1", RecipePath = "x/zlib_ng/zlib_ng-1.recipe" }); await db.SaveChangesAsync(); });
        try
        {
            string own = Hpkg("zlib"), devel = Hpkg("zlib_devel"), dep = Hpkg("foo"), ng = Hpkg("zlib_ng");
            var done = await BuildTwiceAsync(admin, client, (own, [1]), (devel, [2]), (dep, [3]), (ng, [4]));
            Assert.Equal(new[] { devel, own }.Order(), done.Packages.Order());
            var build = (await VerifiedJobAsync(done.Id)).Builds[0];
            Assert.Equal(new[] { (dep, true), (devel, false), (own, false), (ng, true) }.OrderBy(x => x.Item1, StringComparer.Ordinal),
                build.Packages.Select(p => (p.FileName, p.Extra)).OrderBy(x => x.FileName, StringComparer.Ordinal));
            Assert.False(await BlobExists($"arm64/{Sha([3])}/{dep}"));
            Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/repo/arm64/packages/{dep}")).StatusCode);

            // Only extras: the build has nothing of its port to complete with.
            await QueueAsync(admin, "openexr30");
            var job = await ClaimAsync(client);
            Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, Hpkg("zlib"), [9])).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "" })).StatusCode);
        }
        finally
        {
            await app.WithDbAsync(db => db.Recipes.Where(r => r.PortName == "zlib_ng").ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task Concurrent_claims_of_the_second_build_have_exactly_one_winner_and_never_the_first_builder()
    {
        var admin = await app.SignedInAsync();
        var (_, first) = await app.BuildClientAsync(admin);
        var others = new List<HttpClient>();
        for (var i = 0; i < 4; i++) others.Add((await app.BuildClientAsync(admin)).Http);
        for (var round = 0; round < 3; round++)
        {
            await app.ResetQueueAsync();
            var job = await QueueAsync(admin, "zlib");
            await BuildOnceAsync(first, Hpkg("zlib"), [1], took: TimeSpan.FromHours(1));
            var racers = others.Prepend(first).ToList();
            var results = await Task.WhenAll(racers.Select(c => c.PostAsync("/api/client/jobs/next", null)));
            Assert.Equal(HttpStatusCode.NoContent, results[0].StatusCode);
            var won = Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(job.Id, (await won.Content.ReadFromJsonAsync<ClaimedJob>())!.JobId);
            Assert.Equal(racers.Count - 1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            await app.WithDbAsync(async db => Assert.Equal(2, await db.JobBuilds.CountAsync(b => b.JobId == job.Id)));
        }
    }

    [Fact]
    public async Task Redigest_migration_rejudges_undecided_jobs_and_leaves_decided_ones_alone()
    {
        var admin = await app.SignedInAsync();
        var (_, c1) = await app.BuildClientAsync(admin);
        var (_, c2) = await app.BuildClientAsync(admin);
        async Task Stale(long jobId) => await app.WithDbAsync(async db =>
        {
            await db.StagedPackages.Where(p => p.Build!.JobId == jobId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Digest, p => "old" + p.Id));
            await db.JobBuilds.Where(b => b.JobId == jobId).ExecuteUpdateAsync(s => s.SetProperty(b => b.Digest, b => "old" + b.Number));
        });
        async Task<List<string?>> Digests(long jobId)
        {
            List<string?> d = [];
            await app.WithDbAsync(async db => d = [.. await db.JobBuilds.Where(b => b.JobId == jobId).OrderBy(b => b.Number).Select(b => b.Digest).ToListAsync(),
                .. await db.StagedPackages.Where(p => p.Build!.JobId == jobId).OrderBy(p => p.Id).Select(p => p.Digest).ToListAsync()]);
            return d;
        }

        // Decided: a Failed job (blobs kept for an override) and a Succeeded one, both with old-rule digests.
        var failed = await QueueAsync(admin, "foo");
        var foo = Hpkg("foo");
        await BuildOnceAsync(c1, foo, Bytes("foo a"));
        Assert.Equal("Queued", (await BuildOnceAsync(c2, foo, Bytes("foo b, longer"))).Status);
        await app.WithDbAsync(db => db.Jobs.Where(j => j.Id == failed.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed)));
        await Stale(failed.Id);
        var succeeded = await QueueAsync(admin, "openexr30");
        var exr = Hpkg("openexr30");
        await BuildOnceAsync(c1, exr, [5]);
        Assert.Equal("Succeeded", (await BuildOnceAsync(c2, exr, [5])).Status);
        await Stale(succeeded.Id);

        // Undecided: same bytes from two clients, but build 2's stored digests are from the old rule (the packager differed), so it
        // was queued for a third build.
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        Assert.Equal("Queued", (await BuildOnceAsync(c1, name, [7, 7])).Status);
        Assert.Equal(job.Id, (await ClaimAsync(c2)).JobId);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(c2, job.Id, name, [7, 7])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c2.PostAsJsonAsync($"/api/client/jobs/{job.Id}/complete", new { log = "" })).StatusCode);
        await app.WithDbAsync(async db =>
        {
            await db.StagedPackages.Where(p => p.Build!.JobId == job.Id && p.Build.Number == 2).ExecuteUpdateAsync(s => s.SetProperty(p => p.Digest, "old"));
            await db.JobBuilds.Where(b => b.JobId == job.Id && b.Number == 2).ExecuteUpdateAsync(s => s.SetProperty(b => b.Digest, "old").SetProperty(b => b.DiffJson, "[]"));
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued));
        });
        var (failedBefore, succeededBefore) = (await Digests(failed.Id), await Digests(succeeded.Id));

        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(RenkuPorts.Server.Data.Migrations.RedigestWithoutPackager.Sql));
        Assert.Equal("Verifying", (await VerifiedJobAsync(job.Id)).Status);
        await app.VerifyAsync();

        var done = await VerifiedJobAsync(job.Id);
        Assert.Equal(("Succeeded", "Match", done.Builds[0].Id), (done.Status, done.Approval, done.ApprovedBuildId));
        Assert.Equal(done.Builds[0].Digest, done.Builds[1].Digest);
        Assert.Equal("Failed", (await VerifiedJobAsync(failed.Id)).Status);
        Assert.Equal(failedBefore, await Digests(failed.Id));
        Assert.Equal("Succeeded", (await VerifiedJobAsync(succeeded.Id)).Status);
        Assert.Equal(succeededBefore, await Digests(succeeded.Id));
    }
}
