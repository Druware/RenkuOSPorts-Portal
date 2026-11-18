using System.Linq.Expressions;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Services;

public record MeDto(string Id, string Email, string DisplayName, IList<string> Roles);

public record ClientDto(Guid Id, string Name, string Platform, string Owner, bool Enabled, DateTime CreatedAt, DateTime? LastSeenAt);

public record JobDto(long Id, string PortName, string Version, string Platform, JobStatus Status, DateTime QueuedAt, string QueuedBy,
    string? ClaimedBy, DateTime? ClaimedAt, DateTime? LeaseExpiresAt, DateTime? CompletedAt, int Attempts, string? FailureSummary,
    bool HasLog, List<string> Packages);

public record PackageDto(string FileName, long Size, string Sha256, DateTime BuildDate, DateTime UploadedAt, DateTime RecipeUpdatedAt,
    string PortName, string BuiltBy);

/// <summary>EF-translatable projections, so list endpoints load only the columns they return.</summary>
public static class Project
{
    public static readonly Expression<Func<Job, JobDto>> Job = j => new JobDto(j.Id, j.Recipe!.PortName, j.Recipe.Version, j.Platform,
        j.Status, j.QueuedAt, j.QueuedBy!.DisplayName, j.ClaimedBy == null ? null : j.ClaimedBy.Name, j.ClaimedAt, j.LeaseExpiresAt,
        j.CompletedAt, j.Attempts, j.FailureSummary, j.HasLog, j.Packages.Select(p => p.FileName).ToList());

    public static readonly Expression<Func<BuildClient, ClientDto>> Client = c => new ClientDto(c.Id, c.Name, c.Platform,
        c.Owner!.DisplayName, c.Enabled, c.CreatedAt, c.LastSeenAt);

    public static readonly Expression<Func<Package, PackageDto>> Package = p => new PackageDto(p.FileName, p.Size, p.Sha256,
        p.BuildDate, p.UploadedAt, p.RecipeUpdatedAt, p.PortName, p.BuiltBy!.DisplayName);
}
