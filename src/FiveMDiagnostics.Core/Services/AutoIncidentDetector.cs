namespace FiveMDiagnostics.Core;

/// <summary>What kind of observation crossed a threshold.</summary>
public enum AutoIncidentKind
{
    /// <summary>A frame whose time is the measurement, and which <see cref="AutoIncidentTrigger.FrameTimeMs"/> describes.</summary>
    FrameTime,

    /// <summary>
    /// A run of frames that never reached the screen. The visible freeze is the run, not any one frame:
    /// each present looks healthy and carries an ordinary frame time, so anything downstream that judges
    /// severity by <see cref="AutoIncidentTrigger.FrameTimeMs"/> would rank a freeze below a mild spike.
    /// </summary>
    DroppedFrameRun,
}

/// <summary>Why the detector decided to mark an incident on its own.</summary>
/// <param name="FrameTimeMs">
/// The frame that crossed the threshold. Carried because two decisions downstream need the magnitude
/// rather than the category: whether the hitch is worth spending a deep capture on, and whether it is
/// worse than the incident already open over this moment. Not meaningful for
/// <see cref="AutoIncidentKind.DroppedFrameRun"/>, where the frame times are normal by construction.
/// </param>
public sealed record AutoIncidentTrigger(
    IncidentSeverity Severity,
    string Label,
    double FrameTimeMs = 0,
    AutoIncidentKind Kind = AutoIncidentKind.FrameTime);

/// <summary>Why the detector declined to act on a threshold-crossing frame.</summary>
public enum AutoIncidentSuppression
{
    /// <summary>Not suppressed; the detector raised this one.</summary>
    None,

    /// <summary>
    /// Too soon after the last incident. This expires: once the previous incident window has closed, a
    /// new incident would describe telemetry nothing else has looked at, so the caller may still act.
    /// </summary>
    Cooldown,

    /// <summary>
    /// The rolling incident budget is spent. This is a hard rate ceiling rather than a spacing rule, so
    /// the caller must not raise an incident for it under any circumstances — the whole point of the
    /// budget is that a bad hour cannot flood the session with incidents.
    /// </summary>
    BudgetExhausted,
}

