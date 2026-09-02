namespace FiveMDiagnostics.Core;

/// <summary>
/// Decides whether an automatically detected hitch may spend one of the session's deep captures.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the previous rule — automatic incidents never capture — cost a whole session.
/// Eighteen severe incidents were raised over five and a half hours and not one produced a trace, so
/// the five hitch clusters that were the entire remaining problem could not be looked inside. The only
/// ETL of the evening came from the single marker a human pressed, and the stall it was meant to catch
/// had ended 0.4 seconds before the retained window began.
/// </para>
/// <para>
/// The opposite extreme is worse. Capturing every automatic incident would have written 117 ETLs that
/// night, each a few hundred megabytes, each leaving the ring buffer empty for the seconds afterwards —
/// which is precisely the window the next hitch in a burst would land in. So the gates are three, and
/// all three have to pass: the frame has to be far beyond an ordinary spike, captures have to be spaced
/// out so a burst spends one rather than twenty, and the session has a hard ceiling.
/// </para>
/// <para>
/// The spacing gate has one way past it, added after it refused the wrong events. A frame beyond
/// <see cref="DeepCaptureOptions.AutoCaptureOverrideFrameTimeMs"/> spends budget the ordinary cooldown
/// would have withheld — the ceiling was always meant to be the binding constraint, and a session that
/// turned away its third and fourth largest frames while holding an unspent capture had the two the
/// wrong way round. What the override cannot skip is the ring buffer refilling, so it answers to a
/// shorter spacing of its own rather than to none.
/// </para>
/// <para>
/// Sized against real data. That session held 965 frames over 33 ms, 120 over 100 ms and 16 over
/// 300 ms. At the default threshold the ceiling of six is the binding constraint rather than the
/// threshold, which is the intended shape: the rare frames all qualify, and the budget decides how many
/// of them are worth the disk.
/// </para>
/// <para>
/// Both thresholds follow the session's own frames upwards. A constant threshold is right for the
/// sessions it was calibrated against and goes wrong as soon as they change, which has now happened
/// twice: 300 ms was lowered to 120 after an evening improved past it, and 500 ms to 250 after the next
/// one did — each time discovered by counting frames by hand a session later. See
/// <see cref="Observe"/>; the constants remain the floor, so the adaptation can only ever make the
/// gates stricter on an evening that is producing large frames routinely.
/// </para>
/// <para>
/// Reservations all come from the telemetry pump, which is single-reader, so the counters need no
/// synchronisation. <see cref="Remaining"/> is also read from the UI thread, where a stale value is
/// harmless — it is a status line, not a decision.
/// </para>
/// </remarks>
public sealed class AutoDeepCaptureBudget
{
    /// <summary>
    /// Hours of session the adaptive thresholds are allowed to scale their sample count by.
    /// </summary>
    /// <remarks>
    /// The retained sample is <c>rate × hours</c> frames, so an unbounded session would grow it without
    /// limit — and a threshold derived from the twelfth hour of material is describing an evening nobody
    /// is having any more. Twelve hours is longer than any session measured and bounds the list at a few
    /// hundred doubles.
    /// </remarks>
    private const double MaxAdaptiveHours = 12;

    /// <summary>
    /// Frames the sample has to reach before it is allowed to move a threshold.
    /// </summary>
    /// <remarks>
    /// At rank one the "level" is the single largest frame of the session, which is not an estimate of
    /// anything — it is one event, and taking it as the bar means only a new session record may break
    /// the cooldown. Replayed against the evening of 27 August that alone refused the 284 ms frame seven
    /// minutes after the 484 ms one, for no better reason than that a 528 ms frame had happened earlier.
    /// Three is the fewest frames that can describe a level rather than an incident, and until the
    /// session has produced them the configured constant stands.
    /// </remarks>
    private const int MinimumAdaptiveRank = 3;

    /// <summary>
    /// How far above the ordinary threshold the exception has to stay when the session's own frames
    /// place it, expressed as a ratio.
    /// </summary>
    /// <remarks>
    /// The session level may lower the exception — that is the point of it — but not into the ordinary
    /// threshold. When the two meet, every frame that qualifies for a capture also bypasses the cooldown
    /// and the window budget, and the session ceiling becomes the only gate left. A quarter above what
    /// already counts as capture-worthy keeps them apart on any threshold.
    /// </remarks>
    private const double MinimumOverrideRatio = 1.25;

