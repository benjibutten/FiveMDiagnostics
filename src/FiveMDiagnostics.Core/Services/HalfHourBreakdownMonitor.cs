namespace FiveMDiagnostics.Core;

/// <summary>
/// Buckets the session into wall-clock half hours and reports what each one looked like.
/// </summary>
/// <remarks>
/// This table has been recomputed by hand, from the same columns, in every note since 2026-09-06 —
/// playtime, hitches per hour, lost time, VRAM median and CPU time per frame, one row per half hour. It
/// is what shows the spread inside a single evening, which has been the investigation's own open
/// question since 2026-09-11: a session can average an unremarkable hitch rate over hours nobody would
/// call alike. Every input already passes through the session manager once a frame and once a GPU
/// reading; this only sorts them into thirty-minute bins instead of an evening-long total.
/// </remarks>
public sealed class HalfHourBreakdownMonitor
{
    private readonly HitchThreshold _hitch;
    private readonly SortedDictionary<DateTimeOffset, Bucket> _buckets = new();

    /// <summary>Frames held back until the bar is fixed, so this table counts what the other lines count.</summary>
    private readonly List<(DateTimeOffset At, double FrameTimeMs, double? CpuBusyMs)> _warmup = [];

    public HalfHourBreakdownMonitor(HitchThreshold hitch)
    {
        _hitch = hitch;
    }

    /// <summary>
    /// Folds one frame in. Callers are expected to have already excluded frames the game was not in
    /// focus for — this reports the same play the rest of the session's summaries do.
    /// </summary>
    public void ObserveFrame(DateTimeOffset at, double frameTimeMs, double? cpuBusyMs)
    {
        if (!_hitch.IsSettled)
        {
            _warmup.Add((at, frameTimeMs, cpuBusyMs));
            return;
        }

        DrainWarmup();
        Count(at, frameTimeMs, cpuBusyMs);
    }

    private void Count(DateTimeOffset at, double frameTimeMs, double? cpuBusyMs)
    {
        var bucket = BucketFor(at);
        bucket.PlayMs += frameTimeMs;

        if (frameTimeMs >= _hitch.ThresholdMs)
        {
            bucket.Hitches++;

            // Only the excess over the hitch bar counts as lost, on the same reasoning the rest of the
            // app's "förlorad tid" figures use: a frame two milliseconds under the bar cost nothing, and
            // a 300 ms frame cost everything past what a healthy frame would have taken.
            bucket.LostMs += frameTimeMs - _hitch.ThresholdMs;
        }

        if (cpuBusyMs is { } cpu)
        {
            bucket.CpuSampleCount++;
            bucket.CpuMsTotal += cpu;
        }
    }

    /// <summary>Folds one adapter VRAM reading in.</summary>
    public void ObserveVram(DateTimeOffset at, double vramPercent)
    {
        BucketFor(at).VramReadings.Add(vramPercent);
    }

    /// <summary>The finished table, oldest half hour first, or null when nothing was ever observed.</summary>
    public HalfHourBreakdownReport? Summary()
    {
        // A session shorter than the warm-up still gets a bar, from the frames it has.
        _hitch.Settle();
        DrainWarmup();

        if (_buckets.Count == 0)
        {
            return null;
        }

        var rows = _buckets.Select(entry => BuildRow(entry.Key, entry.Value)).ToArray();
        return new HalfHourBreakdownReport(rows);
    }

    private static HalfHourBreakdownRow BuildRow(DateTimeOffset start, Bucket bucket)
    {
        var playTime = TimeSpan.FromMilliseconds(bucket.PlayMs);
        var hitchesPerHour = playTime.TotalHours > 0 ? bucket.Hitches / playTime.TotalHours : 0;
        var lostSecondsPerHour = playTime.TotalHours > 0 ? bucket.LostMs / 1000 / playTime.TotalHours : 0;
        var cpuMsPerFrame = bucket.CpuSampleCount > 0 ? bucket.CpuMsTotal / bucket.CpuSampleCount : (double?)null;

        return new HalfHourBreakdownRow(start, playTime, hitchesPerHour, lostSecondsPerHour, Median(bucket.VramReadings), cpuMsPerFrame);
    }

    /// <summary>Counts the held-back frames now that the bar is known.</summary>
    private void DrainWarmup()
    {
        if (_warmup.Count == 0)
        {
            return;
        }

        var held = _warmup.ToArray();
        _warmup.Clear();
        _warmup.TrimExcess();

        foreach (var (at, frameTimeMs, cpuBusyMs) in held)
        {
            Count(at, frameTimeMs, cpuBusyMs);
        }
    }

    /// <summary>The half hour, in local time, a moment falls into. Half hours are the app's own unit here.</summary>
    private Bucket BucketFor(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var flooredMinute = local.Minute < 30 ? 0 : 30;
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, flooredMinute, 0, local.Offset);

        if (!_buckets.TryGetValue(start, out var bucket))
        {
            bucket = new Bucket();
            _buckets[start] = bucket;
        }

        return bucket;
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        return sorted[sorted.Length / 2];
    }

    private sealed class Bucket
    {
        public double PlayMs;
        public int Hitches;
        public double LostMs;
        public int CpuSampleCount;
        public double CpuMsTotal;
        public List<double> VramReadings { get; } = [];
    }
}

/// <param name="Start">The half hour's own start, in local time.</param>
/// <param name="PlayTime">In-focus play time inside this half hour.</param>
/// <param name="VramMedianPercent">Median adapter VRAM occupancy, or null when nothing was read.</param>
/// <param name="CpuMsPerFrame">Mean <c>MsCPUBusy</c> across the frames that carried it, or null when none did.</param>
public sealed record HalfHourBreakdownRow(
    DateTimeOffset Start,
    TimeSpan PlayTime,
    double HitchesPerHour,
    double LostSecondsPerHour,
    double? VramMedianPercent,
    double? CpuMsPerFrame)
{
    public string Line =>
        $"{Start:HH:mm}  speltid {PlayTime.TotalMinutes:F1} min  hitch/h {HitchesPerHour:F1}  "
        + $"förlorad tid {LostSecondsPerHour:F2} s/h  VRAM median {(VramMedianPercent is { } vram ? $"{vram:F1} %" : "—")}  "
        + $"CPU/frame {(CpuMsPerFrame is { } cpu ? $"{cpu:F2} ms" : "—")}";
}

/// <summary>The session's half hours, oldest first.</summary>
public sealed record HalfHourBreakdownReport(IReadOnlyList<HalfHourBreakdownRow> Rows)
{
    public string Message
    {
        get
        {
            var withPlay = Rows.Where(row => row.PlayTime > TimeSpan.Zero).ToArray();
            var lowest = withPlay.Length > 0 ? withPlay.Min(row => row.HitchesPerHour) : 0;
            var highest = withPlay.Length > 0 ? withPlay.Max(row => row.HitchesPerHour) : 0;

            // Zero is a real floor some half hours reach, but it makes the ratio meaningless rather than
            // infinite or huge, so it is left unsaid rather than printed as a number nobody can use.
            var spread = lowest > 0
                ? $" Spridningen mellan halvtimmarna är {highest / lowest:F1}× ({lowest:F1}–{highest:F1} hitch/h)."
                : string.Empty;

            return $"Halvtimmar, fokusfiltrerat:\n{string.Join("\n", Rows.Select(row => row.Line))}{spread}";
        }
    }
}
