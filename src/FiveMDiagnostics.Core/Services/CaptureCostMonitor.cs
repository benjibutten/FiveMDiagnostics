namespace FiveMDiagnostics.Core;

/// <summary>
/// Measures what taking deep captures costs the session that is being measured.
/// </summary>
/// <remarks>
/// <para>
/// A deep capture ends by flushing roughly 900 MB of ring buffer to disk while the game is running, and
/// the app has never said what that costs. Counted by hand afterwards on the 29 August session, the
/// minute following each of its ten flushes held hitches at four times the rate of the rest of the
/// evening — 222 against 80 per hour at 33 ms, 96 against 22 at 50 ms — while the large ones were
/// untouched, 6.0 against 6.4 per hour at 100 ms. That works out at roughly 27 of the evening's 412
/// hitches being the instrument rather than the machine, and nothing in the report said so.
/// </para>
/// <para>
/// Part of the excess is not the flush at all: a capture happens because a hitch happened, and hitches
/// cluster. The comparison cannot separate those, so the line says what it measured and not what caused
/// it. The point is that a reader comparing two evenings can see how many captures each took before
/// concluding one was worse than the other.
/// </para>
/// </remarks>
public sealed class CaptureCostMonitor
{
    /// <summary>
    /// How long after a flush a frame is counted against it.
    /// </summary>
    /// <remarks>
    /// The write itself takes a handful of seconds and the analysis that follows reads the file back, so
    /// the disturbance outlasts the write. A minute covers both and is short enough that the comparison
    /// window stays a small fraction of the session.
    /// </remarks>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Frames held back before the threshold is fixed, so it can be set from the cadence the session
    /// actually holds rather than from the one the display is capable of.
    /// </summary>
    /// <remarks>
    /// A few seconds of play, matching <c>DisplayCadenceMonitor</c>'s own warm-up. The frames are not
    /// discarded: once the cadence is known they are counted against it, so the session's first seconds
    /// are compared on the same threshold as the rest of it.
    /// </remarks>
    private const int CadenceWarmupFrames = 600;

    /// <summary>
    /// Guards every field below. <see cref="Observe"/> runs on the telemetry pump while
    /// <see cref="RecordCaptureWritten"/> runs on the task that took the capture, so the two genuinely
    /// meet: an add during the loop over <see cref="_captures"/> throws and takes the pump down with it.
    /// Uncontended in practice — a handful of captures an evening against a loop that only runs for a
    /// frame that already qualified.
    /// </summary>
    private readonly object _sync = new();

    private readonly List<DateTimeOffset> _captures = [];
    private readonly List<(DateTimeOffset At, double FrameTimeMs)> _warmup = new(CadenceWarmupFrames);
    private readonly double _refreshIntervalMs;

    private double _hitchThresholdMs;
    private int _hitches;
    private int _hitchesNearCapture;
    private DateTimeOffset? _firstFrameAt;
    private DateTimeOffset? _lastFrameAt;

    /// <param name="refreshRateHz">
    /// The display's rate, which sets the floor under what can count as a hitch: two refreshes rather
    /// than one. A fixed millisecond threshold would mean something different on every machine.
    /// </param>
    public CaptureCostMonitor(double? refreshRateHz)
    {
        _refreshIntervalMs = refreshRateHz is > 0 ? 1000d / refreshRateHz.Value : 1000d / 60;
    }

    /// <summary>Notes that a capture finished writing.</summary>
    public void RecordCaptureWritten(DateTimeOffset at)
    {
        lock (_sync)
        {
            _captures.Add(at);
        }
    }

    /// <summary>Folds one frame into the comparison.</summary>
    public void Observe(FrameTelemetrySample sample)
    {
        lock (_sync)
        {
            _firstFrameAt ??= sample.Timestamp;
            _lastFrameAt = sample.Timestamp;

            if (_hitchThresholdMs > 0)
            {
                Count(sample.Timestamp, sample.FrameTimeMs);
                return;
            }

            _warmup.Add((sample.Timestamp, sample.FrameTimeMs));
            if (_warmup.Count >= CadenceWarmupFrames)
            {
                SettleThreshold();
            }
        }
    }