    private readonly DeepCaptureOptions _options;
    private readonly List<DateTimeOffset> _spentAt = [];

    /// <summary>
    /// The largest frame times of the session so far, descending. Bounded by
    /// <see cref="AdaptiveSampleCapacity"/>, which is what the two rates need at the longest session
    /// they are allowed to scale over.
    /// </summary>
    private readonly List<double> _largestFrames = [];

    private int _spent;
    private DateTimeOffset? _lastCaptureAt;

    /// <summary>
    /// When the previous capture reported its file written, if it has.
    /// </summary>
    /// <remarks>
    /// Reserving is not capturing. Everything else here is timed from the reservation, which is all the
    /// budget can see on its own, and the extreme path is the one place where that is not good enough:
    /// what it has to clear is the previous capture still recording, and the only party that knows when
    /// that stopped is the caller that awaited it.
    /// </remarks>
    private DateTimeOffset? _lastCaptureWrittenAt;

    /// <summary>
    /// The frame time the previous capture was taken for, or zero when it was taken for something with
    /// no frame time — saturation, or a run of dropped frames.
    /// </summary>
    /// <remarks>
    /// What keeps the extreme tier from eating the session. Skipping the ring buffer's refill is worth
    /// it for a frame nothing has traced yet; it is not worth it for the fourth 900 ms frame of an
    /// unbroken bad patch, which the trace already on disk describes as well as a half-filled one would.
    /// </remarks>
    private double _lastCaptureFrameTimeMs;
    private DateTimeOffset? _firstFrameAt;
    private DateTimeOffset? _lastFrameAt;

    public AutoDeepCaptureBudget(DeepCaptureOptions options)
    {
        _options = options;
    }

    /// <summary>Captures the detector has spent so far this session.</summary>
    public int Spent => _spent;

    /// <summary>
    /// Notes that the capture reserved most recently has finished writing its file.
    /// </summary>
    /// <remarks>
    /// Optional: a caller that never says so is treated as one whose capture may still be running, and
    /// gets <see cref="DeepCaptureOptions.ExtremeCaptureSpacing"/> in full.
    /// </remarks>
    public void NoteCaptureWritten(DateTimeOffset timestamp)
    {
        _lastCaptureWrittenAt = timestamp;
    }

    /// <summary>Captures still available, for the UI to show rather than leaving the user to guess.</summary>
    public int Remaining => Math.Max(0, _options.MaxAutoCapturesPerSession - _spent);

    /// <summary>
    /// Frame time a hitch has to reach right now, which is the configured threshold until the session
    /// produces enough large frames to raise it.
    /// </summary>
    public double EffectiveFrameTimeMs => Adapt(_options.AutoCaptureFrameTimeMs, _options.AdaptiveThresholdFramesPerHour);

    /// <summary>Frame time that breaks the cooldown right now, adapted the same way.</summary>
    /// <remarks>
    /// <para>
    /// Kept a fixed distance clear of <see cref="EffectiveFrameTimeMs"/>, and that floor is not a
    /// refinement — without it the two thresholds can meet, and when they meet the exception swallows
    /// the rule. Every frame that clears the ordinary threshold would then also clear the override, so
    /// every capture-worthy frame would bypass both the cooldown and the hourly budget and the session
    /// ceiling would be the only gate left. It is reachable: the two thresholds adapt at different rates
    /// and therefore warm up at different times, so twenty minutes into a session with four frames over
    /// 250 ms the ordinary threshold has moved to the fourth largest of them while the override is still
    /// sitting on its constant. Setting <see cref="DeepCaptureOptions.AdaptiveOverrideFramesPerHour"/>
    /// to zero produces the same collapse by pinning the override while the ordinary threshold climbs
    /// past it.
    /// </para>
    /// <para>
    /// The distance is the one that was configured — 130 ms at the defaults — because that is what the
    /// two settings say an override is: a frame this much beyond an ordinary capture-worthy one. A
    /// margin of zero is the disabled case, and <see cref="OverridesCooldownFor"/> refuses it there
    /// rather than firing on everything.
    /// </para>
    /// </remarks>
    public double EffectiveOverrideFrameTimeMs
    {
        get
        {
            var adapted = Math.Max(
                EffectiveFrameTimeMs + ConfiguredOverrideMarginMs,
                Adapt(_options.AutoCaptureOverrideFrameTimeMs, _options.AdaptiveOverrideFramesPerHour));

            // The session's own level, allowed to lower the exception as well as raise it. Four notes
            // running have recorded the same failure from the other side: the constant sits at 250 ms,
            // an evening's largest frames come in at 150–240, and the exception turns away the very
            // frames it exists for while the budget still holds unspent captures. On 31 August that was
            // a 235 ms frame — the evening's second largest — refused with nine hours of session left.
            // The rate is what bounds it: at three an hour the level is where three frames an hour
            // actually reach, so lowering it cannot admit more than the budget was already sized for.
            if (SessionLevel(_options.AdaptiveOverrideFramesPerHour) is not { } level)
            {
                return adapted;
            }

            return level < adapted && level >= EffectiveFrameTimeMs * MinimumOverrideRatio ? level : adapted;
        }
    }

