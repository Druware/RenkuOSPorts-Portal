using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;

namespace RenkuPorts.Server.Services;

/// <summary>A published package that may go into a platform's index.</summary>
public record IndexCandidate(string FileName, string Sha256, string BlobName);

/// <summary>A package left out of the index, and why.</summary>
public record ExcludedPackage(string FileName, string Sha256, string Reason);

/// <summary>
/// The outcome of the last index build of a platform, stored next to the index at <c>{platform}/index/state.json</c>.
/// <see cref="Inputs"/> (<c>fileName:sha256</c>) and <see cref="RepoInfo"/> say what the index was built from, so an
/// unchanged input set is not rebuilt and the next build only has to fetch and check packages that are new.
/// </summary>
public record RepoIndexState(DateTime? BuiltAt, string? RepoInfo, List<string> Inputs, List<IndexCandidate> Packages, List<ExcludedPackage> Excluded, string? Error);

/// <summary>Picks the packages a platform's index is built from.</summary>
public static partial class RepoIndexInput
{
    /// <summary>
    /// The newest version (then revision) of each package name, by <see cref="VersionComparer"/>. <c>_debuginfo</c> and
    /// <c>_source</c> packages are left out silently; file names that are not <c>name-version-revision-arch.hpkg</c> are rejected.
    /// </summary>
    public static (List<IndexCandidate> Selected, List<ExcludedPackage> Rejected) Select(IEnumerable<IndexCandidate> published)
    {
        var rejected = new List<ExcludedPackage>();
        var parsed = new List<(IndexCandidate Pkg, string Name, string Version, string Revision)>();
        foreach (var p in published)
        {
            var m = FileNameParts().Match(p.FileName);
            if (!m.Success) { rejected.Add(new(p.FileName, p.Sha256, "file name is not <name>-<version>-<revision>-<arch>.hpkg")); continue; }
            var name = m.Groups["name"].Value;
            if (name.EndsWith("_debuginfo") || name.EndsWith("_source")) continue;
            parsed.Add((p, name, m.Groups["version"].Value, m.Groups["revision"].Value));
        }
        var selected = parsed.GroupBy(x => x.Name)
            .Select(g => g.OrderByDescending(x => x.Version, VersionComparer.Instance)
                .ThenByDescending(x => x.Revision, VersionComparer.Instance)
                .ThenBy(x => x.Pkg.FileName, StringComparer.Ordinal).First().Pkg)
            .OrderBy(p => p.FileName, StringComparer.Ordinal).ToList();
        return (selected, rejected);
    }

    // Haiku package names and versions contain no "-".
    [GeneratedRegex(@"^(?<name>[^-]+)-(?<version>[^-]+)-(?<revision>[^-]+)-(?<arch>[^-]+)\.hpkg$")]
    private static partial Regex FileNameParts();
}

/// <summary>Runs Haiku's <c>package</c> and <c>package_repo</c> host tools (Linux binaries, with their <c>*_build.so</c> libraries in the same directory).</summary>
public sealed class HaikuTools(string dir)
{
    public string Dir { get; } = dir;
    public bool Available => File.Exists(Path.Combine(Dir, "package_repo")) && File.Exists(Path.Combine(Dir, "package"));

