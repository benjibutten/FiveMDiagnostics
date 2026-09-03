namespace FiveMDiagnostics.Core;

/// <summary>
/// Measures how much of a session the card spent inside the VRAM band, and what that band cost.
/// </summary>
/// <remarks>
/// <para>
/// The app has warned about processes whose VRAM grows for several sessions, and never once about the
/// card simply being full. On the evening of 30 August the card sat above 88% for 12% of the session
/// and above 91% for seven minutes, and the report said nothing at all: the growth detector had nothing
/// to complain about, because nothing grew — the memory had been taken before the session started.
/// </para>
/// <para>
/// What makes the band worth a line is that its cost is now measured rather than argued. Counted by
/// hand out of that session's CSV, minutes inside the band held 794 hitches ≥33 ms per hour against 82
/// in the minutes outside it. That gradient is the whole finding, and every number in it is already in
/// the telemetry the session collects anyway: a VRAM percentage every half second and a frame time per
/// frame. The 88% band came from an argument on 26 August; it is now the level the measurement
/// separates.
/// </para>
/// <para>
/// An interval, not a sample, is the unit. Frame times and adapter readings arrive at wildly different
/// rates and from two different clocks — PresentMon's anchor runs about a second behind the wall clock
/// — so pairing them per sample would compare a frame against whichever reading happened to be nearest.
/// The interval has to be far longer than that skew and short enough that a bad patch does not average
/// away.
/// </para>
/// <para>
/// It was a minute, and a minute was too long. The card crosses the band in bursts of twenty or thirty
/// seconds, so most of the minutes it spent there were minutes it spent partly there — and the majority
/// rule below then filed every one of them as "outside", with their hitches. Measured over 350 minutes
/// on 2 September: 22 minutes in the band, 125 minutes that touched it and were counted as outside, and
/// those 125 carried 654 of the session's 1 246 hitches. The report came out at 1.4× where the same
/// data, matched per sample, says 3.4×; at fifteen seconds it says 2.5× and discards nothing. A ratio
/// of 1.4 reads as noise and this one is not noise, so the interval is the difference between a line
/// somebody acts on and a line somebody skips.
/// </para>
/// </remarks>
public sealed class VramPressureBandMonitor
{
    /// <summary>
    /// The bucket both series are folded into.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds is fifteen times the clock skew between the two collectors and about half the
    /// length of the shortest excursion into the band that the sessions show. Shorter would start to
    /// pair frames against the wrong side of a crossing; longer is what this class already tried.
    /// </remarks>
    private const int IntervalSeconds = 15;

    /// <summary>
    /// Share of an interval's adapter readings that has to be inside the band before it counts.
    /// </summary>
    /// <remarks>
    /// An interval in which the card touched the band once is not an interval spent in it. Half is the
    /// point at which it is more inside than outside, which is what the comparison below needs it to
    /// mean. Intervals that are genuinely split are still counted on the side they lean to rather than
    /// discarded — <see cref="VramPressureBandReport.MixedIntervals"/> says how many there were, so a
    /// reader can see how sharp the split was without any of the session being thrown away to make it
    /// look sharper.
    /// </remarks>
    private const double IntervalShareInBand = 0.5;

    /// <summary>
    /// Frames held back before the hitch threshold is fixed, so it follows the cadence the session
    /// actually holds rather than the one the display is capable of.
    /// </summary>
    /// <remarks>
    /// The same warm-up <see cref="CaptureCostMonitor"/> uses, and for the same reason: on a 120 Hz
    /// panel with the game capped to 60 fps a threshold of two refreshes lands on the cadence itself and
    /// every frame of a smooth evening counts as a hitch. The held-back frames are not discarded.
    /// </remarks>
    private const int CadenceWarmupFrames = 600;

    /// <summary>The band, in percent of the card's own capacity.</summary>
    public const double BandPercent = 88;

    /// <summary>The deeper band, reported separately because minutes there are minutes at the edge.</summary>
    public const double DeepBandPercent = 91;

    private readonly object _sync = new();
    private readonly Dictionary<long, Interval> _intervals = [];
    private readonly List<(DateTimeOffset At, double FrameTimeMs)> _warmup = new(CadenceWarmupFrames);
    private readonly double _refreshIntervalMs;

    private double _hitchThresholdMs;

    /// <param name="refreshRateHz">
    /// The display's rate, which sets the floor under what can count as a hitch: two refreshes rather
    /// than one.
    /// </param>
    public VramPressureBandMonitor(double? refreshRateHz)
    {
        _refreshIntervalMs = refreshRateHz is > 0 ? 1000d / refreshRateHz.Value : 1000d / 60;
    }

