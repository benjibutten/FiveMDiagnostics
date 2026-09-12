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

    /// <summary>Which instance of that process the cores belonged to.</summary>
    /// <remarks>
    /// Absent from traces written before 11 September and from hand-imported ETLs, which is why a
    /// missing pid continues the run it arrives in rather than starting a new one: an old trace must not
    /// split a series it knows nothing about.
    /// </remarks>
    private const string ProcessPidPrefix = "cpuProcessPid_";

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
    private readonly Dictionary<string, List<Sample>> _byProcess = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Sample(DateTimeOffset At, double Cores, int? ProcessId);

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

                var processId = metrics.TryGetValue(ProcessPidPrefix + name, out var pid) && pid > 0
                    ? (int)pid
                    : (int?)null;

                series.Add(new Sample(at, cores, processId));
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
                .SelectMany(entry => StepsWithinInstances(entry.Key, entry.Value))
                .OrderByDescending(report => report.After - report.Before)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Steps found inside one process instance, never across two.
    /// </summary>
    /// <remarks>
    /// A restart resets everything the comparison assumes. On 10 September the game crashed at 00:09 and
    /// relaunched, taking <c>FiveM_ChromeBrowser</c> with it; the monitor saw 0.34 cores in the first
    /// trace and 0.88–1.37 in the three after, called it a step that "never came down again", and added
    /// that the machine was not the one the earlier half had run on. Two of those three traces were a
    /// different process, and one of them was the new instance loading the game. Splitting on the pid
    /// leaves two runs of two traces, neither long enough to claim anything — which is the honest answer
    /// for that evening.
    /// <para>
    /// Every run is offered rather than only the last: a step inside an earlier instance is as real as
    /// one inside the current instance, and <see cref="Summary"/> picks the largest.
    /// </para>
    /// </remarks>
    private static IEnumerable<NeighbourCpuTrendReport> StepsWithinInstances(string process, List<Sample> series)
    {
        var restartsSeen = 0;
        foreach (var run in SplitRuns(series))
        {
            if (run.Count >= MinimumTraces && FindStep(process, run, restartsSeen) is { } step)
            {
                yield return step;
            }

            restartsSeen++;
        }
    }

    /// <summary>
    /// Splits one process's samples into runs on the same process id, oldest first. Shared by the step
    /// search and <see cref="DescribeNoStep"/>, which both need to know where a restart broke the series
    /// without disagreeing on where.
    /// </summary>
    private static IEnumerable<List<Sample>> SplitRuns(List<Sample> series)
    {
        var run = new List<Sample>();

        foreach (var sample in series.OrderBy(item => item.At))
        {
            var known = run.LastOrDefault(item => item.ProcessId is not null).ProcessId;
            if (sample.ProcessId is { } pid && known is { } previous && pid != previous)
            {
                yield return run;
                run = [];
            }

            run.Add(sample);
        }

        yield return run;
    }

    /// <summary>
    /// Why <see cref="Summary"/> found no step, so a session that had nothing to say can be told apart
    /// from one where the monitor never got a real look. Meaningful only when <see cref="Summary"/>
    /// returns null.
    /// </summary>
    /// <remarks>
    /// A session that fills six deep captures across two process instances of the same neighbour never
    /// reaches <see cref="MinimumTraces"/> in either run, and silence there reads exactly like the silence
    /// of an evening with nothing going on. The two need different sentences.
    /// </remarks>
    public string? DescribeNoStep()
    {
        lock (_sync)
        {
            if (_byProcess.Count == 0)
            {
                return "Ingen grannprocess hade CPU-kärnor med i någon trace den här sessionen; regeln hade inget att jämföra.";
            }

            var byProcess = _byProcess
                .Select(entry => (entry.Key, RunLengths: SplitRuns(entry.Value).Select(run => run.Count).Where(length => length > 0).ToArray()))
                .ToArray();

            var eligible = byProcess.SelectMany(entry => entry.RunLengths).Where(length => length >= MinimumTraces).ToArray();
            if (eligible.Length == 0)
            {
                var longestProcess = byProcess.OrderByDescending(entry => entry.RunLengths.DefaultIfEmpty(0).Max()).First();
                var longest = longestProcess.RunLengths.DefaultIfEmpty(0).Max();
                return $"Ingen processinstans nådde de {MinimumTraces} spår regeln kräver för att jämföra en "
                    + $"nivå före och efter (längst kom {longestProcess.Key} med {longest}).";
            }

            var runsText = string.Join(" och ", eligible
                .GroupBy(length => length)
                .OrderByDescending(group => group.Key)
                .Select(group => group.Count() == 1
                    ? $"en körning om {group.Key} spår"
                    : $"{group.Count()} körningar om {group.Key} spår"));

            return $"{runsText} hade underlag nog att jämföras; inget steg inom någon av dem.";
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
    private static NeighbourCpuTrendReport? FindStep(string process, List<Sample> series, int restartsSeen)
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
                    SteppedBetween: (before[^1].At, after[0].At),
                    RestartsSeen: restartsSeen);
            }
        }

        return null;
    }
}

/// <param name="Before">The highest the process reached in any trace before the step.</param>
/// <param name="After">The lowest it held in any trace after it.</param>
/// <param name="SteppedBetween">The two captures the step happened between; it cannot be placed closer.</param>
/// <param name="RestartsSeen">
/// How many times the process restarted during the session. The step is always read inside one instance;
/// this only warns the reader that the series had breaks in it, so the trace counts are not the whole
/// evening.
/// </param>
public sealed record NeighbourCpuTrendReport(
    string ProcessName,
    double Before,
    double After,
    double AfterPeak,
    int TracesBefore,
    int TracesAfter,
    (DateTimeOffset From, DateTimeOffset To) SteppedBetween,
    int RestartsSeen = 0)
{
    public string Message =>
        $"{ProcessName} steg från högst {Before:F2} kärnor i {FirstTraces} {TracesBefore} traces till "
        + $"{After:F2}–{AfterPeak:F2} i de {TracesAfter} följande, och gick aldrig ner igen. Steget ligger "
        + $"mellan {SteppedBetween.From.ToLocalTime():HH:mm:ss} och {SteppedBetween.To.ToLocalTime():HH:mm:ss}. "
        + Restarts
        + "Det säger inte att processen orsakade något — läs väntkedjorna för det — men maskinen den senare "
        + "delen av sessionen kördes på är inte den som den tidigare delen kördes på, och kvällarna är inte "
        + "jämförbara över den gränsen.";

    private string FirstTraces => RestartsSeen == 0 ? "sessionens första" : "instansens första";

    /// <summary>Names the breaks in the series, so the trace counts are not read as the whole evening.</summary>
    private string Restarts => RestartsSeen switch
    {
        0 => string.Empty,
        1 => "Processen startades om en gång under sessionen; steget är läst inom en och samma instans, "
            + "inte över omstarten. ",
        _ => $"Processen startades om {RestartsSeen} gånger under sessionen; steget är läst inom en och "
            + "samma instans, inte över någon omstart. ",
    };
}
