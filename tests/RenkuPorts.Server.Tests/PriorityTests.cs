using System.Net;
using System.Net.Http.Json;
using Azure.Storage.Blobs.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>Build priorities: the dependency graph, recipe evaluation, the claim order and the admin endpoints.</summary>
public partial class ApiTests
{
    private record PrioRecipeView(int Id, string PortName, string Version, int? Priority, int? Dependents, bool? Outstanding, DateTime? PinnedAt = null);

    private static EvaluatedRecipe R(string port, string version, string archs, string? provides = null, string? build = null, string? requires = null, Dictionary<string, string>? more = null)
    {
        var a = new Dictionary<string, string> { ["ARCHITECTURES"] = archs, ["PROVIDES"] = provides ?? $"{port} = {version}" };
        if (build is not null) a["BUILD_REQUIRES"] = build;
        if (requires is not null) a["REQUIRES"] = requires;
        foreach (var (k, v) in more ?? []) a[k] = v;
        return new(port, version, $"cat/{port}/{port}-{version}.recipe", a);
    }

    [Fact]
    public void Priority_counts_outstanding_dependents_directly_and_transitively()
    {
        var recipes = new[]
        {
            R("lib", "1.0", "all", "lib = 1.0\nlib:libz = 1.0", requires: "haiku",
                more: new() { ["PROVIDES_devel"] = "lib_devel = 1.0\ndevel:libz = 1.0", ["REQUIRES_devel"] = "lib == 1.0 base" }),
            R("a", "1", "arm64", build: "haiku_devel\ndevel:libz >= 1\ncmd:nothing_provides_this"),
            R("b", "1", "all", build: "devel:libz"),
            R("mid", "1", "all", build: "devel:libz"),
            R("top", "1", "all", build: "mid"),                       // lib only through mid (outstanding): counts
            R("built", "1", "all", build: "devel:libz"),              // published
            R("over", "1", "all", build: "built"),                    // lib only through built: does not count
            R("wrap", "1", "all", "wrap = 1", requires: "lib:libz"),  // runtime-requires lib
            R("user", "1", "all", build: "wrap"),                     // installing wrap needs lib: direct
            R("x", "1", "all", build: "y\ndevel:libz"),
            R("y", "1", "all", build: "x"),                           // cycle x <-> y
            R("brk", "1", "all !arm64", build: "devel:libz"),
            R("uns", "1", "x86_64", build: "devel:libz"),
            R("unt", "1", "?arm64", build: "devel:libz"),
            // A newer untested recipe loses to the stable one, which is the one that needs lib.
            R("pick", "1.0", "arm64", build: "devel:libz"),
            R("pick", "2.0", "?arm64"),
        };
        var res = PriorityGraph.Compute(recipes, "arm64", new HashSet<string> { "built" }).ToDictionary(r => r.PortName);

        // Direct: a, b, mid, user (via wrap's REQUIRES), x, pick; built is published. Transitive adds top and y, not over.
        Assert.Equal(6, res["lib"].Dependents);
        Assert.Equal(8, res["lib"].Priority);
        Assert.Equal((1, 1), (res["mid"].Priority, res["mid"].Dependents));
        Assert.Equal((1, 1), (res["x"].Priority, res["y"].Priority));
        Assert.Equal((1, 1), (res["built"].Priority, res["built"].Dependents)); // over still needs built itself
        Assert.False(res["built"].Outstanding);
        Assert.False(res["brk"].Outstanding);
        Assert.False(res["uns"].Outstanding);
        Assert.False(res["unt"].Outstanding);
        Assert.True(res["pick"].Outstanding);
        Assert.Equal("cat/pick/pick-1.0.recipe", res["pick"].RecipePath);
        Assert.Equal(1, res["wrap"].Priority);
    }

