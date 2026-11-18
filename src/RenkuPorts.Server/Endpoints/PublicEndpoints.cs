using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>Anonymous repository browsing and package download.</summary>
public static class PublicEndpoints
{
    /// <summary>Packages of succeeded jobs, and imported ones; uploads of a job still building (or that failed) are not public.</summary>
    internal static IQueryable<Package> Published(AppDbContext db) => db.Packages.Where(p => p.Job!.Status == JobStatus.Succeeded || p.ImportId != null);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/platforms", async (AppDbContext db) =>
        {
            var stats = await Published(db).GroupBy(p => p.Platform)
                .Select(g => new { Platform = g.Key, PackageCount = g.Count(), TotalBytes = g.Sum(p => p.Size), LastUploadAt = (DateTime?)g.Max(p => p.UploadedAt) })
                .ToDictionaryAsync(s => s.Platform);
            return Validate.Platforms.Select(p => stats.TryGetValue(p, out var s)
                ? s : new { Platform = p, PackageCount = 0, TotalBytes = 0L, LastUploadAt = (DateTime?)null });
        });

        app.MapGet("/api/repo/{platform}/packages", async (string platform, string? search, AppDbContext db) =>
        {
            if (!Validate.IsPlatform(platform)) return Results.NotFound();
            var q = Published(db).Where(p => p.Platform == platform);
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(p => p.FileName.Contains(search));
            return Results.Ok(await q.OrderByDescending(p => p.UploadedAt).ThenByDescending(p => p.Id).Select(Project.Package).ToListAsync());
        });

        // pkgman's repository index, built by RepoIndexer; 404 until the platform's first build.
        foreach (var file in new[] { "repo", "repo.info", "repo.sha256" })
        {
            app.MapGet($"/repo/{{platform}}/{file}", async (string platform, BlobContainerClient blobs, HttpContext ctx) =>
            {
                if (!Validate.IsPlatform(platform)) return Results.NotFound();
                try
                {
                    var blob = await blobs.GetBlobClient(RepoIndexer.IndexBlob(platform, file)).DownloadStreamingAsync(cancellationToken: ctx.RequestAborted);
                    ctx.Response.ContentLength = blob.Value.Details.ContentLength;
                    ctx.Response.Headers.CacheControl = "no-cache"; // repo and repo.sha256 must change together
                    return Results.Stream(blob.Value.Content, blob.Value.Details.ContentType ?? "application/octet-stream");
                }
                catch (Azure.RequestFailedException e) when (e.Status == 404)
                {
                    return Results.NotFound();
                }
            });
        }

        app.MapGet("/repo/{platform}/packages/{fileName}", async (string platform, string fileName, AppDbContext db, BlobContainerClient blobs, HttpContext ctx) =>
        {
            if (!Validate.IsPlatform(platform) || !Validate.IsHpkgName(fileName)) return Results.NotFound();
            var pkg = await Published(db).AsNoTracking().FirstOrDefaultAsync(p => p.Platform == platform && p.FileName == fileName);
            if (pkg is null) return Results.NotFound();
            try
            {
                var blob = await blobs.GetBlobClient(pkg.BlobName).DownloadStreamingAsync(cancellationToken: ctx.RequestAborted);
                ctx.Response.ContentLength = blob.Value.Details.ContentLength;
                return Results.Stream(blob.Value.Content, "application/octet-stream", fileName);
            }
            catch (Azure.RequestFailedException e) when (e.Status == 404)
            {
                return Results.NotFound(); // row inserted, blob upload still running
            }
        });
    }
}
