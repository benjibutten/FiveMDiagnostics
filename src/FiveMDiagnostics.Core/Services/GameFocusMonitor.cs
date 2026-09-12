namespace FiveMDiagnostics.Core;

/// <summary>Whether a moment belongs to the game, and if not, why it does not.</summary>
public enum GameFocusState
{
    /// <summary>Nothing has been observed yet. Treated as in play, deliberately — see the monitor.</summary>
    Unknown,

    /// <summary>The game owned the foreground and had owned it long enough to be running normally.</summary>
    InPlay,

    /// <summary>Another window owned the foreground.</summary>
    NotInFocus,

    /// <summary>The game has the foreground back, but is still paying for having lost it.</summary>
    Settling,
}

/// <summary>
/// Separates the frames the player lost from the frames nobody was looking at.
/// </summary>
/// <remarks>
/// <para>
/// Alt-tab and the Windows key produce frame times that look exactly like a stutter and are not one. The
/// game drops to background priority, the compositor takes the display, the shell draws its menu, and
/// PresentMon reports every millisecond of it — into the same hitch rate, the same auto-detector
/// baseline, the same VRAM band comparison and the same incident list as a freeze in traffic. On
/// 4 September five of the nine stalls that were measured deeply coincided with Windows drawing part of
/// itself, and the session could not tell them apart from the four that did not.
/// </para>
/// <para>
/// The rule is not that those seconds are uninteresting. It is that they are a different question. So
/// they are excluded from every measurement of how the game ran, counted separately, and reported —
/// with the process that took the foreground, because "the start menu was open for eleven seconds" is
/// checkable in a way that "the game was out of focus" is not.
/// </para>
/// <para>
/// Fails open. With no focus telemetry at all — the collector missing, the machine locked, a session
/// recorded before this existed — every frame counts as a frame in play, which is what the app did
/// before and is the only safe default: silently discarding an evening's hitches because a collector
/// went quiet would be far worse than counting an alt-tab.
/// </para>
/// </remarks>
public sealed class GameFocusMonitor
{
    /// <summary>
    /// How long after regaining the foreground the game is still paying for the switch.
    /// </summary>
    /// <remarks>
    /// Coming back is more expensive than leaving. The window is restored, the swap chain is re-created,
    /// the driver re-uploads whatever it evicted while the game was behind, and the streamer refills. Two
    /// seconds is the span over which the captures of 4 September show frame times settling back to the
    /// evening's median after a switch; it is also short enough that it cannot hide a real stall a few
    /// seconds later.
    /// </remarks>
    public static readonly TimeSpan RegainGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far back a focus loss reaches when it is finally observed.
    /// </summary>
    /// <remarks>
    /// The cost of leaving starts before Windows has finished changing the foreground: the key is
    /// pressed, the shell begins drawing, and only then does the foreground window change and the next
    /// poll see it. Half a second covers that ordering plus the poll interval. It is applied backwards
    /// over frames that have already been counted, which the live gate cannot do — so the tallies below
    /// are right about a switch even though the frame passed through the detector before anything knew.
    /// </remarks>
    public static readonly TimeSpan LossGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Shortest excursion that earns a line of its own in the journal.
    /// </summary>
    /// <remarks>
    /// Two seconds is about the shortest deliberate alt-tab there is. Below it the switch is a misclick
    /// or a notification stealing the foreground for a moment; those are still excluded from the
    /// measurements, they simply are not worth a sentence each.
    /// </remarks>
    private static readonly TimeSpan ReportableExcursion = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Frames held for the retroactive correction above. Only their timestamps and times are kept, and
    /// only for <see cref="LossGrace"/>, so this is tens of entries at any frame rate a game produces.
    /// </summary>
    private readonly Queue<(DateTimeOffset At, double FrameTimeMs)> _recentCounted = new();

    /// <summary>
    /// One entry per change of foreground, oldest first. An evening produces a few dozen; a session that
    /// somehow produced thousands is still a list of small records.
    /// </summary>
    /// <remarks>
    /// Kept as the samples themselves so the live path and <see cref="Classify"/> — which the analysis
    /// engine runs over the samples inside an incident window — share one implementation of what a
    /// moment's focus state is. Two implementations of that rule would disagree at exactly the edges the
    /// grace periods exist for.
    /// </remarks>
    private readonly List<WindowFocusSample> _transitions = [];

