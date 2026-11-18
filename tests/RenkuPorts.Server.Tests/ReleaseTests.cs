using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Tests;

/// <summary>POST /api/client/jobs/{id}/release: a client gives a claimed job back without a result.</summary>
public partial class ApiTests
{
    [Fact]
    public async Task Release_requeues_the_job_expires_the_build_and_does_not_email()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(client);
        byte[] bytes = [7, 7, 7];
        var name = Hpkg("zlib");
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, bytes)).StatusCode);
        var before = app.Mail.Sent.Count;

        var res = await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/release", new { reason = "Docker is not running", log = "docker: not found" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var view = await JobAsync(job.JobId);
        Assert.Equal(("Queued", (string?)null, (DateTime?)null, 1), (view.Status, view.ClaimedBy, view.LeaseExpiresAt, view.Attempts));
        Assert.Equal("Released: Docker is not running", view.FailureSummary);
        Assert.True(view.HasLog);
        Assert.Equal("docker: not found", await admin.GetStringAsync($"/api/jobs/{job.JobId}/log"));
        Assert.False(await BlobExists($"arm64/{Sha(bytes)}/{name}"));
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(JobBuildStatus.Expired, (await db.JobBuilds.SingleAsync(b => b.JobId == job.JobId)).Status);
            Assert.False(await db.StagedPackages.AnyAsync(p => p.Build!.JobId == job.JobId && p.BlobName != ""));
        });
        Assert.Equal(before, app.Mail.Sent.Count);

        // Released twice: the job is no longer InProgress. The same client may claim it again at once and finish it.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/release", new { reason = "again" })).StatusCode);
        Assert.Equal(job.JobId, (await ClaimAsync(client)).JobId);
        Assert.Equal(2, (await JobAsync(job.JobId)).Attempts);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, bytes)).StatusCode);
        var done = await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "" });
        var verifying = (await done.Content.ReadFromJsonAsync<JobView>())!;
        Assert.Equal(("Verifying", (string?)null), (verifying.Status, verifying.FailureSummary));
        Assert.True(verifying.HasLog);
    }

    [Fact]
    public async Task Release_of_another_clients_job_is_forbidden_and_of_a_queued_job_conflicts()
    {
        var admin = await app.SignedInAsync();
        var (_, owner) = await app.BuildClientAsync(admin);
        var (_, other) = await app.BuildClientAsync(admin);
        var queued = await QueueAsync(admin, "zlib");
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync($"/api/client/jobs/{queued.Id}/release", new { reason = "x" })).StatusCode);

        var job = await ClaimAsync(owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/release", new { reason = "x" })).StatusCode);
        Assert.Equal("InProgress", (await JobAsync(job.JobId)).Status);

        // Released without a reason or log: still requeued, and another client can claim it.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/release", new { })).StatusCode);
        Assert.Equal("Released: no reason given", (await JobAsync(job.JobId)).FailureSummary);
        Assert.Equal(job.JobId, (await ClaimAsync(other)).JobId);
    }
}
