using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;

namespace RenkuPorts.Server.Services;

/// <summary>The build priority of one port on one platform, as computed by <see cref="PriorityGraph"/>.</summary>
/// <param name="RecipePath">The recipe a build of the port uses on this platform (see <see cref="PriorityGraph.Compute"/>).</param>
/// <param name="Outstanding">Its recipe is stable on the platform and no package of the port is published there.</param>
/// <param name="Priority">Outstanding ports that need this one built first, directly or through other outstanding ports.</param>
/// <param name="Dependents">Outstanding ports that need it directly.</param>
/// <param name="Needs">The ports it needs directly (the edges <paramref name="Priority"/> follows); empty unless it is outstanding.</param>
public record PortPriorityResult(string PortName, string RecipePath, bool Outstanding, int Priority, int Dependents, IReadOnlyList<string> Needs);

/// <summary>
/// The port dependency graph of one architecture and the priorities read from it, after RenkuOSPorts'
/// <c>renkuos/bootstrap/arm64/phase2/resolve.py</c>: its architecture status, recipe choice, package and provider tables.
/// </summary>
public static partial class PriorityGraph
{
    /// <summary>The haiku and haiku_devel system packages (Haiku's <c>src/data/package_infos/generic</c>); no recipe builds them.</summary>
    private static readonly HashSet<string> System = [.. """
        haiku haiku_devel haiku_loader makefile_engine lib:libgcc_s lib:libstdc++ lib:libsupc++
        cmd:arp cmd:chop cmd:clear cmd:df cmd:ftpd cmd:hd cmd:hey cmd:login cmd:more cmd:passwd
        cmd:pidof cmd:ping cmd:prio cmd:ps cmd:renice cmd:resattr cmd:su cmd:telnet cmd:telnetd
        cmd:top cmd:traceroute cmd:trash cmd:unchop cmd:useradd cmd:watch cmd:xres
        """.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)];

    /// <summary><c>broken</c>, <c>stable</c>, <c>untested</c> or <c>unsupported</c>: what the recipe's ARCHITECTURES says of <paramref name="arch"/>.</summary>
    public static string Status(string? architectures, string arch)
    {
        var tokens = (architectures ?? "").ToLowerInvariant().Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Contains("!" + arch)) return "broken";
        if (tokens.Contains(arch) || tokens.Contains("all") || tokens.Contains("any")) return "stable";
        if (tokens.Contains("?" + arch) || tokens.Contains("?all")) return "untested";
        return "unsupported";
    }

    /// <summary>The entity names of a PROVIDES or REQUIRES value, lower case, without version constraints or comments.</summary>
    public static List<string> Entities(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0)
            .Select(l => Constraint().Split(l.Split(' ', '\t')[0].Trim('"'))[0].ToLowerInvariant()).ToList();

    [GeneratedRegex("[<>=]")]
    private static partial Regex Constraint();

    /// <summary>
    /// Computes every port's priority on <paramref name="arch"/>.
    /// <list type="bullet">
    /// <item>Each port's recipe is its newest stable one, else its newest untested one, else its newest (HaikuPorter without ALLOW_UNTESTED).</item>
    /// <item>Port X needs port Y if one of X's BUILD_REQUIRES or BUILD_PREREQUIRES, or a runtime REQUIRES of a package those pull in
    /// (followed transitively, as installing them needs it), resolves to a package of Y. Names only the system provides, and names
    /// no recipe provides, are ignored.</item>
    /// <item>Priority(Y) counts the outstanding ports that reach Y through such edges with only outstanding ports in between:
    /// a published intermediate already satisfies its dependents. Each port is counted once, so cycles terminate.</item>
    /// </list>
    /// </summary>
    /// <param name="publishedPorts">Ports with at least one published package on this platform.</param>
    public static List<PortPriorityResult> Compute(IEnumerable<EvaluatedRecipe> recipes, string arch, ISet<string> publishedPorts)
    {
        var ports = new Dictionary<string, (EvaluatedRecipe Recipe, string Status)>();
        foreach (var g in recipes.GroupBy(r => r.PortName))
        {
            var rs = g.Select(r => (Recipe: r, Status: Status(r.Attrs.GetValueOrDefault("ARCHITECTURES"), arch))).ToList();
            var usable = rs.Where(r => r.Status == "stable").ToList() is { Count: > 0 } s ? s
                : rs.Where(r => r.Status == "untested").ToList() is { Count: > 0 } u ? u : rs;
            ports[g.Key] = usable.MaxBy(r => r.Recipe.Version, VersionComparer.Instance);
        }

        // Every package each port makes, and what each package provides.
        var packages = new Dictionary<string, (string Port, List<string> Requires)>();
        var providers = new Dictionary<string, HashSet<string>>();
        void Provide(string entity, string pkg)
        {
            if (!providers.TryGetValue(entity, out var set)) providers[entity] = set = [];
            set.Add(pkg);
        }
        foreach (var (name, (recipe, _)) in ports)
        {
            var a = recipe.Attrs;
            foreach (var s in a.Keys.Where(k => k.StartsWith("PROVIDES_")).Select(k => k["PROVIDES_".Length..]).Prepend(""))
            {
                var pkg = s == "" ? name : a.GetValueOrDefault("PACKAGE_NAME_" + s) is { Length: > 0 } n ? n : name + "_" + s;
                var suffix = s == "" ? "" : "_" + s;
                packages[pkg] = (name, Entities(a.GetValueOrDefault("REQUIRES" + suffix)));
                Provide(pkg.ToLowerInvariant(), pkg);
                foreach (var e in Entities(a.GetValueOrDefault("PROVIDES" + suffix))) Provide(e, pkg);
            }
        }

        // resolve.py also prefers a package it already needs; a whole-tree graph has no such context, so that rule is dropped.
        var chosen = new Dictionary<string, string?>();
        string? Provider(string e)
        {
            if (chosen.TryGetValue(e, out var c)) return c;
            if (!providers.TryGetValue(e, out var cands)) return chosen[e] = null;
            var plain = e.Split(':', 2)[^1];
            return chosen[e] = cands
                .OrderByDescending(p => p.ToLowerInvariant() == e || p.ToLowerInvariant() == plain)
                .ThenByDescending(p => ports[packages[p].Port].Status == "stable")
                .ThenByDescending(p => !p.EndsWith("_bootstrap"))
                .ThenByDescending(p => p, StringComparer.Ordinal).First();
        }

        bool Outstanding(string port) => ports[port].Status == "stable" && !publishedPorts.Contains(port);

        // Reverse edges, from the outstanding ports only: a published port's own build needs no longer matter.
        var dependents = ports.Keys.ToDictionary(p => p, _ => new List<string>());
        var needsOf = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var x in ports.Keys.Where(Outstanding))
        {
            var a = ports[x].Recipe.Attrs;
            var queue = new Stack<string>(Entities(a.GetValueOrDefault("BUILD_REQUIRES")).Concat(Entities(a.GetValueOrDefault("BUILD_PREREQUIRES"))));
            var seen = new HashSet<string>();
            var needs = new HashSet<string>();
            while (queue.TryPop(out var e))
            {
                if (System.Contains(e) || Provider(e) is not { } p || !seen.Add(p)) continue;
                var (port, requires) = packages[p];
                needs.Add(port);
                foreach (var r in requires) queue.Push(r);
            }
            needs.Remove(x);
            foreach (var y in needs) dependents[y].Add(x);
            needsOf[x] = [.. needs.Order(StringComparer.Ordinal)];
        }

        // reach[y] = every port that reaches y over the dependents edges, as a bitset: reach[y] |= {d} | reach[d] for each
        // dependent d, repeated until nothing changes (monotone, so cycles settle too). A walk per port was minutes on the real tree.
        // Every node on dependents[] is outstanding, so the reach only passes through outstanding ports.
        var names = ports.Keys.ToArray();
        var index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var edges = names.Select(n => dependents[n].Select(d => index[d]).ToArray()).ToArray();
        var words = (names.Length + 63) / 64;
        var reach = names.Select(_ => new ulong[words]).ToArray();
        for (var changed = true; changed;)
        {
            changed = false;
            for (var y = 0; y < names.Length; y++)
                foreach (var d in edges[y])
                {
                    var (r, rd) = (reach[y], reach[d]);
                    var before = r[d >> 6];
                    r[d >> 6] |= 1UL << (d & 63);
                    var grew = r[d >> 6] != before;
                    for (var w = 0; w < words; w++)
                    {
                        var v = r[w] | rd[w];
                        if (v != r[w]) { r[w] = v; grew = true; }
                    }
                    changed |= grew;
                }
        }

        var result = new List<PortPriorityResult>(names.Length);
        for (var y = 0; y < names.Length; y++)
        {
            reach[y][y >> 6] &= ~(1UL << (y & 63)); // a port on a cycle reaches itself; it is not its own dependent
            var count = reach[y].Sum(w => global::System.Numerics.BitOperations.PopCount(w));
            result.Add(new(names[y], ports[names[y]].Recipe.RecipePath, Outstanding(names[y]), count, dependents[names[y]].Count,
                needsOf.GetValueOrDefault(names[y]) ?? []));
        }
        return result;
    }
}

