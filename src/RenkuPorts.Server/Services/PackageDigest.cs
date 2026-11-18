using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RenkuPorts.Server.Services;

/// <summary>
/// One entry of a package: <see cref="Record"/> is everything compared besides the bytes (type and permissions, size, symlink
/// target, attribute names, types and sizes), <see cref="Sha256"/> the bytes of a regular file.
/// </summary>
public record ListingEntry(string Path, string Record, string? Sha256, long Size);

/// <summary>
/// What a package's content digest is computed from: <see cref="Metadata"/> (<c>package list -i</c>, time and packager fields
/// dropped) and every entry but the package-root <c>.PackageInfo</c>. <see cref="Raw"/>: the file could not be read as a package (or the tools are missing), so it is compared as
/// one opaque file named after the package.
/// </summary>
public record PackageListing(string Metadata, List<ListingEntry> Entries, bool Raw);

/// <summary>A differing byte run: offset, and both sides with a little context, non-printables escaped.</summary>
public record ByteRun(long Offset, string A, string B);

/// <summary>
/// How one file (or a package's metadata, path <c>.package-info</c>) differs between two builds. <see cref="Note"/> describes a
/// difference that is not in the bytes (missing on one side, other permissions); <see cref="Runs"/> are the first differing byte runs.
/// </summary>
public record FileDiff(string Package, string Path, long? SizeA, long? SizeB, string? Note, int RunCount, List<ByteRun> Runs, bool TimestampLike);

/// <summary>How a build differs from the earlier build <see cref="AgainstBuild"/> (by number). <see cref="Files"/> lists at most <see cref="PackageDigest.MaxFiles"/>.</summary>
public record BuildDiff(int AgainstBuild, bool TimestampLike, int FileCount, List<FileDiff> Files);

