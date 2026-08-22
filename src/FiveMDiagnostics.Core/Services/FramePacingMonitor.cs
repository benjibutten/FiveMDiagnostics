namespace FiveMDiagnostics.Core;

/// <summary>How much headroom the frame pipeline had over a window of frames.</summary>
public enum FramePacingState
{
    /// <summary>Not enough frames in the window to classify it.</summary>
    Unknown,

    /// <summary>The cadence was met with room to spare.</summary>
    Healthy,

    /// <summary>The cadence was still met, but the margin is nearly gone.</summary>
    Marginal,

    /// <summary>The cadence was not met: the pipeline had no idle time left and frame rate fell.</summary>
    Saturated,
}

/// <summary>
/// One classified window of frames.
/// </summary>
/// <param name="SustainedWindows">
/// How many windows the current state has held, this one included. 1 means the state just changed.
/// </param>
public sealed record FramePacingWindow(
    DateTimeOffset Start,
    DateTimeOffset End,
    FramePacingState State,
    int FrameCount,
    double AchievedFps,
    double TargetFps,
    double MedianFrameTimeMs,
    double? MedianCpuWaitMs,
    double? MedianCpuBusyMs,
    double? MedianGpuBusyMs,
    int SustainedWindows)
{
    /// <summary>True when this window is the first of a new state.</summary>
    public bool IsTransition => SustainedWindows == 1;

    /// <summary>A one-line description for the journal and the status log.</summary>
    public string Describe()
    {
        var cadence = $"{AchievedFps:F1} fps mot {TargetFps:F1}, frametime {MedianFrameTimeMs:F1} ms";
        var slack = MedianCpuWaitMs is { } wait ? $", CPU-marginal {wait:F1} ms" : string.Empty;
        var busy = MedianCpuBusyMs is { } cpu ? $", CPU busy {cpu:F1} ms" : string.Empty;
        var gpu = MedianGpuBusyMs is { } value ? $", GPU busy {value:F1} ms" : string.Empty;
        return $"{State}: {cadence}{slack}{busy}{gpu}.";
    }
}

/// <summary>Counts of how the session was spent, for the end-of-session line and the UI.</summary>
public sealed record FramePacingSummary(
    int TotalWindows,
    int SaturatedWindows,
    int MarginalWindows,
    int HealthyWindows,
    double WorstFps,
    double TargetFps,
    int LongestSaturatedRun)
{
    public static FramePacingSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public double SaturatedShare => TotalWindows > 0 ? (double)SaturatedWindows / TotalWindows : 0;

    public string Describe(TimeSpan windowLength)
    {
        if (TotalWindows == 0)
        {
            return "Ingen frame pacing-data samlades in.";
        }

        var minutes = windowLength.TotalMinutes;
        return $"Frame pacing: {SaturatedWindows} av {TotalWindows} fönster ({SaturatedShare:P0}) var CPU-mättade, "
            + $"{MarginalWindows} marginella, {HealthyWindows} friska. Längsta mättade period "
            + $"{LongestSaturatedRun * minutes:F0} min, lägsta median-FPS {WorstFps:F1} mot måltakten {TargetFps:F1}.";
    }
}

/// <summary>
/// Classifies the session minute by minute into "the cadence held" and "the cadence did not".
/// </summary>
/// <remarks>
/// <para>
/// This exists because the spike detector went blind exactly when the machine was worst. Its threshold
/// is a multiple of a rolling baseline, and in a sustained bad patch the baseline drifts up with the
/// damage — a session that spent 104 of 391 minutes below 50 fps raised almost no incidents, because a
/// 20 ms baseline moves the 2x bar to 40 ms and ordinary frames stopped clearing it. Every relative
/// detector has that blind spot; the fix is a measurement that does not move.
/// </para>
/// <para>
/// The measurement is <see cref="FrameTelemetrySample.CpuWaitMs"/>. When a frame rate cap is being met,
/// the pipeline reaches it by waiting, and the wait is visible: roughly 6–8 ms per frame of a 16.67 ms
/// budget. When the wait collapses towards zero the cap is no longer what limits the frame rate — the
/// CPU is, and the frame rate falls with no spike anywhere. That distinction is invisible in frame time
/// alone, which is why "median 17 ms" looked healthy for three sessions while the player was seeing
/// 45 fps.
/// </para>
/// <para>
/// The cadence guard stops that firing on a game which is simply uncapped: a saturated verdict needs
/// both the vanished slack and a frame time worse than the best cadence this session has actually
/// sustained. The target is measured rather than assumed, because a deliberate 60 fps cap on a 120 Hz
/// display is not a fault and the refresh rate cannot tell the two apart.
/// </para>
/// <para>
/// Replayed over that session, these thresholds classify 91 of 390 minutes as saturated, find a 27
/// minute unbroken run and a worst minute of 37.3 fps, and would have raised 14 incidents against the
/// spike detector's 180.
/// </para>
/// </remarks>
public sealed class FramePacingMonitor
{
    /// <summary>
    /// Share of a window that has to be covered by frame intervals before the window is judged at all.
    /// Generous, because the only thing it is meant to catch is a capture that stopped: a game running
    /// at any frame rate covers essentially all of it.
    /// </summary>
    private const double CaptureCoverageFloor = 0.9;

