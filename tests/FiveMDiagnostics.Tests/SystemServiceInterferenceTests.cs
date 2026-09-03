namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// The worst non-VRAM frame of 2 September, and the two rules that between them made it invisible.
/// </summary>
/// <remarks>
/// <para>
/// At 21:42:32 the game lost a 2 145 ms frame with the card half empty at 54%. The deep capture shows
/// <c>SearchIndexer.exe</c> holding 1.06 of sixteen cores and making 192 788 file operations in four
/// seconds against its own index database on the same volume as the game's cache — while the game's own
/// CPU fell from 3.0 cores to 1.46 and its main thread sat off the processor for 2 070 ms.
/// </para>
/// <para>
/// The incident recorded <c>suspectedProcesses: []</c>. Two reasons, both of them rules rather than
/// missing data: the collector skipped every process outside the interactive session, which is where all
/// Windows services live, and the analysis needed 12% of the machine to name a neighbour — two whole
/// cores on a sixteen-thread processor, a bar no background workload has ever cleared.
/// </para>
/// </remarks>
public sealed class SystemServiceInterferenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 19, 41, 50, TimeSpan.Zero);

    private readonly FiveMCorrelationEngine _engine = new();

    /// <summary>
    /// One held core on a sixteen-thread machine is 6.6%, and it has to be enough to name the process.
    /// </summary>
    [Fact]
    public void AServiceHoldingOneCoreOnAWideMachineIsNamed()
    {
        var analysis = _engine.Analyze(BuildIncident(cores: 16));

        Assert.Contains(analysis.SuspectedProcesses, item => item.ProcessName == "SearchIndexer");
    }

    /// <summary>
    /// And it is named as a service, because "close it" is not the advice and the taskbar will not
    /// have it.
    /// </summary>
    [Fact]
    public void AServiceIsMarkedAsOneInTheText()
    {
        var analysis = _engine.Analyze(BuildIncident(cores: 16));

        var suspect = analysis.SuspectedProcesses.First(item => item.ProcessName == "SearchIndexer");
        Assert.True(suspect.IsSystemService);
        Assert.Equal("SearchIndexer (systemtjänst)", suspect.DisplayName);
        Assert.Contains("systemtjänst", analysis.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The floor still means one core, so a narrow machine behaves exactly as it did. Without this the
    /// change would just be "lower the threshold", which lets noise in on every machine.
    /// </summary>
    [Fact]
    public void TheSameSharePassesOnlyBecauseTheMachineIsWide()
    {
        // 6.6% of a four-thread machine is a quarter of a core, and nothing worth naming.
        var narrow = _engine.Analyze(BuildIncident(cores: 4));

        Assert.DoesNotContain(narrow.SuspectedProcesses, item => item.ProcessName == "SearchIndexer");
    }

    /// <summary>
    /// The file system traffic the trace measured, in the unit that shows it. Twelve megabytes a second
    /// is under every byte threshold the analysis has; 48 000 operations a second is not.
    /// </summary>
    [Fact]
    public void OperationRateIsWeighedWhereThroughputSaysNothing()
    {
        var analysis = _engine.Analyze(BuildIncident(cores: 16));
        var disk = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        Assert.NotNull(disk);
        Assert.Contains(disk!.Evidence, item => item.Contains("filsystemsoperationer i sekunden", StringComparison.Ordinal));
    }

    /// <summary>
    /// The rate that separates a neighbour using the file system from one contending for it, checked
    /// against the three traces it has to tell apart.
    /// </summary>
    /// <remarks>
    /// Run through the parser against the evening's own captures: the two quiet traces come out at
    /// 3 185 and 4 671 operations a second with the game itself at about 2 400, and the capture taken
    /// during the freeze at 68 268 with Windows Search holding 62 759 of them. The bar sits an order of
    /// magnitude below the one and twice above the others.
    /// </remarks>
    [Theory]
    [InlineData(2_407, false)]
    [InlineData(4_671, false)]
    [InlineData(62_759, true)]
    public void ContentionIsSeparatedFromOrdinaryFileSystemUse(double neighbourPerSecond, bool contending)
    {
        var summary = new FileOperationSummary(
            TotalOperations: 1_000_000,
            CoveredSeconds: 21.7,
            TopProcesses: [],
            BusiestNeighbour: new FileOperationProcess("SearchIndexer.exe", 5672, 1_364_973, neighbourPerSecond, IsGame: false),
            NeighbourContendingIntervals: []);

        Assert.Equal(contending, summary.HasContendingNeighbour);
        Assert.Equal(contending, summary.Describe().Contains("filsystemsträngsel", StringComparison.Ordinal));
    }

    private static IncidentRecord BuildIncident(int cores)
    {
        var events = new List<TelemetryEvent>();
        var markedAt = Start.AddSeconds(42);

        for (var i = 0; i < 600; i++)
        {
            var slow = i is 500;
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.67),
                slow ? 2145.0 : 16.67,
                GpuBusyMs: slow ? 12.0 : 5.0,
                DisplayLatencyMs: 20,
                MsBetweenPresents: slow ? 2145.0 : 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: slow ? 2129.1 : 7.8,
                CpuWaitMs: slow ? 0.22 : 8.8));
        }

        // 1.06 cores of sixteen is 6.6% of the machine. The same figure on a four-thread machine would
        // be a quarter of a core, which is why the floor follows the width rather than being fixed.
        //
        // The throughput is the measured one and it is deliberately below every byte threshold in the
        // analysis: 49 MB over four seconds. That is the whole point of the operation-rate signal —
        // this workload is small in bytes and enormous in count, so nothing weighed in megabytes was
        // ever going to see it.
        var indexer = new ProcessActivity("SearchIndexer", 3216, 6.6, 11L * 1024 * 1024, IsSystemService: true);
        var perCore = Enumerable.Range(0, cores).ToDictionary(index => index.ToString(), _ => 45d);

        for (var second = 0; second < 60; second++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 44,
                PerCoreUsagePercent: perCore,
                MemoryCommitPercent: 45,
                AvailableMemoryMb: 15_000,
                TopCpuProcesses: [indexer],
                TopDiskProcesses: [indexer],
                DiskAverageLatencyMs: 1.1,
                DiskQueueLength: 0.4,
                HardFaultPagesPerSecond: 12));
        }

        events.Add(new ProcessTelemetrySample(
            markedAt,
            31076,
            "FiveM_b3407_GTAProcess",
            CpuUsagePercent: 19,
            PrivateBytes: 9_000L * 1024 * 1024,
            WorkingSetBytes: 9_000L * 1024 * 1024,
            ThreadCount: 96,
            ReadBytesPerSecond: 15_000_000,
            WriteBytesPerSecond: 20_000));

        // What the deep capture measured over the four seconds around the freeze.
        events.Add(new ArtifactEvidence(
            markedAt,
            ArtifactKind.EtlTrace,
            "Filsystem: 240 235 operationer på 4,0 s (60 059/s).",
            new Dictionary<string, double>
            {
                ["fileOperations"] = 240_235,
                ["fileOperationsPerSecond"] = 60_059,
                ["fileOperationsNeighbourPerSecond"] = 48_441,
                ["fileOperationsNeighbourContending"] = 1,
            },
            "deep_20260902_194232.etl"));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Severe, "Auto: 2145 ms frame"),
            Start,
            Start.AddSeconds(90),
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
                "Disabled",
                ObsDetectedAtStart: true,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
