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
        TargetProcessInfo? bestMatch = null;
        var bestScore = int.MinValue;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var processName = process.ProcessName;
                    if (!CandidateTokens.Any(token => processName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var score = Score(processName);
                    if (score <= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    bestMatch = new TargetProcessInfo(process.Id, processName, TryGetExecutablePath(process), now);
                }
                catch
                {
                    // Ignore inaccessible or transient processes.
                }
            }
        }

        return bestMatch;
    }

    private static bool IsRunning(TargetProcessInfo? processInfo)
    {
        if (processInfo is null)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processInfo.ProcessId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int Score(string processName)
    {
        var score = 0;
        if (processName.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (processName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
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