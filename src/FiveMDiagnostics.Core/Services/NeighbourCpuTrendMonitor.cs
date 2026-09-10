namespace FiveMDiagnostics.Core;

/// <summary>
/// Watches a neighbouring process's CPU across a session's traces, and reports one that steps up and
/// stays there.
/// </summary>
/// <remarks>
/// <para>
/// The traces are the only place per-process CPU is measured on a scale that means anything: the
/// counters sample once a second and the loads that matter last tenths. Each capture already reports
/// what every neighbour held, and the app already prints those figures — one trace at a time, where a
/// level says nothing.
/// </para>
/// <para>
/// Across a session they say a great deal. On 9 September <c>FiveM_ChromeBrowser</c> — the process that
/// renders the server's NUI layers — held 0.27 and 0.41 cores in the evening's first two traces and
/// 1.50, 1.34, 1.18, 1.17 and 1.14 in the five after it, with the step somewhere between 23:45 and
/// 00:13 and no return. The evening's hitch rate went from 184 to 229 an hour across the same boundary.
/// Every figure was in the session log; the step was found by reading seven trace summaries side by side
/// a day later.
/// </para>
/// <para>
/// It reports a step, not a cause. Nothing here says the process caused anything — the wait chains of
/// that evening end inside the game, not in this process — only that something on the machine changed
/// in the middle of the session and stayed changed, which is a fact worth having before an evening is
/// compared with another.
/// </para>
/// </remarks>
public sealed class NeighbourCpuTrendMonitor
{
    /// <summary>How a trace reports what each neighbouring process held.</summary>
    private const string ProcessCoresPrefix = "cpuProcessCores_";

    /// <summary>
    /// Traces a process needs before its levels are compared at all.
    /// </summary>
    /// <remarks>
    /// Four is one on each side of a step plus one more on each, which is the least that can tell a step
    /// from two readings that happened to differ. A session takes six or seven captures, so this is
    /// reachable without being satisfied by noise.
    /// </remarks>
    private const int MinimumTraces = 4;

    /// <summary>
    /// How much higher the later level has to be, as a multiple and in cores both.
    /// </summary>
    /// <remarks>
    /// Between the quiet traces of 8 and 9 September the same processes varied by 0.1–0.15 cores from
    /// capture to capture — different places in the world, different amounts of work, the ordinary spread.
    /// The step this exists for was 3× and 0.8 cores. Requiring both a ratio and an absolute rise keeps a
    /// process that idles at 0.02 and blips to 0.06 out of it, and keeps the spread above out too.
    /// </remarks>
    private const double StepRatio = 2.0;

    private const double StepCores = 0.5;

    private readonly object _sync = new();
    private readonly Dictionary<string, List<(DateTimeOffset At, double Cores)>> _byProcess = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The window a trace describes, which is not when the app finished reading it.</summary>
    private const string CoveredStartKey = "traceCoveredStartUnixMs";

    /// <summary>Folds one trace's per-process CPU in.</summary>
    /// <param name="at">
    /// When the trace was parsed, used only when the file does not say what window it covers. The step
    /// is placed in the evening by these timestamps and the order they impose is the whole finding, so
    /// the trace's own window comes first: several ETLs imported by hand arrive in whatever order the
    /// files were picked, minutes apart, and ordering those by parse time invents a step out of an
    /// evening that had none.
    /// </param>
    public void Observe(DateTimeOffset at, IReadOnlyDictionary<string, double> metrics)
    {
        if (metrics.TryGetValue(CoveredStartKey, out var coveredStart))
        {
            at = DateTimeOffset.FromUnixTimeMilliseconds((long)coveredStart);
        }

        lock (_sync)
        {
            foreach (var (key, cores) in metrics)
            {
                if (!key.StartsWith(ProcessCoresPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = key[ProcessCoresPrefix.Length..];
                if (!_byProcess.TryGetValue(name, out var series))
                {
                    series = [];
                    _byProcess[name] = series;
                }

                series.Add((at, cores));
            }
        }
    }

    /// <summary>
    /// The clearest step across the session, or null when no process took one.
    /// </summary>
    public NeighbourCpuTrendReport? Summary()
    {
        lock (_sync)
        {
            return _byProcess
                .Where(entry => entry.Value.Count >= MinimumTraces)
                .Select(entry => FindStep(entry.Key, entry.Value))
                .OfType<NeighbourCpuTrendReport>()
                .OrderByDescending(report => report.After - report.Before)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// The longest run of trailing traces that all sit above everything before them.
    /// </summary>
    /// <remarks>
    /// Longest rather than first: a step held for five traces is the finding, and taking the first split
    /// that qualifies would report it as a step of one. Trailing rather than anywhere, because a level
    /// that went up and came back down again is not what this is for — the question it answers is whether
    /// the machine the second half of the session ran on was the same one the first half did.
    /// </remarks>
    private static NeighbourCpuTrendReport? FindStep(string process, List<(DateTimeOffset At, double Cores)> series)
    {
        var ordered = series.OrderBy(item => item.At).ToArray();

        for (var tail = ordered.Length - 1; tail >= 2; tail--)
        {
            var before = ordered[..^tail];
            var after = ordered[^tail..];
            if (before.Length == 0)
            {
                continue;
            }

            var ceiling = before.Max(item => item.Cores);
            var floor = after.Min(item => item.Cores);

            if (floor >= ceiling * StepRatio && floor - ceiling >= StepCores)
            {
                return new NeighbourCpuTrendReport(
                    process,
                    Before: ceiling,
                    After: floor,
                    AfterPeak: after.Max(item => item.Cores),
                    TracesBefore: before.Length,
                    TracesAfter: after.Length,
                    SteppedBetween: (before[^1].At, after[0].At));
            }
        }

        return null;
    }
}

/// <param name="Before">The highest the process reached in any trace before the step.</param>
/// <param name="After">The lowest it held in any trace after it.</param>
/// <param name="SteppedBetween">The two captures the step happened between; it cannot be placed closer.</param>
public sealed record NeighbourCpuTrendReport(
    string ProcessName,
    double Before,
    double After,
    double AfterPeak,
    int TracesBefore,
    int TracesAfter,
    (DateTimeOffset From, DateTimeOffset To) SteppedBetween)
{
    public string Message =>
        $"{ProcessName} steg från högst {Before:F2} kärnor i sessionens första {TracesBefore} traces till "
        + $"{After:F2}–{AfterPeak:F2} i de {TracesAfter} följande, och gick aldrig ner igen. Steget ligger "
        + $"mellan {SteppedBetween.From.ToLocalTime():HH:mm:ss} och {SteppedBetween.To.ToLocalTime():HH:mm:ss}. "
        + "Det säger inte att processen orsakade något — läs väntkedjorna för det — men maskinen den senare "
        + "delen av sessionen kördes på är inte den som den tidigare delen kördes på, och kvällarna är inte "
        + "jämförbara över den gränsen.";
}
