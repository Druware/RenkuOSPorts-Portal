using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>Paged package, job and recipe listings, and the admin package delete.</summary>
public partial class ApiTests
{
    /// <summary>Publishes the given arm64 packages (random bytes) through one succeeded zlib job (two matching builds), uploaded in order.</summary>
    private async Task PublishAsync(HttpClient admin, params string[] names)
    {
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        await BuildTwiceAsync(admin, client, names.Select(n => (n, Guid.NewGuid().ToByteArray())).ToArray());
    }

    private async Task<PageView<PackageView>> PageAsync(string query) =>
        (await app.CreateClient().GetFromJsonAsync<PageView<PackageView>>($"/api/repo/arm64/packages?{query}"))!;

    [Fact]
    public async Task Package_listing_is_paged_in_a_stable_newest_first_order()
    {
        var admin = await app.SignedInAsync();
        var names = Enumerable.Range(0, 7).Select(i => Hpkg(i < 5 ? "zlib" : "zlib_other")).ToArray();
        await PublishAsync(admin, names);
        var newestFirst = names.Reverse().ToList();

        var all = await PageAsync("pageSize=200");
        Assert.Equal((7, 1, 200), (all.Total, all.Page, all.PageSize));
        Assert.Equal(newestFirst, all.Items.Select(p => p.FileName));

        // Walking the pages gives every package exactly once, in the same order.
        var walked = new List<string>();
        var counts = new List<int>();
        foreach (var page in new[] { 1, 2, 3 })
        {
            var p = await PageAsync($"page={page}&pageSize=3");
            Assert.Equal((7, page, 3), (p.Total, p.Page, p.PageSize));
            walked.AddRange(p.Items.Select(x => x.FileName));
            counts.Add(p.Items.Count);
        }
        Assert.Equal(newestFirst, walked);
        Assert.Equal([3, 3, 1], counts);

        var past = await PageAsync("page=9&pageSize=3");
        Assert.Equal((0, 7, 9), (past.Items.Count, past.Total, past.Page));

        var search = await PageAsync("search=other&pageSize=1&page=2");
        Assert.Equal((2, newestFirst[1]), (search.Total, Assert.Single(search.Items).FileName));

        // Defaults and clamping.
        var def = await PageAsync("");
        Assert.Equal((1, 50, 7), (def.Page, def.PageSize, def.Items.Count));
        var low = await PageAsync("page=-3&pageSize=0");
        Assert.Equal((1, 1, 1), (low.Page, low.PageSize, low.Items.Count));
        Assert.Equal(200, (await PageAsync("pageSize=100000")).PageSize);
        Assert.Empty((await PageAsync($"page={int.MaxValue}&pageSize=200")).Items);
    }

