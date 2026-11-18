using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Read-only views for build clients: the job queue (what waits, what is being built, by whom) and the client itself.</summary>
public static partial class BuildClientApi
{
    /// <summary>
    /// The order build clients claim Queued jobs in, as a SQL ORDER BY over the Jobs table: highest build priority of the job's
    /// port on its platform first (no PortPriorities row counts as 0), then oldest, then lowest id. The claim statement and
    /// <see cref="QueueSlotsSql"/> both use it, so the queue position shown is the order the claim takes.
    /// </summary>
    public const string ClaimOrder = """
        (SELECT ISNULL(MAX(pp.Priority), 0) FROM PortPriorities pp JOIN Recipes r ON r.PortName = pp.PortName
         WHERE r.Id = Jobs.RecipeId AND pp.Platform = Jobs.Platform) DESC, Jobs.QueuedAt, Jobs.Id
        """;

    /// <summary>The 1-based claim position of every Queued job within its platform.</summary>
    private const string QueueSlotsSql =
        "SELECT Id AS JobId, ROW_NUMBER() OVER (PARTITION BY Platform ORDER BY " + ClaimOrder + ") AS Position FROM Jobs WHERE Status = 'Queued'";

    private sealed class QueueSlot
    {
        public long JobId { get; set; }
        public long Position { get; set; }
    }

    private sealed class JobRow
    {
        public Job Job { get; init; } = null!;
        public int Priority { get; init; }
        public long? Position { get; init; }
    }

