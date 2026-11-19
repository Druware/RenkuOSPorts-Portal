using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Cookie sign-in for the SPA: local password (with optional TOTP second factor), self-registration, and optional external (GitHub) login.</summary>
public static class AuthEndpoints
{
    public record LoginRequest(string Email, string Password);
    public record TwoFactorRequest(string? Code, string? RecoveryCode);
    public record RegisterRequest(string? Email, string? Password, string? DisplayName);
    public record ConfirmEmailRequest(string? UserId, string? Token);
    public record EmailRequest(string? Email);
    public record ResetPasswordRequest(string? Email, string? Token, string? Password);
    public record CodeRequest(string? Code);
    public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    private const string Issuer = "RenkuOS Ports";
    private const int RecoveryCodeCount = 10;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = string.IsNullOrEmpty(req.Email) ? null : await users.FindByEmailAsync(req.Email);
            if (user is null) return Results.Problem(statusCode: 401, title: "Invalid email or password");
            var res = await signIn.PasswordSignInAsync(user, req.Password ?? "", isPersistent: true, lockoutOnFailure: true);
            if (res.IsNotAllowed)
            {
                // Identity refuses before checking the password: say why only to someone who knows it, and count misses toward lockout.
                if (await users.IsLockedOutAsync(user)) return Results.Problem(statusCode: 401, title: "Account locked, try again later");
                if (!await users.CheckPasswordAsync(user, req.Password ?? ""))
                {
                    await users.AccessFailedAsync(user);
                    return Results.Problem(statusCode: 401, title: "Invalid email or password");
                }
                return Results.Problem(statusCode: 403, title: user.Disabled ? "Account disabled" : "Confirm your email address first");
            }
            if (res.RequiresTwoFactor) return Results.Ok(new { TwoFactorRequired = true });
            if (!res.Succeeded) return Results.Problem(statusCode: 401, title: res.IsLockedOut ? "Account locked, try again later" : "Invalid email or password");
            return Results.Ok(await Me(user, users));
        }).RequireRateLimiting("auth");

        g.MapPost("/login/2fa", async (TwoFactorRequest req, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code) == string.IsNullOrWhiteSpace(req.RecoveryCode))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Give either a code or a recovery code."] });
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            if (user is null) return Results.Problem(statusCode: 401, title: "Invalid code");
            var res = req.Code is { Length: > 0 } code
                ? await signIn.TwoFactorAuthenticatorSignInAsync(Strip(code), isPersistent: true, rememberClient: false)
                : await signIn.TwoFactorRecoveryCodeSignInAsync(Strip(req.RecoveryCode!));
            return res.Succeeded ? Results.Ok(await Me(user, users)) : Results.Problem(statusCode: 401, title: "Invalid code");
        }).RequireRateLimiting("auth");

        g.MapGet("/registration", (IConfiguration cfg) => new { Enabled = RegistrationEnabled(cfg) });

        // 202 whether or not the address is taken, so the response does not reveal who has an account.
        g.MapPost("/register", async (RegisterRequest req, IConfiguration cfg, UserManager<AppUser> users, INotifier mail, ILoggerFactory logs) =>
        {
            if (!RegistrationEnabled(cfg)) return Results.NotFound();
            var email = req.Email?.Trim() ?? "";
            var name = req.DisplayName?.Trim() ?? "";
            var errors = new Dictionary<string, string[]>();
            if (email.Length > 256 || !new EmailAddressAttribute().IsValid(email)) errors["email"] = ["A valid email address is required."];
            if (name.Length is 0 or > 100) errors["displayName"] = ["Display name is required (at most 100 characters)."];
            var weak = await PasswordErrorsAsync(users, req.Password ?? "");
            if (weak.Length > 0) errors["password"] = weak;
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var log = logs.CreateLogger(nameof(AuthEndpoints));
            if (await users.FindByEmailAsync(email) is { } existing)
            {
                await SendAsync(mail, log, existing.Email!, "[RenkuOS Ports] Someone tried to register with your address",
                    $"Someone tried to create a RenkuOS Ports account with {existing.Email}, which already has one.\n\n" +
                    $"If it was you, sign in instead, or reset your password: {BaseUrl(cfg)}/signin\nIf not, you can ignore this email.");
                return Results.Accepted(value: new { });
            }
            var user = new AppUser { UserName = email, Email = email, DisplayName = name };
            var created = await users.CreateAsync(user, req.Password!);
            // A duplicate here is a concurrent registration of the same address; answer as for a taken one.
            if (created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")) return Results.Accepted(value: new { });
            if (!created.Succeeded) return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = created.Errors.Select(e => e.Description).ToArray() });
            await users.AddToRoleAsync(user, "Builder");
            log.LogInformation("Registered {Email}", email);
            await SendConfirmationAsync(user, users, mail, log, cfg);
            return Results.Accepted(value: new { });
        }).RequireRateLimiting("register");

        g.MapPost("/confirm-email", async (ConfirmEmailRequest req, UserManager<AppUser> users) =>
        {
            var user = string.IsNullOrEmpty(req.UserId) ? null : await users.FindByIdAsync(req.UserId);
            if (user is null || string.IsNullOrEmpty(req.Token) || !(await users.ConfirmEmailAsync(user, req.Token)).Succeeded) return InvalidLink();
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        g.MapPost("/resend-confirmation", async (EmailRequest req, IConfiguration cfg, UserManager<AppUser> users, INotifier mail, ILoggerFactory logs) =>
        {
            if (await FindActiveAsync(users, req.Email) is { EmailConfirmed: false } user)
                await SendConfirmationAsync(user, users, mail, logs.CreateLogger(nameof(AuthEndpoints)), cfg);
            return Results.Accepted(value: new { });
        }).RequireRateLimiting("auth");

        g.MapPost("/forgot-password", async (EmailRequest req, IConfiguration cfg, UserManager<AppUser> users, INotifier mail, ILoggerFactory logs) =>
        {
            if (await FindActiveAsync(users, req.Email) is { EmailConfirmed: true } user)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                await SendAsync(mail, logs.CreateLogger(nameof(AuthEndpoints)), user.Email!, "[RenkuOS Ports] Reset your password",
                    $"To choose a new password for your RenkuOS Ports account, open this link:\n\n" +
                    $"{BaseUrl(cfg)}/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(token)}\n\n" +
                    "If you did not ask for this, you can ignore this email.");
            }
            return Results.Accepted(value: new { });
        }).RequireRateLimiting("auth");

        g.MapPost("/reset-password", async (ResetPasswordRequest req, UserManager<AppUser> users) =>
        {
            var user = string.IsNullOrEmpty(req.Email) ? null : await users.FindByEmailAsync(req.Email);
            if (user is null || string.IsNullOrEmpty(req.Token)) return InvalidLink();
            var res = await users.ResetPasswordAsync(user, req.Token, req.Password ?? "");
            if (res.Errors.Any(e => e.Code == "InvalidToken")) return InvalidLink();
            if (!res.Succeeded) return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = res.Errors.Select(e => e.Description).ToArray() });
            await users.ResetAccessFailedCountAsync(user);
            await users.SetLockoutEndDateAsync(user, null);
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        var mine = g.MapGroup("").RequireAuthorization();

        mine.MapGet("/mfa", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = (await users.GetUserAsync(principal))!;
            return new { Enabled = user.TwoFactorEnabled, RecoveryCodesLeft = await users.CountRecoveryCodesAsync(user) };
        });

        // A new key each time; MFA stays off until /mfa/enable proves the app holds it.
        mine.MapPost("/mfa/setup", async (ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = (await users.GetUserAsync(principal))!;
            // Swapping the key while on would need no code and break the user's working app.
            if (user.TwoFactorEnabled) return Results.Problem(statusCode: 400, title: "Turn off two-factor sign-in first");
            await users.ResetAuthenticatorKeyAsync(user);
            await signIn.RefreshSignInAsync(user); // the reset changed the security stamp, which would end this session
            var key = (await users.GetAuthenticatorKeyAsync(user))!;
            var issuer = Uri.EscapeDataString(Issuer);
            return Results.Ok(new { SharedKey = key, OtpauthUri = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer={issuer}&digits=6" });
        });

        mine.MapPost("/mfa/enable", async (CodeRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = (await users.GetUserAsync(principal))!;
            if (!await VerifyTotpAsync(users, user, req.Code)) return InvalidCode();
            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
            await signIn.RefreshSignInAsync(user);
            return Results.Ok(new { RecoveryCodes = codes });
        });

        mine.MapPost("/mfa/disable", async (CodeRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = (await users.GetUserAsync(principal))!;
            if (!user.TwoFactorEnabled || !await VerifyCodeAsync(users, user, req.Code)) return InvalidCode();
            await users.SetTwoFactorEnabledAsync(user, false);
            await signIn.RefreshSignInAsync(user);
            return Results.NoContent();
        });

        mine.MapPost("/mfa/recovery-codes", async (CodeRequest req, ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = (await users.GetUserAsync(principal))!;
            if (!user.TwoFactorEnabled || !await VerifyCodeAsync(users, user, req.Code)) return InvalidCode();
            return Results.Ok(new { RecoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) });
        });

        mine.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = (await users.GetUserAsync(principal))!;
            if (!await users.HasPasswordAsync(user)) return Results.Problem(statusCode: 400, title: "No local password; use Forgot password to set one");
            var res = await users.ChangePasswordAsync(user, req.CurrentPassword ?? "", req.NewPassword ?? "");
            if (res.Errors.Any(e => e.Code == "PasswordMismatch")) return Results.Problem(statusCode: 400, title: "Current password is incorrect");
            if (!res.Succeeded) return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = res.Errors.Select(e => e.Description).ToArray() });
            await signIn.RefreshSignInAsync(user);
            return Results.NoContent();
        });

        g.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        });

        g.MapGet("/me", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
            await users.GetUserAsync(principal) is { } user ? Results.Ok(await Me(user, users)) : Results.Unauthorized());

        g.MapGet("/providers", async (SignInManager<AppUser> signIn) =>
            (await signIn.GetExternalAuthenticationSchemesAsync()).Select(s => s.Name));

        g.MapGet("/external/{provider}", async (string provider, string? returnUrl, SignInManager<AppUser> signIn) =>
        {
            if (!(await signIn.GetExternalAuthenticationSchemesAsync()).Any(s => s.Name == provider)) return Results.NotFound();
            var redirect = "/api/auth/external-callback?returnUrl=" + Uri.EscapeDataString(LocalUrl(returnUrl));
            return Results.Challenge(signIn.ConfigureExternalAuthenticationProperties(provider, redirect), [provider]);
        });

        // Not part of the contract's surface for the SPA: the OAuth handler redirects here after the provider's callback.
        g.MapGet("/external-callback", async (string? returnUrl, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            var info = await signIn.GetExternalLoginInfoAsync();
            if (info is null) return Results.Redirect("/signin?error=external");
            var target = LocalUrl(returnUrl);

            var res = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
            if (res.Succeeded) return Results.Redirect(target);
            if (res.IsLockedOut || res.IsNotAllowed) return Results.Redirect("/signin?error=locked");

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(email)) return Results.Redirect("/signin?error=noemail");
            // An existing local account with this email is not linked automatically: that would let whoever controls
            // the provider account take over the local one.
            if (await users.FindByEmailAsync(email) is not null) return Results.Redirect("/signin?error=exists");

            var user = new AppUser
            {
                UserName = email, Email = email, EmailConfirmed = true,
                DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name) ?? info.Principal.FindFirstValue("urn:github:login") ?? email,
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded) return Results.Redirect("/signin?error=create");
            await users.AddToRoleAsync(user, "Builder");
            await users.AddLoginAsync(user, info);
            await signIn.SignInAsync(user, isPersistent: true, info.LoginProvider);
            return Results.Redirect(target);
        });
    }

    public static async Task<MeDto> Me(AppUser u, UserManager<AppUser> users) =>
        new(u.Id, u.Email ?? "", u.DisplayName, await users.GetRolesAsync(u), u.TwoFactorEnabled);

    private static bool RegistrationEnabled(IConfiguration cfg) => cfg.GetValue("Auth:AllowRegistration", true);

    /// <summary>The public site address for links in emails; never the request's Host, which a caller controls.</summary>
    private static string BaseUrl(IConfiguration cfg) => (cfg["RepoIndex:PublicBaseUrl"] ?? "http://localhost:8080").TrimEnd('/');

    private static IResult InvalidLink() => Results.Problem(statusCode: 400, title: "Invalid or expired link");
    private static IResult InvalidCode() => Results.Problem(statusCode: 400, title: "Invalid code");

    /// <summary>Authenticator apps show codes as "123 456"; recovery codes keep their hyphen.</summary>
    private static string Strip(string code) => code.Replace(" ", "");

    private static async Task<AppUser?> FindActiveAsync(UserManager<AppUser> users, string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim()) is { Disabled: false } u ? u : null;

    private static async Task<string[]> PasswordErrorsAsync(UserManager<AppUser> users, string password)
    {
        var errors = new List<string>();
        foreach (var v in users.PasswordValidators)
            errors.AddRange((await v.ValidateAsync(users, new AppUser(), password)).Errors.Select(e => e.Description));
        return [.. errors];
    }

    private static async Task<bool> VerifyTotpAsync(UserManager<AppUser> users, AppUser user, string? code) =>
        !string.IsNullOrWhiteSpace(code) && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Strip(code));

    /// <summary>A current authenticator code, or a recovery code (which is then used up).</summary>
    private static async Task<bool> VerifyCodeAsync(UserManager<AppUser> users, AppUser user, string? code) =>
        await VerifyTotpAsync(users, user, code) || (!string.IsNullOrWhiteSpace(code) && (await users.RedeemTwoFactorRecoveryCodeAsync(user, Strip(code))).Succeeded);

    private static async Task SendConfirmationAsync(AppUser user, UserManager<AppUser> users, INotifier mail, ILogger log, IConfiguration cfg)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await SendAsync(mail, log, user.Email!, "[RenkuOS Ports] Confirm your email address",
            $"Welcome to RenkuOS Ports, {user.DisplayName}. To confirm your email address and activate your account, open this link:\n\n" +
            $"{BaseUrl(cfg)}/confirm-email?userId={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}\n\n" +
            "If you did not create this account, you can ignore this email.");
    }

    /// <summary>Sends one email; a delivery failure is logged rather than failing the request (the user can ask again).</summary>
    private static async Task SendAsync(INotifier mail, ILogger log, string to, string subject, string body)
    {
        try
        {
            await mail.SendAsync([to], subject, body);
        }
        catch (Exception e)
        {
            log.LogError(e, "Sending '{Subject}' failed", subject);
        }
    }

    /// <summary>Only same-site relative paths, so the login flow cannot be used as an open redirect.</summary>
    private static string LocalUrl(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}

/// <summary>
/// Identity's account confirmation (<c>SignIn.RequireConfirmedAccount</c>) as "not disabled", so a disabled user fails
/// every sign-in route (password, second factor, external login) in <see cref="SignInManager{TUser}.CanSignInAsync"/>.
/// </summary>
public class NotDisabledConfirmation : IUserConfirmation<AppUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<AppUser> manager, AppUser user) => Task.FromResult(!user.Disabled);
}
