namespace FiveMDiagnostics.Tools.EtlAnalyzer;

using Microsoft.Windows.EventTracing.Disk;
using Microsoft.Windows.EventTracing.Memory;

/// <summary>
/// The reports that rule storage in or out: hard faults, physical disk activity, and the file system
/// traffic that produces neither.
/// </summary>
/// <remarks>
/// These three exist to be read together. A stall with thousands of file operations, no hard faults and
/// a handful of disk operations is not a storage problem however busy the file system looks — the
/// operations are metadata polls served entirely from cache, and the cost is CPU in the minifilter
/// stack, not latency on the drive.
/// </remarks>
internal static class IoReports
{
    public static void Report(
        TraceWindow window,
        IReadOnlyList<IHardFault> hardFaults,
        IReadOnlyList<IDiskActivity> diskActivity,
        IFileActivityDataSource fileActivity,
        string targetProcess)
    {
        HardFaults(hardFaults, targetProcess);
        Disk(diskActivity, targetProcess);
        Files(window, fileActivity, targetProcess);
    }

    private static void HardFaults(IReadOnlyList<IHardFault> faults, string targetProcess)
    {
        var target = faults.Count(fault => Matches(fault.FaultingProcess?.ImageName, targetProcess));
        Console.WriteLine();
        Console.WriteLine($"  hard faults: {faults.Count} total, {target} in {targetProcess}");
        foreach (var group in faults
            .GroupBy(fault => fault.FaultingProcess?.ImageName ?? "?")
            .OrderByDescending(group => group.Count())
            .Take(6))
        {
            Console.WriteLine($"    {group.Count(),6}  {group.Key}");
        }
    }

    private static void Disk(IReadOnlyList<IDiskActivity> activity, string targetProcess)
    {
        Console.WriteLine();
        Console.WriteLine($"  disk operations: {activity.Count} total");
        foreach (var group in activity
            .GroupBy(entry => entry.IssuingProcess?.ImageName ?? "?")
            .OrderByDescending(group => group.Count())
            .Take(8))
        {
            var megabytes = group.Sum(entry => (double)entry.Size.Bytes) / (1024 * 1024);
            var serviceMs = group.Sum(entry => (double)entry.DiskServiceDuration.TotalMilliseconds);
            Console.WriteLine($"    {group.Count(),6} ops  {megabytes,8:F1} MB  {serviceMs,8:F0} ms service  {group.Key}");
        }

        var target = activity.Where(entry => Matches(entry.IssuingProcess?.ImageName, targetProcess)).ToArray();
        foreach (var group in target
            .GroupBy(entry => entry.Path ?? "?")
            .OrderByDescending(group => group.Count())
            .Take(8))
        {
            var megabytes = group.Sum(entry => (double)entry.Size.Bytes) / (1024 * 1024);
            Console.WriteLine($"      {group.Count(),5}  {megabytes,7:F1} MB  {group.Key}");
        }
    }

    /// <summary>
    /// File system traffic, split by operation kind, by thread and by path.
    /// </summary>
    /// <remarks>
    /// The split by operation is what stops this being read as "the game is loading assets". A thread
    /// whose traffic is create/query/cleanup/close against one executable, with almost no reads, is
    /// polling for a file's metadata in a loop — anti-cheat behaviour, not streaming. The split by
    /// thread then decides whether anyone should care: the same loop is a frame killer on the render
    /// thread and merely a wasted core on a background one.
    /// </remarks>
    private static void Files(TraceWindow window, IFileActivityDataSource source, string targetProcess)
    {
        var operations = new List<(string Kind, IFileActivity Activity)>();
        Collect(operations, "Create", source.CreateFileObjectActivity);
        Collect(operations, "Cleanup", source.CleanupFileActivity);
        Collect(operations, "Close", source.CloseFileActivity);
        Collect(operations, "QueryInfo", source.QueryFileInformationActivity);
        Collect(operations, "Read", source.ReadFileActivity);
        Collect(operations, "Write", source.WriteFileActivity);
        Collect(operations, "SetInfo", source.SetFileInformationActivity);
        Collect(operations, "Flush", source.FlushFileActivity);
        Collect(operations, "DirEnum", source.EnumerateDirectoryActivity);
        Collect(operations, "FsControl", source.FileSystemControlActivity);

        var target = operations
            .Where(entry => Matches(entry.Activity.IssuingProcess?.ImageName, targetProcess))
            .ToArray();

        var perSecond = window.DurationSeconds > 0 ? target.Length / window.DurationSeconds : 0;
        Console.WriteLine();
        Console.WriteLine($"  file operations: {operations.Count} total, {target.Length} in {targetProcess} ({perSecond:F0}/s)");
        Console.WriteLine("    by process: " + string.Join(", ", operations
            .GroupBy(entry => entry.Activity.IssuingProcess?.ImageName ?? "?")
            .OrderByDescending(group => group.Count())
            .Take(8)
            .Select(group => $"{group.Key} {group.Count()}")));

        if (target.Length == 0)
        {
            return;
        }

        Console.WriteLine("    by kind: " + string.Join(", ", target
            .GroupBy(entry => entry.Kind)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} {group.Count()}")));

        Console.WriteLine("    by thread:");
        foreach (var group in target
            .GroupBy(entry => entry.Activity.IssuingThread?.Id ?? -1)
            .OrderByDescending(group => group.Count())
            .Take(8))
        {
            var paths = group
                .GroupBy(entry => FileName(entry.Activity.Path))
                .OrderByDescending(path => path.Count())
                .Take(3)
                .Select(path => $"{path.Key} ({path.Count()})");
            Console.WriteLine($"      tid {group.Key,-8} {group.Count(),6}  {string.Join(", ", paths)}");
        }

        Console.WriteLine("    top paths:");
        foreach (var group in target
            .GroupBy(entry => entry.Activity.Path ?? "?")
            .OrderByDescending(group => group.Count())
            .Take(12))
        {
            Console.WriteLine($"      {group.Count(),6}  {group.Key}");
        }
    }

    private static void Collect<TActivity>(
        List<(string Kind, IFileActivity Activity)> destination,
        string kind,
        IReadOnlyList<TActivity> activities)
        where TActivity : IFileActivity
    {
        foreach (var activity in activities)
        {
            destination.Add((kind, activity));
        }
    }

    private static bool Matches(string? candidate, string processName)
    {
        return candidate is not null && candidate.Contains(processName, StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(string? path)
    {
        return string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path);
    }
}
