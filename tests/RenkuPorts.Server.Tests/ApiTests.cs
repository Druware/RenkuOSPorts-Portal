using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>Integration tests over HTTP. They share one database, so each starts from an empty job queue.</summary>
public partial class ApiTests(AppFactory app) : IClassFixture<AppFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => app.ResetQueueAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private record ClaimedJob(long JobId, string PortName, string Version, string Category, string RecipePath, string Platform);
    private record JobView(long Id, string PortName, string Version, string Platform, string Status, string? ClaimedBy, DateTime? LeaseExpiresAt,
        int Attempts, string? FailureSummary, bool HasLog, List<string> Packages);
    private record PackageView(string FileName, long Size, string Sha256, string PortName, string BuiltBy);

    private async Task<JobView> QueueAsync(HttpClient admin, string port, string platform = "arm64")
    {
        var res = await admin.PostAsJsonAsync("/api/jobs", new { portName = port, platforms = new[] { platform } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return Assert.Single((await res.Content.ReadFromJsonAsync<List<JobView>>())!);
    }

    private static async Task<ClaimedJob> ClaimAsync(HttpClient client)
    {
        var res = await client.PostAsync("/api/client/jobs/next", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ClaimedJob>())!;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>Uploads like the client: sha256 and buildDate fields, then the file part. <paramref name="sha"/> overrides the real hash.</summary>
    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, long jobId, string fileName, byte[] bytes, string? sha = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(sha ?? Sha(bytes)), "sha256" },
            { new StringContent("2026-11-01T10:00:00Z"), "buildDate" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return client.PostAsync($"/api/client/jobs/{jobId}/packages", form);
    }

    private BlobContainerClient Blobs => app.Services.GetRequiredService<BlobContainerClient>();

    private async Task<bool> BlobExists(string name) => (await Blobs.GetBlobClient(name).ExistsAsync()).Value;

    [Fact]
    public async Task Upload_streams_to_a_content_addressed_blob_and_discard_deletes_it()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(client);
        var name = Hpkg("zlib");
        byte[] first = [4, 5, 6], other = [8, 8];

        // Wrong or missing hash: refused, nothing stored.
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, job.JobId, name, first, sha: Sha(other))).StatusCode);
        Assert.False(await BlobExists($"arm64/{Sha(other)}/{name}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, job.JobId, name, first, sha: "")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, first)).StatusCode);
        Assert.True(await BlobExists($"arm64/{Sha(first)}/{name}"));
        // Identical again: 200 and the shared blob stays; different: 409 and the blob just written is removed.
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, first)).StatusCode);
        Assert.True(await BlobExists($"arm64/{Sha(first)}/{name}"));
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, job.JobId, name, other)).StatusCode);
        Assert.False(await BlobExists($"arm64/{Sha(other)}/{name}"));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/fail", new { summary = "x" })).StatusCode);
        Assert.False(await BlobExists($"arm64/{Sha(first)}/{name}"));
    }

    [Fact]
    public async Task Upload_requires_the_hash_field_before_the_file_part()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(client);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1]), "file", Hpkg("zlib"));
        form.Add(new StringContent(Sha([1])), "sha256");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/client/jobs/{job.JobId}/packages", form)).StatusCode);
    }

    [Fact]
    public async Task Recipe_sync_clones_and_then_updates_from_a_git_url()
    {
        var dir = Directory.CreateTempSubdirectory("renkuports-git-").FullName;
        var src = Path.Combine(dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "app-misc", "gitport"));
        Git(src, "init", "-q", "-b", "main");
        Git(src, "config", "uploadpack.allowFilter", "true");
        File.WriteAllText(Path.Combine(src, "app-misc", "gitport", "gitport-1.0.recipe"), "x\n");
        Git(src, "add", ".");
        Git(src, "commit", "-q", "-m", "one");

        var checkout = Path.Combine(dir, "checkout");
        using var gitApp = app.WithWebHostBuilder(b => b.UseSetting("PortsTree:GitUrl", new Uri(src).AbsoluteUri).UseSetting("PortsTree:Path", checkout));
        var admin = gitApp.CreateClient(new() { HandleCookies = true });
        (await admin.PostAsJsonAsync("/api/auth/login", new { email = AppFactory.AdminEmail, password = AppFactory.AdminPassword })).EnsureSuccessStatusCode();

        var first = await (await admin.PostAsync("/api/recipes/sync", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, first.GetProperty("total").GetInt32());
        Assert.True(File.Exists(Path.Combine(checkout, "app-misc", "gitport", "gitport-1.0.recipe")));
        var recipe = Assert.Single((await admin.GetFromJsonAsync<List<JsonElement>>("/api/recipes?search=gitport"))!);
        Assert.Equal(DateTime.Parse("2020-01-02T03:04:05Z").ToUniversalTime(), recipe.GetProperty("recipeUpdatedAt").GetDateTime().ToUniversalTime());

        File.WriteAllText(Path.Combine(src, "app-misc", "gitport", "gitport-1.1.recipe"), "y\n");
        Git(src, "add", ".");
        Git(src, "commit", "-q", "-m", "two");
        var second = await (await admin.PostAsync("/api/recipes/sync", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((1, 2), (second.GetProperty("added").GetInt32(), second.GetProperty("total").GetInt32()));
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var v in new[] { "GIT_AUTHOR_DATE", "GIT_COMMITTER_DATE" }) psi.Environment[v] = "2020-01-02T03:04:05Z";
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.invalid";
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
    }

    private static string Hpkg(string port) => $"{port}-{Guid.NewGuid():N}-1-arm64.hpkg";

    [Fact]
    public async Task Token_requires_valid_client_credentials()
    {
        var admin = await app.SignedInAsync();
        var (c, bearer) = await app.BuildClientAsync(admin);
        var anon = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/client/token", new { clientId = c.Id, secret = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/client/token", new { clientId = Guid.NewGuid(), secret = c.Secret })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/client/jobs/next", null)).StatusCode);
        // The SPA cookie does not authenticate the build-client API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PostAsync("/api/client/jobs/next", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bearer.PostAsync("/api/client/jobs/next", null)).StatusCode);

        // A deleted client's token stops working.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/clients/{c.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.PostAsync("/api/client/jobs/next", null)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_claims_of_one_job_have_exactly_one_winner()
    {
        var admin = await app.SignedInAsync();
        var clients = new List<HttpClient>();
        for (var i = 0; i < 4; i++) clients.Add((await app.BuildClientAsync(admin)).Http);

        for (var round = 0; round < 5; round++)
        {
            await app.ResetQueueAsync();
            var job = await QueueAsync(admin, "zlib");
            var results = await Task.WhenAll(clients.Select(c => c.PostAsync("/api/client/jobs/next", null)));
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(clients.Count - 1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            var won = await results.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<ClaimedJob>();
            Assert.Equal(job.Id, won!.JobId);
        }
    }

    [Fact]
    public async Task Upload_and_complete_publishes_a_downloadable_package()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(client);
        Assert.Equal(("zlib", "1.3.2", "sys-libs", "sys-libs/zlib/zlib-1.3.2.recipe"), (job.PortName, job.Version, job.Category, job.RecipePath));

        // Completing before any upload is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "x" })).StatusCode);

        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        var name = Hpkg("zlib");
        var up = await UploadAsync(client, job.JobId, name, bytes);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var complete = await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "build ok" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var done = (await complete.Content.ReadFromJsonAsync<JobView>())!;
        Assert.Equal("Succeeded", done.Status);
        Assert.Equal([name], done.Packages);

        var anon = app.CreateClient();
        var list = (await anon.GetFromJsonAsync<List<PackageView>>("/api/repo/arm64/packages?search=zlib"))!;
        var pkg = Assert.Single(list, p => p.FileName == name);
        Assert.Equal(bytes.Length, pkg.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), pkg.Sha256);
        Assert.Equal("Administrator", pkg.BuiltBy);

        var dl = await anon.GetAsync($"/repo/arm64/packages/{name}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/octet-stream", dl.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await dl.Content.ReadAsByteArrayAsync());
        Assert.Equal("build ok", await admin.GetStringAsync($"/api/jobs/{job.JobId}/log"));

        var platforms = (await anon.GetFromJsonAsync<JsonElement>("/api/platforms"));
        Assert.Equal(4, platforms.GetArrayLength());
        Assert.Equal(1, platforms.EnumerateArray().Single(p => p.GetProperty("platform").GetString() == "arm64").GetProperty("packageCount").GetInt32());
    }

    [Fact]
    public async Task Fail_marks_failed_clears_claim_and_emails_admins()
    {
        var admin = await app.SignedInAsync();
        var (c, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "openexr30");
        var job = await ClaimAsync(client);
        var before = app.Mail.Sent.Count;

        var res = await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/fail", new { summary = "compile error", log = "line 1\nerror: boom" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var failed = (await res.Content.ReadFromJsonAsync<JobView>())!;
        Assert.Equal("Failed", failed.Status);
        Assert.Null(failed.ClaimedBy);
        Assert.Null(failed.LeaseExpiresAt);
        Assert.Equal("compile error", failed.FailureSummary);
        Assert.True(failed.HasLog);
        Assert.Equal("line 1\nerror: boom", await admin.GetStringAsync($"/api/jobs/{job.JobId}/log"));

        var mail = app.Mail.Sent.Skip(before).Single();
        Assert.Contains(AppFactory.AdminEmail, mail.To);
        Assert.Contains("openexr30", mail.Subject);
        Assert.Contains(c.Name, mail.Body);

        var dash = await admin.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Contains(dash.GetProperty("recentFailures").EnumerateArray(), j => j.GetProperty("id").GetInt64() == job.JobId);
        var arm = dash.GetProperty("counts").EnumerateArray().Single(x => x.GetProperty("platform").GetString() == "arm64");
        Assert.Equal(1, arm.GetProperty("failed").GetInt32());

        // The client no longer holds it; an admin can requeue it.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/fail", new { summary = "again" })).StatusCode);
        var requeued = await admin.PostAsync($"/api/jobs/{job.JobId}/requeue", null);
        Assert.Equal("Queued", (await requeued.Content.ReadFromJsonAsync<JobView>())!.Status);
    }

    [Fact]
    public async Task Expired_lease_is_requeued_before_the_next_claim()
    {
        var admin = await app.SignedInAsync();
        var (_, first) = await app.BuildClientAsync(admin);
        var (_, second) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(first);
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsync("/api/client/jobs/next", null)).StatusCode);

        await app.WithDbAsync(db => db.Jobs.Where(j => j.Id == job.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1))));
        var before = app.Mail.Sent.Count;
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(first, job.JobId, Hpkg("zlib"), [5])).StatusCode);

        var again = await ClaimAsync(second);
        await app.WithDbAsync(async db => Assert.False(await db.Packages.AnyAsync(p => p.JobId == job.JobId)));
        Assert.Equal(job.JobId, again.JobId);
        Assert.Contains("lease", app.Mail.Sent.Skip(before).Single().Subject);
        var view = Assert.Single((await admin.GetFromJsonAsync<List<JobView>>("/api/jobs"))!);
        Assert.Equal(2, view.Attempts);
        // The first client lost the job.
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(first, job.JobId, Hpkg("zlib"), [1])).StatusCode);
    }

    [Fact]
    public async Task Duplicate_upload_is_ok_when_identical_and_conflict_when_different()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(client);
        var name = Hpkg("zlib");

        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, [1, 2, 3])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, [1, 2, 3])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, job.JobId, name, [9, 9, 9])).StatusCode);
        // Not public until the job succeeds; then the stored package is still the first one.
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/repo/arm64/packages/{name}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "" })).StatusCode);
        Assert.Equal([1, 2, 3], await app.CreateClient().GetByteArrayAsync($"/repo/arm64/packages/{name}"));

        // A later job (a rebuild without a revision bump) cannot replace the published package.
        await QueueAsync(admin, "zlib");
        var rebuild = await ClaimAsync(client);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, rebuild.JobId, name, [7, 7])).StatusCode);
    }

    [Fact]
    public async Task Packages_of_a_failed_job_are_unpublished_and_the_retry_can_reupload()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(client);
        var name = Hpkg("zlib");
        var anon = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, [1, 1])).StatusCode);
        Assert.DoesNotContain((await anon.GetFromJsonAsync<List<PackageView>>("/api/repo/arm64/packages"))!, p => p.FileName == name);
        Assert.Equal(0, (await anon.GetFromJsonAsync<JsonElement>("/api/platforms")).EnumerateArray()
            .Single(p => p.GetProperty("platform").GetString() == "arm64").GetProperty("packageCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/repo/arm64/packages/{name}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/fail", new { summary = "later step failed" })).StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.Packages.AnyAsync(p => p.JobId == job.JobId)));

        // Retry: requeue, reclaim, upload the same name with new content (hpkgs embed the build time).
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/jobs/{job.JobId}/requeue", null)).StatusCode);
        Assert.Equal(job.JobId, (await ClaimAsync(client)).JobId);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, name, [2, 2, 2])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "ok" })).StatusCode);
        Assert.Equal([2, 2, 2], await anon.GetByteArrayAsync($"/repo/arm64/packages/{name}"));
    }

    [Theory]
    [InlineData("../evil.hpkg")]
    [InlineData("dir/evil.hpkg")]
    [InlineData("dir\\evil.hpkg")]
    [InlineData("evil.txt")]
    [InlineData(".hpkg")]
    public async Task Bad_file_names_are_rejected(string fileName)
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var job = await ClaimAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, job.JobId, fileName, [1])).StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.Packages.AnyAsync()));
    }

    [Fact]
    public async Task Admin_endpoints_are_forbidden_for_builders()
    {
        var builder = await app.BuilderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsync("/api/recipes/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/recipes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsJsonAsync("/api/jobs", new { portName = "zlib", platforms = new[] { "arm64" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/dashboard")).StatusCode);

        // Builders manage their own clients only.
        var admin = await app.SignedInAsync();
        var (adminClient, _) = await app.BuildClientAsync(admin);
        var (own, _) = await app.BuildClientAsync(builder, "x86_64");
        var mine = (await builder.GetFromJsonAsync<List<JsonElement>>("/api/clients"))!;
        Assert.Equal([own.Id], mine.Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await builder.DeleteAsync($"/api/clients/{adminClient.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await builder.PostAsJsonAsync("/api/clients", new { name = "x", platform = "sparc" })).StatusCode);
    }

    [Fact]
    public async Task Recipes_sync_and_queueing_pick_the_highest_version()
    {
        var admin = await app.SignedInAsync();
        var sync = await admin.PostAsync("/api/recipes/sync", null);
        Assert.Equal(4, (await sync.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());

        var job = await QueueAsync(admin, "foo");
        Assert.Equal("1.10", job.Version);
        // Already queued for arm64: skipped; x86 is new.
        var again = await admin.PostAsJsonAsync("/api/jobs", new { portName = "foo", platforms = new[] { "arm64", "x86" } });
        Assert.Equal(["x86"], (await again.Content.ReadFromJsonAsync<List<JobView>>())!.Select(j => j.Platform));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/jobs", new { portName = "nope", platforms = new[] { "arm64" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/jobs", new { portName = "foo", platforms = new[] { "m68k" } })).StatusCode);

        Assert.Equal("Cancelled", (await (await admin.PostAsync($"/api/jobs/{job.Id}/cancel", null)).Content.ReadFromJsonAsync<JobView>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Spa_routes_get_index_html_and_unknown_downloads_404()
    {
        var anon = app.CreateClient();
        var spa = await anon.GetAsync("/repo/arm64");
        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Equal("text/html", spa.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/repo/arm64/packages/x.hpkg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/repo/sparc/packages/x.hpkg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("1.10", "1.9")]
    [InlineData("1.4", "1.4~rc5")]
    [InlineData("2.0", "1.99.99")]
    [InlineData("20260512_3.1", "20250101_3.1")]
    [InlineData("123456789012345678901234567890", "99")]
    public void Version_ordering(string higher, string lower)
    {
        Assert.True(VersionComparer.Instance.Compare(higher, lower) > 0);
        Assert.True(VersionComparer.Instance.Compare(lower, higher) < 0);
    }
}