/// <summary>
/// Keeps <see cref="PortPriority"/> rows of every platform current: evaluates the ports tree's recipes with
/// <see cref="RecipeEvaluator"/> (cached per recipe file hash in blob <c>priorities/{platform}/eval.json</c>, so only changed recipes
/// are evaluated again), computes <see cref="PriorityGraph"/> against the published packages and replaces the platform's rows.
/// Runs at startup, right after <see cref="RequestNow"/> (recipe sync, the admin's Recompute), and <see cref="Debounce"/> after the
/// last <see cref="MarkDirty"/> (packages published or deleted). A lease on blob <c>priorities/lock</c> lets one instance compute at a time.
/// </summary>
public sealed class PriorityComputer(IServiceScopeFactory scopes, BlobContainerClient blobs, IConfiguration cfg, ILogger<PriorityComputer> log) : BackgroundService
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private DateTime? _due;
    private volatile bool _running;

    /// <summary>True while this instance computes (another instance's run is not visible here).</summary>
    public bool Running => _running;
    /// <summary>The error of this instance's last computation, null if it succeeded.</summary>
    public string? LastError { get; private set; }

    /// <summary>Schedules a computation <see cref="Debounce"/> from now; a later call pushes it back again.</summary>
    public void MarkDirty() { lock (_gate) _due = DateTime.UtcNow + Debounce; }

    /// <summary>Schedules a computation right away.</summary>
    public void RequestNow() { lock (_gate) _due = DateTime.UtcNow; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!cfg.GetValue("Priorities:Schedule", true)) return; // tests drive runs themselves
        RequestNow();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            lock (_gate)
            {
                if (_due is null || _due > DateTime.UtcNow) continue;
                _due = null;
            }
            try
            {
                if (!await ComputeAsync(stop)) lock (_gate) _due ??= DateTime.UtcNow + Debounce; // another instance holds the lease
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                log.LogError(e, "Computing build priorities failed");
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>Computes the priorities of every platform now. False if another computation holds the lease.</summary>
    public async Task<bool> ComputeAsync(CancellationToken ct)
    {
        var root = cfg["PortsTree:Path"];
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException(LastError = $"PortsTree:Path '{root}' does not exist");

        var lockBlob = blobs.GetBlobClient("priorities/lock");
        try { await lockBlob.UploadAsync(BinaryData.Empty, new BlobUploadOptions { Conditions = new() { IfNoneMatch = ETag.All } }, ct); }
        catch (RequestFailedException e) when (e.Status is 409 or 412) { }
        var lease = lockBlob.GetBlobLeaseClient();
        try { await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct); }
        catch (RequestFailedException e) when (e.Status == 409) { return false; }

        using var renewing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew = RepoIndexer.RenewAsync(lease, renewing.Token);
        _running = true;
        LastError = null; // a previous run's error is stale while this one is in progress
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var recipes = (await db.Recipes.AsNoTracking().Select(r => new { r.Id, r.PortName, r.Version, r.RecipePath }).ToListAsync(ct));
            var ids = recipes.ToDictionary(r => r.RecipePath, r => r.Id);
            foreach (var platform in Validate.Platforms)
            {
                var started = DateTime.UtcNow;
                var cacheBlob = blobs.GetBlobClient($"priorities/{platform}/eval.json");
                Dictionary<string, EvalCacheEntry> cache;
                try { cache = (await cacheBlob.DownloadContentAsync(ct)).Value.Content.ToObjectFromJson<Dictionary<string, EvalCacheEntry>>()!; }
                catch (RequestFailedException e) when (e.Status == 404) { cache = []; }

                var (evaluated, newCache, ran) = await RecipeEvaluator.EvaluateAsync(root, recipes.Select(r => (r.PortName, r.Version, r.RecipePath)), platform, cache, log, ct);
                if (ran > 0 || newCache.Count != cache.Count) await cacheBlob.UploadAsync(BinaryData.FromObjectAsJson(newCache), overwrite: true, ct);
                var evaluatedAt = DateTime.UtcNow;

                var published = (await PublicEndpoints.Published(db).Where(p => p.Platform == platform).Select(p => p.PortName).Distinct().ToListAsync(ct)).ToHashSet();
                var computed = PriorityGraph.Compute(evaluated, platform, published);
                await SaveAsync(db, platform, computed, ids, ct);
                log.LogInformation("{Platform} build priorities: {Ports} ports ({Outstanding} outstanding), {Ran} of {Recipes} recipes evaluated in {Eval:F1} s, graph and save {Graph:F1} s",
                    platform, computed.Count, computed.Count(c => c.Outstanding), ran, evaluated.Count, (evaluatedAt - started).TotalSeconds, (DateTime.UtcNow - evaluatedAt).TotalSeconds);
            }
            LastError = null;
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            LastError = e.Message;
            throw;
        }
        finally
        {
            _running = false;
            await renewing.CancelAsync();
            await renew;
            try { await lease.ReleaseAsync(cancellationToken: CancellationToken.None); } catch (RequestFailedException) { } // it expires anyway
        }
    }

    /// <summary>Replaces the platform's priority and dependency rows with <paramref name="computed"/> in one transaction.</summary>
    private static async Task SaveAsync(AppDbContext db, string platform, List<PortPriorityResult> computed, Dictionary<string, int> recipeIds, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // A retry of the execution strategy runs the whole replace again from a clean change tracker.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.PortPriorities.Where(p => p.Platform == platform).ToDictionaryAsync(p => p.PortName, ct);
            foreach (var c in computed)
            {
                if (!rows.Remove(c.PortName, out var row)) db.PortPriorities.Add(row = new PortPriority { Platform = platform, PortName = c.PortName });
                row.RecipeId = recipeIds[c.RecipePath];
                row.Outstanding = c.Outstanding;
                row.Priority = c.Priority;
                row.Dependents = c.Dependents;
                row.ComputedAt = now;
            }
            db.PortPriorities.RemoveRange(rows.Values);
            await db.PortDependencies.Where(d => d.Platform == platform).ExecuteDeleteAsync(ct);
            db.PortDependencies.AddRange(computed.SelectMany(c => c.Needs.Select(n => new PortDependency { Platform = platform, PortName = c.PortName, DependsOn = n })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        db.ChangeTracker.Clear();
    }
}
