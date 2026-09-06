using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

public sealed class SystemTelemetryCollector : ITelemetryCollector, IDisposable
{
    /// <summary>
    /// Samples taken before the collector says whether the disk counters are actually producing values.
    /// A counter that constructs successfully and then returns nothing on every read is, as far as the
    /// analysis is concerned, indistinguishable from one that was never created — and both used to be
    /// completely silent.
    /// </summary>
    private const int SamplesBeforeYieldReport = 20;

    /// <summary>
    /// CPU a process outside the interactive session has to be using before it is worth listing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session 0 was skipped outright for eleven sessions, and that is where every Windows service
    /// lives: Search indexing, Defender, SysMain, Windows Update, BITS. On 2 September the game lost a
    /// 2 145 ms frame while <c>SearchIndexer.exe</c> held a full core and made 48 000 file operations a
    /// second against its own database on the same disk as the game's cache — and the incident recorded
    /// an empty suspect list, because none of that was in a session the collector would look at. Every
    /// "external process interference" verdict the app has ever reached was drawn from the interactive
    /// session alone.
    /// </para>
    /// <para>
    /// A floor rather than a whitelist, because the population is large and mostly idle: a machine runs
    /// well over a hundred services and two or three of them are ever doing anything. Two percent of one
    /// machine is a fifth of a core on eight, which is below anything that has ever mattered and above
    /// the noise of a service waking up to poll something.
    /// </para>
    /// </remarks>
    private const double ServiceCpuFloorPercent = 2;

    /// <summary>Disk traffic that earns a service a row on its own, at five megabytes a second.</summary>
    private const long ServiceIoFloorBytesPerSecond = 5L * 1024 * 1024;

    private readonly PerformanceCounter? _totalCpuCounter;
    private readonly IReadOnlyList<PerformanceCounter> _perCoreCounters;
    private readonly IReadOnlyList<DiskInstanceProbe> _diskInstances;
    private readonly CounterProbe _hardFaultPagesProbe;
    private readonly IReadOnlyList<CounterProbe> _diskProbes;
    private readonly Dictionary<int, ProcessMetricSnapshot> _previousSnapshots = new();
    private readonly TimeSpan _processSampleInterval = TimeSpan.FromSeconds(2);
    private readonly int _currentSessionId = Process.GetCurrentProcess().SessionId;

    private DateTimeOffset _lastProcessSampleUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<ProcessActivity> _cachedTopCpu = [];
    private IReadOnlyList<ProcessActivity> _cachedTopDisk = [];

    /// <summary>Set once the process table had to be read the slow way, so the session log can say so.</summary>
    private bool _processTableFallback;
    private bool _reportedProcessTableFallback;
    private string? _cpuCounterFailure;
    private long _samplesTaken;
    private bool _reportedYield;

    public SystemTelemetryCollector()
    {
        try
        {
            _totalCpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            var category = new PerformanceCounterCategory("Processor");
            _perCoreCounters = category
                .GetInstanceNames()
                .Where(name => name != "_Total")
                .OrderBy(name => int.TryParse(name, out var numeric) ? numeric : int.MaxValue)
                .Select(name => new PerformanceCounter("Processor", "% Processor Time", name, true))
                .ToArray();

            _ = _totalCpuCounter.NextValue();
            foreach (var counter in _perCoreCounters)
            {
                _ = counter.NextValue();
            }
        }
        catch (Exception ex)
        {
            _perCoreCounters = [];
            _cpuCounterFailure = ex.Message;
        }

        // Deliberately outside the CPU try block: a failing Processor category used to skip the disk
        // counters entirely, and that outcome then looked exactly like disk counters which had been
        // created and simply had nothing to report.
        _diskInstances = CreateDiskInstances();
        _hardFaultPagesProbe = CounterProbe.Create("Hard faults", "Memory", "Pages Input/sec", null);
        _diskProbes = [.. _diskInstances.SelectMany(instance => instance.Probes), _hardFaultPagesProbe];
    }

