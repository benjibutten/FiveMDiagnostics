using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// Reconstructs off-CPU intervals from context switches. PresentMon's CPU-busy column covers the whole
/// frame-side delay; this class separates time actually executing from time the game thread slept.
/// </summary>
/// <remarks>
/// It also follows the chain. "The main thread was off the processor for 355.7 ms" is where the app
/// stopped for three sessions, and it is one question short of an answer: a thread that is asleep is
/// waiting for something, and the useful sentence names the thread at the far end of that. Across those
/// sessions the thread that released the game's main thread was a near-idle synchronisation thread
/// inside <c>gta-core-five.dll</c> which was itself waiting on the render thread — so naming only the
/// first link reports a thread doing nothing as the cause of the stall. The walk ends at the first
/// thread with no wait of its own covering the interval: that thread was on the processor while
/// everything behind it was not, and it is the one worth naming.
/// </remarks>
internal sealed class ThreadWaitAttribution
{
    private const double LongWaitThresholdMs = 100;
    private const int MaxLongWaits = 100_000;

    /// <summary>
    /// Links the chain may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// A cycle means the attribution is wrong rather than that a deadlock is real, and a trace whose
    /// context switch stream wrapped mid-stall can produce one. Eight is far past any chain the captures
    /// have shown — the longest measured is three.
    /// </remarks>
    private const int MaxChainDepth = 8;

    /// <summary>
    /// Share of a wait a candidate has to be off the processor for before it can explain it.
    /// </summary>
    /// <remarks>
    /// The threads in a real chain go off the processor within a millisecond of each other and come back
    /// together, so half is generous. What it excludes is an unrelated thread that happened to park
    /// nearby, which is the whole risk of walking a chain from timestamps.
    /// </remarks>
    private const double ChainOverlapShare = 0.5;

    private readonly Dictionary<int, SwitchOut> _switchOutByThread = [];
    private readonly List<ThreadWait> _longWaits = [];

    /// <summary>
    /// The ReadyThread event that has made a thread runnable but whose switch-in has not arrived yet.
    /// </summary>
    /// <remarks>
    /// Parked per readied thread and claimed by the next switch-in of that thread, because the event
    /// fires before the switch that ends the wait. Consumed on every switch-in rather than only the ones
    /// long enough to report: a record left behind by a short wait would outlive it and be claimed by the
    /// next wait on that thread — a timer expiry with no ReadyThread event of its own — naming a waker
    /// that had nothing to do with it.
    /// </remarks>
    private readonly Dictionary<int, Ready> _readyByThread = [];

    /// <summary>
    /// Which thread the context switch stream last put on each processor.
    /// </summary>
    /// <remarks>
    /// The only handle on who did the readying without a second pass over the trace. The classic kernel
    /// ReadyThread event carries the thread it woke and leaves the header's own thread id at -1; what it
    /// does carry is the processor it fired on, and this says which thread was running there. That holds
    /// for an ordinary user mode wake and is what a trace viewer shows — it is still an inference, and
    /// every sentence built on it says so.
    /// </remarks>
    private readonly Dictionary<int, int> _runningByProcessor = [];

    /// <summary>
    /// Notes that a thread was made runnable, and by whom as far as the trace can say.
    /// </summary>
    /// <remarks>
    /// A wake from inside a DPC names no thread at all: the thread on that processor is merely the one
    /// the interrupt suspended, so claiming it would attribute the wake to a thread that had nothing to
    /// do with it — and the chain would then keep walking from there.
    /// </remarks>
    public void OnReadyThread(DispatcherReadyThreadTraceData data)
    {
        RecordReady(
            data.AwakenedThreadID,
            data.ProcessorNumber,
            data.Flags.HasFlag(DispatcherReadyThreadTraceData.ReadyThreadFlags.ReadiedFromDPC));
    }

    public void OnContextSwitch(CSwitchTraceData data)
    {
        RecordSwitch(
            data.ProcessorNumber,
            data.TimeStamp,
            data.NewThreadID,
            data.OldThreadID,
            data.OldProcessID,
            data.OldThreadState.ToString(),
            data.OldThreadWaitReason.ToString());
    }