    /// <summary>Folds one adapter reading into the minute it belongs to.</summary>
    public void Observe(GpuTelemetrySample sample)
    {
        if (!sample.IsAvailable || sample.VramUsagePercent is not { } percent)
        {
            return;
        }

        lock (_sync)
        {
            var interval = IntervalOf(sample.Timestamp);
            interval.AdapterReadings++;
            if (percent >= BandPercent)
            {
                interval.ReadingsInBand++;
            }

            if (percent >= DeepBandPercent)
            {
                interval.ReadingsInDeepBand++;
            }

            if (percent > interval.PeakPercent)
            {
                interval.PeakPercent = percent;
            }
        }
    }

    /// <summary>Folds one frame into the minute it belongs to.</summary>
    public void Observe(FrameTelemetrySample sample)
    {
        lock (_sync)
        {
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
    /// The comparison, or null when the session never produced minutes on both sides of the band.
    /// </summary>
    /// <remarks>
    /// Both sides are required. An evening spent entirely inside the band, or entirely outside it, has
    /// no gradient to report — only a share, and the share alone is what the previous six sessions
    /// already had and could not act on.
    /// </remarks>
    public VramPressureBandReport? Summary()
    {
        lock (_sync)
        {
            if (_warmup.Count > 0)
            {
                // A session shorter than the warm-up still gets a threshold, from the frames it has.
                SettleThreshold();
            }

            var measured = _intervals.Values.Where(interval => interval.AdapterReadings > 0).ToArray();
            if (measured.Length == 0)
            {
                return null;
            }

            var inBand = measured.Where(interval => interval.IsInBand).ToArray();
            var outside = measured.Where(interval => !interval.IsInBand).ToArray();

            // Only intervals that carried frames can carry a hitch rate; one where the capture was down
            // is an interval about PresentMon rather than about the card.
            var inBandWithFrames = inBand.Where(interval => interval.Frames > 0).ToArray();
            var outsideWithFrames = outside.Where(interval => interval.Frames > 0).ToArray();

            var perHour = 3600d / IntervalSeconds;
            double? inBandRate = inBandWithFrames.Length > 0
                ? inBandWithFrames.Sum(interval => interval.Hitches) * perHour / inBandWithFrames.Length
                : null;
            double? outsideRate = outsideWithFrames.Length > 0
                ? outsideWithFrames.Sum(interval => interval.Hitches) * perHour / outsideWithFrames.Length
                : null;

            return new VramPressureBandReport(
                IntervalSeconds,
                measured.Length,
                inBand.Length,
                measured.Count(interval => interval.IsInDeepBand),
                measured.Count(interval => interval.IsMixed),
                measured.Max(interval => interval.PeakPercent),
                _hitchThresholdMs,
                inBandWithFrames.Sum(interval => interval.Hitches),
                outsideWithFrames.Sum(interval => interval.Hitches),
                inBandRate,
                outsideRate);
        }
    }

    /// <summary>Called under the lock.</summary>
    private Interval IntervalOf(DateTimeOffset timestamp)
    {
        var key = timestamp.ToUnixTimeSeconds() / IntervalSeconds;
        if (!_intervals.TryGetValue(key, out var interval))
        {
            interval = new Interval();
            _intervals[key] = interval;
        }

        return interval;
    }

    /// <summary>
    /// Fixes what counts as a hitch at twice the interval the session is actually running at, never
    /// below twice the display's own refresh, and counts the held-back frames against it.
    /// </summary>
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

    /// <summary>Counts one frame into its interval. Called under the lock.</summary>
    private void Count(DateTimeOffset at, double frameTimeMs)
    {
        var interval = IntervalOf(at);
        interval.Frames++;
        if (frameTimeMs >= _hitchThresholdMs)
        {
            interval.Hitches++;
        }
    }

    private sealed class Interval
    {
        public int AdapterReadings;
        public int ReadingsInBand;
        public int ReadingsInDeepBand;
        public int Frames;
        public int Hitches;
        public double PeakPercent;

        public bool IsInBand => AdapterReadings > 0 && ReadingsInBand >= AdapterReadings * IntervalShareInBand;

        public bool IsInDeepBand => AdapterReadings > 0 && ReadingsInDeepBand >= AdapterReadings * IntervalShareInBand;

        /// <summary>The card crossed the band inside this interval rather than spending it on one side.</summary>
        public bool IsMixed => ReadingsInBand > 0 && ReadingsInBand < AdapterReadings;
    }
}

/// <summary>What the session spent inside the VRAM band, and what it cost while it was there.</summary>
/// <param name="IntervalSeconds">The bucket the two series were folded into, so the minutes below can be derived.</param>
/// <param name="MixedIntervals">
/// Intervals in which the card was on both sides of the band. They are counted on whichever side they
/// lean to rather than discarded; this figure is how the reader judges how clean the split was.
/// </param>
/// <param name="InBandHitchesPerHour">
/// Null when no interval inside the band carried frames, which is the only honest answer then.
/// </param>
public sealed record VramPressureBandReport(
    int IntervalSeconds,
    int MeasuredIntervals,
    int IntervalsInBand,
    int IntervalsInDeepBand,
    int MixedIntervals,
    double PeakPercent,
    double HitchThresholdMs,
    int InBandHitches,
    int OutsideHitches,
    double? InBandHitchesPerHour,
    double? OutsideHitchesPerHour)
{
    /// <summary>The measured session in minutes, which is the unit the line is read in.</summary>
    public double MeasuredMinutes => MeasuredIntervals * IntervalSeconds / 60d;

    /// <summary>Minutes spent inside the band.</summary>
    public double MinutesInBand => IntervalsInBand * IntervalSeconds / 60d;

    /// <summary>Minutes spent inside the deeper band.</summary>
    public double MinutesInDeepBand => IntervalsInDeepBand * IntervalSeconds / 60d;

    /// <summary>Share of the measured session spent inside the band.</summary>
    public double InBandShare => MeasuredIntervals > 0 ? (double)IntervalsInBand / MeasuredIntervals : 0;

    /// <summary>
    /// How much worse the band was, or null when there is no finite ratio to state.
    /// </summary>
    /// <remarks>
    /// Two different things produce a null and only <see cref="Message"/> can tell them apart: one side
    /// had no frames at all, or nothing outside the band hitched. The second is the strongest gradient a
    /// session can produce rather than a failed comparison, and reporting it as "one side has no frames"
    /// said the opposite of what those minutes showed.
    /// </remarks>
    public double? HitchRatio => InBandHitchesPerHour is { } inBand && OutsideHitchesPerHour is > 0 and { } outside
        ? inBand / outside
        : null;

    /// <summary>True once the band was occupied enough to be worth acting on rather than noting.</summary>
    public bool IsPressured => IntervalsInBand > 0 && (IntervalsInDeepBand > 0 || InBandShare >= 0.05);

    public string Message
    {
        get
        {
            if (IntervalsInBand == 0)
            {
                return $"VRAM-tryck: kortet höll sig under {VramPressureBandMonitor.BandPercent:F0} % hela sessionen "
                    + $"({MeasuredMinutes:F0} mätta minuter, högst {PeakPercent:F1} %). Texturinställningen har marginal.";
            }

            var deep = IntervalsInDeepBand > 0
                ? $" och över {VramPressureBandMonitor.DeepBandPercent:F0} % i {MinutesInDeepBand:F1} minuter"
                : string.Empty;

            var gradient = DescribeGradient();

            return $"VRAM-tryck: kortet låg över {VramPressureBandMonitor.BandPercent:F0} % i {MinutesInBand:F1} av "
                + $"{MeasuredMinutes:F0} minuter ({InBandShare:P0}){deep}; högst {PeakPercent:F1} %.{gradient} "
                + $"Mätt i {IntervalSeconds}-sekundersintervall, varav {MixedIntervals} låg på båda sidor om "
                + "gränsen och räknats dit de lutar. Bandet är den här sessionens egen tid jämförd mot sig "
                + "själv, inte en gissad gräns.";
        }
    }

    /// <summary>
    /// The sentence comparing a minute inside the band with a minute outside it.
    /// </summary>
    /// <remarks>
    /// Split out because there are four outcomes and only one of them is a ratio: a side with no frames,
    /// a real ratio, an outside that never hitched, and a session that never hitched at all. Folding the
    /// last two into the first is what made "every hitch in the session happened inside the band" print
    /// as "den ena sidan saknar frames".
    /// </remarks>
    private string DescribeGradient()
    {
        if (InBandHitchesPerHour is not { } inBand || OutsideHitchesPerHour is not { } outside)
        {
            return $" Hitchfrekvensen kunde inte jämföras: {InBandHitches} hitches ≥{HitchThresholdMs:F0} ms i bandet "
                + $"och {OutsideHitches} utanför, men den ena sidan saknar frames.";
        }

        var rates = $"{inBand:F0} mot {outside:F0} hitches ≥{HitchThresholdMs:F0} ms per timme";

        if (HitchRatio is { } ratio)
        {
            return ratio >= 1
                ? $" I de minuterna var hitchfrekvensen {ratio:F1}× högre än i resten — {rates}."
                : $" I de minuterna var hitchfrekvensen lägre än i resten — {rates} — så bandet kostade "
                    + "ingenting mätbart.";
        }

        return inBand > 0
            ? $" I de minuterna inföll {rates}: utanför bandet hitchade sessionen inte alls, vilket är den "
                + "skarpaste gradient den kan visa."
            : $" Varken i eller utanför bandet förekom hitches ≥{HitchThresholdMs:F0} ms.";
    }
}
