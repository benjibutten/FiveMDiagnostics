using System.ComponentModel;
using System.Diagnostics;

namespace FiveMDiagnostics.Core;

/// <summary>
/// Answers whether a resolved target process is still the process it was resolved as.
/// </summary>
/// <remarks>
/// Liveness on its own is not that question. Windows hands out process ids from a small pool and reuses
/// them within seconds, so a PID that answers "running" moments after FiveM exited can belong to
/// anything — and every consumer of a stale PID then acts on the wrong process. PresentMon is the worst
/// case: it attaches to whatever owns the id and reports that process's frames as FiveM's.
/// </remarks>
public static class ProcessIdentity
{
    /// <summary>
    /// Start times come from the same source on both sides, so they compare exactly; the tolerance is
    /// only there so a value that made a round trip through a different precision still matches.
    /// </summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether <paramref name="target"/>'s PID still belongs to the same process it named when it was
    /// resolved.
    /// </summary>
    public static bool StillMatches(TargetProcessInfo target)
    {
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            // The name is the check that always works. A reused id would have to land on a process named
            // FiveM-something for this to pass, which the start time below then rules out anyway.
            if (!string.Equals(process.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Null when the start time could not be read at resolve time, which is a permission problem
            // rather than a mismatch — the name check stands alone in that case.
            if (target.StartedAt is not { } startedAt)
            {
                return true;
            }

            return TryGetStartTime(process) is not { } current
                || (current - startedAt).Duration() <= StartTimeTolerance;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ArgumentException is what "no process with that id" looks like.
            return false;
        }
    }

    /// <summary>
    /// The process start time, or null when it cannot be read. Protected processes and processes owned
    /// by another user refuse the query, and that is not evidence of anything either way.
    /// </summary>
    public static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The start time of the given PID, or null when the process is gone or will not say.
    /// </summary>
    public static DateTimeOffset? TryGetStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryGetStartTime(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
