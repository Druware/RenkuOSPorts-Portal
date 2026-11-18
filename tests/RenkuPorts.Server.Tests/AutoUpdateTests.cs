using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>The auto-update: diff the ports tree from the last processed commit and queue changed ports we already publish.</summary>
public partial class ApiTests
{
    private record SkipPortView(string Port, string Platform, string Reason);
    private record AutoResultView(string? FromCommit, string? ToCommit, List<string> ChangedPorts, int Queued, List<SkipPortView> Skipped, string? Note, string? Error);
    private record AutoView(bool Enabled, int IntervalHours, List<string> Platforms, bool Running, DateTime? LastRunAt, string? LastCommit, AutoResultView? LastResult);

    private static void WriteRecipe(string repo, string port, string version, int revision, string extra = "") =>
        File.WriteAllText(Path.Combine(repo, "app-auto", port, $"{port}-{version}.recipe"), $"SUMMARY=\"{port}\"\nREVISION=\"{revision}\"\n{extra}");

    private static string Head(string repo)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", ["-C", repo, "rev-parse", "HEAD"]) { RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var head = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return head;
    }

    private static async Task<AutoView> RunNowAsync(HttpClient admin)
    {
        var res = await admin.PostAsync("/api/settings/auto-update/run", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<AutoView>())!;
    }

    [Fact]
    public async Task Auto_update_queues_changed_ports_we_publish_whose_revision_is_new()
    {
        var repo = Directory.CreateTempSubdirectory("renkuports-auto-").FullName;
        foreach (var port in new[] { "autoport", "otherport" }) Directory.CreateDirectory(Path.Combine(repo, "app-auto", port));
        WriteRecipe(repo, "autoport", "1.0", 1);
        WriteRecipe(repo, "otherport", "2.0", 1);
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "one");

        using var gitApp = app.WithWebHostBuilder(b => b.UseSetting("PortsTree:Path", repo));
        await app.WithDbAsync(db => db.AutoUpdateSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastCommit, (string?)null)
            .SetProperty(x => x.LastRunAt, (DateTime?)null).SetProperty(x => x.RunningSince, (DateTime?)null).SetProperty(x => x.LastResultJson, (string?)null)));
        var admin = gitApp.CreateClient(new() { HandleCookies = true });
        (await admin.PostAsJsonAsync("/api/auth/login", new { email = AppFactory.AdminEmail, password = AppFactory.AdminPassword })).EnsureSuccessStatusCode();
        var updater = gitApp.Services.GetRequiredService<AutoUpdater>();

        // Disabled: the schedule does nothing.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/auto-update", new { enabled = false, intervalHours = 1, platforms = new[] { "arm64" } })).StatusCode);
        Assert.Null(await updater.RunIfDueAsync());
        Assert.Null((await admin.GetFromJsonAsync<AutoView>("/api/settings/auto-update"))!.LastRunAt);

        // First run (Run now works while disabled): records HEAD, queues nothing.
        var first = await RunNowAsync(admin);
        Assert.Equal(Head(repo), first.LastCommit);
        Assert.StartsWith("First run", first.LastResult!.Note);
        Assert.Empty((await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs"))!.Items);

        // Publish autoport 1.0 revision 1 on arm64.
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "autoport");
        var job = await ClaimAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, job.JobId, "autoport-1.0-1-arm64.hpkg", [1, 2])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/client/jobs/{job.JobId}/complete", new { log = "" })).StatusCode);

        // A change without a REVISION bump, and a change to a port we do not publish: both skipped.
        WriteRecipe(repo, "autoport", "1.0", 1, "# tweak\n");
        WriteRecipe(repo, "otherport", "2.0", 2);
        Git(repo, "commit", "-q", "-am", "two");
        var second = (await RunNowAsync(admin)).LastResult!;
        Assert.Null(second.Error);
        Assert.Equal(["autoport", "otherport"], second.ChangedPorts);
        Assert.Equal(0, second.Queued);
        Assert.Contains(new SkipPortView("otherport", "arm64", "not built on arm64 yet"), second.Skipped);
        Assert.Contains(second.Skipped, s => s.Port == "autoport" && s.Reason.Contains("already published"));

        // A patch plus a REVISION bump of a port we publish, picked up by the schedule once due.
        Directory.CreateDirectory(Path.Combine(repo, "app-auto", "autoport", "patches"));
        File.WriteAllText(Path.Combine(repo, "app-auto", "autoport", "patches", "autoport-1.0.patchset"), "p\n");
        WriteRecipe(repo, "autoport", "1.0", 2);
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "three");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/auto-update", new { enabled = true, intervalHours = 1, platforms = new[] { "arm64" } })).StatusCode);
        Assert.Null(await updater.RunIfDueAsync()); // ran less than an hour ago
        await app.WithDbAsync(db => db.AutoUpdateSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAt, DateTime.UtcNow.AddHours(-2))));
        var third = (await updater.RunIfDueAsync())!;
        Assert.Equal(1, third.Queued);
        Assert.Null(third.Error);
        Assert.Equal(["autoport"], third.ChangedPorts);
        var queued = Assert.Single((await admin.GetFromJsonAsync<PageView<JobView>>("/api/jobs?status=Queued"))!.Items);
        Assert.Equal(("autoport", "arm64"), (queued.PortName, queued.Platform));
        Assert.Equal("auto-update", (await admin.GetFromJsonAsync<PageView<System.Text.Json.JsonElement>>("/api/jobs?status=Queued"))!.Items[0].GetProperty("queuedBy").GetString());
        Assert.Equal(Head(repo), (await admin.GetFromJsonAsync<AutoView>("/api/settings/auto-update"))!.LastCommit);

        // A git error keeps the old commit and records the error.
        var before = Head(repo);
        await app.WithDbAsync(db => db.AutoUpdateSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastCommit, "0000000000000000000000000000000000000000")));
        var failed = await RunNowAsync(admin);
        Assert.NotNull(failed.LastResult!.Error);
        Assert.Equal("0000000000000000000000000000000000000000", failed.LastCommit);
        Assert.NotEqual(before, failed.LastCommit);

        // A run in progress elsewhere: Run now is refused.
        await app.WithDbAsync(db => db.AutoUpdateSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.RunningSince, DateTime.UtcNow)));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/settings/auto-update/run", null)).StatusCode);
        Assert.True((await admin.GetFromJsonAsync<AutoView>("/api/settings/auto-update"))!.Running);
        await app.WithDbAsync(db => db.AutoUpdateSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.RunningSince, (DateTime?)null).SetProperty(x => x.Enabled, false)));
    }

    [Fact]
    public async Task Auto_update_settings_are_admin_only_and_validated()
    {
        var builder = await app.BuilderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/settings/auto-update")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PutAsJsonAsync("/api/settings/auto-update", new { enabled = true, intervalHours = 6, platforms = new[] { "arm64" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsync("/api/settings/auto-update/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/settings/auto-update")).StatusCode);

        var admin = await app.SignedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/auto-update", new { enabled = false, intervalHours = 0, platforms = new[] { "arm64" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/auto-update", new { enabled = false, intervalHours = 6, platforms = new[] { "sparc" } })).StatusCode);
        var saved = (await (await admin.PutAsJsonAsync("/api/settings/auto-update", new { enabled = false, intervalHours = 12, platforms = new[] { "arm64", "x86", "arm64" } }))
            .Content.ReadFromJsonAsync<AutoView>())!;
        Assert.Equal((false, 12), (saved.Enabled, saved.IntervalHours));
        Assert.Equal(["x86", "arm64"], saved.Platforms);
    }

    [Fact]
    public void Changed_files_map_to_ports()
    {
        string[] recipes = ["media-libs/openexr/openexr30-3.0.5.recipe", "media-libs/openexr/openexr-2.5.recipe", "sys-libs/zlib/zlib-1.3.2.recipe"];
        Assert.Equal(["openexr", "openexr30", "zlib"], AutoUpdater.PortsForFiles(
            ["media-libs/openexr/patches/openexr-2.5.patchset", "sys-libs/zlib/zlib-1.3.3.recipe", "README.md", "sys-libs/zlib/licenses/x"], recipes));
        Assert.Equal(["zlib"], AutoUpdater.PortsForFiles(["sys-libs/zlib/additional-files/zlib.pc"], recipes));
    }
}