    /// <summary>
    /// Frame time from which a hitch may also skip the ring buffer's refill, rather than only the
    /// cooldown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the configured constant is for now that the session's own material places the
    /// ordinary exception. A frame this large is the evening's worst, and refusing it to protect the
    /// buffer has now cost four sessions their largest frame in a row — 356 ms at 20:23:50 on
    /// 31 August, refused with "43 s left before the ring buffer has refilled", in the opening ten
    /// minutes where the buffer never catches up at all.
    /// </para>
    /// <para>
    /// Half a ring buffer is worth more than no trace. What it may not skip is the previous capture
    /// still writing itself to disk, which is a different constraint from the refill and is what
    /// <see cref="DeepCaptureOptions.ExtremeCaptureSpacing"/> holds.
    /// </para>
    /// </remarks>
    public double EffectiveExtremeFrameTimeMs =>
        Math.Max(_options.AutoCaptureOverrideFrameTimeMs, EffectiveOverrideFrameTimeMs);

    /// <summary>
    /// How far above the ordinary threshold the configuration puts the exception. Zero when the override
    /// is configured off, which <see cref="DeepCaptureOptions.Normalize"/> produces from any value below
    /// the ordinary one.
    /// </summary>
    private double ConfiguredOverrideMarginMs =>
        Math.Max(0, _options.AutoCaptureOverrideFrameTimeMs - _options.AutoCaptureFrameTimeMs);

    /// <summary>
    /// Feeds one presented frame into the distribution the thresholds are derived from.
    /// </summary>
    /// <remarks>
    /// Called for every frame, so the common path has to be free: a frame smaller than the smallest
    /// retained one is compared once and dropped. The list only reaches its capacity on a session that
    /// has genuinely produced that many large frames, and after that an insert is a memmove over a few
    /// hundred doubles for a frame that arrives a handful of times an hour.
    /// </remarks>
    public void Observe(DateTimeOffset timestamp, double frameTimeMs)
    {
        if (double.IsNaN(frameTimeMs) || frameTimeMs <= 0)
        {
            return;
        }

        _firstFrameAt ??= timestamp;

        // Frame timestamps are derived from PresentMon's trace anchor and can step backwards slightly
        // when it converges, so the session's length is the furthest point reached rather than the last
        // one seen — otherwise a re-anchored batch would shorten the session and lower both thresholds.
        if (_lastFrameAt is not { } last || timestamp > last)
        {
            _lastFrameAt = timestamp;
        }

        var capacity = AdaptiveSampleCapacity;
        if (capacity == 0)
        {
            return;
        }

        if (_largestFrames.Count == capacity && frameTimeMs <= _largestFrames[^1])
        {
            return;
        }

        var index = _largestFrames.BinarySearch(frameTimeMs, DescendingComparer.Instance);
        _largestFrames.Insert(index < 0 ? ~index : index, frameTimeMs);

        if (_largestFrames.Count > capacity)
        {
            _largestFrames.RemoveAt(_largestFrames.Count - 1);
        }
    }