    [Fact]
    public async Task Admin_deletes_a_package_row_and_blob_and_it_leaves_listing_download_and_index_input()
    {
        var admin = await app.SignedInAsync();
        string older = "zlib-1.3.1-1-arm64.hpkg", newer = "zlib-1.3.2-1-arm64.hpkg";
        await PublishAsync(admin, older, newer);
        string blob = "";
        await app.WithDbAsync(async db => blob = await db.Packages.Where(p => p.FileName == newer).Select(p => p.BlobName).SingleAsync());
        Assert.True(await BlobExists(blob));
        await app.WithDbAsync(async db =>
            Assert.Equal([newer], RepoIndexInput.Select(await RepoIndexer.CandidatesAsync(db, "arm64")).Selected.Select(p => p.FileName)));
        long jobId = 0;
        await app.WithDbAsync(async db => jobId = await db.Packages.Where(p => p.FileName == newer).Select(p => p.JobId!.Value).SingleAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().DeleteAsync($"/api/repo/arm64/packages/{newer}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.BuilderAsync()).DeleteAsync($"/api/repo/arm64/packages/{newer}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/repo/arm64/packages/{newer}")).StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.Packages.AnyAsync(p => p.FileName == newer)));
        Assert.False(await BlobExists(blob));
        Assert.Equal([older], (await PageAsync("")).Items.Select(p => p.FileName));
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/repo/arm64/packages/{newer}")).StatusCode);
        // The next index build's input set: the deleted package is gone and the older version takes its place.
        await app.WithDbAsync(async db =>
            Assert.Equal([older], RepoIndexInput.Select(await RepoIndexer.CandidatesAsync(db, "arm64")).Selected.Select(p => p.FileName)));
        // The job's record stays; it just no longer lists the package.
        var job = (await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs"))!.Items.Single(j => j.Id == jobId);
        Assert.Equal("Succeeded", job.Status);
        Assert.Equal([older], job.Packages);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/repo/arm64/packages/{newer}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/repo/arm64/packages/nope-1-1-arm64.hpkg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/repo/sparc/packages/{older}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/repo/arm64/packages/evil.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/repo/arm64/packages/..%2Fevil.hpkg")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_package_keeps_a_blob_another_row_still_references()
    {
        var admin = await app.SignedInAsync();
        string a = Hpkg("zlib"), b = Hpkg("zlib");
        await PublishAsync(admin, a, b);
        string blob = "";
        // Legacy rows could share a blob; point b at a's blob to simulate that.
        await app.WithDbAsync(async db =>
        {
            blob = await db.Packages.Where(p => p.FileName == a).Select(p => p.BlobName).SingleAsync();
            await db.Packages.Where(p => p.FileName == b).ExecuteUpdateAsync(s => s.SetProperty(p => p.BlobName, blob));
        });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/repo/arm64/packages/{a}")).StatusCode);
        Assert.True(await BlobExists(blob));
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync($"/repo/arm64/packages/{b}")).StatusCode);
    }

    [Fact]
    public async Task Job_listing_is_paged_newest_first_with_filters()
    {
        var admin = await app.SignedInAsync();
        var all = new[] { "x86", "x86_64", "arm64", "riscv64" };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/jobs", new { portName = "zlib", platforms = all })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/jobs", new { portName = "foo", platforms = all })).StatusCode);
        var cancelled = (await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs?pageSize=1"))!.Items[0];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/jobs/{cancelled.Id}/cancel", null)).StatusCode);

        var ids = new List<long>();
        foreach (var page in new[] { 1, 2, 3 })
        {
            var p = (await admin.GetFromJsonAsync<PageView<JobView>>($"/api/jobs?page={page}&pageSize=3"))!;
            Assert.Equal((8, page, 3), (p.Total, p.Page, p.PageSize));
            ids.AddRange(p.Items.Select(j => j.Id));
        }
        Assert.Equal(8, ids.Distinct().Count());
        Assert.Equal(ids.OrderDescending(), ids);
        Assert.Empty((await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs?page=4&pageSize=3"))!.Items);

        var queued = (await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs?status=Queued&platform=arm64"))!;
        Assert.Equal((2, 50), (queued.Total, queued.PageSize));
        Assert.Equal(7, (await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs?status=Queued&pageSize=500"))!.Total);
        Assert.Equal(200, (await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs?pageSize=500"))!.PageSize);
    }

    private record RecipeView(int Id, string PortName, string Version);

    [Fact]
    public async Task Recipe_listing_is_paged_in_a_stable_order()
    {
        var admin = await app.SignedInAsync();
        var all = (await admin.GetFromJsonAsync<PageView<RecipeView>>("/api/recipes?pageSize=200"))!;
        Assert.True(all.Total >= 4);
        Assert.Equal(all.Total, all.Items.Count);
        var walked = new List<int>();
        for (var page = 1; page <= all.Total; page++)
        {
            var p = (await admin.GetFromJsonAsync<PageView<RecipeView>>($"/api/recipes?page={page}&pageSize=1"))!;
            Assert.Equal((all.Total, page, 1), (p.Total, p.Page, p.PageSize));
            walked.Add(Assert.Single(p.Items).Id);
        }
        Assert.Equal(all.Items.Select(r => r.Id), walked);

        var foo = (await admin.GetFromJsonAsync<PageView<RecipeView>>("/api/recipes?search=foo&page=0&pageSize=0"))!;
        Assert.Equal((2, 1, 1), (foo.Total, foo.Page, foo.PageSize));
        Assert.Empty((await admin.GetFromJsonAsync<PageView<RecipeView>>("/api/recipes?search=foo&page=3&pageSize=1"))!.Items);
        Assert.Equal(50, (await admin.GetFromJsonAsync<PageView<RecipeView>>("/api/recipes"))!.PageSize);
    }

    private record RecipeStatusView(int Id, string? Status);

    [Fact]
    public async Task Recipe_listing_shows_the_latest_jobs_status_on_the_platform()
    {
        var admin = await app.SignedInAsync();
        int[] ids = [];
        await app.WithDbAsync(async db =>
        {
            ids = await db.Recipes.OrderBy(r => r.Id).Select(r => r.Id).Take(4).ToArrayAsync();
            Job J(int recipe, JobStatus status, string platform = "arm64") => new() { RecipeId = recipe, Platform = platform, Status = status, QueuedAt = DateTime.UtcNow };
            // Added one by one so Ids follow this order: the failure after the success is the latest.
            foreach (var j in new[] { J(ids[0], JobStatus.Succeeded), J(ids[1], JobStatus.Verifying), J(ids[2], JobStatus.Succeeded), J(ids[2], JobStatus.Failed),
                J(ids[3], JobStatus.Succeeded, "x86_64"), J(ids[0], JobStatus.Cancelled, "x86_64") })
            {
                db.Jobs.Add(j);
                await db.SaveChangesAsync();
            }
        });
        async Task<string?[]> StatusesAsync(string query)
        {
            var items = (await admin.GetFromJsonAsync<PageView<RecipeStatusView>>($"/api/recipes?pageSize=200{query}"))!.Items;
            return ids.Select(id => items.Single(r => r.Id == id).Status).ToArray();
        }
        Assert.Equal(new string?[] { "published", "queued", "failed", null }, await StatusesAsync("&platform=arm64"));
        // Without a platform the latest job of any platform counts.
        Assert.Equal(new string?[] { null, "queued", "failed", "published" }, await StatusesAsync(""));
    }
}