    /// <summary>
    /// The body of <see cref="OnReadyThread"/>, separated from the event type so the chain can be tested.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <c>FileOperationAttribution.Record</c>: a <c>TraceEvent</c> cannot be
    /// constructed outside the library that decodes one, so everything below these two entry points was
    /// otherwise reachable only by parsing a real ETL. The release chain is the most delicate reasoning
    /// in this file and it must not be the least covered.
    /// </remarks>
    internal void RecordReady(int awakenedThreadId, int processorNumber, bool fromDeferredProcedureCall)
    {
        if (awakenedThreadId < 0)
        {
            return;
        }

        _readyByThread[awakenedThreadId] = new Ready(
            fromDeferredProcedureCall ? -1 : _runningByProcessor.GetValueOrDefault(processorNumber, -1),
            processorNumber,
            fromDeferredProcedureCall);
    }

    /// <summary>The body of <see cref="OnContextSwitch"/>, separated for the same reason.</summary>
    internal void RecordSwitch(
        int processorNumber,
        DateTime timestamp,
        int newThreadId,
        int oldThreadId,
        int oldProcessId,
        string oldThreadState,
        string oldThreadWaitReason)
    {
        _runningByProcessor[processorNumber] = newThreadId;

        if (newThreadId >= 0)
        {
            _readyByThread.Remove(newThreadId, out var ready);

            if (_switchOutByThread.Remove(newThreadId, out var previous))
            {
                var durationMs = (timestamp - previous.Timestamp).TotalMilliseconds;
                if (durationMs >= LongWaitThresholdMs
                    && IsWaiting(previous.State)
                    && _longWaits.Count < MaxLongWaits)
                {
                    _longWaits.Add(new ThreadWait(
                        previous.ProcessId,
                        newThreadId,
                        previous.Timestamp,
                        timestamp,
                        durationMs,
                        previous.State,
                        previous.Reason,
                        ready));
                }
            }
        }

        if (oldThreadId >= 0)
        {
            _switchOutByThread[oldThreadId] = new SwitchOut(timestamp, oldProcessId, oldThreadState, oldThreadWaitReason);
        }
    }

    /// <summary>
    /// The release chain behind a given thread's longest wait, for tests and for
    /// <see cref="Summarize"/>.
    /// </summary>
    internal IReadOnlyList<ThreadWaitChainLink> ChainFor(int threadId, Func<int, string> processNameOf)
    {
        var anchor = _longWaits
            .Where(wait => wait.ThreadId == threadId)
            .OrderByDescending(wait => wait.DurationMs)
            .FirstOrDefault();

        return WalkChain(anchor, processNameOf);
    }