    /// <summary>
    /// Reserves a capture for a single catastrophic frame, returning false when any gate refuses.
    /// </summary>
    /// <remarks>
    /// Reserving is the act of spending: the caller starts the capture immediately afterwards, and a
    /// capture that then fails is still charged, because the ring buffer was disturbed either way.
    /// </remarks>
    public bool TryReserve(DateTimeOffset timestamp, double frameTimeMs, out string? refusal)
    {
        if (frameTimeMs < EffectiveFrameTimeMs)
        {
            // Not a refusal worth reporting: the overwhelming majority of incidents land here, and
            // saying so every time would bury the two cases the user does need to know about.
            refusal = null;
            return false;
        }

        return TryReserveCore(
            timestamp,
            $"en {frameTimeMs:F0} ms hitch",
            mayOverrideCooldown: OverridesCooldownFor(frameTimeMs),
            mayOverrideRefill: SkipsRefillFor(frameTimeMs),
            frameTimeMs,
            out refusal);
    }

    /// <summary>
    /// Whether a frame is extreme enough to be traced against a ring buffer that has not refilled.
    /// </summary>
    /// <remarks>
    /// Answers to the same disabling case as the cooldown exception: with the override configured off
    /// there is no extreme tier either, and every frame waits for the buffer as it always did.
    /// </remarks>
    private bool SkipsRefillFor(double frameTimeMs)
    {
        return _options.AutoCaptureOverrideFrameTimeMs > _options.AutoCaptureFrameTimeMs
            && frameTimeMs >= EffectiveExtremeFrameTimeMs
            && frameTimeMs > _lastCaptureFrameTimeMs;
    }

    /// <summary>
    /// Whether a frame is catastrophic enough for this session to spend budget the cooldown would have
    /// withheld, measured against the session's own material rather than the constant alone.
    /// </summary>
    /// <remarks>
    /// The disabling case is <see cref="DeepCaptureOptions.OverridesCooldownFor"/>'s: an override
    /// threshold set at or below the ordinary one means the exception is off, and no amount of
    /// adaptation may turn it back on.
    /// </remarks>
    private bool OverridesCooldownFor(double frameTimeMs)
    {
        return _options.AutoCaptureOverrideFrameTimeMs > _options.AutoCaptureFrameTimeMs
            && frameTimeMs >= EffectiveOverrideFrameTimeMs;
    }

    /// <summary>
    /// Reserves a capture for a frame rate that has stopped recovering, rather than for one bad frame.
    /// </summary>
    /// <remarks>
    /// The frame time threshold cannot see this case at all, and it is the case that mattered most. A
    /// session spent 104 of 391 minutes below 50 fps in unbroken half-hour blocks, and because no single
    /// frame in them was remarkable, nothing in the app ever asked for a trace of one. Sustained
    /// saturation is raised rarely by <see cref="FramePacingMonitor"/> — once on entering a bad patch and
    /// then on a reminder cadence — so letting it reach the budget adds very few captures and covers the
    /// only condition a per-frame rule structurally cannot.
    /// </remarks>
    public bool TryReserveForSustainedSaturation(DateTimeOffset timestamp, out string? refusal)
    {
        // No frame to be far beyond the threshold, so no override: saturation is by definition a stretch
        // of unremarkable frames, and one stretch is one trace however long it lasts.
        return TryReserveCore(
            timestamp,
            "en period där bildfrekvensen inte återhämtade sig",
            mayOverrideCooldown: false,
            mayOverrideRefill: false,
            frameTimeMs: 0,
            out refusal);
    }

    /// <summary>
    /// Reserves for a consecutive run of frames that never reached the display. Its individual frame
    /// times are normal by definition, so only the kind-aware caller can make it eligible; the ordinary
    /// cooldown and both budgets still apply.
    /// </summary>
    public bool TryReserveForDroppedFrameRun(DateTimeOffset timestamp, out string? refusal)
    {
        return TryReserveCore(
            timestamp,
            "en sammanhängande följd av tappade frames",
            mayOverrideCooldown: false,
            mayOverrideRefill: false,
            frameTimeMs: 0,
            out refusal);
    }

