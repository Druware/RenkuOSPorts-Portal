using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary><see cref="BuildIssues.Signature"/> and <see cref="BuildIssues.Kind"/>: pure functions.</summary>
public class BuildIssueSignatureTests
{
    [Fact]
    public void The_same_failure_of_different_ports_and_versions_has_one_signature()
    {
        Assert.Equal(BuildIssues.Signature("cannot resolve gcc for port foo-1.2"), BuildIssues.Signature("Cannot resolve gcc for port bar-3.4"));
        Assert.Equal(BuildIssues.Signature("cannot resolve gcc for port foo-1.2", "foo"), BuildIssues.Signature("cannot resolve gcc for port bar-3.4.1~beta", "bar"));
        Assert.Equal(BuildIssues.Signature("foo: error in /boot/home/ports/foo/work-1/src/a.c line 12 (commit 3f2a9c1d)", "foo"),
            BuildIssues.Signature("bar:  error in /tmp/x/bar/b.c line 7  (commit 0ab45ce9e1)", "bar"));
        Assert.Equal("released: build environment failed (exit #)", BuildIssues.Signature("Released: build environment failed (exit 3)"));
        Assert.Equal("cannot resolve gcc for port <pkg>", BuildIssues.Signature("cannot resolve gcc for port zlib-1.3.2", "zlib"));
        Assert.Equal("<port>: configure failed", BuildIssues.Signature("zlib: configure failed", "zlib"));
    }

    [Fact]
    public void Different_failures_have_different_signatures()
    {
        Assert.NotEqual(BuildIssues.Signature("cannot resolve gcc for port foo-1.2"), BuildIssues.Signature("cannot resolve cmake for port foo-1.2"));
        Assert.NotEqual(BuildIssues.Signature("build command exited with code 1"), BuildIssues.Signature("build produced no .hpkg"));
        Assert.NotEqual(BuildIssues.Signature("Released: build environment failed (exit 3)"), BuildIssues.Signature("Released: Docker is not running"));
        Assert.True(BuildIssues.Signature(new string('x', 5000)).Length <= BuildIssues.MaxSignature);
    }

    [Fact]
    public void Kind_is_guessed_from_the_summary()
    {
        Assert.Equal(BuildIssueKind.Environment, BuildIssues.Kind("Docker is not running", released: true));
        Assert.Equal(BuildIssueKind.Environment, BuildIssues.Kind("build environment failed (exit 3)"));
        Assert.Equal(BuildIssueKind.Dependency, BuildIssues.Kind("cannot resolve gcc for port foo-1.2"));
        Assert.Equal(BuildIssueKind.Dependency, BuildIssues.Kind("zlib requires lib:libfoo"));
        Assert.Equal(BuildIssueKind.Recipe, BuildIssues.Kind("build command exited with code 1"));
    }
}

/// <summary>Build issues: failures are filed and counted, block their port from being queued again, and admins resolve or escalate them.</summary>
public partial class ApiTests
{
    private record AffectedPortView(string PortName, long LatestJobId);
    private record IssueView(long Id, string Platform, string Kind, string Status, string Title, string Signature, int Count, string? Notes,
        long? ExampleJobId, string? ResolvedBy, List<AffectedPortView> Ports);
    private record ResolvedView(IssueView Issue, List<long> Requeued);

    private static async Task<long> FailAsync(HttpClient client, string summary)
    {
        var job = await ClaimAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/fail", new { summary, log = "log" })).StatusCode);
        return job.JobId;
    }

    private async Task<long?> IssueOfAsync(long jobId)
    {
        long? id = null;
        await app.WithDbAsync(async db => id = await db.Jobs.Where(j => j.Id == jobId).Select(j => j.IssueId).SingleAsync());
        return id;
    }

    [Fact]
    public async Task Fail_files_the_same_failure_under_one_issue_and_emails_once()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        var before = app.Mail.Sent.Count;

        await QueueAsync(admin, "zlib");
        var zlib = await FailAsync(client, "cannot resolve gcc for port zlib-1.3.2");
        Assert.Equal(before + 1, app.Mail.Sent.Count);
        var issue = Assert.Single((await admin.GetFromJsonAsync<List<IssueView>>("/api/issues"))!);
        Assert.Equal(("arm64", "Dependency", "Open", 1, zlib), (issue.Platform, issue.Kind, issue.Status, issue.Count, issue.ExampleJobId));
        Assert.Contains($"#{issue.Id}", app.Mail.Sent.Last().Subject);
        Assert.Contains("cannot resolve gcc", app.Mail.Sent.Last().Body);

