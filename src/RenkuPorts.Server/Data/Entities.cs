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
    public string QueuedByUserId { get; set; } = "";
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
}
