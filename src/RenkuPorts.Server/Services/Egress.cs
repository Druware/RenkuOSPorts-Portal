using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;

namespace RenkuPorts.Server.Services;

/// <summary>
/// The monthly egress cap on package downloads. Usage is one <see cref="EgressUsage"/> row per UTC month, added to by a single
/// conditional UPDATE, so every server instance shares the count and the cap holds under concurrent downloads.
/// </summary>
public static class Egress
{
    /// <summary>Bytes in one GB of <see cref="EgressSettings.MonthlyCapGb"/> (binary, GiB).</summary>
    public const long BytesPerGb = 1L << 30;

    /// <summary>The usage key for now: the UTC month as "yyyy-MM".</summary>
    public static string Month(TimeProvider time) => time.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds <paramref name="bytes"/> to this month's usage. With <paramref name="enforceCap"/> only if the total stays within the
    /// cap; false then means over the cap and nothing was added. Without it, always adds and returns true.
    /// </summary>
    public static async Task<bool> TryAddAsync(AppDbContext db, TimeProvider time, long bytes, bool enforceCap, CancellationToken ct)
    {
        var month = Month(time);
        for (var attempt = 0; ; attempt++)
        {
            var row = db.EgressUsage.Where(u => u.Month == month);
            if (enforceCap)
                row = row.Where(u => u.BytesServed + bytes <= db.EgressSettings.Where(s => s.Id == EgressSettings.SingletonId).Select(s => s.MonthlyCapGb * BytesPerGb).First());
            if (await row.ExecuteUpdateAsync(s => s.SetProperty(u => u.BytesServed, u => u.BytesServed + bytes), ct) > 0) return true;
            if (attempt > 0 || await db.EgressUsage.AnyAsync(u => u.Month == month, ct)) return false;
            // First download of the month: create the row, then retry the update.
            db.EgressUsage.Add(new EgressUsage { Month = month });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Another instance inserted it first; anything else is a real failure.
                db.ChangeTracker.Clear();
                if (!await db.EgressUsage.AnyAsync(u => u.Month == month, ct)) throw;
            }
        }
    }
}