        // The same cause on another port and version: counted under the issue, no second email.
        await QueueAsync(admin, "foo");
        var foo = await FailAsync(client, "cannot resolve gcc for port foo-1.10");
        Assert.Equal(before + 1, app.Mail.Sent.Count);
        Assert.Equal(issue.Id, await IssueOfAsync(foo));
        var merged = (await admin.GetFromJsonAsync<IssueView>($"/api/issues/{issue.Id}"))!;
        Assert.Equal(2, merged.Count);
        Assert.Equal([new AffectedPortView("foo", foo), new AffectedPortView("zlib", zlib)], merged.Ports);

        // Another cause: a new issue and a new email.
        await QueueAsync(admin, "openexr30");
        var exr = await FailAsync(client, "build command exited with code 2");
        Assert.Equal(before + 2, app.Mail.Sent.Count);
        Assert.NotEqual(issue.Id, await IssueOfAsync(exr));
        Assert.Equal(2, (await admin.GetFromJsonAsync<List<IssueView>>("/api/issues"))!.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/issues/999999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/issues?status=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.BuilderAsync()).GetAsync("/api/issues")).StatusCode);
    }

    [Fact]
    public async Task Release_past_the_cap_fails_the_job_and_opens_an_environment_issue()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        var queued = await QueueAsync(admin, "zlib");
        var before = app.Mail.Sent.Count;
        for (var i = 1; i < BuildClientApi.MaxReleases; i++)
        {
            Assert.Equal(queued.Id, (await ClaimAsync(client)).JobId);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/client/jobs/{queued.Id}/release", new { reason = "build environment failed (exit 3)" })).StatusCode);
            Assert.Equal("Queued", (await JobAsync(queued.Id)).Status);
        }
        Assert.Equal(before, app.Mail.Sent.Count);

        var job = await ClaimAsync(client);
        byte[] bytes = [5, 5];
        var name = Hpkg("zlib");
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/release", new { reason = "build environment failed (exit 3)", log = "no gcc" })).StatusCode);

        var view = await JobAsync(job.JobId);
        Assert.Equal(("Failed", "Released: build environment failed (exit 3)", BuildClientApi.MaxReleases, true), (view.Status, view.FailureSummary, view.Attempts, view.HasLog));
        Assert.False(await BlobExists($"arm64/{Sha(bytes)}/{name}"));
        var issue = Assert.Single((await admin.GetFromJsonAsync<List<IssueView>>("/api/issues"))!);
        Assert.Equal(("Environment", "Open", 1), (issue.Kind, issue.Status, issue.Count));
        Assert.Equal(before + 1, app.Mail.Sent.Count);
        await app.WithDbAsync(async db => Assert.Equal(JobBuildStatus.Failed, (await db.JobBuilds.OrderBy(b => b.Number).LastAsync(b => b.JobId == job.JobId)).Status));

        // Requeued, the job gets MaxReleases releases again: the releases before the requeue no longer count.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/jobs/{job.JobId}/requeue", null)).StatusCode);
        Assert.Null(await IssueOfAsync(job.JobId));
        Assert.Equal(job.JobId, (await ClaimAsync(client)).JobId);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/release", new { reason = "x" })).StatusCode);
        Assert.Equal("Queued", (await JobAsync(job.JobId)).Status);
    }

    [Fact]
    public async Task Blocked_ports_are_skipped_by_queue_top_and_refused_by_queue_without_force()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var failed = await FailAsync(client, "build command exited with code 1");
        var issue = (await IssueOfAsync(failed))!.Value;

        await SetPriorityAsync("zlib", 9);
        await SetPriorityAsync("foo", 5);
        var top = (await (await admin.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 1 })).Content.ReadFromJsonAsync<List<JobView>>())!;
        Assert.Equal(["foo"], top.Select(j => j.PortName));

        var refused = await admin.PostAsJsonAsync("/api/jobs", new { portName = "zlib", platforms = new[] { "arm64", "x86" } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains($"#{issue} (arm64)", await refused.Content.ReadAsStringAsync());
        Assert.DoesNotContain((await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs?status=Queued"))!.Items, j => j.PortName == "zlib");
        // Other platforms are not blocked.
        Assert.Equal("x86", (await QueueAsync(admin, "zlib", "x86")).Platform);

        var forced = await admin.PostAsJsonAsync("/api/jobs", new { portName = "zlib", platforms = new[] { "arm64" }, force = true });
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        Assert.Equal("arm64", Assert.Single((await forced.Content.ReadFromJsonAsync<List<JobView>>())!).Platform);
    }

    [Fact]
    public async Task Resolve_with_requeue_requeues_the_failed_jobs_and_unblocks_the_port()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var zlib = await FailAsync(client, "cannot resolve gcc for port zlib-1.3.2");
        await QueueAsync(admin, "foo");
        var foo = await FailAsync(client, "cannot resolve gcc for port foo-1.10");
        var issue = (await IssueOfAsync(zlib))!.Value;

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/issues/{issue}/resolve", new { note = " ", requeue = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/issues/{issue}/escalate", new { note = new string('n', 1001) })).StatusCode);

        // Escalated once: one email; again: 409.
        var before = app.Mail.Sent.Count;
        var escalated = await admin.PostAsJsonAsync($"/api/issues/{issue}/escalate", new { note = "gcc missing on the arm64 image" });
        Assert.Equal(HttpStatusCode.OK, escalated.StatusCode);
        Assert.Equal("Escalated", (await escalated.Content.ReadFromJsonAsync<IssueView>())!.Status);
        Assert.Equal(before + 1, app.Mail.Sent.Count);
        Assert.Contains("foo, zlib", app.Mail.Sent.Last().Body);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/issues/{issue}/escalate", new { note = "again" })).StatusCode);
        Assert.Equal(before + 1, app.Mail.Sent.Count);

        var res = await admin.PostAsJsonAsync($"/api/issues/{issue}/resolve", new { note = "gcc added to the image", requeue = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var resolved = (await res.Content.ReadFromJsonAsync<ResolvedView>())!;
        Assert.Equal(("Resolved", AppFactory.AdminEmail), (resolved.Issue.Status, resolved.Issue.ResolvedBy));
        Assert.Equal([zlib, foo], resolved.Requeued.Order());
        Assert.Contains("(escalated): gcc missing", resolved.Issue.Notes);
        Assert.Contains("(resolved): gcc added", resolved.Issue.Notes);
        foreach (var id in new[] { zlib, foo })
        {
            Assert.Equal(("Queued", (string?)null), ((await JobAsync(id)).Status, (await JobAsync(id)).FailureSummary));
            Assert.Null(await IssueOfAsync(id));
        }
        Assert.Empty((await admin.GetFromJsonAsync<List<IssueView>>("/api/issues"))!);
        Assert.Single((await admin.GetFromJsonAsync<List<IssueView>>("/api/issues?status=resolved"))!);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/issues/{issue}/resolve", new { note = "again", requeue = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/issues/{issue}/escalate", new { note = "late" })).StatusCode);

        // Unblocked: queueing is no longer refused (zlib is queued already, so nothing new), and the same cause again opens a new issue.
        var queue = await admin.PostAsJsonAsync("/api/jobs", new { portName = "zlib", platforms = new[] { "arm64" } });
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        Assert.Empty((await queue.Content.ReadFromJsonAsync<List<JobView>>())!);
        var again = await FailAsync(client, "cannot resolve gcc for port zlib-1.3.2");
        Assert.NotEqual(issue, await IssueOfAsync(again));
    }

    [Fact]
    public async Task A_failed_job_approved_anyway_no_longer_blocks_its_port()
    {
        var admin = await app.SignedInAsync();
        var clients = new List<HttpClient>();
        for (var i = 0; i < 3; i++) clients.Add((await app.BuildClientAsync(admin)).Http);
        var job = await QueueAsync(admin, "zlib");
        var name = Hpkg("zlib");
        await BuildOnceAsync(clients[0], name, [1]);
        await BuildOnceAsync(clients[1], name, [2, 2]);
        var failed = await BuildOnceAsync(clients[2], name, [3, 3, 3]);
        Assert.Equal("Failed", failed.Status);
        Assert.NotNull(await IssueOfAsync(job.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/jobs", new { portName = "zlib", platforms = new[] { "arm64" } })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/jobs/{job.Id}/approve", new { buildId = failed.Builds[1].Id, reason = "build 2 is fine" })).StatusCode);
        Assert.Equal("Succeeded", (await JobAsync(job.Id)).Status);
        Assert.NotNull(await IssueOfAsync(job.Id)); // still filed under the open issue, but no longer blocking
        Assert.Equal("arm64", (await QueueAsync(admin, "zlib")).Platform);
    }
}
