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
    private readonly List<DateTimeOffset> _gameStarts = [];
    private readonly List<(DateTimeOffset At, double FrameTimeMs)> _warmup = new(CadenceWarmupFrames);
    private readonly double _refreshIntervalMs;

    private double _hitchThresholdMs;
    private int _hitches;
    private int _hitchesNearCapture;
    private int _hitchesWhileLoading;
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

    /// <summary>
    /// Notes that the game started, so the minutes it spends loading are left out of the comparison.
    /// </summary>
    /// <remarks>
    /// Loading hitches at several times the rate of play and has nothing to do with what a capture costs.
    /// On 10 September the game crashed and relaunched mid-session, two of the evening's four captures
    /// were taken in the minutes around the relaunch, and the line reported "75/h there against 39/h in
    /// the rest of the session" — an overstatement of the instrument's cost built almost entirely out of
    /// a reload. The same <see cref="VramPressureBandMonitor.LoadingWindow"/> is used, so the two
    /// summaries of one session agree on which minutes were loading.
    /// </remarks>
    public void NoteGameStart(DateTimeOffset at)
    {
        lock (_sync)
        {
            // A resolver that re-reports the same start within the minute must not stack two windows.
            if (_gameStarts.Count > 0 && (at - _gameStarts[^1]).Duration() < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _gameStarts.Add(at);
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

            // Every span is intersected with the measured window and merged, so two captures a
            // half-minute apart are not charged two full minutes and a capture taken during a reload is
            // not charged at all — its frames were not counted either.
            var loading = Merge(_gameStarts.Select(start => (start, start + VramPressureBandMonitor.LoadingWindow)), first, last);
            var captureWindows = Merge(_captures.Select(capture => (capture, capture + Window)), first, last);

            var loadingHours = loading.Sum(span => (span.To - span.From).TotalHours);
            var nearHours = captureWindows.Sum(span => (span.To - span.From).TotalHours)
                - Overlap(captureWindows, loading).TotalHours;
            var elsewhereHours = (last - first).TotalHours - loadingHours - nearHours;

            if (nearHours <= 0 || elsewhereHours <= 0)
            {
                // Either no capture landed outside a reload, or everything left is inside one of their
                // windows; there is nothing to compare against in both cases.
                return null;
            }

            return new CaptureCostReport(
                _captures.Count,
                _hitches,
                _hitchesNearCapture,
                _hitchesNearCapture / nearHours,
                (_hitches - _hitchesNearCapture) / elsewhereHours,
                _hitchThresholdMs,
                _hitchesWhileLoading,
                TimeSpan.FromHours(loadingHours));
        }
    }

    /// <summary>Whether a frame fell inside the loading window after a game start. Called under the lock.</summary>
    private bool IsLoading(DateTimeOffset at)
    {
        foreach (var start in _gameStarts)
        {
            if (at >= start && at - start < VramPressureBandMonitor.LoadingWindow)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The spans clipped to the measured window and merged where they overlap.</summary>
    private static List<(DateTimeOffset From, DateTimeOffset To)> Merge(
        IEnumerable<(DateTimeOffset From, DateTimeOffset To)> spans,
        DateTimeOffset first,
        DateTimeOffset last)
    {
        var merged = new List<(DateTimeOffset From, DateTimeOffset To)>();

        foreach (var (from, to) in spans
            .Select(span => (From: Max(span.From, first), To: Min(span.To, last)))
            .Where(span => span.To > span.From)
            .OrderBy(span => span.From))
        {
            if (merged.Count > 0 && from <= merged[^1].To)
            {
                merged[^1] = (merged[^1].From, Max(merged[^1].To, to));
                continue;
            }

            merged.Add((from, to));
        }

        return merged;
    }

    /// <summary>How much of two merged span lists falls in both.</summary>
    private static TimeSpan Overlap(
        List<(DateTimeOffset From, DateTimeOffset To)> left,
        List<(DateTimeOffset From, DateTimeOffset To)> right)
    {
        var total = TimeSpan.Zero;

        foreach (var a in left)
        {
            foreach (var b in right)
            {
                var from = Max(a.From, b.From);
                var to = Min(a.To, b.To);
                if (to > from)
                {
                    total += to - from;
                }
            }
        }

        return total;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

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

        // Loading is its own regime and belongs in neither side of the comparison. Counted separately
        // rather than dropped, so the line can say how much of the evening it set aside.
        if (IsLoading(at))
        {
            _hitchesWhileLoading++;
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
/// <param name="HitchesWhileLoading">Hitches set aside because the game was still loading.</param>
/// <param name="LoadingTime">How much of the measured window those minutes came to.</param>
public sealed record CaptureCostReport(
    int CaptureCount,
    int Hitches,
    int HitchesNearCapture,
    double NearCaptureHitchesPerHour,
    double ElsewhereHitchesPerHour,
    double HitchThresholdMs,
    int HitchesWhileLoading = 0,
    TimeSpan LoadingTime = default)
{
    /// <summary>
    /// Captures beyond which an evening is paying for traces the review will not open.
    /// </summary>
    /// <remarks>
    /// Six is what the 2 September review actually read: twelve were taken, and the conclusion rested on
    /// three of them plus the confirmation that the rest agreed. Each one costs most of a gigabyte
    /// written while the game runs, and the session's own figures put that at roughly a tenth more
    /// hitches during the minute it is written — so the surplus is not free, and it is not evidence.
    /// </remarks>
    public const int SufficientCaptures = 6;

    /// <summary>
    /// How much worse the minute after a capture was, or null when the session has no comparison.
    /// </summary>
    public double? CostRatio => ElsewhereHitchesPerHour > 0
        ? NearCaptureHitchesPerHour / ElsewhereHitchesPerHour
        : null;

    public string Message
    {
        get
        {
            // The setting named here has to be one that exists. It said DeepCapture.MaxCapturesPerWindow,
            // which is not a setting in this app and never has been: whoever followed the advice would
            // have searched the configuration for it and found nothing. The ceiling on an evening is
            // MaxAutoCapturesPerSession; MaxAutoCapturesPerWindow governs how many may be taken in a
            // burst and is the wrong lever for "twelve over five hours".
            var advice = CaptureCount > SufficientCaptures
                ? $" {CaptureCount} captures på en kväll är fler än analysen behöver — {SufficientCaptures} hade "
                    + "räckt, och resten är betald diskskrivning under pågående spel. Sänk "
                    + "DeepCapture.MaxAutoCapturesPerSession om nästa session inte ska betala för traces "
                    + "ingen läser; DeepCapture.MaxAutoCapturesPerWindow styr i stället hur många som får "
                    + "tas i följd."
                : string.Empty;

            // Loading is excluded from both sides, and said so. Without the sentence the counts do not
            // add up against the session's other lines, and a reader checking them assumes a bug.
            var loading = HitchesWhileLoading > 0
                ? $" {HitchesWhileLoading} hitches i {LoadingTime.TotalMinutes:F0} minuters inladdning är "
                    + "borträknade ur båda sidorna — en omstartad spelprocess hackar av egna skäl, och "
                    + "en capture som tas i de minuterna får inte betala för det."
                : string.Empty;

            return $"Deep captures: {CaptureCount} st. Av sessionens {Hitches} hitches ≥{HitchThresholdMs:F0} ms inträffade "
                + $"{HitchesNearCapture} inom en minut efter att en capture skrivits till disk — "
                + $"{NearCaptureHitchesPerHour:F0}/h där, mot {ElsewhereHitchesPerHour:F0}/h i resten av sessionen. "
                + "Delvis är det efterdyningar av hitchen som utlöste capturen, delvis kostnaden för att skriva "
                + $"~900 MB medan spelet kör. Räkna med det innan två kvällar med olika antal captures jämförs.{loading}{advice}";
        }
    }
}
