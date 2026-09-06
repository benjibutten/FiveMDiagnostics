namespace FiveMDiagnostics.Integrations.Etw;

using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

/// <summary>
/// Measures how long the disks took to answer, one volume at a time, and names the slowest operations.
/// </summary>
/// <remarks>
/// <para>
/// Every other storage measurement in this app is a rate: megabytes a second, operations a second, a
/// queue length averaged over a machine. None of them can see a disk that is idle and slow, and that is
/// the disk that cost the evening of 5 September. C: answered a 64 kB read in 0.04 ms all night while
/// D: — which held nothing but <c>pagefile.sys</c> — answered the same read in 431–454 ms, ten times in
/// ten traces. Averaged together the two volumes read as an unremarkable disk.
/// </para>
/// <para>
/// So the unit is one operation on one volume, and the volume is the grouping. The kernel supplies the
/// service time the drive itself reports, the file object's path and the thread that issued it; the
/// three together turn "storage was slow" into "a 64 kB read out of D:\pagefile.sys took 454 ms and the
/// game's main thread was off the processor for exactly that long".
/// </para>
/// <para>
/// Hard faults are collected here rather than beside them because they are the other half of the same
/// sentence. A paging read is only a stall when a thread was waiting for it, and the hard fault event is
/// what says which thread that was.
/// </para>
/// </remarks>
internal sealed class DiskVolumeAttribution
{
    /// <summary>Slowest operations detailed by name, which is as many as a sentence can carry.</summary>
    private const int ReportedOperations = 5;

    /// <summary>Paths that mean the read was Windows fetching a page back, not a program reading a file.</summary>
    private const string PagingFileName = "pagefile.sys";

    private readonly Dictionary<ulong, string> _pathsByFileKey = [];
    private readonly Dictionary<int, string> _driveLetterByDisk = [];
    private readonly List<Operation> _operations = [];
    private readonly List<HardFault> _hardFaults = [];

    /// <summary>
    /// Notes a file object's path. The disk events carry only the key, and the name table that resolves
    /// it is emitted at rundown, so both have to be collected and joined at the end.
    /// </summary>
    /// <remarks>
    /// The whole table is kept rather than only the keys the disk events used, because a name can arrive
    /// after the operation that needs it. It runs to some ninety thousand paths on a retained window of
    /// half a minute — a few tens of megabytes held for the length of one parse, against an ETL of
    /// several hundred.
    /// </remarks>
    public void OnFileName(FileIONameTraceData data)
    {
        if (data.FileKey != 0 && !string.IsNullOrEmpty(data.FileName))
        {
            _pathsByFileKey[data.FileKey] = data.FileName;
        }
    }

    /// <summary>
    /// Notes which drive letter a physical disk carries, for operations whose path never resolved.
    /// </summary>
    public void OnLogicalDisk(SystemConfigLogDiskTraceData data)
    {
        // Several partitions can share a disk, and the first one is as good an answer as any: the
        // fallback exists so an unresolved operation is still attributable to a piece of hardware, not
        // to be precise about which partition of it.
        if (!string.IsNullOrWhiteSpace(data.DriveLetterString))
        {
            _driveLetterByDisk.TryAdd(data.DiskNumber, data.DriveLetterString);
        }
    }

    public void OnDiskOperation(DiskIOTraceData data, bool isRead)
    {
        _operations.Add(new Operation(
            data.FileKey,
            data.DiskNumber,
            data.ThreadID,
            data.TransferSize,
            data.DiskServiceTimeMSec,
            isRead));
    }

    /// <summary>
    /// Records one hard fault. The event leaves <c>ProcessID</c> at -1 and names the faulting thread, so
    /// attribution goes through the thread map like everything else here.
    /// </summary>
    public void OnHardFault(MemoryHardFaultTraceData data)
    {
        _hardFaults.Add(new HardFault(data.ThreadID, data.FileKey, data.ElapsedTimeMSec));
    }

