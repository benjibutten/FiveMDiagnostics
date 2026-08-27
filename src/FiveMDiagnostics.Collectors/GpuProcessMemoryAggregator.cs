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
    private const string AdapterMarker = "_luid_";
    private const string SegmentMarker = "_phys_";

    /// <summary>
    /// Reads the adapter out of an instance name such as
    /// <c>pid_29268_luid_0x00000000_0x0000c42a_phys_0</c>, dropping the memory segment.
    /// </summary>
    /// <returns>Null when the name carries no adapter, which is how a future counter revision arrives.</returns>
    public static string? ParseAdapter(string? instanceName)
    {
        if (instanceName is null)
        {
            return null;
        }

        var start = instanceName.IndexOf(AdapterMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        // Segments of the same adapter are the same card, so the segment is deliberately not part of
        // the key: they are summed, not separated.
        var rest = instanceName.AsSpan(start + 1);
        var end = rest.IndexOf(SegmentMarker, StringComparison.OrdinalIgnoreCase);
        var adapter = end >= 0 ? rest[..end] : rest;

        return adapter.IsEmpty ? null : adapter.ToString();
    }

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
    /// <param name="anchorProcessId">
    /// The process whose adapter the table is about — the game. Everything on other adapters is dropped.
    /// </param>
    /// <remarks>
    /// <para>
    /// Summed rather than taken per instance because one process has an instance per adapter and per
    /// memory segment. On a laptop with switchable graphics, reporting the first instance found would
    /// show the integrated GPU's share and silently omit the discrete card's.
    /// </para>
    /// <para>
    /// Summed only within one adapter, though, and that is a correction rather than a refinement. The
    /// first session to use this table reported obs64 holding 213 GB on a 10 GB card, climbing all
    /// evening, and it stood as "largest VRAM holder" in all 145 incident reports. Every other row was
    /// right: the same sample with obs64 removed tracked the adapter's own figure to within a quarter of
    /// a gigabyte for five hours. A total that large cannot be one card, so it was instances from more
    /// than one being added together — a capture program that hooks other processes ends up with them —
    /// and the fix is to decide which card the table is about. The game's is the only defensible answer,
    /// which is why the anchor is a process id and not a heuristic over the totals: the largest total is
    /// exactly what a runaway sum wins.
    /// </para>
    /// <para>
    /// More than one adapter is the normal case, not the laptop case. A single-GPU desktop enumerated 29
    /// counter instances across 24 processes and two distinct adapter LUIDs — the card, and whatever
    /// Windows keeps alongside it — with five processes present on both.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GpuProcessMemoryUsage> Aggregate(
        IEnumerable<KeyValuePair<string, long>> dedicated,
        IEnumerable<KeyValuePair<string, long>> shared,
        IReadOnlyDictionary<int, string> processNames,
        int topCount,
        int? anchorProcessId = null)
    {
        var dedicatedReadings = Parse(dedicated);
        var sharedReadings = Parse(shared);
        var adapter = SelectAdapter(dedicatedReadings, sharedReadings, anchorProcessId);

        var dedicatedByProcess = SumByProcess(dedicatedReadings, adapter, out var instanceCounts);
        var sharedByProcess = SumByProcess(sharedReadings, adapter, out _);

        return dedicatedByProcess.Keys
            .Union(sharedByProcess.Keys)
            .Select(processId => Build(
                processId,
                processNames.TryGetValue(processId, out var name) ? name : $"pid {processId}",
                dedicatedByProcess.GetValueOrDefault(processId),
                sharedByProcess.GetValueOrDefault(processId),
                instanceCounts.GetValueOrDefault(processId),
                dedicatedReadings,
                sharedReadings))
            .Where(usage => usage.DedicatedBytes > 0 || usage.SharedBytes > 0)
            .OrderByDescending(usage => usage.DedicatedBytes)
            .ThenByDescending(usage => usage.SharedBytes)
            .Take(Math.Max(topCount, 1))
            .ToArray();
    }

    /// <summary>
    /// Picks the adapter the table describes: the one the anchor process holds the most memory on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Falls back to the adapter with the largest total when there is no anchor, or when the anchor
    /// holds nothing — a session that starts before the game has allocated anything, and the tests that
    /// call this without a game at all. That fallback is the behaviour a runaway instance sum defeats,
    /// so it is the fallback and not the rule. Null means "every instance", which is what a counter set
    /// that stops naming adapters would produce and is better than an empty table.
    /// </para>
    /// <para>
    /// The anchor is located using both dedicated and shared readings, because a process can hold one
    /// without the other. Deciding on dedicated alone would send a game that has shared memory and no
    /// dedicated yet to whichever adapter something else was busiest on, and then filter the game's own
    /// memory out of its own table. The fallback ranking stays on dedicated: the question it answers is
    /// which adapter is the card, and shared memory is by definition not on it.
    /// </para>
    /// </remarks>
    private static string? SelectAdapter(
        IReadOnlyList<Reading> dedicated,
        IReadOnlyList<Reading> shared,
        int? anchorProcessId)
    {
        var byAdapter = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var byAnchor = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        foreach (var reading in dedicated)
        {
            if (reading.Adapter is not { } adapter)
            {
                continue;
            }

            byAdapter[adapter] = byAdapter.GetValueOrDefault(adapter) + reading.Value;
            if (reading.ProcessId == anchorProcessId)
            {
                byAnchor[adapter] = byAnchor.GetValueOrDefault(adapter) + reading.Value;
            }
        }

        foreach (var reading in shared)
        {
            if (reading.Adapter is { } adapter && reading.ProcessId == anchorProcessId)
            {
                byAnchor[adapter] = byAnchor.GetValueOrDefault(adapter) + reading.Value;
            }
        }

        return Largest(byAnchor) ?? Largest(byAdapter);

        static string? Largest(Dictionary<string, ulong> totals)
        {
            return totals.Count == 0
                ? null
                : totals.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        }
    }

    private static Dictionary<int, ulong> SumByProcess(
        IReadOnlyList<Reading> readings,
        string? adapter,
        out Dictionary<int, int> instanceCounts)
    {
        var totals = new Dictionary<int, ulong>();
        instanceCounts = [];

        foreach (var reading in readings)
        {
            // An instance with no adapter in its name is kept whatever the selection: dropping it would
            // lose the whole table on a counter revision that stops naming them.
            if (adapter is not null && reading.Adapter is not null && !string.Equals(reading.Adapter, adapter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totals[reading.ProcessId] = totals.GetValueOrDefault(reading.ProcessId) + reading.Value;
            instanceCounts[reading.ProcessId] = instanceCounts.GetValueOrDefault(reading.ProcessId) + 1;
        }

        return totals;
    }

    private static List<Reading> Parse(IEnumerable<KeyValuePair<string, long>> readings)
    {
        var parsed = new List<Reading>();
        foreach (var (instance, value) in readings)
        {
            // Negative readings mean the counter returned an error status for that instance; treating
            // them as zero keeps one bad instance from subtracting from a process's real total.
            if (value <= 0 || ParseProcessId(instance) is not { } processId)
            {
                continue;
            }

            parsed.Add(new Reading(processId, ParseAdapter(instance), (ulong)value, instance));
        }

        return parsed;
    }

    /// <summary>
    /// Dedicated bytes across <em>every</em> process on the table's adapter, before the top-N cut.
    /// </summary>
    /// <remarks>
    /// <see cref="Aggregate"/> returns the largest holders only, so summing what it returns answers "how
    /// much do the biggest twenty-five hold" and not "how much is accounted for". The difference matters
    /// exactly where the reconciliation against the adapter's own figure does: double counting in a
    /// process that never reaches the list would be invisible to a check summing the list.
    /// <para>
    /// Impossible readings are excluded here for the same reason they are excluded from the table — one
    /// runaway row would swamp the comparison it is meant to inform.
    /// </para>
    /// </remarks>
    public static ulong TotalDedicatedBytes(
        IEnumerable<KeyValuePair<string, long>> dedicated,
        IEnumerable<KeyValuePair<string, long>> shared,
        int? anchorProcessId = null)
    {
        var dedicatedReadings = Parse(dedicated);
        var sharedReadings = Parse(shared);
        var adapter = SelectAdapter(dedicatedReadings, sharedReadings, anchorProcessId);

        return SumByProcess(dedicatedReadings, adapter, out _)
            .Values
            .Where(bytes => bytes <= GpuProcessMemoryUsage.ImplausibleDedicatedBytes)
            .Aggregate(0UL, (total, bytes) => total + bytes);
    }

    /// <summary>
    /// Builds one process's row, attaching the raw instances behind it when the total is impossible.
    /// </summary>
    /// <remarks>
    /// The instances are the diagnosis and they are only affordable on the rows that need one. Every
    /// instance the process reported is attached, including the ones the adapter filter dropped: which
    /// adapters it is spread across is exactly what the impossible row is being asked about, and a
    /// listing that had already been filtered could not answer it.
    /// </remarks>
    private static GpuProcessMemoryUsage Build(
        int processId,
        string processName,
        ulong dedicatedBytes,
        ulong sharedBytes,
        int instanceCount,
        IReadOnlyList<Reading> dedicatedReadings,
        IReadOnlyList<Reading> sharedReadings)
    {
        var usage = new GpuProcessMemoryUsage(processId, processName, dedicatedBytes, sharedBytes, instanceCount);
        if (!usage.IsImplausible)
        {
            return usage;
        }

        var shared = sharedReadings
            .Where(reading => reading.ProcessId == processId)
            .ToDictionary(reading => reading.InstanceName, reading => reading.Value, StringComparer.OrdinalIgnoreCase);

        var instances = dedicatedReadings
            .Where(reading => reading.ProcessId == processId)
            .Select(reading => new GpuProcessMemoryInstance(
                reading.InstanceName,
                reading.Adapter,
                reading.Value,
                shared.GetValueOrDefault(reading.InstanceName)))
            .OrderByDescending(instance => instance.DedicatedBytes)
            .ToArray();

        return usage with { Instances = instances };
    }

    private readonly record struct Reading(int ProcessId, string? Adapter, ulong Value, string InstanceName);
}
