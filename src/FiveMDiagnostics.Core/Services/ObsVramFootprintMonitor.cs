namespace FiveMDiagnostics.Core;

/// <summary>
/// Measures what the stream stack costs the card, by reading the steps when it stops.
/// </summary>
/// <remarks>
/// <para>
/// The app already writes "OBS avslutades. Allt efter den här punkten mäts utan OBS, vilket gör
/// perioderna före och efter jämförbara som ett A/B-test" and then reports no comparison at all. Twice
/// now the measurement has been done by hand from the GPU log afterwards, and it took five minutes both
/// times: 8 September gave roughly 740 MB and 9 September roughly 784 MB, the two within 6% of each
/// other.
/// </para>
/// <para>
/// Read as steps rather than as period medians, deliberately. The game refills free video memory within
/// half a minute of it appearing — on 9 September the card went 75.4% to 77.8% in forty seconds with OBS
/// already gone — so a median of the period after is a median of the refill, and the first version of
/// this measurement was wrong by a fifth for exactly that reason. The seconds either side of the
/// transition are the only ones that describe it.
/// </para>
/// <para>
/// The stack comes off in two steps on this machine and they are worth apart: stopping the stream frees
/// the encoder, and quitting OBS frees the canvas, the game capture and the browser sources. Only the
/// second is a step this can see when someone quits OBS without stopping the stream first, and the
/// report then names one step instead of two.
/// </para>
/// </remarks>
public sealed class ObsVramFootprintMonitor
{
    /// <summary>Seconds before a transition whose readings describe the state being left.</summary>
    /// <remarks>
    /// Short, because the point is the level immediately before. At two readings a second this is a dozen
    /// samples, enough for a median that one outlier cannot move.
    /// </remarks>
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The window after a transition, and the pause before it starts.
    /// </summary>
    /// <remarks>
    /// The pause lets the release finish — the encoder's memory came back over two readings on both
    /// measured evenings — and the window closes well before the game has refilled the space, which took
    /// some forty seconds when it happened.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan After = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long before a transition is noticed the memory may already have been released.
    /// </summary>
    /// <remarks>
    /// OBS frees its video memory while it shuts down, before the process is gone. On 2026-09-13 the card
    /// dropped 521 MB at 02:21:12 and OBS was seen gone at 02:21:16, so the "before" window already held
    /// the drop, the "after" window held the game's refill six seconds later, and the step came out at
    /// −109 MB. The "before" window ends this long ahead of the transition, and the "after" level is the
    /// lowest reading from there on: the release lands somewhere inside, and the refill can only raise it.
    /// </remarks>
    private static readonly TimeSpan ReleaseLead = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long the period without OBS has to run before it is offered as a comparison of anything but
    /// video memory.
    /// </summary>
    /// <remarks>
    /// On 9 September it was eleven minutes of nobody really playing. The VRAM steps are seconds wide and
    /// stand on their own; a hitch rate off eleven minutes does not, and the line says so rather than
    /// letting a reader take the quiet tail of an evening for a result.
    /// </remarks>
    private static readonly TimeSpan UsableComparison = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far apart the stream stopping and OBS quitting have to land for the two steps to be read as
    /// separate.
    /// </summary>
    /// <remarks>
    /// On 2026-09-11 the two were four seconds apart, and the encoder's step was measured at 692 MB
    /// against roughly 235 MB on three other evenings where the routine — stop the stream, wait, then
    /// quit — was followed. The windows may overlap at this distance; what may not happen is one step's
    /// release landing inside the other's reading. The encoder's "after" minimum runs to twelve seconds
    /// past the stream stopping, and OBS can release up to <see cref="ReleaseLead"/> before it is seen gone,
    /// so its drop reaches that minimum at eighteen seconds apart. Twenty clears it.
    /// </remarks>
    private static readonly TimeSpan MinimumStepSeparation = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How much of the reading history is kept.
    /// </summary>
    /// <remarks>
    /// Long enough to answer both sides of a transition that has just happened, and no longer: a step is
    /// worked out and stored the moment its after-window closes, so nothing here has to survive the
    /// evening. A session's worth of readings would be a couple of megabytes and never read again.
    /// </remarks>
    private static readonly TimeSpan History = ReleaseLead + Before + Settle + After + TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the game may go without presenting a frame before the monitor takes it as closing.
    /// </summary>
    /// <remarks>
    /// Frames reach the session a second or so behind the adapter readings, so a shorter silence would cut
    /// windows short on an ordinary delivery delay.
    /// </remarks>
    private static readonly TimeSpan GameSilence = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private readonly List<(DateTimeOffset At, double Percent)> _readings = [];

