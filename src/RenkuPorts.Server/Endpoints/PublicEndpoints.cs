using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

        // Newest first, Id as tiebreak so pages never overlap or skip. Page past the end: no items, the real total.
        app.MapGet("/api/repo/{platform}/packages", async (string platform, string? search, int? page, int? pageSize, AppDbContext db) =>
        {
            if (!Validate.IsPlatform(platform)) return Results.NotFound();
            var q = Published(db).Where(p => p.Platform == platform);
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(p => p.FileName.Contains(search));
            return Results.Ok(await Paged<PackageDto>.FromAsync(q, o => o.OrderByDescending(p => p.UploadedAt).ThenByDescending(p => p.Id), Project.Package, page, pageSize));
        });

        // pkgman's repository index, built by RepoIndexer; 404 until the platform's first build.
        foreach (var file in new[] { "repo", "repo.info", "repo.sha256" })
        {
            app.MapGet($"/repo/{{platform}}/{file}", async (string platform, AppDbContext db, BlobContainerClient blobs, TimeProvider time, HttpContext ctx) =>
            {
                if (!Validate.IsPlatform(platform)) return Results.NotFound();
                try
                {
                    var blob = await blobs.GetBlobClient(RepoIndexer.IndexBlob(platform, file)).DownloadStreamingAsync(cancellationToken: ctx.RequestAborted);
                    await Egress.TryAddAsync(db, time, blob.Value.Details.ContentLength, enforceCap: false, ctx.RequestAborted); // counted, never blocked
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

        app.MapGet("/repo/{platform}/packages/{fileName}", async (string platform, string fileName, AppDbContext db, BlobContainerClient blobs, TimeProvider time, HttpContext ctx) =>
        {
            if (!Validate.IsPlatform(platform) || !Validate.IsHpkgName(fileName)) return Results.NotFound();
            var pkg = await Published(db).AsNoTracking().FirstOrDefaultAsync(p => p.Platform == platform && p.FileName == fileName);
            if (pkg is null) return Results.NotFound();
            // pkgman resumes with "Range: bytes=<size>-" and appends whatever comes back, so a resumed download needs the tail only.
            var range = ByteRange(ctx.Request, pkg.Size);
            ctx.Response.Headers.AcceptRanges = "bytes";
            if (range is { Length: 0 })
            {
                ctx.Response.Headers.ContentRange = $"bytes */{pkg.Size}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }
            try
            {
                var options = new BlobDownloadOptions { Range = range is null ? default : new HttpRange(range.Value.Start, range.Value.Length) };
                var blob = await blobs.GetBlobClient(pkg.BlobName).DownloadStreamingAsync(options, ctx.RequestAborted);
                // shortcut: counts the served length up front, so aborted downloads overcount; meter streamed bytes if that ever matters.
                if (!await Egress.TryAddAsync(db, time, blob.Value.Details.ContentLength, enforceCap: true, ctx.RequestAborted))
                {
                    blob.Value.Dispose();
                    return Results.NotFound(); // this month's egress cap is reached
                }
                if (range is { } r)
                {
                    ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                    ctx.Response.Headers.ContentRange = $"bytes {r.Start}-{r.Start + r.Length - 1}/{pkg.Size}";
                }
                ctx.Response.ContentLength = blob.Value.Details.ContentLength;
                return Results.Stream(blob.Value.Content, "application/octet-stream", fileName);
            }
            catch (Azure.RequestFailedException e) when (e.Status == 404)
            {
                return Results.NotFound(); // row inserted, blob upload still running
            }
        });
    }

    /// <summary>
    /// The single byte range of a <paramref name="length"/>-byte file that <paramref name="request"/> asks for. Null means the
    /// whole file: no Range header, or one RFC 9110 lets a server ignore (malformed, another unit, several ranges). Length 0
    /// means unsatisfiable (416).
    /// </summary>
    /// <remarks>
    /// shortcut: If-Range is not evaluated (pkgman never sends it), so a resume across a republish under the same name gets mixed
    /// bytes and fails its checksum; honour If-Range against the blob's ETag if a client that sends it ever needs this.
    /// </remarks>
    internal static (long Start, long Length)? ByteRange(HttpRequest request, long length)
    {
        var header = request.GetTypedHeaders().Range;
        if (header is null || !header.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || header.Ranges.Count != 1) return null;
        var r = header.Ranges.Single();
        var (start, end) = r.From is { } from
            ? (from, Math.Min(r.To ?? long.MaxValue, length - 1)) // bytes=N- or bytes=N-M
            : (Math.Max(0, length - r.To!.Value), length - 1);    // bytes=-K: the last K bytes
        return (start, Math.Max(0, end - start + 1));
    }
}
