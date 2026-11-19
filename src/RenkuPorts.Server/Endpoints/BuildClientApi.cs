using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Claims;
using Azure.Storage.Blobs;
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
    private const string ReleasedPrefix = "Released: ";

    public record TokenRequest(string? ClientId, string? Secret);
    public record LogBody(string? Log);
    public record FailRequest(string? Summary, string? Log);
    public record ReleaseRequest(string? Reason, string? Log);

    public static string LogBlob(long jobId) => $"logs/{jobId}.log";

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/client/token", async (TokenRequest req, AppDbContext db, IConfiguration cfg, HttpContext http, ILoggerFactory logs) =>
        {
            if (!Guid.TryParse(req.ClientId, out var id) || string.IsNullOrEmpty(req.Secret)) return BadCredentials();
            var client = await db.BuildClients.Include(c => c.Owner).FirstOrDefaultAsync(c => c.Id == id);
            if (client is null || !client.Enabled || client.Owner!.Disabled || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(client.SecretHash), Convert.FromHexString(ClientsEndpoints.HashSecret(req.Secret))))
                return BadCredentials();

            // The client sends User-Agent renkuports-client/<version>.
            logs.CreateLogger(nameof(BuildClientApi)).LogInformation("Token for build client {Client} ({Platform}), {UserAgent}",
                client.Name, client.Platform, http.Request.Headers.UserAgent.ToString());
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

        var api = app.MapGroup("/api/client").RequireAuthorization("BuildClient").AddEndpointFilter(async (ctx, next) =>
        {
            // A token outlives a deleted or disabled client (or owner), so check the client on every call (and record it as seen).
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var client = Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? await db.BuildClients.Include(c => c.Owner).FirstOrDefaultAsync(c => c.Id == id) : null;
            if (client is null || !client.Enabled || client.Owner!.Disabled) return Results.Unauthorized();
            client.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            http.Items[ClientItem] = client;
            return await next(ctx);
        });
        var g = api.MapGroup("/jobs");
        MapQueueViews(api, g);

        g.MapPost("/next", async (HttpContext http, AppDbContext db, BlobContainerClient blobs, IConfiguration cfg, INotifier notifier,
            UserManager<AppUser> users, ILoggerFactory logs, TimeProvider time) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var now = time.GetUtcNow().UtcDateTime;

            var expired = await ReleaseClaimsAsync(db, blobs, now, null);
            if (expired.Count > 0)
                await NotifyAdmins(users, notifier, logs, $"[RenkuPorts] {expired.Count} build lease(s) expired",
                    $"These jobs were claimed but not finished within their lease and are queued again: {string.Join(", ", expired)}.");

            // One transaction: claim the job row and record the build. UPDLOCK keeps a second claimer from taking the row this one
            // selected, and READPAST makes that second claimer skip the locked row and take the next job instead of waiting; a job
            // has at most one open build slot (Queued), so one job row is one slot. The NOT EXISTS is the independence window: a
            // client that completed a counting build of the job may not claim its next build until that build's ExclusiveUntil
            // (completion + its duration, written with the completion); other clients may at once. The window rows are written
            // before the job becomes Queued again (by the verifier), so the claim never sees a slot without its window.
            // ClaimOrder: highest build priority of the job's port first, then oldest (the queue position uses it too).
            var lease = now.AddHours(cfg.GetValue("Jobs:LeaseHours", 72.0));
            var claimed = await db.Database.SqlQuery<long>(FormattableStringFactory.Create($$"""
                SET XACT_ABORT ON;
                BEGIN TRAN;
                DECLARE @claimed TABLE (Id bigint);
                WITH next AS (
                    SELECT TOP (1) * FROM Jobs WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE Status = 'Queued' AND Platform = {0}
                      AND NOT EXISTS (SELECT 1 FROM JobBuilds b WHERE b.JobId = Jobs.Id AND b.Status = 'Completed'
                                      AND b.ClientId = {1} AND b.ExclusiveUntil > {2})
                    ORDER BY {{ClaimOrder}})
                UPDATE next SET Status = 'InProgress', ClaimedByClientId = {1}, ClaimedAt = {2},
                    LeaseExpiresAt = {3}, Attempts = Attempts + 1
                OUTPUT inserted.Id INTO @claimed;
                INSERT INTO JobBuilds (JobId, Number, ClientId, ClientName, OwnerId, Status, ClaimedAt)
                SELECT c.Id, (SELECT COUNT(*) FROM JobBuilds x WHERE x.JobId = c.Id) + 1, {1}, {4}, {5}, 'InProgress', {2} FROM @claimed c;
                COMMIT;
                SELECT Id AS Value FROM @claimed;
                """, client.Platform, client.Id, now, lease, client.Name, client.OwnerId)).ToListAsync();
            if (claimed.Count == 0) return Results.NoContent();

            return Results.Ok(await db.Jobs.Where(j => j.Id == claimed[0]).Select(j => new
            {
                JobId = j.Id, j.Recipe!.PortName, j.Recipe.Version, j.Recipe.Category, j.Recipe.RecipePath, j.Platform,
                Build = j.Builds.Max(b => b.Number),
            }).FirstAsync());
        });

        // Streams the 'file' part straight to blob storage while hashing it: no temp file, and the response follows the end
        // of the body at once (App Service's front end drops a request that is silent for ~230 s). The form fields
        // sha256 (required) and buildDate must come before the file part; sha256 names the content-addressed blob
        // {platform}/{sha256}/{fileName} and is checked against the bytes received. The package is staged for the build;
        // a package of another port (a dependency the build made) is only recorded as an extra, its bytes are not kept.
        g.MapPost("/{id:long}/packages", async (long id, HttpContext http, AppDbContext db, BlobContainerClient blobs) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var (job, build, error) = await ClaimedBuild(db, id, client);
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

            var extra = !await BelongsToPortAsync(db, fileName, job.Recipe!.PortName);
            var staged = await db.StagedPackages.AsNoTracking().FirstOrDefaultAsync(p => p.BuildId == build!.Id && p.FileName == fileName, ct);
            if (staged is not null && staged.Sha256 != sha) return Conflict($"{fileName} was already uploaded for this build with sha256 {staged.Sha256}.");
            // Existing packages are never replaced (a rebuild without a revision bump); extras are not published, so they cannot clash.
            if (!extra && await db.Packages.Where(p => p.Platform == job.Platform && p.FileName == fileName && p.Sha256 != sha).Select(p => p.Sha256).FirstOrDefaultAsync(ct) is { } published)
                return Conflict($"{job.Platform}/{fileName} exists with different content (sha256 {published}).");

            var row = new StagedPackage { BuildId = build!.Id, FileName = fileName, Sha256 = sha, BuildDate = buildDate, UploadedAt = DateTime.UtcNow, Extra = extra };
            PackageStore.BlobWrite? w = null;
            if (extra)
            {
                await using var hashing = new HashingStream(section.Body);
                await hashing.CopyToAsync(Stream.Null, ct);
                if (hashing.Sha256Hex != sha) return BadRequest($"sha256 {sha} does not match the uploaded bytes ({hashing.Sha256Hex}).");
                row.Size = hashing.BytesRead;
            }
            else
            {
                w = await PackageStore.WriteBlobAsync(blobs, job.Platform, fileName, sha, section.Body, ct);
                if (w.Sha256 != sha) return BadRequest($"sha256 {sha} does not match the uploaded bytes ({w.Sha256}).");
                (row.BlobName, row.Size) = (w.BlobName, w.Size);
            }
            if (staged is null)
            {
                db.StagedPackages.Add(row);
                try { await db.SaveChangesAsync(CancellationToken.None); }
                catch (DbUpdateException)
                {
                    // Unique (BuildId, FileName): a concurrent upload of the same name won.
                    db.ChangeTracker.Clear();
                    var other = await db.StagedPackages.AsNoTracking().FirstAsync(p => p.BuildId == build.Id && p.FileName == fileName, CancellationToken.None);
                    if (other.Sha256 != sha)
                    {
                        if (w is { Wrote: true }) await blobs.GetBlobClient(w.BlobName).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
                        return Conflict($"{fileName} was already uploaded for this build with sha256 {other.Sha256}.");
                    }
                }
            }
            return Results.Ok(new { FileName = fileName, Sha256 = sha, row.Size, Extra = extra });
        }).WithMetadata(new RequestSizeLimitAttribute(MaxUpload + (1 << 20)));

        // The build is done; the job is verified (compared with its other builds) before anything is published.
        g.MapPost("/{id:long}/complete", async (long id, HttpContext http, AppDbContext db, BlobContainerClient blobs, BuildVerifier verifier, TimeProvider time) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var (job, build, error) = await ClaimedBuild(db, id, client);
            if (job is null) return error!;

            string? log = null;
            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                log = form.Files.GetFile("log") is { } f ? await new StreamReader(f.OpenReadStream()).ReadToEndAsync() : form["log"].FirstOrDefault();
            }
            else if (http.Request.HasJsonContentType())
                log = (await http.Request.ReadFromJsonAsync<LogBody>(http.RequestAborted))?.Log;

            if (!await db.StagedPackages.AnyAsync(p => p.BuildId == build!.Id && !p.Extra))
                return BadRequest($"Upload at least one package of {job.Recipe!.PortName} before completing the job.");
            if (log is not null) await SaveLog(blobs, job, log);
            var now = time.GetUtcNow().UtcDateTime;
            build!.Status = JobBuildStatus.Completed;
            build.CompletedAt = now;
            build.ExclusiveUntil = now + (now - build.ClaimedAt);
            job.Status = JobStatus.Verifying;
            // A release reason describes an earlier claim, not this build; a real fail summary never reaches here (Failed jobs start over).
            if (job.FailureSummary?.StartsWith(ReleasedPrefix, StringComparison.Ordinal) == true) job.FailureSummary = null;
            job.ClaimedByClientId = null;
            job.ClaimedAt = null;
            job.LeaseExpiresAt = null;
            var (res, saved) = await SaveJob(db, job);
            if (saved) verifier.Kick();
            return res;
        });

        // A failed build fails the job (as before verification existed): its builds' staged packages are deleted and admins emailed.
        g.MapPost("/{id:long}/fail", async (long id, FailRequest req, HttpContext http, AppDbContext db, BlobContainerClient blobs,
            INotifier notifier, UserManager<AppUser> users, ILoggerFactory logs, TimeProvider time) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var (job, build, error) = await ClaimedBuild(db, id, client);
            if (job is null) return error!;

            var summary = string.IsNullOrWhiteSpace(req.Summary) ? "build failed" : req.Summary.Trim();
            if (summary.Length > 2000) summary = summary[..2000];
            if (req.Log is not null) await SaveLog(blobs, job, req.Log);
            var now = time.GetUtcNow().UtcDateTime;
            build!.Status = JobBuildStatus.Failed;
            build.CompletedAt = now;
            job.Status = JobStatus.Failed;
            job.FailureSummary = summary;
            job.CompletedAt = now;
            job.ClaimedByClientId = null;
            job.ClaimedAt = null;
            job.LeaseExpiresAt = null;
            var (res, saved) = await SaveJob(db, job);
            if (saved)
            {
                await Verification.DropBlobsAsync(db, blobs, await db.JobBuilds.Where(b => b.JobId == job.Id).Select(b => b.Id).ToListAsync());
                await NotifyAdmins(users, notifier, logs, $"[RenkuPorts] {job.Recipe!.PortName} {job.Recipe.Version} failed on {job.Platform}",
                    $"Job {job.Id} ({job.Recipe.PortName} {job.Recipe.Version}, {job.Platform}) failed on build client '{client.Name}' " +
                    $"(build {build.Number}, attempt {job.Attempts}).\n\n{summary}\n\nThe build log is on the Ports dashboard.");
            }
            return res;
        });

        // The client gives the job back without a result (infrastructure failure, client stopped): it is requeued like an
        // expired lease, without the admin email. Attempts stays incremented: it counts claims, as it does after a lease expires.
        g.MapPost("/{id:long}/release", async (long id, ReleaseRequest req, HttpContext http, AppDbContext db, BlobContainerClient blobs, TimeProvider time) =>
        {
            var client = (BuildClient)http.Items[ClientItem]!;
            var (job, _, error) = await ClaimedBuild(db, id, client);
            if (job is null) return error!;

            if ((await ReleaseClaimsAsync(db, blobs, time.GetUtcNow().UtcDateTime, client.Id, id)).Count == 0)
                return Results.Problem(statusCode: 409, title: "Job changed concurrently (lease expired?)");
            var reason = ReleasedPrefix + (string.IsNullOrWhiteSpace(req.Reason) ? "no reason given" : req.Reason.Trim());
            if (reason.Length > 2000) reason = reason[..2000];
            if (req.Log is not null) await SaveLog(blobs, job, req.Log);
            // The release SQL changed the row version, so the tracked job cannot be saved; update the columns directly.
            await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.FailureSummary, reason).SetProperty(j => j.HasLog, job.HasLog));
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Loads a job and its open build the calling client may act on: 404 if unknown, 409 unless InProgress, 403 if another client holds it.
    /// </summary>
    private static async Task<(Job?, JobBuild?, IResult?)> ClaimedBuild(AppDbContext db, long id, BuildClient client)
    {
        var job = await db.Jobs.Include(j => j.Recipe).FirstOrDefaultAsync(j => j.Id == id);
        if (job is null) return (null, null, Results.NotFound());
        if (job.Status != JobStatus.InProgress) return (null, null, Results.Problem(statusCode: 409, title: $"Job is {job.Status}"));
        if (job.ClaimedByClientId != client.Id) return (null, null, Results.Problem(statusCode: 403, title: "Job is claimed by another client"));
        var build = await db.JobBuilds.FirstOrDefaultAsync(b => b.JobId == id && b.Status == JobBuildStatus.InProgress && b.ClientId == client.Id);
        if (build is null) return (null, null, Results.Problem(statusCode: 409, title: "The job has no open build of this client"));
        return (job, build, null);
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
    /// Requeues InProgress jobs whose lease expired before <paramref name="now"/> (<paramref name="client"/> null) or that
    /// <paramref name="client"/> holds (it is being deleted, or releases <paramref name="job"/> when given). Only the open build slot goes
    /// back: that build becomes Expired and its staged blobs are deleted; completed builds keep counting. Returns the requeued job ids.
    /// </summary>
    public static async Task<List<long>> ReleaseClaimsAsync(AppDbContext db, BlobContainerClient blobs, DateTime now, Guid? client, long? job = null)
    {
        var jobs = await db.Database.SqlQuery<long>($"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @jobs TABLE (Id bigint);
            UPDATE Jobs SET Status = 'Queued', ClaimedByClientId = NULL, ClaimedAt = NULL, LeaseExpiresAt = NULL
            OUTPUT inserted.Id INTO @jobs
            WHERE Status = 'InProgress' AND (({client} IS NULL AND LeaseExpiresAt < {now}) OR ClaimedByClientId = {client})
              AND ({job} IS NULL OR Id = {job});
            UPDATE JobBuilds SET Status = 'Expired', CompletedAt = {now} WHERE Status = 'InProgress' AND JobId IN (SELECT Id FROM @jobs);
            COMMIT;
            SELECT Id AS Value FROM @jobs;
            """).ToListAsync();
        if (jobs.Count > 0)
            await Verification.DropBlobsAsync(db, blobs, await db.JobBuilds.Where(b => jobs.Contains(b.JobId) && b.Status == JobBuildStatus.Expired).Select(b => b.Id).ToListAsync());
        return jobs;
    }

    /// <summary>
    /// Whether an uploaded package belongs to <paramref name="port"/>: its name (the file name up to the first '-') is the port name
    /// or starts with <c>port_</c>, and no longer port name in the recipes claims it (<c>gcc_bootstrap_x</c> belongs to a
    /// <c>gcc_bootstrap</c> port, not to <c>gcc</c>).
    /// </summary>
    /// <remarks>shortcut: by name, not the recipe's evaluated PACKAGE_NAME_* (a renamed subpackage counts as an extra); evaluate the recipe if a port needs it.</remarks>
    public static async Task<bool> BelongsToPortAsync(AppDbContext db, string fileName, string port)
    {
        var name = fileName.Split('-')[0];
        if (name != port && !name.StartsWith(port + "_", StringComparison.Ordinal)) return false;
        if (name == port) return true;
        var longer = new List<string> { name };
        for (var i = name.IndexOf('_', port.Length + 1); i > 0; i = name.IndexOf('_', i + 1)) longer.Add(name[..i]);
        return !await db.Recipes.AnyAsync(r => longer.Contains(r.PortName));
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
    internal static async Task NotifyAdmins(UserManager<AppUser> users, INotifier notifier, ILoggerFactory logs, string subject, string body)
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

    private static IResult Conflict(string detail) => Results.Problem(statusCode: 409, title: "Package already exists", detail: detail);
    private static IResult BadRequest(string detail) => Results.Problem(statusCode: 400, title: "Bad request", detail: detail);
    private static IResult BadCredentials() => Results.Problem(statusCode: 401, title: "Invalid client id or secret");
}
