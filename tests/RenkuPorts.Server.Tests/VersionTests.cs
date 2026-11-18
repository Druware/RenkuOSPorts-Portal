using System.Net.Http.Json;
using System.Reflection;
using RenkuPorts.Server.Endpoints;

namespace RenkuPorts.Server.Tests;

/// <summary>In the "Database" collection with ApiTests: each AppFactory recreates the shared test database, so they must not run in parallel.</summary>
[Collection("Database")]
public class VersionTests(AppFactory app) : IClassFixture<AppFactory>
{
    private record VersionView(string Version, string? Commit);

    [Fact]
    public async Task Version_endpoint_reports_the_build_version()
    {
        // Directory.Build.props gives every project the same -p:Version, so the test assembly's own version is the expected one.
        var expected = typeof(VersionTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var v = await app.CreateClient().GetFromJsonAsync<VersionView>("/api/version");
        Assert.Equal(VersionEndpoints.Split(expected), (v!.Version, v.Commit));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", null)]
    [InlineData("1.2.4-dev.5+gabc1234", "1.2.4-dev.5", "abc1234")]
    [InlineData("1.2.4-dev.0+gabc1234.dirty", "1.2.4-dev.0", "abc1234.dirty")]
    [InlineData("0.0.0-dev", "0.0.0-dev", null)]
    public void Split_separates_the_commit(string informational, string version, string? commit) =>
        Assert.Equal((version, commit), VersionEndpoints.Split(informational));
}
