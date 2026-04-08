using System.Diagnostics;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

public sealed class FiveMTargetProcessResolver : ITargetProcessResolver
{
    private static readonly string[] CandidateTokens = ["FiveM", "GTAProcess"];
    private readonly object _sync = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMilliseconds(500);
    private DateTimeOffset _lastRefreshUtc;
    private TargetProcessInfo? _cached;

    public TargetProcessInfo? TryGetTargetProcess()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastRefreshUtc <= _cacheDuration && IsRunning(_cached))
            {
                return _cached;
            }

            _cached = Scan(now);
            _lastRefreshUtc = now;
            return _cached;
        }
    }

    private static TargetProcessInfo? Scan(DateTimeOffset now)
    {
        var candidates = Process.GetProcesses()
            .Where(process => CandidateTokens.Any(token => process.ProcessName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(process => process.ProcessName.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(process => process.ProcessName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var process in candidates)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                return new TargetProcessInfo(process.Id, process.ProcessName, TryGetExecutablePath(process), now);
            }
            catch
            {
                // Ignore inaccessible or transient processes.
            }
        }

        return null;
    }

    private static bool IsRunning(TargetProcessInfo? processInfo)
    {
        if (processInfo is null)
        {
            return false;
        }

        try
        {
            return !Process.GetProcessById(processInfo.ProcessId).HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}