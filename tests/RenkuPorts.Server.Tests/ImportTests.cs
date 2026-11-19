using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>Repository import against a real source repository (made with package_repo from real hpkgs) served over HTTP.</summary>
public partial class ApiTests
{
    private record SkipView(string FileName, string Reason);
    private record ImportView(long Id, string Status, int Total, int Missing, int Imported, List<SkipView> Skipped, string? Error, string StartedBy);

    private static async Task<ImportView> ImportAsync(HttpClient admin, string url, string platform, bool dryRun, bool gentle = false)
    {
        var res = await admin.PostAsJsonAsync("/api/imports", gentle
            ? new { sourceUrl = url, platform, dryRun, parallelism = (int?)1, delayMinSeconds = (double?)0, delayMaxSeconds = (double?)0.05, maxKilobytesPerSecond = (int?)64 }
            : new { sourceUrl = url, platform, dryRun, parallelism = (int?)null, delayMinSeconds = (double?)null, delayMaxSeconds = (double?)null, maxKilobytesPerSecond = (int?)null });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        for (var i = 0; i < 300; i++)
        {
            var view = (await admin.GetFromJsonAsync<ImportView>($"/api/imports/{id}"))!;
            if (view.Status != "Running") return view;
            await Task.Delay(200);
        }
        throw new TimeoutException($"import {id} still running");
    }

