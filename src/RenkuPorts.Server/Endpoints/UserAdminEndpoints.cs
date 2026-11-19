using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Admin-only user management: list users, grant or remove the Admin role, disable or enable accounts.</summary>
public static class UserAdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/users").RequireAuthorization("Admin");

        g.MapGet("", async (string? search, int? page, int? pageSize, AppDbContext db) =>
        {
            var q = db.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(u => u.Email!.Contains(search) || u.DisplayName.Contains(search));
            return await Paged<UserDto>.FromAsync(q, o => o.OrderBy(u => u.Email).ThenBy(u => u.Id),
                u => new UserDto(u.Id, u.Email ?? "", u.DisplayName,
                    (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == u.Id orderby r.Name select r.Name!).ToList(),
                    u.EmailConfirmed, u.TwoFactorEnabled, u.Disabled, db.BuildClients.Count(c => c.OwnerId == u.Id)),
                page, pageSize);
        });

        g.MapPost("/{id}/admin", async (string id, ClaimsPrincipal caller, UserManager<AppUser> users, ILoggerFactory logs) =>
        {
            if (await users.FindByIdAsync(id) is not { } user) return Results.NotFound();
            if (!await users.IsInRoleAsync(user, "Admin")) await users.AddToRoleAsync(user, "Admin");
            Log(logs).LogInformation("{User} made {Target} an admin", caller.Identity?.Name, user.Email);
            return Results.NoContent();
        });

        g.MapDelete("/{id}/admin", async (string id, ClaimsPrincipal caller, UserManager<AppUser> users, ILoggerFactory logs) =>
        {
            if (await users.FindByIdAsync(id) is not { } user) return Results.NotFound();
            if (user.Id == users.GetUserId(caller)) return Results.Problem(statusCode: 409, title: "You cannot remove your own admin role");
            if (!await users.IsInRoleAsync(user, "Admin")) return Results.NoContent();
            // Disabled admins cannot sign in, so they do not count toward keeping one.
            if (!(await users.GetUsersInRoleAsync("Admin")).Any(a => a.Id != user.Id && !a.Disabled))
                return Results.Problem(statusCode: 409, title: "The last admin cannot be demoted");
            await users.RemoveFromRoleAsync(user, "Admin");
            Log(logs).LogInformation("{User} removed the admin role from {Target}", caller.Identity?.Name, user.Email);
            return Results.NoContent();
        });

        g.MapPost("/{id}/disable", async (string id, ClaimsPrincipal caller, UserManager<AppUser> users, ILoggerFactory logs) =>
        {
            if (await users.FindByIdAsync(id) is not { } user) return Results.NotFound();
            if (user.Id == users.GetUserId(caller)) return Results.Problem(statusCode: 409, title: "You cannot disable your own account");
            user.Disabled = true;
            if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) return Changed(); // saves Disabled too, and ends the user's existing sessions
            Log(logs).LogInformation("{User} disabled {Target}", caller.Identity?.Name, user.Email);
            return Results.NoContent();
        });

        g.MapPost("/{id}/enable", async (string id, ClaimsPrincipal caller, UserManager<AppUser> users, ILoggerFactory logs) =>
        {
            if (await users.FindByIdAsync(id) is not { } user) return Results.NotFound();
            user.Disabled = false;
            if (!(await users.UpdateAsync(user)).Succeeded) return Changed();
            Log(logs).LogInformation("{User} enabled {Target}", caller.Identity?.Name, user.Email);
            return Results.NoContent();
        });
    }

    private static IResult Changed() => Results.Problem(statusCode: 409, title: "The user was changed meanwhile; try again");

    private static ILogger Log(ILoggerFactory logs) => logs.CreateLogger(nameof(UserAdminEndpoints));
}
