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
    /// quit — was followed. <see cref="Before"/> and <see cref="After"/> put roughly eighteen seconds of
    /// reading around each transition; closer than that and the "before" window of the second step is
    /// already inside the "after" window of the first, so each step's drop partly counts the other one's.
    /// Twenty seconds clears both windows with margin.
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
    private static readonly TimeSpan History = Before + Settle + After + TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly List<(DateTimeOffset At, double Percent)> _readings = [];

    private double _totalVramGb;
    private bool? _streaming;
    private bool? _processRunning;
    private DateTimeOffset? _streamStoppedAt;
    private DateTimeOffset? _processStoppedAt;
    private DateTimeOffset? _lastReadingAt;
    private ObsVramStep? _encoderStep;
    private ObsVramStep? _restOfStackStep;

    /// <summary>Folds one adapter reading in.</summary>
    public void Observe(GpuTelemetrySample sample)
    {
        if (!sample.IsAvailable || sample.VramUsagePercent is not { } percent)
        {
            return;
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
            _encoderStep ??= CompletedStep(_streamStoppedAt, sample.Timestamp);
            _restOfStackStep ??= CompletedStep(_processStoppedAt, sample.Timestamp);

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

    /// <summary>The steps, or null when the stack never came off during the session.</summary>
    public ObsVramFootprintReport? Summary()
    {
        lock (_sync)
        {
            if (_encoderStep is null && _restOfStackStep is null)
            {
                return null;
            }

            var lastTransition = _processStoppedAt ?? _streamStoppedAt;
            var tail = _lastReadingAt is { } last && lastTransition is { } at ? last - at : TimeSpan.Zero;

            var separation = _streamStoppedAt is { } stream && _processStoppedAt is { } process
                ? (process - stream).Duration()
                : (TimeSpan?)null;
            var stepsTooClose = separation is { } gap && gap < MinimumStepSeparation;

            return new ObsVramFootprintReport(
                _encoderStep, _restOfStackStep, _totalVramGb, tail, tail >= UsableComparison, separation, stepsTooClose);
        }
    }

    /// <summary>
    /// The drop across one transition, once every reading that describes it has arrived. Null until then,
    /// and null when either side of it was never sampled.
    /// </summary>
    private ObsVramStep? CompletedStep(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } moment || now < moment + Settle + After)
        {
            return null;
        }

        var before = Median(moment - Before, moment);
        var after = Median(moment + Settle, moment + Settle + After);

        return before is { } from && after is { } to ? new ObsVramStep(moment, from, to) : null;
    }

    private double? Median(DateTimeOffset from, DateTimeOffset to)
    {
        var window = _readings
            .Where(item => item.At >= from && item.At <= to)
            .Select(item => item.Percent)
            .OrderBy(percent => percent)
            .ToArray();

        return window.Length == 0 ? null : window[window.Length / 2];
    }
}

/// <param name="PercentBefore">Median occupancy in the seconds before the transition.</param>
/// <param name="PercentAfter">Median occupancy in the seconds after it, before the game refills.</param>
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
/// Whether that separation is short enough that the two steps' reading windows overlap, which makes
/// each step's own figure unreliable even though both were measured.
/// </param>
public sealed record ObsVramFootprintReport(
    ObsVramStep? Encoder,
    ObsVramStep? RestOfStack,
    double TotalVramGb,
    TimeSpan TailWithoutObs,
    bool TailIsUsable,
    TimeSpan? StepSeparation = null,
    bool StepsTooClose = false)
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
                (not null, null) => " OBS-processen avslutades aldrig under sessionen, så resten av "
                    + "stacken — kanvas, game capture och webbkällor — är omätt i kväll. Den delen "
                    + "kräver att processen stängs medan mätningen fortfarande rullar.",
                (null, not null) => " Strömmen stoppades aldrig separat, så encoderns egen andel är omätt "
                    + "i kväll; siffran ovan är hela stacken i ett steg.",
                _ => string.Empty,
            };

            var caveat = TailIsUsable
                ? string.Empty
                : $" Perioden efter är {TailWithoutObs.TotalMinutes:F0} minuter, vilket är för kort för att "
                    + "jämföra frametider över — VRAM-stegen är sekundupplösta och står ändå.";

            // Overlapping windows, not a failed measurement: both steps have a figure, but each one's
            // "efter"-läsning may already include part of the other transition's drop.
            var overlap = StepsTooClose && Encoder is not null && RestOfStack is not null
                ? $" VARNING: stegen låg {StepSeparation!.Value.TotalSeconds:F0} s isär, vilket är för tätt "
                    + "för att skilja encodern från resten — siffrorna ovan överlappar och ska inte jämföras "
                    + "med kvällar där rutinen (stoppa strömmen, vänta, avsluta OBS) hölls."
                : string.Empty;

            return $"OBS-avstängningen mätt: {string.Join("; ", steps)}.{total}{missing}{overlap}{caveat} "
                + "Stegen är lästa sekunderna runt varje övergång, inte som medianer över perioderna: spelet "
                + "fyller på i det lediga inom en halvminut.";
        }
    }
}