    public ThreadWaitSummary? Summarize(CpuSampleAttribution cpu)
    {
        if (cpu.FirstSampleTimestamp is not { } windowStart || cpu.LastSampleTimestamp is not { } windowEnd)
        {
            return null;
        }

        // A small tolerance admits the switch bracketing the first/last sample, but rejects a dormant
        // worker that slept for minutes and merely happened to wake inside the retained ring window.
        var tolerance = TimeSpan.FromMilliseconds(250);
        var candidates = _longWaits
            .Where(wait => wait.Start >= windowStart - tolerance
                && wait.End <= windowEnd + tolerance
                && cpu.IsGameThread(wait.ThreadId))
            .GroupBy(wait => wait.ThreadId)
            .Select(group => new
            {
                ProcessId = cpu.ProcessIdForThread(group.Key),
                ThreadId = group.Key,
                Waits = group.ToArray(),
                Samples = cpu.SampleCountForThread(group.Key),
                GameExecutableShare = cpu.GameExecutableSampleShareForThread(group.Key, cpu.ProcessIdForThread(group.Key)),
            })
            .Where(group => group.Samples >= 5 && group.GameExecutableShare >= 0.2)
            // Pick the thread doing the most actual GTA-executable work, not the helper that accumulated
            // the most sleep. In the field traces the main frame thread has thousands of such samples;
            // a background worker can sleep longer but only wakes for a handful.
            .OrderByDescending(group => group.Samples * group.GameExecutableShare)
            .ThenByDescending(group => group.Waits.Max(wait => wait.DurationMs))
            .FirstOrDefault();

        if (candidates is null)
        {
            return null;
        }

        var selectedWaits = candidates.Waits
            .OrderByDescending(wait => wait.DurationMs)
            .Take(64)
            .ToArray();
        var userRequestCount = selectedWaits.Count(wait =>
            wait.State.Contains("Wait", StringComparison.OrdinalIgnoreCase)
            && wait.Reason.Contains("UserRequest", StringComparison.OrdinalIgnoreCase));

        var reasons = selectedWaits
            .GroupBy(wait => $"{wait.State}/{wait.Reason}")
            .OrderByDescending(group => group.Count())
            .Take(3)
            .Select(group => $"{group.Key} ×{group.Count()}")
            .ToArray();

        var chain = WalkChain(selectedWaits.FirstOrDefault(), processId => cpu.Name(cpu.ProcessIdForThread(processId)));

        // What the thread at the end of the chain was executing. Without it the sentence names a thread
        // id and a duration, and the reader has to run etlanalyzer by hand to learn that the id belongs
        // to the render thread and that it was sitting in Direct3D — which is the whole finding.
        var blocker = chain.LastOrDefault(link => link is { EndsChain: true, FromDpc: false });
        var blockerModules = blocker is null
            ? []
            : cpu.ModulesForThread(blocker.ThreadId, take: 4)
                .Select(module => $"{module.Share:P0} {ModuleGlossary.Annotate(module.Module)}")
                .ToArray();

        return new ThreadWaitSummary(
            candidates.ThreadId,
            selectedWaits
                .Select(wait => new ThreadWaitInterval(
                    new DateTimeOffset(wait.Start).ToUnixTimeMilliseconds(),
                    new DateTimeOffset(wait.End).ToUnixTimeMilliseconds(),
                    wait.DurationMs,
                    wait.Reason.Contains("UserRequest", StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            userRequestCount,
            candidates.Samples,
            reasons,
            chain,
            blockerModules);
    }

    /// <summary>
    /// Follows a wait to the thread that released it, then to whatever released that, and so on until a
    /// thread that was not itself waiting.
    /// </summary>
    /// <remarks>
    /// Bounded three ways: a thread already on the chain ends it, because a cycle means the attribution
    /// is wrong rather than that a deadlock is real; a wake the trace cannot attribute to a thread ends
    /// it, because there is nothing to step to; and <see cref="MaxChainDepth"/> ends it, because neither
    /// of the first two is a guarantee on a trace whose switch stream wrapped mid-stall.
    /// </remarks>
    private IReadOnlyList<ThreadWaitChainLink> WalkChain(ThreadWait? anchor, Func<int, string> processNameOf)
    {
        if (anchor is null)
        {
            return [];
        }

        var links = new List<ThreadWaitChainLink>();
        var seen = new HashSet<int> { anchor.ThreadId };
        var current = anchor;

        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            if (current.Ready is not { } ready)
            {
                // Nothing readied this thread: its own timer expired, or the keyword was off. Either way
                // there is no far end to name.
                break;
            }

            if (ready.FromDeferredProcedureCall)
            {
                links.Add(new ThreadWaitChainLink(-1, string.Empty, 0, ready.Processor, EndsChain: true, FromDpc: true));
                break;
            }

            if (ready.WakerThreadId < 0 || !seen.Add(ready.WakerThreadId))
            {
                break;
            }

            var wakerProcess = processNameOf(ready.WakerThreadId);

            // The link's own wait has to cover the interval it is supposed to explain, or it is a
            // different wait on the same thread that happens to be in the trace.
            var blocking = _longWaits
                .Where(candidate => candidate.ThreadId == ready.WakerThreadId && Covers(candidate, current))
                .OrderByDescending(candidate => candidate.DurationMs)
                .FirstOrDefault();

            if (blocking is null)
            {
                links.Add(new ThreadWaitChainLink(ready.WakerThreadId, wakerProcess, 0, ready.Processor, EndsChain: true, FromDpc: false));
                break;
            }

            links.Add(new ThreadWaitChainLink(
                ready.WakerThreadId,
                wakerProcess,
                blocking.DurationMs,
                ready.Processor,
                EndsChain: false,
                FromDpc: false));

            current = blocking;
        }

        return links;
    }

    /// <summary>
    /// Whether a candidate is off the processor for most of the wait it is supposed to explain.
    /// </summary>
    private static bool Covers(ThreadWait candidate, ThreadWait anchor)
    {
        var start = candidate.Start > anchor.Start ? candidate.Start : anchor.Start;
        var end = candidate.End < anchor.End ? candidate.End : anchor.End;
        var overlapMs = (end - start).TotalMilliseconds;
        return overlapMs > 0 && overlapMs >= anchor.DurationMs * ChainOverlapShare;
    }

    private static bool IsWaiting(string state)
    {
        return state.Equals("Wait", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Waiting", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SwitchOut(DateTime Timestamp, int ProcessId, string State, string Reason);

    /// <param name="WakerThreadId">
    /// The thread the switch stream had on <paramref name="Processor"/> when the wake fired, or -1 when
    /// nothing can be claimed. Always an inference — see <see cref="_runningByProcessor"/>.
    /// </param>
    private sealed record Ready(int WakerThreadId, int Processor, bool FromDeferredProcedureCall);

    private sealed record ThreadWait(
        int ProcessId,
        int ThreadId,
        DateTime Start,
        DateTime End,
        double DurationMs,
        string State,
        string Reason,
        Ready? Ready);
}

/// <summary>One step of a release chain: who was waited on, and whether the chain ends there.</summary>
/// <param name="WaitMs">How long this thread was itself off the processor, or zero when it was not.</param>
/// <param name="EndsChain">
/// True when this thread was on the processor for the interval, which is where the chain stops and the
/// sentence has its subject.
/// </param>
/// <param name="FromDpc">
/// True when the wake came from a deferred procedure call, which names no thread at all: the thread on
/// that processor is merely the one the interrupt suspended.
/// </param>
internal sealed record ThreadWaitChainLink(
    int ThreadId,
    string ProcessName,
    double WaitMs,
    int Processor,
    bool EndsChain,
    bool FromDpc);

/// <param name="ReleaseChain">
/// The threads behind the longest wait, nearest first. Empty when nothing readied the thread — a timer
/// expiry — or when the trace could not attribute the wake to anything.
/// </param>
/// <param name="BlockerModules">
/// What the thread at the end of the chain was executing, already formatted as share and module. Empty
/// when the chain names no such thread.
/// </param>
internal sealed record ThreadWaitSummary(
    int ThreadId,
    IReadOnlyList<ThreadWaitInterval> Intervals,
    int UserRequestWaitCount,
    int CpuSampleCount,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ThreadWaitChainLink> ReleaseChain,
    IReadOnlyList<string> BlockerModules)
{
    public int LongWaitCount => Intervals.Count;
    public double MaxWaitMs => Intervals.Select(wait => wait.DurationMs).DefaultIfEmpty().Max();
    public double TotalWaitMs => Intervals.Sum(wait => wait.DurationMs);

    /// <summary>
    /// The thread the chain ends on, which is the one that was actually running. Null when the chain
    /// ends on a DPC or was never established.
    /// </summary>
    public ThreadWaitChainLink? Blocker => ReleaseChain.LastOrDefault(link => link is { EndsChain: true, FromDpc: false });

    public string Describe()
    {
        var reasons = Reasons.Count > 0 ? $" Orsaker: {string.Join(", ", Reasons)}." : string.Empty;
        return $"Schemaläggning: aktiv GTA-tråd tid {ThreadId} låg sammanhängande av CPU:n upp till "
            + $"{MaxWaitMs:F1} ms ({LongWaitCount} väntor ≥100 ms; {UserRequestWaitCount} Wait/UserRequest)."
            + reasons
            + DescribeChain();
    }

    /// <summary>
    /// The sentence that separates "the game blocked on itself" from "something outside took the
    /// processor", which is the distinction the wait length alone cannot make.
    /// </summary>
    private string DescribeChain()
    {
        if (ReleaseChain.Count == 0)
        {
            return " Ingen ReadyThread-händelse släppte tråden — väntan löpte ut på sin egen timer, "
                + "eller så saknas keywordet i spåret, så kedjan går inte att följa.";
        }

        var steps = string.Join(
            " → ",
            ReleaseChain.Select(link => link.FromDpc
                ? $"en DPC på CPU {link.Processor}"
                : link.EndsChain
                    ? $"tid {link.ThreadId} ({link.ProcessName}), som låg på processorn hela tiden"
                    : $"tid {link.ThreadId} ({link.ProcessName}), som väntade {link.WaitMs:F0} ms"));

        var ending = ReleaseChain[^1].FromDpc
            ? " En DPC namnger ingen tråd, så kedjan slutar där."
            : Blocker is null
                ? " Kedjan kunde inte följas hela vägen."
                : BlockerModules.Count > 0
                    ? $" Tråd {Blocker.ThreadId} körde {string.Join(", ", BlockerModules)}."
                    : string.Empty;

        return $" Kedjan bakom den längsta väntan: tid {ThreadId} → {steps}."
            + " Vem som släppte tråden är härlett ur vilken tråd som låg på samma processor när "
            + "ReadyThread-händelsen kom, inte avläst ur dess stack."
            + ending;
    }
}

internal sealed record ThreadWaitInterval(long StartUnixMs, long EndUnixMs, double DurationMs, bool IsUserRequest);
