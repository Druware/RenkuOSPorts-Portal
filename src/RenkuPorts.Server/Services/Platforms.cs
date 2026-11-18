using System.Text.RegularExpressions;

namespace RenkuPorts.Server.Services;

/// <summary>Input checks shared by the endpoints.</summary>
public static partial class Validate
{
    /// <summary>Haiku architecture names served by the repo, in display order.</summary>
    public static readonly string[] Platforms = ["x86", "x86_64", "arm64", "riscv64"];

    public static bool IsPlatform(string? p) => p is not null && Platforms.Contains(p);

    /// <summary>A plain <c>*.hpkg</c> name: no path separators, no leading dot, no "..".</summary>
    public static bool IsHpkgName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? name) => name is not null && name.Length <= 255 && HpkgName().IsMatch(name) && !name.Contains("..");

    [GeneratedRegex(@"^[A-Za-z0-9_+~][A-Za-z0-9._+~-]*\.hpkg$")]
    private static partial Regex HpkgName();
}

/// <summary>
/// Orders version strings the way people expect for recipe versions: numeric runs compare as numbers,
/// and a "~" segment (pre-release, e.g. 1.4~rc5) sorts before the release.
/// </summary>
/// <remarks>shortcut: not HaikuPorter's full version ordering (which also weighs the recipe's ARCHITECTURES); upgrade if a port's "highest" pick is wrong.</remarks>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        var (ar, apre) = SplitPre(a);
        var (br, bpre) = SplitPre(b);
        var c = CompareParts(ar, br);
        if (c != 0) return c;
        if (apre is null || bpre is null) return apre is null ? (bpre is null ? 0 : 1) : -1;
        return CompareParts(apre, bpre);
    }

    private static (string, string?) SplitPre(string v) => v.IndexOf('~') is var i and >= 0 ? (v[..i], v[(i + 1)..]) : (v, null);

    private static int CompareParts(string a, string b)
    {
        var pa = Regex.Matches(a, @"\d+|[A-Za-z]+");
        var pb = Regex.Matches(b, @"\d+|[A-Za-z]+");
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            if (i >= pa.Count) return -1;
            if (i >= pb.Count) return 1;
            string x = pa[i].Value, y = pb[i].Value;
            int c = char.IsDigit(x[0]) && char.IsDigit(y[0])
                ? CompareNumbers(x.TrimStart('0'), y.TrimStart('0'))
                : string.CompareOrdinal(x, y);
            if (c != 0) return c;
        }
        return 0;
    }

    // Digit strings of any length, leading zeros removed: longer is larger, else ordinal.
    private static int CompareNumbers(string x, string y) => x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
}