    private double _totalVramGb;
    private bool? _streaming;
    private bool? _processRunning;
    private DateTimeOffset? _streamStoppedAt;
    private DateTimeOffset? _processStoppedAt;
    private DateTimeOffset? _lastReadingAt;
    private DateTimeOffset? _lastGameFrameAt;
    private ObsVramStep? _encoderStep;
    private ObsVramStep? _restOfStackStep;

    /// <summary>Folds one adapter reading in.</summary>
    /// <returns>
    /// True on the reading that completes a step. The line is written then rather than on the next
    /// quarter-hour: on 2026-09-12 both steps were measured, 100 seconds apart, and the app was gone
    /// before any summary came due.
    /// </returns>
    public bool Observe(GpuTelemetrySample sample)
    {
        if (!sample.IsAvailable || sample.VramUsagePercent is not { } percent)
        {
            return false;
        }

        lock (_sync)
        {
            _readings.Add((sample.Timestamp, percent));
            _lastReadingAt = sample.Timestamp;

            if (sample.TotalVramBytes is { } total)
            {
                _totalVramGb = total / 1024d / 1024 / 1024;
            }

            // Each step is worked out once, as soon as the readings that describe it have all arrived.
            var hadEncoder = _encoderStep is not null;
            var hadRestOfStack = _restOfStackStep is not null;
            // The encoder first: the rest of the stack may start where the encoder's step ended.
            _encoderStep ??= EncoderStep(sample.Timestamp, sessionEnding: false);
            _restOfStackStep ??= RestOfStackStep(sample.Timestamp, sessionEnding: false);
            var completed = (!hadEncoder && _encoderStep is not null) || (!hadRestOfStack && _restOfStackStep is not null);

            var cutoff = sample.Timestamp - History;
            var stale = 0;
            while (stale < _readings.Count && _readings[stale].At < cutoff)
            {
                stale++;
            }

            if (stale > 0)
            {
                _readings.RemoveRange(0, stale);
            }

            return completed;
        }
    }

    /// <summary>Folds one OBS reading in, and notes the moment either counter turns off.</summary>
    /// <remarks>
    /// Only the transitions to off are kept. OBS starting mid-session is a different experiment run
    /// backwards — the game has already filled the memory the stack is about to want, so the step is the
    /// driver rearranging rather than the stack's size — and this measures the one that reads cleanly.
    /// </remarks>
    public void Observe(ObsTelemetrySample sample)
    {
        lock (_sync)
        {
            if (_streaming == true && !sample.IsStreaming)
            {
                _streamStoppedAt ??= sample.Timestamp;
            }

            if (_processRunning == true && !sample.IsProcessRunning)
            {
                _processStoppedAt ??= sample.Timestamp;
            }

            _streaming = sample.IsStreaming;
            _processRunning = sample.IsProcessRunning;
        }
    }

    /// <summary>Notes that the game presented a frame, in focus or not.</summary>
    public void ObserveGameFrame(DateTimeOffset at)
    {
        lock (_sync)
        {
            if (_lastGameFrameAt is not { } last || at > last)
            {
                _lastGameFrameAt = at;
            }
        }
    }

    /// <summary>The steps, or null when the stack never came off during the session.</summary>
    /// <param name="sessionEnding">
    /// True when no more readings will come. A step whose "after" window is still open is then read from
    /// the readings there are, as long as they reach <see cref="Settle"/> past the transition: a machine
    /// switched off seconds after OBS quit leaves no other chance to read it.
    /// </param>
    public ObsVramFootprintReport? Summary(bool sessionEnding = false)
    {
        lock (_sync)
        {
            if (sessionEnding && _lastReadingAt is { } now)
            {
                _encoderStep ??= EncoderStep(now, sessionEnding: true);
                _restOfStackStep ??= RestOfStackStep(now, sessionEnding: true);
            }

            if (_encoderStep is null && _restOfStackStep is null)
            {
                return null;
            }

            var lastTransition = _processStoppedAt ?? _streamStoppedAt;
            var tail = _lastReadingAt is { } last && lastTransition is { } at ? last - at : TimeSpan.Zero;

            return new ObsVramFootprintReport(
                _encoderStep,
                _restOfStackStep,
                _totalVramGb,
                tail,
                tail >= UsableComparison,
                StepSeparation,
                StepsTooClose,
                _processStoppedAt);
        }
    }

    /// <summary>How far apart the stream stopping and OBS quitting landed, or null unless both did.</summary>
    private TimeSpan? StepSeparation => _streamStoppedAt is { } stream && _processStoppedAt is { } process
        ? (process - stream).Duration()
        : null;

