using System.Collections.Concurrent;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;

namespace RenkuPorts.Server.Services;

/// <summary>A package listed in a source repository, with the SHA-256 the repository records for its file (if any).</summary>
public record SourcePackage(string FileName, string? Checksum);

/// <summary>What <c>package_repo list -f -v</c> says about a repository.</summary>
public record RepoListing(string? Architecture, string? Vendor, List<SourcePackage> Packages)
{
    /// <summary>
    /// Parses <c>package_repo list -f -v</c> output: a <c>repository-info:</c> block, then per package a <c>package-attributes:</c>
    /// block (one of whose lines is <c>checksum: &lt;sha256 of the file&gt;</c>) ended by <c>filename: &lt;canonical name&gt;</c>.
    /// </summary>
    public static RepoListing Parse(string output)
    {
        string? arch = null, vendor = null, checksum = null;
        var packages = new List<SourcePackage>();
        var inRepoInfo = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "repository-info:") inRepoInfo = true;
            else if (line is "packages:" or "package-attributes:") { inRepoInfo = false; if (line != "packages:") checksum = null; }
            else if (inRepoInfo && line.StartsWith("architecture: ")) arch = line["architecture: ".Length..];
            else if (inRepoInfo && line.StartsWith("vendor: ")) vendor = line["vendor: ".Length..];
            else if (!inRepoInfo && line.StartsWith("checksum: ")) checksum = line["checksum: ".Length..].ToLowerInvariant();
            else if (raw.StartsWith("filename: ")) { packages.Add(new(line["filename: ".Length..], checksum)); checksum = null; }
        }
        return new(arch, vendor, packages);
    }
}

/// <summary>
/// Copies the packages a source pkgman repository has and this platform lacks (by file name). Runs in the background on the
/// instance that started it; progress, and the cancel request from any instance, go through the <see cref="Import"/> row.
/// </summary>
public sealed class RepoImporter(IServiceScopeFactory scopes, BlobContainerClient blobs, RepoIndexer indexer, PriorityComputer priorities, IHostApplicationLifetime life, ILogger<RepoImporter> log)
{
    /// <summary>A Running import whose row was not updated for this long was interrupted (its instance stopped).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    // Redirects: SocketsHttpHandler follows only http(s) targets and never https -> http.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>How hard an import may press on its source.</summary>
    /// <param name="Parallelism">Downloads at once.</param>
    /// <param name="DelayMinSeconds">Shortest random pause before each package download.</param>
    /// <param name="DelayMaxSeconds">Longest random pause before each package download.</param>
    /// <param name="MaxKilobytesPerSecond">Limit for all downloads of the import together, in KiB/s; 0 for none.</param>
    public record Options(int Parallelism, double DelayMinSeconds, double DelayMaxSeconds, int MaxKilobytesPerSecond);

    /// <summary>Paces the downloads of one import, shared by its workers, to a byte rate. Never grants credit for idle time.</summary>
    public sealed class Throttle(int bytesPerSecond)
    {
        private readonly Lock _gate = new();
        private DateTime _next = DateTime.MinValue;

        /// <summary>Waits until <paramref name="bytes"/> more fit within the rate.</summary>
        public async Task WaitAsync(int bytes, CancellationToken ct)
        {
            TimeSpan wait;
            lock (_gate) wait = Reserve(ref _next, DateTime.UtcNow, bytes, bytesPerSecond);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }

        /// <summary>Books <paramref name="bytes"/> after the last booking (or <paramref name="now"/>, if later), moving <paramref name="next"/>
        /// to the end of the slot. Returns how long to wait for that end.</summary>
        public static TimeSpan Reserve(ref DateTime next, DateTime now, int bytes, int bytesPerSecond)
        {
            next = (next > now ? next : now) + TimeSpan.FromSeconds((double)bytes / bytesPerSecond);
            return next - now;
        }
    }

    /// <summary>Runs the (already saved, Running) import in the background.</summary>
    public void Start(long importId, Options options) => _ = Task.Run(() => RunAsync(importId, options, life.ApplicationStopping));

