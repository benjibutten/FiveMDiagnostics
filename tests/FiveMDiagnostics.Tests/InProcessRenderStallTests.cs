namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The 465 ms frame of 23:45:44 on 9 September, and the verdict it did not get.
/// </summary>
/// <remarks>
/// <para>
/// Everything outside the game was quiet. The card sat dead flat at 81.8 % before and after,
/// <c>dxgmms2.sys</c> held 0.06 cores across the whole trace, the slowest disk operation was 11.2 ms and
/// no hard fault touched the game. The main thread waited 449 ms and the thread that released it was
/// FiveM's own render thread, on the processor for all of it, inside <c>d3d11.dll</c> and the driver's
/// user-mode half.
/// </para>
/// <para>
/// The engine returned "External process interference (57 %)" and named
/// <c>FiveM_ChromeBrowser</c>, whose entire contribution was 0.41 cores somewhere in the same
/// thirty seconds. 27 of that evening's 37 frames over 100 ms look like this one, and they were split
/// between that verdict and insufficient evidence.
/// </para>
/// </remarks>
public sealed class InProcessRenderStallTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 9, 21, 45, 14, TimeSpan.Zero);

    private static readonly DateTimeOffset FrameAt = Start.AddSeconds(30);

    private const double StallMs = 464.6;

    /// <summary>The chain, the calm card and the quiet driver together are the verdict.</summary>
    [Fact]
    public void TheChainThatNeverLeavesTheGameIsItsOwnVerdict()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        Assert.Equal(RootCauseCategory.InProcessRenderStall, analysis.Hypotheses[0].Category);

        var evidence = analysis.Hypotheses[0].Evidence;
        Assert.Contains(evidence, item => item.Contains("egen process", StringComparison.Ordinal));
        Assert.Contains(evidence, item => item.Contains("d3d11.dll", StringComparison.Ordinal));
        Assert.Contains(evidence, item => item.Contains("82 %", StringComparison.Ordinal));
    }

    /// <summary>
    /// The neighbour that got the verdict is still reported — it was on the machine — but it can no
    /// longer be the answer, because the trace says it was not holding the thread that stopped.
    /// </summary>
    [Fact]
    public void ABystanderIsNoLongerTheAnswer()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        var external = analysis.Hypotheses.Single(item => item.Category == RootCauseCategory.ExternalProcessInterference);

        Assert.True(external.Confidence <= 0.34);
        Assert.True(external.Confidence < analysis.Hypotheses[0].Confidence);
        Assert.Contains(external.Evidence, item => item.Contains("var inte i vägen", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same chain during an eviction is the driver's verdict, not this one. The render thread blocks
    /// there too, and the card's own occupancy is the only thing that separates the two.
    /// </summary>
    [Theory]
    [InlineData(81.8, 0.06, true)]
    [InlineData(92.6, 0.06, false)]
    [InlineData(81.8, 0.97, false)]
    public void TheCalmCardIsWhatSeparatesItFromEviction(double vramPercent, double driverCores, bool expected)
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(vramPercent, driverCores));

        Assert.Equal(
            expected,
            analysis.Hypotheses.Any(item => item.Category == RootCauseCategory.InProcessRenderStall));
    }

    /// <summary>
    /// A chain that leaves the process is exactly what the external verdict is for, and this rule has
    /// nothing to say about it.
    /// </summary>
    [Fact]
    public void AChainThatLeavesTheProcessIsNotThisVerdict()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(blockerIsGame: false));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.InProcessRenderStall);
    }

    /// <summary>
    /// A trace whose CPU sampling never caught <c>dxgmms2.sys</c>. Read as a default that absence came
    /// out below every threshold here and then went out as "drivrutinen flyttade inget videominne" — a
    /// measurement stated from the lack of one.
    /// </summary>
    [Fact]
    public void ATraceWithoutTheDriversFiguresCannotClearTheDriver()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(withVideoMemoryMetrics: false));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.InProcessRenderStall);
    }

    /// <summary>
    /// The same trace on an evening where nothing read the card at all — neither the session's own
    /// samples nor the trace. The calm card is what separates this verdict from eviction; without a
    /// reading there is nothing to separate them with, and the 0 % it used to fall back to read as the
    /// calmest card possible.
    /// </summary>
    [Fact]
    public void AnUnmeasuredCardIsNotACalmCard()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(
            Incident(withOccupancyAtPeak: false, withAdapterSamples: false));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.InProcessRenderStall);
    }

    /// <summary>
    /// The session's own GPU samples still answer when the trace could not name a second to read.
    /// </summary>
    [Fact]
    public void TheSessionsOwnReadingStandsInWhenTheTraceHasNone()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(withOccupancyAtPeak: false));

        Assert.Contains(analysis.Hypotheses, item => item.Category == RootCauseCategory.InProcessRenderStall);
    }

    /// <summary>
    /// The blocking thread without a graphics module on it. The chain still ends inside FiveM — that much
    /// is measured — but which of the game's threads held the main one is not, and the verdict may not
    /// name the render thread on the strength of the process alone.
    /// </summary>
    [Fact]
    public void WithoutAGraphicsModuleTheBlockingThreadIsNotNamed()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(withDriverModules: false));

        var stall = analysis.Hypotheses.Single(item => item.Category == RootCauseCategory.InProcessRenderStall);

        Assert.DoesNotContain(stall.Evidence, item => item.Contains("rendertråden", StringComparison.Ordinal));
        Assert.Contains(stall.Evidence, item => item.Contains("går inte att säga", StringComparison.Ordinal));

        // Still the verdict, and still below the one the identified thread earns.
        Assert.True(stall.Confidence < new FiveMCorrelationEngine().Analyze(Incident()).Hypotheses[0].Confidence);
    }

    private static IncidentRecord Incident(
        double vramPercent = 81.8,
        double videoMemoryCores = 0.06,
        bool blockerIsGame = true,
        bool withDriverModules = true,
        bool withVideoMemoryMetrics = true,
        bool withOccupancyAtPeak = true,
        bool withAdapterSamples = true)
    {
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(Frame(Start.AddMilliseconds(i * 16.7), 16.7));
        }

        events.Add(Frame(FrameAt, StallMs, cpuBusyMs: StallMs - 9, gpuBusyMs: 5));

        // Flat. The whole point of the window is that the card does nothing unusual in it.
        for (var half = 0; half < 120 && withAdapterSamples; half++)
        {
            events.Add(Adapter(Start.AddSeconds(half * 0.5), vramPercent));
        }

        events.Add(Trace(
            vramPercent,
            videoMemoryCores,
            blockerIsGame,
            withDriverModules,
            withVideoMemoryMetrics,
            withOccupancyAtPeak));

        // The neighbour the verdict used to name, with the load it actually had: 10.2 % of the machine
        // at its peak, on a machine that was nowhere near saturated.
        var browser = new ProcessActivity("FiveM_ChromeBrowser", 4_812, 10.2, 900L * 1024 * 1024);
        for (var second = 0; second < 60; second++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 44,
                PerCoreUsagePercent: new Dictionary<string, double>(),
                MemoryCommitPercent: 50,
                AvailableMemoryMb: 10_300,
                TopCpuProcesses: [browser],
                TopDiskProcesses: [browser],
                DiskAverageLatencyMs: 1.1,
                DiskQueueLength: 0.1,
                HardFaultPagesPerSecond: 0));
        }

        events.Add(new ProcessTelemetrySample(
            FrameAt,
            ProcessId: 23_652,
            "FiveM_b3407_GTAProcess",
            CpuUsagePercent: 24,
            PrivateBytes: 9_000L * 1024 * 1024,
            WorkingSetBytes: 9_000L * 1024 * 1024,
            ThreadCount: 122,
            ReadBytesPerSecond: 400_000,
            WriteBytesPerSecond: 20_000));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), FrameAt, IncidentSeverity.Severe, "Auto: 465 ms frame"),
            Start,
            Start.AddSeconds(90),
            PagingStallAgainstTheSeptember5SessionTests.Environment(),
            events,
            Analysis: null,
            Attachments: []);
    }

    /// <summary>
    /// The 23:45 capture. Every figure is the one <c>etlanalyzer</c> read out of it by hand.
    /// </summary>
    private static ArtifactEvidence Trace(
        double vramPercent,
        double videoMemoryCores,
        bool blockerIsGame,
        bool withDriverModules = true,
        bool withVideoMemoryMetrics = true,
        bool withOccupancyAtPeak = true)
    {
        var metrics = new Dictionary<string, double>
        {
            ["durationSeconds"] = 27.7,
            ["dpcMaxMs"] = 0.34,
            ["isrMaxMs"] = 0.16,
            ["cpuSampleCount"] = 161_753,
            ["cpuSubjectIsGame"] = 1,
            ["cpuSubjectProcessCores"] = 3.17,

            ["traceCoveredStartUnixMs"] = Start.AddSeconds(12).ToUnixTimeMilliseconds(),
            ["traceCoveredEndUnixMs"] = Start.AddSeconds(40).ToUnixTimeMilliseconds(),

            ["gameThreadWaitThreadId"] = 25_740,
            ["gameThreadLongWaitCount"] = 2,
            ["gameThreadUserRequestWaitCount"] = 2,
            ["gameThreadMaxWaitMs"] = 449.0,
            ["gameThreadWaitIntervalCount"] = 1,
            ["gameThreadWaitChainLength"] = 3,
            ["gameThreadWait0StartUnixMs"] = FrameAt.ToUnixTimeMilliseconds(),
            ["gameThreadWait0EndUnixMs"] = FrameAt.AddMilliseconds(449).ToUnixTimeMilliseconds(),
            ["gameThreadWait0DurationMs"] = 449.0,
            ["gameThreadWait0UserRequest"] = 1,

            ["gameThreadBlockedByThreadId"] = 28_124,
            ["gameThreadBlockerIsGame"] = blockerIsGame ? 1 : 0,
            ["gameThreadBlockerCores_GTAProcess.exe"] = 0.26,
        };

        if (withVideoMemoryMetrics)
        {
            metrics["videoMemoryManagerPeakCores"] = videoMemoryCores;
            metrics["videoMemoryManagerBaselineCores"] = 0.05;
            metrics["videoMemoryManagerPressured"] = videoMemoryCores >= 0.40 ? 1 : 0;

            if (withOccupancyAtPeak)
            {
                metrics["videoMemoryAdapterPercentAtPeak"] = vramPercent;
            }
        }

        if (withDriverModules)
        {
            metrics["gameThreadBlockerCores_d3d11.dll"] = 0.05;
            metrics["gameThreadBlockerCores_nvwgf2umx.dll"] = 0.03;
        }

        return new ArtifactEvidence(
            FrameAt,
            ArtifactKind.EtlTrace,
            "Väntan: huvudtråden låg av processorn 449,0 ms, släppt av rendertråden i samma process.",
            metrics);
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset at, double vramPercent)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            at,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 38,
            MemoryBandwidthUtilizationPercent: 13,
            (ulong)(Total * vramPercent / 100),
            Total,
            EncoderUtilizationPercent: 39,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 55,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    private static FrameTelemetrySample Frame(
        DateTimeOffset at,
        double frameTimeMs,
        double? cpuBusyMs = null,
        double? gpuBusyMs = null)
    {
        return new FrameTelemetrySample(
            at,
            frameTimeMs,
            GpuBusyMs: gpuBusyMs ?? 4.4,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuBusyMs ?? 7.8,
            CpuWaitMs: 0.1);
    }
}
