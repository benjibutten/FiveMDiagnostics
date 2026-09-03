namespace FiveMDiagnostics.Integrations.Etw;

using Microsoft.Diagnostics.Tracing;

/// <summary>
/// Counts file system operations per process during the retained window of a trace.
/// </summary>
/// <remarks>
/// <para>
/// The app has always weighed disk pressure in megabytes per second, and megabytes are the wrong unit
/// for the thing that hurts. On 2 September the game lost a 2 145 ms frame while Windows Search moved
/// 49 MB in four seconds — twelve megabytes a second, well under any threshold the analysis had — and
/// made <em>192 788 file operations</em> doing it, 48 000 a second against its own index database on
/// the same volume as the game's cache. A volume of traffic that small and an operation rate that large
/// is exactly the shape a metadata-heavy workload has, and it is the operation rate that contends for
/// the file system, not the bytes.
/// </para>
/// <para>
/// Counted rather than measured in time. The trace has no per-operation duration that survives a ring
/// buffer honestly, and a count against the window the samples cover is enough to say who was doing the
/// work: this exists to name a process, not to model a queue.
/// </para>
/// </remarks>
internal sealed class FileOperationAttribution
{
    /// <summary>
    /// Processes reported. Three is enough for the sentence — the game, the worst neighbour, and one
    /// more so the reader can see whether the neighbour was alone.
    /// </summary>
    private const int ReportedProcesses = 3;

    /// <summary>
    /// Lowest kernel FileIO opcode that is an operation rather than a name-table entry.
    /// </summary>
    /// <remarks>
    /// The FileIO keywords emit two unrelated kinds of event. Below 64 are the name-table records —
    /// Name, FileCreate, FileDelete, FileRundown — which map a file object to a path and are emitted in
    /// bulk at rundown; they are not work anybody did. From 64 up are the operations themselves: Create,
    /// Cleanup, Close, Read, Write, SetInfo, Delete, Rename, DirEnum, Flush, QueryInfo, FSControl.
    /// </remarks>
    private const int FirstOperationOpcode = 64;

    /// <summary>
    /// The completion event, which mirrors every operation exactly once and must not be counted.
    /// </summary>
    /// <remarks>
    /// Measured on the trace this class was written for: 1 484 795 OperationEnd events against 1 484 806
    /// operations. Counting both put the reported rate at almost exactly twice the truth, which is the
    /// kind of error that survives review because the number still looks plausible.
    /// </remarks>
    private const int OperationEndOpcode = 76;

    /// <summary>
    /// Quiet seconds a burst may contain before it is reported as two.
    /// </summary>
    /// <remarks>
    /// An indexer works in waves and drops below the bar for a second at a time; splitting on every one
    /// of those would turn one three-minute burst into forty intervals, all of which then have to be
    /// carried in a metrics dictionary keyed by index. Two seconds is short enough that two genuinely
    /// separate bursts are still two intervals.
    /// </remarks>
    private const long ContendingGapSeconds = 2;

    /// <summary>
    /// Contending stretches reported, busiest first. The consumer needs enough of them to find one that
    /// overlaps a ninety second incident window, not a full timeline.
    /// </summary>
    private const int ReportedIntervals = 8;

    private readonly Dictionary<int, long> _operationsByProcess = [];

    /// <summary>
    /// Operations per process per whole second, counted from the first one seen.
    /// </summary>
    /// <remarks>
    /// The reason the counts are bucketed at all is that a total over a trace is not evidence about an
    /// incident inside it. A ring buffer holds tens of seconds and an incident window is ninety, so the
    /// two overlap constantly without the operations having happened while the frames were being lost —
    /// and 48 000 operations a second is worth three tenths of a disk verdict. A per-second count is the
    /// coarsest thing that can answer "was it happening then", and it is exactly the unit the threshold
    /// is already stated in.
    /// </remarks>
    private readonly Dictionary<int, Dictionary<long, int>> _secondsByProcess = [];

    private DateTime? _first;
    private DateTime? _last;

    public long TotalOperations { get; private set; }