    private async Task RunAsync(long id, Options options, CancellationToken stopping)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var imp = await db.Imports.SingleAsync(i => i.Id == id, CancellationToken.None);
        var work = Directory.CreateTempSubdirectory($"import-{id}-").FullName;
        var skipped = new ConcurrentQueue<SkippedPackage>();
        var imported = 0;
        var throttle = options.MaxKilobytesPerSecond > 0 ? new Throttle(options.MaxKilobytesPerSecond * 1024) : null;
        log.LogInformation("Import {Id} from {Url}: {Parallelism} at once, {Min}-{Max} s pause per file, {Rate} KiB/s (0 = no limit)",
            id, imp.SourceUrl, options.Parallelism, options.DelayMinSeconds, options.DelayMaxSeconds, options.MaxKilobytesPerSecond);
        try
        {
            var baseUrl = imp.SourceUrl.TrimEnd('/');
            var repo = Path.Combine(work, "repo");
            var repoSha = await DownloadAsync($"{baseUrl}/repo", repo, throttle, stopping);
            if (await GetOptionalAsync($"{baseUrl}/repo.sha256", stopping) is { } expected && !expected.Trim().StartsWith(repoSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The source's repo does not match its repo.sha256 ({repoSha} vs {expected.Trim()}); it may be changing, try again.");
            var listing = RepoListing.Parse(await indexer.Tools.ListVerboseAsync(repo, stopping));
            if (listing.Architecture != imp.Platform)
                throw new InvalidOperationException($"The source repository's architecture is '{listing.Architecture}', not {imp.Platform}.");

            var have = (await db.Packages.Where(p => p.Platform == imp.Platform).Select(p => p.FileName).ToListAsync(stopping)).ToHashSet();
            var missing = listing.Packages.Where(p => !have.Contains(p.FileName)).ToList();
            imp.Total = listing.Packages.Count;
            imp.Missing = missing.Count;
            if (imp.DryRun)
            {
                foreach (var p in missing) skipped.Enqueue(new() { FileName = p.FileName, Reason = "missing (dry run, not imported)" });
                imp.Status = ImportStatus.Completed;
                return;
            }
            imp.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(stopping);

            var recipes = await db.Recipes.GroupBy(r => r.PortName).Select(g => new { g.Key, Updated = g.Max(r => r.RecipeUpdatedAt) })
                .ToDictionaryAsync(r => r.Key, r => r.Updated, stopping);
            var cancel = false;
            using var progressStop = new CancellationTokenSource();
            var progress = Task.Run(async () =>
            {
                while (true)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(3), progressStop.Token); }
                    catch (OperationCanceledException) { return; }
                    cancel |= await db.Imports.Where(i => i.Id == id).Select(i => i.CancelRequested).FirstAsync(CancellationToken.None);
                    Snapshot(imp, imported, skipped);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            });
            try
            {
                await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = stopping }, async (p, ct) =>
                {
                    if (cancel) return; // stop between files; files already downloading finish
                    if (options.DelayMaxSeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(options.DelayMinSeconds + Random.Shared.NextDouble() * (options.DelayMaxSeconds - options.DelayMinSeconds)), ct);
                        if (cancel) return;
                    }
                    if (await ImportOneAsync(imp, p, baseUrl, work, recipes, throttle, ct) is { } reason) skipped.Enqueue(new() { FileName = p.FileName, Reason = reason });
                    else Interlocked.Increment(ref imported);
                });
            }
            finally
            {
                await progressStop.CancelAsync();
                await progress;
            }
            imp.Status = cancel ? ImportStatus.Cancelled : ImportStatus.Completed;
        }
        catch (Exception e)
        {
            log.LogError(e, "Import {Id} from {Url} failed", id, imp.SourceUrl);
            imp.Status = ImportStatus.Failed;
            imp.Error = (stopping.IsCancellationRequested ? "The server stopped during the import; run it again to continue. " : "") + e.Message;
            if (imp.Error.Length > 2000) imp.Error = imp.Error[..2000];
        }
        finally
        {
            Snapshot(imp, imported, skipped);
            imp.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            Directory.Delete(work, true);
            if (imported > 0) { indexer.MarkDirty(imp.Platform); priorities.MarkDirty(); }
        }
    }

    private static void Snapshot(Import imp, int imported, ConcurrentQueue<SkippedPackage> skipped)
    {
        imp.Imported = imported;
        imp.Skipped = skipped.ToList();
        imp.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Downloads, verifies, checks and stores one package. Null on success, else why it was skipped.</summary>
    private async Task<string?> ImportOneAsync(Import imp, SourcePackage p, string baseUrl, string work, Dictionary<string, DateTime> recipes, Throttle? throttle, CancellationToken ct)
    {
        if (!Validate.IsHpkgName(p.FileName)) return "not a plain .hpkg file name";
        var path = Path.Combine(work, p.FileName);
        try
        {
            var sha = await DownloadAsync($"{baseUrl}/packages/{Uri.EscapeDataString(p.FileName)}", path, throttle, ct);
            if (p.Checksum is { } c && c != sha) return $"checksum mismatch: the source repository lists {c}, the file is {sha}";
            if (await indexer.Tools.CheckPackageAsync(path, indexer.Vendor, imp.Platform, ct) is { } reason) return reason;

            var port = PortName(p.FileName, recipes);
            var now = DateTime.UtcNow;
            using var scope = scopes.CreateScope();
            await using var file = File.OpenRead(path);
            var res = await PackageStore.StoreAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), blobs, imp.Platform, p.FileName, sha, file, new Package
            {
                BuildDate = now, UploadedAt = now, RecipeUpdatedAt = recipes.GetValueOrDefault(port, now), PortName = port,
                BuiltByUserId = imp.StartedByUserId, ImportId = imp.Id,
            }, ct);
            return res.Outcome switch
            {
                PackageStore.Outcome.Added => null,
                PackageStore.Outcome.AlreadyPresent => "already present",
                PackageStore.Outcome.Conflict => $"exists with different content (sha256 {res.ExistingSha256})",
                _ => "file changed while it was stored",
            };
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return e is OperationCanceledException ? $"download stalled for {StallTimeout.TotalSeconds:0} s" : $"download failed: {e.Message}";
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The port a package file belongs to: its package name, or that name with <c>_suffix</c> parts removed from the end
    /// (subpackages such as <c>inih_devel</c>) if that is a known recipe; the package name when nothing matches.
    /// </summary>
    public static string PortName(string fileName, IReadOnlyDictionary<string, DateTime> recipes)
    {
        var name = fileName.Split('-')[0];
        for (var n = name; ; n = n[..n.LastIndexOf('_')])
        {
            if (recipes.ContainsKey(n)) return n;
            if (!n.Contains('_')) return name;
        }
    }

    /// <summary>Streams a URL to a file, hashing it; at most <see cref="BuildClientApi.MaxUpload"/> bytes, and no more than
    /// <see cref="StallTimeout"/> without data (time spent waiting on <paramref name="throttle"/> is not a stall). Returns the lowercase hex SHA-256.</summary>
    private static async Task<string> DownloadAsync(string url, string path, Throttle? throttle, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase} from {url}");
        if (res.Content.Headers.ContentLength > BuildClientApi.MaxUpload) throw new IOException($"{url} is larger than 4 GiB");
        await using var body = new HashingStream(await res.Content.ReadAsStreamAsync(stall.Token));
        await using var file = File.Create(path);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await body.ReadAsync(buf, stall.Token)) > 0)
        {
            if (body.BytesRead > BuildClientApi.MaxUpload) throw new IOException($"{url} is larger than 4 GiB");
            await file.WriteAsync(buf.AsMemory(0, n), stall.Token);
            if (throttle is not null)
            {
                stall.CancelAfter(Timeout.InfiniteTimeSpan);
                await throttle.WaitAsync(n, ct);
            }
            stall.CancelAfter(StallTimeout);
        }
        return body.Sha256Hex;
    }

    /// <summary>A small text file, or null on 404.</summary>
    private static async Task<string?> GetOptionalAsync(string url, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var res = await Http.GetAsync(url, stall.Token);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase} from {url}");
        return await res.Content.ReadAsStringAsync(stall.Token);
    }
}