/// <summary>
/// The content digest that decides whether two builds match. HaikuPorter builds are not reproducible (every hpkg embeds file
/// times), so it hashes what the package contains rather than its bytes:
/// <list type="bullet">
/// <item>the metadata <c>package list -i</c> prints (name, version, provides, requires, ...), minus any attribute whose key mentions a date or time and the packager (it names the building client);</item>
/// <item>every entry of <c>package list -a</c> (path, type and permissions, size, symlink target, attribute names, types and sizes), its time column dropped, except the package-root <c>.PackageInfo</c> (the same metadata, packager included);</item>
/// <item>the SHA-256 of every regular file, extracted with <c>package extract</c>.</item>
/// </list>
/// The digest is the SHA-256 of that canonical text, entries sorted by path (ordinal).
/// </summary>
public static partial class PackageDigest
{
    /// <summary>Most files kept in a stored <see cref="BuildDiff"/>; all are still classified.</summary>
    public const int MaxFiles = 50;
    /// <summary>Byte runs shown per file.</summary>
    public const int ShownRuns = 10;
    /// <summary>More differing runs than this in one file is not a timestamp.</summary>
    public const int MaxTimestampRuns = 64;
    /// <summary>A longer differing run is not a timestamp.</summary>
    public const int MaxTimestampRunLength = 32;
    /// <summary>Differing bytes closer than this are one run.</summary>
    private const int MergeGap = 4;
    private const int Context = 12;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Hash(PackageListing l)
    {
        var sb = new StringBuilder("metadata\n").Append(l.Metadata).Append("\nentries\n");
        foreach (var e in l.Entries.OrderBy(e => e.Path, StringComparer.Ordinal)) sb.Append(e.Path).Append('\t').Append(e.Record).Append('\t').Append(e.Sha256).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>A build's digest: the SHA-256 over its port packages' <c>fileName digest</c> lines, sorted.</summary>
    public static string BuildHash(IEnumerable<(string FileName, string Digest)> packages) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(
            packages.OrderBy(p => p.FileName, StringComparer.Ordinal).Select(p => $"{p.FileName} {p.Digest}\n")))));

    /// <summary>The listing of a file that is compared as opaque bytes.</summary>
    public static async Task<PackageListing> RawAsync(string hpkg, string fileName, CancellationToken ct)
    {
        await using var f = File.OpenRead(hpkg);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
        return new("", [new(fileName, $"raw {f.Length}", sha, f.Length)], true);
    }

    /// <summary>
    /// Lists <paramref name="hpkg"/> with the package tool and extracts it into <paramref name="extractDir"/> to hash the files.
    /// Falls back to <see cref="RawAsync"/> when the tools are missing or the file is not a package the tool can read.
    /// </summary>
    public static async Task<PackageListing> ListAsync(HaikuTools tools, string hpkg, string fileName, string extractDir, CancellationToken ct)
    {
        if (!tools.Available) return await RawAsync(hpkg, fileName, ct);
        var info = await tools.TryRunAsync("package", ["list", "-i", hpkg], ct);
        var contents = info is null ? null : await tools.TryRunAsync("package", ["list", "-a", hpkg], ct);
        if (contents is null || ParseContents(contents) is not { } entries) return await RawAsync(hpkg, fileName, ct);
        Directory.CreateDirectory(extractDir);
        if (await tools.TryRunAsync("package", ["extract", "-C", extractDir, hpkg], ct) is null) return await RawAsync(hpkg, fileName, ct);
        var hashed = new List<ListingEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Path == ".PackageInfo") continue; // the metadata again, with the packager (the building client); Metadata covers it
            if (!e.Record.StartsWith('-')) { hashed.Add(e); continue; }
            var path = Path.Combine(extractDir, e.Path);
            if (!File.Exists(path)) return await RawAsync(hpkg, fileName, ct);
            await using var f = File.OpenRead(path);
            hashed.Add(e with { Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct)) });
        }
        return new(Metadata(info!), hashed, false);
    }

    /// <summary>
    /// The attribute lines of <c>package list -i</c>, trimmed, without keys that mention a date or time and without the
    /// packager, which names the client that built the package and so differs between otherwise identical builds.
    /// </summary>
    public static string Metadata(string listInfo) => string.Join('\n', listInfo.Split('\n').Select(l => l.Trim())
        .Where(l => l.Length > 0 && l != "package-attributes:" && !TimeKey().IsMatch(l) && !PackagerKey().IsMatch(l)));

    /// <summary>
    /// Parses the entries of <c>package list -a</c> (lines after the tab-indented metadata): two spaces of indent per directory
    /// level, then name, size, date and time, permissions and an optional <c>-&gt; target</c>; attribute lines are
    /// <c>&lt;name size 'type'&gt;</c>, one level deeper than their entry. The time is dropped. Null if a line does not parse.
    /// </summary>
    public static List<ListingEntry>? ParseContents(string listA)
    {
        var entries = new List<(string Path, string Record, long Size, List<string> Attrs)>();
        var dirs = new List<string>();
        foreach (var raw in listA.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('\t') || line == "package-attributes:") continue;
            if (AttrLine().Match(line) is { Success: true } a)
            {
                var owner = a.Groups["indent"].Length / 2 - 1;
                if (owner < 0 || entries.Count == 0 || owner >= dirs.Count + 1) return null;
                entries[^1].Attrs.Add($"{a.Groups["name"].Value}:{a.Groups["type"].Value}:{a.Groups["size"].Value}");
                continue;
            }
            var m = EntryLine().Match(line);
            if (!m.Success) return null;
            var depth = m.Groups["indent"].Length / 2;
            if (depth > dirs.Count) return null;
            dirs.RemoveRange(depth, dirs.Count - depth);
            var path = string.Join('/', dirs.Append(m.Groups["name"].Value));
            var perm = m.Groups["perm"].Value;
            if (perm[0] == 'd') dirs.Add(m.Groups["name"].Value);
            var size = long.Parse(m.Groups["size"].Value);
            var target = m.Groups["target"].Success ? " -> " + m.Groups["target"].Value : "";
            entries.Add((path, $"{perm} {size}{target}", size, []));
        }
        return entries.Select(e => new ListingEntry(e.Path, e.Attrs.Count == 0 ? e.Record : $"{e.Record} [{string.Join(' ', e.Attrs)}]", null, e.Size)).ToList();
    }

    /// <summary>
    /// Compares two builds' port packages. <paramref name="dirA"/>/<paramref name="dirB"/> give the directory a package's entries
    /// were extracted to (for raw listings: a directory holding the file under its package file name); they are only called
    /// when file bytes differ.
    /// </summary>
    public static async Task<BuildDiff> CompareAsync(int against, IReadOnlyDictionary<string, PackageListing> a, IReadOnlyDictionary<string, PackageListing> b,
        Func<string, Task<string>> dirA, Func<string, Task<string>> dirB, CancellationToken ct)
    {
        var files = new List<FileDiff>();
        foreach (var pkg in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
        {
            if (!a.TryGetValue(pkg, out var la) || !b.TryGetValue(pkg, out var lb))
            {
                files.Add(new(pkg, "", null, null, $"package only in build {(la is null ? "this" : against.ToString())}", 0, [], false));
                continue;
            }
            if (la.Metadata != lb.Metadata)
            {
                using var ma = new MemoryStream(Encoding.UTF8.GetBytes(la.Metadata));
                using var mb = new MemoryStream(Encoding.UTF8.GetBytes(lb.Metadata));
                files.Add(await CompareBytesAsync(pkg, ".package-info", ma, mb, ct));
            }
            var ea = la.Entries.ToDictionary(e => e.Path);
            var eb = lb.Entries.ToDictionary(e => e.Path);
            foreach (var path in ea.Keys.Union(eb.Keys).Order(StringComparer.Ordinal))
            {
                ea.TryGetValue(path, out var x);
                eb.TryGetValue(path, out var y);
                if (x is null || y is null)
                    files.Add(new(pkg, path, x?.Size, y?.Size, $"only in build {(x is null ? "this" : against.ToString())}", 0, [], false));
                else if (x.Record != y.Record && !(la.Raw && lb.Raw))
                    files.Add(new(pkg, path, x.Size, y.Size, $"{x.Record} vs {y.Record}", 0, [], false));
                else if (x.Sha256 != y.Sha256)
                {
                    await using var fa = File.OpenRead(Path.Combine(await dirA(pkg), path));
                    await using var fb = File.OpenRead(Path.Combine(await dirB(pkg), path));
                    files.Add(await CompareBytesAsync(pkg, path, fa, fb, ct));
                }
            }
        }
        return new(against, files.Count > 0 && files.All(f => f.TimestampLike), files.Count, files.Take(MaxFiles).ToList());
    }

    /// <summary>
    /// Finds the differing byte runs of two seekable streams and classifies them. Files of different sizes are only reported
    /// (an inserted byte shifts everything after it, so there is nothing to classify).
    /// </summary>
    public static async Task<FileDiff> CompareBytesAsync(string pkg, string path, Stream a, Stream b, CancellationToken ct)
    {
        if (a.Length != b.Length) return new(pkg, path, a.Length, b.Length, "size differs", 0, [], false);
        var runs = new List<(long Start, long End)>(); // End exclusive
        var bufA = new byte[1 << 16];
        var bufB = new byte[1 << 16];
        long offset = 0;
        var count = 0;
        while (true)
        {
            var n = await a.ReadAtLeastAsync(bufA, bufA.Length, throwOnEndOfStream: false, ct);
            await b.ReadExactlyAsync(bufB.AsMemory(0, n), ct);
            if (n == 0) break;
            for (var i = 0; i < n; i++)
            {
                if (bufA[i] == bufB[i]) continue;
                var at = offset + i;
                if (runs.Count > 0 && at - runs[^1].End < MergeGap) runs[^1] = (runs[^1].Start, at + 1);
                else if (++count <= MaxTimestampRuns + 1) runs.Add((at, at + 1));
            }
            offset += n;
        }
        var timestamp = runs.Count is > 0 and <= MaxTimestampRuns;
        var shown = new List<ByteRun>();
        foreach (var (start, end) in runs)
        {
            var from = Math.Max(0, start - MaxTimestampRunLength);
            var len = (int)(Math.Min(a.Length, end + MaxTimestampRunLength) - from);
            var wa = await ReadAtAsync(a, from, len, ct);
            var wb = await ReadAtAsync(b, from, len, ct);
            int s = (int)(start - from), e = (int)(end - from);
            timestamp &= end - start <= MaxTimestampRunLength && Timestamps.Covers(wa, s, e) && Timestamps.Covers(wb, s, e);
            if (shown.Count < ShownRuns)
            {
                var cs = Math.Max(0, s - Context);
                var ce = Math.Min(len, e + Context);
                shown.Add(new(start, Escape(wa.AsSpan(cs, ce - cs)), Escape(wb.AsSpan(cs, ce - cs))));
            }
        }
        return new(pkg, path, a.Length, b.Length, null, count, shown, timestamp);
    }

    private static async Task<byte[]> ReadAtAsync(Stream s, long at, int len, CancellationToken ct)
    {
        var buf = new byte[len];
        s.Position = at;
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    /// <summary>Printable ASCII as is, everything else as <c>\xNN</c>.</summary>
    public static string Escape(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var c in bytes) if (c is >= 0x20 and < 0x7f and not (byte)'\\') sb.Append((char)c); else sb.Append($"\\x{c:x2}");
        return sb.ToString();
    }

    [GeneratedRegex(@"^[^:]*\b(date|time)\b[^:]*:", RegexOptions.IgnoreCase)]
    private static partial Regex TimeKey();

    [GeneratedRegex(@"^packager\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex PackagerKey();

    [GeneratedRegex(@"^(?<indent> *)(?<name>\S.*?) +(?<size>\d+)  \d{4}-\d\d-\d\d \d\d:\d\d:\d\d  (?<perm>[-dlbcps][-rwxsStT]{9})(?:  -> (?<target>.*))?$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^(?<indent> *)<(?<name>.+?) +(?<size>\d+)  (?<type>.+)>$")]
    private static partial Regex AttrLine();
}

/// <summary>
/// Recognises build timestamps in a byte window. A differing run is timestamp-like when, on both sides, one of these patterns
/// matches text that covers the whole run (bytes read as Latin-1):
/// <list type="bullet">
/// <item>asctime / <c>date</c>: <c>Tue Nov 17 21:13:05 2026</c> (optionally with a time zone name before the year);</item>
/// <item><c>__DATE__</c>: <c>Nov 17 2026</c>, <c>Nov  7 2026</c>; <c>__TIME__</c> and other clock times: <c>21:13:05</c>;</item>
/// <item>ISO 8601: <c>2026-11-17</c>, optionally <c>T21:13:05</c> with fraction and zone;</item>
/// <item>epoch seconds or milliseconds between 2001 and 2033: ten digits starting with 1 (thirteen for milliseconds).</item>
/// </list>
/// Binary timestamps (e.g. a little-endian time_t) are deliberately not recognised: the run would look like any other byte change.
/// </summary>
public static partial class Timestamps
{
    private const string Month = "(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)";
    private const string Day = "(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)";

    /// <summary>True if some timestamp pattern match in <paramref name="window"/> contains bytes [<paramref name="start"/>, <paramref name="end"/>).</summary>
    public static bool Covers(byte[] window, int start, int end)
    {
        var text = Encoding.Latin1.GetString(window);
        foreach (var re in Patterns)
            foreach (Match m in re.Matches(text))
                if (m.Index <= start && m.Index + m.Length >= end) return true;
        return false;
    }

    private static readonly Regex[] Patterns = [Asctime(), CDate(), Clock(), Iso(), Epoch()];

    [GeneratedRegex(Day + " " + Month + @" [ \d]\d \d\d:\d\d:\d\d (?:[A-Z]{3,5} )?\d{4}")]
    private static partial Regex Asctime();

    [GeneratedRegex(Month + @" [ \d]\d \d{4}")]
    private static partial Regex CDate();

    [GeneratedRegex(@"(?<!\d)\d\d:\d\d:\d\d(?!\d)")]
    private static partial Regex Clock();

    [GeneratedRegex(@"(?<!\d)\d{4}-\d\d-\d\d(?:[T ]\d\d:\d\d(?::\d\d(?:\.\d+)?)?(?:Z|[+-]\d\d:?\d\d)?)?(?!\d)")]
    private static partial Regex Iso();

    [GeneratedRegex(@"(?<!\d)1\d{9}(?:\d{3})?(?!\d)")]
    private static partial Regex Epoch();
}
