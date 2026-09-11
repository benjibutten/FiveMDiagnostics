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
/// The unit is one adapter reading, and each frame is counted against the reading nearest it in time.
/// It was a bucket of wall clock instead — a minute, then fifteen seconds — because the two clocks
/// disagree and a bucket wide enough to absorb the disagreement was the cheap way to pair them. The
/// cost of that is a bucket the card crossed the band inside: it was filed on whichever side it leant
/// to, with every hitch in it. A minute filed 125 of 350 minutes on the wrong side on 2 September;
/// fifteen seconds still left 235 of 1 480 intervals of 6 September straddling the line, a sixth of the
/// evening counted approximately. Pairing each frame with the reading nearest it removes the question:
/// nothing straddles anything, and the same evening's analysis done by hand this way is what the report
/// now reproduces.
/// </para>
/// <para>
/// What remains is the clock skew itself, and it is small enough to name. PresentMon's anchor can run
/// about a second behind the wall clock the adapter is sampled on, so a frame near a crossing can be
/// counted against the reading on the other side of it. That misplaces roughly a second of frames per
/// crossing rather than the fifteen seconds a bucket misplaced, and it does not accumulate.
/// </para>
/// </remarks>
public sealed class VramPressureBandMonitor
{
    /// <summary>
    /// How far a frame may be from the nearest adapter reading before it is not described by it.
    /// </summary>
    /// <remarks>
    /// Readings arrive about twice a second, so this is a missed poll or two plus the clock skew several
    /// times over. Beyond it the nearest reading is stale rather than near, and a frame is counted as
    /// unpaired — which the report states — instead of being attributed to a VRAM level that was
    /// measured somewhere else entirely.
    /// </remarks>
    private static readonly TimeSpan MaxPairingGap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Frames the queue may hold before the oldest are given up as unpairable.
    /// </summary>
    /// <remarks>
    /// The queue holds the frames since the last adapter reading, which is a few hundred at any frame
    /// rate a game produces. It only reaches this on a machine where the adapter is not being measured at
    /// all — no NVML, or a GPU collector that never started — and there the frames can never be paired
    /// with anything. Without the bound they would accumulate for the length of the session.
    /// </remarks>
    private const int MaxPendingFrames = 4096;

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

    /// <summary>
    /// How long after the game starts the session is still loading rather than running.
    /// </summary>
    /// <remarks>
    /// The measurement, not a guess: on 4 September the card spent 11.5 minutes above the band inside the
    /// first forty minutes after a restart and 3.0 minutes across the six hours after that — 80% of the
    /// pressure in 10% of the evening. A single share over the whole session hides that completely, and
    /// worse, it makes two evenings incomparable whenever they contain a different number of restarts:
    /// the same machine reads as twice as pressured on the night the game crashed twice.
    /// </remarks>
    public static readonly TimeSpan LoadingWindow = TimeSpan.FromMinutes(40);

    /// <summary>The deeper band, reported separately because minutes there are minutes at the edge.</summary>
    public const double DeepBandPercent = 91;

    private readonly object _sync = new();

    /// <summary>
    /// Every adapter reading of the session, each carrying the frames counted against it.
    /// </summary>
    /// <remarks>
    /// Kept rather than folded into running totals because the loading split is decided at the end: the
    /// caller learns about a game start a poll or two after it happened, and a reading has to be able to
    /// change sides afterwards. Two readings a second is 43 000 entries over the longest session
    /// measured, which is a couple of megabytes beside a ring buffer holding ninety seconds of every
    /// event the app collects.
    /// </remarks>
    private readonly List<Reading> _readings = [];

    /// <summary>
    /// Frames waiting for a reading at or after them, so the nearest one can be decided.
    /// </summary>
    /// <remarks>
    /// A frame cannot be paired the moment it arrives: the reading nearest it may not have been taken
    /// yet. It holds the frames since the last reading — a few hundred at most — and is drained whenever
    /// one arrives.
    /// </remarks>
    private readonly List<(DateTimeOffset At, double FrameTimeMs)> _pending = [];

