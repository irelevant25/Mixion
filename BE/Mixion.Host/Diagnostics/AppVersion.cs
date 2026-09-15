using System.Reflection;

namespace Mixion.Host.Diagnostics;

/// <summary>
/// The version Mixion reports — <c>/api/health</c>, the <c>ping</c> RPC, the
/// startup log line and the tray tooltip. It is the assembly's informational
/// version, which <c>build.ps1</c> stamps from <c>-Version</c> or the git tags
/// (<c>1.2.0</c>, or <c>1.2.0-3-gabc1234</c> for work after a release), without
/// the <c>+commit</c> build metadata the SDK appends. The four-part assembly
/// version (<c>1.2.0.0</c>) can't carry those labels, so it is only a fallback.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(AppVersion).Assembly.GetName().Version);

    internal static string Resolve(string? informationalVersion, Version? assemblyVersion)
    {
        var version = informationalVersion?.Split('+', 2)[0].Trim();
        if (!string.IsNullOrEmpty(version)) return version;

        return assemblyVersion is null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
    }
}
