using System.Text;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>The timestamp classifier, the package listing parser and the digest, without a database.</summary>
/// <remarks>In the Database collection anyway: the verifier there empties the tools' attribute directory, which this test's extractions use.</remarks>
[Collection("Database")]
public class PackageDigestTests
{
    private static Task<FileDiff> Diff(string a, string b) =>
        PackageDigest.CompareBytesAsync("p.hpkg", "f", new MemoryStream(Encoding.Latin1.GetBytes(a)), new MemoryStream(Encoding.Latin1.GetBytes(b)), default);

    [Theory]
    [InlineData("built Nov 17 2026 21:13:05 ok", "built Nov 17 2026 21:14:22 ok")] // __DATE__ __TIME__
    [InlineData("v1 (Nov  7 2026)", "v1 (Nov 17 2026)")]                            // __DATE__, space-padded day
    [InlineData("at 2026-11-17T21:13:05Z.", "at 2026-11-18T01:02:03Z.")]            // ISO 8601
    [InlineData("date 2026-11-17 x", "date 2026-11-18 x")]
    [InlineData("Tue Nov 17 21:13:05 2026", "Wed Nov 18 01:00:00 2026")]            // asctime
    [InlineData("Tue Nov 17 21:13:05 UTC 2026", "Tue Nov 17 22:00:00 UTC 2026")]    // date(1)
    [InlineData("mtime=1763413985;", "mtime=1763414000;")]                          // epoch seconds
    [InlineData("ms 1763413985123 x", "ms 1763413999999 x")]                        // epoch milliseconds
    [InlineData("\0\x01bin\0 12:00:01 \x7f\0 and later 21:00:00", "\0\x01bin\0 12:30:59 \x7f\0 and later 22:10:00")] // two runs, binary around
    public async Task Timestamp_differences_are_recognised(string a, string b)
    {
        var d = await Diff(a, b);
        Assert.True(d.TimestampLike, string.Join(" | ", d.Runs.Select(r => $"{r.Offset}: {r.A} / {r.B}")));
        Assert.NotEmpty(d.Runs);
    }

    [Theory]
    [InlineData("version 1.2.3", "version 1.2.4")]                   // a version, not a date
    [InlineData("built Nov 17 2026", "built Nov 17 2026!")]          // sizes differ
    [InlineData("at 12:00:00 x", "at 12:00:0x x")]                   // a time on one side only
    [InlineData("9876543210", "9876543211")]                         // ten digits, but not an epoch between 2001 and 2033
    [InlineData("id 1763413985 and 0123456789abcdef0123456789abcdef0123", "id 1763413985 and fedcba9876543210fedcba9876543210fe32")] // long run
    [InlineData("\x01\x02\x03\x04", "\x01\x09\x03\x04")]             // a binary byte
    public async Task Other_differences_are_not(string a, string b) => Assert.False((await Diff(a, b)).TimestampLike);

    [Fact]
    public async Task Many_scattered_runs_are_not_a_timestamp()
    {
        var a = string.Concat(Enumerable.Repeat("t=10:00:00;", 70));
        var b = string.Concat(Enumerable.Repeat("t=11:00:00;", 70));
        var d = await Diff(a, b);
        Assert.False(d.TimestampLike);
        Assert.Equal(PackageDigest.ShownRuns, d.Runs.Count);
    }

    [Fact]
    public async Task Identical_bytes_have_no_runs() => Assert.Empty((await Diff("same", "same")).Runs);

    private const string ListA = """
        package-attributes:
        	name: xxhash
        	summary: A fast hash
        	build date: 2026-11-17 22:48:36
        	version: 0.8.3-1
        	provides: xxhash = 0.8.3
        bin                                      0  2026-11-17 22:48:36  drwxr-xr-x
          <BEOS:TYPE                            31  'MIMS'>
          xxh128sum                              0  2026-11-17 22:48:36  lrwxrwxrwx  -> xxhsum
            <BEOS:TYPE                          29  'MIMS'>
          xxhsum                             94208  2026-11-17 22:48:36  -rwxr-xr-x
        lib                                      0  2026-11-17 22:48:36  drwxr-xr-x
          a file                                10  2026-11-17 22:48:36  -rw-r--r--
        .PackageInfo                           750  2026-11-17 22:48:37  -rw-r--r--
        """;

