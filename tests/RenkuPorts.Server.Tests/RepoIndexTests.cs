using RenkuPorts.Server.Endpoints;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>
/// Runs only where Haiku's host tools and real .hpkg files are available: <c>RENKUPORTS_HAIKU_TOOLS</c> (default /opt/haiku-tools)
/// and <c>RENKUPORTS_TEST_HPKG_DIR</c>. Elsewhere it is reported as skipped with this reason, unless
/// <c>RENKUPORTS_REQUIRE_HAIKU_TOOLS=1</c> (set by tests/run-haiku-tools-test.sh), which makes missing tools a failure.
/// </summary>
public sealed class HaikuToolsFactAttribute : FactAttribute
{
    public static string ToolsDir => Environment.GetEnvironmentVariable("RENKUPORTS_HAIKU_TOOLS") ?? "/opt/haiku-tools";
    public static string? HpkgDir => Environment.GetEnvironmentVariable("RENKUPORTS_TEST_HPKG_DIR");

    public HaikuToolsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RENKUPORTS_REQUIRE_HAIKU_TOOLS") == "1") return;
        if (!new HaikuTools(ToolsDir).Available || HpkgDir is null)
            Skip = $"Needs the Haiku host tools in {ToolsDir} and RENKUPORTS_TEST_HPKG_DIR; run tests/run-haiku-tools-test.sh";
    }
}

/// <summary>Repository index input selection, log cleaning, and package_repo itself on real packages.</summary>
public class RepoIndexTests
{
    private static IndexCandidate C(string file) => new(file, new string('a', 64), $"arm64/x/{file}");

    [Fact]
    public void Selection_keeps_the_newest_version_and_revision_of_each_name()
    {
        var (selected, rejected) = RepoIndexInput.Select([
            C("foo-1.9-1-arm64.hpkg"), C("foo-1.10-1-arm64.hpkg"), C("foo-1.10~rc1-3-arm64.hpkg"),
            C("bar-2.0-2-arm64.hpkg"), C("bar-2.0-10-arm64.hpkg"),
            C("foo_devel-1.10-1-arm64.hpkg"), C("py-3.1-1-any.hpkg"),
        ]);
        Assert.Equal(["bar-2.0-10-arm64.hpkg", "foo-1.10-1-arm64.hpkg", "foo_devel-1.10-1-arm64.hpkg", "py-3.1-1-any.hpkg"], selected.Select(p => p.FileName));
        Assert.Empty(rejected);
    }

    [Fact]
    public void Selection_drops_debuginfo_and_source_and_rejects_odd_names()
    {
        var (selected, rejected) = RepoIndexInput.Select([
            C("foo-1.0-1-arm64.hpkg"), C("foo_debuginfo-1.0-1-arm64.hpkg"), C("foo_source-1.0-1-source.hpkg"), C("gzip-big.hpkg"),
        ]);
        Assert.Equal(["foo-1.0-1-arm64.hpkg"], selected.Select(p => p.FileName));
        Assert.Equal("gzip-big.hpkg", Assert.Single(rejected).FileName);
    }

    [Fact]
    public void Log_escape_sequences_are_stripped()
    {
        Assert.Equal("Building zlib done\nok ",
            BuildClientApi.StripAnsi("\x1b[1mBuilding\x1b[0m zlib \x1b[32;1mdone\x1b[m\n\x1b]0;title\x07ok \x1b]8;;http://x\x1b\\\x1b"));
        Assert.Equal("plain [1m text", BuildClientApi.StripAnsi("plain [1m text"));
    }

    [HaikuToolsFact]
    public async Task Package_repo_creates_and_incrementally_updates_an_index_of_real_packages()
    {
        string[] real = ["packaging-26.2-1-any.hpkg", "markupsafe-2.1.3-3-arm64.hpkg", "libbase58_devel-0.1.4-2-arm64.hpkg"];
        var tools = new HaikuTools(HaikuToolsFactAttribute.ToolsDir);
        Assert.True(tools.Available, $"package_repo and package not found in {tools.Dir}");
        var work = Directory.CreateTempSubdirectory("repoindex-test-").FullName;
        var pkgs = Directory.CreateDirectory(Path.Combine(work, "packages")).FullName;
        foreach (var f in real) File.Copy(Path.Combine(HaikuToolsFactAttribute.HpkgDir!, f), Path.Combine(pkgs, f));
        File.WriteAllText(Path.Combine(pkgs, "zlib-1.3.2-1-arm64.hpkg"), "mock package\n");
        File.Copy(Path.Combine(pkgs, real[1]), Path.Combine(pkgs, "renamed-1-1-arm64.hpkg"));

        foreach (var f in real) Assert.Null(await tools.CheckPackageAsync(Path.Combine(pkgs, f), "Haiku Project", "arm64", default));
        Assert.StartsWith("not a valid package", await tools.CheckPackageAsync(Path.Combine(pkgs, "zlib-1.3.2-1-arm64.hpkg"), "Haiku Project", "arm64", default));
        Assert.StartsWith("vendor is", await tools.CheckPackageAsync(Path.Combine(pkgs, real[1]), "Someone Else", "arm64", default));
        Assert.StartsWith("architecture is", await tools.CheckPackageAsync(Path.Combine(pkgs, real[1]), "Haiku Project", "x86_64", default));
        Assert.StartsWith("file name differs", await tools.CheckPackageAsync(Path.Combine(pkgs, "renamed-1-1-arm64.hpkg"), "Haiku Project", "arm64", default));

        const string info = "name RenkuOSPorts\nvendor \"Haiku Project\"\nsummary \"test\"\npriority 1\nbaseurl http://localhost/repo/arm64\n" +
            "identifier tag:renkuos.org,2026:repositories/renkuosports/arm64\narchitecture arm64\n";
        var first = Directory.CreateDirectory(Path.Combine(work, "first")).FullName;
        File.WriteAllText(Path.Combine(first, "repo.info"), info);
        await tools.CreateAsync(first, real[..2].Select(f => Path.Combine(pkgs, f)), default);
        Assert.Equal(real[..2].Order(), (await tools.ListAsync(Path.Combine(first, "repo"), default)).Order());

        // Incremental: only the new package's file is present; the others come from the old repo (update -t).
        foreach (var f in real[..2]) File.Delete(Path.Combine(pkgs, f));
        var second = Directory.CreateDirectory(Path.Combine(work, "second")).FullName;
        File.WriteAllText(Path.Combine(second, "repo.info"), info);
        var list = Path.Combine(work, "list");
        File.WriteAllLines(list, real);
        await tools.UpdateAsync(Path.Combine(first, "repo"), second, list, pkgs, default);
        Assert.Equal(real.Order(), (await tools.ListAsync(Path.Combine(second, "repo"), default)).Order());
        Directory.Delete(work, true);
    }
}
