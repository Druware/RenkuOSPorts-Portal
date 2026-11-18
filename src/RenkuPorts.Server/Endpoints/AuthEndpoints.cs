using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Cookie sign-in for the SPA: local password and optional external (GitHub) login.</summary>
public static class AuthEndpoints
{
    public record LoginRequest(string Email, string Password);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = string.IsNullOrEmpty(req.Email) ? null : await users.FindByEmailAsync(req.Email);
            if (user is null) return Results.Problem(statusCode: 401, title: "Invalid email or password");
            var res = await signIn.PasswordSignInAsync(user, req.Password ?? "", isPersistent: true, lockoutOnFailure: true);
            if (!res.Succeeded) return Results.Problem(statusCode: 401, title: res.IsLockedOut ? "Account locked, try again later" : "Invalid email or password");
            return Results.Ok(await Me(user, users));
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
            if (info is null) return Results.Redirect("/login?error=external");
            var target = LocalUrl(returnUrl);

            var res = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
            if (res.Succeeded) return Results.Redirect(target);
            if (res.IsLockedOut || res.IsNotAllowed) return Results.Redirect("/login?error=locked");

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(email)) return Results.Redirect("/login?error=noemail");
            // An existing local account with this email is not linked automatically: that would let whoever controls
            // the provider account take over the local one.
            if (await users.FindByEmailAsync(email) is not null) return Results.Redirect("/login?error=exists");

            var user = new AppUser
            {
                UserName = email, Email = email, EmailConfirmed = true,
                DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name) ?? info.Principal.FindFirstValue("urn:github:login") ?? email,
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded) return Results.Redirect("/login?error=create");
            await users.AddToRoleAsync(user, "Builder");
            await users.AddLoginAsync(user, info);
            await signIn.SignInAsync(user, isPersistent: true, info.LoginProvider);
            return Results.Redirect(target);
        });
    }

    public static async Task<MeDto> Me(AppUser u, UserManager<AppUser> users) =>
        new(u.Id, u.Email ?? "", u.DisplayName, await users.GetRolesAsync(u));

    /// <summary>Only same-site relative paths, so the login flow cannot be used as an open redirect.</summary>
    private static string LocalUrl(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}
