using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace FiveMDiagnostics.App.Wpf.Services;

/// <summary>
/// Whether the app runs as administrator, and the restart that makes it do so.
/// </summary>
/// <remarks>
/// Deep capture through WPR needs an administrator; frame capture through PresentMon does not, for a
/// game running on the same account. The app starts either way, so a player can see what is missing
/// before deciding to accept the UAC prompt.
/// </remarks>
internal static class Elevation
{
    private const string AfterArgument = "--after";

    /// <summary>ERROR_CANCELLED: the user said no to the UAC prompt.</summary>
    private const int Cancelled = 1223;

    public static bool IsElevated { get; } = Check();

    /// <summary>
    /// Starts this executable again as administrator, with arguments that make the new instance wait for
    /// this one to exit before it claims the single-instance lock.
    /// </summary>
    /// <returns>False when the user declined the UAC prompt; this instance should then keep running.</returns>
    public static bool TryRestartElevated()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { AfterArgument, Environment.ProcessId.ToString() },
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for the instance that restarted this one to exit, when the arguments name one. Returns at
    /// once otherwise, and gives up after ten seconds so a stuck predecessor cannot hold the app back.
    /// </summary>
    public static void WaitForPredecessor(string[] args)
    {
        var index = Array.FindIndex(args, arg => arg.Equals(AfterArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var processId))
        {
            return;
        }

        try
        {
            using var predecessor = Process.GetProcessById(processId);
            predecessor.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
