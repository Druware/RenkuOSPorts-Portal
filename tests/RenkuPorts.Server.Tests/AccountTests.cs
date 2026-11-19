using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Tests;

/// <summary>Self-registration, password reset, TOTP second factor, and admin user management (roles, disabling).</summary>
[Collection("Database")]
public class AccountTests(AppFactory app) : IClassFixture<AppFactory>
{
    private const string Password = "Account_Pass1!";

    private HttpClient Anon(WebApplicationFactory<Program>? f = null) =>
        (f ?? app).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static string NewEmail() => $"acct-{Guid.NewGuid():N}@renkuos.local";

    private static Task<HttpResponseMessage> LoginAsync(HttpClient http, string email, string password = Password) =>
        http.PostAsJsonAsync("/api/auth/login", new { email, password });

    /// <summary>The last mail sent to <paramref name="to"/>, and the query values of its link to <paramref name="path"/>.</summary>
    private Dictionary<string, string> LinkIn(string to, string path)
    {
        var mail = app.Mail.Sent.Last(m => m.To.Contains(to));
        var m = Regex.Match(mail.Body, $@"http://localhost:8080/{path}\?(\S+)");
        Assert.True(m.Success, mail.Body);
        return m.Groups[1].Value.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    /// <summary>A confirmed Builder created through Identity; its id.</summary>
    private async Task<string> UserAsync(string email)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = new AppUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Acct" };
        Assert.True((await users.CreateAsync(u, Password)).Succeeded);
        await users.AddToRoleAsync(u, "Builder");
        return u.Id;
    }

