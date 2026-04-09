using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

public sealed class SystemTelemetryCollector : ITelemetryCollector, IDisposable
{
    private readonly PerformanceCounter? _totalCpuCounter;
    private readonly IReadOnlyList<PerformanceCounter> _perCoreCounters;
    private readonly Dictionary<int, ProcessMetricSnapshot> _previousSnapshots = new();
    private readonly TimeSpan _processSampleInterval = TimeSpan.FromSeconds(2);
    private readonly int _currentSessionId = Process.GetCurrentProcess().SessionId;

    private DateTimeOffset _lastProcessSampleUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<ProcessActivity> _cachedTopCpu = [];
    private IReadOnlyList<ProcessActivity> _cachedTopDisk = [];

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
        catch
        {
            _perCoreCounters = [];
        }
    }

    public string Name => "SystemTelemetry";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (context.ProcessResolver.TryGetTargetProcess() is not null)
            {
                var timestamp = context.UtcNow();
                var (memoryPressure, availableMb) = ReadMemorySnapshot();
                var (topCpu, topDisk) = SampleProcesses(timestamp);

                await context.Writer.WriteAsync(
                    new SystemTelemetrySample(
                        timestamp,
                        ReadCpuUsage(_totalCpuCounter),
                        ReadPerCoreCpuUsage(),
                        memoryPressure,
                        availableMb,
                        topCpu,
                        topDisk),
                    cancellationToken).ConfigureAwait(false);
            }

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

    private (IReadOnlyList<ProcessActivity> TopCpu, IReadOnlyList<ProcessActivity> TopDisk) SampleProcesses(DateTimeOffset timestamp)
    {
        if (timestamp - _lastProcessSampleUtc < _processSampleInterval)
        {
            return (_cachedTopCpu, _cachedTopDisk);
        }

        var samples = new List<ProcessActivity>();
        var activeProcessIds = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != _currentSessionId)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (!ProcessMetricsReader.TryRead(process, timestamp, out var snapshot))
                {
                    continue;
                }

                activeProcessIds.Add(snapshot.ProcessId);
                var cpu = 0d;
                var ioPerSecond = 0L;

                if (_previousSnapshots.TryGetValue(snapshot.ProcessId, out var previous))
                {
                    cpu = ProcessMetricsReader.ComputeCpuPercent(snapshot, previous);
                    ioPerSecond = ProcessMetricsReader.ComputeReadBytesPerSecond(snapshot, previous)
                        + ProcessMetricsReader.ComputeWriteBytesPerSecond(snapshot, previous);
                }

                _previousSnapshots[snapshot.ProcessId] = snapshot;

                samples.Add(new ProcessActivity(snapshot.ProcessName, snapshot.ProcessId, cpu, ioPerSecond));
            }
        }

        foreach (var staleProcessId in _previousSnapshots.Keys.Where(processId => !activeProcessIds.Contains(processId)).ToArray())
        {
            _previousSnapshots.Remove(staleProcessId);
        }

        _cachedTopCpu = samples.OrderByDescending(item => item.CpuPercent).Take(5).ToArray();
        _cachedTopDisk = samples.OrderByDescending(item => item.IoBytesPerSecond).Take(5).ToArray();
        _lastProcessSampleUtc = timestamp;

        return (_cachedTopCpu, _cachedTopDisk);
    }
}