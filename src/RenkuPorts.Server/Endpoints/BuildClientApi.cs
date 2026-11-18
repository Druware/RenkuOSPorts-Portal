using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Endpoints;

/// <summary>The API build clients call with their bearer token: claim a job, upload its packages, complete or fail it.</summary>
public static partial class BuildClientApi
{
    /// <summary>Largest accepted .hpkg upload (4 GiB).</summary>
    public const long MaxUpload = 4L << 30;
    private const string ClientItem = "build-client";

    public record TokenRequest(string? ClientId, string? Secret);
    public record LogBody(string? Log);
    public record FailRequest(string? Summary, string? Log);

    public static string LogBlob(long jobId) => $"logs/{jobId}.log";

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/client/token", async (TokenRequest req, AppDbContext db, IConfiguration cfg) =>
        {
            if (!Guid.TryParse(req.ClientId, out var id) || string.IsNullOrEmpty(req.Secret)) return BadCredentials();
            var client = await db.BuildClients.FindAsync(id);
            if (client is null || !client.Enabled || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(client.SecretHash), Convert.FromHexString(ClientsEndpoints.HashSecret(req.Secret))))
                return BadCredentials();

            client.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var expires = DateTime.UtcNow.Add(ClientTokens.Lifetime);
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = ClientTokens.Issuer,
                Audience = ClientTokens.Audience,
                Expires = expires,
                Subject = new ClaimsIdentity([new Claim("sub", client.Id.ToString()), new Claim("platform", client.Platform)]),
                SigningCredentials = new SigningCredentials(ClientTokens.Key(cfg), SecurityAlgorithms.HmacSha256),
            });
            return Results.Ok(new { AccessToken = token, ExpiresAt = expires });
        });

        var g = app.MapGroup("/api/client/jobs").RequireAuthorization("BuildClient").AddEndpointFilter(async (ctx, next) =>
        {
            // A token outlives a deleted or disabled client, so check the client on every call (and record it as seen).
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var client = Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? await db.BuildClients.FindAsync(id) : null;
            if (client is null || !client.Enabled) return Results.Unauthorized();
            client.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            http.Items[ClientItem] = client;
            return await next(ctx);
        });

        g.MapPost("/next", async (HttpContext http, AppDbContext db, BlobContainerClient blobs, IConfiguration cfg, INotifier notifier, UserManager<AppUser> users, ILoggerFactory logs) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var now = DateTime.UtcNow;

            var expired = await db.Database.SqlQuery<long>($"""
                UPDATE Jobs SET Status = 'Queued', ClaimedByClientId = NULL, ClaimedAt = NULL, LeaseExpiresAt = NULL
                OUTPUT inserted.Id AS Value
                WHERE Status = 'InProgress' AND LeaseExpiresAt < {now}
                """).ToListAsync();
            if (expired.Count > 0)
            {
                await DiscardPackagesAsync(db, blobs, expired);
                await NotifyAdmins(users, notifier, logs, $"[RenkuPorts] {expired.Count} build lease(s) expired",
                    $"These jobs were claimed but not finished within their lease and are queued again: {string.Join(", ", expired)}.");
            }

            // One statement, so it is atomic: UPDLOCK keeps a second claimer from taking the row this one selected, and
            // READPAST makes that second claimer skip the locked row and take the next job instead of waiting.
            var lease = now.AddHours(cfg.GetValue("Jobs:LeaseHours", 72.0));
            var claimed = await db.Database.SqlQuery<long>($"""
                WITH next AS (
                    SELECT TOP (1) * FROM Jobs WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE Status = 'Queued' AND Platform = {client.Platform}
                    ORDER BY QueuedAt, Id)
                UPDATE next SET Status = 'InProgress', ClaimedByClientId = {client.Id}, ClaimedAt = {now},
                    LeaseExpiresAt = {lease}, Attempts = Attempts + 1
                OUTPUT inserted.Id AS Value;
                """).ToListAsync();
            if (claimed.Count == 0) return Results.NoContent();

            return Results.Ok(await db.Jobs.Where(j => j.Id == claimed[0]).Select(j => new
            {
                JobId = j.Id, j.Recipe!.PortName, j.Recipe.Version, j.Recipe.Category, j.Recipe.RecipePath, j.Platform,
            }).FirstAsync());
        });

        // Streams the 'file' part straight to blob storage while hashing it: no temp file, and the response follows the end
        // of the body at once (App Service's front end drops a request that is silent for ~230 s). The form fields
        // sha256 (required) and buildDate must come before the file part; sha256 names the content-addressed blob
        // {platform}/{sha256}/{fileName} and is checked against the bytes received.
        g.MapPost("/{id:long}/packages", async (long id, HttpContext http, AppDbContext db, BlobContainerClient blobs) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var (job, error) = await ClaimedJob(db, id, client);
            if (job is null) return error!;
            var ct = http.RequestAborted;

            if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type) || type.MediaType != "multipart/form-data"
                || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 70 } boundary)
                return BadRequest("Expected multipart/form-data: sha256 and buildDate fields, then the 'file' part.");
            var reader = new MultipartReader(boundary, http.Request.Body);
            var fields = new Dictionary<string, string>();
            MultipartSection? section;
            ContentDispositionHeaderValue? file = null;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)) return BadRequest("Malformed multipart section.");
                var name = HeaderUtilities.RemoveQuotes(cd.Name).Value ?? "";
                if (cd.IsFileDisposition()) { file = cd; break; }
                if (fields.Count >= 8 || await ReadFieldAsync(section.Body, ct) is not { } value) return BadRequest("Too many or too long form fields.");
                fields[name] = value;
            }
            if (section is null || file is null || HeaderUtilities.RemoveQuotes(file.Name).Value != "file") return BadRequest("Missing 'file' part.");
            var fileName = HeaderUtilities.RemoveQuotes(file.FileNameStar.HasValue ? file.FileNameStar : file.FileName).Value;
            if (!Validate.IsHpkgName(fileName)) return BadRequest("fileName must be a plain *.hpkg name without path separators.");
            var sha = fields.GetValueOrDefault("sha256")?.ToLowerInvariant();
            if (sha is null || sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)) return BadRequest("A sha256 field (64 hex digits) must come before the file part.");
            var buildDate = DateTime.UtcNow;
            if (fields.TryGetValue("buildDate", out var bd) && !DateTime.TryParse(bd, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out buildDate))
                return BadRequest("buildDate must be an ISO-8601 date.");

            var blobName = $"{job.Platform}/{sha}/{fileName}";
            var blob = blobs.GetBlobClient(blobName);
            await using var body = new HashingStream(section.Body);
            bool wrote;
            try
            {
                // Never overwrite: an existing blob at this name already holds these bytes (if the hash checks out).
                await blob.UploadAsync(body, new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
                wrote = true;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                wrote = false;
                await body.CopyToAsync(Stream.Null, ct); // still hash the rest, to verify the claim
            }
            if (body.Sha256Hex != sha)
            {
                if (wrote) await blob.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
                return BadRequest($"sha256 {sha} does not match the uploaded bytes ({body.Sha256Hex}).");
            }
            var result = new { FileName = fileName, Sha256 = sha, Size = body.BytesRead };

            db.Packages.Add(new Package
            {
                FileName = fileName, Platform = job.Platform, BlobName = blobName, Size = body.BytesRead, Sha256 = sha, BuildDate = buildDate,
                UploadedAt = DateTime.UtcNow, RecipeUpdatedAt = job.Recipe!.RecipeUpdatedAt, PortName = job.Recipe.PortName,
                BuiltByUserId = client.OwnerId, ClientId = client.Id, JobId = job.Id,
            });
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (DbUpdateException)
            {
                // Unique (Platform, FileName): this name is already taken.
                db.ChangeTracker.Clear();
                var existing = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Platform == job.Platform && p.FileName == fileName, CancellationToken.None);
                if (existing is null) throw;
                // The blob we wrote is unreferenced unless the existing row points at the same (shared) name.
                if (wrote && existing.BlobName != blobName) await blob.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
                return existing.Sha256 == sha
                    ? Results.Ok(result)
                    : Results.Problem(statusCode: 409, title: "Package already exists", detail: $"{job.Platform}/{fileName} exists with different content (sha256 {existing.Sha256}).");
            }
            return Results.Ok(result);
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload + (1 << 20)));

        g.MapPost("/{id:long}/complete", async (long id, HttpContext http, AppDbContext db, BlobContainerClient blobs, RepoIndexer indexer) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var (job, error) = await ClaimedJob(db, id, client);
            if (job is null) return error!;

            string? log = null;
            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                log = form.Files.GetFile("log") is { } f ? await new StreamReader(f.OpenReadStream()).ReadToEndAsync() : form["log"].FirstOrDefault();
            }
            else if (http.Request.HasJsonContentType())
                log = (await http.Request.ReadFromJsonAsync<LogBody>(http.RequestAborted))?.Log;

            if (!await db.Packages.AnyAsync(p => p.JobId == id)) return BadRequest("Upload at least one package before completing the job.");
            if (log is not null) await SaveLog(blobs, job, log);
            job.Status = JobStatus.Succeeded;
            job.CompletedAt = DateTime.UtcNow;
            job.LeaseExpiresAt = null;
            var (res, saved) = await SaveJob(db, job);
            if (saved) indexer.MarkDirty(job.Platform);
            return res;
        });

        g.MapPost("/{id:long}/fail", async (long id, FailRequest req, HttpContext http, AppDbContext db, BlobContainerClient blobs,
            INotifier notifier, UserManager<AppUser> users, ILoggerFactory logs) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var (job, error) = await ClaimedJob(db, id, client);
            if (job is null) return error!;

            var summary = string.IsNullOrWhiteSpace(req.Summary) ? "build failed" : req.Summary.Trim();
            if (summary.Length > 2000) summary = summary[..2000];
            if (req.Log is not null) await SaveLog(blobs, job, req.Log);
            job.Status = JobStatus.Failed;
            job.FailureSummary = summary;
            job.CompletedAt = DateTime.UtcNow;
            job.ClaimedByClientId = null;
            job.ClaimedAt = null;
            job.LeaseExpiresAt = null;
            var (res, saved) = await SaveJob(db, job);
            if (saved)
            {
                await DiscardPackagesAsync(db, blobs, [job.Id]);
                await NotifyAdmins(users, notifier, logs, $"[RenkuPorts] {job.Recipe!.PortName} {job.Recipe.Version} failed on {job.Platform}",
                    $"Job {job.Id} ({job.Recipe.PortName} {job.Recipe.Version}, {job.Platform}) failed on build client '{client.Name}' " +
                    $"(attempt {job.Attempts}).\n\n{summary}\n\nThe build log is on the Ports dashboard.");
            }
            return res;
        });
    }

    /// <summary>Loads a job the calling client may act on: 404 if unknown, 409 unless InProgress, 403 if another client holds it.</summary>
    private static async Task<(Job?, IResult?)> ClaimedJob(AppDbContext db, long id, BuildClient client)
    {
        var job = await db.Jobs.Include(j => j.Recipe).FirstOrDefaultAsync(j => j.Id == id);
        if (job is null) return (null, Results.NotFound());
        if (job.Status != JobStatus.InProgress) return (null, Results.Problem(statusCode: 409, title: $"Job is {job.Status}"));
        if (job.ClaimedByClientId != client.Id) return (null, Results.Problem(statusCode: 403, title: "Job is claimed by another client"));
        return (job, null);
    }

    private static async Task<(IResult Result, bool Saved)> SaveJob(AppDbContext db, Job job)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // The lease expired and the job was requeued (or reclaimed) while this client was finishing.
            return (Results.Problem(statusCode: 409, title: "Job changed concurrently (lease expired?)"), false);
        }
        return (Results.Ok(await db.Jobs.Where(j => j.Id == job.Id).Select(Project.Job).FirstAsync()), true);
    }

    /// <summary>
    /// Deletes the (unpublished) packages of jobs that left InProgress without succeeding, rows first, then blobs,
    /// so the retry can upload the same file names with new content. A blob left by a failed delete is overwritten then.
    /// </summary>
    public static async Task DiscardPackagesAsync(AppDbContext db, BlobContainerClient blobs, IReadOnlyCollection<long> jobIds)
    {
        var pkgs = await db.Packages.Where(p => p.JobId != null && jobIds.Contains(p.JobId.Value) && p.Job!.Status != JobStatus.Succeeded)
            .Select(p => new { p.Id, p.BlobName }).ToListAsync();
        if (pkgs.Count == 0) return;
        var ids = pkgs.Select(p => p.Id).ToList();
        await db.Packages.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync();
        foreach (var p in pkgs) await blobs.GetBlobClient(p.BlobName).DeleteIfExistsAsync();
    }

    /// <summary>A small multipart form field (at most 1024 chars), or null if longer.</summary>
    private static async Task<string?> ReadFieldAsync(Stream body, CancellationToken ct)
    {
        using var r = new StreamReader(body, System.Text.Encoding.UTF8, leaveOpen: true);
        var buf = new char[1025];
        int n = 0, k;
        while (n < buf.Length && (k = await r.ReadAsync(buf.AsMemory(n), ct)) > 0) n += k;
        return n > 1024 ? null : new string(buf, 0, n);
    }

    private static async Task SaveLog(BlobContainerClient blobs, Job job, string log)
    {
        await blobs.GetBlobClient(LogBlob(job.Id)).UploadAsync(BinaryData.FromString(StripAnsi(log)), overwrite: true);
        job.HasLog = true;
    }

    /// <summary>Removes terminal escape sequences (HaikuPorter colours its output), which the log viewer would show as junk.</summary>
    public static string StripAnsi(string log) => AnsiEscape().Replace(log, "");

    /// <summary>CSI sequences (<c>ESC [ ... final</c>), OSC sequences (<c>ESC ] ... BEL|ST</c>), then any lone ESC left over.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B")]
    private static partial System.Text.RegularExpressions.Regex AnsiEscape();

    /// <summary>Emails every Admin. A mail failure is logged, not returned: the job state change has already been saved.</summary>
    private static async Task NotifyAdmins(UserManager<AppUser> users, INotifier notifier, ILoggerFactory logs, string subject, string body)
    {
        var to = (await users.GetUsersInRoleAsync("Admin")).Select(u => u.Email).OfType<string>().ToList();
        try
        {
            await notifier.SendAsync(to, subject, body);
        }
        catch (Exception e)
        {
            logs.CreateLogger(nameof(BuildClientApi)).LogError(e, "Sending '{Subject}' to admins failed", subject);
        }
    }

    private static IResult BadRequest(string detail) => Results.Problem(statusCode: 400, title: "Bad request", detail: detail);
    private static IResult BadCredentials() => Results.Problem(statusCode: 401, title: "Invalid client id or secret");
}
