using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FiveMDiagnostics.Integrations.Etw;

// TimeStampQPC is marked "discouraged" in favour of relative milliseconds, but it is the exact integer
// a StackWalk event carries to name the event its stack belongs to. A double comparison would match the
// wrong sample on a thread sampled twice inside the same microsecond.
#pragma warning disable CS0618

/// <summary>
/// The second read of a trace, for the stacks the first read could not know it wanted.
/// </summary>
/// <remarks>
/// <para>
/// A stack arrives as a StackWalk event after the event it describes and is keyed to it by processor
/// (or thread) and tick. The first pass has to finish before the wait worth explaining — and so the
/// handful of keys worth keeping — is known, and buffering every stack instead would hold millions of
/// frames for the sake of a few hundred. One more read with two handlers subscribed costs two to four
/// seconds on the 900 MB ring buffer captures measured, about the same as the first pass.
/// </para>
/// <para>
/// Two things come out of it. The thread id on a ReadyThread event's stack is the thread that did the
/// readying, which turns the release chain from an inference about who was on the processor into a
/// recorded fact. And the sample stacks of every thread inside the wait say what the thread at the end
/// of the chain was actually doing there, at module granularity. Mirrors <c>WaitReports.SecondPass</c>
/// in etlanalyzer.
/// </para>
/// </remarks>
internal static class StackSecondPass
{
    /// <summary>
    /// Innermost distinct user mode modules kept per sample, and innermost kernel modules.
    /// </summary>
    /// <remarks>
    /// Split by mode rather than taken from the top, because a kernel stack is often four drivers deep
    /// before the first user frame and taking the innermost four would never show which user module
    /// entered the kernel. Three of each is enough to tell <c>d3d11 → ntdll → ntoskrnl</c> (a lock)
    /// from <c>d3d11 → nvwgf2umx → dxgkrnl</c> (the driver), which is the distinction the chain is for.
    /// </remarks>
    private const int UserModules = 3;
    private const int KernelModules = 3;

    /// <summary>Above this address a frame is in the kernel; same boundary CpuSampleAttribution uses for images.</summary>
    private const ulong KernelSpace = 0xFFFF_8000_0000_0000;

    /// <summary>
    /// Sample stacks buffered inside the wait before collection stops. A three second wait on sixteen
    /// processors is under fifty thousand.
    /// </summary>
    private const int MaxPendingStacks = 200_000;