    /// <summary>Null if the package can go into a repository with this vendor and architecture, else the reason it cannot.</summary>
    /// <remarks>Checks what package_repo would reject (vendor, architecture) up front, so one bad upload cannot fail the whole build;
    /// the canonical file name must match because pkgman downloads <c>packages/&lt;canonical name&gt;</c>.</remarks>
    public async Task<string?> CheckPackageAsync(string path, string vendor, string architecture, CancellationToken ct)
    {
        var (code, output, error) = await RunAsync("package", ["list", "-i", path], null, ct);
        if (code != 0) return $"not a valid package: {FirstLine(error, output)}";
        string? Attr(string key) => output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + ": "))?[(key.Length + 2)..];
        if (!string.Equals(Attr("vendor"), vendor, StringComparison.OrdinalIgnoreCase)) return $"vendor is '{Attr("vendor")}', the repository's is '{vendor}'";
        if (Attr("architecture") is not { } arch || (arch != architecture && arch != "any" && arch != "source"))
            return $"architecture is '{Attr("architecture")}', expected {architecture} or any";
        (code, output, error) = await RunAsync("package", ["info", "-f", "%fileName%", path], null, ct);
        if (code != 0) return $"package info failed: {FirstLine(error, output)}";
        if (output.Trim() != Path.GetFileName(path)) return $"file name differs from the package's canonical name '{output.Trim()}'";
        return null;
    }

    /// <summary><c>package_repo create</c>: writes <c>{repoDir}/repo</c> from <c>{repoDir}/repo.info</c> and the given package files.</summary>
    public Task CreateAsync(string repoDir, IEnumerable<string> packages, CancellationToken ct) =>
        CheckAsync("package_repo", ["create", "-q", Path.Combine(repoDir, "repo.info"), .. packages], null, ct);

    /// <summary>
    /// <c>package_repo update -t</c>: writes <c>{repoDir}/repo</c> (from <c>{repoDir}/repo.info</c>) holding the packages
    /// named in <paramref name="listFile"/>. Names found in <paramref name="oldRepo"/> keep its entries without their files;
    /// the others are read from <paramref name="packageDir"/>.
    /// </summary>
    public Task UpdateAsync(string oldRepo, string repoDir, string listFile, string packageDir, CancellationToken ct) =>
        CheckAsync("package_repo", ["update", "-q", "-t", "-C", packageDir, oldRepo, Path.Combine(repoDir, "repo"), listFile], null, ct);

    /// <summary><c>package_repo list -f</c>: the canonical file names in a repository.</summary>
    public async Task<List<string>> ListAsync(string repo, CancellationToken ct)
    {
        var output = await CheckAsync("package_repo", ["list", "-f", repo], null, ct);
        return output.Split('\n').SkipWhile(l => l.Trim() != "packages:").Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    /// <summary><c>package_repo list -f -v</c>: the repository info, then each package's attributes followed by its canonical file name.</summary>
    public Task<string> ListVerboseAsync(string repo, CancellationToken ct) => CheckAsync("package_repo", ["list", "-f", "-v", repo], null, ct);

    private async Task<string> CheckAsync(string tool, string[] args, string? cwd, CancellationToken ct)
    {
        var (code, output, error) = await RunAsync(tool, args, cwd, ct);
        if (code != 0) throw new InvalidOperationException($"{tool} {args[0]} failed ({code}): {FirstLine(error, output)}");
        return output;
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(string tool, string[] args, string? cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Path.Combine(Dir, tool)) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? "" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LD_LIBRARY_PATH"] = Dir;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { p.Kill(true); throw; }
        return (p.ExitCode, await output, await error);
    }

    private static string FirstLine(string error, string output) =>
        (error.Trim().Length > 0 ? error : output).Trim().Split('\n')[0] is { Length: > 0 } l ? l : "no output";
}

/// <summary>
/// Builds the pkgman repository index (<c>repo</c>, <c>repo.info</c>, <c>repo.sha256</c>) of each platform into blob
/// <c>{platform}/index/</c>. A succeeded job marks its platform dirty; the platform is rebuilt once no new job succeeded for
/// <c>RepoIndex:DebounceSeconds</c>. Every platform is checked once at startup (cheap when nothing changed). A lease on
/// <c>{platform}/index/lock</c> keeps two instances from building the same platform at once.
/// </summary>
public sealed class RepoIndexer(IServiceScopeFactory scopes, BlobContainerClient blobs, IConfiguration cfg, ILogger<RepoIndexer> log) : BackgroundService
{
    public HaikuTools Tools { get; } = new(cfg["RepoIndex:ToolsPath"] ?? "/opt/haiku-tools");
    /// <summary>The repositories' vendor; package_repo accepts only packages with this vendor.</summary>
    public string Vendor => cfg["RepoIndex:Vendor"] ?? "Haiku Project";
    public string DisabledReason => $"Repository indexing is disabled: package_repo and package not found in {Tools.Dir} (RepoIndex:ToolsPath).";

    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(cfg.GetValue("RepoIndex:DebounceSeconds", 60));
    private readonly ConcurrentDictionary<string, (DateTime Due, bool Force)> _dirty = new();

    public static string IndexBlob(string platform, string file) => $"{platform}/index/{file}";

    /// <summary>Schedules a rebuild after the debounce; a later call pushes it back again.</summary>
    public void MarkDirty(string platform) =>
        _dirty.AddOrUpdate(platform, _ => (DateTime.UtcNow + _debounce, false), (_, d) => (DateTime.UtcNow + _debounce, d.Force));