    /// <summary>
    /// The comparison, or null when the session took no captures or produced too few frames to compare.
    /// </summary>
    public CaptureCostReport? Summary()
    {
        lock (_sync)
        {
            if (_warmup.Count > 0)
            {
                // A session shorter than the warm-up still gets a threshold, from the frames it has.
                SettleThreshold();
            }

            if (_captures.Count == 0
                || _firstFrameAt is not { } first
                || _lastFrameAt is not { } last
                || _hitches == 0)
            {
                return null;
            }

            var sessionHours = (last - first).TotalHours;
            var nearHours = _captures.Count * Window.TotalHours;
            if (sessionHours <= nearHours)
            {
                // Every frame is inside a capture window, so there is nothing to compare it against.
                return null;
            }

            return new CaptureCostReport(
                _captures.Count,
                _hitches,
                _hitchesNearCapture,
                _hitchesNearCapture / nearHours,
                (_hitches - _hitchesNearCapture) / (sessionHours - nearHours),
                _hitchThresholdMs);
        }
    }

    /// <summary>
    /// Fixes what counts as a hitch at twice the interval the session is actually running at, never
    /// below twice the display's own refresh, and counts the held-back frames against it.
    /// </summary>
    /// <remarks>
    /// The threshold was two refreshes of the display, which is right only when the game runs at the
    /// display's rate. On a 120 Hz panel with the game capped to 60 fps it lands at 16.67 ms — the
    /// cadence itself — so every frame of a perfectly smooth evening counts as a hitch and the line
    /// reports two indistinguishable five-figure rates. Taking the cadence from the frames is what
    /// <c>DisplayCadenceMonitor</c> already does for the same reason: nothing outside these classes
    /// knows whether it is looking at a capped game or a slow panel. The refresh interval stays as the
    /// floor, because a frame inside two refreshes cannot be seen as a hitch however the game is capped.
    /// </remarks>
    private void SettleThreshold()
    {
        var frameTimes = _warmup.Select(frame => frame.FrameTimeMs).OrderBy(value => value).ToArray();

        // Median rather than mean: the warm-up is where a session's loading stutters live, and the whole
        // point of the figure is the interval the evening settles at.
        var cadenceMs = frameTimes.Length > 0 ? frameTimes[frameTimes.Length / 2] : _refreshIntervalMs;
        _hitchThresholdMs = Math.Max(cadenceMs, _refreshIntervalMs) * 2;

        foreach (var (at, frameTimeMs) in _warmup)
        {
            Count(at, frameTimeMs);
        }

        _warmup.Clear();
        _warmup.TrimExcess();
    }

    /// <summary>Counts one frame, and whether it fell in the wake of a capture. Called under the lock.</summary>
    private void Count(DateTimeOffset at, double frameTimeMs)
    {
        if (frameTimeMs < _hitchThresholdMs)
        {
            return;
        }

        _hitches++;

        // Linear over the session's captures, which is single digits by design, and reached only by a
        // frame that already qualified as a hitch.
        foreach (var capture in _captures)
        {
            var since = at - capture;
            if (since >= TimeSpan.Zero && since <= Window)
            {
                _hitchesNearCapture++;
                return;
            }
        }
    }
}

/// <summary>What the session's own captures coincided with.</summary>
public sealed record CaptureCostReport(
    int CaptureCount,
    int Hitches,
    int HitchesNearCapture,
    double NearCaptureHitchesPerHour,
    double ElsewhereHitchesPerHour,
    double HitchThresholdMs)
{
    public string Message =>
        $"Deep captures: {CaptureCount} st. Av sessionens {Hitches} hitches ≥{HitchThresholdMs:F0} ms inträffade "
        + $"{HitchesNearCapture} inom en minut efter att en capture skrivits till disk — "
        + $"{NearCaptureHitchesPerHour:F0}/h där, mot {ElsewhereHitchesPerHour:F0}/h i resten av sessionen. "
        + "Delvis är det efterdyningar av hitchen som utlöste capturen, delvis kostnaden för att skriva "
        + "~900 MB medan spelet kör. Räkna med det innan två kvällar med olika antal captures jämförs.";
}
