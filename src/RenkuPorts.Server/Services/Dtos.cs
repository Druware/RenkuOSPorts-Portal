using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Services;

public record MeDto(string Id, string Email, string DisplayName, IList<string> Roles);

public record ClientDto(Guid Id, string Name, string Platform, string Owner, bool Enabled, DateTime CreatedAt, DateTime? LastSeenAt);

public record JobDto(long Id, string PortName, string Version, string Platform, JobStatus Status, DateTime QueuedAt, string QueuedBy,
    string? ClaimedBy, DateTime? ClaimedAt, DateTime? LeaseExpiresAt, DateTime? CompletedAt, int Attempts, string? FailureSummary,
    bool HasLog, List<string> Packages);

/// <summary>
/// A job as a build client sees it: no owner emails or user ids, no log. <see cref="Mine"/>: claimed by the calling client;
/// <see cref="SameOwner"/>: claimed by another client of the same owner; <see cref="QueuePosition"/>: 1-based place in claim
/// order, for Queued jobs on the caller's platform only.
/// Dependency tree (<see cref="Data.PortDependency"/> edges): <see cref="WaitingOn"/> are the first ports, nearest first, that the job's
/// port needs directly or transitively and that are queued or building on its platform (<see cref="WaitingOnCount"/> counts them all);
/// <see cref="InMyChain"/>: the port is needed by, or needs, the port the calling client is building; <see cref="InOwnerChain"/>: the
/// same for the other clients of its owner; <see cref="Unblocks"/>, on the caller's own current job only: queued jobs that need it.
/// </summary>
public record ClientJobDto(long Id, string PortName, string Version, string Platform, JobStatus Status, DateTime QueuedAt,
    DateTime? ClaimedAt, DateTime? LeaseExpiresAt, DateTime? CompletedAt, int Attempts, int Priority, string? ClaimedBy,
    bool Mine, bool SameOwner, long? QueuePosition, string? FailureSummary,
    List<string> WaitingOn, int WaitingOnCount, bool InMyChain, bool InOwnerChain, int? Unblocks);

public record OwnerClientDto(string Name, string Platform, DateTime? LastSeenAt, long? CurrentJobId);

/// <summary>The calling build client, the job it is building and every client of its owner.</summary>
public record ClientSelfDto(Guid Id, string Name, string Platform, string Owner, ClientJobDto? CurrentJob, List<OwnerClientDto> OwnerClients);

/// <summary>A recipe;<see cref="Priority"/>, <see cref="Dependents"/> and <see cref="Outstanding"/> are its port's <see cref="Data.PortPriority"/>
/// on the platform the list was asked for, null without one or before the port's priority is computed.</summary>
public record RecipeDto(int Id, string PortName, string Category, string Version, string RecipePath, DateTime RecipeUpdatedAt,
    int? Priority = null, int? Dependents = null, bool? Outstanding = null);

public record PackageDto(string FileName, long Size, string Sha256, DateTime BuildDate, DateTime UploadedAt, DateTime RecipeUpdatedAt,
    string PortName, string BuiltBy);

/// <summary>One page of a list; <see cref="Total"/> counts every item matching the filter.</summary>
public record Paged<T>(List<T> Items, int Total, int Page, int PageSize)
{
    /// <summary>
    /// Counts <paramref name="filtered"/>, then takes the requested page of <paramref name="ordered"/> (the same query, with a total order).
    /// <paramref name="pageSize"/> defaults to 50 and is clamped to 1..200; a page below 1 is 1; a page past the end has no items.
    /// </summary>
    public static async Task<Paged<T>> FromAsync<TRow>(IQueryable<TRow> filtered, Func<IQueryable<TRow>, IOrderedQueryable<TRow>> order,
        Expression<Func<TRow, T>> project, int? page, int? pageSize)
    {
        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        var number = Math.Max(page ?? 1, 1);
        var total = await filtered.CountAsync();
        var items = await order(filtered).Skip((int)Math.Min((long)(number - 1) * size, int.MaxValue)).Take(size).Select(project).ToListAsync();
        return new(items, total, number, size);
    }
}

/// <summary>EF-translatable projections, so list endpoints load only the columns they return.</summary>
public static class Project
{
    public static readonly Expression<Func<Job, JobDto>> Job = j => new JobDto(j.Id, j.Recipe!.PortName, j.Recipe.Version, j.Platform,
        j.Status, j.QueuedAt, j.QueuedBy == null ? "auto-update" : j.QueuedBy.DisplayName, j.ClaimedBy == null ? null : j.ClaimedBy.Name, j.ClaimedAt, j.LeaseExpiresAt,
        j.CompletedAt, j.Attempts, j.FailureSummary, j.HasLog, j.Packages.Select(p => p.FileName).ToList());

    public static readonly Expression<Func<BuildClient, ClientDto>> Client = c => new ClientDto(c.Id, c.Name, c.Platform,
        c.Owner!.DisplayName, c.Enabled, c.CreatedAt, c.LastSeenAt);

    public static readonly Expression<Func<Package, PackageDto>> Package = p => new PackageDto(p.FileName, p.Size, p.Sha256,
        p.BuildDate, p.UploadedAt, p.RecipeUpdatedAt, p.PortName, p.Import != null ? "import: " + p.Import.SourceHost : p.BuiltBy!.DisplayName);
}