    /// <summary>Wall clock the counted operations span, which the rates below are taken over.</summary>
    public double CoveredSeconds => _first is { } first && _last is { } last
        ? Math.Max((last - first).TotalSeconds, 0)
        : 0;

    /// <summary>Records one file system operation, ignoring everything that is not one.</summary>
    /// <remarks>
    /// Called from the raw event stream rather than a typed parser callback: the FileIO keywords emit a
    /// dozen distinct operation opcodes and this class wants all of them, which is one comparison here
    /// against twelve separate subscriptions. Filtering by opcode rather than by name because
    /// TraceEvent leaves most of these undecoded — they arrive as "FileIO/Opcode(67)" — so the name is
    /// not something to match on.
    /// </remarks>
    public void OnEvent(TraceEvent traceEvent)
    {
        var opcode = (int)traceEvent.Opcode;
        if (opcode < FirstOperationOpcode || opcode == OperationEndOpcode)
        {
            return;
        }

        Record(traceEvent.ProcessID, traceEvent.TimeStamp);
    }

    /// <summary>
    /// Counts one operation against a process and a moment.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OnEvent"/> so the bucketing and the intervals it feeds can be tested. A
    /// <c>TraceEvent</c> cannot be constructed outside the library that decodes one, so everything below
    /// the opcode filter was previously reachable only by parsing an ETL — which is why an average over
    /// a whole trace passed for a measurement of an incident inside it for as long as it did.
    /// </remarks>
    internal void Record(int processId, DateTime timestamp)
    {
        if (processId <= 0)
        {
            return;
        }

        TotalOperations++;
        _first ??= timestamp;
        _last = timestamp;

        _operationsByProcess[processId] = _operationsByProcess.GetValueOrDefault(processId) + 1;

        var second = Math.Max(0, (timestamp - _first.Value).Ticks / TimeSpan.TicksPerSecond);
        if (!_secondsByProcess.TryGetValue(processId, out var seconds))
        {
            seconds = [];
            _secondsByProcess[processId] = seconds;
        }

        seconds[second] = seconds.GetValueOrDefault(second) + 1;
    }

    /// <summary>
    /// The busiest processes and what they did, or null when the trace held no file system events.
    /// </summary>
    /// <param name="isGameProcess">
    /// Tells the game's own rows from a neighbour's, so the summary can put the two side by side. The
    /// game makes thousands of operations a second in normal running — the anti-cheat alone reads one
    /// file 1 200 times a second — so its own rate is the baseline a neighbour has to be read against.
    /// </param>
    /// <param name="nameOf">
    /// Resolves a process id to its image name. The kernel's file events carry only the id, and the
    /// image names are in the process rundown that <see cref="CpuSampleAttribution"/> already reads.
    /// </param>
    public FileOperationSummary? Summarize(Func<int, bool> isGameProcess, Func<int, string> nameOf)
    {
        if (TotalOperations == 0 || CoveredSeconds <= 0)
        {
            return null;
        }

        var byProcess = _operationsByProcess
            .Select(entry => new FileOperationProcess(
                nameOf(entry.Key),
                entry.Key,
                entry.Value,
                entry.Value / CoveredSeconds,
                isGameProcess(entry.Key)))
            .OrderByDescending(item => item.Operations)
            .ToArray();

        var neighbour = byProcess.Where(item => !item.IsGame).Take(1).FirstOrDefault();

        return new FileOperationSummary(
            TotalOperations,
            CoveredSeconds,
            byProcess.Take(ReportedProcesses).ToArray(),
            neighbour,
            neighbour is null ? [] : ContendingIntervals(neighbour.ProcessId));
    }