    private readonly FramePacingOptions _options;
    private readonly double _refreshIntervalMs;
    private readonly List<FrameTelemetrySample> _window = [];

    private DateTimeOffset? _windowStart;
    private DateTimeOffset? _lastFrame;
    private double _bestMedianFrameTimeMs = double.MaxValue;
    private FramePacingState _currentState = FramePacingState.Unknown;
    private int _sustainedWindows;

    private int _totalWindows;
    private int _saturatedWindows;
    private int _marginalWindows;
    private int _healthyWindows;
    private int _longestSaturatedRun;
    private double _worstFps = double.MaxValue;

    public FramePacingMonitor(FramePacingOptions options, double? displayRefreshRateHz)
    {
        _options = options;
        _refreshIntervalMs = displayRefreshRateHz is > 0 ? 1000d / displayRefreshRateHz.Value : 1000d / 60;
    }

    /// <summary>The cadence the machine has been shown to sustain, in milliseconds per frame.</summary>
    public double TargetFrameTimeMs => _bestMedianFrameTimeMs is double.MaxValue
        ? _refreshIntervalMs
        : Math.Max(_bestMedianFrameTimeMs, _refreshIntervalMs);

    public FramePacingState CurrentState => _currentState;

    public FramePacingSummary Summary => new(
        _totalWindows,
        _saturatedWindows,
        _marginalWindows,
        _healthyWindows,
        _worstFps is double.MaxValue ? 0 : _worstFps,
        1000d / TargetFrameTimeMs,
        _longestSaturatedRun);

    /// <summary>
    /// Feeds one presented frame in. Returns a window whenever this frame closed one, which is roughly
    /// once a minute; every other call returns null.
    /// </summary>
    public FramePacingWindow? Observe(FrameTelemetrySample sample)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        _windowStart ??= sample.Timestamp;
        _lastFrame = sample.Timestamp;

        // The window is closed by the frame that runs past its end, and that frame opens the next one.
        // Timestamps come from PresentMon's trace anchor and can step backwards slightly when the
        // anchor converges, so a frame from before the window start closes nothing.
        if (sample.Timestamp - _windowStart.Value < _options.WindowLength)
        {
            _window.Add(sample);
            return null;
        }