    /// <summary>Schedules a full rebuild (every package fetched and checked again) right away.</summary>
    public void RequestReindex(string platform) => _dirty[platform] = (DateTime.UtcNow, true);

    /// <summary>The stored state of a platform's index, or null before its first build.</summary>
    public async Task<RepoIndexState?> StateAsync(string platform, CancellationToken ct = default)
    {
        try
        {
            var data = await blobs.GetBlobClient(IndexBlob(platform, "state.json")).DownloadContentAsync(ct);
            return data.Value.Content.ToObjectFromJson<RepoIndexState>(JsonOpts);
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!Tools.Available) { log.LogWarning("{Reason}", DisabledReason); return; }
        foreach (var p in Validate.Platforms) _dirty.TryAdd(p, (DateTime.UtcNow, false));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            foreach (var (platform, d) in _dirty)
            {
                if (d.Due > DateTime.UtcNow || !_dirty.TryRemove(new(platform, d))) continue;
                try
                {
                    if (!await BuildAsync(platform, d.Force, stop))
                        _dirty.TryAdd(platform, (DateTime.UtcNow + _debounce, d.Force)); // another instance holds the lock: try again later
                }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    log.LogError(e, "Building the {Platform} repository index failed", platform);
                }
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>Rebuilds one platform's index if its inputs changed (always with <paramref name="force"/>). False if another instance holds the lock.</summary>
    public async Task<bool> BuildAsync(string platform, bool force, CancellationToken ct)
    {
        List<IndexCandidate> published;
        using (var scope = scopes.CreateScope())
            published = await PublicEndpoints.Published(scope.ServiceProvider.GetRequiredService<AppDbContext>())
                .Where(p => p.Platform == platform).Select(p => new IndexCandidate(p.FileName, p.Sha256, p.BlobName)).ToListAsync(ct);

        var lockBlob = blobs.GetBlobClient(IndexBlob(platform, "lock"));
        try { await lockBlob.UploadAsync(BinaryData.Empty, new BlobUploadOptions { Conditions = new() { IfNoneMatch = ETag.All } }, ct); }
        catch (RequestFailedException e) when (e.Status is 409 or 412) { }
        var lease = lockBlob.GetBlobLeaseClient();
        try { await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct); }
        catch (RequestFailedException e) when (e.Status == 409) { return false; }

        using var renewing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew = RenewAsync(lease, renewing.Token);
        var prev = await StateAsync(platform, ct);
        try
        {
            var (selected, rejected) = RepoIndexInput.Select(published);
            var inputs = selected.Concat(rejected.Select(r => new IndexCandidate(r.FileName, r.Sha256, ""))).Select(p => $"{p.FileName}:{p.Sha256}").Order().ToList();
            var repoInfo = RepoInfo(platform);
            if (!force && prev is { Error: null } && prev.RepoInfo == repoInfo && prev.Inputs.SequenceEqual(inputs)) return true;

            var reuse = !force && prev is { Error: null, BuiltAt: not null } && prev.RepoInfo == repoInfo;
            RepoIndexState state;
            try { state = await BuildLockedAsync(platform, selected, rejected, inputs, repoInfo, reuse ? prev : null, ct); }
            catch (Exception e) when (reuse && !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Incremental {Platform} index update failed; rebuilding from all packages", platform);
                state = await BuildLockedAsync(platform, selected, rejected, inputs, repoInfo, null, ct);
            }
            await SaveStateAsync(platform, state, ct);
            log.LogInformation("{Platform} repository index: {Count} packages, {Excluded} excluded", platform, state.Packages.Count, state.Excluded.Count);
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Keep the last good index and its listing; only record the error.
            await SaveStateAsync(platform, (prev ?? new(null, null, [], [], [], null)) with { Error = e.Message }, CancellationToken.None);
            throw;
        }
        finally
        {
            await renewing.CancelAsync();
            await renew;
            try { await lease.ReleaseAsync(cancellationToken: CancellationToken.None); } catch (RequestFailedException) { } // it expires anyway
        }
    }

