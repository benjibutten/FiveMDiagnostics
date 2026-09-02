namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The evening OBS was blamed for a stall it merely recorded.
/// </summary>
/// <remarks>
/// <para>
/// 1 September, 00:11:45 to 00:11:54: a nine-second freeze with frames of 790, 677, 381 and 291 ms, the
/// card at 92% and the video memory manager at 0.91 cores. The engine ranked it
/// <see cref="RootCauseCategory.ObsRenderOutputContention"/> at 80%, ahead of
/// <see cref="RootCauseCategory.GpuVramPressure"/> at 65%, on four signals that are every one of them
/// downstream of the freeze: OBS skipped 280 render frames because the game produced nothing to
/// capture, its render time rose because the whole machine stalled, the severe spikes were the freeze
/// itself, and NVENC peaked afterwards catching up.
/// </para>
/// <para>
/// Output skipped frames — the counter that measures OBS rather than the game — stood at zero for the
/// entire evening, from 23:11 to 01:10. Not one viewer lost a frame.
/// </para>
/// </remarks>
public sealed class ObsIsAWitnessNotACauseTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 22, 11, 17, TimeSpan.Zero);

    private readonly FiveMCorrelationEngine _engine = new();

    /// <summary>
    /// The case as it happened: OBS skipped roughly what the game lost, and dropped nothing on output.
    /// </summary>
    [Fact]
    public void RenderSkipsThatMerelyMatchTheGamesOwnLossesCannotCarryTheVerdict()
    {
        var analysis = _engine.Analyze(BuildIncident(renderSkipDelta: 280, outputSkipDelta: 0));

        var obs = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.ObsRenderOutputContention);
        Assert.NotNull(obs);
        Assert.True(obs!.Confidence <= 0.25, $"a witness reached {obs.Confidence:P0}");
        Assert.Contains(obs.Evidence, item => item.Contains("Noll output skipped frames", StringComparison.Ordinal));
        Assert.Contains(obs.Evidence, item => item.Contains("kan inte rendera frames spelet aldrig producerade", StringComparison.Ordinal));

        Assert.NotEqual(RootCauseCategory.ObsRenderOutputContention, analysis.Hypotheses[0].Category);
    }

    /// <summary>
    /// The other direction, and the reason the rule is about output rather than about OBS. A stream that
    /// really is dropping frames on its way out is dropping them for its own reasons, and that is
    /// evidence.
    /// </summary>
    [Fact]
    public void DroppedOutputFramesAreStillObsEvidence()
    {
        var analysis = _engine.Analyze(BuildIncident(renderSkipDelta: 280, outputSkipDelta: 44));

        var obs = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.ObsRenderOutputContention);
        Assert.True(obs.Confidence > 0.25, $"a real output drop was capped at {obs.Confidence:P0}");
        Assert.Contains(obs.Evidence, item => item.Contains("output skipped frames ökade med 44", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the case the ratio exists for: OBS losing far more than the game did is OBS's own problem,
    /// even with output intact.
    /// </summary>
    [Fact]
    public void SkippingFarMoreThanTheGameLostIsObsEvidence()
    {
        var analysis = _engine.Analyze(BuildIncident(renderSkipDelta: 4_000, outputSkipDelta: 0));

        var obs = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.ObsRenderOutputContention);
        Assert.True(obs.Confidence > 0.25, $"an excess render skip was capped at {obs.Confidence:P0}");
        Assert.Contains(obs.Evidence, item => item.Contains("OBS tappade alltså mer än spelet", StringComparison.Ordinal));
    }

    /// <summary>
    /// The verdict the window should reach instead, and the measurement that settles it: the trace
    /// watched Windows evacuate the card while the game went quiet.
    /// </summary>
    [Fact]
    public void TheVideoMemoryManagerCarriesTheVerdictInstead()
    {
        var analysis = _engine.Analyze(BuildIncident(renderSkipDelta: 280, outputSkipDelta: 0));

        Assert.Equal(RootCauseCategory.GpuVramPressure, analysis.Hypotheses[0].Category);
        Assert.Contains(analysis.Hypotheses[0].Evidence, item => item.Contains("dxgmms2.sys", StringComparison.Ordinal));
        Assert.Contains(analysis.Hypotheses[0].Evidence, item => item.Contains("vänta på flytten, inte till att räkna", StringComparison.Ordinal));
    }

    private static IncidentRecord BuildIncident(long renderSkipDelta, long outputSkipDelta)
    {
        var events = new List<TelemetryEvent>();
        var markedAt = Start.AddSeconds(30);

        // A calm minute either side of the freeze, so the window's baseline is the cadence rather than
        // the damage.
        for (var i = 0; i < 3_000; i++)
        {
            events.Add(Frame(Start.AddMilliseconds(i * 16.67), 16.67, cpuBusyMs: 6.9, gpuBusyMs: 4.5, cpuWaitMs: 9.7));
        }

        // The freeze, as PresentMon recorded it: the CPU side holds nearly the whole frame and the GPU
        // does four to twenty milliseconds of work in frames lasting hundreds.
        double[] freeze = [228.9, 275.9, 265.1, 380.8, 153.4, 676.9, 789.8, 290.9];
        var offset = 0d;
        foreach (var frameTime in freeze)
        {
            events.Add(Frame(markedAt.AddMilliseconds(offset), frameTime, cpuBusyMs: frameTime - 1, gpuBusyMs: 8.0, cpuWaitMs: 0.2));
            offset += frameTime;
        }

        // The rest of the window's spikes. The real incident held 69 of them, and their combined loss is
        // the denominator OBS's skipped frames are read against — a window containing only the eight
        // largest would understate what the game lost and make any OBS counter look like an excess.
        for (var i = 0; i < 61; i++)
        {
            events.Add(Frame(markedAt.AddMilliseconds(offset), 40.0, cpuBusyMs: 38.0, gpuBusyMs: 7.0, cpuWaitMs: 0.4));
            offset += 40.0;
        }

        events.Add(new GpuTelemetrySample(
            markedAt,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 18,
            MemoryBandwidthUtilizationPercent: 15,
            UsedVramBytes: (ulong)(9.17 * 1024 * 1024 * 1024),
            TotalVramBytes: 10UL * 1024 * 1024 * 1024,
            EncoderUtilizationPercent: 95,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: 1));

        // Two readings, so the engine sees the delta the counters moved by rather than their absolutes.
        events.Add(Obs(Start, renderSkipped: 466, outputSkipped: 0));
        events.Add(Obs(markedAt.AddSeconds(20), renderSkipped: 466 + renderSkipDelta, outputSkipped: outputSkipDelta));

        events.Add(new ArtifactEvidence(
            markedAt,
            ArtifactKind.EtlTrace,
            "ETL-trace.",
            new Dictionary<string, double>
            {
                ["cpuSampleCount"] = 208_360,
                ["cpuSubjectIsGame"] = 1,
                ["cpuSubjectProcessCores"] = 3.93,
                ["videoMemoryManagerPeakCores"] = 0.91,
                ["videoMemoryManagerBaselineCores"] = 0.18,
                ["videoMemoryManagerPressured"] = 1,
                ["videoMemorySubjectCoresAtPeak"] = 3.22,
                ["videoMemorySubjectBaselineCores"] = 3.93,
                ["videoMemorySubjectWentQuiet"] = 1,
            }));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Severe, "Auto: 790 ms frame"),
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

    private static FrameTelemetrySample Frame(
        DateTimeOffset at,
        double frameTimeMs,
        double cpuBusyMs,
        double gpuBusyMs,
        double cpuWaitMs)
    {
        return new FrameTelemetrySample(
            at,
            frameTimeMs,
            GpuBusyMs: gpuBusyMs,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuBusyMs,
            CpuWaitMs: cpuWaitMs);
    }

    private static ObsTelemetrySample Obs(DateTimeOffset at, long renderSkipped, long outputSkipped)
    {
        return new ObsTelemetrySample(
            at,
            IsConnected: true,
            ActiveFps: 60,
            AverageFrameRenderTimeMs: 0.8,
            RenderSkippedFrames: renderSkipped,
            OutputSkippedFrames: outputSkipped,
            CpuUsagePercent: 4,
            MemoryUsageMb: 900,
            IsStreaming: true,
            IsRecording: false,
            IsProcessRunning: true);
    }
}