    [Fact]
    public void Listing_parses_paths_attributes_and_links_and_drops_times()
    {
        var entries = PackageDigest.ParseContents(ListA)!;
        Assert.Equal(["bin", "bin/xxh128sum", "bin/xxhsum", "lib", "lib/a file", ".PackageInfo"], entries.Select(e => e.Path));
        Assert.Equal("drwxr-xr-x 0 [BEOS:TYPE:'MIMS':31]", entries[0].Record);
        Assert.Equal("lrwxrwxrwx 0 -> xxhsum [BEOS:TYPE:'MIMS':29]", entries[1].Record);
        Assert.Equal("-rwxr-xr-x 94208", entries[2].Record);
        Assert.Equal("name: xxhash\nsummary: A fast hash\nversion: 0.8.3-1\nprovides: xxhash = 0.8.3", PackageDigest.Metadata(ListA.Split("bin ")[0]));

        var later = PackageDigest.ParseContents(ListA.Replace("2026-11-17 22:48:3", "2027-01-02 03:04:0"))!;
        Assert.Equal(PackageDigest.Hash(new("m", entries, false)), PackageDigest.Hash(new("m", later, false)));
        var chmod = PackageDigest.ParseContents(ListA.Replace("94208  2026-11-17 22:48:36  -rwxr-xr-x", "94208  2026-11-17 22:48:36  -rw-r--r--"))!;
        Assert.NotEqual(PackageDigest.Hash(new("m", entries, false)), PackageDigest.Hash(new("m", chmod, false)));
        Assert.Null(PackageDigest.ParseContents("garbage line\n"));
    }

    /// <summary>Real packages made with <c>package create</c>: the digest ignores file times and nothing else.</summary>
    [HaikuToolsFact]
    public async Task Digest_of_real_packages_ignores_times_and_catches_content_mode_file_and_metadata_changes()
    {
        var tools = new HaikuTools(HaikuToolsFactAttribute.ToolsDir);
        var work = Directory.CreateTempSubdirectory("digest-").FullName;
        var n = 0;
        async Task<(string Digest, PackageListing Listing, string Dir, string File)> Make(Action<string>? change = null, string summary = "Test", DateTime? mtime = null)
        {
            var src = Path.Combine(work, $"src{++n}");
            Directory.CreateDirectory(Path.Combine(src, "bin"));
            Directory.CreateDirectory(Path.Combine(src, "lib"));
            File.WriteAllText(Path.Combine(src, "bin", "hi"), "hello\n");
            File.SetUnixFileMode(Path.Combine(src, "bin", "hi"), (UnixFileMode)0b111_101_101);
            File.CreateSymbolicLink(Path.Combine(src, "lib", "link"), "../bin/hi");
            change?.Invoke(src);
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, mtime ?? new DateTime(2026, 1, 1));
            var info = Path.Combine(work, $"info{n}");
            File.WriteAllText(info, $$"""
                name testpkg
                version 1.0-1
                architecture arm64
                summary "{{summary}}"
                description "Test package"
                packager "test <test@example.invalid>"
                vendor "Haiku Project"
                copyrights "2026 test"
                licenses "MIT"
                provides { testpkg = 1.0 }
                requires { haiku }
                """);
            var hpkg = Path.Combine(work, $"testpkg{n}.hpkg");
            Assert.NotNull(await tools.TryRunAsync("package", ["create", "-q", "-C", src, "-i", info, hpkg], default));
            var dir = Path.Combine(work, $"x{n}");
            var l = await PackageDigest.ListAsync(tools, hpkg, "testpkg-1.0-1-arm64.hpkg", dir, default);
            Assert.False(l.Raw);
            return (PackageDigest.Hash(l), l, dir, hpkg);
        }

        var a = await Make();
        var later = await Make(mtime: new DateTime(2026, 6, 6, 12, 0, 0));
        Assert.NotEqual(await File.ReadAllBytesAsync(a.File), await File.ReadAllBytesAsync(later.File)); // the hpkgs differ...
        Assert.Equal(a.Digest, later.Digest);                                                           // ...their content does not
        Assert.Contains(a.Listing.Entries, e => e.Path == "lib/link" && e.Record.Contains("-> ../bin/hi"));

        var bytes = await Make(src => File.WriteAllText(Path.Combine(src, "bin", "hi"), "hellp\n"));
        var mode = await Make(src => File.SetUnixFileMode(Path.Combine(src, "bin", "hi"), (UnixFileMode)0b110_100_100));
        var extra = await Make(src => File.WriteAllText(Path.Combine(src, "bin", "more"), "x"));
        var meta = await Make(summary: "Other");
        Assert.Equal(5, new[] { a.Digest, bytes.Digest, mode.Digest, extra.Digest, meta.Digest }.Distinct().Count());

        var pkg = "testpkg-1.0-1-arm64.hpkg";
        var diff = await PackageDigest.CompareAsync(1, new Dictionary<string, PackageListing> { [pkg] = a.Listing }, new Dictionary<string, PackageListing> { [pkg] = bytes.Listing },
            _ => Task.FromResult(a.Dir), _ => Task.FromResult(bytes.Dir), default);
        var f = Assert.Single(diff.Files);
        Assert.Equal(("bin/hi", 1), (f.Path, f.Runs.Count));
        Assert.False(diff.TimestampLike);
        Directory.Delete(work, true);
    }
}