    /// <summary>
    /// The stretches during which one process was over the contention bar, busiest first.
    /// </summary>
    /// <remarks>
    /// Every second in an interval is itself over the bar, so a consumer needs no threshold of its own:
    /// an interval that overlaps a window is contention that happened during that window, and one that
    /// does not is contention that happened at some other time in the same file.
    /// </remarks>
    private IReadOnlyList<FileOperationInterval> ContendingIntervals(int processId)
    {
        if (_first is not { } first || !_secondsByProcess.TryGetValue(processId, out var seconds))
        {
            return [];
        }

        var contending = seconds
            .Where(entry => entry.Value >= FileOperationSummary.ContendingOperationsPerSecond)
            .OrderBy(entry => entry.Key)
            .ToArray();

        if (contending.Length == 0)
        {
            return [];
        }

        var intervals = new List<FileOperationInterval>();
        var runStart = contending[0].Key;
        var runEnd = contending[0].Key;
        var peak = contending[0].Value;

        foreach (var entry in contending.Skip(1))
        {
            if (entry.Key - runEnd <= ContendingGapSeconds)
            {
                runEnd = entry.Key;
                peak = Math.Max(peak, entry.Value);
                continue;
            }

            intervals.Add(Interval(first, runStart, runEnd, peak));
            runStart = entry.Key;
            runEnd = entry.Key;
            peak = entry.Value;
        }

        intervals.Add(Interval(first, runStart, runEnd, peak));

        return intervals
            .OrderByDescending(interval => interval.PeakOperationsPerSecond)
            .Take(ReportedIntervals)
            .ToArray();
    }

    /// <summary>
    /// One run of contending seconds, ending at the far edge of the last of them rather than at its
    /// start — a burst that occupied second 40 lasted until 41, and a window that begins at 40.5 saw it.
    /// </summary>
    private static FileOperationInterval Interval(DateTime first, long startSecond, long endSecond, int peak) =>
        new(first.AddSeconds(startSecond), first.AddSeconds(endSecond + 1), peak);
}

/// <summary>A stretch of a trace during which one process was over the contention bar.</summary>
/// <param name="PeakOperationsPerSecond">The busiest single second in it.</param>
internal sealed record FileOperationInterval(DateTime Start, DateTime End, double PeakOperationsPerSecond);

/// <summary>One process's share of the file system traffic in a trace.</summary>
internal sealed record FileOperationProcess(
    string ProcessName,
    int ProcessId,
    long Operations,
    double OperationsPerSecond,
    bool IsGame);

/// <summary>What the trace saw the file system doing, and who was doing it.</summary>
/// <param name="BusiestNeighbour">
/// The heaviest process that is not the game, which is the only row anybody can act on.
/// </param>
/// <param name="NeighbourContendingIntervals">
/// When that neighbour was actually over the bar, so a reader of this trace can tell whether it was
/// doing it during the seconds under examination or during some other part of the same file.
/// </param>
internal sealed record FileOperationSummary(
    long TotalOperations,
    double CoveredSeconds,
    IReadOnlyList<FileOperationProcess> TopProcesses,
    FileOperationProcess? BusiestNeighbour,
    IReadOnlyList<FileOperationInterval> NeighbourContendingIntervals)
{
    /// <summary>
    /// Operation rate at which a neighbour is contending for the file system rather than using it.
    /// </summary>
    /// <remarks>
    /// The game itself runs at 2 000–7 000 operations a second all evening without anything going wrong,
    /// so the bar for a neighbour has to be well above that to mean anything. Windows Search reached
    /// 48 000 during the frame it cost; ten thousand sits between the two with room on both sides.
    /// </remarks>
    public const double ContendingOperationsPerSecond = 10_000;

    /// <summary>True when a process other than the game was hammering the file system.</summary>
    public bool HasContendingNeighbour =>
        BusiestNeighbour is { OperationsPerSecond: >= ContendingOperationsPerSecond };

    public string Describe()
    {
        var top = string.Join(
            ", ",
            TopProcesses.Select(item => $"{item.ProcessName} {item.Operations:N0} ({item.OperationsPerSecond:N0}/s)"));

        var verdict = HasContendingNeighbour
            ? $" {BusiestNeighbour!.ProcessName} är inte spelet och låg på "
                + $"{BusiestNeighbour.OperationsPerSecond:N0} operationer i sekunden — det är filsystemsträngsel, "
                + "och den syns inte i MB/s eftersom sådan trafik är liten i byte och stor i antal."
            : string.Empty;

        return $"Filsystem: {TotalOperations:N0} operationer på {CoveredSeconds:F1} s "
            + $"({TotalOperations / CoveredSeconds:N0}/s). Mest: {top}.{verdict}";
    }
}
