using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;

namespace RenkuPorts.Server.Services;

/// <summary>A port the auto-update did not queue for a platform, and why.</summary>
public record SkippedPort(string Port, string Platform, string Reason);

/// <summary>What one auto-update run did. <see cref="Note"/> explains a run that queued nothing on purpose (first run, no new commits).</summary>
public record AutoUpdateResult(DateTime FinishedAt, string? FromCommit, string? ToCommit, List<string> ChangedPorts, int Queued,
    List<SkippedPort> Skipped, string? Note, string? Error);

/// <summary>
/// Queues builds of the ports whose recipes changed in the ports tree, after RenkuOSPorts' <c>update-packages.sh</c>: update the
/// tree as the recipe sync does, diff from the last processed commit to HEAD, map the changed files to ports, and queue a port's
/// newest version on each configured platform where we already publish that port and the version and REVISION are not published
/// yet. Runs every <see cref="AutoUpdateSettings.IntervalHours"/> while enabled, or on demand. One atomic UPDATE on the settings
/// row claims a run, so only one instance runs it.
/// </summary>
public sealed partial class AutoUpdater(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<AutoUpdater> log) : BackgroundService
{
    /// <summary>A run claimed longer ago than this is assumed dead (its server stopped) and may be claimed again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static AutoUpdateResult? ReadResult(AutoUpdateSettings s) =>
        s.LastResultJson is null ? null : JsonSerializer.Deserialize<AutoUpdateResult>(s.LastResultJson, JsonOpts);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!cfg.GetValue("AutoUpdate:Schedule", true)) return; // tests drive runs themselves
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await RunIfDueAsync(stop); }
            catch (Exception e) when (!stop.IsCancellationRequested) { log.LogError(e, "Auto-update check failed"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>Runs an update if enabled and the interval has passed since the last run. Null if nothing ran.</summary>
    public Task<AutoUpdateResult?> RunIfDueAsync(CancellationToken ct = default) => RunAsync(scheduled: true, ct);

    /// <summary>Runs an update now, enabled or not. Null if another run holds the claim.</summary>
    public Task<AutoUpdateResult?> RunNowAsync(CancellationToken ct = default) => RunAsync(scheduled: false, ct);

    private async Task<AutoUpdateResult?> RunAsync(bool scheduled, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var stale = now - StaleAfter;
        var claim = db.AutoUpdateSettings.Where(s => s.Id == AutoUpdateSettings.SingletonId && (s.RunningSince == null || s.RunningSince < stale));
        if (scheduled) claim = claim.Where(s => s.Enabled && (s.LastRunAt == null || s.LastRunAt.Value.AddHours(s.IntervalHours) <= now));
        if (await claim.ExecuteUpdateAsync(s => s.SetProperty(x => x.RunningSince, now), ct) == 0) return null;

        var settings = await db.AutoUpdateSettings.AsNoTracking().SingleAsync(s => s.Id == AutoUpdateSettings.SingletonId, ct);
        AutoUpdateResult result;
        string? commit = settings.LastCommit;
        try
        {
            (result, commit) = await UpdateAsync(db, settings, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Keep the old commit, so the next run covers these changes again.
            log.LogError(e, "Auto-update failed");
            result = new(DateTime.UtcNow, settings.LastCommit, null, [], 0, [], null, e.Message);
        }
        finally
        {
            if (ct.IsCancellationRequested)
                await db.AutoUpdateSettings.Where(s => s.Id == AutoUpdateSettings.SingletonId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RunningSince, (DateTime?)null), CancellationToken.None);
        }
        var json = JsonSerializer.Serialize(result, JsonOpts);
        await db.AutoUpdateSettings.Where(s => s.Id == AutoUpdateSettings.SingletonId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RunningSince, (DateTime?)null).SetProperty(x => x.LastRunAt, result.FinishedAt)
            .SetProperty(x => x.LastCommit, commit).SetProperty(x => x.LastResultJson, json), CancellationToken.None);
        log.LogInformation("Auto-update {From}..{To}: {Changed} changed ports, {Queued} jobs queued, {Skipped} skipped{Error}",
            result.FromCommit, result.ToCommit, result.ChangedPorts.Count, result.Queued, result.Skipped.Count, result.Error is null ? "" : $"; error: {result.Error}");
        return result;
    }

    /// <summary>Updates the tree and recipes, then queues the changed ports. Returns the result and the commit to record.</summary>
    private async Task<(AutoUpdateResult, string)> UpdateAsync(AppDbContext db, AutoUpdateSettings settings, CancellationToken ct)
    {
        var root = cfg["PortsTree:Path"];
        if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("PortsTree:Path is not set");
        string head;
        await AdminEndpoints.SyncLock.WaitAsync(ct);
        try
        {
            // As the recipe sync: fetch with PortsTree:GitUrl, else read the local checkout as it is (the compose mount is read-only).
            if (cfg["PortsTree:GitUrl"] is { Length: > 0 } url) await RecipeScanner.UpdateCheckoutAsync(root, url, cfg["PortsTree:Branch"] ?? "main");
            await RecipeScanner.SyncAsync(db, root, log);
            head = (await RecipeScanner.GitAsync(root, "rev-parse", "HEAD")).Trim();
        }
        finally
        {
            AdminEndpoints.SyncLock.Release();
        }

        var last = settings.LastCommit;
        if (last is null) return (new(DateTime.UtcNow, null, head, [], 0, [], $"First run: recorded {head[..Math.Min(12, head.Length)]}; nothing queued. Changes after it are queued from the next run.", null), head);
        if (last == head) return (new(DateTime.UtcNow, last, head, [], 0, [], "No new commits.", null), head);

        var files = (await RecipeScanner.GitAsync(root, "diff", "--name-only", "--no-renames", "--diff-filter=ACMRT", last, head))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var recipes = await db.Recipes.AsNoTracking().ToListAsync(ct);
        var ports = PortsForFiles(files, recipes.Select(r => r.RecipePath));

        var platforms = settings.Platforms.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(Validate.IsPlatform).ToList();
        var skipped = new List<SkippedPort>();
        var queued = 0;
        foreach (var port in ports)
        {
            var newest = recipes.Where(r => r.PortName == port).OrderByDescending(r => r.Version, VersionComparer.Instance).FirstOrDefault();
            if (newest is null) { skipped.AddRange(platforms.Select(p => new SkippedPort(port, p, "no recipe for this port any more"))); continue; }
            var revision = ReadRevision(Path.Combine(root, newest.RecipePath));
            if (revision is null) { skipped.AddRange(platforms.Select(p => new SkippedPort(port, p, $"{newest.RecipePath} has no REVISION"))); continue; }
            var prefix = $"{port}-{newest.Version}-{revision}-";
            var targets = new List<string>();
            foreach (var platform in platforms)
            {
                var ours = PublicEndpoints.Published(db).Where(p => p.Platform == platform && p.PortName == port);
                if (!await ours.AnyAsync(ct)) skipped.Add(new(port, platform, $"not built on {platform} yet"));
                else if (await ours.AnyAsync(p => p.FileName.StartsWith(prefix), ct))
                    skipped.Add(new(port, platform, $"{port}-{newest.Version}-{revision} already published; bump REVISION to rebuild"));
                else targets.Add(platform);
            }
            if (targets.Count == 0) continue;
            var jobs = await AdminEndpoints.QueueAsync(db, newest, targets, null);
            queued += jobs.Count;
            skipped.AddRange(targets.Except(jobs.Select(j => j.Platform)).Select(p => new SkippedPort(port, p, "already queued or building")));
        }
        return (new(DateTime.UtcNow, last, head, ports, queued, skipped, null, null), head);
    }

    /// <summary>
    /// The ports changed files belong to: a changed <c>&lt;category&gt;/&lt;dir&gt;/&lt;port&gt;-&lt;version&gt;.recipe</c> gives its port;
    /// a changed file under <c>&lt;category&gt;/&lt;dir&gt;/patches/</c> or <c>additional-files/</c> gives every port with a recipe in that directory.
    /// </summary>
    public static List<string> PortsForFiles(IEnumerable<string> files, IEnumerable<string> recipePaths)
    {
        static string? PortOf(string recipeFile)
        {
            var stem = Path.GetFileNameWithoutExtension(recipeFile);
            var dash = stem.LastIndexOf('-');
            return dash > 0 ? stem[..dash] : null;
        }
        var byDir = recipePaths.Select(r => r.Split('/')).Where(p => p.Length == 3)
            .ToLookup(p => $"{p[0]}/{p[1]}", p => PortOf(p[2]));
        var ports = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var parts = f.Split('/');
            if (parts.Length == 3 && parts[2].EndsWith(".recipe") && PortOf(parts[2]) is { } port) ports.Add(port);
            else if (parts.Length >= 4 && parts[2] is "patches" or "additional-files")
                foreach (var p in byDir[$"{parts[0]}/{parts[1]}"]) if (p is not null) ports.Add(p);
        }
        return [.. ports];
    }

    /// <summary>The recipe's <c>REVISION="n"</c>, or null if it has none.</summary>
    public static string? ReadRevision(string recipeFile) =>
        File.Exists(recipeFile) && File.ReadLines(recipeFile).Select(l => Revision().Match(l)).FirstOrDefault(m => m.Success) is { } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^\s*REVISION\s*=\s*[""']?([0-9]+)[""']?\s*(#.*)?$")]
    private static partial Regex Revision();
}
