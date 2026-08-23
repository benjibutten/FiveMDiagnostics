namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// Cover for the misranking that survived the count-based attribution gate.
/// </summary>
/// <remarks>
/// Built from a real incident window (2026-08-22 21:02:16–21:03:46). It lost 1 415 ms across five
/// spikes, and 1 258 ms of that was a single frame whose CPU was busy for 1 242 ms of it — a stall the
/// CPU spent computing, which storage cannot cause. Counting spikes scored it 3 of 5 CPU-bound, under
/// the 80% gate, so an 88% "streaming/disk stall" verdict stood. By time it is 95%.
/// </remarks>
public sealed class SpikeTimeAttributionTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 22, 21, 2, 16, TimeSpan.Zero);

    private readonly FiveMCorrelationEngine _engine = new();

    [Fact]
    public void OneHugeCpuBoundFrameOutweighsSeveralSmallAmbiguousOnes()
    {
        var analysis = _engine.Analyze(BuildIncident());
        var disk = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        // The hypothesis must actually be raised, or this test would pass for the wrong reason: the
        // point is that it is raised and then capped, not that the window failed to produce it.
        Assert.NotNull(disk);
        Assert.True(
            disk!.Confidence <= 0.3,
            $"disk stall reached {disk.Confidence:P0} in a window where 95% of the lost time was CPU-bound");
        Assert.NotEqual(RootCauseCategory.StreamingOrDiskStall, analysis.Hypotheses[0].Category);
    }

    [Fact]
    public void TheEvidenceReportsTheTimeShareNotJustTheCount()
    {
        var analysis = _engine.Analyze(BuildIncident());
        var disk = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        Assert.NotNull(disk);
        Assert.Contains(disk!.Evidence, item => item.Contains("spike-tiden", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other direction, and the reason the cap is a share rather than a blanket rule: a window whose
    /// lost time was spent waiting must still be allowed to reach a storage verdict.
    /// </summary>
    [Fact]
    public void AWindowWhoseLostTimeIsNotCpuBoundIsNotCapped()
    {
        var events = BuildBaseline();

        // The same lost time, but spent waiting rather than computing — neither CPU nor GPU busy, which
        // is the signature of a frame blocked on something outside the pipeline.
        AddSpike(events, Start.AddSeconds(30), frameTimeMs: 1258, cpuBusyMs: 12, gpuBusyMs: 9);
        AddSpike(events, Start.AddSeconds(31), frameTimeMs: 43, cpuBusyMs: 9, gpuBusyMs: 8);
        AddSpike(events, Start.AddSeconds(32), frameTimeMs: 36, cpuBusyMs: 8, gpuBusyMs: 5);

        var analysis = _engine.Analyze(BuildIncident(events));
        var disk = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        Assert.NotNull(disk);
        Assert.DoesNotContain(disk!.Evidence, item => item.Contains("NEDVIKTAD", StringComparison.Ordinal));
        Assert.True(disk.Confidence > 0.3, $"an uncontradicted storage verdict was capped anyway ({disk.Confidence:P0})");
    }

    /// <summary>
    /// Thin attribution coverage must not be mistaken for a decisive answer.
    /// </summary>
    /// <remarks>
    /// PresentMon v1 supplies no CPU figure, and a capture can straddle a version change or drop the
    /// column for part of a window. Dividing CPU-bound time by only the time an attribution could be
    /// made for would let one classified frame among many unclassifiable ones report 100% CPU-bound —
    /// a claim about a fraction of the evidence, used to cap a hypothesis about all of it. Unattributed
    /// time belongs in the denominator, so a window like this simply fails to reach the bar.
    /// </remarks>
    [Fact]
    public void OneClassifiableSpikeAmongManyUnknownOnesDoesNotCapAnything()
    {
        var events = BuildBaseline();

        // One CPU-bound frame we can attribute...
        AddSpike(events, Start.AddSeconds(30), frameTimeMs: 300, cpuBusyMs: 290, gpuBusyMs: 8);

        // ...and far more lost time we cannot, because the CPU column is missing.
        for (var i = 0; i < 10; i++)
        {
            events.Add(new FrameTelemetrySample(
                Start.AddSeconds(31 + i),
                200,
                GpuBusyMs: 9,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 200,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: null,
                CpuWaitMs: 0.1));
        }

        var analysis = _engine.Analyze(BuildIncident(events));
        var disk = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        Assert.NotNull(disk);
        Assert.DoesNotContain(disk!.Evidence, item => item.Contains("NEDVIKTAD", StringComparison.Ordinal));
    }

    private static IncidentRecord BuildIncident(List<TelemetryEvent>? events = null)
    {
        events ??= BuildRealWindow();

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), Start.AddSeconds(30), IncidentSeverity.Normal, "Auto: 36 ms frame"),
            Start,
            Start.AddSeconds(90),
            CreateEnvironment(),
            events,
            Analysis: null,
            Attachments: []);
    }

    private static List<TelemetryEvent> BuildRealWindow()
    {
        var events = BuildBaseline();

        // The five spikes the window actually contained, with their measured CPU and GPU busy times.
        AddSpike(events, Start.AddSeconds(30.0), 43.1, cpuBusyMs: 27.4, gpuBusyMs: 7.9);
        AddSpike(events, Start.AddSeconds(30.5), 36.2, cpuBusyMs: 21.1, gpuBusyMs: 5.0);
        AddSpike(events, Start.AddSeconds(31.0), 1258.5, cpuBusyMs: 1242.5, gpuBusyMs: 155.5);
        AddSpike(events, Start.AddSeconds(33.0), 34.7, cpuBusyMs: 19.6, gpuBusyMs: 3.3);
        AddSpike(events, Start.AddSeconds(33.5), 42.0, cpuBusyMs: 30.1, gpuBusyMs: 3.3);

        return events;
    }

    /// <summary>
    /// A quiet 60 fps window plus the disk throughput that made the engine reach for a storage verdict:
    /// a neighbour process moving real data, with no latency, queue or paging counter to back it up.
    /// </summary>
    private static List<TelemetryEvent> BuildBaseline()
    {
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.67),
                16.67,
                GpuBusyMs: 7.9,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 9.0,
                CpuWaitMs: 7.6));
        }

        var busyNeighbour = new ProcessActivity("OneDrive.Sync.Service", 4321, 5.3, 313L * 1024 * 1024);
        for (var second = 0; second < 60; second++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 62,
                PerCoreUsagePercent: new Dictionary<string, double>(),
                MemoryCommitPercent: 45,
                AvailableMemoryMb: 15_000,
                TopCpuProcesses: [busyNeighbour],
                TopDiskProcesses: [busyNeighbour],

                // A latency peak and a queue reading somewhere in the ninety second window — exactly the
                // shape that let the real incident reach 88%. They are genuine measurements and they are
                // still not what stalled a frame the CPU spent 1 242 ms computing.
                DiskAverageLatencyMs: second == 10 ? 24 : 1.2,
                DiskQueueLength: second == 10 ? 2.4 : 0.3,
                HardFaultPagesPerSecond: 2));
        }

        return events;
    }

    private static void AddSpike(List<TelemetryEvent> events, DateTimeOffset timestamp, double frameTimeMs, double cpuBusyMs, double gpuBusyMs)
    {
        events.Add(new FrameTelemetrySample(
            timestamp,
            frameTimeMs,
            GpuBusyMs: gpuBusyMs,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuBusyMs,
            CpuWaitMs: 0.1));
    }

    private static EnvironmentMetadata CreateEnvironment()
    {
        return new EnvironmentMetadata(
            "Windows 11",
            "AMD Ryzen 7 5700X",
            32UL * 1024 * 1024 * 1024,
            "RTX 3080",
            "555.12",
            120,
            "Enabled",
            true,
            "Example Server",
            Start,
            Start.AddSeconds(90));
    }
}
