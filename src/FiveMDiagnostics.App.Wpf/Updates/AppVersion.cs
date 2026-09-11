using System.Reflection;

namespace FiveMDiagnostics.App.Wpf.Updates;

internal static class AppVersion
{
    /// <summary>What the csproj stamps on a build that did not come from the release workflow.</summary>
    private const string LocalBuildMarker = "-local";

    static AppVersion()
    {
        string? informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = string.IsNullOrWhiteSpace(informational)
            ? "1.0.0" + LocalBuildMarker
            : informational.Split('+')[0];

        DisplayText = $"v{version}";
        IsReleaseBuild = !version.EndsWith(LocalBuildMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stamped assembly version, which is what the updater compares against release
    /// tags. <see cref="DisplayText"/> is for the user and cannot be compared.
    /// </summary>
    public static Version? Current { get; } = typeof(AppVersion).Assembly.GetName().Version;

    /// <summary>Version as built, without the "+&lt;commit&gt;" suffix the SDK appends.</summary>
    public static string DisplayText { get; }

    /// <summary>
    /// False for a build made from the source tree. Such a build carries 1.0.0.0, so
    /// every release looks newer than it, and installing one over a working copy is not
    /// what anyone asked for.
    /// </summary>
    public static bool IsReleaseBuild { get; }
}