    [Fact]
    public async Task Import_rejects_bad_requests()
    {
        var admin = await app.SignedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/imports", new { sourceUrl = "ftp://x/repo", platform = "arm64" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/imports", new { sourceUrl = "http://x/repo", platform = "sparc" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.BuilderAsync()).PostAsJsonAsync("/api/imports", new { sourceUrl = "http://x/repo", platform = "arm64" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/imports/999999")).StatusCode);

        async Task AssertBad(object body, string key)
        {
            var res = await admin.PostAsJsonAsync("/api/imports", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.True((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(key, out _), key);
        }
        await AssertBad(new { sourceUrl = "http://x/repo", platform = "arm64", parallelism = 0 }, "parallelism");
        await AssertBad(new { sourceUrl = "http://x/repo", platform = "arm64", parallelism = 5 }, "parallelism");
        await AssertBad(new { sourceUrl = "http://x/repo", platform = "arm64", delayMinSeconds = 10, delayMaxSeconds = 2 }, "delayMaxSeconds");
        await AssertBad(new { sourceUrl = "http://x/repo", platform = "arm64", delayMinSeconds = -1 }, "delayMinSeconds");
        await AssertBad(new { sourceUrl = "http://x/repo", platform = "arm64", delayMaxSeconds = 301 }, "delayMaxSeconds");
        await AssertBad(new { sourceUrl = "http://x/repo", platform = "arm64", maxKilobytesPerSecond = 8 }, "maxKilobytesPerSecond");
        await AssertBad(new { sourceUrl = "http://x/repo", platform = "arm64", maxKilobytesPerSecond = 1_000_001 }, "maxKilobytesPerSecond");
    }

    [Fact]
    public void Import_throttle_paces_bytes_to_the_rate_without_idle_credit()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = DateTime.MinValue;
        // 64 KiB through 32 KiB/s in two chunks read back to back: the second waits for the end of its slot, 2 s in.
        Assert.Equal(TimeSpan.FromSeconds(1), RepoImporter.Throttle.Reserve(ref next, t0, 32768, 32768));
        Assert.Equal(TimeSpan.FromSeconds(2), RepoImporter.Throttle.Reserve(ref next, t0, 32768, 32768));
        // A second worker booking at the same moment queues behind both.
        Assert.Equal(TimeSpan.FromSeconds(2.5), RepoImporter.Throttle.Reserve(ref next, t0.AddSeconds(0.5), 32768, 32768));
        // After a long idle spell no burst is allowed: the slot starts now.
        Assert.Equal(TimeSpan.FromSeconds(1), RepoImporter.Throttle.Reserve(ref next, t0.AddSeconds(60), 32768, 32768));
    }

    [HaikuToolsFact]
    public async Task Import_backfills_missing_packages_from_a_pkgman_repository()
    {
        string[] real = ["packaging-26.2-1-any.hpkg", "markupsafe-2.1.3-3-arm64.hpkg", "libbase58_devel-0.1.4-2-arm64.hpkg"];
        var tools = new HaikuTools(HaikuToolsFactAttribute.ToolsDir);
        var src = Directory.CreateTempSubdirectory("import-src-").FullName;
        var pkgs = Directory.CreateDirectory(Path.Combine(src, "packages")).FullName;
        foreach (var f in real) File.Copy(Path.Combine(HaikuToolsFactAttribute.HpkgDir!, f), Path.Combine(pkgs, f));
        File.WriteAllText(Path.Combine(src, "repo.info"), "name Source\nvendor \"Haiku Project\"\nsummary \"test source\"\npriority 1\n" +
            "baseurl http://localhost/\nidentifier tag:example.org,2026:source/arm64\narchitecture arm64\n");
        await tools.CreateAsync(src, real.Select(f => Path.Combine(pkgs, f)), default);
        File.WriteAllText(Path.Combine(src, "repo.sha256"), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(src, "repo")))) + "\n");
        // Served file no longer matches the checksum the repository lists for it.
        var corrupt = Path.Combine(pkgs, real[2]);
        var bytes = File.ReadAllBytes(corrupt);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(corrupt, bytes);

        using var server = new StaticServer(src);
        var admin = await app.SignedInAsync();

        var dry = await ImportAsync(admin, server.Url, "arm64", dryRun: true);
        Assert.Equal(("Completed", 3, 3, 0), (dry.Status, dry.Total, dry.Missing, dry.Imported));
        Assert.Equal(real.Order(), dry.Skipped.Select(s => s.FileName).Order());
        await app.WithDbAsync(async db => Assert.Equal(0, await db.Packages.CountAsync()));

        var wrongArch = await ImportAsync(admin, server.Url, "x86_64", dryRun: true);
        Assert.Equal("Failed", wrongArch.Status);
        Assert.Contains("architecture is 'arm64'", wrongArch.Error);

        var run = await ImportAsync(admin, server.Url, "arm64", dryRun: false, gentle: true);
        Assert.Equal(("Completed", 3, 3, 2), (run.Status, run.Total, run.Missing, run.Imported));
        Assert.Equal(real[2], Assert.Single(run.Skipped).FileName);
        Assert.StartsWith("checksum mismatch", run.Skipped[0].Reason);

        var again = await ImportAsync(admin, server.Url, "arm64", dryRun: false);
        Assert.Equal((1, 0), (again.Missing, again.Imported)); // only the corrupt one is still missing

        var anon = app.CreateClient();
        var listed = (await anon.GetFromJsonAsync<PageView<PackageView>>("/api/repo/arm64/packages"))!.Items;
        Assert.Equal(real[..2].Order(), listed.Select(p => p.FileName).Order());
        Assert.All(listed, p => Assert.Equal($"import: {new Uri(server.Url).Authority}", p.BuiltBy));
        Assert.Equal("markupsafe", listed.Single(p => p.FileName == real[1]).PortName);
        Assert.Equal(File.ReadAllBytes(Path.Combine(pkgs, real[0])), await anon.GetByteArrayAsync($"/repo/arm64/packages/{real[0]}"));

        // The regenerated index lists the imported packages.
        var indexer = app.Services.GetRequiredService<RepoIndexer>();
        for (var i = 0; !await indexer.BuildAsync("arm64", force: true, default); i++)
        {
            Assert.True(i < 50, "index lock never became free");
            await Task.Delay(200);
        }
        var repo = Path.Combine(src, "portal.repo");
        File.WriteAllBytes(repo, await anon.GetByteArrayAsync("/repo/arm64/repo"));
        Assert.Equal(real[..2].Order(), (await tools.ListAsync(repo, default)).Order());
        Directory.Delete(src, true);
    }

    /// <summary>Serves a directory over HTTP on a free loopback port.</summary>
    private sealed class StaticServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public StaticServer(string root)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/source/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    var path = Path.Combine(root, Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath["/source/".Length..]));
                    if (File.Exists(path))
                    {
                        var data = await File.ReadAllBytesAsync(path);
                        ctx.Response.ContentLength64 = data.Length;
                        await ctx.Response.OutputStream.WriteAsync(data);
                    }
                    else ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }
}
