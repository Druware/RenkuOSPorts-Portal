using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>Captures notification emails instead of sending them.</summary>
public class FakeNotifier : INotifier
{
    public ConcurrentQueue<(IReadOnlyCollection<string> To, string Subject, string Body)> Sent { get; } = new();

    public Task SendAsync(IReadOnlyCollection<string> to, string subject, string body)
    {
        Sent.Enqueue((to, subject, body));
        return Task.CompletedTask;
    }
}

/// <summary>
/// The server against the compose SQL Server (database RenkuPortsTests, recreated per run) and Azurite
/// (container packages-test), with a small generated ports tree and a wwwroot holding a stub index.html.
/// Needs <c>docker compose up -d sql azurite</c>; the SA password comes from RENKUPORTS_TEST_SA_PASSWORD or .env.example's default.
/// RENKUPORTS_TEST_SQL_SERVER and RENKUPORTS_TEST_BLOB point a run inside a container at them (tests/run-haiku-tools-test.sh).
/// </summary>
public class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@renkuos.local";
    public const string AdminPassword = "TestAdmin_Pass1!";
    public static readonly string SqlConnection =
        $"Server={Environment.GetEnvironmentVariable("RENKUPORTS_TEST_SQL_SERVER") ?? "localhost,14333"};Database=RenkuPortsTests;User Id=sa;Password={Environment.GetEnvironmentVariable("RENKUPORTS_TEST_SA_PASSWORD") ?? "RenkuPorts_Local1!"};TrustServerCertificate=True";

    public FakeNotifier Mail { get; } = new();
    public string PortsTree { get; }
    private readonly string _webRoot;

    public AppFactory()
    {
        var tmp = Directory.CreateTempSubdirectory("renkuports-tests-").FullName;
        PortsTree = Path.Combine(tmp, "ports");
        foreach (var (dir, file) in new[] { ("sys-libs/zlib", "zlib-1.3.2.recipe"), ("dev-libs/foo", "foo-1.9.recipe"), ("dev-libs/foo", "foo-1.10.recipe"), ("media-libs/openexr", "openexr30-3.0.5.recipe") })
        {
            Directory.CreateDirectory(Path.Combine(PortsTree, dir));
            File.WriteAllText(Path.Combine(PortsTree, dir, file), "SUMMARY=\"test\"\n");
        }
        _webRoot = Directory.CreateDirectory(Path.Combine(tmp, "wwwroot")).FullName;
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>spa</title>");

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(SqlConnection).Options);
        db.Database.EnsureDeleted();
    }

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        b.UseSetting("ConnectionStrings:Sql", SqlConnection);
        b.UseSetting("ConnectionStrings:Blob", Environment.GetEnvironmentVariable("RENKUPORTS_TEST_BLOB") ?? "UseDevelopmentStorage=true");
        b.UseSetting("Blob:Container", "packages-test");
        b.UseSetting("Jwt:Key", "test-only-jwt-key-0123456789abcdef0123456789");
        b.UseSetting("Seed:AdminEmail", AdminEmail);
        b.UseSetting("Seed:AdminPassword", AdminPassword);
        b.UseSetting("PortsTree:Path", PortsTree);
        b.UseSetting("AutoUpdate:Schedule", "false");
        b.UseWebRoot(_webRoot);
        b.ConfigureServices(s =>
        {
            s.RemoveAll<INotifier>();
            s.AddSingleton<INotifier>(Mail);
        });
    }

    /// <summary>A cookie-holding client signed in with the given credentials.</summary>
    public async Task<HttpClient> SignedInAsync(string email = AdminEmail, string password = AdminPassword)
    {
        var http = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        var res = await http.PostAsJsonAsync("/api/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        return http;
    }

    /// <summary>Creates a user with only the Builder role, directly through Identity (there is no sign-up endpoint besides OAuth).</summary>
    public async Task<HttpClient> BuilderAsync()
    {
        var email = $"builder-{Guid.NewGuid():N}@renkuos.local";
        const string password = "Builder_Pass1!";
        using (var scope = Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = new AppUser { UserName = email, Email = email, DisplayName = "Builder" };
            Assert.True((await users.CreateAsync(u, password)).Succeeded);
            await users.AddToRoleAsync(u, "Builder");
        }
        return await SignedInAsync(email, password);
    }

    public record NewClient(Guid Id, string Name, string Platform, string Secret);

    /// <summary>Registers a build client (as the given user) and returns a bearer-token HttpClient for it.</summary>
    public async Task<(NewClient Client, HttpClient Http)> BuildClientAsync(HttpClient owner, string platform = "arm64")
    {
        var res = await owner.PostAsJsonAsync("/api/clients", new { name = $"c-{Guid.NewGuid():N}"[..12], platform });
        res.EnsureSuccessStatusCode();
        var c = (await res.Content.ReadFromJsonAsync<NewClient>())!;
        var tok = await CreateClient().PostAsJsonAsync("/api/client/token", new { clientId = c.Id, secret = c.Secret });
        tok.EnsureSuccessStatusCode();
        var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await tok.Content.ReadFromJsonAsync<Token>())!.AccessToken);
        return (c, http);
    }

    public record Token(string AccessToken, DateTime ExpiresAt);

    /// <summary>Removes all jobs and packages, so each test sees an empty queue.</summary>
    public async Task ResetQueueAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Packages.ExecuteDeleteAsync();
        await db.Jobs.ExecuteDeleteAsync();
        if (!await db.Recipes.AnyAsync()) await RecipeScanner.SyncAsync(db, PortsTree, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