    private async Task<string> UserIdAsync(string email)
    {
        using var scope = app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email))!.Id;
    }

    [Fact]
    public async Task Register_confirm_then_sign_in()
    {
        var email = NewEmail();
        var anon = Anon();
        Assert.True((await anon.GetFromJsonAsync<JsonElement>("/api/auth/registration")).GetProperty("enabled").GetBoolean());
        var reg = await anon.PostAsJsonAsync("/api/auth/register", new { email, password = Password, displayName = "New One" });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);

        var before = await LoginAsync(anon, email);
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Equal("Confirm your email address first", (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        // Without the password, nothing about the account is revealed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(anon, email, "Wrong_Pass1!")).StatusCode);

        // Resend mails a fresh link; either confirms.
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/auth/resend-confirmation", new { email })).StatusCode);
        var link = LinkIn(email, "confirm-email");
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/confirm-email", new { userId = link["userId"], token = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/auth/confirm-email", new { userId = link["userId"], token = link["token"] })).StatusCode);

        var login = await LoginAsync(anon, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var me = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New One", me.GetProperty("displayName").GetString());
        Assert.Equal(["Builder"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.False(me.GetProperty("twoFactorEnabled").GetBoolean());
    }

    [Fact]
    public async Task Register_with_a_taken_address_or_bad_input()
    {
        var email = NewEmail();
        await UserAsync(email);
        var sent = app.Mail.Sent.Count;
        var anon = Anon();
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/auth/register", new { email = email.ToUpperInvariant(), password = Password, displayName = "Again" })).StatusCode);
        Assert.Equal(sent + 1, app.Mail.Sent.Count);
        Assert.Contains("already has one", app.Mail.Sent.Last().Body);
        Assert.Contains("http://localhost:8080/signin", app.Mail.Sent.Last().Body);
        await app.WithDbAsync(async db => Assert.Equal(1, db.Users.Count(u => u.NormalizedEmail == email.ToUpperInvariant())));

        var bad = await anon.PostAsJsonAsync("/api/auth/register", new { email = "not-an-email", password = "short", displayName = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var key in new[] { "email", "password", "displayName" }) Assert.True(errors.TryGetProperty(key, out _), key);
    }

    [Fact]
    public async Task Registration_can_be_switched_off()
    {
        using var closed = app.WithWebHostBuilder(b => b.UseSetting("Auth:AllowRegistration", "false"));
        var anon = Anon(closed);
        Assert.False((await anon.GetFromJsonAsync<JsonElement>("/api/auth/registration")).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsJsonAsync("/api/auth/register", new { email = NewEmail(), password = Password, displayName = "X" })).StatusCode);
    }

    [Fact]
    public async Task Forgot_and_reset_password()
    {
        var email = NewEmail();
        await UserAsync(email);
        var anon = Anon();
        var sent = app.Mail.Sent.Count;
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email = NewEmail() })).StatusCode);
        Assert.Equal(sent, app.Mail.Sent.Count);
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email })).StatusCode);
        var link = LinkIn(email, "reset-password");
        Assert.Equal(email, link["email"]);

        var bad = await anon.PostAsJsonAsync("/api/auth/reset-password", new { email, token = "bogus", password = "New_Pass1!" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("Invalid or expired link", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/reset-password", new { email, token = link["token"], password = "weak" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/auth/reset-password", new { email, token = link["token"], password = "New_Pass1!" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(anon, email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(anon, email, "New_Pass1!")).StatusCode);

        // Signed in: change it again.
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/password", new { currentPassword = "Wrong_Pass1!", newPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/auth/password", new { currentPassword = "New_Pass1!", newPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Anon(), email)).StatusCode);
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) for a base32 key, at the given step offset from now.</summary>
    private static string Totp(string base32Key, int stepOffset = 0)
    {
        var bits = string.Concat(base32Key.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(key, counter);
        var o = hash[^1] & 0x0f;
        var code = ((hash[o] & 0x7f) << 24 | hash[o + 1] << 16 | hash[o + 2] << 8 | hash[o + 3]) % 1_000_000;
        return code.ToString("D6");
    }

    [Fact]
    public async Task Authenticator_second_factor_and_recovery_codes()
    {
        var email = NewEmail();
        await UserAsync(email);
        var http = await app.SignedInAsync(email, Password);
        var setup = await (await http.PostAsync("/api/auth/mfa/setup", null)).Content.ReadFromJsonAsync<JsonElement>();
        var key = setup.GetProperty("sharedKey").GetString()!;
        Assert.StartsWith($"otpauth://totp/RenkuOS%20Ports:{Uri.EscapeDataString(email)}?secret={key}&issuer=RenkuOS%20Ports", setup.GetProperty("otpauthUri").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/auth/mfa/enable", new { code = "000000" == Totp(key) ? "111111" : "000000" })).StatusCode);
        var enabled = await http.PostAsJsonAsync("/api/auth/mfa/enable", new { code = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var codes = (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, codes.Count);
        // The session survives the security-stamp changes of setup and enable.
        var mfa = await http.GetFromJsonAsync<JsonElement>("/api/auth/mfa");
        Assert.True(mfa.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, mfa.GetProperty("recoveryCodesLeft").GetInt32());
        // While on, the key cannot be swapped.
        var again = await http.PostAsync("/api/auth/mfa/setup", null);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("Turn off two-factor sign-in first", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

        // Password alone is not enough now.
        var anon = Anon();
        var first = await (await LoginAsync(anon, email)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(first.GetProperty("twoFactorRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/login/2fa", new { code = "1", recoveryCode = "2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login/2fa", new { recoveryCode = "nope-nope" })).StatusCode);
        var second = await anon.PostAsJsonAsync("/api/auth/login/2fa", new { code = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("twoFactorEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/auth/me")).StatusCode);

        // A recovery code works once.
        var viaRecovery = Anon();
        await LoginAsync(viaRecovery, email);
        Assert.Equal(HttpStatusCode.OK, (await viaRecovery.PostAsJsonAsync("/api/auth/login/2fa", new { recoveryCode = codes[0] })).StatusCode);
        var reuse = Anon();
        await LoginAsync(reuse, email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reuse.PostAsJsonAsync("/api/auth/login/2fa", new { recoveryCode = codes[0] })).StatusCode);
        Assert.Equal(9, (await http.GetFromJsonAsync<JsonElement>("/api/auth/mfa")).GetProperty("recoveryCodesLeft").GetInt32());

        var regenerated = await http.PostAsJsonAsync("/api/auth/mfa/recovery-codes", new { code = Totp(key) });
        Assert.Equal(10, (await regenerated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/auth/mfa/disable", new { code = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("/api/auth/mfa/disable", new { code = Totp(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Anon(), email)).StatusCode);
    }

    private record UserView(string Id, string Email, List<string> Roles, bool EmailConfirmed, bool TwoFactorEnabled, bool Disabled, int ClientCount);
    private record UserPage(List<UserView> Items, int Total, int Page, int PageSize);

    [Fact]
    public async Task Admins_promote_and_demote_but_never_the_last_admin_or_themselves()
    {
        var admin = await app.SignedInAsync();
        var adminId = await UserIdAsync(AppFactory.AdminEmail);
        var email = NewEmail();
        var id = await UserAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.SignedInAsync(email, Password)).GetAsync("/api/admin/users")).StatusCode);
        var page = await admin.GetFromJsonAsync<UserPage>($"/api/admin/users?search={Uri.EscapeDataString(email)}");
        var row = Assert.Single(page!.Items);
        Assert.Equal((id, "Builder", true, false, 0), (row.Id, Assert.Single(row.Roles), row.EmailConfirmed, row.Disabled, row.ClientCount));

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/users/{adminId}/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{id}/admin", null)).StatusCode);
        var other = await app.SignedInAsync(email, Password);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/admin/users")).StatusCode);

        // The new admin may demote the seeded one; then the new admin is the last and cannot be demoted.
        Assert.Equal(HttpStatusCode.NoContent, (await other.DeleteAsync($"/api/admin/users/{adminId}/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/users/{id}/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await other.PostAsync($"/api/admin/users/{adminId}/admin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{id}/admin")).StatusCode);
        Assert.Equal(["Builder"], (await admin.GetFromJsonAsync<UserPage>($"/api/admin/users?search={Uri.EscapeDataString(email)}"))!.Items.Single().Roles);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/users/nope/admin", null)).StatusCode);
    }

    [Fact]
    public async Task Disabling_ends_sign_in_sessions_and_build_clients_until_enabled()
    {
        var admin = await app.SignedInAsync();
        var adminId = await UserIdAsync(AppFactory.AdminEmail);
        var email = NewEmail();
        var id = await UserAsync(email);
        var session = await app.SignedInAsync(email, Password);
        var (client, bearer) = await app.BuildClientAsync(session);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/client/me")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/users/{adminId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{id}/disable", null)).StatusCode);
        Assert.True((await admin.GetFromJsonAsync<UserPage>($"/api/admin/users?search={Uri.EscapeDataString(email)}"))!.Items.Single().Disabled);

        var login = await LoginAsync(Anon(), email);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("Account disabled", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await Anon().PostAsJsonAsync("/api/auth/forgot-password", new { email })).StatusCode);
        Assert.DoesNotContain(app.Mail.Sent, m => m.To.Contains(email));
        // The existing cookie stops working once the security stamp is next checked.
        app.Clock.Offset = TimeSpan.FromMinutes(2);
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/client/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsJsonAsync("/api/client/token", new { clientId = client.Id, secret = client.Secret })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Anon(), email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/client/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().PostAsJsonAsync("/api/client/token", new { clientId = client.Id, secret = client.Secret })).StatusCode);
    }

    [Fact]
    public async Task Clients_list_is_own_only_unless_an_admin_asks_for_all()
    {
        var admin = await app.SignedInAsync();
        var builder = await app.BuilderAsync();
        var (mine, _) = await app.BuildClientAsync(builder);
        var (theirs, _) = await app.BuildClientAsync(admin);
        async Task<List<Guid>> Ids(HttpClient h, string url) =>
            (await h.GetFromJsonAsync<List<JsonElement>>(url))!.Select(c => c.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([mine.Id], await Ids(builder, "/api/clients"));
        Assert.Equal([mine.Id], await Ids(builder, "/api/clients?all=true"));
        Assert.DoesNotContain(mine.Id, await Ids(admin, "/api/clients"));
        Assert.Contains(theirs.Id, await Ids(admin, "/api/clients"));
        Assert.Superset(new HashSet<Guid> { mine.Id, theirs.Id }, (await Ids(admin, "/api/clients?all=true")).ToHashSet());
        // An admin still deletes anyone's client.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/clients/{mine.Id}")).StatusCode);
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited_per_client()
    {
        using var limited = app.WithWebHostBuilder(b => b.UseSetting("RateLimit:Auth:PermitLimit", "2").UseSetting("RateLimit:Register:PermitLimit", "1"));
        var anon = Anon(limited);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email = NewEmail() })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email = NewEmail() })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(anon, NewEmail())).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/auth/register", new { email = NewEmail(), password = Password, displayName = "R" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.PostAsJsonAsync("/api/auth/register", new { email = NewEmail(), password = Password, displayName = "R" })).StatusCode);
    }
}
