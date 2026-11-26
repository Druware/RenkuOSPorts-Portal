using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Endpoints;

/// <summary>
/// Personal API tokens (<see cref="ApiToken"/>) for automated issue work. Managed by the signed-in admin (cookie only, so a token can
/// never mint or list tokens) under <c>/api/admin/tokens</c>; accepted only by the <c>IssueWorker</c> policy.
/// </summary>
public static class ApiTokenEndpoints
{
    public const string Scheme = "ApiToken";
    public const string Marker = "rpat_";
    public const string IssuesScope = "issues";
    /// <summary>Claim on a token principal holding the token's name.</summary>
    public const string TokenClaim = "api_token";
    public const int MaxActive = 10;

    public record CreateTokenRequest(string Name, int? ExpiresInDays);
    public record ApiTokenDto(long Id, string Name, string Prefix, string Scope, DateTime CreatedAt, DateTime ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt);

    /// <summary>Who acted, for notes and logs: the user name, plus <c>via token '…'</c> when a token was used. At most 256 characters (the ResolvedBy column).</summary>
    public static string By(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name ?? "admin";
        var by = user.FindFirstValue(TokenClaim) is { } token ? $"{name} via token '{token}'" : name;
        return by.Length > 256 ? by[..256] : by;
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/tokens").RequireAuthorization("Admin");

        g.MapGet("", async (ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db) =>
        {
            var id = users.GetUserId(user);
            return await db.ApiTokens.Where(t => t.UserId == id).OrderByDescending(t => t.CreatedAt)
                .Select(t => new ApiTokenDto(t.Id, t.Name, t.Prefix, t.Scope, t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt)).ToListAsync();
        });

        g.MapPost("", async (CreateTokenRequest req, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db, TimeProvider time, ILoggerFactory logs) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required (at most 100 characters)."] });
            var days = req.ExpiresInDays ?? 30;
            if (days is < 1 or > 90)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["expiresInDays"] = ["Expiry is 1 to 90 days."] });
            var userId = users.GetUserId(user)!;
            var now = time.GetUtcNow().UtcDateTime;
            if (await db.ApiTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now) >= MaxActive)
                return Results.Problem(statusCode: 409, title: "Too many tokens", detail: $"At most {MaxActive} active tokens; revoke one first.");

            var secret = ClientsEndpoints.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var token = new ApiToken
            {
                UserId = userId, Name = name, Hash = ClientsEndpoints.HashSecret(Marker + secret), Prefix = secret[..8], Scope = IssuesScope,
                CreatedAt = now, ExpiresAt = now.AddDays(days),
            };
            db.ApiTokens.Add(token);
            await db.SaveChangesAsync();
            logs.CreateLogger(nameof(ApiTokenEndpoints)).LogInformation("{User} created API token {Token} ({Id}), expiring {ExpiresAt:u}", user.Identity?.Name, name, token.Id, token.ExpiresAt);
            return Results.Ok(new
            {
                Token = Marker + secret,
                Info = new ApiTokenDto(token.Id, token.Name, token.Prefix, token.Scope, token.CreatedAt, token.ExpiresAt, null, null),
            });
        });

        g.MapDelete("/{id:long}", async (long id, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db, TimeProvider time, ILoggerFactory logs) =>
        {
            var userId = users.GetUserId(user);
            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);
            if (token is null) return Results.NotFound();
            if (token.RevokedAt is null)
            {
                token.RevokedAt = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync();
                logs.CreateLogger(nameof(ApiTokenEndpoints)).LogInformation("{User} revoked API token {Token} ({Id})", user.Identity?.Name, token.Name, id);
            }
            return Results.NoContent();
        });
    }
}

/// <summary>
/// Authenticates <c>Authorization: Bearer rpat_…</c> as the token's user, with claims <c>scope</c> and <see cref="ApiTokenEndpoints.TokenClaim"/>.
/// Other bearer values get no result, so the build-client JWT path is untouched. The user is checked on every request: confirmed, not
/// disabled or locked out (as at sign-in), and still an admin.
/// </summary>
public class ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    AppDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        const string bearer = "Bearer ";
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(bearer + ApiTokenEndpoints.Marker, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        // Looked up by hash: the stored value is a digest, so the comparison leaks nothing about the token.
        var hash = ClientsEndpoints.HashSecret(header[bearer.Length..].Trim());
        var now = time.GetUtcNow().UtcDateTime;
        var token = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Hash == hash);
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now) return AuthenticateResult.Fail("Unknown, revoked or expired API token.");
        var user = await users.FindByIdAsync(token.UserId);
        if (user is null || await users.IsLockedOutAsync(user) || !await signIn.CanSignInAsync(user) || !await users.IsInRoleAsync(user, "Admin"))
            return AuthenticateResult.Fail("The API token's user cannot sign in or is not an admin.");

        if (token.LastUsedAt is not { } last || now - last > LastUsedInterval)
            await db.ApiTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now));

        var identity = new ClaimsIdentity((await signIn.CreateUserPrincipalAsync(user)).Claims, Scheme.Name);
        identity.AddClaim(new Claim("scope", token.Scope));
        identity.AddClaim(new Claim(ApiTokenEndpoints.TokenClaim, token.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