    /// <summary>Downloads the packages <paramref name="prev"/> does not already cover, checks them, runs package_repo and uploads the index.</summary>
    private async Task<RepoIndexState> BuildLockedAsync(string platform, List<IndexCandidate> selected, List<ExcludedPackage> rejected,
        List<string> inputs, string repoInfo, RepoIndexState? prev, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory($"repoindex-{platform}-").FullName;
        try
        {
            var pkgDir = Directory.CreateDirectory(Path.Combine(work, "packages")).FullName;
            var kept = new List<IndexCandidate>();
            var added = new List<IndexCandidate>();
            var excluded = new List<ExcludedPackage>(rejected);
            foreach (var p in selected)
            {
                if (prev?.Packages.Contains(p) == true) { kept.Add(p); continue; }
                if (prev?.Excluded.FirstOrDefault(x => x.FileName == p.FileName && x.Sha256 == p.Sha256) is { } was) { excluded.Add(was); continue; }
                var path = Path.Combine(pkgDir, p.FileName);
                await blobs.GetBlobClient(p.BlobName).DownloadToAsync(path, ct);
                if (await Tools.CheckPackageAsync(path, Vendor, platform, ct) is { } reason)
                {
                    File.Delete(path);
                    excluded.Add(new(p.FileName, p.Sha256, reason));
                }
                else added.Add(p);
            }

            var packages = kept.Concat(added).OrderBy(p => p.FileName, StringComparer.Ordinal).ToList();
            excluded = excluded.OrderBy(x => x.FileName, StringComparer.Ordinal).ToList();
            if (packages.Count == 0)
            {
                // package_repo cannot write an empty repository; serve none (404) until a package qualifies.
                foreach (var f in new[] { "repo.sha256", "repo", "repo.info" }) await blobs.GetBlobClient(IndexBlob(platform, f)).DeleteIfExistsAsync(cancellationToken: ct);
                return new(null, repoInfo, inputs, [], excluded, null);
            }

            var repo = Path.Combine(work, "repo");
            await File.WriteAllTextAsync(Path.Combine(work, "repo.info"), repoInfo, ct);
            if (prev is not null)
            {
                var oldRepo = Path.Combine(work, "old.repo");
                await blobs.GetBlobClient(IndexBlob(platform, "repo")).DownloadToAsync(oldRepo, ct);
                var list = Path.Combine(work, "packages.list");
                await File.WriteAllLinesAsync(list, kept.Concat(added).Select(p => p.FileName), ct);
                await Tools.UpdateAsync(oldRepo, work, list, pkgDir, ct);
            }
            else await Tools.CreateAsync(work, added.Select(p => Path.Combine(pkgDir, p.FileName)), ct);

            // repo before repo.sha256: a reader sees a mismatched pair only between these two uploads.
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(File.OpenRead(repo), ct));
            await Upload(IndexBlob(platform, "repo.info"), BinaryData.FromString(repoInfo), "text/plain; charset=utf-8", ct);
            await Upload(IndexBlob(platform, "repo"), BinaryData.FromBytes(await File.ReadAllBytesAsync(repo, ct)), "application/octet-stream", ct);
            await Upload(IndexBlob(platform, "repo.sha256"), BinaryData.FromString(sha + "\n"), "text/plain", ct);
            return new(DateTime.UtcNow, repoInfo, inputs, packages, excluded, null);
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    /// <summary>The repo.info text for a platform, from the <c>RepoIndex</c> settings.</summary>
    public string RepoInfo(string platform)
    {
        var baseUrl = (cfg["RepoIndex:PublicBaseUrl"] ?? "http://localhost:8080").TrimEnd('/');
        return $"""
            name {cfg["RepoIndex:Name"] ?? "RenkuOSPorts"}
            vendor "{Vendor}"
            summary "{cfg["RepoIndex:Summary"] ?? $"RenkuOSPorts packages for {platform}"}"
            priority {cfg.GetValue("RepoIndex:Priority", 1)}
            baseurl {baseUrl}/repo/{platform}
            identifier {cfg["RepoIndex:IdentifierPrefix"] ?? "tag:renkuos.org,2026:repositories/renkuosports"}/{platform}
            architecture {platform}

            """;
    }

    private Task Upload(string name, BinaryData data, string contentType, CancellationToken ct) =>
        blobs.GetBlobClient(name).UploadAsync(data, new BlobUploadOptions { HttpHeaders = new() { ContentType = contentType } }, ct);

    private Task SaveStateAsync(string platform, RepoIndexState state, CancellationToken ct) =>
        Upload(IndexBlob(platform, "state.json"), BinaryData.FromObjectAsJson(state, JsonOpts), "application/json", ct);

    private static async Task RenewAsync(BlobLeaseClient lease, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await lease.RenewAsync(cancellationToken: ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
