using Azure.Storage.Blobs;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Build-client registration by signed-in users (own clients; Admin sees all with <c>?all=true</c>).</summary>
public static class ClientsEndpoints
{
    public record CreateClientRequest(string Name, string Platform);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/clients").RequireAuthorization();

        g.MapGet("", async (bool? all, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db) =>
            await Visible(db, user, users, all == true).OrderBy(c => c.Name).Select(Project.Client).ToListAsync());

        g.MapPost("", async (CreateClientRequest req, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required (at most 100 characters)."] });
            if (!Validate.IsPlatform(req.Platform))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["platform"] = [$"Platform must be one of: {string.Join(", ", Validate.Platforms)}."] });

            var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var client = new BuildClient
            {
                Id = Guid.NewGuid(), Name = name, Platform = req.Platform, OwnerId = users.GetUserId(user)!,
                SecretHash = HashSecret(secret), CreatedAt = DateTime.UtcNow,
            };
            db.BuildClients.Add(client);
            await db.SaveChangesAsync();
            return Results.Ok(new { client.Id, client.Name, client.Platform, Secret = secret });
        });

        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, UserManager<AppUser> users, AppDbContext db, BlobContainerClient blobs) =>
        {
            var client = await Visible(db, user, users, all: true).FirstOrDefaultAsync(c => c.Id == id);
            if (client is null) return Results.NotFound();
            // Its claimed builds go back to the queue rather than waiting out the lease, minus what they uploaded; its completed builds
            // keep counting (under their recorded client name).
            await BuildClientApi.ReleaseClaimsAsync(db, blobs, DateTime.UtcNow, id);
            await db.JobBuilds.Where(b => b.ClientId == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.ClientId, (Guid?)null));
            db.BuildClients.Remove(client);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>The caller's own clients; every client when <paramref name="all"/> and the caller is an admin.</summary>
    private static IQueryable<BuildClient> Visible(AppDbContext db, ClaimsPrincipal user, UserManager<AppUser> users, bool all)
    {
        var id = users.GetUserId(user);
        return all && user.IsInRole("Admin") ? db.BuildClients : db.BuildClients.Where(c => c.OwnerId == id);
    }

    /// <summary>SHA-256 hex of a client secret. A plain hash is enough: secrets are 256 random bits, not passwords.</summary>
    public static string HashSecret(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    internal static string Base64UrlEncode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