    /// <summary>How long each foreground process held the foreground, and how often it took it.</summary>
    private readonly Dictionary<string, (int Count, TimeSpan Held)> _byForegroundProcess = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the game most recently lost the foreground, or null while it has it.
    /// </summary>
    /// <remarks>
    /// One excursion is one stretch with the game behind something, however many windows the player
    /// moved through while it lasted. Ending it at each change of window instead — which this did until
    /// it was reviewed — reports alt-tabbing through Chrome and Explorer and back as two excursions of
    /// ten seconds rather than one of twenty: the count comes out inflated, the longest comes out
    /// understated, and the journal gets a line per window rather than a line per switch. The total time
    /// was right either way, which is why it read as correct.
    /// </remarks>
    private DateTimeOffset? _excursionStart;

    /// <summary>
    /// How long each window held the foreground inside the excursion currently open, so the line can
    /// name the one that actually held it. Cleared when the excursion closes.
    /// </summary>
    private readonly Dictionary<string, TimeSpan> _excursionHolders = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly HitchThreshold _hitch;

    /// <summary>
    /// Frames held back until the bar is fixed. They are counted on the same rule as the rest of the
    /// session once it is, rather than against a provisional bar nothing else in the session uses.
    /// </summary>
    private readonly List<(DateTimeOffset At, double FrameTimeMs)> _warmup = [];

    private DateTimeOffset? _firstSampleAt;
    private DateTimeOffset? _lastSampleAt;

    private int _framesInPlay;
    private int _hitchesInPlay;
    private int _framesExcluded;
    private int _hitchesExcluded;
    private int _excursions;
    private TimeSpan _totalUnfocused;
    private TimeSpan _longestUnfocused;

    /// <param name="hitch">
    /// The session's hitch bar. The same one <see cref="VramPressureBandMonitor"/> and
    /// <see cref="CaptureCostMonitor"/> read, so the lines of a session summary count the same frames.
    /// This counted against two refreshes alone until 2026-09-12, which is a different figure whenever
    /// the game is not running at the panel's rate.
    /// </param>
    public GameFocusMonitor(HitchThreshold hitch)
    {
        _hitch = hitch;
    }

