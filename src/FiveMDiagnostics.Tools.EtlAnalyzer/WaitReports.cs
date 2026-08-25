namespace FiveMDiagnostics.Tools.EtlAnalyzer;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

/// <summary>
/// The report that names the other end of a wait: when the game thread went off the processor, for how
/// long, and which thread released it.
/// </summary>
/// <remarks>
/// The CPU reports answer "what did the thread run", which is the wrong question for a stall the thread
/// spends asleep — three sessions running, the main thread was off-CPU for almost the whole long frame
/// while every core column read as idle. A <c>ReadyThread</c> event names the thread that made the
/// waiter runnable again, so the stall stops being "the main thread waited 1985 ms" and becomes
/// "thread X, running module Y, released it after 1985 ms".
/// <para>
/// This one report reads the ETL with TraceEvent rather than the TraceProcessing library the rest of the
/// tool uses. It is not a preference: TraceProcessing rejects the context switch stream in these
/// captures outright ("the context switch event has an invalid data size") and takes the ready thread
/// data down with it, while TraceEvent parses the same file without complaint. The app's own
/// <c>EtlArtifactParser</c> already reads these traces with TraceEvent for the same reason.
/// </para>
/// <para>
/// Module attribution only, as everywhere in this tool. "Released by GTAProcess tid 30920 in
/// adhesive.dll" is the level that has moved the investigation; function names need symbol servers.
/// </para>
/// </remarks>
internal static class WaitReports
{
    /// <summary>
    /// Waits shorter than this are never worth a second pass, whatever the caller asked for. A thread
    /// that parks for a millisecond between frames does it thousands of times in a capture, and the
    /// second pass has to keep a stack for every one of them.
    /// </summary>
    private const double FloorMilliseconds = 1;

    // TimeStampQPC is marked "discouraged" in favour of relative milliseconds, but it is the exact
    // integer a StackWalk event carries to name the event its stack belongs to. A double comparison
    // would match the wrong switch on a thread that parks and resumes inside the same microsecond.
#pragma warning disable CS0618

