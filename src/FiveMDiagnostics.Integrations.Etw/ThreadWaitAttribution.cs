using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// Reconstructs off-CPU intervals from context switches. PresentMon's CPU-busy column covers the whole
/// frame-side delay; this class separates time actually executing from time the game thread slept.
/// </summary>
internal sealed class ThreadWaitAttribution
{
    private const double LongWaitThresholdMs = 100;
    private const int MaxLongWaits = 100_000;

    private readonly Dictionary<int, SwitchOut> _switchOutByThread = [];
    private readonly List<ThreadWait> _longWaits = [];

    public void OnContextSwitch(CSwitchTraceData data)
    {
        if (data.NewThreadID >= 0 && _switchOutByThread.Remove(data.NewThreadID, out var previous))
        {
            var durationMs = (data.TimeStamp - previous.Timestamp).TotalMilliseconds;
            if (durationMs >= LongWaitThresholdMs
                && IsWaiting(previous.State)
                && _longWaits.Count < MaxLongWaits)
            {
                _longWaits.Add(new ThreadWait(
                    previous.ProcessId,
                    data.NewThreadID,
                    previous.Timestamp,
                    data.TimeStamp,
                    durationMs,
                    previous.State,
                    previous.Reason));
            }
        }

        if (data.OldThreadID >= 0)
        {
            _switchOutByThread[data.OldThreadID] = new SwitchOut(
                data.TimeStamp,
                data.OldProcessID,
                data.OldThreadState.ToString(),
                data.OldThreadWaitReason.ToString());
        }
    }

    public ThreadWaitSummary? Summarize(CpuSampleAttribution cpu)
    {
        if (cpu.FirstSampleTimestamp is not { } windowStart || cpu.LastSampleTimestamp is not { } windowEnd)
        {
            return null;
        }

        // A small tolerance admits the switch bracketing the first/last sample, but rejects a dormant
        // worker that slept for minutes and merely happened to wake inside the retained ring window.
        var tolerance = TimeSpan.FromMilliseconds(250);
        var candidates = _longWaits
            .Where(wait => wait.Start >= windowStart - tolerance
                && wait.End <= windowEnd + tolerance
                && cpu.IsGameThread(wait.ThreadId))
            .GroupBy(wait => wait.ThreadId)
            .Select(group => new
            {
                ProcessId = cpu.ProcessIdForThread(group.Key),
                ThreadId = group.Key,
                Waits = group.ToArray(),
                Samples = cpu.SampleCountForThread(group.Key),
                GameExecutableShare = cpu.GameExecutableSampleShareForThread(group.Key, cpu.ProcessIdForThread(group.Key)),
            })
            .Where(group => group.Samples >= 5 && group.GameExecutableShare >= 0.2)
            // Pick the thread doing the most actual GTA-executable work, not the helper that accumulated
            // the most sleep. In the field traces the main frame thread has thousands of such samples;
            // a background worker can sleep longer but only wakes for a handful.
            .OrderByDescending(group => group.Samples * group.GameExecutableShare)
            .ThenByDescending(group => group.Waits.Max(wait => wait.DurationMs))
            .FirstOrDefault();

        if (candidates is null)
        {
            return null;
        }

        var selectedWaits = candidates.Waits
            .OrderByDescending(wait => wait.DurationMs)
            .Take(64)
            .ToArray();
        var userRequestCount = selectedWaits.Count(wait =>
            wait.State.Contains("Wait", StringComparison.OrdinalIgnoreCase)
            && wait.Reason.Contains("UserRequest", StringComparison.OrdinalIgnoreCase));

        var reasons = selectedWaits
            .GroupBy(wait => $"{wait.State}/{wait.Reason}")
            .OrderByDescending(group => group.Count())
            .Take(3)
            .Select(group => $"{group.Key} ×{group.Count()}")
            .ToArray();

        return new ThreadWaitSummary(
            candidates.ThreadId,
            selectedWaits
                .Select(wait => new ThreadWaitInterval(
                    new DateTimeOffset(wait.Start).ToUnixTimeMilliseconds(),
                    new DateTimeOffset(wait.End).ToUnixTimeMilliseconds(),
                    wait.DurationMs,
                    wait.Reason.Contains("UserRequest", StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            userRequestCount,
            candidates.Samples,
            reasons);
    }

    private static bool IsWaiting(string state)
    {
        return state.Equals("Wait", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Waiting", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SwitchOut(DateTime Timestamp, int ProcessId, string State, string Reason);
    private sealed record ThreadWait(int ProcessId, int ThreadId, DateTime Start, DateTime End, double DurationMs, string State, string Reason);
}

internal sealed record ThreadWaitSummary(
    int ThreadId,
    IReadOnlyList<ThreadWaitInterval> Intervals,
    int UserRequestWaitCount,
    int CpuSampleCount,
    IReadOnlyList<string> Reasons)
{
    public int LongWaitCount => Intervals.Count;
    public double MaxWaitMs => Intervals.Select(wait => wait.DurationMs).DefaultIfEmpty().Max();
    public double TotalWaitMs => Intervals.Sum(wait => wait.DurationMs);

    public string Describe()
    {
        var reasons = Reasons.Count > 0 ? $" Orsaker: {string.Join(", ", Reasons)}." : string.Empty;
        return $"Schemaläggning: aktiv GTA-tråd tid {ThreadId} låg sammanhängande av CPU:n upp till "
            + $"{MaxWaitMs:F1} ms ({LongWaitCount} väntor ≥100 ms; {UserRequestWaitCount} Wait/UserRequest)."
            + reasons;
    }
}

internal sealed record ThreadWaitInterval(long StartUnixMs, long EndUnixMs, double DurationMs, bool IsUserRequest);
