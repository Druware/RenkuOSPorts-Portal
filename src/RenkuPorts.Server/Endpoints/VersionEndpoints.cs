using System.Reflection;

namespace RenkuPorts.Server.Endpoints;

/// <summary>The server's build version (set at build time with <c>-p:Version=$(scripts/version.sh)</c>).</summary>
public static class VersionEndpoints
{
    /// <summary>AssemblyInformationalVersion, e.g. <c>1.2.3</c> or <c>1.2.4-dev.5+gabc1234</c>.</summary>
    public static readonly string Informational =
        typeof(VersionEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

    /// <summary>Splits off the build metadata: <c>1.2.4-dev.5+gabc1234</c> gives (<c>1.2.4-dev.5</c>, <c>abc1234</c>); a tagged release has no commit.</summary>
    public static (string Version, string? Commit) Split(string informational) => informational.Split('+', 2) is [var v, var meta]
        ? (v, meta.StartsWith('g') ? meta[1..] : meta)
        : (informational, null);

    public static void Map(WebApplication app)
    {
        var (version, commit) = Split(Informational);
        app.MapGet("/api/version", () => new { version, commit });
    }
}
