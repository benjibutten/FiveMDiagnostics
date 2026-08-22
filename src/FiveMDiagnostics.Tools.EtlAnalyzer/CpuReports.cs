namespace FiveMDiagnostics.Tools.EtlAnalyzer;

using Microsoft.Windows.EventTracing.Cpu;

/// <summary>
/// The CPU-side reports: who was on the processor, which thread inside the game was the bottleneck,
/// what code that thread was running, and who it was sharing a physical core with.
/// </summary>
internal static class CpuReports
{
    /// <summary>
    /// Per-process CPU, then a thread and module breakdown for the target process.
    /// </summary>
    /// <remarks>
    /// The thread breakdown is the report that matters. A frame budget is spent on one thread, so
    /// "FiveM used 4.8 cores" says nothing while "the main thread held 0.89 of a core" is the whole
    /// answer: at 0.89 cores a 60 fps cap needs 19.6 ms of CPU inside a 16.67 ms budget, and the game
    /// drops to 45 fps no matter how idle the other fifteen logical processors are.
    /// </remarks>
    public static void Cpu(TraceWindow window, string targetProcess, int topThreads)
    {
        Console.WriteLine();
        Console.WriteLine("  cores  process");
        foreach (var group in window.Samples
            .GroupBy(TraceWindow.ProcessName)
            .OrderByDescending(group => group.Count())
            .Take(15))
        {
            Console.WriteLine($"  {window.Cores(group.Count()),6:F2}  {group.Key}");
        }

        var target = window.Samples.Where(sample => Matches(sample, targetProcess)).ToArray();
        if (target.Length == 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  no samples for a process matching \"{targetProcess}\"");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {targetProcess}: {window.Cores(target.Length):F2} cores over {Threads(target)} threads");
        foreach (var group in target
            .GroupBy(TraceWindow.ThreadId)
            .OrderByDescending(group => group.Count())
            .Take(topThreads))
        {
            var modules = group
                .GroupBy(TraceWindow.ModuleName)
                .OrderByDescending(module => module.Count())
                .Take(5)
                .Select(module => $"{module.Key} {100.0 * module.Count() / group.Count():F0}%");
            Console.WriteLine($"    tid {group.Key,-8} {window.Cores(group.Count()),5:F2} cores  {string.Join(", ", modules)}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {targetProcess} modules, all threads:");
        foreach (var group in target
            .GroupBy(TraceWindow.ModuleName)
            .OrderByDescending(group => group.Count())
            .Take(15))
        {
            Console.WriteLine($"    {window.Cores(group.Count()),6:F3} cores  {100.0 * group.Count() / target.Length,5:F1}%  {group.Key}");
        }
    }

    /// <summary>
    /// Module breakdown for a single thread, in cores rather than percentages.
    /// </summary>
    /// <remarks>
    /// Percentages hide the finding. A thread that goes from 0.71 to 0.89 cores while its module mix
    /// stays flat in percentage terms has grown every module proportionally; the interesting case is
    /// the one where the percentages barely move but one module's <em>rate</em> multiplies. Run this
    /// against a healthy capture and a degraded one and diff the columns.
    /// </remarks>
    public static void Thread(TraceWindow window, int threadId)
    {
        var thread = window.Samples.Where(sample => TraceWindow.ThreadId(sample) == threadId).ToArray();
        if (thread.Length == 0)
        {
            Console.WriteLine($"  thread {threadId} has no samples in this trace");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  tid {threadId} ({TraceWindow.ProcessName(thread[0])}): {window.Cores(thread.Length):F3} cores");
        foreach (var group in thread
            .GroupBy(TraceWindow.ModuleName)
            .OrderByDescending(group => group.Count())
            .Take(15))
        {
            Console.WriteLine($"    {window.Cores(group.Count()),6:F3} cores  {100.0 * group.Count() / thread.Length,5:F1}%  {group.Key}");
        }
    }

    /// <summary>
    /// How often the thread was sharing a physical core with something else, and with what.
    /// </summary>
    /// <remarks>
    /// On an SMT machine two logical processors share one core's execution resources, so a latency
    /// critical thread loses 20–40 % of its throughput whenever its sibling is busy. That is invisible
    /// in every "CPU usage" number: the machine can report 60 % and still be starving the one thread a
    /// game's frame rate depends on.
    /// <para>
    /// The pairing assumed here is the usual <c>n</c>/<c>n^1</c> layout, and occupancy is inferred from
    /// whether the sibling produced a sample in the same millisecond. That makes this a strong
    /// indicator rather than a measurement — context switch data would be exact, but it is precisely
    /// what tends to be missing from a ring buffer trace.
    /// </para>
    /// </remarks>
    public static void Smt(TraceWindow window, int threadId)
    {
        var byProcessorAndMillisecond = new Dictionary<(long Millisecond, int Processor), ICpuSample>();
        foreach (var sample in window.Samples)
        {
            var key = ((long)sample.Timestamp.RelativeTimestamp.TotalMilliseconds, sample.Processor);
            byProcessorAndMillisecond[key] = sample;
        }

        var thread = window.Samples.Where(sample => TraceWindow.ThreadId(sample) == threadId).ToArray();
        if (thread.Length == 0)
        {
            Console.WriteLine($"  thread {threadId} has no samples in this trace");
            return;
        }

        var siblings = new Dictionary<string, int>(StringComparer.Ordinal);
        var contended = 0;
        foreach (var sample in thread)
        {
            var key = ((long)sample.Timestamp.RelativeTimestamp.TotalMilliseconds, sample.Processor ^ 1);
            if (!byProcessorAndMillisecond.TryGetValue(key, out var sibling))
            {
                continue;
            }

            contended++;
            var name = TraceWindow.ProcessName(sibling);
            siblings[name] = siblings.GetValueOrDefault(name) + 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  tid {threadId}: {window.Cores(thread.Length):F3} cores, ran on "
            + $"{thread.Select(sample => sample.Processor).Distinct().Count()} processors");
        Console.WriteLine($"  SMT sibling busy in {contended}/{thread.Length} = {100.0 * contended / thread.Length:F0}% of its sampled time");
        foreach (var sibling in siblings.OrderByDescending(entry => entry.Value).Take(10))
        {
            Console.WriteLine($"    {100.0 * sibling.Value / thread.Length,5:F1}%  {sibling.Key}");
        }
    }

    /// <summary>Per-bucket CPU for the busiest processes, to locate the moment inside the window.</summary>
    public static void Timeline(TraceWindow window, int bucketMilliseconds)
    {
        var processes = window.Samples
            .GroupBy(TraceWindow.ProcessName)
            .OrderByDescending(group => group.Count())
            .Take(8)
            .Select(group => group.Key)
            .ToArray();

        var start = window.Samples[0].Timestamp.RelativeTimestamp.TotalSeconds;
        var bucketSeconds = (decimal)bucketMilliseconds / 1000m;

        Console.WriteLine();
        Console.WriteLine("  ms    " + string.Join(" ", processes.Select(name => Truncate(name, 9).PadLeft(9))) + "     total");
        foreach (var bucket in window.Samples
            .GroupBy(sample => (int)((sample.Timestamp.RelativeTimestamp.TotalSeconds - start) / bucketSeconds))
            .OrderBy(group => group.Key))
        {
            var counts = processes.Select(name => bucket.Count(sample => TraceWindow.ProcessName(sample) == name));
            Console.WriteLine($"  {bucket.Key * bucketMilliseconds,5} "
                + string.Join(" ", counts.Select(count => count.ToString().PadLeft(9)))
                + $"     {bucket.Count(),5}");
        }
    }

    private static bool Matches(ICpuSample sample, string processName)
    {
        return TraceWindow.ProcessName(sample).Contains(processName, StringComparison.OrdinalIgnoreCase);
    }

    private static int Threads(IReadOnlyCollection<ICpuSample> samples)
    {
        return samples.Select(TraceWindow.ThreadId).Distinct().Count();
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }
}