/// <summary>
/// A frame that crossed a threshold, and whether the detector was allowed to act on it.
/// </summary>
/// <remarks>
/// Suppressed triggers used to be indistinguishable from quiet frames: the detector returned null for
/// both, so a catastrophic frame arriving inside an open incident's window vanished. A 2 846 ms frame —
/// the worst of its whole session — left no trace anywhere because a 41 ms frame had opened a window
/// nine seconds earlier. Reporting the suppression separately lets the caller escalate the incident
/// that is already running instead of losing the observation.
/// <para>
/// The reason is part of the report because the two are not interchangeable. A cooldown is a spacing
/// rule that expires; an exhausted budget is the ceiling that keeps a bad hour from flooding the
/// session. Reporting both as a plain "suppressed" let the caller raise incidents past the budget,
/// which is the one thing the budget exists to prevent.
/// </para>
/// </remarks>
public sealed record AutoIncidentObservation(AutoIncidentTrigger Trigger, AutoIncidentSuppression Suppression)
{
    public bool IsSuppressed => Suppression != AutoIncidentSuppression.None;
}

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
/// Auto-marked incidents may now save a deep capture, but only through the budget in
/// <see cref="DeepCaptureOptions.MaxAutoCapturesPerSession"/>. Saving the ring buffer writes a multi
/// hundred megabyte ETL and leaves the background session empty until it refills, so this is rationed
/// to the few frames catastrophic enough to be worth one — not granted to every incident.
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

    /// <summary>
    /// When each incident inside the current budget window was raised, oldest first. Only the budget's
    /// worth of timestamps is ever held, so this stays a handful of entries however long a session runs.
    /// </summary>
    private readonly Queue<DateTimeOffset> _triggersInWindow = new();

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

    /// <summary>How many incidents this detector has raised over the whole session.</summary>
    public int TriggerCount => _triggerCount;

    /// <summary>
    /// Incidents raised inside the current budget window, against
    /// <see cref="AutoDetectOptions.MaxIncidentsPerWindow"/>. Exposed so the UI can say how much of the
    /// budget is left rather than leaving the user to guess why marking went quiet.
    /// </summary>
    public int TriggersInCurrentWindow => _triggersInWindow.Count;

    /// <summary>
    /// Feeds one presented frame in and returns an observation when this frame crosses a threshold.
    /// Returns null on every other frame, which is the overwhelming majority of calls.
    /// </summary>
    /// <remarks>
    /// Classification now happens before the cooldown and budget gates rather than after them, so a
    /// frame that crossed a threshold is reported either way — as a trigger to act on, or as a
    /// suppressed observation the caller can fold into the incident already open. Only an unsuppressed
    /// trigger spends budget or resets the cooldown.
    /// </remarks>
    public AutoIncidentObservation? Observe(FrameTelemetrySample sample)
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

        if (!_options.Enabled)
        {
            return null;
        }

        // Without a settled baseline every threshold is guesswork, and the first seconds after a level
        // load are the least representative frames of the whole session.
        if (_sampleCount < _options.MinimumSamples)
        {
            return null;
        }

        var trigger = Classify(sample);
        if (trigger is null)
        {
            return null;
        }

        // Deliberately leaves _droppedRun standing in both suppressed paths. A freeze that is still going
        // has not ended just because it fell inside a cooldown, and clearing the run here would make the
        // observation stop being reported halfway through the very stall it describes.
        if (_lastTriggerAt is { } last && sample.Timestamp - last < _options.Cooldown)
        {
            return new AutoIncidentObservation(trigger, AutoIncidentSuppression.Cooldown);
        }

        if (!HasBudget(sample.Timestamp))
        {
            return new AutoIncidentObservation(trigger, AutoIncidentSuppression.BudgetExhausted);
        }

        _lastTriggerAt = sample.Timestamp;
        _triggersInWindow.Enqueue(sample.Timestamp);
        _triggerCount++;
        _droppedRun = 0;
        return new AutoIncidentObservation(trigger, AutoIncidentSuppression.None);
    }

    /// <summary>
    /// Drops triggers that have aged out of the budget window and reports whether another one fits.
    /// </summary>
    /// <remarks>
    /// Frame timestamps are derived from PresentMon's trace anchor and can therefore step backwards
    /// slightly when the anchor converges, so entries are compared against this frame's own timestamp
    /// rather than wall clock — otherwise a re-anchored batch could expire the window early.
    /// </remarks>
    private bool HasBudget(DateTimeOffset timestamp)
    {
        var windowStart = timestamp - _options.IncidentBudgetWindow;
        while (_triggersInWindow.TryPeek(out var oldest) && oldest < windowStart)
        {
            _triggersInWindow.Dequeue();
        }

        return _triggersInWindow.Count < _options.MaxIncidentsPerWindow;
    }

    private AutoIncidentTrigger? Classify(FrameTelemetrySample sample)
    {
        var baseline = Math.Max(_baselineMs, _refreshIntervalMs);

        if (sample.FrameTimeMs >= baseline * _options.SevereMultiplier)
        {
            return new AutoIncidentTrigger(
                IncidentSeverity.Severe,
                $"Auto: {sample.FrameTimeMs:F0} ms frame (baslinje {baseline:F1} ms)",
                sample.FrameTimeMs);
        }

        if (sample.FrameTimeMs >= baseline * _options.SpikeMultiplier)
        {
            return new AutoIncidentTrigger(
                IncidentSeverity.Normal,
                $"Auto: {sample.FrameTimeMs:F0} ms frame (baslinje {baseline:F1} ms)",
                sample.FrameTimeMs);
        }

        // A run of frames that never reached the screen is a visible freeze even when each individual
        // present looks healthy, so it needs its own rule rather than a frame time threshold.
        if (_droppedRun >= _options.DroppedFrameRun)
        {
            return new AutoIncidentTrigger(
                IncidentSeverity.Normal,
                $"Auto: {_droppedRun} frames i rad nådde aldrig skärmen",
                sample.FrameTimeMs,
                AutoIncidentKind.DroppedFrameRun);
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