    /// <summary>
    /// When the game was seen to start, oldest first. One or two entries on an ordinary evening.
    /// </summary>
    /// <remarks>
    /// Told to the monitor rather than inferred from the frames, because the two are different events: a
    /// capture that restarts produces a gap in the frames and no restart, and a game that restarts while
    /// PresentMon keeps running produces a restart and barely a gap.
    /// </remarks>
    private readonly List<DateTimeOffset> _gameStarts = [];
    private readonly List<(DateTimeOffset At, double FrameTimeMs)> _warmup = new(CadenceWarmupFrames);
    private readonly double _refreshIntervalMs;

    /// <summary>When the game was last seen to go away, while it is still away.</summary>
    private DateTimeOffset? _gameExitedAt;

    private double _hitchThresholdMs;
    private double _peakPercent;
    private int _unpairedFrames;

    /// <param name="refreshRateHz">
    /// The display's rate, which sets the floor under what can count as a hitch: two refreshes rather
    /// than one.
    /// </param>
    public VramPressureBandMonitor(double? refreshRateHz)
    {
        _refreshIntervalMs = refreshRateHz is > 0 ? 1000d / refreshRateHz.Value : 1000d / 60;
    }

    /// <summary>
    /// Notes that the game has started, so the minutes after it can be reported apart from the rest.
    /// </summary>
    /// <remarks>
    /// Repeated calls for the same start are ignored — the caller polls for the process and does not know
    /// whether it has told this already, and a list of a hundred identical starts would make the whole
    /// session loading.
    /// </remarks>
    public void NoteGameStart(DateTimeOffset at)
    {
        lock (_sync)
        {
            // Cleared before the repeat guard: a restart inside the tail is the same start told again
            // for this list, but it is still the moment the card goes back to being measured.
            _gameExitedAt = null;

            if (_gameStarts.Count > 0 && (at - _gameStarts[^1]).Duration() < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _gameStarts.Add(at);
        }
    }

    /// <summary>
    /// Notes that the game has gone, so the minutes measured after it stay out of the session's figures.
    /// </summary>
    /// <remarks>
    /// The adapter is deliberately sampled for ten minutes after the game exits — that tail is the one
    /// measurement saying whose memory the card was full of — but those readings are not the evening.
    /// Counted here they grow the denominator under every share below, and in the case the tail exists
    /// for, a card still held at 89 % after the exit, they grow the numerator too: ten minutes in which
    /// nobody played, reported as band pressure on the evening conclusions are drawn from. They would
    /// also land in "drift" rather than in loading, which is the figure whose own sentence says it can be
    /// compared with another evening's — and since an evening that ends with the machine switched off
    /// produces no tail at all, that comparison would turn on how each evening happened to end.
    /// </remarks>
    public void NoteGameExit(DateTimeOffset at)
    {
        lock (_sync)
        {
            _gameExitedAt = at;
        }
    }

    /// <summary>Records one adapter reading, and pairs the frames that were waiting for it.</summary>
    public void Observe(GpuTelemetrySample sample)
    {
        if (!sample.IsAvailable || sample.VramUsagePercent is not { } percent)
        {
            return;
        }

        lock (_sync)
        {
            // The reading's own timestamp rather than a flag, because the exit is noticed on the UI timer
            // while the readings arrive on the pump: a reading taken before the game went away still
            // belongs to the evening even when it is handed over after.
            if (_gameExitedAt is { } exited && sample.Timestamp > exited)
            {
                return;
            }

            _readings.Add(new Reading(sample.Timestamp, percent));
            _peakPercent = Math.Max(_peakPercent, percent);
            DrainPending(final: false);
        }
    }

    /// <summary>Holds one frame until the reading nearest it is known.</summary>
    public void Observe(FrameTelemetrySample sample)
    {
        lock (_sync)
        {
            if (_hitchThresholdMs > 0)
            {
                _pending.Add((sample.Timestamp, sample.FrameTimeMs));

                if (_pending.Count > MaxPendingFrames)
                {
                    var unpairable = _pending.Count - (MaxPendingFrames / 2);
                    _unpairedFrames += unpairable;
                    _pending.RemoveRange(0, unpairable);
                }

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
    /// The comparison, or null when the session never produced readings on both sides of the band.
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

            DrainPending(final: true);

            if (_readings.Count == 0)
            {
                return null;
            }

            var inBand = _readings.Where(reading => reading.IsInBand).ToArray();
            var outside = _readings.Where(reading => !reading.IsInBand).ToArray();

            // The denominator is the time the frames themselves cover, not a count of readings. A
            // reading stands for the sampling cadence only while the collector is sampling: one taken
            // either side of a gap absorbs every frame within the pairing window, five seconds of them
            // against a cadence of half a second, and counting it as one reading would weight its
            // hitches ten times. A frame's own interval is exactly the time it occupied, whatever
            // reading it was counted against, and a reading that carried no frames contributes nothing
            // to either side.
            var inBandHours = inBand.Sum(reading => reading.FrameMs) / 3_600_000d;
            var outsideHours = outside.Sum(reading => reading.FrameMs) / 3_600_000d;

            double? inBandRate = inBandHours > 0 ? inBand.Sum(reading => reading.Hitches) / inBandHours : null;
            double? outsideRate = outsideHours > 0 ? outside.Sum(reading => reading.Hitches) / outsideHours : null;
            var secondsPerReading = MedianReadingGapSeconds();

            // Decided here rather than when the reading arrived, because the caller can learn about a
            // start a poll or two after it happened and the readings it affects are already recorded.
            var loading = _readings.Where(IsLoading).ToArray();

            return new VramPressureBandReport(
                _readings.Count,
                inBand.Length,
                _readings.Count(reading => reading.IsInDeepBand),
                secondsPerReading,
                _peakPercent,
                _hitchThresholdMs,
                inBand.Sum(reading => reading.Hitches),
                outside.Sum(reading => reading.Hitches),
                inBandRate,
                outsideRate,
                _readings.Sum(reading => reading.Frames),
                _unpairedFrames,
                _gameStarts.Count,
                loading.Length,
                loading.Count(reading => reading.IsInBand),
                loading.Where(reading => reading.IsInBand).Sum(reading => reading.Hitches));
        }
    }

    /// <summary>
    /// Counts every frame that can no longer find a nearer reading than the ones already recorded.
    /// </summary>
    /// <remarks>
    /// A frame after the newest reading has to wait: the next reading may be closer to it than the last
    /// one was. Everything at or before the newest reading is decided, and on the final pass the whole
    /// queue is, because no further readings are coming. Called under the lock.
    /// </remarks>
    private void DrainPending(bool final)
    {
        if (_pending.Count == 0 || _readings.Count == 0)
        {
            return;
        }

        var newest = _readings[^1].At;

        // Compacted in place rather than rebuilt: this runs twice a second against the frames since the
        // last reading, and the frames that stay are the tail of the list.
        var kept = 0;
        for (var index = 0; index < _pending.Count; index++)
        {
            var frame = _pending[index];
            if (!final && frame.At > newest)
            {
                _pending[kept++] = frame;
                continue;
            }

            Count(frame.At, frame.FrameTimeMs);
        }

        _pending.RemoveRange(kept, _pending.Count - kept);
    }

    /// <summary>Counts one frame against the reading nearest it. Called under the lock.</summary>
    private void Count(DateTimeOffset at, double frameTimeMs)
    {
        var nearest = NearestReading(at);
        if (nearest is null || (nearest.At - at).Duration() > MaxPairingGap)
        {
            _unpairedFrames++;
            return;
        }

        nearest.Frames++;
        nearest.FrameMs += frameTimeMs;
        if (frameTimeMs >= _hitchThresholdMs)
        {
            nearest.Hitches++;
        }
    }

    /// <summary>
    /// The reading closest in time to a frame, or null when there are none. Called under the lock.
    /// </summary>
    /// <remarks>
    /// Binary search rather than a cursor: frame timestamps are derived from PresentMon's anchor and can
    /// step backwards slightly when it converges, and a cursor that only moves forwards would then pair
    /// a re-anchored batch against whatever it had reached.
    /// </remarks>
    private Reading? NearestReading(DateTimeOffset at)
    {
        var low = 0;
        var high = _readings.Count - 1;
        if (high < 0)
        {
            return null;
        }

        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_readings[middle].At < at)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        // low is the first reading at or after the frame; its predecessor may still be the nearer one.
        var candidate = _readings[low];
        if (low > 0 && (at - _readings[low - 1].At).Duration() <= (candidate.At - at).Duration())
        {
            return _readings[low - 1];
        }

        return candidate;
    }

    /// <summary>
    /// How much wall clock one reading stands for, as the session's own cadence rather than a constant.
    /// </summary>
    /// <remarks>
    /// The median gap and not the mean: a session contains gaps where the collector was restarted or the
    /// machine slept, and a mean over those would stretch every reading to cover time nothing measured.
    /// Called under the lock.
    /// </remarks>
    private double MedianReadingGapSeconds()
    {
        if (_readings.Count < 2)
        {
            return 0;
        }

        var gaps = new double[_readings.Count - 1];
        for (var index = 1; index < _readings.Count; index++)
        {
            gaps[index - 1] = (_readings[index].At - _readings[index - 1].At).TotalSeconds;
        }

        Array.Sort(gaps);
        return gaps[gaps.Length / 2];
    }

    /// <summary>Whether a reading fell inside the loading window after a game start. Called under the lock.</summary>
    private bool IsLoading(Reading reading)
    {
        foreach (var start in _gameStarts)
        {
            if (reading.At >= start && reading.At - start < LoadingWindow)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fixes what counts as a hitch at twice the interval the session is actually running at, never
    /// below twice the display's own refresh, and returns the held-back frames to the queue.
    /// </summary>
    private void SettleThreshold()
    {
        var frameTimes = _warmup.Select(frame => frame.FrameTimeMs).OrderBy(value => value).ToArray();

        // Median rather than mean: the warm-up is where a session's loading stutters live, and the whole
        // point of the figure is the interval the evening settles at.
        var cadenceMs = frameTimes.Length > 0 ? frameTimes[frameTimes.Length / 2] : _refreshIntervalMs;
        _hitchThresholdMs = Math.Max(cadenceMs, _refreshIntervalMs) * 2;

        _pending.InsertRange(0, _warmup);
        _warmup.Clear();
        _warmup.TrimExcess();
        DrainPending(final: false);
    }

    /// <summary>One adapter reading, and what the frames nearest it did.</summary>
    private sealed class Reading
    {
        public Reading(DateTimeOffset at, double percent)
        {
            At = at;
            Percent = percent;
        }

        public DateTimeOffset At { get; }

        public double Percent { get; }

        public int Frames;

        /// <summary>Wall clock those frames covered, which is what a hitch rate is measured against.</summary>
        public double FrameMs;

        public int Hitches;

        public bool IsInBand => Percent >= BandPercent;

        public bool IsInDeepBand => Percent >= DeepBandPercent;
    }
}

/// <summary>What the session spent inside the VRAM band, and what it cost while it was there.</summary>
/// <param name="AdapterReadings">Readings that carried a VRAM percentage — the unit everything below is counted in.</param>
/// <param name="SecondsPerReading">
/// The session's own sampling cadence, as the median gap between readings. It is what turns a count of
/// readings into minutes, and it is measured rather than assumed so that a collector polling at a
/// different rate still reports the right amount of time. The hitch rates do not use it: those are
/// measured against the frames' own intervals, which stay right across a gap in the sampling.
/// </param>
/// <param name="PairedFrames">Frames counted against a reading.</param>
/// <param name="UnpairedFrames">
/// Frames with no reading within five seconds, which are counted nowhere. Stated rather than hidden: it
/// is how a reader tells a session where the two instruments overlapped from one where the GPU collector
/// was down for a stretch.
/// </param>
/// <param name="InBandHitchesPerHour">
/// Null when no reading inside the band carried frames, which is the only honest answer then.
/// </param>
/// <param name="GameStarts">
/// How many times the game was seen to start during the session. Carried because it is what makes the
/// loading split readable: two restarts is eighty minutes of loading, and an evening's share of pressure
/// cannot be compared with another evening's without knowing that.
/// </param>
/// <param name="LoadingReadings">Readings taken inside <see cref="VramPressureBandMonitor.LoadingWindow"/> of a start.</param>
/// <param name="LoadingReadingsInBand">Of those, the ones inside the band.</param>
/// <param name="LoadingInBandHitches">Hitches inside the band during loading, which the steady figure excludes.</param>
public sealed record VramPressureBandReport(
    int AdapterReadings,
    int ReadingsInBand,
    int ReadingsInDeepBand,
    double SecondsPerReading,
    double PeakPercent,
    double HitchThresholdMs,
    int InBandHitches,
    int OutsideHitches,
    double? InBandHitchesPerHour,
    double? OutsideHitchesPerHour,
    int PairedFrames,
    int UnpairedFrames,
    int GameStarts = 0,
    int LoadingReadings = 0,
    int LoadingReadingsInBand = 0,
    int LoadingInBandHitches = 0)
{
    /// <summary>Minutes above the band inside the loading window after a game start.</summary>
    public double MinutesInBandLoading => Minutes(LoadingReadingsInBand);

    /// <summary>Minutes above the band during the rest of the session — the figure to compare evenings on.</summary>
    public double MinutesInBandSteady => Minutes(ReadingsInBand - LoadingReadingsInBand);

    /// <summary>Minutes of the session spent loading.</summary>
    public double LoadingMinutes => Minutes(LoadingReadings);

    /// <summary>Minutes of the session spent running.</summary>
    public double SteadyMinutes => Minutes(AdapterReadings - LoadingReadings);

    /// <summary>The measured session in minutes, which is the unit the line is read in.</summary>
    public double MeasuredMinutes => Minutes(AdapterReadings);

    /// <summary>Minutes spent inside the band.</summary>
    public double MinutesInBand => Minutes(ReadingsInBand);

    /// <summary>Minutes spent inside the deeper band.</summary>
    public double MinutesInDeepBand => Minutes(ReadingsInDeepBand);

    /// <summary>Share of the measured session spent inside the band.</summary>
    public double InBandShare => AdapterReadings > 0 ? (double)ReadingsInBand / AdapterReadings : 0;

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

    /// <summary>
    /// True when a minute inside the band hitched less often than a minute outside it.
    /// </summary>
    /// <remarks>
    /// The strongest thing this measurement can say, and it says the opposite of a warning: the card was
    /// full and the frames were fine, so the band is not what cost the session anything. On 5 September
    /// the evening with the highest VRAM pressure ever measured — half the session above the band — had
    /// its hitches concentrated <em>outside</em> it, 789 against 869 an hour, while the actual cause was
    /// a paging read. That line went out as a Warning and was read as a VRAM warning for a day.
    /// </remarks>
    public bool BandCostNothing => HitchRatio is { } ratio && ratio < 1;

    /// <summary>True once the band was occupied enough to be worth acting on rather than noting.</summary>
    /// <remarks>
    /// The occupancy terms are measurements and need no floor; <see cref="BandCostNothing"/> is a verdict
    /// and gets the same one <see cref="Message"/> holds it to. Ungated it downgraded the line on the
    /// strength of a comparison the prose beside it was refusing to state — a card sitting in the deep
    /// band reported as Info because fifty frames on one side of a ratio happened to fall the right way.
    /// </remarks>
    public bool IsPressured =>
        ReadingsInBand > 0
        && !(BandCostNothing && HasEnoughForVerdict)
        && (ReadingsInDeepBand > 0 || InBandShare >= 0.05);

    /// <summary>
    /// Minutes of session the comparison needs before "the band cost nothing" is stated as a conclusion
    /// rather than as thin material.
    /// </summary>
    /// <remarks>
    /// On 7 September the quarter-hourly lines early in a session wrote "Bandet kostade ingenting den här
    /// sessionen" on 3-7 minutes of material, and the same session's closing line reported a genuine 3.1×
    /// on 324 minutes. The conclusion was right both times; the early wording claimed a whole session's
    /// worth of confidence for a few minutes of it. Half an hour is short beside the sessions this app
    /// measures and long enough that a session's first quarter-hourly line, taken alone, cannot reach it.
    /// </remarks>
    private const double MinimumMeasuredMinutesForVerdict = 30;

    /// <summary>
    /// Minutes <em>inside the band</em> before the band's own hitch rate is compared to anything.
    /// </summary>
    /// <remarks>
    /// The gate above counts session time, which is the wrong quantity for the claim being made. At
    /// 02:14 on 9 September the line read "Det är ett motbevis mot att bandet skulle vara orsaken" on
    /// 0.9 minutes in the band — thirty measured minutes cleared the session gate, and the comparison
    /// that sentence rests on had fifty-odd frames on one side of it. Five minutes in the band is some
    /// eighteen thousand frames at 60 Hz, which is enough for a rate; less is a number, not a finding,
    /// and that cuts both ways — the same floor holds back "the band cost nothing" and "the band is the
    /// cause" alike.
    /// </remarks>
    private const double MinimumBandMinutesForVerdict = 5;

    /// <summary>True once both the session and the band have enough time behind them for a conclusion.</summary>
    private bool HasEnoughForVerdict =>
        MeasuredMinutes >= MinimumMeasuredMinutesForVerdict && MinutesInBand >= MinimumBandMinutesForVerdict;

    public string Message
    {
        get
        {
            if (ReadingsInBand == 0)
            {
                return $"VRAM-tryck: kortet höll sig under {VramPressureBandMonitor.BandPercent:F0} % hela sessionen "
                    + $"({MeasuredMinutes:F0} mätta minuter, högst {PeakPercent:F1} %). Texturinställningen har marginal.";
            }

            var deep = ReadingsInDeepBand > 0
                ? $" och över {VramPressureBandMonitor.DeepBandPercent:F0} % i {MinutesInDeepBand:F1} minuter"
                : string.Empty;

            var gradient = DescribeGradient();
            var split = DescribeLoadingSplit();

            // The conclusion first when there is one, because the rest of the sentence is a large VRAM
            // percentage and a reader who stops after the first clause has to stop on the right one.
            //
            // Not stated at all on thin material: exonerating the band on three minutes of a session that
            // may run for hours is a claim the data has not earned yet, even when the sign of the ratio
            // happens to already be right.
            var lead = HasEnoughForVerdict
                ? BandCostNothing
                    ? "Bandet kostade ingenting den här sessionen. "
                    : string.Empty
                : MeasuredMinutes < MinimumMeasuredMinutesForVerdict
                    ? $"Bara {MeasuredMinutes:F0} minuter mätta hittills — för tunt underlag för att fria eller "
                        + "fälla bandet än. "
                    : $"Bara {MinutesInBand:F1} minuter i bandet hittills — för tunt underlag för att fria "
                        + "eller fälla det än, oavsett hur länge sessionen mätts. ";

            return $"{lead}VRAM-tryck: kortet låg över {VramPressureBandMonitor.BandPercent:F0} % i {MinutesInBand:F1} av "
                + $"{MeasuredMinutes:F0} minuter ({InBandShare:P0}){deep}; högst {PeakPercent:F1} %.{split}{gradient} "
                + $"Mätt per frame mot närmaste GPU-avläsning — {PairedFrames:N0} frames mot "
                + $"{AdapterReadings:N0} mätpunkter{DescribeUnpaired()}, ingen tidsbucket att hamna på fel "
                + "sida om. Bandet är den här sessionens egen tid jämförd mot sig själv, inte en gissad gräns.";
        }
    }

    /// <summary>Minutes a number of readings stands for, at the session's own cadence.</summary>
    private double Minutes(int readings) => readings * SecondsPerReading / 60d;

    /// <summary>The clause naming the frames no reading was near enough to describe.</summary>
    private string DescribeUnpaired()
    {
        return UnpairedFrames == 0
            ? string.Empty
            : $", varav {UnpairedFrames:N0} frames saknade avläsning och räknas inte";
    }

    /// <summary>
    /// The sentence splitting the band time into the minutes after a game start and the rest.
    /// </summary>
    /// <remarks>
    /// Silent when nothing told the monitor about a game start, which is the honest answer: without one
    /// there is no loading window to measure against and the undivided figure is all there is. Silent too
    /// when the session was entirely loading or entirely running, where the split would say nothing the
    /// line above has not.
    /// </remarks>
    private string DescribeLoadingSplit()
    {
        if (GameStarts == 0 || LoadingReadings == 0 || LoadingReadings >= AdapterReadings)
        {
            return string.Empty;
        }

        var starts = GameStarts == 1
            ? "efter spelstarten"
            : $"efter {GameStarts} spelstarter";

        return $" Av dem låg {MinutesInBandLoading:F1} min i inladdningen — de första "
            + $"{VramPressureBandMonitor.LoadingWindow.TotalMinutes:F0} minuterna {starts}, "
            + $"{LoadingMinutes:F0} minuter totalt — och {MinutesInBandSteady:F1} min under "
            + $"{SteadyMinutes:F0} minuters drift. Det är driftsiffran som går att jämföra mot en annan "
            + "kväll; inladdningen beror på hur många gånger spelet startades om.";
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
            if (ratio >= 1)
            {
                // The accusation needs the floor as much as the exoneration below it does, and only the
                // exoneration was getting it: a session with 0.9 minutes in the band wrote its ratio as a
                // finding while the identical measurement pointing the other way was held back.
                return HasEnoughForVerdict
                    ? $" I de minuterna var hitchfrekvensen {ratio:F1}× högre än i resten — {rates}."
                    : $" I de minuterna var hitchfrekvensen {ratio:F1}× högre än i resten — {rates} — men på "
                        + $"{MinutesInBand:F1} minuter i bandet är det ingen slutsats åt något håll.";
            }

            // The exoneration needs the same floor the accusation does. Read off a minute in the band it
            // is a coin toss with a sentence attached.
            return HasEnoughForVerdict
                ? $" I de minuterna var hitchfrekvensen lägre än i resten — {rates}. Det är ett motbevis mot "
                    + "att bandet skulle vara orsaken, inte en varning om det."
                : $" I de minuterna var hitchfrekvensen lägre än i resten — {rates} — men på "
                    + $"{MinutesInBand:F1} minuter i bandet är det ingen slutsats åt något håll.";
        }

        return inBand > 0
            ? $" I de minuterna inföll {rates}: utanför bandet hitchade sessionen inte alls, vilket är den "
                + "skarpaste gradient den kan visa."
            : $" Varken i eller utanför bandet förekom hitches ≥{HitchThresholdMs:F0} ms.";
    }
}