    /// <summary>
    /// Folds one reading of the foreground in, and returns a line for the journal when an excursion long
    /// enough to matter has just ended.
    /// </summary>
    /// <remarks>
    /// The line is returned rather than the change, because the alternative is for the session manager to
    /// keep its own copy of when focus was lost in order to write the same sentence — two places holding
    /// the same state, which is how the window-mode history and the settings reader drifted apart before.
    /// Short excursions produce nothing: a click through to another monitor and straight back is not an
    /// event, and a journal line per click would bury the ones that are.
    /// </remarks>
    public string? Observe(WindowFocusSample sample)
    {
        lock (_sync)
        {
            _firstSampleAt ??= sample.Timestamp;
            _lastSampleAt = sample.Timestamp;

            string? line = null;

            if (_transitions.Count > 0)
            {
                var previous = _transitions[^1];
                if (previous.GameHasFocus == sample.GameHasFocus
                    && string.Equals(previous.ForegroundProcessName, sample.ForegroundProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    // A heartbeat. It moves the end of the session forward and nothing else.
                    return null;
                }

                if (!previous.GameHasFocus)
                {
                    // The window in front has changed. Credit the one that was there for the time it
                    // held, and end the excursion only if what took over is the game — moving from one
                    // background window to another is the same excursion continuing.
                    CreditHolder(previous, sample.Timestamp);

                    if (sample.GameHasFocus)
                    {
                        line = CloseExcursion(sample.Timestamp);
                    }
                }
                else if (!sample.GameHasFocus)
                {
                    BeginExcursion(sample.Timestamp);
                }
            }
            else if (!sample.GameHasFocus)
            {
                BeginExcursion(sample.Timestamp);
            }

            _transitions.Add(sample);
            return line;
        }
    }

    /// <summary>
    /// Where a moment falls. Anything before the first reading is <see cref="GameFocusState.Unknown"/>,
    /// which the caller is expected to treat as play.
    /// </summary>
    public GameFocusState StateAt(DateTimeOffset at)
    {
        lock (_sync)
        {
            return StateAtCore(at);
        }
    }

    /// <summary>
    /// Counts one presented frame, and says whether the rest of the session should look at it.
    /// </summary>
    /// <remarks>
    /// The tally and the verdict are one call because they must not be able to disagree. Two calls would
    /// let a caller gate the detector on one answer and count the frame under another, which is exactly
    /// the kind of divergence that makes a summary line stop matching the incident list.
    /// </remarks>
    public bool ObserveFrame(DateTimeOffset at, double frameTimeMs)
    {
        lock (_sync)
        {
            var inPlay = IsInPlay(at);

            if (!_hitch.IsSettled)
            {
                _warmup.Add((at, frameTimeMs));
                return inPlay;
            }

            DrainWarmup();
            Count(at, frameTimeMs, inPlay);
            return inPlay;
        }
    }

    /// <summary>Files one frame on the side it belongs to. Called under the lock.</summary>
    private void Count(DateTimeOffset at, double frameTimeMs, bool inPlay)
    {
        var isHitch = frameTimeMs >= _hitch.ThresholdMs;

        if (!inPlay)
        {
            _framesExcluded++;
            if (isHitch)
            {
                _hitchesExcluded++;
            }

            return;
        }

        _framesInPlay++;
        if (isHitch)
        {
            _hitchesInPlay++;
        }

        // Held only long enough for a focus loss noticed a moment later to take them back.
        _recentCounted.Enqueue((at, frameTimeMs));
        while (_recentCounted.Count > 0 && at - _recentCounted.Peek().At > LossGrace)
        {
            _recentCounted.Dequeue();
        }
    }

    /// <summary>
    /// Files the held-back frames now that the bar is known. Called under the lock.
    /// </summary>
    /// <remarks>
    /// Each is classified again rather than on the verdict returned at the time. By now the readings
    /// that follow it have arrived, so the look-ahead sees a focus loss the live call could not — which
    /// is what <see cref="ReclaimFramesBefore"/> exists to repair for the frames that were counted.
    /// </remarks>
    private void DrainWarmup()
    {
        if (_warmup.Count == 0)
        {
            return;
        }

        var held = _warmup.ToArray();
        _warmup.Clear();
        _warmup.TrimExcess();

        foreach (var (at, frameTimeMs) in held)
        {
            Count(at, frameTimeMs, IsInPlay(at));
        }
    }

    /// <summary>Called under the lock.</summary>
    private bool IsInPlay(DateTimeOffset at) =>
        StateAtCore(at) is not (GameFocusState.NotInFocus or GameFocusState.Settling);

    /// <summary>
    /// The session's focus accounting, or null when nothing was ever observed.
    /// </summary>
    /// <remarks>
    /// An excursion still open when this runs is counted up to the last reading. The session on this
    /// machine is normally ended by the computer being switched off, so refusing to count the excursion
    /// in progress would routinely drop the last one of the evening.
    /// </remarks>
    public GameFocusReport? Summary()
    {
        lock (_sync)
        {
            // A session shorter than the warm-up still gets a bar, from the frames it has.
            _hitch.Settle();
            DrainWarmup();

            if (_transitions.Count == 0 || _firstSampleAt is not { } first || _lastSampleAt is not { } last)
            {
                return null;
            }

            var excursions = _excursions;
            var total = _totalUnfocused;
            var longest = _longestUnfocused;
            var holders = _byForegroundProcess;

            if (_excursionStart is { } openedAt)
            {
                var open = last - openedAt;
                if (open > TimeSpan.Zero)
                {
                    excursions++;
                    total += open;
                    longest = open > longest ? open : longest;
                }

                // The window in front right now has not been credited yet — a holder is credited when it
                // stops holding — so the top list is built over a copy that includes it. A copy because
                // this runs on the interim summary every quarter of an hour and must not accumulate the
                // same open stretch each time.
                var current = _transitions[^1];
                var segment = last - current.Timestamp;
                if (!current.GameHasFocus && segment > TimeSpan.Zero)
                {
                    var name = NameOf(current);
                    holders = new Dictionary<string, (int Count, TimeSpan Held)>(_byForegroundProcess, StringComparer.OrdinalIgnoreCase);
                    var previous = holders.GetValueOrDefault(name);
                    holders[name] = (previous.Count + 1, previous.Held + segment);
                }
            }

            var top = holders
                .OrderByDescending(entry => entry.Value.Held)
                .Take(3)
                .Select(entry => new ForegroundHolder(entry.Key, entry.Value.Count, entry.Value.Held))
                .ToArray();

            return new GameFocusReport(
                last - first,
                excursions,
                total,
                longest,
                _framesInPlay,
                _hitchesInPlay,
                _framesExcluded,
                _hitchesExcluded,
                _hitch.ThresholdMs,
                top);
        }
    }

    /// <summary>Called under the lock.</summary>
    private GameFocusState StateAtCore(DateTimeOffset at) => Classify(_transitions, at);

    /// <summary>
    /// Where a moment falls, given the focus readings that bracket it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static and taking its readings as an argument because it is used from two places that hold them
    /// differently: this monitor, live, as frames arrive; and the analysis engine, minutes later, over
    /// the samples that landed inside an incident window. The rule has to be the same in both, and the
    /// grace periods are what make it easy for two copies of it to disagree.
    /// </para>
    /// <para>
    /// The look-ahead is the one part that only works after the fact. A Windows key press begins costing
    /// frames before Windows has finished handing the foreground over, so a reading that says "not in
    /// focus" a fraction of a second later is evidence about the moment before it. The live path has no
    /// future readings and cannot use this; it corrects the same frames backwards instead, once the loss
    /// is observed.
    /// </para>
    /// </remarks>
    /// <param name="readings">Focus readings in ascending order of time. Out-of-order input is not handled.</param>
    public static GameFocusState Classify(IReadOnlyList<WindowFocusSample> readings, DateTimeOffset at)
    {
        if (readings.Count == 0)
        {
            return GameFocusState.Unknown;
        }

        // Before the first reading nothing is known. Deliberately not resolved to the first reading's
        // answer the way the window-mode history is: that one reads a settings file that was already
        // true before the session started, and this one reads a foreground that was not.
        if (at < readings[0].Timestamp)
        {
            return GameFocusState.Unknown;
        }

        var index = readings.Count - 1;
        while (index > 0 && readings[index].Timestamp > at)
        {
            index--;
        }

        var current = readings[index];
        if (!current.GameHasFocus)
        {
            return GameFocusState.NotInFocus;
        }

        for (var ahead = index + 1; ahead < readings.Count; ahead++)
        {
            if (readings[ahead].Timestamp - at > LossGrace)
            {
                break;
            }

            if (!readings[ahead].GameHasFocus)
            {
                return GameFocusState.NotInFocus;
            }
        }

        // The game has the foreground. It is still settling if it only just got it back — and only if it
        // got it back, rather than having held it since the session began.
        //
        // The moment it got it back is the start of the unbroken run of readings that say it has it, not
        // the previous reading. The live path holds transitions only, where the two are the same; the
        // engine classifies over the raw samples in an incident window, and the collector writes a
        // heartbeat every five seconds as well as on change. Against those, "there is an earlier
        // reading" is true of every moment, and two seconds out of every five came out as Settling — a
        // verdict that rules the window out at 95% confidence, on an evening where the game never left
        // the foreground at all.
        var regainedAt = index;
        while (regainedAt > 0 && readings[regainedAt - 1].GameHasFocus)
        {
            regainedAt--;
        }

        if (regainedAt > 0 && at - readings[regainedAt].Timestamp < RegainGrace)
        {
            return GameFocusState.Settling;
        }

        return GameFocusState.InPlay;
    }

    /// <summary>
    /// A window worth flagging on an <see cref="GameFocusState.InPlay"/> frame, even though it already
    /// counts as play. Longer than <see cref="RegainGrace"/> on purpose.
    /// </summary>
    /// <remarks>
    /// <see cref="RegainGrace"/> decides what still costs the switch itself and is excluded; this decides
    /// what is worth a caveat once a frame has cleared that bar and counts as spellagg regardless. GTA V
    /// runs with <c>PauseOnFocusLoss 1</c>, so regaining the foreground restarts the simulation and streams
    /// back in whatever had evicted while it was away — a cost that does not always finish inside two
    /// seconds. The 738 ms frame at 22:19:04 on 7 September landed 3.2 seconds after such a return, just
    /// past <see cref="RegainGrace"/>, and lengthening that grace period would have hidden a real stall
    /// behind the same excuse instead of only explaining the switch. Five seconds is long enough to cover
    /// the observed case with margin while leaving the frame counted, tagged, and countable either way.
    /// </remarks>
    public static readonly TimeSpan ExtendedRegainWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the game has held the foreground continuously as of <paramref name="at"/>, or null when it
    /// does not have it then, or has held it since the first reading with nothing to measure the return
    /// from.
    /// </summary>
    /// <param name="readings">Focus readings in ascending order of time. Out-of-order input is not handled.</param>
    public static TimeSpan? TimeSinceRegainedFocus(IReadOnlyList<WindowFocusSample> readings, DateTimeOffset at)
    {
        if (readings.Count == 0 || at < readings[0].Timestamp)
        {
            return null;
        }

        var index = readings.Count - 1;
        while (index > 0 && readings[index].Timestamp > at)
        {
            index--;
        }

        if (!readings[index].GameHasFocus)
        {
            return null;
        }

        var regainedAt = index;
        while (regainedAt > 0 && readings[regainedAt - 1].GameHasFocus)
        {
            regainedAt--;
        }

        return regainedAt > 0 ? at - readings[regainedAt].Timestamp : null;
    }

    /// <summary>
    /// Moves the frames counted just before a focus loss into the excluded tally. Called under the lock.
    /// </summary>
    private void ReclaimFramesBefore(DateTimeOffset lostAt)
    {
        var from = lostAt - LossGrace;

        while (_recentCounted.Count > 0)
        {
            var frame = _recentCounted.Dequeue();
            if (frame.At < from)
            {
                continue;
            }

            _framesInPlay--;
            _framesExcluded++;
            if (frame.FrameTimeMs >= _hitch.ThresholdMs)
            {
                _hitchesInPlay--;
                _hitchesExcluded++;
            }
        }
    }

    /// <summary>Notes that the game has lost the foreground. Called under the lock.</summary>
    /// <remarks>
    /// Idempotent: an excursion already open stays open with its original start, so a switch between two
    /// background windows cannot restart the clock.
    /// </remarks>
    private void BeginExcursion(DateTimeOffset lostAt)
    {
        _excursionStart ??= lostAt;
        ReclaimFramesBefore(lostAt);
    }

    /// <summary>
    /// Credits one window with the stretch it held the foreground. Called under the lock.
    /// </summary>
    private void CreditHolder(WindowFocusSample segment, DateTimeOffset endedAt)
    {
        var held = endedAt - segment.Timestamp;
        if (held <= TimeSpan.Zero)
        {
            return;
        }

        var name = NameOf(segment);
        var previous = _byForegroundProcess.GetValueOrDefault(name);
        _byForegroundProcess[name] = (previous.Count + 1, previous.Held + held);
        _excursionHolders[name] = _excursionHolders.GetValueOrDefault(name) + held;
    }

    /// <summary>Called under the lock.</summary>
    private string? CloseExcursion(DateTimeOffset endedAt)
    {
        if (_excursionStart is not { } start)
        {
            return null;
        }

        _excursionStart = null;

        var windows = _excursionHolders
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .ToArray();

        _excursionHolders.Clear();

        var held = endedAt - start;
        if (held <= TimeSpan.Zero)
        {
            return null;
        }

        _excursions++;
        _totalUnfocused += held;
        if (held > _longestUnfocused)
        {
            _longestUnfocused = held;
        }

        if (held < ReportableExcursion)
        {
            return null;
        }

        // The window that held the foreground longest, which is the one worth naming when the player
        // moved through several. The rest are counted; naming them all would make the line unreadable.
        var name = windows.Length == 0 ? "okänt fönster" : windows[0];
        var others = windows.Length > 1 ? $" och {windows.Length - 1} fönster till" : string.Empty;

        return $"Spelet låg ur fokus i {held.TotalSeconds:F0} s ({start.ToLocalTime():HH:mm:ss}–"
            + $"{endedAt.ToLocalTime():HH:mm:ss}); {name}{others} tog förgrunden. Frames i den perioden "
            + "räknas inte som spellagg.";
    }

    private static string NameOf(WindowFocusSample sample) =>
        string.IsNullOrWhiteSpace(sample.ForegroundProcessName) ? "okänt fönster" : sample.ForegroundProcessName;
}

/// <summary>One process that held the foreground while the game did not.</summary>
public sealed record ForegroundHolder(string ProcessName, int Times, TimeSpan Held);

/// <summary>What the session spent behind other windows, and what that cost the hitch count.</summary>
public sealed record GameFocusReport(
    TimeSpan Measured,
    int Excursions,
    TimeSpan Unfocused,
    TimeSpan LongestExcursion,
    int FramesInPlay,
    int HitchesInPlay,
    int FramesExcluded,
    int HitchesExcluded,
    double HitchThresholdMs,
    IReadOnlyList<ForegroundHolder> TopForegroundProcesses)
{
    /// <summary>Share of the measured time the game spent behind another window.</summary>
    public double UnfocusedShare => Measured > TimeSpan.Zero ? Unfocused / Measured : 0;

    /// <summary>
    /// Share of the session's hitches that happened while nobody was looking at the game.
    /// </summary>
    /// <remarks>
    /// The figure the whole class exists to produce. A session where this is a third has been reporting a
    /// hitch rate half again as high as the one the player actually experienced.
    /// </remarks>
    public double ExcludedHitchShare => HitchesInPlay + HitchesExcluded > 0
        ? (double)HitchesExcluded / (HitchesInPlay + HitchesExcluded)
        : 0;

    public string Message
    {
        get
        {
            if (Excursions == 0)
            {
                return $"Fönsterfokus: spelet låg i förgrunden hela den mätta tiden ({Measured.TotalMinutes:F0} min). "
                    + $"Samtliga {HitchesInPlay} hitches ≥{HitchThresholdMs:F0} ms räknas som spellagg.";
            }

            var holders = TopForegroundProcesses.Count > 0
                ? " I förgrunden låg då: "
                    + string.Join(", ", TopForegroundProcesses.Select(item =>
                        $"{item.ProcessName} ({item.Times} ggr, {item.Held.TotalSeconds:F0} s)"))
                    + "."
                : string.Empty;

            var cost = HitchesExcluded > 0
                ? $" {HitchesExcluded} av {HitchesInPlay + HitchesExcluded} hitches ≥{HitchThresholdMs:F0} ms "
                    + $"({ExcludedHitchShare:P0}) inföll där och räknas inte som spellagg: spelet var inte i "
                    + $"bild. Kvar i spelet: {HitchesInPlay} hitches på {FramesInPlay:N0} frames."
                : $" Inga hitches ≥{HitchThresholdMs:F0} ms inföll där. Kvar i spelet: {HitchesInPlay} hitches "
                    + $"på {FramesInPlay:N0} frames.";

            return $"Fönsterfokus: spelet låg bakom ett annat fönster i {Unfocused.TotalMinutes:F1} av "
                + $"{Measured.TotalMinutes:F0} minuter ({UnfocusedShare:P0}), {Excursions} gånger, längst "
                + $"{LongestExcursion.TotalSeconds:F0} s.{holders}{cost} Räknat med "
                + $"{GameFocusMonitor.RegainGrace.TotalSeconds:F0} s efter varje återgång och "
                + $"{GameFocusMonitor.LossGrace.TotalMilliseconds:F0} ms före varje växling, eftersom själva "
                + "växlingen kostar frames i båda ändar.";
        }
    }
}