    /// <param name="mayOverrideCooldown">
    /// Whether the frame is catastrophic enough to spend budget the ordinary cooldown would have
    /// withheld. The shorter <see cref="DeepCaptureOptions.AutoCaptureOverrideCooldown"/> still applies:
    /// it is what keeps the override from capturing a ring buffer that has not refilled yet.
    /// </param>
    /// <param name="mayOverrideRefill">
    /// Whether the frame is extreme enough to be worth a half-filled ring buffer. It still answers to
    /// <see cref="DeepCaptureOptions.ExtremeCaptureSpacing"/>, which is the previous capture finishing
    /// its write rather than the buffer filling again — starting a capture inside that produces two
    /// files describing the same seconds and neither of them complete.
    /// </param>
    /// <summary>
    /// What an extreme frame still has to wait for, given what is known about the previous capture.
    /// </summary>
    /// <remarks>
    /// <see cref="DeepCaptureOptions.ExtremeCaptureSpacing"/> is the worst case: a tail held open to
    /// <see cref="DeepCaptureOptions.MaxPostMarkerTail"/> by a stall that would not end, plus the write.
    /// That is the right bound while a capture is unaccounted for, and the wrong one the moment it has
    /// reported its file on disk — nothing is recording then, and the frame in hand is the largest
    /// of the evening. Waiting out a worst case that has already been observed not to happen is how the
    /// 356 ms frame of 31 August was lost.
    /// </remarks>
    private TimeSpan ExtremeSpacingFrom(DateTimeOffset lastCaptureAt)
    {
        return _lastCaptureWrittenAt is { } written && written >= lastCaptureAt
            ? TimeSpan.Zero
            : _options.ExtremeCaptureSpacing;
    }

    private bool TryReserveCore(
        DateTimeOffset timestamp,
        string description,
        bool mayOverrideCooldown,
        bool mayOverrideRefill,
        double frameTimeMs,
        out string? refusal)
    {
        if (!_options.Enabled || !_options.CaptureAutoIncidents)
        {
            refusal = null;
            return false;
        }

        if (_spent >= _options.MaxAutoCapturesPerSession)
        {
            refusal = $"Deep capture hoppades över för {description}: sessionens budget på "
                + $"{_options.MaxAutoCapturesPerSession} automatiska captures är förbrukad. Höj "
                + $"DeepCapture.MaxAutoCapturesPerSession om fler behövs.";
            return false;
        }

        // The window budget rations the ordinary frames so a bad opening hour cannot buy silence for the
        // rest of the evening. A frame past the override threshold answers to the session ceiling only:
        // that ceiling is what this feature was sized around, and turning away a catastrophic frame to
        // preserve an allowance for ordinary ones has the two the wrong way round.
        // Floored at one so an un-normalised options object cannot index past the end of the list below.
        var perWindow = Math.Max(1, _options.MaxAutoCapturesPerWindow);
        if (!mayOverrideCooldown && CountWithin(timestamp, _options.CaptureBudgetWindow) >= perWindow)
        {
            var window = _options.CaptureBudgetWindow;
            var freesAt = _spentAt[^perWindow] + window;
            refusal = $"Deep capture hoppades över för {description}: {perWindow} "
                + $"capture(s) per {window.TotalMinutes:F0} min är redan tagna, nästa plats öppnar om "
                + $"{Math.Max(0, (freesAt - timestamp).TotalMinutes):F0} min. En frame över "
                + $"{EffectiveOverrideFrameTimeMs:F0} ms hade gått förbi den här gränsen.";
            return false;
        }

        if (_lastCaptureAt is { } last)
        {
            var elapsed = timestamp - last;
            var cooldown = mayOverrideRefill
                ? ExtremeSpacingFrom(last)
                : mayOverrideCooldown
                    ? _options.AutoCaptureOverrideCooldown
                    : _options.AutoCaptureCooldown;

            if (elapsed < cooldown)
            {
                var remaining = cooldown - elapsed;
                refusal = mayOverrideRefill
                    ? $"Deep capture hoppades över för {description}: {remaining.TotalSeconds:F0} s kvar innan "
                        + "föregående capture skrivit klart sin fil. Frametimen räckte för att kringgå "
                        + "påfyllningen av ringbufferten, men två captures kan inte skriva samtidigt."
                    : mayOverrideCooldown
                        ? $"Deep capture hoppades över för {description}: {remaining.TotalSeconds:F0} s kvar innan "
                            + "ringbufferten hunnit fyllas på efter föregående capture. Frametimen räckte för att "
                            + $"bryta cooldownen; en frame över {EffectiveExtremeFrameTimeMs:F0} ms hade tagits "
                            + "ändå, mot en halvfull buffert."
                        : $"Deep capture hoppades över för {description}: {remaining.TotalMinutes:F0} min "
                            + "kvar av cooldown efter föregående capture, och ringbufferten har ännu inte fyllts på.";
                return false;
            }
        }

        _spent++;
        _lastCaptureAt = timestamp;
        _lastCaptureFrameTimeMs = frameTimeMs;

        // Only ordinary captures are charged to the window. An override that took the hour's slot would
        // let one catastrophic frame buy silence for the rest of it, which is the failure the window was
        // introduced to prevent — arriving by the one path that is supposed to be exempt from it. The
        // session ceiling and the refill spacing still bound the override, and they are what it is
        // documented to answer to.
        if (!mayOverrideCooldown)
        {
            _spentAt.Add(timestamp);
        }

        refusal = null;
        return true;
    }

