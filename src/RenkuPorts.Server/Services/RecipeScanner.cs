using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Services;

/// <summary>Syncs the Recipes table from a HaikuPorts-layout tree: <c>&lt;category&gt;/&lt;dir&gt;/&lt;port&gt;-&lt;version&gt;.recipe</c>.</summary>
public static class RecipeScanner
{
    public record Result(int Added, int Updated, int Total);

    public static async Task<Result> SyncAsync(AppDbContext db, string root, ILogger log)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"PortsTree:Path '{root}' does not exist");
        var commitDates = GitCommitDates(root, log);
        var existing = await db.Recipes.ToDictionaryAsync(r => r.RecipePath);
        int added = 0, updated = 0, total = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.recipe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var parts = rel.Split('/');
            if (parts.Length != 3 || parts[0].StartsWith('.')) continue;
            // The port directory may differ from the port name (e.g. openexr/openexr30-3.0.5.recipe); versions have no "-".
            var stem = Path.GetFileNameWithoutExtension(parts[2]);
            var dash = stem.LastIndexOf('-');
            if (dash <= 0 || dash == stem.Length - 1) continue;
            total++;
            var updatedAt = commitDates.TryGetValue(rel, out var d) ? d : File.GetLastWriteTimeUtc(file);

            if (!existing.TryGetValue(rel, out var r))
            {
                db.Recipes.Add(new Recipe { PortName = stem[..dash], Version = stem[(dash + 1)..], Category = parts[0], RecipePath = rel, RecipeUpdatedAt = updatedAt });
                added++;
            }
            else if (r.RecipeUpdatedAt != updatedAt)
            {
                r.RecipeUpdatedAt = updatedAt;
                updated++;
            }
        }
        await db.SaveChangesAsync();
        return new Result(added, updated, total);
    }

    /// <summary>
    /// Makes <paramref name="root"/> a checkout of <paramref name="branch"/> of <paramref name="url"/>: clones it if absent, otherwise
    /// fetches and hard-resets to the branch tip. Blobless clone: the commit-date walk needs commits and trees, not old file contents.
    /// </summary>
    public static async Task UpdateCheckoutAsync(string root, string url, string branch)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")))
            await GitAsync(null, "clone", "--quiet", "--filter=blob:none", "--single-branch", "--branch", branch, "--", url, root);
        else
        {
            await GitAsync(root, "remote", "set-url", "origin", url);
            await GitAsync(root, "fetch", "--quiet", "--filter=blob:none", "origin", branch);
            await GitAsync(root, "reset", "--quiet", "--hard", "FETCH_HEAD");
        }
    }

    /// <summary>Runs git (any repository owner allowed) and returns its standard output; throws on a non-zero exit.</summary>
    public static async Task<string> GitAsync(string? dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.directory=*");
        if (dir is not null) { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args[0]} failed ({p.ExitCode}): {stderr.Trim()}");
        return await stdout;
    }

    /// <summary>Last commit date of every recipe path, from one <c>git log</c> walk (newest first, so the first hit wins). Empty if git is unavailable.</summary>
    private static Dictionary<string, DateTime> GitCommitDates(string root, ILogger log)
    {
        var dates = new Dictionary<string, DateTime>();
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            // The tree is usually a read-only mount owned by another uid.
            foreach (var a in new[] { "-c", "safe.directory=*", "-C", root, "log", "--format=%x00%cI", "--name-only", "--no-renames", "--", "*.recipe" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            DateTime current = default;
            while (p.StandardOutput.ReadLine() is { } line)
            {
                if (line.StartsWith('\0')) current = DateTimeOffset.Parse(line[1..]).UtcDateTime;
                else if (line.Length > 0) dates.TryAdd(line, current);
            }
            p.WaitForExit();
            if (p.ExitCode != 0) log.LogWarning("git log in {Root} failed ({Code}): {Err}; using file times", root, p.ExitCode, stderr.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogWarning(e, "git not available; using file times for recipe dates");
        }
        return dates;
    }
}