    [Fact]
    public void Status_and_entities_follow_haikuporter()
    {
        Assert.Equal("broken", PriorityGraph.Status("all !arm64", "arm64"));
        Assert.Equal("stable", PriorityGraph.Status("x86_64 arm64", "arm64"));
        Assert.Equal("untested", PriorityGraph.Status("?all", "arm64"));
        Assert.Equal("unsupported", PriorityGraph.Status("x86_64", "arm64"));
        Assert.Equal(["lib:libz", "cmd:python3.10", "foo"], PriorityGraph.Entities("\tlib:libz >= 1 # comment\n\n# only comment\n  cmd:python3.10\n\"Foo\" <2"));
    }

    /// <summary>Unmodified recipes from the ports tree (RealRecipes/), evaluated with bash as HaikuPorter would.</summary>
    [Fact]
    public async Task Real_recipes_evaluate_like_haikuporter()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "RealRecipes");
        (string, string, string)[] recipes =
        [
            ("zlib", "1.3.2", "sys-libs/zlib/zlib-1.3.2.recipe"), ("libpng16", "1.6.59", "media-libs/libpng/libpng16-1.6.59.recipe"),
            ("brotli", "1.2.1", "app-arch/brotli/brotli-1.2.1.recipe"), ("chrpath", "0.16", "app-admin/chrpath/chrpath-0.16.recipe"),
        ];
        var (evaluated, cache, ran) = await RecipeEvaluator.EvaluateAsync(root, recipes, "arm64", new Dictionary<string, EvalCacheEntry>(), NullLogger.Instance, default);
        Assert.Equal(4, ran);
        var zlib = evaluated.Single(r => r.PortName == "zlib").Attrs;
        Assert.Equal(["zlib", "lib:libz"], PriorityGraph.Entities(zlib["PROVIDES"])); // $secondaryArchSuffix expanded to ""
        Assert.Contains("lib:libz = 1.3.2 compat >= 1", zlib["PROVIDES"]);           // ${libVersion%%.*}
        Assert.Equal(["zlib_devel", "devel:libz"], PriorityGraph.Entities(zlib["PROVIDES_devel"]));
        // brotli builds its python sub-packages in a loop with eval; 3.14 gets a PACKAGE_NAME with a dot.
        var brotli = evaluated.Single(r => r.PortName == "brotli").Attrs;
        Assert.Equal("brotli_python3.14", brotli["PACKAGE_NAME_python314"]);
        Assert.Contains("brotli_python310", PriorityGraph.Entities(brotli["PROVIDES_python310"]));

        var res = PriorityGraph.Compute(evaluated, "arm64", new HashSet<string>()).ToDictionary(r => r.PortName);
        Assert.Equal((1, 1), (res["zlib"].Priority, res["zlib"].Dependents)); // libpng16 build-requires devel:libz
        Assert.True(res["chrpath"].Outstanding);                               // "all !x86_gcc2"
        Assert.Equal("broken", PriorityGraph.Status(evaluated.Single(r => r.PortName == "chrpath").Attrs["ARCHITECTURES"], "x86_gcc2"));

        // Unchanged files come from the cache.
        var again = await RecipeEvaluator.EvaluateAsync(root, recipes, "arm64", cache, NullLogger.Instance, default);
        Assert.Equal(0, again.Evaluated);
        Assert.Equal(evaluated.Select(r => r.Attrs.Count), again.Recipes.Select(r => r.Attrs.Count));
    }

    private async Task SetPriorityAsync(string port, int priority, bool outstanding = true, string platform = "arm64") => await app.WithDbAsync(async db =>
    {
        var recipe = await db.Recipes.Where(r => r.PortName == port).OrderByDescending(r => r.Id).FirstAsync();
        db.PortPriorities.Add(new PortPriority { Platform = platform, PortName = port, RecipeId = recipe.Id, Priority = priority, Dependents = priority, Outstanding = outstanding, ComputedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Claim_takes_the_highest_priority_then_the_oldest()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");      // oldest, no priority row: 0
        await QueueAsync(admin, "openexr30"); // priority 2
        await QueueAsync(admin, "foo");       // priority 2, younger
        await QueueAsync(admin, "foo", "x86"); // other platform; its priority row there must not matter
        await SetPriorityAsync("openexr30", 2);
        await SetPriorityAsync("foo", 2);
        await SetPriorityAsync("zlib", 50, platform: "x86");

        Assert.Equal(["openexr30", "foo", "zlib"], [(await ClaimAsync(client)).PortName, (await ClaimAsync(client)).PortName, (await ClaimAsync(client)).PortName]);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/client/jobs/next", null)).StatusCode);
    }

    [Fact]
    public async Task Queue_top_n_queues_the_highest_outstanding_ports_not_yet_built_or_queued()
    {
        var admin = await app.SignedInAsync();
        await SetPriorityAsync("zlib", 7);
        await SetPriorityAsync("foo", 3);
        await SetPriorityAsync("openexr30", 9, outstanding: false);

        var recipes = (await admin.GetFromJsonAsync<PageView<PrioRecipeView>>("/api/recipes?platform=arm64&sort=priority"))!.Items;
        Assert.Equal(("openexr30", 9, false), (recipes[0].PortName, recipes[0].Priority, recipes[0].Outstanding));
        Assert.Equal(("zlib", 7, 7), (recipes[1].PortName, recipes[1].Priority, recipes[1].Dependents));
        Assert.Null((await admin.GetFromJsonAsync<PageView<PrioRecipeView>>("/api/recipes?search=zlib"))!.Items[0].Priority);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/recipes?sort=priority")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/recipes?platform=m68k")).StatusCode);

        var first = (await (await admin.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 1 })).Content.ReadFromJsonAsync<List<JobView>>())!;
        Assert.Equal([("zlib", "1.3.2", "arm64")], first.Select(j => (j.PortName, j.Version, j.Platform)));

        // zlib built and published: of the rest only foo is outstanding.
        var (_, client) = await app.BuildClientAsync(admin);
        await BuildTwiceAsync(admin, client, (Hpkg("zlib"), [3]));
        var rest = (await (await admin.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 5 })).Content.ReadFromJsonAsync<List<JobView>>())!;
        Assert.Equal(["foo"], rest.Select(j => j.PortName));
        Assert.Empty((await (await admin.PostAsync("/api/priorities/arm64/queue-top", JsonContent.Create(new { }))).Content.ReadFromJsonAsync<List<JobView>>())!);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 201 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/priorities/m68k/queue-top", new { count = 1 })).StatusCode);
    }

    [Fact]
    public async Task Queue_top_n_is_a_conflict_before_priorities_are_calculated()
    {
        var admin = await app.SignedInAsync();
        await SetPriorityAsync("zlib", 7, platform: "x86"); // another platform's rows do not count
        var res = await admin.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 1 });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("sync recipes", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 0 })).StatusCode);
    }

    [Fact]
    public async Task Priority_endpoints_are_admin_only()
    {
        var builder = await app.BuilderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/priorities")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsync("/api/priorities/recompute", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsJsonAsync("/api/priorities/arm64/queue-top", new { count = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/recipes?platform=arm64&sort=priority")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PutAsync("/api/priorities/arm64/zlib/pin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.DeleteAsync("/api/priorities/arm64/zlib/pin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/priorities")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await (await app.SignedInAsync()).PostAsync("/api/priorities/recompute", null)).StatusCode);
    }

    [Fact]
    public async Task Pinned_ports_come_first_newest_pin_first_and_stay_pinned_across_a_recompute()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        var zlib = await QueueAsync(admin, "zlib");
        var exr = await QueueAsync(admin, "openexr30");
        var foo = await QueueAsync(admin, "foo");
        await SetPriorityAsync("openexr30", 9);
        await SetPriorityAsync("foo", 5);
        await SetPriorityAsync("zlib", 1);
        async Task<List<long>> Order() => (await ClientJobsAsync(client, "?status=Queued")).Items.OrderBy(j => j.QueuePosition).Select(j => j.Id).ToList();
        Task<HttpResponseMessage> Pin(string port, string platform = "arm64") => admin.PutAsync($"/api/priorities/{platform}/{port}/pin", null);
        Assert.Equal([exr.Id, foo.Id, zlib.Id], await Order());

        Assert.Equal(HttpStatusCode.NoContent, (await Pin("zlib")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Pin("foo")).StatusCode);
        Assert.Equal([foo.Id, zlib.Id, exr.Id], await Order());
        Assert.Equal(HttpStatusCode.NoContent, (await Pin("zlib")).StatusCode); // pinning again moves it to the top
        Assert.Equal([zlib.Id, foo.Id, exr.Id], await Order());
        var recipes = (await admin.GetFromJsonAsync<PageView<PrioRecipeView>>("/api/recipes?platform=arm64&sort=priority"))!.Items;
        Assert.Equal(["zlib", "foo", "openexr30"], recipes.Select(r => r.PortName).Distinct().Take(3));
        Assert.NotNull(recipes[0].PinnedAt);
        Assert.Null(recipes.First(r => r.PortName == "openexr30").PinnedAt);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/priorities/arm64/foo/pin")).StatusCode);
        Assert.Equal([zlib.Id, exr.Id, foo.Id], await Order());

        Assert.Equal(HttpStatusCode.NotFound, (await Pin("nosuchport")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/priorities/arm64/nosuchport/pin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Pin("zlib", "m68k")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Pin("zlib", "x86")).StatusCode); // no priorities computed there

        // A recompute rewrites zlib's row but keeps the pin.
        Assert.True(await app.Services.GetRequiredService<PriorityComputer>().ComputeAsync(default));
        Assert.NotNull((await admin.GetFromJsonAsync<PageView<PrioRecipeView>>("/api/recipes?platform=arm64&search=zlib"))!.Items[0].PinnedAt);
        Assert.Equal(zlib.Id, (await Order())[0]);
        Assert.Equal("zlib", (await ClaimAsync(client)).PortName);
    }

    [Fact]
    public async Task Computer_evaluates_the_tree_and_stores_priorities_per_platform()
    {
        // The factory's tree: make foo (newest 1.10) build-require zlib on arm64 only.
        File.WriteAllText(Path.Combine(app.PortsTree, "sys-libs/zlib/zlib-1.3.2.recipe"), "ARCHITECTURES=\"all\"\nPROVIDES_devel=\"devel:libz$secondaryArchSuffix\"\n");
        File.WriteAllText(Path.Combine(app.PortsTree, "dev-libs/foo/foo-1.10.recipe"),
            "ARCHITECTURES=\"all\"\nif [ \"$targetArchitecture\" = arm64 ]; then BUILD_REQUIRES=\"devel:libz\"; fi\n");
        var computer = app.Services.GetRequiredService<PriorityComputer>();
        Assert.True(await computer.ComputeAsync(default));

        var admin = await app.SignedInAsync();
        var arm = (await admin.GetFromJsonAsync<PageView<PrioRecipeView>>("/api/recipes?platform=arm64&sort=priority"))!.Items;
        Assert.Equal(("zlib", 1, true), (arm[0].PortName, arm[0].Priority, arm[0].Outstanding));
        var x86 = (await admin.GetFromJsonAsync<PageView<PrioRecipeView>>("/api/recipes?platform=x86_64&search=zlib"))!.Items;
        Assert.Equal(0, x86[0].Priority);
        var status = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/priorities");
        Assert.All(status.GetProperty("platforms").EnumerateArray(), p => Assert.NotEqual(System.Text.Json.JsonValueKind.Null, p.GetProperty("computedAt").ValueKind));
        Assert.True((await Blobs.GetBlobClient("priorities/arm64/eval.json").ExistsAsync()).Value);

        // Another holder of the lease: this computation does not run.
        var lease = Blobs.GetBlobClient("priorities/lock").GetBlobLeaseClient();
        await lease.AcquireAsync(TimeSpan.FromSeconds(15));
        try { Assert.False(await computer.ComputeAsync(default)); }
        finally { await lease.ReleaseAsync(); }
    }
}
