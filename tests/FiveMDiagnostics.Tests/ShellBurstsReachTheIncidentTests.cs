namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The processes a deep capture saw on the processor reach the incident's suspect list.
/// </summary>
/// <remarks>
/// The process counters read about once a second; Windows draws its start menu in two to four tenths of
/// one. So the shell bursts that coincided with five of the nine stalls measured on 4 September never
/// became a <c>peakCpuPercent</c> anywhere, and the incident at 21:23 recorded an empty suspect list and
/// the verdict "insufficient evidence" while <c>StartMenuExperienceHost</c> held 1.25 cores. The trace
/// samples at a kilohertz and already resolves every sample to a process — the figures were simply not
/// being carried out of it.
/// </remarks>
public sealed class ShellBurstsReachTheIncidentTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 19, 23, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset MarkedAt = Start.AddSeconds(46);

    /// <summary>
    /// The burst the counters missed is named, and in the same unit as everything else in the list: a
    /// share of the whole machine, not cores.
    /// </summary>
    [Fact]
    public void AProcessOnlyTheTraceSawBecomesASuspect()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(new Dictionary<string, double>
        {
            ["cpuProcessCores_StartMenuExperienceHost"] = 1.25,
            ["cpuProcessCores_FiveM_b3407_GTAProcess"] = 3.30,
        }));

        var suspect = Assert.Single(
            analysis.SuspectedProcesses,
            item => item.ProcessName == "StartMenuExperienceHost");

        // 1.25 cores of a sixteen-thread machine.
        Assert.Equal(7.8, suspect.PeakCpuPercent, 1);
        Assert.Contains("deep capture", suspect.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The game's own row is not a suspect in its own stutter, exactly as it is not when the counters
    /// report it.
    /// </summary>
    [Fact]
    public void TheGamesOwnRowIsNotASuspect()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(new Dictionary<string, double>
        {
            ["cpuProcessCores_FiveM_b3407_GTAProcess"] = 3.30,
        }));

        Assert.DoesNotContain(analysis.SuspectedProcesses, item => item.ProcessName.Contains("FiveM", StringComparison.Ordinal));
    }

    /// <summary>
    /// A process woken to poll something is not a burst. The bar is a third of a core over the window
    /// the trace retained.
    /// </summary>
    [Fact]
    public void AnIdleNeighbourIsNotPromoted()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(new Dictionary<string, double>
        {
            ["cpuProcessCores_SearchIndexer"] = 0.12,
        }));

        Assert.DoesNotContain(analysis.SuspectedProcesses, item => item.ProcessName == "SearchIndexer");
    }

    /// <summary>
    /// Where both saw the same process the larger figure wins and the row is not duplicated — a counter
    /// that caught a burst at all caught part of it, and the trace caught the whole of it.
    /// </summary>
    [Fact]
    public void AProcessBothSawAppearsOnceWithTheLargerFigure()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            new Dictionary<string, double> { ["cpuProcessCores_explorer"] = 1.60 },
            polledExplorerPercent: 3));

        var suspect = Assert.Single(analysis.SuspectedProcesses, item => item.ProcessName == "explorer");

        Assert.Equal(10, suspect.PeakCpuPercent, 1);
    }

    /// <summary>A window of ordinary frames with one hitch, plus one ETL artifact and its metrics.</summary>
    private static IncidentRecord Incident(IReadOnlyDictionary<string, double> traceMetrics, double polledExplorerPercent = 0)
    {
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            var slow = i is 460;
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.9),
                slow ? 281.0 : 16.9,
                GpuBusyMs: slow ? 16.1 : 5.0,
                DisplayLatencyMs: 20,
                MsBetweenPresents: slow ? 281.0 : 16.9,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: slow ? 251.2 : 7.8,
                CpuWaitMs: slow ? 4.76 : 8.8,
                PresentMode: "Composed: Copy with GPU GDI"));
        }

        // The machine's width, which is what a core count is expressed as a share of.
        IReadOnlyList<ProcessActivity> topCpu = polledExplorerPercent > 0
            ? [new ProcessActivity("explorer", 7420, polledExplorerPercent, 0)]
            : [];

        var perCore = Enumerable.Range(0, 16).ToDictionary(index => index.ToString(), _ => 38d);

        for (var second = 0; second < 90; second += 2)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 38,
                PerCoreUsagePercent: perCore,
                MemoryCommitPercent: 58,
                AvailableMemoryMb: 12_000,
                TopCpuProcesses: topCpu,
                TopDiskProcesses: []));
        }

        events.Add(new ArtifactEvidence(
            MarkedAt,
            ArtifactKind.EtlTrace,
            "ETL-trace analyserad.",
            traceMetrics));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), MarkedAt, IncidentSeverity.Severe, "Auto: 281 ms frame"),
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
                ObsDetectedAtStart: false,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
