using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FiveMDiagnostics.Integrations.Etw;

// TimeStampQPC is marked "discouraged" in favour of relative milliseconds, but it is the exact integer
// a StackWalk event carries to name the event its stack belongs to. See StackSecondPass.
#pragma warning disable CS0618

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
    /// every sentence built on it says so. The second pass replaces it with the stack's own thread id
    /// where the trace has the stack.
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
            data.Flags.HasFlag(DispatcherReadyThreadTraceData.ReadyThreadFlags.ReadiedFromDPC),
            data.TimeStampQPC);
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
    /// <param name="qpc">
    /// The event's own tick, which is what its stack is keyed by on the second pass. Zero when the
    /// caller has no trace to read it back from.
    /// </param>
    internal void RecordReady(int awakenedThreadId, int processorNumber, bool fromDeferredProcedureCall, long qpc = 0)
    {
        if (awakenedThreadId < 0)
        {
            return;
        }

        _readyByThread[awakenedThreadId] = new Ready(
            fromDeferredProcedureCall ? -1 : _runningByProcessor.GetValueOrDefault(processorNumber, -1),
            processorNumber,
            qpc,
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
    /// <see cref="Summarize(CpuSampleAttribution, string?, CancellationToken)"/>.
    /// </summary>
    internal IReadOnlyList<ThreadWaitChainLink> ChainFor(int threadId, Func<int, string> processNameOf)
    {
        return ChainFor(threadId, processNameOf, TraceStacks.Empty.Wakers);
    }

    /// <summary>The same, with the wakers a second pass read out of the ReadyThread stacks.</summary>
    internal IReadOnlyList<ThreadWaitChainLink> ChainFor(
        int threadId,
        Func<int, string> processNameOf,
        IReadOnlyDictionary<(int Processor, long Qpc), int> recordedWakers)
    {
        var anchor = _longWaits
            .Where(wait => wait.ThreadId == threadId)
            .OrderByDescending(wait => wait.DurationMs)
            .FirstOrDefault();

        return WalkChain(anchor, processNameOf, recordedWakers);
    }

    public ThreadWaitSummary? Summarize(CpuSampleAttribution cpu)
    {
        return Summarize(cpu, stacksFrom: null, CancellationToken.None);
    }

    /// <param name="stacksFrom">
    /// The trace to read stacks out of on a second pass, or null to settle for what the switch stream
    /// alone can say. The stacks make the waker a recorded fact instead of an inference, and they are
    /// the only way to see what the thread at the end of the chain was doing <em>during</em> the wait
    /// rather than across the whole retained window.
    /// </param>
    public ThreadWaitSummary? Summarize(CpuSampleAttribution cpu, string? stacksFrom, CancellationToken cancellationToken)
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

        var anchor = selectedWaits[0];
        var stacks = stacksFrom is null
            ? TraceStacks.Empty
            : StackSecondPass.Read(stacksFrom, ReadyKeysAround(anchor), anchor.Start, anchor.End, cpu.ModuleForFrame, cancellationToken);

        var chain = WalkChain(anchor, threadId => cpu.Name(cpu.ProcessIdForThread(threadId)), stacks.Wakers);

        // What the thread at the end of the chain was executing. Without it the sentence names a thread
        // id and a duration, and the reader has to run etlanalyzer by hand to learn that the id belongs
        // to the render thread and that it was sitting in Direct3D — which is the whole finding. Measured
        // inside the wait as well as across the window: the window is twenty seconds and the wait one,
        // and everything the thread did before and after dilutes the answer.
        var blocker = chain.LastOrDefault(link => link is { EndsChain: true, FromDpc: false });
        var blockerModules = blocker is null ? [] : cpu.ModulesForThread(blocker.ThreadId, take: 4);
        var blockerModulesDuringWait = blocker is null
            ? []
            : cpu.ModulesForThread(blocker.ThreadId, take: int.MaxValue, anchor.Start, anchor.End);
        var blockerStacksDuringWait = blocker is null ? [] : stacks.TopChains(blocker.ThreadId, take: 4);

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
            blockerModules,
            blockerModulesDuringWait.Take(4).ToArray(),
            blockerModulesDuringWait.Sum(module => module.Cores),
            blockerStacksDuringWait);
    }

    /// <summary>
    /// The ReadyThread events the chain may step through: those of every long wait overlapping the
    /// anchor, the anchor's own included.
    /// </summary>
    /// <remarks>
    /// Overlap rather than the inferred waker picks the set, because which thread released which is
    /// exactly what the stacks are being fetched to establish; selecting on it here would only retain
    /// the links the inference already agreed with.
    /// </remarks>
    private HashSet<(int Processor, long Qpc)> ReadyKeysAround(ThreadWait anchor)
    {
        var keys = new HashSet<(int Processor, long Qpc)>();
        foreach (var wait in _longWaits)
        {
            if (wait.Ready is { FromDeferredProcedureCall: false, Qpc: not 0 } ready
                && wait.Start < anchor.End
                && wait.End > anchor.Start)
            {
                keys.Add((ready.Processor, ready.Qpc));
            }
        }

        return keys;
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
    /// <param name="recordedWakers">
    /// The thread each ReadyThread stack belongs to, by the event's processor and tick. A key found here
    /// names the waker as a fact; one missing falls back to the switch stream's inference.
    /// </param>
    private IReadOnlyList<ThreadWaitChainLink> WalkChain(
        ThreadWait? anchor,
        Func<int, string> processNameOf,
        IReadOnlyDictionary<(int Processor, long Qpc), int> recordedWakers)
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

            var recorded = recordedWakers.TryGetValue((ready.Processor, ready.Qpc), out var fromStack);
            var wakerThreadId = recorded ? fromStack : ready.WakerThreadId;
            if (wakerThreadId < 0 || !seen.Add(wakerThreadId))
            {
                break;
            }

            var wakerProcess = processNameOf(wakerThreadId);

            // The link's own wait has to cover the interval it is supposed to explain, or it is a
            // different wait on the same thread that happens to be in the trace.
            var blocking = _longWaits
                .Where(candidate => candidate.ThreadId == wakerThreadId && Covers(candidate, current))
                .OrderByDescending(candidate => candidate.DurationMs)
                .FirstOrDefault();

            if (blocking is null)
            {
                links.Add(new ThreadWaitChainLink(wakerThreadId, wakerProcess, 0, ready.Processor, EndsChain: true, FromDpc: false, recorded));
                break;
            }

            links.Add(new ThreadWaitChainLink(
                wakerThreadId,
                wakerProcess,
                blocking.DurationMs,
                ready.Processor,
                EndsChain: false,
                FromDpc: false,
                recorded));

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
    /// <param name="Qpc">The event's tick, which keys its stack on the second pass; zero when unknown.</param>
    private sealed record Ready(int WakerThreadId, int Processor, long Qpc, bool FromDeferredProcedureCall);

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
/// <param name="WakerRecorded">
/// True when the thread id was read out of the ReadyThread event's own stack, false when it was inferred
/// from which thread the switch stream had on the processor at the time.
/// </param>
internal sealed record ThreadWaitChainLink(
    int ThreadId,
    string ProcessName,
    double WaitMs,
    int Processor,
    bool EndsChain,
    bool FromDpc,
    bool WakerRecorded = false);

/// <param name="ReleaseChain">
/// The threads behind the longest wait, nearest first. Empty when nothing readied the thread — a timer
/// expiry — or when the trace could not attribute the wake to anything.
/// </param>
/// <param name="BlockerModules">
/// What the thread at the end of the chain was executing across the whole retained window. Empty when
/// the chain names no such thread.
/// </param>
/// <param name="BlockerModulesDuringWait">
/// The same, restricted to the longest wait itself. Empty when the thread was never sampled inside it.
/// </param>
/// <param name="BlockerCoresDuringWait">
/// How much of the wait the blocker was actually on a processor, as a share of one core. The chain
/// only knows the thread had no single wait of 100 ms or more; on 10 September the thread at the end
/// of a 2.9 s chain was sampled for 1 % of it, which is a thread waiting in short steps, not a thread
/// running.
/// </param>
/// <param name="BlockerStacksDuringWait">
/// The blocker's sample stacks inside the wait, collapsed to module chains and already formatted with
/// their share. Empty without a second pass over the trace.
/// </param>
internal sealed record ThreadWaitSummary(
    int ThreadId,
    IReadOnlyList<ThreadWaitInterval> Intervals,
    int UserRequestWaitCount,
    int CpuSampleCount,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ThreadWaitChainLink> ReleaseChain,
    IReadOnlyList<ModuleShare> BlockerModules,
    IReadOnlyList<ModuleShare> BlockerModulesDuringWait,
    double BlockerCoresDuringWait,
    IReadOnlyList<string> BlockerStacksDuringWait)
{
    public int LongWaitCount => Intervals.Count;
    public double MaxWaitMs => Intervals.Select(wait => wait.DurationMs).DefaultIfEmpty().Max();
    public double TotalWaitMs => Intervals.Sum(wait => wait.DurationMs);

    /// <summary>
    /// The thread the chain ends on, which is the one that was actually running. Null when the chain
    /// ends on a DPC or was never established.
    /// </summary>
    public ThreadWaitChainLink? Blocker => ReleaseChain.LastOrDefault(link => link is { EndsChain: true, FromDpc: false });

    /// <summary>Links that name a thread and so had a waker to record or infer.</summary>
    public int RecordedLinkCount => ReleaseChain.Count(link => link is { FromDpc: false, WakerRecorded: true });

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

        var threadLinks = ReleaseChain.Count(link => !link.FromDpc);
        var recorded = RecordedLinkCount;
        var mixed = recorded > 0 && recorded < threadLinks;

        var steps = string.Join(
            " → ",
            ReleaseChain.Select(link => link.FromDpc
                ? $"en DPC på CPU {link.Processor}"
                : $"tid {link.ThreadId} ({link.ProcessName}{(mixed && !link.WakerRecorded ? ", härledd" : string.Empty)}), "
                    + (link.EndsChain ? "utan egen väntan ≥100 ms" : $"som väntade {link.WaitMs:F0} ms")));

        // Whether the reader may treat the chain as fact. Only the inferred case gets the caveat; a chain
        // read out of the stacks has earned the plain statement.
        var derivation = threadLinks == 0
            ? string.Empty
            : recorded == threadLinks
                ? " Varje länk är avläst ur ReadyThread-stacken."
                : recorded == 0
                    ? " Vem som släppte tråden är härlett ur vilken tråd som låg på samma processor när "
                        + "ReadyThread-händelsen kom, inte avläst ur dess stack."
                    : " Länkar märkta härledd bygger på vilken tråd som låg på samma processor när "
                        + "ReadyThread-händelsen kom; övriga är avlästa ur dess stack.";

        var ending = ReleaseChain[^1].FromDpc
            ? " En DPC namnger ingen tråd, så kedjan slutar där."
            : Blocker is null
                ? " Kedjan kunde inte följas hela vägen."
                : DescribeBlocker();

        return $" Kedjan bakom den längsta väntan: tid {ThreadId} → {steps}." + derivation + ending;
    }

    /// <summary>
    /// What the blocker did inside the wait — how much of it on a processor, and in what — with the
    /// window-wide mix beside it so the reader can see whether the thread changed behaviour.
    /// </summary>
    private string DescribeBlocker()
    {
        if (BlockerModules.Count == 0)
        {
            return string.Empty;
        }

        var threadId = Blocker!.ThreadId;
        var presence = BlockerCoresDuringWait switch
        {
            >= 0.9 => $" Tråd {threadId} låg på processorn nästan hela väntan ({MaxWaitMs:F0} ms)",
            >= 0.1 => $" Tråd {threadId} var på processorn {BlockerCoresDuringWait:P0} av väntan ({MaxWaitMs:F0} ms) "
                + "och däremellan i väntor kortare än 100 ms",
            > 0 => $" Tråd {threadId} var på processorn bara {BlockerCoresDuringWait:P0} av väntan ({MaxWaitMs:F0} ms) "
                + "— den väntade själv, i steg kortare än 100 ms",
            _ => $" Tråd {threadId} har inga samples alls under väntan ({MaxWaitMs:F0} ms)",
        };

        var duringWait = BlockerModulesDuringWait.Count > 0
            ? $" och körde då {Modules(BlockerModulesDuringWait)}"
            : string.Empty;

        var stacks = BlockerStacksDuringWait.Count > 0
            ? $" Dess stackar i väntan: {string.Join("; ", BlockerStacksDuringWait)}."
            : string.Empty;

        return $"{presence}{duringWait}; över hela fönstret {Modules(BlockerModules)}.{stacks}";
    }

    private static string Modules(IReadOnlyList<ModuleShare> modules)
    {
        return string.Join(", ", modules.Select(module => $"{module.Share:P0} {ModuleGlossary.Annotate(module.Module)}"));
    }
}

internal sealed record ThreadWaitInterval(long StartUnixMs, long EndUnixMs, double DurationMs, bool IsUserRequest);
