using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>The build-client views of the queue: GET /api/client/jobs and /api/client/me.</summary>
public partial class ApiTests
{
    private record ClientJobView(long Id, string PortName, string Platform, string Status, int Priority, string? ClaimedBy, bool Mine, bool SameOwner,
        long? QueuePosition, List<string> WaitingOn, int WaitingOnCount, bool InMyChain, bool InOwnerChain, int? Unblocks);
    private record OwnerClientView(string Name, string Platform, DateTime? LastSeenAt, long? CurrentJobId);
    private record ClientSelfView(Guid Id, string Name, string Platform, string Owner, ClientJobView? CurrentJob, List<OwnerClientView> OwnerClients);

    private static async Task<PageView<ClientJobView>> ClientJobsAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<PageView<ClientJobView>>("/api/client/jobs" + query))!;

    private async Task AddEdgesAsync(string platform, params (string Port, string DependsOn)[] edges) => await app.WithDbAsync(async db =>
    {
        db.PortDependencies.AddRange(edges.Select(e => new PortDependency { Platform = platform, PortName = e.Port, DependsOn = e.DependsOn }));
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Client_queue_views_need_a_client_token_and_the_token_does_not_reach_admin_endpoints()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        foreach (var path in new[] { "/api/client/jobs", "/api/client/me" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(path)).StatusCode); // the SPA cookie alone
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/client/jobs?scope=everyone")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/client/jobs?status=Bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/client/jobs?status=7")).StatusCode);
    }

    [Fact]
    public async Task Client_jobs_mark_mine_and_same_owner_and_filter_by_scope_without_leaking_users()
    {
        var admin = await app.SignedInAsync();
        var builder = await app.BuilderAsync();
        var (x, cx) = await app.BuildClientAsync(admin);
        var (y, cy) = await app.BuildClientAsync(admin);
        var (z, cz) = await app.BuildClientAsync(builder);
        var jobs = new[] { await QueueAsync(admin, "zlib"), await QueueAsync(admin, "foo"), await QueueAsync(admin, "openexr30"), await QueueAsync(admin, "zlib", "x86") };
        var claimed = new[] { (await ClaimAsync(cx)).JobId, (await ClaimAsync(cy)).JobId, (await ClaimAsync(cz)).JobId };

        var all = await ClientJobsAsync(cx);
        Assert.Equal(4, all.Total);
        var byId = all.Items.ToDictionary(j => j.Id);
        Assert.Equal((x.Name, true, false), (byId[claimed[0]].ClaimedBy, byId[claimed[0]].Mine, byId[claimed[0]].SameOwner));
        Assert.Equal((y.Name, false, true), (byId[claimed[1]].ClaimedBy, byId[claimed[1]].Mine, byId[claimed[1]].SameOwner));
        Assert.Equal((z.Name, false, false), (byId[claimed[2]].ClaimedBy, byId[claimed[2]].Mine, byId[claimed[2]].SameOwner));
        Assert.Equal(("Queued", null), (byId[jobs[3].Id].Status, byId[jobs[3].Id].ClaimedBy));

        Assert.Equal([claimed[0]], (await ClientJobsAsync(cx, "?scope=mine")).Items.Select(j => j.Id));
        Assert.Equal(claimed[..2].Order(), (await ClientJobsAsync(cx, "?scope=owner")).Items.Select(j => j.Id).Order());
        Assert.Equal([claimed[2]], (await ClientJobsAsync(cz, "?scope=owner")).Items.Select(j => j.Id));
        Assert.Equal([jobs[3].Id], (await ClientJobsAsync(cx, "?platform=x86")).Items.Select(j => j.Id));
        Assert.Equal([jobs[3].Id], (await ClientJobsAsync(cx, "?status=Queued")).Items.Select(j => j.Id));
        Assert.Empty((await ClientJobsAsync(cx, "?status=Succeeded")).Items);

        // Nothing identifying the owners beyond display names: no emails, user ids or secrets.
        string adminId = "", builderId = "", builderEmail = "";
        await app.WithDbAsync(async db =>
        {
            adminId = (await db.Users.FirstAsync(u => u.Email == AppFactory.AdminEmail)).Id;
            var b = await db.BuildClients.Include(c => c.Owner).FirstAsync(c => c.Id == z.Id);
            (builderId, builderEmail) = (b.OwnerId, b.Owner!.Email!);
        });
        foreach (var json in new[] { await cx.GetStringAsync("/api/client/jobs"), await cx.GetStringAsync("/api/client/me"), await cz.GetStringAsync("/api/client/me") })
            foreach (var secret in new[] { AppFactory.AdminEmail, builderEmail, adminId, builderId, x.Secret, y.Secret, z.Secret, "@renkuos.local" })
                Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Queue_position_is_the_order_the_claim_takes()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        await QueueAsync(admin, "openexr30");
        await QueueAsync(admin, "foo");
        // foo 1.9: same port and priority as foo 1.10, queued later.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/jobs", new { portName = "foo", version = "1.9", platforms = new[] { "arm64" } })).StatusCode);
        var other = await QueueAsync(admin, "foo", "x86");
        await SetPriorityAsync("foo", 3);
        await SetPriorityAsync("openexr30", 2);
        await SetPriorityAsync("zlib", 9, platform: "x86");

        var listed = (await ClientJobsAsync(client, "?status=Queued")).Items;
        Assert.Null(listed.Single(j => j.Id == other.Id).QueuePosition); // another platform
        var expected = listed.Where(j => j.Platform == "arm64").OrderBy(j => j.QueuePosition).Select(j => j.Id).ToList();
        Assert.Equal([1L, 2, 3, 4], listed.Where(j => j.Platform == "arm64").Select(j => j.QueuePosition!.Value).Order());
        Assert.Equal(3, listed.First().Priority); // listed in claim order too

        var taken = new List<long>();
        for (var i = 0; i < expected.Count; i++) taken.Add((await ClaimAsync(client)).JobId);
        Assert.Equal(expected, taken);
        Assert.Equal(listed.Where(j => j.Platform == "arm64").Select(j => j.Id), taken);
    }

    [Fact]
    public async Task Client_jobs_page_size_is_clamped()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        await QueueAsync(admin, "foo");
        Assert.Equal((1, 2, 1, 1), ((await ClientJobsAsync(client, "?pageSize=0&page=0")) is var a ? (a.Items.Count, a.Total, a.Page, a.PageSize) : default));
        Assert.Equal(200, (await ClientJobsAsync(client, "?pageSize=500")).PageSize);
        Assert.Empty((await ClientJobsAsync(client, "?page=3&pageSize=1")).Items);
    }

    [Fact]
    public async Task Client_jobs_show_the_dependency_chain()
    {
        // openexr30 needs foo needs zlib on arm64; x86 has no edges.
        await AddEdgesAsync("arm64", ("openexr30", "foo"), ("foo", "zlib"));
        var admin = await app.SignedInAsync();
        var builder = await app.BuilderAsync();
        var (x, cx) = await app.BuildClientAsync(admin);
        var (y, cy) = await app.BuildClientAsync(admin);
        var (z, cz) = await app.BuildClientAsync(builder);
        var idleName = z.Name;
        var zlib = await QueueAsync(admin, "zlib");
        var foo = await QueueAsync(admin, "foo");
        var exr = await QueueAsync(admin, "openexr30");
        var exrX86 = await QueueAsync(admin, "openexr30", "x86");
        Assert.Equal(zlib.Id, (await ClaimAsync(cx)).JobId); // X builds zlib
        Assert.Equal(foo.Id, (await ClaimAsync(cy)).JobId);  // Y (same owner) builds foo

        var seen = (await ClientJobsAsync(cx)).Items.ToDictionary(j => j.Id);
        Assert.Equal(["foo", "zlib"], seen[exr.Id].WaitingOn); // nearest first
        Assert.Equal((2, true, true, null), (seen[exr.Id].WaitingOnCount, seen[exr.Id].InMyChain, seen[exr.Id].InOwnerChain, seen[exr.Id].Unblocks));
        Assert.Equal((0, false, false), (seen[exrX86.Id].WaitingOnCount, seen[exrX86.Id].InMyChain, seen[exrX86.Id].InOwnerChain));
        Assert.Equal(["zlib"], seen[foo.Id].WaitingOn);
        Assert.Equal((true, false, null), (seen[foo.Id].InMyChain, seen[foo.Id].InOwnerChain, seen[foo.Id].Unblocks));
        Assert.Equal((false, true, 1), (seen[zlib.Id].InMyChain, seen[zlib.Id].InOwnerChain, seen[zlib.Id].Unblocks)); // only openexr30 is still queued

        // Another owner's client sees the tree but no chain of its own.
        var other = (await ClientJobsAsync(cz)).Items.Single(j => j.Id == exr.Id);
        Assert.Equal((2, false, false), (other.WaitingOnCount, other.InMyChain, other.InOwnerChain));

        var me = (await cx.GetFromJsonAsync<ClientSelfView>("/api/client/me"))!;
        Assert.Equal((x.Id, "Administrator", zlib.Id, 1), (me.Id, me.Owner, me.CurrentJob!.Id, me.CurrentJob.Unblocks));
        // Other tests leave more clients of the admin behind; these two are building.
        Assert.Equal(new[] { (x.Name, (long?)zlib.Id), (y.Name, foo.Id) }.Order(), me.OwnerClients.Where(c => c.CurrentJobId != null).Select(c => (c.Name, c.CurrentJobId)).Order());
        Assert.DoesNotContain(me.OwnerClients, c => c.Name == idleName);
        var idle = (await cz.GetFromJsonAsync<ClientSelfView>("/api/client/me"))!;
        Assert.Null(idle.CurrentJob);
        Assert.Single(idle.OwnerClients);
    }

    [Fact]
    public async Task Priority_computation_replaces_the_dependency_edges()
    {
        await AddEdgesAsync("arm64", ("openexr30", "foo"));
        await AddEdgesAsync("x86", ("foo", "zlib"));
        // The factory's tree: foo (newest 1.10) build-requires zlib on arm64 only.
        File.WriteAllText(Path.Combine(app.PortsTree, "sys-libs/zlib/zlib-1.3.2.recipe"), "ARCHITECTURES=\"all\"\nPROVIDES_devel=\"devel:libz$secondaryArchSuffix\"\n");
        File.WriteAllText(Path.Combine(app.PortsTree, "dev-libs/foo/foo-1.10.recipe"),
            "ARCHITECTURES=\"all\"\nif [ \"$targetArchitecture\" = arm64 ]; then BUILD_REQUIRES=\"devel:libz\"; fi\n");
        Assert.True(await app.Services.GetRequiredService<PriorityComputer>().ComputeAsync(default));

        await app.WithDbAsync(async db => Assert.Equal([("arm64", "foo", "zlib")],
            (await db.PortDependencies.ToListAsync()).Select(d => (d.Platform, d.PortName, d.DependsOn))));
    }
}
