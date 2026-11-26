using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Tests;

/// <summary>Personal API tokens: issues-scoped, accepted on the build-issue endpoints only, and only while their user is a usable admin.</summary>
public partial class ApiTests
{
    private record TokenInfoView(long Id, string Name, string Prefix, string Scope, DateTime CreatedAt, DateTime ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt);
    private record CreatedTokenView(string Token, TokenInfoView Info);

    private static async Task<CreatedTokenView> CreateTokenAsync(HttpClient admin, string name = "agent", int? days = null)
    {
        var res = await admin.PostAsJsonAsync("/api/admin/tokens", new { name, expiresInDays = days });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CreatedTokenView>())!;
    }

    private HttpClient WithToken(string token)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>A second confirmed admin, signed in, and their id (so a test can take the role away without touching the seeded admin).</summary>
    private async Task<(HttpClient Http, string Id)> OtherAdminAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@renkuos.local";
        const string password = "OtherAdmin_Pass1!";
        string id;
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = new AppUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Other admin" };
            Assert.True((await users.CreateAsync(u, password)).Succeeded);
            await users.AddToRoleAsync(u, "Admin");
            id = u.Id;
        }
        return (await app.SignedInAsync(email, password), id);
    }

    [Fact]
    public async Task Token_creation_validates_returns_the_token_once_and_stores_only_its_hash()
    {
        var admin = await app.SignedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/tokens", new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/tokens", new { name = "x", expiresInDays = 91 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/tokens", new { name = "x", expiresInDays = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsJsonAsync("/api/admin/tokens", new { name = "x" })).StatusCode);

        var created = await CreateTokenAsync(admin, "triage");
        Assert.StartsWith("rpat_", created.Token);
        Assert.Equal(("triage", "issues", created.Token[5..13]), (created.Info.Name, created.Info.Scope, created.Info.Prefix));
        Assert.InRange((created.Info.ExpiresAt - created.Info.CreatedAt).TotalDays, 29.99, 30.01);

        await app.WithDbAsync(async db =>
        {
            var row = await db.ApiTokens.SingleAsync(t => t.Id == created.Info.Id);
            Assert.Equal(Endpoints.ClientsEndpoints.HashSecret(created.Token), row.Hash);
            Assert.DoesNotContain(created.Token[5..], row.Hash + row.Prefix + row.Name);
        });
        var listed = await admin.GetStringAsync("/api/admin/tokens");
        Assert.Contains("triage", listed);
        Assert.DoesNotContain(created.Token, listed);
        Assert.DoesNotContain("hash", listed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_token_works_the_issues_and_is_recorded_as_via_token()
    {
        var admin = await app.SignedInAsync();
        var (_, client) = await app.BuildClientAsync(admin);
        await QueueAsync(admin, "zlib");
        var zlib = await FailAsync(client, "cannot resolve gcc for port zlib-1.3.2");
        await QueueAsync(admin, "foo");
        await FailAsync(client, "foo: configure failed");
        var issues = (await admin.GetFromJsonAsync<List<IssueView>>("/api/issues"))!;
        var gcc = issues.Single(i => i.ExampleJobId == zlib).Id;
        var configure = issues.Single(i => i.ExampleJobId != zlib).Id;

        var created = await CreateTokenAsync(admin, "claude");
        var agent = WithToken(created.Token);
        Assert.Equal(2, (await agent.GetFromJsonAsync<List<IssueView>>("/api/issues"))!.Count);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/issues/{gcc}")).StatusCode);
        Assert.Equal("log", await agent.GetStringAsync($"/api/jobs/{zlib}/log"));

        var escalated = await agent.PostAsJsonAsync($"/api/issues/{configure}/escalate", new { note = "needs a human" });
        Assert.Equal(HttpStatusCode.OK, escalated.StatusCode);
        Assert.Contains($"{AppFactory.AdminEmail} via token 'claude' (escalated)", (await escalated.Content.ReadFromJsonAsync<IssueView>())!.Notes);
        Assert.Contains($"{AppFactory.AdminEmail} via token 'claude' escalated", app.Mail.Sent.Last().Body);

        var res = await agent.PostAsJsonAsync($"/api/issues/{gcc}/resolve", new { note = "gcc added", requeue = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var resolved = (await res.Content.ReadFromJsonAsync<ResolvedView>())!;
        Assert.Equal($"{AppFactory.AdminEmail} via token 'claude'", resolved.Issue.ResolvedBy);
        Assert.Equal([zlib], resolved.Requeued);

        // Last used is written on first use, then not again within five minutes.
        DateTime? lastUsed = null;
        await app.WithDbAsync(async db => lastUsed = (await db.ApiTokens.SingleAsync(t => t.Id == created.Info.Id)).LastUsedAt);
        Assert.NotNull(lastUsed);
        await agent.GetAsync("/api/issues");
        await app.WithDbAsync(async db => Assert.Equal(lastUsed, (await db.ApiTokens.SingleAsync(t => t.Id == created.Info.Id)).LastUsedAt));
    }

    [Fact]
    public async Task A_token_is_refused_everywhere_else_and_cannot_manage_tokens()
    {
        var admin = await app.SignedInAsync();
        var agent = WithToken((await CreateTokenAsync(admin)).Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync("/api/jobs", new { portName = "zlib", platforms = new[] { "arm64" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync("/api/admin/reset", new { platform = "arm64", confirm = "arm64" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync("/api/admin/cleanup/logs", new { olderThanDays = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/admin/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync("/api/admin/tokens", new { name = "more" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsync("/api/client/jobs/next", null)).StatusCode);
    }

    [Fact]
    public async Task Revoked_expired_and_garbage_tokens_are_refused_and_build_clients_still_work()
    {
        var admin = await app.SignedInAsync();
        var revoked = await CreateTokenAsync(admin, "revoked");
        Assert.Equal(HttpStatusCode.OK, (await WithToken(revoked.Token).GetAsync("/api/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/tokens/{revoked.Info.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(revoked.Token).GetAsync("/api/issues")).StatusCode);
        Assert.NotNull((await admin.GetFromJsonAsync<List<TokenInfoView>>("/api/admin/tokens"))!.Single(t => t.Id == revoked.Info.Id).RevokedAt);

        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken("rpat_garbage").GetAsync("/api/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken("rpat_").GetAsync("/api/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken("not-a-token").GetAsync("/api/issues")).StatusCode);

        // A build client's JWT is no token here, and the build-client API still takes it (with an rpat_ there too, it just fails).
        var (_, client) = await app.BuildClientAsync(admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/issues")).StatusCode);
        await QueueAsync(admin, "zlib");
        Assert.Equal("zlib", (await ClaimAsync(client)).PortName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken("rpat_garbage").PostAsync("/api/client/jobs/next", null)).StatusCode);

        var expiring = WithToken((await CreateTokenAsync(admin, "expiring", days: 1)).Token);
        Assert.Equal(HttpStatusCode.OK, (await expiring.GetAsync("/api/issues")).StatusCode);
        app.Clock.Offset = TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await expiring.GetAsync("/api/issues")).StatusCode);
    }

    [Fact]
    public async Task A_token_stops_working_when_its_user_loses_admin_or_is_disabled_and_tokens_are_per_user()
    {
        var admin = await app.SignedInAsync();
        var (other, otherId) = await OtherAdminAsync();
        var demoted = await CreateTokenAsync(other, "demoted");
        var agent = WithToken(demoted.Token);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/issues")).StatusCode);

        // Tokens are the caller's own: not listed to, nor revocable by, another admin.
        Assert.DoesNotContain((await admin.GetFromJsonAsync<List<TokenInfoView>>("/api/admin/tokens"))!, t => t.Id == demoted.Info.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/tokens/{demoted.Info.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{otherId}/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/issues")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{otherId}/admin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{otherId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/issues")).StatusCode);
    }

    [Fact]
    public async Task At_most_ten_active_tokens_per_user()
    {
        var (other, _) = await OtherAdminAsync();
        var first = await CreateTokenAsync(other, "t0");
        for (var i = 1; i < 10; i++) await CreateTokenAsync(other, $"t{i}");
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync("/api/admin/tokens", new { name = "t10" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await other.DeleteAsync($"/api/admin/tokens/{first.Info.Id}")).StatusCode);
        await CreateTokenAsync(other, "t10");
    }
}