        var closed = Close(_windowStart.Value, sample.Timestamp);
        _windowStart = sample.Timestamp;
        _window.Clear();
        _window.Add(sample);
        return closed;
    }

    /// <summary>
    /// Closes the window in progress, for the end of a session. Returns null when too few frames were
    /// collected to say anything.
    /// </summary>
    /// <remarks>
    /// The window ends at the last frame, not at the moment of stopping. Frame timestamps come from
    /// PresentMon's trace anchor rather than from the wall clock, and a session whose capture died
    /// before the user pressed stop would otherwise be handed a window stretching to now — reporting
    /// however long the app sat idle as time the game spent producing no frames.
    /// </remarks>
    public FramePacingWindow? Flush()
    {
        if (!_options.Enabled
            || _windowStart is not { } start
            || _lastFrame is not { } end
            || _window.Count < _options.MinimumFrames)
        {
            return null;
        }

        var closed = Close(start, end);
        _windowStart = null;
        _window.Clear();
        return closed;
    }

    private FramePacingWindow Close(DateTimeOffset start, DateTimeOffset end)
    {
        var elapsed = (end - start).TotalSeconds;
        var frameTimes = _window.Select(item => item.FrameTimeMs).ToArray();
        var medianFrameTime = Median(frameTimes);
        var medianCpuWait = MedianOfPresent(_window.Select(item => item.CpuWaitMs), _window.Count);
        var medianCpuBusy = MedianOfPresent(_window.Select(item => item.CpuBusyMs), _window.Count);
        var medianGpuBusy = MedianOfPresent(_window.Select(item => item.GpuBusyMs), _window.Count);
        var fps = elapsed > 0 ? _window.Count / elapsed : 0;

        // The target only ever ratchets down, so a bad window cannot redefine the machine's capability
        // downwards however many of them there are — the same drift that makes the rolling spike
        // baseline useless in a sustained bad patch. It is updated before classifying so that the first
        // window of a session is measured against itself rather than against the refresh rate, which
        // would call every deliberately capped session degraded.
        if (medianFrameTime > 0 && _window.Count >= _options.MinimumFrames)
        {
            _bestMedianFrameTimeMs = Math.Min(_bestMedianFrameTimeMs, medianFrameTime);
        }

        var state = Classify(medianFrameTime, medianCpuWait, _window.Count, elapsed);

        if (state == _currentState)
        {
            _sustainedWindows++;
        }
        else
        {
            _currentState = state;
            _sustainedWindows = 1;
        }

        Record(state, fps);

        return new FramePacingWindow(
            start,
            end,
            state,
            _window.Count,
            fps,
            1000d / TargetFrameTimeMs,
            medianFrameTime,
            medianCpuWait,
            medianCpuBusy,
            medianGpuBusy,
            _sustainedWindows);
    }

    private FramePacingState Classify(double medianFrameTime, double? medianCpuWait, int frameCount, double elapsedSeconds)
    {
        if (frameCount < _options.MinimumFrames || medianFrameTime <= 0)
        {
            return FramePacingState.Unknown;
        }

        // A window the capture dropped out of is not a window about the machine. Frame times cover the
        // interval between presents, so they add up to the elapsed time whenever frames kept arriving —
        // including through a genuine freeze, which is one enormous interval and must still count. What
        // they cannot cover is a PresentMon restart, where the frames on either side of the gap simply
        // do not describe the seconds in between. Judging such a window would report the missing frames
        // as a frame rate the game never had.
        var covered = _window.Sum(item => item.FrameTimeMs) / 1000d;
        if (elapsedSeconds > 0 && covered < elapsedSeconds * CaptureCoverageFloor)
        {
            return FramePacingState.Unknown;
        }

        var cadenceRatio = medianFrameTime / TargetFrameTimeMs;

        // Without the CPU/GPU breakdown — PresentMon v1, or a capture that lost the columns — cadence is
        // all there is. It is a weaker signal, so it has to be further off the target before it counts.
        if (medianCpuWait is not { } wait)
        {
            return cadenceRatio >= _options.SaturatedCadenceRatio
                ? FramePacingState.Saturated
                : cadenceRatio >= _options.MarginalCadenceRatio
                    ? FramePacingState.Marginal
                    : FramePacingState.Healthy;
        }

        if (wait < _options.SaturatedCpuWaitMs && cadenceRatio >= _options.MarginalCadenceRatio)
        {
            return FramePacingState.Saturated;
        }

        if (wait < _options.MarginalCpuWaitMs || cadenceRatio >= _options.MarginalCadenceRatio)
        {
            return FramePacingState.Marginal;
        }

        return FramePacingState.Healthy;
    }

    private void Record(FramePacingState state, double fps)
    {
        if (state == FramePacingState.Unknown)
        {
            return;
        }

        _totalWindows++;
        switch (state)
        {
            case FramePacingState.Saturated:
                _saturatedWindows++;
                _longestSaturatedRun = Math.Max(_longestSaturatedRun, _sustainedWindows);
                break;
            case FramePacingState.Marginal:
                _marginalWindows++;
                break;
            default:
                _healthyWindows++;
                break;
        }

        if (fps > 0 && fps < _worstFps)
        {
            _worstFps = fps;
        }
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static double? MedianOfPresent(IEnumerable<double?> values, int frameCount)
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();

        // A window where only a handful of frames carried the column is not a window the column can
        // classify, and half is a low enough bar that a brief gap in the capture does not disqualify it.
        return present.Length * 2 >= frameCount && present.Length > 0 ? Median(present) : null;
    }
}
