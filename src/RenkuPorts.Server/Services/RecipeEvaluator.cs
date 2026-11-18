using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace RenkuPorts.Server.Services;

/// <summary>A recipe evaluated for one architecture: its HaikuPorter attributes (<c>PROVIDES</c>, <c>REQUIRES_devel</c>, ...) as bash left them.</summary>
public record EvaluatedRecipe(string PortName, string Version, string RecipePath, IReadOnlyDictionary<string, string> Attrs);

/// <summary>A cached evaluation: valid while the recipe file's SHA-256 is <see cref="Sha256"/>.</summary>
public record EvalCacheEntry(string Sha256, Dictionary<string, string> Attrs);

/// <summary>
/// Evaluates recipes the way HaikuPorter does: bash sources the recipe with the port variables set for a target architecture,
/// then prints the attributes it defined. Ported from RenkuOSPorts' <c>renkuos/bootstrap/arm64/phase2/eval-recipes.py</c> (MIT),
/// keeping its bash script; only the driver moved from Python to C#, so the server image needs bash but not Python.
/// </summary>
public static class RecipeEvaluator
{
    /// <summary>The parts of HaikuPorter's <c>commonRecipeScriptHead</c> that recipes call at the top level.</summary>
    private const string Head = """

        defineDebugInfoPackage() {
        	[ "$1" = --directory ] && shift 2
        	PROVIDES_debuginfo="${1}_debuginfo = $portVersion"
        	REQUIRES_debuginfo="$1 == $portVersion base"
        }
        getPackagePrefix() { echo /packages/x/.self; }
        updateRevisionVariables() { :; }

        """;

    private const string Keys = "ARCHITECTURES|PROVIDES|REQUIRES|BUILD_REQUIRES|BUILD_PREREQUIRES|SECONDARY_ARCHITECTURES|PACKAGE_NAME|DISABLED";

    /// <summary>Prints every attribute variable (names matching KEYS, with any package suffix) as <c>KEY=value</c>, newlines escaped as <c>\n</c>.</summary>
    private const string Tail = """

        for key in $(set | grep -E -o "^(KEYS)(_[0-9a-zA-Z_]+)?=" | cut -d= -f1); do
        	value=${!key}
        	printf '%s=%s\n' "$key" "${value//$'\n'/\\n}"
        done

        """;

    private static readonly string Script = (Head + ". \"$recipe\" > /dev/null 2>&1 || true\n" + Tail).Replace("KEYS", Keys);

    /// <summary>
    /// Evaluates <paramref name="recipes"/> (paths relative to <paramref name="root"/>) for <paramref name="arch"/>, in parallel,
    /// reusing <paramref name="cache"/> entries whose file hash is unchanged. Recipes whose file is gone, or whose evaluation timed
    /// out, are left out of the result. Returns the evaluations and the new cache (only the recipes given) and how many ran bash.
    /// </summary>
    public static async Task<(List<EvaluatedRecipe> Recipes, Dictionary<string, EvalCacheEntry> Cache, int Evaluated)> EvaluateAsync(
        string root, IEnumerable<(string PortName, string Version, string RecipePath)> recipes, string arch,
        IReadOnlyDictionary<string, EvalCacheEntry> cache, ILogger log, CancellationToken ct)
    {
        var results = new ConcurrentBag<EvaluatedRecipe>();
        var newCache = new ConcurrentDictionary<string, EvalCacheEntry>();
        var evaluated = 0;
        await Parallel.ForEachAsync(recipes, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, async (r, ct) =>
        {
            var file = Path.Combine(root, r.RecipePath);
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!cache.TryGetValue(r.RecipePath, out var entry) || entry.Sha256 != sha)
            {
                var attrs = await EvaluateOneAsync(file, r.PortName, r.Version, arch, ct);
                if (attrs is null) { log.LogWarning("Evaluating {Recipe} for {Arch} timed out", r.RecipePath, arch); return; }
                entry = new(sha, attrs);
                Interlocked.Increment(ref evaluated);
            }
            newCache[r.RecipePath] = entry;
            results.Add(new(r.PortName, r.Version, r.RecipePath, entry.Attrs));
        });
        return ([.. results.OrderBy(r => r.RecipePath, StringComparer.Ordinal)], new(newCache), evaluated);
    }

    /// <summary>Sources one recipe with bash for <paramref name="arch"/>; null if it ran longer than 30 s.</summary>
    public static async Task<Dictionary<string, string>?> EvaluateOneAsync(string file, string port, string version, string arch, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(file)!;
        var name = $"{port}-{version}";
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Script);
        psi.Environment.Clear();
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin", ["recipe"] = file,
            ["portName"] = port, ["portBaseName"] = port, ["portVersion"] = version, ["portVersionedName"] = name,
            ["portRevision"] = "1", ["portFullVersion"] = version + "-1", ["portRevisionedName"] = name + "-1",
            ["portDir"] = dir, ["portBaseDir"] = dir,
            ["targetArchitecture"] = arch, ["effectiveTargetArchitecture"] = arch, ["buildArchitecture"] = arch,
            ["secondaryArchSuffix"] = "", ["secondaryArchSubDir"] = "",
            ["effectiveTargetMachineTriple"] = arch + "-unknown-haiku", ["effectiveTargetMachineTripleAsName"] = arch + "_unknown_haiku",
        }) psi.Environment[k] = v;

        using var p = Process.Start(psi)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token); // drained so a chatty recipe cannot block on a full pipe
        try { await p.WaitForExitAsync(timeout.Token); await stderr; }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            if (ct.IsCancellationRequested) throw;
            return null;
        }
        var attrs = new Dictionary<string, string>();
        foreach (var line in (await stdout).Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) attrs[line[..eq]] = line[(eq + 1)..].Replace("\\n", "\n");
        }
        return attrs;
    }
}
