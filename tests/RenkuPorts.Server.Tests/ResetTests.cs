using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>The danger-zone reset: one platform back to scratch, the others and the shared data untouched.</summary>
public partial class ApiTests
{
    private record ResetView(string Platform, int Jobs, int Builds, int StagedPackages, int Packages, int Imports, int Blobs, long Bytes);

    private async Task<long> AddImportAsync(string platform, ImportStatus status)
    {
        long id = 0;
        await app.WithDbAsync(async db =>
        {
            var admin = await db.Users.Where(u => u.Email == AppFactory.AdminEmail).Select(u => u.Id).SingleAsync();
            var imp = new Import
            {
                SourceUrl = "http://x/repo", SourceHost = "x", Platform = platform, Status = status,
                StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, StartedByUserId = admin,
            };
            db.Imports.Add(imp);
            await db.SaveChangesAsync();
            id = imp.Id;
        });
        return id;
    }

    [Fact]
    public async Task Reset_wipes_one_platform_and_keeps_the_others_and_shared_data()
    {
        var admin = await app.SignedInAsync();
        await app.WithDbAsync(db => db.Imports.ExecuteDeleteAsync());

        // arm64: a published package (two builds, staged rows sharing its blob), its log, an import and an index.
        var published = Hpkg("zlib");
        await PublishAsync(admin, published);
        var publishedBlob = await BlobOfAsync(published);
        var armJob = (await (await app.SignedInAsync()).GetFromJsonAsync<PageView<JobView>>("/api/jobs?platform=arm64"))!.Items.Single();
        await PutBlobAsync($"logs/{armJob.Id}.log", "log");
        await app.WithDbAsync(db => db.Jobs.Where(j => j.Id == armJob.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.HasLog, true)));
        await PutBlobAsync(RepoIndexer.IndexBlob("arm64", "repo"), "old repo");
        await PutBlobAsync(RepoIndexer.IndexBlob("arm64", "repo.sha256"), "old sha");
        var armImport = await AddImportAsync("arm64", ImportStatus.Running);

        // x86: a job being built with a staged upload, an import and an index.
        var (_, x86Client) = await app.BuildClientAsync(admin, "x86");
        var x86Job = await QueueAsync(admin, "foo", "x86");
        Assert.Equal(x86Job.Id, (await ClaimAsync(x86Client)).JobId);
        var stagedName = "foo-1.10-1-x86.hpkg";
        var stagedBytes = Guid.NewGuid().ToByteArray();
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(x86Client, x86Job.Id, stagedName, stagedBytes)).StatusCode);
        var stagedBlob = $"x86/{Sha(stagedBytes)}/{stagedName}";
        await PutBlobAsync(RepoIndexer.IndexBlob("x86", "repo"), "x86 repo");
        var x86Import = await AddImportAsync("x86", ImportStatus.Completed);

        int recipes = 0, clients = 0;
        await app.WithDbAsync(async db => { recipes = await db.Recipes.CountAsync(); clients = await db.BuildClients.CountAsync(); });

        // Bad requests and non-admins change nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/reset", new { platform = "arm64", confirm = "arm6" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/reset", new { platform = "arm64" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/reset", new { platform = "sparc", confirm = "sparc" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.BuilderAsync()).PostAsJsonAsync("/api/admin/reset", new { platform = "arm64", confirm = "arm64" })).StatusCode);
        // A live import of the platform blocks it (also for "all"); a stale one does not.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/reset", new { platform = "arm64", confirm = "arm64" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/reset", new { platform = "all", confirm = "all" })).StatusCode);
        Assert.True(await BlobExists(publishedBlob));
        await app.WithDbAsync(db => db.Imports.Where(i => i.Id == armImport).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow - RepoImporter.StaleAfter - TimeSpan.FromMinutes(1))));

        var res = await admin.PostAsJsonAsync("/api/admin/reset", new { platform = "arm64", confirm = "arm64" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var t = Assert.Single((await res.Content.ReadFromJsonAsync<List<ResetView>>())!);
        Assert.Equal(("arm64", 1, 2, 2, 1, 1), (t.Platform, t.Jobs, t.Builds, t.StagedPackages, t.Packages, t.Imports));
        Assert.True(t.Blobs >= 4); // the package blob, the log, repo and repo.sha256; the shared test container may hold index files of earlier runs
        Assert.Equal(16, t.Bytes);

        foreach (var n in new[] { publishedBlob, $"logs/{armJob.Id}.log", RepoIndexer.IndexBlob("arm64", "repo"), RepoIndexer.IndexBlob("arm64", "repo.sha256") })
            Assert.False(await BlobExists(n), n);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/repo/arm64/packages/{published}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/repo/arm64/repo")).StatusCode);
        Assert.True(await BlobExists(stagedBlob));
        Assert.True(await BlobExists(RepoIndexer.IndexBlob("x86", "repo")));
        await app.WithDbAsync(async db =>
        {
            Assert.False(await db.Jobs.AnyAsync(j => j.Platform == "arm64"));
            Assert.False(await db.Packages.AnyAsync(p => p.Platform == "arm64"));
            Assert.False(await db.Imports.AnyAsync(i => i.Platform == "arm64"));
            Assert.True(await db.Jobs.AnyAsync(j => j.Id == x86Job.Id));
            Assert.True(await db.StagedPackages.AnyAsync(s => s.BlobName == stagedBlob));
            Assert.True(await db.Imports.AnyAsync(i => i.Id == x86Import));
            Assert.Equal(recipes, await db.Recipes.CountAsync());
            Assert.Equal(clients, await db.BuildClients.CountAsync());
        });
    }
}