    public DiskVolumeSummary? Summarize(CpuSampleAttribution cpu)
    {
        if (_operations.Count == 0)
        {
            return null;
        }

        var detailed = _operations
            .Select(operation =>
            {
                var path = _pathsByFileKey.GetValueOrDefault(operation.FileKey, string.Empty);
                var processId = cpu.ProcessIdForThread(operation.ThreadId);
                return new DiskOperation(
                    VolumeOf(path, operation.DiskNumber),
                    path,
                    operation.ServiceMs,
                    operation.Bytes,
                    operation.IsRead,
                    processId >= 0 ? cpu.Name(processId) : "okänd process",
                    processId >= 0 && cpu.IsGameProcess(processId));
            })
            .ToArray();

        var volumes = detailed
            .GroupBy(operation => operation.Volume)
            .Select(group =>
            {
                var sorted = group.Select(operation => operation.ServiceMs).Order().ToArray();
                return new DiskVolumeStats(
                    group.Key,
                    sorted.Length,
                    sorted[sorted.Length / 2],
                    sorted[^1],
                    group.Sum(operation => (double)operation.Bytes) / (1024 * 1024));
            })
            .OrderByDescending(volume => volume.MaxMs)
            .ToArray();

        var paging = detailed
            .Where(operation => IsPagingFile(operation.Path))
            .OrderByDescending(operation => operation.ServiceMs)
            .FirstOrDefault();

        var gameHardFaults = _hardFaults.Count(fault => cpu.IsGameThread(fault.ThreadId));

        return new DiskVolumeSummary(
            volumes,
            detailed.OrderByDescending(operation => operation.ServiceMs).Take(ReportedOperations).ToArray(),
            _hardFaults.Count,
            gameHardFaults,
            paging,
            _hardFaults.Count == 0 ? 0 : _hardFaults.Max(fault => fault.ElapsedMs));
    }

    /// <summary>
    /// The volume an operation went to: the path's own drive letter, the disk's letter when the path
    /// never resolved, and the bare disk number when neither is known.
    /// </summary>
    private string VolumeOf(string path, int diskNumber)
    {
        if (path.Length >= 2 && path[1] == ':')
        {
            return path[..2].ToUpperInvariant();
        }

        return _driveLetterByDisk.TryGetValue(diskNumber, out var letter) ? letter : $"disk {diskNumber}";
    }