    /// <param name="wantedReady">ReadyThread events, by processor and tick, whose stack should name the waker.</param>
    /// <param name="from">Start of the wait whose sample stacks are collected, for every thread.</param>
    /// <param name="resolve">Module for a frame in a given thread's address space.</param>
    public static TraceStacks Read(
        string path,
        IReadOnlySet<(int Processor, long Qpc)> wantedReady,
        DateTime from,
        DateTime to,
        Func<int, ulong, string> resolve,
        CancellationToken cancellationToken)
    {
        var wakers = new Dictionary<(int Processor, long Qpc), int>();

        // The samples inside the wait, keyed the way their stacks will arrive. Keys alone, because a
        // stack arrives for some of them and the rest would be an empty list apiece — fifty thousand of
        // them on a three second wait, none of which is ever written to.
        var wantedSamples = new HashSet<(int ThreadId, long Qpc)>();

        // Frames are appended, not assigned: one logical stack comes as two events, kernel frames first
        // and user frames when the thread next returns to user mode, which can be after its next sample.
        var stacksBySample = new Dictionary<(int ThreadId, long Qpc), List<(string Module, bool Kernel)>>();

        cancellationToken.ThrowIfCancellationRequested();

        using var source = new ETWTraceEventSource(path);
        var kernel = new KernelTraceEventParser(source);

        // Stopping the read rather than throwing out of a handler, so cancellation is honoured on a
        // trace whose stack stream is missing or ends early: the StackWalkStack handler was the only
        // thing checking, and where it never fires the read used to run on to the end of the file.
        using var stopOnCancellation = cancellationToken.Register(source.StopProcessing);

        kernel.PerfInfoSample += data =>
        {
            if (data.ThreadID >= 0 && data.TimeStamp >= from && data.TimeStamp <= to && wantedSamples.Count < MaxPendingStacks)
            {
                wantedSamples.Add((data.ThreadID, data.TimeStampQPC));
            }
        };

        kernel.StackWalkStack += data =>
        {
            // The stack walked for a ReadyThread event belongs to the thread that did the readying, and
            // this event says which thread that was.
            var readyKey = (data.ProcessorNumber, data.EventTimeStampQPC);
            if (wantedReady.Contains(readyKey))
            {
                wakers.TryAdd(readyKey, data.ThreadID);
            }

            var sampleKey = (data.ThreadID, data.EventTimeStampQPC);
            if (!wantedSamples.Contains(sampleKey))
            {
                return;
            }

            if (!stacksBySample.TryGetValue(sampleKey, out var modules))
            {
                modules = [];
                stacksBySample[sampleKey] = modules;
            }

            // Consecutive repeats collapsed: sixty frames inside one dll are one module transition.
            for (var index = 0; index < data.FrameCount; index++)
            {
                var frame = data.InstructionPointer(index);
                var module = resolve(data.ThreadID, frame);
                if (modules.Count == 0 || modules[^1].Module != module)
                {
                    modules.Add((module, frame >= KernelSpace));
                }
            }
        };

        source.Process();
        cancellationToken.ThrowIfCancellationRequested();

        var chainsByThread = new Dictionary<int, Dictionary<string, int>>();
        foreach (var ((threadId, _), modules) in stacksBySample)
        {
            if (modules.Count == 0)
            {
                continue;
            }

            if (!chainsByThread.TryGetValue(threadId, out var chains))
            {
                chains = [];
                chainsByThread[threadId] = chains;
            }

            var chain = Chain(modules);
            chains[chain] = chains.GetValueOrDefault(chain) + 1;
        }

        return new TraceStacks(
            wakers,
            chainsByThread.ToDictionary(entry => entry.Key, entry => (IReadOnlyDictionary<string, int>)entry.Value));
    }

    /// <summary>Caller to callee: the innermost user modules, then the innermost kernel modules.</summary>
    /// <remarks>
    /// A stack with no user frames is said to be one. The user half is walked when the thread next
    /// returns to user mode, and a thread that sits in the kernel for a second has its queue of deferred
    /// walks dropped — so a kernel-only chain means "in the kernel for a long time", not "no caller".
    /// </remarks>
    internal static string Chain(IReadOnlyList<(string Module, bool Kernel)> modules)
    {
        var user = modules.Where(entry => !entry.Kernel).Select(entry => entry.Module).Distinct().Take(UserModules).Reverse().ToArray();
        var kernel = modules.Where(entry => entry.Kernel).Select(entry => entry.Module).Distinct().Take(KernelModules).Reverse();
        var chain = string.Join(" → ", user.Concat(kernel));
        return user.Length == 0 ? $"{chain} (inga användarramar)" : chain;
    }
}

/// <param name="Wakers">The thread each wanted ReadyThread stack belonged to, by the event's processor and tick.</param>
/// <param name="SampleChainsByThread">How many of a thread's sample stacks inside the wait collapsed to each module chain.</param>
internal sealed record TraceStacks(
    IReadOnlyDictionary<(int Processor, long Qpc), int> Wakers,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> SampleChainsByThread)
{
    public static readonly TraceStacks Empty = new(
        new Dictionary<(int Processor, long Qpc), int>(),
        new Dictionary<int, IReadOnlyDictionary<string, int>>());

    /// <summary>A thread's commonest chains inside the wait, formatted with their share of its samples.</summary>
    public IReadOnlyList<string> TopChains(int threadId, int take)
    {
        if (!SampleChainsByThread.TryGetValue(threadId, out var chains) || chains.Count == 0)
        {
            return [];
        }

        var total = chains.Values.Sum();
        return chains
            .OrderByDescending(entry => entry.Value)
            .Take(take)
            .Select(entry => $"{(double)entry.Value / total:P0} {entry.Key}")
            .ToArray();
    }
}