    /// <summary>
    /// One latency and queue probe per physical disk, rather than one pair over all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>_Total</c> is an average weighted by operation count, and that is the wrong statistic for the
    /// only disk fault this app has ever had to find. Through the evening of 5 September C: served
    /// thousands of operations at 0.1 ms while D: served tens at 20–450 ms; the average over both landed
    /// far under the 20 ms bar in <c>AddDiskHypothesis</c>, so the rule could never fire and the counter
    /// measured the wrong thing all evening.
    /// </para>
    /// <para>
    /// Falls back to <c>_Total</c> when the instances cannot be enumerated, because one wrong statistic
    /// still beats no measurement — and the availability line then names the instance it is reading, so
    /// the fallback is visible in the journal rather than silent.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<DiskInstanceProbe> CreateDiskInstances()
    {
        string[] instances;
        try
        {
            instances = new PerformanceCounterCategory("PhysicalDisk")
                .GetInstanceNames()
                .Where(name => !name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        // Anything at all, like CounterProbe.Create. This runs from the constructor, so a throw here
        // takes the app down at startup — on exactly the machine the fallback below was written for, one
        // whose counter registry is damaged and whose enumeration can fail in ways nothing documents.
        catch (Exception)
        {
            instances = [];
        }

        if (instances.Length == 0)
        {
            instances = ["_Total"];
        }

        return instances.Select(name => new DiskInstanceProbe(name)).ToArray();
    }

    public string Name => "SystemTelemetry";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        ReportCounterAvailability(context);

        while (!cancellationToken.IsCancellationRequested)
        {
            var timestamp = context.UtcNow();
            var (memoryPressure, availableMb) = ReadMemorySnapshot();
            var hasTarget = context.ProcessResolver.TryGetTargetProcess() is not null;
            var (topCpu, topDisk) = hasTarget ? SampleProcesses(timestamp) : ([], []);
            var disk = ReadWorstDisk();

            await context.Writer.WriteAsync(
                new SystemTelemetrySample(
                    timestamp,
                    ReadCpuUsage(_totalCpuCounter),
                    ReadPerCoreCpuUsage(),
                    memoryPressure,
                    availableMb,
                    topCpu,
                    topDisk,
                    disk.LatencyMs,
                    disk.QueueLength,
                    _hardFaultPagesProbe.Read(),
                    disk.Instance),
                cancellationToken).ConfigureAwait(false);

            _samplesTaken++;
            ReportCounterYieldIfDue(context);
            ReportProcessTableFallbackIfDue(context);

            await Task.Delay(context.Settings.SystemPollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _totalCpuCounter?.Dispose();
        foreach (var counter in _perCoreCounters)
        {
            counter.Dispose();
        }

        foreach (var probe in _diskProbes)
        {
            probe.Dispose();
        }
    }

    /// <summary>
    /// Says at session start which disk counters exist, and why the missing ones do not.
    /// </summary>
    /// <remarks>
    /// Without this the correlation engine's storage fallback is the only trace a broken counter leaves
    /// anywhere, and it is an indirect one: the session log shows a disk verdict but never says the
    /// measurements behind it were absent. Reading a session afterwards has to be able to answer
    /// "did the counters work" from the log alone.
    /// </remarks>
    private void ReportCounterAvailability(CollectorContext context)
    {
        if (_cpuCounterFailure is not null)
        {
            context.StatusSink.Report(
                StatusLevel.Warning,
                Name,
                $"CPU-counters kunde inte skapas: {_cpuCounterFailure}. Total och per-core CPU rapporteras som 0 för hela sessionen.");
        }

        var unavailable = _diskProbes.Where(probe => !probe.IsAvailable).ToArray();
        if (unavailable.Length == 0)
        {
            // The instances by name, because which disks are being measured is the part that matters and
            // the counter paths are six near-identical strings.
            context.StatusSink.Report(
                StatusLevel.Info,
                Name,
                $"Diskcounters aktiva per fysisk disk: {string.Join(", ", _diskInstances.Select(instance => instance.Name))}. "
                + "Latens och kö rapporteras för den disk som svarar långsammast, inte som medelvärde över alla. "
                + $"Dessutom {_hardFaultPagesProbe.CounterPath}.");
            return;
        }

        foreach (var probe in unavailable)
        {
            context.StatusSink.Report(
                StatusLevel.Warning,
                Name,
                $"{probe.Label} ({probe.CounterPath}) kunde inte skapas: {probe.CreationError}. "
                + "Fältet lämnas tomt i telemetrin, och en disk-hypotes som byggs utan det får takad konfidens.");
        }

        if (unavailable.Length == _diskProbes.Count)
        {
            context.StatusSink.Report(
                StatusLevel.Error,
                Name,
                "Inga disk- eller hard fault-counters är tillgängliga. Kör 'lodctr /R' förhöjt för att bygga om "
                + "performance counter-registret; till dess kan en disk-stall varken bekräftas eller uteslutas.");
        }
    }

    /// <summary>
    /// Reports counters that were created but never produced a value — the failure the constructor
    /// cannot see, where the category exists, the instance resolves, and every read throws.
    /// </summary>
    private void ReportCounterYieldIfDue(CollectorContext context)
    {
        if (_reportedYield || _samplesTaken < SamplesBeforeYieldReport)
        {
            return;
        }

        _reportedYield = true;

        var mute = _diskProbes.Where(probe => probe.IsAvailable && probe.ValueCount == 0).ToArray();
        if (mute.Length == 0)
        {
            return;
        }

        context.StatusSink.Report(
            StatusLevel.Warning,
            Name,
            $"Följande counters skapades men gav inget värde på {_samplesTaken} avläsningar: "
            + string.Join(", ", mute.Select(probe => $"{probe.Label} ({probe.LastReadError ?? "läsningen returnerade inget"})"))
            + ". Behandla dem som saknade.");
    }

    /// <summary>
    /// Reads every physical disk and keeps the one that answered slowest.
    /// </summary>
    /// <remarks>
    /// Every instance is read on every poll, not only the one that turns out to be worst: a performance
    /// counter reports the delta since its own last read, so skipping an instance for a poll would make
    /// its next reading cover two intervals.
    /// </remarks>
    private (double? LatencyMs, double? QueueLength, string? Instance) ReadWorstDisk()
    {
        (double? LatencyMs, double? QueueLength, string? Instance) worst = (null, null, null);

        foreach (var instance in _diskInstances)
        {
            var latency = instance.Latency.Read(multiplier: 1000);
            var queue = instance.Queue.Read();

            if (latency is { } value && value > (worst.LatencyMs ?? -1))
            {
                worst = (value, queue, instance.Name);
            }
            else if (worst.Instance is null && queue is not null)
            {
                // Nothing has reported a latency yet. The queue still says something, and dropping it
                // because its own instance had no latency reading is how a half-working counter set
                // becomes no disk data at all.
                worst = (null, queue, instance.Name);
            }
        }

        return worst;
    }

    private static double ReadCpuUsage(PerformanceCounter? counter)
    {
        try
        {
            return counter is null ? 0 : Math.Round(counter.NextValue(), 1);
        }
        catch
        {
            return 0;
        }
    }

    private IReadOnlyDictionary<string, double> ReadPerCoreCpuUsage()
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var counter in _perCoreCounters)
        {
            try
            {
                values[counter.InstanceName] = Math.Round(counter.NextValue(), 1);
            }
            catch
            {
                values[counter.InstanceName] = 0;
            }
        }

        return values;
    }

    private static (double MemoryPressurePercent, ulong AvailableMb) ReadMemorySnapshot()
    {
        try
        {
            var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
            if (!WindowsInterop.GetPerformanceInfo(out info, Marshal.SizeOf<PerformanceInformation>()))
            {
                return (0, 0);
            }

            var commitPercent = info.CommitLimit == 0
                ? 0
                : (double)info.CommitTotal / info.CommitLimit * 100;

            var availableBytes = (ulong)info.PhysicalAvailable * (ulong)info.PageSize;
            return (Math.Round(commitPercent, 1), availableBytes / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Says once that the process table is being read the expensive, incomplete way.
    /// </summary>
    /// <remarks>
    /// Worth a line because both consequences are invisible otherwise: the app costs a third of a core
    /// instead of nothing, and the table is missing every process it could not open — which on an
    /// unelevated session is half of them, services included.
    /// </remarks>
    private void ReportProcessTableFallbackIfDue(CollectorContext context)
    {
        if (!_processTableFallback || _reportedProcessTableFallback)
        {
            return;
        }

        _reportedProcessTableFallback = true;
        context.StatusSink.Report(
            StatusLevel.Warning,
            Name,
            "Processtabellen kunde inte läsas med ett systemanrop och läses i stället process för process. "
            + "Det kostar omkring hundra gånger mer CPU i appen själv, och tabellen saknar de processer "
            + "appen inte får öppna — typiskt alla Windowstjänster. Topplistorna över CPU och disk är "
            + "därför ofullständiga den här sessionen.");
    }

    private (IReadOnlyList<ProcessActivity> TopCpu, IReadOnlyList<ProcessActivity> TopDisk) SampleProcesses(DateTimeOffset timestamp)
    {
        if (timestamp - _lastProcessSampleUtc < _processSampleInterval)
        {
            return (_cachedTopCpu, _cachedTopDisk);
        }

        var rows = ProcessTableReader.TryRead(timestamp);
        if (rows is null)
        {
            _processTableFallback = true;
            rows = ReadThroughHandles(timestamp);
        }

        var samples = new List<ProcessActivity>(16);
        var seen = new HashSet<int>(rows.Count);

        foreach (var row in rows)
        {
            var snapshot = row.Snapshot;
            seen.Add(snapshot.ProcessId);

            var cpu = 0d;
            var ioPerSecond = 0L;

            if (_previousSnapshots.TryGetValue(snapshot.ProcessId, out var previous))
            {
                cpu = ProcessMetricsReader.ComputeCpuPercent(snapshot, previous);
                ioPerSecond = ProcessMetricsReader.ComputeReadBytesPerSecond(snapshot, previous)
                    + ProcessMetricsReader.ComputeWriteBytesPerSecond(snapshot, previous);
            }

            _previousSnapshots[snapshot.ProcessId] = snapshot;

            // A machine runs well over a hundred services and two or three of them are ever doing
            // anything. The floor keeps the idle ones out of the top lists without hiding a busy one.
            var isService = row.SessionId != _currentSessionId;
            if (isService && cpu < ServiceCpuFloorPercent && ioPerSecond < ServiceIoFloorBytesPerSecond)
            {
                continue;
            }

            samples.Add(new ProcessActivity(snapshot.ProcessName, snapshot.ProcessId, cpu, ioPerSecond, isService));
        }

        foreach (var staleProcessId in _previousSnapshots.Keys.Where(processId => !seen.Contains(processId)).ToArray())
        {
            _previousSnapshots.Remove(staleProcessId);
        }

        _cachedTopCpu = samples.OrderByDescending(item => item.CpuPercent).Take(5).ToArray();
        _cachedTopDisk = samples.OrderByDescending(item => item.IoBytesPerSecond).Take(5).ToArray();
        _lastProcessSampleUtc = timestamp;

        return (_cachedTopCpu, _cachedTopDisk);
    }

    /// <summary>
    /// The old sweep, kept as the answer for a machine where the system call is unavailable.
    /// </summary>
    /// <remarks>
    /// One handle and several system calls per process, which is why it is no longer the first choice —
    /// 168 ms against 2.6 ms for the same 265 processes. It is also blind to anything it cannot open, so
    /// a session that lands here has a thinner process table and the log says so.
    /// </remarks>
    private List<ProcessTableRow> ReadThroughHandles(DateTimeOffset timestamp)
    {
        var rows = new List<ProcessTableRow>(200);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int sessionId;
                try
                {
                    sessionId = process.SessionId;
                }
                catch
                {
                    continue;
                }

                if (ProcessMetricsReader.TryRead(process, timestamp, out var snapshot))
                {
                    rows.Add(new ProcessTableRow(snapshot, sessionId));
                }
            }
        }

        return rows;
    }

    /// <summary>The latency and queue counters of one physical disk, kept together under its name.</summary>
    private sealed class DiskInstanceProbe
    {
        public DiskInstanceProbe(string name)
        {
            Name = name;
            Latency = CounterProbe.Create($"Disklatens {name}", "PhysicalDisk", "Avg. Disk sec/Transfer", name);
            Queue = CounterProbe.Create($"Diskkö {name}", "PhysicalDisk", "Current Disk Queue Length", name);
        }

        /// <summary>The counter's own instance name, e.g. <c>1 D:</c>, which is what a report has to say.</summary>
        public string Name { get; }

        public CounterProbe Latency { get; }

        public CounterProbe Queue { get; }

        public IEnumerable<CounterProbe> Probes => [Latency, Queue];
    }

    /// <summary>
    /// One performance counter plus the record of whether it ever worked. The counter itself cannot
    /// answer that: a null reading looks the same whether the counter is absent, threw, or genuinely had
    /// nothing to report, and everything downstream treats all three as "no disk data".
    /// </summary>
    private sealed class CounterProbe : IDisposable
    {
        private readonly PerformanceCounter? _counter;

        private CounterProbe(string label, string counterPath, PerformanceCounter? counter, string? creationError)
        {
            Label = label;
            CounterPath = counterPath;
            _counter = counter;
            CreationError = creationError;
        }

        public string Label { get; }

        public string CounterPath { get; }

        public string? CreationError { get; }

        public bool IsAvailable => _counter is not null;

        /// <summary>Readings that produced a number, as opposed to a throw or a missing counter.</summary>
        public long ValueCount { get; private set; }

        public string? LastReadError { get; private set; }

        public static CounterProbe Create(string label, string category, string name, string? instance)
        {
            var path = instance is null ? $@"\{category}\{name}" : $@"\{category}({instance})\{name}";

            try
            {
                var counter = instance is null
                    ? new PerformanceCounter(category, name, readOnly: true)
                    : new PerformanceCounter(category, name, instance, readOnly: true);

                // Reading once here is what actually proves the counter resolves; construction on its own
                // is lazy and succeeds for categories that turn out not to exist.
                _ = counter.NextValue();
                return new CounterProbe(label, path, counter, creationError: null);
            }
            catch (Exception ex)
            {
                return new CounterProbe(label, path, counter: null, creationError: ex.Message);
            }
        }

        public double? Read(double multiplier = 1)
        {
            if (_counter is null)
            {
                return null;
            }

            try
            {
                var value = Math.Round(_counter.NextValue() * multiplier, 2);
                ValueCount++;
                return value;
            }
            catch (Exception ex)
            {
                LastReadError = ex.Message;
                return null;
            }
        }

        public void Dispose()
        {
            _counter?.Dispose();
        }
    }
}
