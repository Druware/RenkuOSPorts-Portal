using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Services;

/// <summary>Adds a package file: bytes to blob <c>{platform}/{sha256}/{fileName}</c>, then its <see cref="Package"/> row. Used by uploads and imports.</summary>
public static class PackageStore
{
    public enum Outcome { Added, AlreadyPresent, HashMismatch, Conflict }

    /// <param name="Sha256">The hash of the bytes received.</param>
    /// <param name="ExistingSha256">For <see cref="Outcome.Conflict"/>: the hash of the package already stored under this name.</param>
    public record Result(Outcome Outcome, string Sha256, long Size, string? ExistingSha256 = null);

    /// <summary>
    /// Writes the blob with <see cref="WriteBlobAsync"/>, then inserts the row from
    /// <paramref name="row"/> (blob name and size filled in). A name already taken with the same bytes is <see cref="Outcome.AlreadyPresent"/>;
    /// with other bytes, <see cref="Outcome.Conflict"/>: existing packages are never overwritten.
    /// </summary>
    public static async Task<Result> StoreAsync(AppDbContext db, BlobContainerClient blobs, string platform, string fileName, string sha,
        Stream body, Package row, CancellationToken ct)
    {
        var w = await WriteBlobAsync(blobs, platform, fileName, sha, body, ct);
        if (w.Sha256 != sha) return new(Outcome.HashMismatch, w.Sha256, w.Size);
        row.FileName = fileName;
        row.Platform = platform;
        row.BlobName = w.BlobName;
        row.Size = w.Size;
        row.Sha256 = sha;
        db.Packages.Add(row);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // Unique (Platform, FileName): this name is already taken.
            db.ChangeTracker.Clear();
            var existing = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Platform == platform && p.FileName == fileName, CancellationToken.None);
            if (existing is null) throw;
            // The blob we wrote is unreferenced unless the existing row points at the same (shared) name.
            if (w.Wrote && existing.BlobName != w.BlobName) await blobs.GetBlobClient(w.BlobName).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            return existing.Sha256 == sha
                ? new(Outcome.AlreadyPresent, sha, w.Size)
                : new(Outcome.Conflict, sha, w.Size, existing.Sha256);
        }
        return new(Outcome.Added, sha, w.Size);
    }

    /// <param name="Wrote">This call created the blob (so it may delete it again); false if it already held these bytes.</param>
    public record BlobWrite(string BlobName, bool Wrote, string Sha256, long Size);

    /// <summary>
    /// Streams <paramref name="body"/> into the content-addressed blob <c>{platform}/{sha}/{fileName}</c> while hashing it, so no temp
    /// file is needed; deletes what it wrote if the bytes do not hash to <paramref name="sha"/> (the result's Sha256 then differs).
    /// </summary>
    public static async Task<BlobWrite> WriteBlobAsync(BlobContainerClient blobs, string platform, string fileName, string sha, Stream body, CancellationToken ct)
    {
        var blobName = $"{platform}/{sha}/{fileName}";
        var blob = blobs.GetBlobClient(blobName);
        await using var hashing = new HashingStream(body);
        bool wrote;
        try
        {
            // Never overwrite: an existing blob at this name already holds these bytes (if the hash checks out).
            await blob.UploadAsync(hashing, new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
            wrote = true;
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            wrote = false;
            await hashing.CopyToAsync(Stream.Null, ct); // still hash the rest, to verify the claim
        }
        if (hashing.Sha256Hex != sha && wrote) await blob.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
        return new(blobName, wrote, hashing.Sha256Hex, hashing.BytesRead);
    }
}
