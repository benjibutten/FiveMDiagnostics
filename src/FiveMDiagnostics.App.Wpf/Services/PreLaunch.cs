using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace FiveMDiagnostics.App.Wpf.Services;

/// <summary>An app that can be closed before FiveM starts, and every process name it runs under.</summary>
public sealed record PreLaunchApp(string Name, string[] ProcessNames, bool ClosedByDefault);

public sealed record PreLaunchResult(IReadOnlyList<string> Closed, IReadOnlyList<string> Failed);

/// <summary>
/// Closes the apps that should not be running with the game, then starts FiveM.
/// </summary>
public static class PreLaunch
{
    /// <summary>
    /// Everything the session logs have shown running beside the game that is not part of the stream
    /// or the voice chain. OBS, StreamDeck, TwitchOverlayHelper, MicMixer (the test build included) and
    /// Discord are left off on purpose: closing them breaks the evening rather than cleaning it.
    /// </summary>
    /// <remarks>
    /// Ticked by default are the ones the notes have held against an evening: Steam's client in the
    /// 09-12 crash, OneDrive's sync on the CPU, Voicemod starting mid-game on 09-11, and the idle apps
    /// holding VRAM. Browsers and Spotify are listed but left for the user.
    /// </remarks>
    public static IReadOnlyList<PreLaunchApp> Apps { get; } =
    [
        new("Steam", ["steam", "steamwebhelper", "steamservice"], true),
        new("OneDrive", ["OneDrive", "OneDrive.Sync.Service"], true),
        new("Voicemod", ["Voicemod"], true),
        new("ChatGPT", ["ChatGPT"], true),
        new("Photos", ["Photos"], true),
        new("Movies & TV", ["Video.UI"], true),
        new("Chrome", ["chrome"], false),
        new("Edge", ["msedge"], false),
        new("Spotify", ["Spotify"], false),
    ];

    public static string FiveMPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.exe");

    private const string FiveMArguments = "-pure_1";

    public static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            if (NameOf(process) is { } name)
            {
                names.Add(name);
            }

            process.Dispose();
        }

        return names;
    }

    public static bool IsRunning(PreLaunchApp app, HashSet<string> runningNames) =>
        app.ProcessNames.Any(runningNames.Contains);

    /// <summary>
    /// Whether any part of FiveM is up: the launcher, the game, or one of its helpers. The session's
    /// resolver only accepts the rendering process, which does not exist yet while the launcher loads.
    /// </summary>
    public static bool IsFiveMRunning(HashSet<string> runningNames) =>
        runningNames.Any(name => name.Equals("FiveM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("FiveM_", StringComparison.OrdinalIgnoreCase));

    /// <summary>Kills every process of the given apps and waits for them to be gone.</summary>
    public static PreLaunchResult Close(IReadOnlyList<PreLaunchApp> apps)
    {
        var closed = new List<string>();
        var failed = new List<string>();
        var processes = Process.GetProcesses();

        try
        {
            foreach (var app in apps)
            {
                var matching = processes
                    .Where(process => NameOf(process) is { } name && app.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (matching.Length == 0)
                {
                    continue;
                }

                // Count rather than All, which would stop killing at the first process that refuses.
                (matching.Count(Kill) == matching.Length ? closed : failed).Add(app.Name);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return new PreLaunchResult(closed, failed);
    }

    /// <summary>
    /// Writes the shortcut FiveM is started through, before anything is closed, so a launch that cannot
    /// happen fails while the user's apps are still open.
    /// </summary>
    /// <remarks>
    /// A shortcut opened by Explorer, so FiveM runs with the shell's ordinary rights and Explorer as its
    /// parent, the same as a launch from the desktop. Started directly it would inherit this app's
    /// elevation, and a game running as administrator is a different evening from the ones it is
    /// compared against. A shortcut because Explorer passes no arguments to an exe it opens.
    /// </remarks>
    /// <returns>The shortcut to hand to <see cref="StartFiveM"/>.</returns>
    public static string PrepareFiveMShortcut()
    {
        if (!File.Exists(FiveMPath))
        {
            throw new FileNotFoundException(FiveMPath);
        }

        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveMDiagnostics", "FiveM.lnk");

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        var shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = FiveMPath;
        shortcut.Arguments = FiveMArguments;
        shortcut.WorkingDirectory = Path.GetDirectoryName(FiveMPath);
        shortcut.Save();

        return shortcutPath;
    }

    public static void StartFiveM(string shortcutPath)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{shortcutPath}\"") { UseShellExecute = false });
    }

    private static bool Kill(Process process)
    {
        try
        {
            process.Kill();
            return process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // Exited on its own between the snapshot and the kill.
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Null for a process that exited or refuses access after the snapshot was taken. On .NET 10 the name
    /// can be looked up per process id rather than read from the snapshot, and that lookup throws.
    /// </summary>
    private static string? NameOf(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
