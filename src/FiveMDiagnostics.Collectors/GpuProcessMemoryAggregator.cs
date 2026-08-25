using System.Globalization;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

/// <summary>
/// Turns raw <c>GPU Process Memory</c> counter instances into one row per process.
/// </summary>
/// <remarks>
/// Separated from the counter reader so the part that can be wrong is the part that can be tested. The
/// instance name is the only place the process id appears, several instances belong to the same
/// process, and getting either wrong produces a plausible-looking table attributing the game's memory
/// to something else.
/// </remarks>
public static class GpuProcessMemoryAggregator
{
    private const string PidPrefix = "pid_";

    /// <summary>
    /// Reads the process id out of an instance name such as
    /// <c>pid_29268_luid_0x00000000_0x0000c42a_phys_0</c>.
    /// </summary>
    /// <returns>Null when the name is not in that form, which is how a future counter revision arrives.</returns>
    public static int? ParseProcessId(string? instanceName)
    {
        if (instanceName is null || !instanceName.StartsWith(PidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = instanceName.AsSpan(PidPrefix.Length);
        var end = rest.IndexOf('_');
        var digits = end >= 0 ? rest[..end] : rest;

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) && processId > 0
            ? processId
            : null;
    }

    /// <summary>
    /// Sums the readings per process and returns the largest holders of dedicated memory.
    /// </summary>
    /// <param name="dedicated">Instance name to bytes resident in VRAM.</param>
    /// <param name="shared">Instance name to bytes the GPU reaches over PCIe.</param>
    /// <param name="processNames">Process id to name; ids missing from it are reported by id.</param>
    /// <param name="topCount">How many processes to keep, largest dedicated usage first.</param>
    /// <remarks>
    /// Summed rather than taken per instance because one process has an instance per adapter and per
    /// memory segment. On a laptop with switchable graphics, reporting the first instance found would
    /// show the integrated GPU's share and silently omit the discrete card's.
    /// </remarks>
    public static IReadOnlyList<GpuProcessMemoryUsage> Aggregate(
        IEnumerable<KeyValuePair<string, long>> dedicated,
        IEnumerable<KeyValuePair<string, long>> shared,
        IReadOnlyDictionary<int, string> processNames,
        int topCount)
    {
        var dedicatedByProcess = SumByProcess(dedicated);
        var sharedByProcess = SumByProcess(shared);

        return dedicatedByProcess.Keys
            .Union(sharedByProcess.Keys)
            .Select(processId => new GpuProcessMemoryUsage(
                processId,
                processNames.TryGetValue(processId, out var name) ? name : $"pid {processId}",
                dedicatedByProcess.GetValueOrDefault(processId),
                sharedByProcess.GetValueOrDefault(processId)))
            .Where(usage => usage.DedicatedBytes > 0 || usage.SharedBytes > 0)
            .OrderByDescending(usage => usage.DedicatedBytes)
            .ThenByDescending(usage => usage.SharedBytes)
            .Take(Math.Max(topCount, 1))
            .ToArray();
    }

    private static Dictionary<int, ulong> SumByProcess(IEnumerable<KeyValuePair<string, long>> readings)
    {
        var totals = new Dictionary<int, ulong>();
        foreach (var (instance, value) in readings)
        {
            // Negative readings mean the counter returned an error status for that instance; treating
            // them as zero keeps one bad instance from subtracting from a process's real total.
            if (value <= 0 || ParseProcessId(instance) is not { } processId)
            {
                continue;
            }

            totals[processId] = totals.GetValueOrDefault(processId) + (ulong)value;
        }

        return totals;
    }
}
