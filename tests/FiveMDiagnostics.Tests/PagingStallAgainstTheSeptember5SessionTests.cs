namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The evening of 5 September, where every long freeze was one 64 kB read out of <c>D:\pagefile.sys</c>
/// taking 431–454 ms and the engine had no category for it.
/// </summary>
/// <remarks>
/// The signal was in the traces the whole time and nothing read it: hard faults in the game process, a
/// paging read whose service time matched the game thread's off-CPU interval to the tenth of a
/// millisecond, and a system drive answering the same size of read in 0.04 ms. Ten traces of ten showed
/// it, and the app's verdict was <c>GameNotInFocus</c> at 95%.
/// </remarks>
public sealed class PagingStallAgainstTheSeptember5SessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 21, 03, 40, TimeSpan.Zero);

    /// <summary>The 23:04 trace: a 454.1 ms wait and a 454.116 ms read, from two different streams.</summary>
    private const double StallMs = 454.1;

    [Fact]
    public void APagingReadThatMatchesTheThreadsWaitIsTheVerdict()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        Assert.Equal(RootCauseCategory.MemoryPagingStall, analysis.Hypotheses[0].Category);
        Assert.True(analysis.Hypotheses[0].Confidence >= 0.85);
        Assert.Contains(
            analysis.Hypotheses[0].Evidence,
            item => item.Contains("D:", StringComparison.Ordinal) && item.Contains("hårda sidfel", StringComparison.Ordinal));
    }

    /// <summary>
    /// The claim that makes this the best-evidenced verdict in the engine: the drive's own service time
    /// and the scheduler's off-CPU interval are the same number, and neither stream knows about the
    /// other.
    /// </summary>
    [Fact]
    public void TheTwoIndependentMeasurementsAreNamedInTheEvidence()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        Assert.Contains(
            analysis.Hypotheses[0].Evidence,
            item => item.Contains("två mätningar som inte känner till varandra", StringComparison.Ordinal));

        // And the healthy volume, because that is what turns a slow read into an accusation against one
        // drive rather than against storage in general.
        Assert.Contains(
            analysis.Hypotheses[0].Evidence,
            item => item.Contains("C:", StringComparison.Ordinal) && item.Contains("0,04 ms", StringComparison.Ordinal));
    }

    /// <summary>
    /// Hard faults and a slow paging read in the same trace, with no wait that matches, is a lead rather
    /// than a verdict — the trace holds both facts and cannot say the frame waited for that read.
    /// </summary>
    [Fact]
    public void WithoutAMatchingWaitItIsOnlyALead()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(waitMs: 120));

        var paging = analysis.Hypotheses.Single(item => item.Category == RootCauseCategory.MemoryPagingStall);

        Assert.True(paging.Confidence <= 0.5);
        Assert.Contains(paging.Evidence, item => item.Contains("inte belagt", StringComparison.Ordinal));
    }

    /// <summary>
    /// A trace with no hard faults in the game does not produce the verdict, however slow the volume is.
    /// Somebody else's paging is a fact about the machine, not about this frame.
    /// </summary>
    [Fact]
    public void PagingInAnotherProcessIsNotTheGamesVerdict()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(gameHardFaults: 0));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.MemoryPagingStall);
    }

    /// <summary>
    /// The free RAM the app has sampled every second since it was written now reaches the incident, so
    /// the reason the pages were written out is answerable afterwards.
    /// </summary>
    [Fact]
    public void TheIncidentTimelineCarriesTheFreeMemory()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        var memory = analysis.TimelineHighlights.Single(item => item.Category == "Memory");

        Assert.Contains("ledigt RAM", memory.Summary, StringComparison.Ordinal);
        Assert.Contains("commit", memory.Summary, StringComparison.Ordinal);
    }

    private static IncidentRecord Incident(double waitMs = StallMs, double gameHardFaults = 18)
    {
        var frameAt = Start.AddSeconds(25);
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(Frame(Start.AddMilliseconds(i * 16.7), 16.7));
        }

        // The frame the thread spent asleep. PresentMon reports the whole of it as CPU busy, which is
        // exactly what a blocked thread looks like from the present side.
        events.Add(Frame(frameAt, StallMs, cpuBusyMs: StallMs - 12));

        // Two seconds of system telemetry with the machine short of memory, which is why the pages were
        // written out in the first place.
        for (var i = 0; i < 60; i++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(i),
                TotalCpuUsagePercent: 41,
                new Dictionary<string, double>(),
                MemoryCommitPercent: 92,
                AvailableMemoryMb: 1_400,
                TopCpuProcesses: [],
                TopDiskProcesses: []));
        }

        var waitStart = frameAt;
        events.Add(new ArtifactEvidence(
            waitStart,
            ArtifactKind.EtlTrace,
            "Disk: långsammaste diskoperation i spåret: 454 ms för en 64 kB-läsning ur D:\\pagefile.sys.",
            new Dictionary<string, double>
            {
                ["durationSeconds"] = 27,
                ["dpcMaxMs"] = 0.30,
                ["isrMaxMs"] = 0.12,
                ["cpuSampleCount"] = 184_234,
                ["cpuSubjectIsGame"] = 1,
                ["cpuSubjectProcessCores"] = 3.67,

                ["diskHardFaults"] = 104,
                ["diskGameHardFaults"] = gameHardFaults,
                ["diskSlowestHardFaultMs"] = 454.204,
                ["diskSlowestOperationMs"] = 454.116,
                ["diskSlowestOperationIsGame"] = 1,
                ["diskVolumeOperations_D:"] = 25,
                ["diskVolumeMedianMs_D:"] = 10.113,
                ["diskVolumeMaxMs_D:"] = 454.116,
                ["diskVolumeOperations_C:"] = 743,
                ["diskVolumeMedianMs_C:"] = 0.04,
                ["diskVolumeMaxMs_C:"] = 11.319,
                ["diskPagingReadMaxMs_D:"] = 454.116,
                ["diskPagingReadIsGame"] = 1,

                ["gameThreadWaitThreadId"] = 28_144,
                ["gameThreadLongWaitCount"] = 1,
                ["gameThreadUserRequestWaitCount"] = 0,
                ["gameThreadMaxWaitMs"] = waitMs,
                ["gameThreadWaitIntervalCount"] = 1,
                ["gameThreadWait0StartUnixMs"] = frameAt.ToUnixTimeMilliseconds(),
                ["gameThreadWait0EndUnixMs"] = frameAt.AddMilliseconds(waitMs).ToUnixTimeMilliseconds(),
                ["gameThreadWait0DurationMs"] = waitMs,
                ["gameThreadWait0UserRequest"] = 0,
            }));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), frameAt, IncidentSeverity.Severe, "Auto: 454 ms frame"),
            Start,
            Start.AddSeconds(90),
            Environment(),
            events,
            Analysis: null,
            Attachments: []);
    }

    private static FrameTelemetrySample Frame(DateTimeOffset at, double frameTimeMs, double? cpuBusyMs = null)
    {
        return new FrameTelemetrySample(
            at,
            frameTimeMs,
            GpuBusyMs: 5.2,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuBusyMs ?? 9.2,
            CpuWaitMs: 0.1);
    }

    internal static EnvironmentMetadata Environment()
    {
        return new EnvironmentMetadata(
            "Microsoft Windows 10.0.26200",
            "AMD Ryzen 7 5700X 8-Core Processor",
            34_278_539_264,
            "NVIDIA GeForce RTX 3080",
            "32.0.16.1088",
            59,
            "Disabled",
            ObsDetectedAtStart: true,
            ServerProfileName: string.Empty,
            SessionStartedAt: Start,
            SessionEndedAt: null);
    }
}
