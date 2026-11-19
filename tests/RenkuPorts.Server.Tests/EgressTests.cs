using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Services;

namespace RenkuPorts.Server.Tests;

/// <summary>The monthly egress cap: package downloads 404 once the month's bytes would pass it; index files are counted, never blocked.</summary>
public partial class ApiTests
{
    private record EgressView(int MonthlyCapGb, string Month, long BytesServed);

    /// <summary>Clears all usage and sets the cap; this month's usage to <paramref name="bytesServed"/> when given.</summary>
    private Task SetEgressAsync(int capGb, long? bytesServed = null) => app.WithDbAsync(async db =>
    {
        await db.EgressUsage.ExecuteDeleteAsync();
        await db.EgressSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MonthlyCapGb, capGb));
        if (bytesServed is { } b)
        {
            db.EgressUsage.Add(new EgressUsage { Month = Egress.Month(app.Clock), BytesServed = b });
            await db.SaveChangesAsync();
        }
    });

    [Fact]
    public async Task Package_downloads_stop_at_the_monthly_egress_cap()
    {
        var admin = await app.SignedInAsync();
        var name = Hpkg("zlib");
        await PublishAsync(admin, name); // 16 bytes
        var anon = app.CreateClient();
        try
        {
            // Exactly fits under the cap, then the next one would pass it.
            await SetEgressAsync(1, Egress.BytesPerGb - 16);
            Assert.Equal(16, (await anon.GetByteArrayAsync($"/repo/arm64/packages/{name}")).Length);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/repo/arm64/packages/{name}")).StatusCode);
            var usage = (await admin.GetFromJsonAsync<EgressView>("/api/settings/egress"))!;
            Assert.Equal((1, Egress.Month(app.Clock), Egress.BytesPerGb), (usage.MonthlyCapGb, usage.Month, usage.BytesServed));

            // Raising the cap allows downloads again.
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/egress", new { monthlyCapGb = 2 })).StatusCode);
            Assert.Equal(16, (await anon.GetByteArrayAsync($"/repo/arm64/packages/{name}")).Length);

            // A new month starts from zero.
            await SetEgressAsync(1, Egress.BytesPerGb);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/repo/arm64/packages/{name}")).StatusCode);
            app.Clock.Offset = TimeSpan.FromDays(32);
            Assert.Equal(16, (await anon.GetByteArrayAsync($"/repo/arm64/packages/{name}")).Length);
            var next = (await (await app.SignedInAsync()).GetFromJsonAsync<EgressView>("/api/settings/egress"))!;
            Assert.Equal((Egress.Month(app.Clock), 16L), (next.Month, next.BytesServed));
            Assert.NotEqual(usage.Month, next.Month);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
            await SetEgressAsync(800);
        }
    }

    [Fact]
    public async Task Index_files_are_served_and_counted_over_the_egress_cap()
    {
        var blob = Blobs.GetBlobClient(RepoIndexer.IndexBlob("x86", "repo.info"));
        await blob.UploadAsync(BinaryData.FromBytes([1, 2, 3]), overwrite: true);
        try
        {
            await SetEgressAsync(1, Egress.BytesPerGb);
            Assert.Equal([1, 2, 3], await app.CreateClient().GetByteArrayAsync("/repo/x86/repo.info"));
            var usage = (await (await app.SignedInAsync()).GetFromJsonAsync<EgressView>("/api/settings/egress"))!;
            Assert.Equal(Egress.BytesPerGb + 3, usage.BytesServed);
        }
        finally
        {
            await blob.DeleteIfExistsAsync();
            await SetEgressAsync(800);
        }
    }

    [Fact]
    public async Task Egress_settings_validate_and_are_admin_only()
    {
        var admin = await app.SignedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/egress", new { monthlyCapGb = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/egress", new { monthlyCapGb = -5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/egress", new { monthlyCapGb = 1_000_001 })).StatusCode);
        Assert.Equal(800, (await admin.GetFromJsonAsync<EgressView>("/api/settings/egress"))!.MonthlyCapGb);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.BuilderAsync()).GetAsync("/api/settings/egress")).StatusCode);
    }
}