    private static bool IsPagingFile(string path)
    {
        return path.EndsWith(PagingFileName, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Operation(
        ulong FileKey,
        int DiskNumber,
        int ThreadId,
        int Bytes,
        double ServiceMs,
        bool IsRead);

    private readonly record struct HardFault(int ThreadId, ulong FileKey, double ElapsedMs);
}

/// <summary>What one volume did in the retained window.</summary>
/// <param name="MedianMs">
/// The figure that separates a slow disk from a busy one. A drive that answers its median operation in
/// tenths of a millisecond is not the reason anything stalled, however many operations it made.
/// </param>
internal sealed record DiskVolumeStats(
    string Volume,
    int Operations,
    double MedianMs,
    double MaxMs,
    double Megabytes);

/// <summary>One disk operation, named well enough to be looked up in the trace by hand.</summary>
internal sealed record DiskOperation(
    string Volume,
    string Path,
    double ServiceMs,
    int Bytes,
    bool IsRead,
    string ProcessName,
    bool IsGameProcess)
{
    public string Describe()
    {
        var kind = IsRead ? "läsning" : "skrivning";
        var where = string.IsNullOrEmpty(Path) ? $"{Volume} (filen namnges inte i spåret)" : Path;
        var who = IsGameProcess ? "spelet" : ProcessName;
        return $"{ServiceMs:F0} ms för en {Bytes / 1024d:F0} kB-{kind} ur {where}, utfärdad av {who}";
    }
}

/// <param name="SlowestPagingOperation">
/// The slowest operation against a paging file, or null when nothing touched one. Held apart from the
/// slowest operation overall because it is the one that can be paired with a thread's wait: a paging
/// read is by construction a thread standing still until it finishes.
/// </param>
/// <param name="GameHardFaults">
/// Hard faults taken by a thread of the game. This is the count that decides whether a slow paging read
/// is the game's problem or somebody else's.
/// </param>
internal sealed record DiskVolumeSummary(
    IReadOnlyList<DiskVolumeStats> Volumes,
    IReadOnlyList<DiskOperation> Slowest,
    int HardFaults,
    int GameHardFaults,
    DiskOperation? SlowestPagingOperation,
    double SlowestHardFaultMs)
{
    /// <summary>
    /// Operations a volume needs before its median is quoted as the fast comparison.
    /// </summary>
    /// <remarks>
    /// The comparison exists to say "the same read costs nothing over here", and three operations is not
    /// a claim about what a volume normally does. C: routinely makes thousands in a retained window, so
    /// this excludes nothing that matters.
    /// </remarks>
    private const int MinimumOperationsForComparison = 20;

    /// <summary>The volume whose median says what this machine's storage costs when it is healthy.</summary>
    public DiskVolumeStats? FastestVolume => Volumes
        .Where(volume => volume.Operations >= MinimumOperationsForComparison)
        .OrderBy(volume => volume.MedianMs)
        .FirstOrDefault();

    public DiskOperation? SlowestOperation => Slowest.Count > 0 ? Slowest[0] : null;

    /// <summary>
    /// How close a disk operation and a thread's wait have to be before they are called the same event.
    /// </summary>
    /// <remarks>
    /// The drive reports its own service time and the scheduler reports the interval the thread was off
    /// the processor; on 5 September those read 454.116 and 454.1 ms for the same stall. A few percent
    /// covers the wake-up latency between the two without letting an unrelated wait borrow the disk's
    /// number.
    /// </remarks>
    private const double MatchingWaitTolerance = 0.05;

    /// <param name="gameThreadMaxWaitMs">
    /// The longest the game thread was off the processor, when the trace measured it. Quoted only when
    /// it matches the slowest operation: that pairing is what makes a storage verdict provable rather
    /// than plausible, because it is the same millisecond figure arriving from two unrelated streams.
    /// </param>
    public string Describe(double? gameThreadMaxWaitMs)
    {
        if (SlowestOperation is not { } slowest)
        {
            return string.Empty;
        }

        var comparison = FastestVolume is { } fastest && fastest.Volume != slowest.Volume
            ? $" {fastest.Volume} levererar samma sorts operation på {fastest.MedianMs:F2} ms."
            : string.Empty;

        var perVolume = string.Join(
            ", ",
            Volumes.Select(volume =>
                $"{volume.Volume} {volume.Operations} ops, median {volume.MedianMs:F2} ms, max {volume.MaxMs:F0} ms"));

        var faults = HardFaults == 0
            ? string.Empty
            : $" Hårda sidfel i spåret: {HardFaults}, varav {GameHardFaults} i spelet"
                + $" (längsta {SlowestHardFaultMs:F0} ms).";

        var pairing = Matches(gameThreadMaxWaitMs, slowest.ServiceMs)
            ? $" Spelets huvudtråd låg av processorn {gameThreadMaxWaitMs:F1} ms i samma sekund."
            : string.Empty;

        return $"Disk: långsammaste diskoperation i spåret: {slowest.Describe()}.{comparison}{pairing}"
            + $" Per volym: {perVolume}.{faults}";
    }

    /// <summary>Whether a wait and a service time describe the same stall.</summary>
    public static bool Matches(double? waitMs, double serviceMs)
    {
        return waitMs is { } wait
            && serviceMs > 0
            && Math.Abs(wait - serviceMs) <= serviceMs * MatchingWaitTolerance;
    }
}