    private bool StepsTooClose => StepSeparation is { } gap && gap < MinimumStepSeparation;

    /// <summary>
    /// The drop across the stream stopping, once its readings have arrived. Called under the lock.
    /// </summary>
    /// <remarks>
    /// When OBS quits inside the "after" window, its own release lands there too and the whole stack
    /// reads as the encoder, so the window ends where that release can begin: <see cref="ReleaseLead"/>
    /// before OBS was seen gone.
    /// </remarks>
    private ObsVramStep? EncoderStep(DateTimeOffset now, bool sessionEnding)
    {
        if (!IsReadable(_streamStoppedAt, now, sessionEnding))
        {
            return null;
        }

        var moment = _streamStoppedAt!.Value;
        var end = AfterWindowEnd(moment);
        if (_processStoppedAt is { } quit && quit - ReleaseLead > moment && quit - ReleaseLead < end)
        {
            end = quit - ReleaseLead;
        }

        return Step(moment, Median(Window(moment - ReleaseLead - Before, moment - ReleaseLead)), end);
    }

    /// <summary>
    /// The drop across OBS quitting, once its readings have arrived. Called under the lock.
    /// </summary>
    /// <remarks>
    /// Closer than <see cref="MinimumStepSeparation"/> to the stream stopping, the seconds before OBS quit
    /// still hold the encoder's release, and a level read there would count the encoder twice. The rest
    /// of the stack then starts where the encoder's step ended.
    /// </remarks>
    private ObsVramStep? RestOfStackStep(DateTimeOffset now, bool sessionEnding)
    {
        if (!IsReadable(_processStoppedAt, now, sessionEnding))
        {
            return null;
        }

        var moment = _processStoppedAt!.Value;
        var before = StepsTooClose && _encoderStep is { } encoder
            ? encoder.PercentAfter
            : Median(Window(moment - ReleaseLead - Before, moment - ReleaseLead));

        return Step(moment, before, AfterWindowEnd(moment));
    }

    /// <summary>
    /// Whether the readings describing a transition have arrived: the whole "after" window, or at the end
    /// of a session only the seconds the release needs.
    /// </summary>
    private static bool IsReadable(DateTimeOffset? at, DateTimeOffset now, bool sessionEnding)
    {
        return at is { } moment && now >= moment + Settle + (sessionEnding ? TimeSpan.Zero : After);
    }

    /// <summary>
    /// The drop from <paramref name="before"/> to the lowest reading up to <paramref name="afterEnd"/>, or
    /// null when either side was never sampled.
    /// </summary>
    private ObsVramStep? Step(DateTimeOffset moment, double? before, DateTimeOffset afterEnd)
    {
        var after = Window(moment - ReleaseLead, afterEnd);
        return before is { } from && after.Length > 0 ? new ObsVramStep(moment, from, after.Min()) : null;
    }

    /// <summary>
    /// Where a step's "after" reading ends: at the game's last frame when the game stopped presenting
    /// inside the window. Called under the lock.
    /// </summary>
    /// <remarks>
    /// The game frees its memory in the seconds after its last frame, and a minimum taken across that
    /// is the game's memory rather than the stack's. On 2026-09-24 OBS quit at 01:35:49, the game's last
    /// frame came at 01:35:54 and the card fell 6.8 GB from 01:35:56, which read as a 7 GB stream stack.
    /// </remarks>
    private DateTimeOffset AfterWindowEnd(DateTimeOffset moment)
    {
        var end = moment + Settle + After;
        return _lastGameFrameAt is { } last && last > moment - ReleaseLead && last < end - GameSilence
            ? last
            : end;
    }

    private double[] Window(DateTimeOffset from, DateTimeOffset to)
    {
        return _readings
            .Where(item => item.At > from && item.At <= to)
            .Select(item => item.Percent)
            .ToArray();
    }

    private static double? Median(double[] window)
    {
        if (window.Length == 0)
        {
            return null;
        }

        Array.Sort(window);
        return window[window.Length / 2];
    }
}

/// <param name="PercentBefore">Median occupancy in the seconds before the release.</param>
/// <param name="PercentAfter">Lowest occupancy across the release, before the game refills.</param>
public sealed record ObsVramStep(DateTimeOffset At, double PercentBefore, double PercentAfter)
{
    public double PercentagePoints => PercentBefore - PercentAfter;

    public double MegabytesFreed(double totalVramGb) => PercentagePoints / 100 * totalVramGb * 1024;
}

