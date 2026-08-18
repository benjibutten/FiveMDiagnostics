namespace FiveMDiagnostics.Core;

/// <summary>Why the detector decided to mark an incident on its own.</summary>
public sealed record AutoIncidentTrigger(IncidentSeverity Severity, string Label);

/// <summary>
/// Decides, frame by frame, when a stutter is bad enough to materialize an incident without the user
/// pressing anything.
/// </summary>
/// <remarks>
/// A six hour session produced roughly a thousand hitches, so relying on a human to notice one and hit
/// a hotkey samples the problem at a few percent — and biases that sample towards whatever the player
/// happened to be looking at. The detector exists to make the marker optional, not to replace it: a
/// manual marker still records that a human perceived something, which is evidence the telemetry alone
/// cannot supply.
/// <para>
/// Auto-marked incidents deliberately never trigger deep capture. WPR writes a multi hundred megabyte
/// ETL and costs about fifteen seconds of tracing, which is acceptable once when the user asks for it
/// and ruinous if it fires every couple of minutes for six hours.
/// </para>
/// </remarks>
public sealed class AutoIncidentDetector
{
    /// <summary>
    /// The median only has to track slow drift (a busier area, a resolution change), so recomputing it
    /// about once a second is enough and keeps the sort off the per-frame path.
    /// </summary>
    private const int BaselineRefreshFrames = 60;

    private readonly AutoDetectOptions _options;
    private readonly double _refreshIntervalMs;
    private readonly double[] _frameTimes;
    private readonly double[] _sortBuffer;

    private int _sampleCount;
    private int _writeIndex;
    private int _framesSinceBaselineRefresh;
    private double _baselineMs;
    private int _droppedRun;
    private int _triggerCount;
    private DateTimeOffset? _lastTriggerAt;

    public AutoIncidentDetector(AutoDetectOptions options, double? displayRefreshRateHz)
    {
        _options = options;
        _refreshIntervalMs = displayRefreshRateHz is > 0 ? 1000d / displayRefreshRateHz.Value : 1000d / 60;

        // Settings are clamped when they are loaded, but the two arrays are allocated up front and sized
        // straight from configuration, so the ceiling is enforced here as well rather than trusting the
        // caller with an allocation this large.
        var windowSize = Math.Clamp(options.BaselineWindowFrames, BaselineRefreshFrames, AutoDetectOptions.MaxBaselineWindowFrames);
        _frameTimes = new double[windowSize];
        _sortBuffer = new double[windowSize];
    }

    /// <summary>Current cadence the machine is achieving, in milliseconds. Exposed for the UI and tests.</summary>
    public double BaselineMs => _baselineMs;

    /// <summary>How many incidents this detector has raised, against <see cref="AutoDetectOptions.MaxIncidentsPerSession"/>.</summary>
    public int TriggerCount => _triggerCount;

    /// <summary>
    /// Feeds one presented frame in and returns a trigger when this frame crosses a threshold. Returns
    /// null on every other frame, which is the overwhelming majority of calls.
    /// </summary>
    public AutoIncidentTrigger? Observe(FrameTelemetrySample sample)
    {
        RecordFrameTime(sample.FrameTimeMs);

        if (sample.Dropped)
        {
            _droppedRun++;
        }
        else
        {
            _droppedRun = 0;
        }

        if (!_options.Enabled || _triggerCount >= _options.MaxIncidentsPerSession)
        {
            return null;
        }

        // Without a settled baseline every threshold is guesswork, and the first seconds after a level
        // load are the least representative frames of the whole session.
        if (_sampleCount < _options.MinimumSamples)
        {
            return null;
        }

        if (_lastTriggerAt is { } last && sample.Timestamp - last < _options.Cooldown)
        {
            return null;
        }

        var trigger = Classify(sample);
        if (trigger is null)
        {
            return null;
        }

        _lastTriggerAt = sample.Timestamp;
        _triggerCount++;
        _droppedRun = 0;
        return trigger;
    }

    private AutoIncidentTrigger? Classify(FrameTelemetrySample sample)
    {
        var baseline = Math.Max(_baselineMs, _refreshIntervalMs);

        if (sample.FrameTimeMs >= baseline * _options.SevereMultiplier)
        {
            return new AutoIncidentTrigger(
                IncidentSeverity.Severe,
                $"Auto: {sample.FrameTimeMs:F0} ms frame (baslinje {baseline:F1} ms)");
        }

        if (sample.FrameTimeMs >= baseline * _options.SpikeMultiplier)
        {
            return new AutoIncidentTrigger(
                IncidentSeverity.Normal,
                $"Auto: {sample.FrameTimeMs:F0} ms frame (baslinje {baseline:F1} ms)");
        }

        // A run of frames that never reached the screen is a visible freeze even when each individual
        // present looks healthy, so it needs its own rule rather than a frame time threshold.
        if (_droppedRun >= _options.DroppedFrameRun)
        {
            return new AutoIncidentTrigger(
                IncidentSeverity.Normal,
                $"Auto: {_droppedRun} frames i rad nådde aldrig skärmen");
        }

        return null;
    }

    private void RecordFrameTime(double frameTimeMs)
    {
        _frameTimes[_writeIndex] = frameTimeMs;
        _writeIndex = (_writeIndex + 1) % _frameTimes.Length;

        if (_sampleCount < _frameTimes.Length)
        {
            _sampleCount++;
        }

        if (++_framesSinceBaselineRefresh < BaselineRefreshFrames && _baselineMs > 0)
        {
            return;
        }

        _framesSinceBaselineRefresh = 0;
        _baselineMs = Median();
    }

    private double Median()
    {
        var span = _sortBuffer.AsSpan(0, _sampleCount);
        _frameTimes.AsSpan(0, _sampleCount).CopyTo(span);
        span.Sort();
        return span[span.Length / 2];
    }
}