    private static void MapQueueViews(RouteGroupBuilder api, RouteGroupBuilder jobs)
    {
        // Default: every platform, Queued and InProgress. scope=mine: claimed by this client; scope=owner: by any client of its owner.
        jobs.MapGet("", async (string? status, string? platform, string? scope, int? page, int? pageSize, HttpContext http, AppDbContext db) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            JobStatus? want = null;
            if (status is not null)
                if (Enum.TryParse<JobStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) want = parsed;
                else return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [$"One of: {string.Join(", ", Enum.GetNames<JobStatus>())}."] });
            var q = db.Jobs.AsQueryable();
            q = want is null ? q.Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.InProgress) : q.Where(j => j.Status == want);
            if (!string.IsNullOrEmpty(platform)) q = q.Where(j => j.Platform == platform);
            switch (scope)
            {
                case null or "" or "all": break;
                case "mine": q = q.Where(j => j.ClaimedByClientId == client.Id); break;
                case "owner": q = q.Where(j => j.ClaimedBy != null && j.ClaimedBy.OwnerId == client.OwnerId); break;
                default: return Results.ValidationProblem(new Dictionary<string, string[]> { ["scope"] = ["all, mine or owner."] });
            }
            // The live queue reads in claim order; finished jobs newest first, like the admin list.
            var live = want is null or JobStatus.Queued or JobStatus.InProgress;
            var result = await Paged<ClientJobDto>.FromAsync(WithQueueInfo(db, q, client.Platform),
                o => live ? o.OrderByDescending(x => x.Priority).ThenBy(x => x.Job.QueuedAt).ThenBy(x => x.Job.Id) : o.OrderByDescending(x => x.Job.Id),
                ToClientJob(client), page, pageSize);
            return Results.Ok(result with { Items = await WithChainsAsync(db, client, result.Items) });
        });

        api.MapGet("/me", async (HttpContext http, AppDbContext db) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var owner = await db.Users.Where(u => u.Id == client.OwnerId).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "";
            var current = await WithQueueInfo(db, db.Jobs.Where(j => j.ClaimedByClientId == client.Id && j.Status == JobStatus.InProgress), client.Platform)
                .OrderByDescending(x => x.Job.ClaimedAt).Select(ToClientJob(client)).Take(1).ToListAsync();
            var clients = await db.BuildClients.Where(c => c.OwnerId == client.OwnerId).OrderBy(c => c.Name)
                .Select(c => new OwnerClientDto(c.Name, c.Platform, c.LastSeenAt, db.Jobs
                    .Where(j => j.ClaimedByClientId == c.Id && j.Status == JobStatus.InProgress)
                    .OrderByDescending(j => j.ClaimedAt).Select(j => (long?)j.Id).FirstOrDefault()))
                .ToListAsync();
            return Results.Ok(new ClientSelfDto(client.Id, client.Name, client.Platform, owner,
                (await WithChainsAsync(db, client, current)).FirstOrDefault(), clients));
        });
    }

    /// <summary>Adds each job's port priority and, for Queued jobs on <paramref name="platform"/>, its claim position.</summary>
    private static IQueryable<JobRow> WithQueueInfo(AppDbContext db, IQueryable<Job> jobs, string platform) =>
        from j in jobs
        join s in db.Database.SqlQueryRaw<QueueSlot>(QueueSlotsSql) on j.Id equals s.JobId into slots
        from s in slots.DefaultIfEmpty()
        select new JobRow
        {
            Job = j,
            Priority = db.PortPriorities.Where(pp => pp.Platform == j.Platform && pp.PortName == j.Recipe!.PortName).Select(pp => pp.Priority).FirstOrDefault(),
            Position = j.Platform == platform ? (long?)s.Position : null,
        };

    private static Expression<Func<JobRow, ClientJobDto>> ToClientJob(BuildClient client)
    {
        var (id, ownerId) = (client.Id, client.OwnerId);
        return x => new ClientJobDto(x.Job.Id, x.Job.Recipe!.PortName, x.Job.Recipe.Version, x.Job.Platform, x.Job.Status, x.Job.QueuedAt,
            x.Job.ClaimedAt, x.Job.LeaseExpiresAt, x.Job.CompletedAt, x.Job.Attempts, x.Priority, x.Job.ClaimedBy == null ? null : x.Job.ClaimedBy.Name,
            x.Job.ClaimedByClientId == id, x.Job.ClaimedByClientId != id && x.Job.ClaimedBy != null && x.Job.ClaimedBy.OwnerId == ownerId,
            x.Position, x.Job.FailureSummary, new List<string>(), 0, false, false, null);
    }

    /// <summary>Most ports listed in <see cref="ClientJobDto.WaitingOn"/>.</summary>
    public const int WaitingOnShown = 10;

    /// <summary>Fills the dependency-tree fields of <paramref name="jobs"/> (one page) from the stored edges.</summary>
    private static async Task<List<ClientJobDto>> WithChainsAsync(AppDbContext db, BuildClient client, List<ClientJobDto> jobs)
    {
        if (jobs.Count == 0) return jobs;
        var current = await db.Jobs.Where(j => j.Status == JobStatus.InProgress && j.ClaimedBy != null && j.ClaimedBy.OwnerId == client.OwnerId)
            .Select(j => new { j.Id, j.Recipe!.PortName, j.Platform, Mine = j.ClaimedByClientId == client.Id }).ToListAsync();
        var graph = await DependencyGraph.LoadAsync(db, jobs.Select(j => j.Platform).Concat(current.Select(c => c.Platform)).Distinct().ToList());

        bool Linked(ClientJobDto j, string platform, string port) => j.Platform == platform && j.PortName != port
            && (graph.Reach(j.Platform, j.PortName).Contains(port) || graph.Reach(platform, port).Contains(j.PortName));

        return jobs.Select(j =>
        {
            var waiting = graph.Reach(j.Platform, j.PortName).Where(p => graph.Active(j.Platform, p)).ToList();
            return j with
            {
                WaitingOn = waiting.Take(WaitingOnShown).ToList(),
                WaitingOnCount = waiting.Count,
                InMyChain = current.Any(c => c.Mine && c.Id != j.Id && Linked(j, c.Platform, c.PortName)),
                InOwnerChain = current.Any(c => !c.Mine && c.Id != j.Id && Linked(j, c.Platform, c.PortName)),
                Unblocks = j.Mine && j.Status == JobStatus.InProgress
                    ? graph.Reach(j.Platform, j.PortName, reverse: true).Sum(p => graph.Queued(j.Platform, p)) : null,
            };
        }).ToList();
    }

    /// <summary>
    /// The <see cref="PortDependency"/> edges of the platforms a request needs, walked in memory, and which ports are queued or
    /// building there. The real arm64 tree has ~137k edges: a walk in SQL took seconds, so each platform's edges are loaded once
    /// and cached until their count or checksum changes (a recomputation replaces them), which one ~40 ms aggregate checks.
    /// </summary>
    private sealed class DependencyGraph
    {
        private sealed record Edges(long Count, int Checksum, Dictionary<string, string[]> Needs, Dictionary<string, string[]> NeededBy);
        private sealed record Stamp(long Count, int Checksum);
        private static readonly ConcurrentDictionary<string, Edges> Cache = new();

        private readonly Dictionary<string, Edges> _edges = [];
        private readonly Dictionary<(string Platform, string Port), int> _queued = [];
        private readonly Dictionary<(string Platform, string Port, bool Reverse), List<string>> _reach = [];

        public static async Task<DependencyGraph> LoadAsync(AppDbContext db, List<string> platforms)
        {
            var g = new DependencyGraph();
            foreach (var p in platforms) g._edges[p] = await EdgesAsync(db, p);
            var active = await db.Jobs.Where(j => platforms.Contains(j.Platform) && (j.Status == JobStatus.Queued || j.Status == JobStatus.InProgress))
                .GroupBy(j => new { j.Platform, j.Recipe!.PortName })
                .Select(x => new { x.Key.Platform, x.Key.PortName, Queued = x.Count(j => j.Status == JobStatus.Queued) }).ToListAsync();
            foreach (var a in active) g._queued[(a.Platform, a.PortName)] = a.Queued;
            return g;
        }

        private static async Task<Edges> EdgesAsync(AppDbContext db, string platform)
        {
            var stamp = (await db.Database.SqlQuery<Stamp>($"""
                SELECT COUNT_BIG(*) AS Count, ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(PortName, DependsOn)), 0) AS Checksum
                FROM PortDependencies WHERE Platform = {platform}
                """).ToListAsync())[0];
            if (Cache.TryGetValue(platform, out var cached) && cached.Count == stamp.Count && cached.Checksum == stamp.Checksum) return cached;
            var rows = await db.PortDependencies.AsNoTracking().Where(d => d.Platform == platform).Select(d => new { d.PortName, d.DependsOn }).ToListAsync();
            var names = new Dictionary<string, string>(); // one string per port name
            string Name(string n) => names.TryGetValue(n, out var s) ? s : names[n] = n;
            var edges = new Edges(stamp.Count, stamp.Checksum,
                rows.GroupBy(r => Name(r.PortName)).ToDictionary(x => x.Key, x => x.Select(r => Name(r.DependsOn)).ToArray()),
                rows.GroupBy(r => Name(r.DependsOn)).ToDictionary(x => x.Key, x => x.Select(r => Name(r.PortName)).ToArray()));
            return Cache[platform] = edges;
        }

        /// <summary>Has a Queued or InProgress job on the platform.</summary>
        public bool Active(string platform, string port) => _queued.ContainsKey((platform, port));

        /// <summary>Queued jobs of the port on the platform.</summary>
        public int Queued(string platform, string port) => _queued.GetValueOrDefault((platform, port));

        /// <summary>
        /// The ports <paramref name="port"/> needs directly or transitively (with <paramref name="reverse"/>: the ports that need it),
        /// nearest first, without the port itself. A walk visits each port once, so it ends on any graph, cycles included.
        /// </summary>
        public List<string> Reach(string platform, string port, bool reverse = false)
        {
            if (_reach.TryGetValue((platform, port, reverse), out var known)) return known;
            var edges = reverse ? _edges[platform].NeededBy : _edges[platform].Needs;
            var seen = new HashSet<string> { port };
            var order = new List<string>();
            for (var i = -1; i < order.Count; i++) // order doubles as the BFS queue; -1 is the start port
                foreach (var n in edges.GetValueOrDefault(i < 0 ? port : order[i]) ?? [])
                    if (seen.Add(n)) order.Add(n);
            return _reach[(platform, port, reverse)] = order;
        }
    }
}