/// <param name="TailWithoutObs">How long the session kept measuring after the last transition.</param>
/// <param name="TailIsUsable">
/// Whether that tail is long enough to compare anything but video memory across.
/// </param>
/// <param name="StepSeparation">
/// How far apart the stream stopping and OBS quitting landed, or null when only one of them happened.
/// </param>
/// <param name="StepsTooClose">
/// Whether that separation is short enough that the two releases cannot be told apart with certainty.
/// The total stands; the split between the steps does not.
/// </param>
/// <param name="ObsQuitAt">When OBS was seen gone, or null when it kept running.</param>
public sealed record ObsVramFootprintReport(
    ObsVramStep? Encoder,
    ObsVramStep? RestOfStack,
    double TotalVramGb,
    TimeSpan TailWithoutObs,
    bool TailIsUsable,
    TimeSpan? StepSeparation = null,
    bool StepsTooClose = false,
    DateTimeOffset? ObsQuitAt = null)
{
    public double TotalMegabytesFreed =>
        (Encoder?.MegabytesFreed(TotalVramGb) ?? 0) + (RestOfStack?.MegabytesFreed(TotalVramGb) ?? 0);

    public string Message
    {
        get
        {
            var steps = new List<string>();

            if (Encoder is { } encoder)
            {
                steps.Add(
                    $"strömmen stoppades {encoder.At.ToLocalTime():HH:mm:ss} och VRAM föll "
                    + $"{encoder.PercentBefore:F1} → {encoder.PercentAfter:F1} % "
                    + $"({encoder.PercentagePoints:F1} pp ≈ {encoder.MegabytesFreed(TotalVramGb):F0} MB, encodern)");
            }

            if (RestOfStack is { } rest)
            {
                steps.Add(
                    $"OBS avslutades {rest.At.ToLocalTime():HH:mm:ss} och VRAM föll "
                    + $"{rest.PercentBefore:F1} → {rest.PercentAfter:F1} % "
                    + $"({rest.PercentagePoints:F1} pp ≈ {rest.MegabytesFreed(TotalVramGb):F0} MB, kanvas, "
                    + "game capture och webbkällor)");
            }

            var total = steps.Count > 1
                ? $" Streamstacken höll alltså ungefär {TotalMegabytesFreed:F0} MB av kortet."
                : $" Det steget är ungefär {TotalMegabytesFreed:F0} MB av kortet.";

            // Which half is missing, and why. On 10 September only the stream was stopped — OBS itself
            // kept running — and the line stated the encoder's 235 MB and stopped. Read cold a fortnight
            // later that is indistinguishable from a measurement that failed, when in fact it is a
            // measurement that was only given half its evidence.
            var missing = (Encoder, RestOfStack) switch
            {
                // Worded to hold both when written the moment the encoder step completes and at the end
                // of an evening where OBS kept running.
                (not null, null) when ObsQuitAt is { } quit => $" OBS avslutades {quit.ToLocalTime():HH:mm:ss}, "
                    + "men det steget är inte avläst: det behöver några sekunders mätning efter att processen "
                    + "stängts. Resten av stacken — kanvas, game capture och webbkällor — är omätt så länge.",
                (not null, null) => " OBS-processen har inte avslutats under mätningen, så resten av "
                    + "stacken — kanvas, game capture och webbkällor — är omätt än så länge. Den delen "
                    + "kräver att processen stängs medan mätningen fortfarande rullar.",
                (null, not null) => " Strömmen stoppades aldrig separat, så encoderns egen andel är omätt "
                    + "i kväll; siffran ovan är hela stacken i ett steg.",
                _ => string.Empty,
            };

            var caveat = TailIsUsable
                ? string.Empty
                : $" Perioden efter är {TailWithoutObs.TotalMinutes:F0} minuter, vilket är för kort för att "
                    + "jämföra frametider över — VRAM-stegen är sekundupplösta och står ändå.";

            // Both steps have a figure, but the encoder's release may not have finished before OBS began
            // its own, so part of one can sit in the other.
            var overlap = StepsTooClose && Encoder is not null && RestOfStack is not null
                ? $" VARNING: stegen låg {StepSeparation!.Value.TotalSeconds:F0} s isär, vilket är för tätt "
                    + "för att skilja encodern säkert från resten — summan gäller, men fördelningen mellan "
                    + "stegen ska inte jämföras med kvällar där rutinen (stoppa strömmen, vänta, avsluta OBS) "
                    + "hölls."
                : string.Empty;

            return $"OBS-avstängningen mätt: {string.Join("; ", steps)}.{total}{missing}{overlap}{caveat} "
                + "Stegen är lästa sekunderna runt varje övergång, inte som medianer över perioderna: spelet "
                + "fyller på i det lediga inom en halvminut.";
        }
    }
}
