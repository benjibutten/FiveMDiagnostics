using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace FiveMDiagnostics.App.Wpf.Services;

using FiveMDiagnostics.Core;

/// <summary>A program on the starter list, and whether a settings file without a choice had it ticked.</summary>
public sealed record PreLaunchStarterApp(string Name, string[] ProcessNames, bool TickedWhenUnset);

public sealed record PreLaunchResult(IReadOnlyList<string> Closed, IReadOnlyList<string> Failed);

/// <summary>A program running with a window, as the add list offers it.</summary>
/// <param name="Name">The program's own description when it has one, otherwise its process name.</param>
public sealed record RunningProgram(string Name, string ProcessName);

/// <summary>
/// Closes the apps that should not be running with the game, then starts FiveM.
/// </summary>
public static class PreLaunch
{
    /// <summary>
    /// The list a settings file without its own list is given. A new install starts with an empty list
    /// and builds its own from the programs it runs and the suggestions.
    /// </summary>
    public static IReadOnlyList<PreLaunchStarterApp> StarterApps { get; } =
    [
        new("Steam", ["steam", "steamwebhelper", "steamservice"], TickedWhenUnset: true),
        new("OneDrive", ["OneDrive", "OneDrive.Sync.Service"], TickedWhenUnset: true),
        new("Voicemod", ["Voicemod"], TickedWhenUnset: true),
        new("ChatGPT", ["ChatGPT"], TickedWhenUnset: true),
        new("Photos", ["Photos"], TickedWhenUnset: true),
        new("Movies & TV", ["Video.UI"], TickedWhenUnset: true),
        new("Chrome", ["chrome"], TickedWhenUnset: false),
        new("Edge", ["msedge"], TickedWhenUnset: false),
        new("Spotify", ["Spotify"], TickedWhenUnset: false),
    ];

    public static string FiveMPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.exe");

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

    public static bool IsRunning(PreLaunchAppEntry app, HashSet<string> runningNames) =>
        app.ProcessNames.Any(runningNames.Contains);

    /// <summary>
    /// Gives a settings file without its own list one built from <see cref="StarterApps"/>, ticked as
    /// <see cref="DiagnosticsSettings.PreLaunchClose"/> says, and clears that field.
    /// </summary>
    /// <returns>Whether the settings changed and should be saved.</returns>
    public static bool MigrateList(DiagnosticsSettings settings)
    {
        if (settings.PreLaunchApps is not null)
        {
            return false;
        }

        settings.PreLaunchApps = StarterApps
            .Select(app => new PreLaunchAppEntry
            {
                Name = app.Name,
                ProcessNames = [.. app.ProcessNames],
                Close = settings.PreLaunchClose?.Contains(app.Name, StringComparer.OrdinalIgnoreCase) ?? app.TickedWhenUnset,
            })
            .ToList();
        settings.PreLaunchClose = null;
        return true;
    }

    /// <summary>
    /// Programs running with a visible window that the player could close, one per process name, by name.
    /// </summary>
    public static IReadOnlyList<RunningProgram> RunningPrograms()
    {
        var programs = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (NameOf(process) is not { } name
                    || programs.ContainsKey(name)
                    || !BackgroundProgram.IsCandidate(name)
                    || process.MainWindowHandle == 0)
                {
                    continue;
                }

                programs[name] = new RunningProgram(DescriptionOf(process) ?? name, name);
            }
            catch (InvalidOperationException)
            {
                // Exited while being looked at.
            }
            finally
            {
                process.Dispose();
            }
        }

        return programs.Values.OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>
    /// The executable's own description ("Spotify", "Google Chrome"). Null when it has none or cannot be
    /// read, which is the case for a process running as administrator when this app is not.
    /// </summary>
    private static string? DescriptionOf(Process process)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether any part of FiveM is up: the launcher, the game, or one of its helpers. The session's
    /// resolver only accepts the rendering process, which does not exist yet while the launcher loads.
    /// </summary>
    public static bool IsFiveMRunning(HashSet<string> runningNames) =>
        runningNames.Any(name => name.Equals("FiveM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("FiveM_", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Closes every process of the given apps, asking a windowed process to close before killing it, and
    /// waits for them to be gone.
    /// </summary>
    public static PreLaunchResult Close(IReadOnlyList<PreLaunchAppEntry> apps)
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

                // Count rather than All, which would stop at the first process that refuses.
                (matching.Count(CloseOrKill) == matching.Length ? closed : failed).Add(app.Name);
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
    /// <param name="arguments">Command-line arguments FiveM is started with; empty for none.</param>
    /// <returns>The shortcut to hand to <see cref="StartFiveM"/>.</returns>
    public static string PrepareFiveMShortcut(string arguments)
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
        shortcut.Arguments = arguments.Trim();
        shortcut.WorkingDirectory = Path.GetDirectoryName(FiveMPath);
        shortcut.Save();

        return shortcutPath;
    }

    public static void StartFiveM(string shortcutPath)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{shortcutPath}\"") { UseShellExecute = false });
    }

    private static bool CloseOrKill(Process process)
    {
        try
        {
            // A window that is asked first gets to save; one that has none, like a tray app, is killed.
            if (process.CloseMainWindow() && process.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                return true;
            }

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