    public static void Wait(
        string path,
        string targetProcess,
        int? requestedThreadId,
        double minimumMilliseconds,
        int top,
        int? fromMilliseconds,
        int? toMilliseconds)
    {
        minimumMilliseconds = Math.Max(minimumMilliseconds, FloorMilliseconds);

        var scan = FirstPass(path, targetProcess, requestedThreadId, minimumMilliseconds, fromMilliseconds, toMilliseconds);
        if (scan is null)
        {
            return;
        }

        var stacks = SecondPass(path, scan, out var stackEvents);

        Console.WriteLine();
        Console.WriteLine($"  tid {scan.ThreadId} ({scan.ProcessName}): {scan.Waits.Count} waits ≥{minimumMilliseconds:F0} ms "
            + $"in the sampled window, {scan.Waits.Sum(wait => wait.DurationMs):F0} ms off-CPU in total");

        // Both of these are silently absent when the profile did not ask for the stack, and the report
        // then reads like "nobody released the thread" rather than "this trace cannot say".
        Console.WriteLine($"  {scan.ReadyThreadEventCount} ReadyThread events in the trace; "
            + $"{stacks.Count} of {scan.WantedStacks.Count + scan.WantedReadyStacks.Count} wanted stacks found "
            + $"among {stackEvents} StackWalk events");

        if (scan.Waits.Count == 0)
        {
            Console.WriteLine("  (no wait that long — either the stall is not on this thread, or the ring buffer");
            Console.WriteLine("   wrapped past it; check the cpu report for which thread carries the frame)");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  who released the thread, summed over every wait above:");
        foreach (var group in scan.Waits
            .GroupBy(wait => Waker(wait, scan, stacks))
            .OrderByDescending(group => group.Sum(wait => wait.DurationMs))
            .Take(10))
        {
            Console.WriteLine($"    {group.Sum(wait => wait.DurationMs),8:F0} ms  ×{group.Count(),-4} {group.Key}");
        }

        Console.WriteLine();
        Console.WriteLine("  where the thread was blocked (module that called the wait), summed:");
        foreach (var group in scan.Waits
            .GroupBy(wait => BlockedIn(wait, scan, stacks))
            .OrderByDescending(group => group.Sum(wait => wait.DurationMs))
            .Take(10))
        {
            Console.WriteLine($"    {group.Sum(wait => wait.DurationMs),8:F0} ms  ×{group.Count(),-4} {group.Key}");
        }

        foreach (var wait in scan.Waits.OrderByDescending(wait => wait.DurationMs).Take(top))
        {
            Console.WriteLine();
            Console.WriteLine($"  {wait.Start:HH:mm:ss.fff} → {wait.End:HH:mm:ss.fff}  {wait.DurationMs,8:F1} ms  "
                + $"{wait.State}/{wait.Reason}");
            Console.WriteLine($"    released by  {Waker(wait, scan, stacks)}");
            Console.WriteLine($"    waker stack  {Chain(WakingFrames(wait, stacks), WakerThreadId(wait, stacks), scan)}");
            Console.WriteLine($"    resumed into {Chain(stacks.Resumed.GetValueOrDefault((scan.ThreadId, wait.SwitchInQpc)), scan.ThreadId, scan)}");
        }
    }

    /// <summary>
    /// Reads the trace once to find the waits, and to note which stacks the second pass has to keep.
    /// </summary>
    /// <remarks>
    /// Two passes rather than one because a stack event arrives immediately after the event it belongs
    /// to, but which thread is "the game thread" is only known once every CPU sample has been counted —
    /// and buffering every context switch stack in a capture to avoid the second read costs gigabytes.
    /// </remarks>
    private static Scan? FirstPass(
        string path,
        string targetProcess,
        int? requestedThreadId,
        double minimumMilliseconds,
        int? fromMilliseconds,
        int? toMilliseconds)
    {
        var images = new ImageMap();
        var processNames = new Dictionary<int, string>();
        var processByThread = new Dictionary<int, int>();
        var samples = new List<(int ThreadId, ulong InstructionPointer)>();
        var switchOutByThread = new Dictionary<int, SwitchOut>();
        var readyByThread = new Dictionary<int, Ready>();
        var runningByProcessor = new Dictionary<int, int>();
        var waits = new List<ThreadWait>();
        var readyEvents = 0;
        DateTime? firstSample = null;
        DateTime? lastSample = null;

        using (var source = new ETWTraceEventSource(path))
        {
            var kernel = new KernelTraceEventParser(source);

            kernel.ImageLoad += images.Add;
            kernel.ImageDCStart += images.Add;
            kernel.ImageDCStop += images.Add;

            kernel.ProcessStart += data => Name(processNames, data);
            kernel.ProcessDCStart += data => Name(processNames, data);
            kernel.ProcessDCStop += data => Name(processNames, data);

            kernel.ThreadStart += data => Map(processByThread, data);
            kernel.ThreadDCStart += data => Map(processByThread, data);
            kernel.ThreadDCStop += data => Map(processByThread, data);

            kernel.PerfInfoSample += data =>
            {
                firstSample ??= data.TimeStamp;
                lastSample = data.TimeStamp;
                if (data.ThreadID >= 0)
                {
                    // Buffered rather than counted, because which module the instruction pointer lands in
                    // is what picks the thread, and the image table is only complete at the end of the
                    // trace. Sampled profile events also report ProcessID as -1 most of the time; the
                    // thread map from context switches fills that in by then.
                    samples.Add((data.ThreadID, data.InstructionPointer));
                    if (data.ProcessID >= 0)
                    {
                        processByThread.TryAdd(data.ThreadID, data.ProcessID);
                    }
                }
            };

            // Who did the readying is the whole point of the report, and the event does not say: the
            // classic kernel ReadyThread event carries the readied thread in its payload and leaves the
            // header's own thread id at -1. What it does carry is the processor it fired on, and the
            // context switch stream says which thread was running there — that is the readying thread.
            // The event fires before the switch that ends the wait, so it is parked per readied thread
            // and claimed by the switch-in below.
            kernel.DispatcherReadyThread += data =>
            {
                readyEvents++;
                if (data.AwakenedThreadID >= 0)
                {
                    readyByThread[data.AwakenedThreadID] = new Ready(
                        data.ThreadID >= 0 ? data.ThreadID : runningByProcessor.GetValueOrDefault(data.ProcessorNumber, -1),
                        data.TimeStamp,
                        data.TimeStampQPC,
                        data.ProcessorNumber,
                        data.Flags.HasFlag(DispatcherReadyThreadTraceData.ReadyThreadFlags.ReadiedFromDPC));
                }
            };

            kernel.ThreadCSwitch += data =>
            {
                runningByProcessor[data.ProcessorNumber] = data.NewThreadID;

                if (data.NewThreadID >= 0)
                {
                    if (data.NewProcessID >= 0)
                    {
                        processByThread[data.NewThreadID] = data.NewProcessID;
                    }

                    // Consumed on every switch-in, not only on the ones long enough to report. A record
                    // left behind by a short wait outlives it, and the next wait on that thread — a
                    // timer expiry with no ReadyThread event of its own — would claim it and name a
                    // waker that had nothing to do with it.
                    readyByThread.Remove(data.NewThreadID, out var ready);

                    if (switchOutByThread.Remove(data.NewThreadID, out var previous))
                    {
                        var durationMs = (data.TimeStamp - previous.Timestamp).TotalMilliseconds;
                        if (durationMs >= minimumMilliseconds && IsWaiting(previous.State))
                        {
                            // A wake that predates the thread going off the processor belongs to an
                            // earlier wait. Nothing released this one.
                            if (ready is not null && ready.Timestamp < previous.Timestamp)
                            {
                                ready = null;
                            }

                            waits.Add(new ThreadWait(
                                data.NewThreadID,
                                previous.Timestamp,
                                data.TimeStamp,
                                durationMs,
                                previous.State,
                                previous.Reason,
                                data.TimeStampQPC,
                                ready?.InferredThreadId ?? -1,
                                ready?.Qpc ?? 0,
                                ready?.Processor ?? -1,
                                ready is not null,
                                ready?.FromDeferredProcedureCall ?? false));
                        }
                    }
                }

                if (data.OldThreadID >= 0)
                {
                    if (data.OldProcessID >= 0)
                    {
                        processByThread[data.OldThreadID] = data.OldProcessID;
                    }

                    switchOutByThread[data.OldThreadID] = new SwitchOut(
                        data.TimeStamp,
                        data.OldThreadState.ToString(),
                        data.OldThreadWaitReason.ToString());
                }
            };

            source.Process();
        }

        var threadId = requestedThreadId ?? GameThread(samples, images, processByThread, processNames, targetProcess);
        if (threadId is null)
        {
            Console.WriteLine();
            Console.WriteLine($"  no sampled thread for a process matching \"{targetProcess}\": pass --tid to pick one");
            return null;
        }

        var from = firstSample is { } origin && fromMilliseconds is { } start ? origin.AddMilliseconds(start) : DateTime.MinValue;
        var to = firstSample is { } begin && toMilliseconds is { } end ? begin.AddMilliseconds(end) : DateTime.MaxValue;
        var selected = waits
            .Where(wait => wait.ThreadId == threadId && wait.End >= from && wait.End <= to)
            .ToArray();

        var wanted = new HashSet<(int ThreadId, long Qpc)>();
        var wantedReady = new HashSet<(int Processor, long Qpc)>();
        foreach (var wait in selected)
        {
            wanted.Add((wait.ThreadId, wait.SwitchInQpc));
            if (wait.HasReadyEvent)
            {
                // Keyed by the processor the ReadyThread event fired on rather than by a thread id,
                // because which thread that was is the question the stack is being fetched to answer.
                wantedReady.Add((wait.ReadyProcessor, wait.ReadyQpc));
            }
        }

        var processId = processByThread.GetValueOrDefault(threadId.Value, -1);
        var processName = processNames.GetValueOrDefault(processId, $"pid {processId}");
        return new Scan(threadId.Value, processName, selected, wanted, wantedReady, images, processNames, processByThread, readyEvents);
    }

    /// <summary>Collects the stacks the first pass asked for, and nothing else.</summary>
    private static Stacks SecondPass(string path, Scan scan, out int stackEvents)
    {
        var stacks = new Stacks();
        stackEvents = 0;
        if (scan.WantedStacks.Count == 0 && scan.WantedReadyStacks.Count == 0)
        {
            return stacks;
        }

        var seen = 0;

        using var source = new ETWTraceEventSource(path);
        var kernel = new KernelTraceEventParser(source);
        kernel.StackWalkStack += data =>
        {
            seen++;

            var switchInKey = (data.ThreadID, data.EventTimeStampQPC);
            if (scan.WantedStacks.Contains(switchInKey))
            {
                stacks.Resumed[switchInKey] = Append(stacks.Resumed.GetValueOrDefault(switchInKey), data);
            }

            // The stack walked for a ReadyThread event belongs to the thread that did the readying, and
            // this event says which thread that was. That makes it a recorded fact rather than the
            // inference the first pass had to settle for.
            var readyKey = (data.ProcessorNumber, data.EventTimeStampQPC);
            if (scan.WantedReadyStacks.Contains(readyKey))
            {
                var existing = stacks.Waking.GetValueOrDefault(readyKey);

                // Two events at one tick on one processor would be the switch-in stack and the ready
                // stack of different threads; only extend the entry that is already this thread's.
                if (existing is null || existing.ThreadId == data.ThreadID)
                {
                    stacks.Waking[readyKey] = new WakingStack(data.ThreadID, Append(existing?.Frames, data));
                }
            }
        };

        source.Process();
        stackEvents = seen;
        return stacks;
    }

    /// <summary>
    /// Adds one stack event's frames to what has been collected for that stack.
    /// </summary>
    /// <remarks>
    /// Appended, not assigned. One logical stack arrives as two events — kernel frames in one, user
    /// frames in the next — and keeping only the first threw away the half that names the caller,
    /// leaving every wait attributed to "ntoskrnl.exe".
    /// </remarks>
    private static ulong[] Append(ulong[]? existing, StackWalkStackTraceData data)
    {
        var frames = existing is null ? [] : new List<ulong>(existing);
        for (var index = 0; index < data.FrameCount; index++)
        {
            frames.Add(data.InstructionPointer(index));
        }

        return frames.ToArray();
    }

    /// <summary>
    /// The thread the frame is spent on: the one running the most code <em>inside the game executable</em>.
    /// </summary>
    /// <remarks>
    /// Not simply the busiest thread of the process. In every capture so far the busiest thread by CPU is
    /// the anti-cheat's, at 0.8 cores of <c>adhesive.dll</c>, and it neither renders nor stalls with the
    /// frame; picking it would report a scan loop's timer sleeps as the stall under investigation.
    /// </remarks>
    private static int? GameThread(
        List<(int ThreadId, ulong InstructionPointer)> samples,
        ImageMap images,
        Dictionary<int, int> processByThread,
        Dictionary<int, string> processNames,
        string targetProcess)
    {
        var executableSamples = new Dictionary<int, int>();
        foreach (var (threadId, instructionPointer) in samples)
        {
            var processId = processByThread.GetValueOrDefault(threadId, -1);
            if (!processNames.GetValueOrDefault(processId, string.Empty).Contains(targetProcess, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (images.Resolve(processId, instructionPointer).Contains(targetProcess, StringComparison.OrdinalIgnoreCase))
            {
                executableSamples[threadId] = executableSamples.GetValueOrDefault(threadId) + 1;
            }
        }

        return executableSamples
            .OrderByDescending(entry => entry.Value)
            .Select(entry => (int?)entry.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// Who released the thread, and how confidently that is known.
    /// </summary>
    /// <remarks>
    /// The ReadyThread event names the thread it woke, not the one doing the waking: the classic kernel
    /// provider leaves the event header's own thread id at -1. Two things can fill that in, and they are
    /// not equally good.
    /// <para>
    /// The stack walked for the event belongs to the readying thread and carries its id, so when the
    /// trace has that stack the answer is recorded rather than derived. Without it, the only remaining
    /// handle is the processor the event fired on and which thread the context switch stream says was
    /// running there. That holds for an ordinary user mode wake and is what a trace viewer shows, but it
    /// is an inference and is labelled as one — and it does not hold at all inside a DPC, where the
    /// thread on that processor is merely the one that was interrupted, so no thread is claimed there.
    /// </para>
    /// </remarks>
    private static string Waker(ThreadWait wait, Scan scan, Stacks stacks)
    {
        if (!wait.HasReadyEvent)
        {
            // Nothing readied this thread: its own timer expired, or the keyword was off.
            return "nobody (timer expiry or no ReadyThread event)";
        }

        var module = LeafModule(WakingFrames(wait, stacks), WakerThreadId(wait, stacks), scan);

        if (wait.ReadiedFromDeferredProcedureCall)
        {
            return $"a DPC on CPU {wait.ReadyProcessor}, in {module}";
        }

        var recorded = stacks.Waking.GetValueOrDefault((wait.ReadyProcessor, wait.ReadyQpc));
        var threadId = recorded?.ThreadId ?? wait.InferredReadyThreadId;
        if (threadId < 0)
        {
            return $"a thread on CPU {wait.ReadyProcessor} the trace does not identify, in {module}";
        }

        var process = scan.ProcessNameFor(scan.ProcessByThread.GetValueOrDefault(threadId, -1));
        var self = threadId == wait.ThreadId ? " (itself)" : string.Empty;
        var derivation = recorded is null ? $" (inferred from CPU {wait.ReadyProcessor})" : string.Empty;
        return $"{process} tid {threadId}{self}{derivation}, in {module}";
    }

    /// <summary>The module that called the wait, i.e. the innermost user mode frame it resumed into.</summary>
    private static string BlockedIn(ThreadWait wait, Scan scan, Stacks stacks)
    {
        return LeafModule(stacks.Resumed.GetValueOrDefault((wait.ThreadId, wait.SwitchInQpc)), wait.ThreadId, scan);
    }

    private static ulong[]? WakingFrames(ThreadWait wait, Stacks stacks)
    {
        return stacks.Waking.GetValueOrDefault((wait.ReadyProcessor, wait.ReadyQpc))?.Frames;
    }

    /// <summary>Whose address space the waking frames should be resolved in.</summary>
    private static int WakerThreadId(ThreadWait wait, Stacks stacks)
    {
        return stacks.Waking.GetValueOrDefault((wait.ReadyProcessor, wait.ReadyQpc))?.ThreadId
            ?? wait.InferredReadyThreadId;
    }

    private static string LeafModule(ulong[]? frames, int threadId, Scan scan)
    {
        if (frames is not { Length: > 0 })
        {
            return "(no stack)";
        }

        // Frame 0 is the innermost. Walk outwards past the kernel and past ntdll's own wait plumbing:
        // "ntoskrnl.exe" is true of every wait in the trace and says nothing about which component owns
        // this one.
        var processId = scan.ProcessByThread.GetValueOrDefault(threadId, -1);
        foreach (var frame in frames)
        {
            var module = scan.Images.Resolve(processId, frame);
            if (!IsPlumbing(module))
            {
                return module;
            }
        }

        return scan.Images.Resolve(processId, frames[0]);
    }

    private static string Chain(ulong[]? frames, int threadId, Scan scan)
    {
        if (frames is not { Length: > 0 })
        {
            return "(no stack)";
        }

        // Innermost first, consecutive repeats collapsed: the caller wants to see the handful of module
        // transitions, not sixty frames inside one dll.
        var processId = scan.ProcessByThread.GetValueOrDefault(threadId, -1);
        var chain = new List<string>();
        foreach (var frame in frames)
        {
            var module = scan.Images.Resolve(processId, frame);
            if (chain.Count == 0 || chain[^1] != module)
            {
                chain.Add(module);
            }
        }

        return string.Join(" ← ", chain.Take(12));
    }

    private static bool IsPlumbing(string module)
    {
        return module is "?"
            || module.Equals("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase)
            || module.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)
            || module.Equals("KernelBase.dll", StringComparison.OrdinalIgnoreCase)
            || module.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)
            || module.Equals("win32u.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWaiting(string state)
    {
        return state.Equals("Wait", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Waiting", StringComparison.OrdinalIgnoreCase);
    }

    private static void Name(Dictionary<int, string> names, ProcessTraceData data)
    {
        if (data.ProcessID >= 0 && !string.IsNullOrEmpty(data.ImageFileName))
        {
            names[data.ProcessID] = data.ImageFileName;
        }
    }

    private static void Map(Dictionary<int, int> processByThread, ThreadTraceData data)
    {
        if (data.ThreadID >= 0 && data.ProcessID >= 0)
        {
            processByThread[data.ThreadID] = data.ProcessID;
        }
    }

#pragma warning restore CS0618

    /// <summary>The stack of the thread that did the readying, and which thread that turned out to be.</summary>
    private sealed record WakingStack(int ThreadId, ulong[] Frames);

    /// <summary>Where a wait resumed, and who released it, for the waits the first pass selected.</summary>
    private sealed class Stacks
    {
        public Dictionary<(int ThreadId, long Qpc), ulong[]> Resumed { get; } = [];

        public Dictionary<(int Processor, long Qpc), WakingStack> Waking { get; } = [];

        public int Count => Resumed.Count + Waking.Count;
    }

    private sealed record SwitchOut(DateTime Timestamp, string State, string Reason);

    private sealed record Ready(
        int InferredThreadId,
        DateTime Timestamp,
        long Qpc,
        int Processor,
        bool FromDeferredProcedureCall);

    private sealed record ThreadWait(
        int ThreadId,
        DateTime Start,
        DateTime End,
        double DurationMs,
        string State,
        string Reason,
        long SwitchInQpc,
        int InferredReadyThreadId,
        long ReadyQpc,
        int ReadyProcessor,
        bool HasReadyEvent,
        bool ReadiedFromDeferredProcedureCall);

    private sealed record Scan(
        int ThreadId,
        string ProcessName,
        IReadOnlyList<ThreadWait> Waits,
        HashSet<(int ThreadId, long Qpc)> WantedStacks,
        HashSet<(int Processor, long Qpc)> WantedReadyStacks,
        ImageMap Images,
        Dictionary<int, string> ProcessNames,
        Dictionary<int, int> ProcessByThread,
        int ReadyThreadEventCount)
    {
        public string ProcessNameFor(int processId)
        {
            return processId switch
            {
                0 or 4 => "System",
                _ => ProcessNames.GetValueOrDefault(processId, $"pid {processId}"),
            };
        }
    }

    /// <summary>Instruction pointer to module name, per process, with drivers in one shared table.</summary>
    private sealed class ImageMap
    {
        private readonly Dictionary<int, List<(ulong Start, ulong End, string Name)>> _byProcess = [];
        private readonly List<(ulong Start, ulong End, string Name)> _kernel = [];

        public void Add(ImageLoadTraceData data)
        {
            var range = (data.ImageBase, data.ImageBase + (ulong)data.ImageSize, FileName(data.FileName));

            // Drivers live in the System process but execute on whichever thread entered the kernel, so
            // a per-process table would fail to resolve every kernel frame in the trace.
            if (data.ProcessID is 0 or 4 || range.Item1 >= 0xFFFF_8000_0000_0000)
            {
                _kernel.Add(range);
                return;
            }

            if (!_byProcess.TryGetValue(data.ProcessID, out var images))
            {
                images = [];
                _byProcess[data.ProcessID] = images;
            }

            images.Add(range);
        }

        public string Resolve(int processId, ulong address)
        {
            if (address >= 0xFFFF_8000_0000_0000)
            {
                return Find(_kernel, address);
            }

            return _byProcess.TryGetValue(processId, out var images) ? Find(images, address) : "?";
        }

        private static string Find(List<(ulong Start, ulong End, string Name)> images, ulong address)
        {
            foreach (var image in images)
            {
                if (address >= image.Start && address < image.End)
                {
                    return image.Name;
                }
            }

            return "?";
        }

        private static string FileName(string path)
        {
            var index = path.LastIndexOfAny(['\\', '/']);
            return index >= 0 ? path[(index + 1)..] : path;
        }
    }
}
