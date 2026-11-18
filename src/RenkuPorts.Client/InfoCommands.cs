using System.Text;

namespace RenkuPorts.Client;

public record ClientJob(long Id, string PortName, string Version, string Platform, string Status, DateTime QueuedAt, DateTime? ClaimedAt,
    DateTime? LeaseExpiresAt, int Attempts, int Priority, string? ClaimedBy, bool Mine, bool SameOwner, long? QueuePosition,
    string? FailureSummary, List<string> WaitingOn, int WaitingOnCount, bool InMyChain, bool InOwnerChain, int? Unblocks);
public record JobPage(List<ClientJob> Items, int Total, int Page, int PageSize);
public record OwnerClient(string Name, string Platform, DateTime? LastSeenAt, long? CurrentJobId);
public record ClientSelf(Guid Id, string Name, string Platform, string Owner, ClientJob? CurrentJob, List<OwnerClient> OwnerClients);

/// <summary>The read-only <c>jobs</c> and <c>status</c> commands: GET /api/client/jobs and /api/client/me, printed as text.</summary>
public static class InfoCommands
{
    public const string Usage = "usage: renkuports-client jobs [--mine|--owner] [--status S] [--platform P] [--page N]\n       renkuports-client status";

    /// <summary>
    /// Splits the arguments after the command into its query string (for <c>jobs</c>) and the rest, which are configuration
    /// (<c>--Server x</c>). False on a missing value or both --mine and --owner.
    /// </summary>
    public static bool TryParse(string command, string[] args, out string query, out string[] configArgs)
    {
        var q = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var name = command == "jobs" ? args[i] switch
            {
                "--status" => "status", "--platform" => "platform", "--page" => "page", _ => null,
            } : null;
            if (command == "jobs" && args[i] is "--mine" or "--owner") q.Add("scope=" + args[i][2..]);
            else if (name is null) rest.Add(args[i]);
            else if (i + 1 < args.Length) q.Add($"{name}={Uri.EscapeDataString(args[++i])}");
            else { (query, configArgs) = ("", []); return false; }
        }
        (query, configArgs) = (q.Count == 0 ? "" : "?" + string.Join('&', q), rest.ToArray());
        return q.Count(x => x.StartsWith("scope=")) <= 1;
    }

    public static async Task<int> RunAsync(BuildClient client, string command, string query, CancellationToken ct)
    {
        try
        {
            Console.Write(command == "jobs"
                ? FormatJobs(await client.GetAsync<JobPage>("api/client/jobs" + query, ct))
                : FormatStatus(await client.GetAsync<ClientSelf>("api/client/me", ct)));
            return 0;
        }
        catch (Exception e) when (e is HttpRequestException or ApiException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    /// <summary>One row per job; the first column marks who holds it (* this client, o another client of the owner).</summary>
    public static string FormatJobs(JobPage page)
    {
        var sb = new StringBuilder();
        var rows = page.Items.Select(j => new[]
        {
            j.Mine ? "*" : j.SameOwner ? "o" : " ", j.Id.ToString(), $"{j.PortName}-{j.Version}", j.Platform, j.Status,
            j.QueuePosition is { } p ? $"#{p}" : j.ClaimedBy ?? "", Chain(j), Waiting(j),
        }).Prepend([" ", "ID", "PORT-VERSION", "PLATFORM", "STATUS", "POS/CLAIMED-BY", "CHAIN", "WAITING ON"]).ToList();
        var widths = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        foreach (var r in rows) sb.AppendLine(string.Join("  ", r.Select((v, c) => v.PadRight(widths[c]))).TrimEnd());
        var pages = Math.Max(1, (page.Total + page.PageSize - 1) / page.PageSize);
        sb.AppendLine($"page {page.Page}/{pages}, {page.Total} job(s). * this client, o another client of your owner; " +
            "chain: in the dependency tree of what this client (mine) or your other clients (owner) build.");
        return sb.ToString();
    }

    public static string FormatStatus(ClientSelf me)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"client   {me.Name} ({me.Platform}), owner {me.Owner}, id {me.Id}");
        if (me.CurrentJob is { } j)
        {
            sb.AppendLine($"building job {j.Id} {j.PortName}-{j.Version} since {j.ClaimedAt:u}, lease until {j.LeaseExpiresAt:u}, attempt {j.Attempts}");
            sb.AppendLine($"         unblocks {j.Unblocks ?? 0} queued job(s){(j.WaitingOnCount > 0 ? $"; waiting on: {Waiting(j)}" : "")}");
        }
        else sb.AppendLine("building nothing");
        sb.AppendLine("owner's clients:");
        var rows = me.OwnerClients.Select(c => new[]
        {
            c.Name == me.Name ? "*" : " ", c.Name, c.Platform, c.LastSeenAt?.ToString("u") ?? "never", c.CurrentJobId?.ToString() ?? "-",
        }).Prepend([" ", "NAME", "PLATFORM", "LAST SEEN", "JOB"]).ToList();
        var widths = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        foreach (var r in rows) sb.AppendLine("  " + string.Join("  ", r.Select((v, c) => v.PadRight(widths[c]))).TrimEnd());
        return sb.ToString();
    }

    private static string Chain(ClientJob j) => string.Join(", ", new[]
    {
        j.InMyChain && j.InOwnerChain ? "mine+owner" : j.InMyChain ? "mine" : j.InOwnerChain ? "owner" : "",
        j.Unblocks is > 0 ? $"unblocks {j.Unblocks}" : "",
    }.Where(x => x != ""));

    private static string Waiting(ClientJob j) =>
        string.Join(", ", j.WaitingOn) + (j.WaitingOnCount > j.WaitingOn.Count ? $" (+{j.WaitingOnCount - j.WaitingOn.Count})" : "");
}
