namespace FiveMDiagnostics.Core;

/// <summary>Which processes a player could close before playing, as opposed to the game and Windows itself.</summary>
public static class BackgroundProgram
{
    /// <summary>
    /// Parts of Windows, the game and this app's own tools. Closing any of them breaks something, so they
    /// are never suggested or offered.
    /// </summary>
    private static readonly HashSet<string> NeverClosed = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "csrss", "dwm", "explorer", "svchost", "sihost",
        "ctfmon", "taskhostw", "RuntimeBroker", "SearchHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "audiodg",
        "MsMpEng", "SecurityHealthSystray", "LockApp", "WidgetService", "Widgets",
        "FiveMDiagnostics", "PresentMon", "wpr", "wprui",
    };

    /// <summary>Whether <paramref name="processName"/> is a program the player could close.</summary>
    public static bool IsCandidate(string processName) =>
        !string.IsNullOrWhiteSpace(processName)
        && !NeverClosed.Contains(processName)
        && !processName.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase)
        // The game runs as several processes, its NUI browser among them, and all carry one of these.
        && !processName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)
        && !processName.Contains("GTA", StringComparison.OrdinalIgnoreCase);
}
