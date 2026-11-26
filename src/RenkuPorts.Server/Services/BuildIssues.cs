using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Services;

/// <summary>
/// Files build failures under <see cref="BuildIssue"/>s: the same failure (by <see cref="Signature"/>) on a platform is one issue, counted
/// until an admin resolves it, and a port with a failure under an unresolved issue is not queued again there.
/// </summary>
public static partial class BuildIssues
{
    public const int MaxSignature = 400;
    public const int MaxTitle = 400;

    /// <summary>
    /// The failure text with what varies between ports and runs replaced: lowercased; absolute paths, hex hashes, name-version tokens
    /// (<c>foo-1.2</c>), the bare port name (when given), versions and numbers become placeholders; whitespace collapsed; at most <see cref="MaxSignature"/> chars.
    /// </summary>
    public static string Signature(string summary, string? port = null)
    {
        var s = summary.ToLowerInvariant();
        s = AbsolutePath().Replace(s, "<path>");
        s = HexHash().Replace(s, "<hash>");
        s = NameVersion().Replace(s, "<pkg>");
        // After name-version tokens, so "foo-1.2" reads the same whether or not foo is the job's own port.
        if (!string.IsNullOrEmpty(port)) s = Regex.Replace(s, $@"(?<![\w.+<-]){Regex.Escape(port.ToLowerInvariant())}(?![\w+-])", "<port>");
        s = Version().Replace(s, "<ver>");
        s = Number().Replace(s, "#");
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length > MaxSignature ? s[..MaxSignature] : s;
    }

    /// <summary>Environment for a released job or a summary naming the environment; Dependency when it mentions resolving or requiring; else Recipe.</summary>
    public static BuildIssueKind Kind(string summary, bool released = false)
    {
        var s = summary.ToLowerInvariant();
        if (released || s.Contains("environment")) return BuildIssueKind.Environment;
        if (s.Contains("resolve") || s.Contains("dependenc") || s.Contains("requires")) return BuildIssueKind.Dependency;
        return BuildIssueKind.Recipe;
    }

    /// <summary>
    /// Files <paramref name="job"/>'s failure: the platform's unresolved issue with the same signature, or a new one, has its count and last-seen
    /// time bumped and becomes <see cref="Job.Issue"/>. Nothing is saved: the caller saves it with the job, in one SaveChanges.
    /// Returns the issue and whether it is new (its id is set once saved).
    /// </summary>
    /// <remarks>shortcut: two first failures saved at the same moment can open two issues with one signature, and concurrent count bumps can lose one; add a filtered unique index and a retry if that shows up.</remarks>
    public static async Task<(BuildIssue Issue, bool Created)> CaptureAsync(AppDbContext db, Job job, string summary, BuildIssueKind kind, DateTime now)
    {
        var port = job.Recipe?.PortName ?? await db.Recipes.Where(r => r.Id == job.RecipeId).Select(r => r.PortName).FirstAsync();
        var signature = Signature(summary, port);
        var issue = await db.BuildIssues.Where(i => i.Platform == job.Platform && i.Signature == signature && i.Status != BuildIssueStatus.Resolved)
            .OrderBy(i => i.Id).FirstOrDefaultAsync();
        var created = issue is null;
        if (issue is null)
        {
            issue = new BuildIssue
            {
                Platform = job.Platform, Signature = signature, Kind = kind, Status = BuildIssueStatus.Open,
                Title = summary.Length > MaxTitle ? summary[..MaxTitle] : summary, FirstSeenAt = now, ExampleJobId = job.Id,
            };
            db.BuildIssues.Add(issue);
        }
        issue.Count++;
        issue.LastSeenAt = now;
        job.Issue = issue;
        return (issue, created);
    }

    /// <summary>
    /// Jobs that block their port on their platform: still Failed, and filed under an issue that is not Resolved. A failed job an admin
    /// approved anyway (Succeeded) or requeued no longer blocks, though it may still point at its issue.
    /// </summary>
    public static IQueryable<Job> Blocking(AppDbContext db) =>
        db.Jobs.Where(j => j.Status == JobStatus.Failed && j.Issue != null && j.Issue.Status != BuildIssueStatus.Resolved);

    /// <summary>The platforms of <paramref name="platforms"/> where <paramref name="port"/> (any version) is blocked, with the lowest blocking issue id.</summary>
    public static async Task<Dictionary<string, long>> BlockingAsync(AppDbContext db, string port, IEnumerable<string> platforms)
    {
        var list = platforms.ToList();
        var rows = await Blocking(db).Where(j => j.Recipe!.PortName == port && list.Contains(j.Platform))
            .Select(j => new { j.Platform, IssueId = j.IssueId!.Value }).ToListAsync();
        return rows.GroupBy(r => r.Platform).ToDictionary(g => g.Key, g => g.Min(r => r.IssueId));
    }

    [GeneratedRegex(@"(?<![\w.~])/[^\s:'""()\[\],;]+")]
    private static partial Regex AbsolutePath();

    /// <summary>7+ hex digits with at least one digit (so plain words like "deadline" survive).</summary>
    [GeneratedRegex(@"\b(?=[0-9a-f]*[0-9])[0-9a-f]{7,}\b")]
    private static partial Regex HexHash();

    [GeneratedRegex(@"\b[a-z][\w+]*-[0-9][\w.~+-]*")]
    private static partial Regex NameVersion();

    [GeneratedRegex(@"\b[0-9]+(?:\.[0-9]+)+[\w~]*")]
    private static partial Regex Version();

    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