    /// <summary>
    /// Raises a configured threshold to where the session's own frames say it belongs, never below it.
    /// </summary>
    /// <remarks>
    /// <paramref name="framesPerHour"/> frames an hour is a count, and a count over a session of known
    /// length picks a frame out of the sorted sample: at three an hour after two hours, the sixth
    /// largest frame is the level three an hour have reached. Below that many frames the session has not
    /// produced the material to raise anything, and the configured constant stands — which is the whole
    /// of the behaviour on an evening that is going well.
    /// </remarks>
    private double Adapt(double configuredMs, double framesPerHour)
    {
        return SessionLevel(framesPerHour) is { } level ? Math.Max(configuredMs, level) : configuredMs;
    }

    /// <summary>
    /// The frame time <paramref name="framesPerHour"/> frames an hour have actually reached this
    /// session, or null while the session has not produced the material to say.
    /// </summary>
    /// <remarks>
    /// The raw level, in both directions. <see cref="Adapt"/> takes it as a floor under a configured
    /// constant, which is right for the ordinary threshold; <see cref="EffectiveOverrideFrameTimeMs"/>
    /// also lets it lower the exception, which is what four notes have asked for and what a constant
    /// cannot do.
    /// </remarks>
    private double? SessionLevel(double framesPerHour)
    {
        if (framesPerHour <= 0 || _firstFrameAt is not { } first || _lastFrameAt is not { } last)
        {
            return null;
        }

        var hours = Math.Clamp((last - first).TotalHours, 0, MaxAdaptiveHours);
        var rank = (int)Math.Floor(framesPerHour * hours);
        if (rank < MinimumAdaptiveRank || _largestFrames.Count < rank)
        {
            return null;
        }

        return _largestFrames[rank - 1];
    }

    /// <summary>
    /// Largest frames worth retaining: what the faster of the two rates asks for over the longest
    /// session it may scale across.
    /// </summary>
    /// <remarks>
    /// Bounded again at 4 096 so a hand-edited rate cannot turn a per-frame path into an allocation. A
    /// rate that large has left the behaviour this models long before the bound is reached.
    /// </remarks>
    private int AdaptiveSampleCapacity
    {
        get
        {
            var rate = Math.Max(_options.AdaptiveThresholdFramesPerHour, _options.AdaptiveOverrideFramesPerHour);
            return rate <= 0 ? 0 : (int)Math.Min(4096, Math.Ceiling(rate * MaxAdaptiveHours));
        }
    }

    /// <summary>
    /// Captures spent inside the trailing <paramref name="window"/> ending at <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// Linear over the session's captures, which is at most
    /// <see cref="DeepCaptureOptions.MaxAutoCapturesPerSession"/> entries — single digits by design, and
    /// consulted once per capture-worthy frame rather than once per frame.
    /// </remarks>
    private int CountWithin(DateTimeOffset now, TimeSpan window)
    {
        var since = now - window;
        var count = 0;
        for (var i = _spentAt.Count - 1; i >= 0 && _spentAt[i] > since; i--)
        {
            count++;
        }

        return count;
    }

    /// <summary>Keeps <see cref="_largestFrames"/> sorted largest first, so rank <c>n</c> is index <c>n-1</c>.</summary>
    private sealed class DescendingComparer : IComparer<double>
    {
        public static readonly DescendingComparer Instance = new();

        public int Compare(double x, double y) => y.CompareTo(x);
    }
}
