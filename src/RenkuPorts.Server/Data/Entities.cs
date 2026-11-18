using Microsoft.AspNetCore.Identity;

namespace RenkuPorts.Server.Data;

/// <summary>A signed-in person (admin or builder). UserName is the email address.</summary>
public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}

/// <summary>A machine that builds ports for one platform, authenticating with a client id and secret.</summary>
public class BuildClient
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public AppUser? Owner { get; set; }
    /// <summary>SHA-256 (hex) of the generated secret; the secret itself is shown once and never stored.</summary>
    public string SecretHash { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>A recipe file found in the ports tree.</summary>
public class Recipe
{
    public int Id { get; set; }
    public string PortName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Path relative to the ports tree root, with forward slashes.</summary>
    public string RecipePath { get; set; } = "";
    public DateTime RecipeUpdatedAt { get; set; }
}

public enum JobStatus { Queued, InProgress, Succeeded, Failed, Cancelled }

/// <summary>A request to build one recipe for one platform.</summary>
public class Job
{
    public long Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public string Platform { get; set; } = "";
    public JobStatus Status { get; set; }
    public DateTime QueuedAt { get; set; }
    /// <summary>The admin who queued it; null for jobs queued by <see cref="Services.AutoUpdater"/> (shown as "auto-update").</summary>
    public string? QueuedByUserId { get; set; }
    public AppUser? QueuedBy { get; set; }
    public Guid? ClaimedByClientId { get; set; }
    public BuildClient? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public string? FailureSummary { get; set; }
    public bool HasLog { get; set; }
    public List<Package> Packages { get; set; } = [];
    /// <summary>Concurrency token: admin transitions fail with 409 if a client claimed or finished the job meanwhile.</summary>
    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>An uploaded .hpkg, stored in blob <see cref="BlobName"/>.</summary>
public class Package
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string Platform { get; set; } = "";
    /// <summary>Blob holding the bytes: <c>{Platform}/{Sha256}/{FileName}</c> (rows from before that layout: <c>{Platform}/{FileName}</c>).</summary>
    public string BlobName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime BuildDate { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime RecipeUpdatedAt { get; set; }
    public string PortName { get; set; } = "";
    public string BuiltByUserId { get; set; } = "";
    public AppUser? BuiltBy { get; set; }
    public Guid? ClientId { get; set; }
    public long? JobId { get; set; }
    /// <summary>The job that uploaded it. A package is public only once this job has Succeeded.</summary>
    public Job? Job { get; set; }
    /// <summary>Set instead of <see cref="JobId"/> for a package copied from another repository; such a package is public at once.</summary>
    public long? ImportId { get; set; }
    public Import? Import { get; set; }
}

public enum ImportStatus { Running, Completed, Failed, Cancelled }

/// <summary>A copy of the packages another pkgman repository has and this one lacks, for one platform.</summary>
public class Import
{
    public long Id { get; set; }
    /// <summary>The source repository's base URL, as given to <c>pkgman add-repo</c>.</summary>
    public string SourceUrl { get; set; } = "";
    public string SourceHost { get; set; } = "";
    public string Platform { get; set; } = "";
    /// <summary>Only count and list the missing packages.</summary>
    public bool DryRun { get; set; }
    public ImportStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Last progress write by the instance running it; a Running import not updated for minutes was interrupted.</summary>
    public DateTime UpdatedAt { get; set; }
    public string StartedByUserId { get; set; } = "";
    public AppUser? StartedBy { get; set; }
    /// <summary>Set by the cancel endpoint (any instance); the runner stops between files.</summary>
    public bool CancelRequested { get; set; }
    /// <summary>Packages in the source repository.</summary>
    public int Total { get; set; }
    /// <summary>Source packages whose file name this platform lacked when the import started.</summary>
    public int Missing { get; set; }
    public int Imported { get; set; }
    public List<SkippedPackage> Skipped { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>A missing package an import did not copy, and why.</summary>
public class SkippedPackage
{
    public string FileName { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>
/// The single settings row (<see cref="SingletonId"/>) of <see cref="Services.AutoUpdater"/>: queue builds of the ports whose
/// recipes changed in the ports tree, every <see cref="IntervalHours"/>, for <see cref="Platforms"/>.
/// </summary>
public class AutoUpdateSettings
{
    public const int SingletonId = 1;
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 6;
    /// <summary>Comma-separated platform names.</summary>
    public string Platforms { get; set; } = "";
    /// <summary>Set by the instance running an update (one atomic UPDATE); null when none runs. A stale value is taken over.</summary>
    public DateTime? RunningSince { get; set; }
    public DateTime? LastRunAt { get; set; }
    /// <summary>The ports tree commit the last successful run processed; the next run diffs from it.</summary>
    public string? LastCommit { get; set; }
    /// <summary>JSON of <see cref="Services.AutoUpdateResult"/>.</summary>
    public string? LastResultJson { get; set; }
}

/// <summary>
/// The build priority of a port on a platform, computed by <see cref="Services.PriorityComputer"/>; the job claim orders by
/// <see cref="Priority"/>. One row per (platform, port) with a recipe.
/// </summary>
public class PortPriority
{
    public string Platform { get; set; } = "";
    public string PortName { get; set; } = "";
    /// <summary>The recipe a build uses on this platform: the newest stable one, else the newest untested, else the newest.</summary>
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    /// <summary>The recipe is stable on the platform and no package of the port was published there when this was computed.</summary>
    public bool Outstanding { get; set; }
    /// <summary>Outstanding ports that need this port built first, directly or through other outstanding ports.</summary>
    public int Priority { get; set; }
    /// <summary>Outstanding ports that need this port directly.</summary>
    public int Dependents { get; set; }
    public DateTime ComputedAt { get; set; }
}

/// <summary>
/// A direct build dependency on a platform, written with <see cref="PortPriority"/>: outstanding port <see cref="PortName"/> needs
/// <see cref="DependsOn"/> built first (see <see cref="Services.PriorityGraph.Compute"/>). Only outstanding ports have rows.
/// </summary>
public class PortDependency
{
    public string Platform { get; set; } = "";
    public string PortName { get; set; } = "";
    public string DependsOn { get; set; } = "";
}
