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
/// A minute, not a sample, is the unit. Frame times and adapter readings arrive at wildly different
/// rates and from two different clocks — PresentMon's anchor runs about a second behind the wall clock
/// — so pairing them per sample would compare a frame against whichever reading happened to be nearest.
/// A minute is far longer than that skew and short enough that a bad patch does not average away.
/// </para>
/// </remarks>
public sealed class VramPressureBandMonitor
{
    /// <summary>
    /// Share of a minute's adapter readings that has to be inside the band before the minute counts.
    /// </summary>
    /// <remarks>
    /// A minute in which the card touched the band once is not a minute spent in it. Half is the point
    /// at which the minute is more inside than outside, which is what the comparison below needs it to
    /// mean.
    /// </remarks>
    private const double MinuteShareInBand = 0.5;

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
    private readonly Dictionary<long, Minute> _minutes = [];
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
            var minute = MinuteOf(sample.Timestamp);
            minute.AdapterReadings++;
            if (percent >= BandPercent)
            {
                minute.ReadingsInBand++;
            }

            if (percent >= DeepBandPercent)
            {
                minute.ReadingsInDeepBand++;
            }

            if (percent > minute.PeakPercent)
            {
                minute.PeakPercent = percent;
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

            var measured = _minutes.Values.Where(minute => minute.AdapterReadings > 0).ToArray();
            if (measured.Length == 0)
            {
                return null;
            }

            var inBand = measured.Where(minute => minute.IsInBand).ToArray();
            var outside = measured.Where(minute => !minute.IsInBand).ToArray();
            var deepMinutes = measured.Count(minute => minute.IsInDeepBand);

            // Only minutes that carried frames can carry a hitch rate; a minute where the capture was
            // down is a minute about PresentMon rather than about the card.
            var inBandWithFrames = inBand.Where(minute => minute.Frames > 0).ToArray();
            var outsideWithFrames = outside.Where(minute => minute.Frames > 0).ToArray();

            double? inBandRate = inBandWithFrames.Length > 0
                ? inBandWithFrames.Sum(minute => minute.Hitches) * 60d / inBandWithFrames.Length
                : null;
            double? outsideRate = outsideWithFrames.Length > 0
                ? outsideWithFrames.Sum(minute => minute.Hitches) * 60d / outsideWithFrames.Length
                : null;

            return new VramPressureBandReport(
                measured.Length,
                inBand.Length,
                deepMinutes,
                measured.Max(minute => minute.PeakPercent),
                _hitchThresholdMs,
                inBandWithFrames.Sum(minute => minute.Hitches),
                outsideWithFrames.Sum(minute => minute.Hitches),
                inBandRate,
                outsideRate);
        }
    }

    /// <summary>Called under the lock.</summary>
    private Minute MinuteOf(DateTimeOffset timestamp)
    {
        var key = timestamp.ToUnixTimeSeconds() / 60;
        if (!_minutes.TryGetValue(key, out var minute))
        {
            minute = new Minute();
            _minutes[key] = minute;
        }

        return minute;
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

    /// <summary>Counts one frame into its minute. Called under the lock.</summary>
    private void Count(DateTimeOffset at, double frameTimeMs)
    {
        var minute = MinuteOf(at);
        minute.Frames++;
        if (frameTimeMs >= _hitchThresholdMs)
        {
            minute.Hitches++;
        }
    }

    private sealed class Minute
    {
        public int AdapterReadings;
        public int ReadingsInBand;
        public int ReadingsInDeepBand;
        public int Frames;
        public int Hitches;
        public double PeakPercent;

        public bool IsInBand => AdapterReadings > 0 && ReadingsInBand >= AdapterReadings * MinuteShareInBand;

        public bool IsInDeepBand => AdapterReadings > 0 && ReadingsInDeepBand >= AdapterReadings * MinuteShareInBand;
    }
}

/// <summary>What the session spent inside the VRAM band, and what it cost while it was there.</summary>
/// <param name="InBandHitchesPerHour">
/// Null when no minute inside the band carried frames, which is the only honest answer then.
/// </param>
public sealed record VramPressureBandReport(
    int MeasuredMinutes,
    int MinutesInBand,
    int MinutesInDeepBand,
    double PeakPercent,
    double HitchThresholdMs,
    int InBandHitches,
    int OutsideHitches,
    double? InBandHitchesPerHour,
    double? OutsideHitchesPerHour)
{
    /// <summary>Share of the measured session spent inside the band.</summary>
    public double InBandShare => MeasuredMinutes > 0 ? (double)MinutesInBand / MeasuredMinutes : 0;

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
    public bool IsPressured => MinutesInBand > 0 && (MinutesInDeepBand > 0 || InBandShare >= 0.05);

    public string Message
    {
        get
        {
            if (MinutesInBand == 0)
            {
                return $"VRAM-tryck: kortet höll sig under {VramPressureBandMonitor.BandPercent:F0} % hela sessionen "
                    + $"({MeasuredMinutes} mätta minuter, högst {PeakPercent:F1} %). Texturinställningen har marginal.";
            }

            var deep = MinutesInDeepBand > 0
                ? $" och över {VramPressureBandMonitor.DeepBandPercent:F0} % i {MinutesInDeepBand} minuter"
                : string.Empty;

            var gradient = DescribeGradient();

            return $"VRAM-tryck: kortet låg över {VramPressureBandMonitor.BandPercent:F0} % i {MinutesInBand} av "
                + $"{MeasuredMinutes} minuter ({InBandShare:P0}){deep}; högst {PeakPercent:F1} %.{gradient} "
                + "Bandet är den här sessionens egna minuter jämförda mot varandra, inte en gissad gräns.";
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
