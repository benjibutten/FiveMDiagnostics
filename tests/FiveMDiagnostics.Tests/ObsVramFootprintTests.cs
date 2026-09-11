namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The two steps at the end of 9 September, which the app watched and did not measure.
/// </summary>
/// <remarks>
/// 03:38:58 the card sat at 82.6 % with the stream running. 03:39:03 the encoder let go and it read
/// 80.4 %. 03:39:16, fourteen seconds later, OBS itself was gone and it read 75.4 %. Forty seconds after
/// that the game had refilled to 77.8 %, which is why the measurement is the steps and not the periods.
/// </remarks>
public sealed class ObsVramFootprintTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 1, 38, 40, TimeSpan.Zero);

    [Fact]
    public void BothStepsAreMeasuredAndAddedUp()
    {
        var monitor = new ObsVramFootprintMonitor();

        // Streaming, steady.
        Play(monitor, from: 0, seconds: 18, vramPercent: 82.6, streaming: true, running: true);

        // The encoder lets go.
        Play(monitor, from: 18, seconds: 14, vramPercent: 80.4, streaming: false, running: true);

        // OBS quits.
        Play(monitor, from: 32, seconds: 14, vramPercent: 75.4, streaming: false, running: false);

        // And the game takes the space back, which no step may include.
        Play(monitor, from: 46, seconds: 60, vramPercent: 77.8, streaming: false, running: false);

        var report = monitor.Summary();

        Assert.NotNull(report);

        Assert.NotNull(report.Encoder);
        Assert.Equal(2.2, report.Encoder.PercentagePoints, 1);
        Assert.Equal(225, report.Encoder.MegabytesFreed(report.TotalVramGb), 0);

        Assert.NotNull(report.RestOfStack);
        Assert.Equal(5.0, report.RestOfStack.PercentagePoints, 1);

        // Roughly the 740 MB of 8 September and the 784 MB of 9 September, measured rather than counted
        // by hand out of the GPU log.
        Assert.InRange(report.TotalMegabytesFreed, 700, 800);
        Assert.Contains("encodern", report.Message, StringComparison.Ordinal);
        Assert.Contains("webbkällor", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tail after the stack came off was eleven minutes of nobody really playing, and the line has to
    /// say that before somebody reads a hitch rate off it.
    /// </summary>
    [Fact]
    public void AShortTailIsCalledTooShortToCompareFrameTimesAcross()
    {
        var monitor = new ObsVramFootprintMonitor();

        Play(monitor, from: 0, seconds: 18, vramPercent: 82.6, streaming: true, running: true);
        Play(monitor, from: 18, seconds: 300, vramPercent: 75.4, streaming: false, running: false);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.False(report.TailIsUsable);
        Assert.Contains("för kort för att", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 10 September, where only the stream was stopped and OBS itself kept running to the end.
    /// </summary>
    /// <remarks>
    /// The line stated the encoder's 2.3 pp and stopped there. Read cold a fortnight later that is
    /// indistinguishable from a measurement that failed, when what actually happened is that the
    /// measurement was given half its evidence — the instruction was to close OBS in two steps and only
    /// the first was done.
    /// </remarks>
    [Fact]
    public void AMissingSecondStepIsSaidRatherThanLeftOut()
    {
        var monitor = new ObsVramFootprintMonitor();

        Play(monitor, from: 0, seconds: 18, vramPercent: 80.5, streaming: true, running: true);

        // The stream stops; OBS stays up for the rest of the evening.
        Play(monitor, from: 18, seconds: 1200, vramPercent: 78.2, streaming: false, running: true);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.NotNull(report.Encoder);
        Assert.Null(report.RestOfStack);
        Assert.Contains("OBS-processen avslutades aldrig", report.Message, StringComparison.Ordinal);
        Assert.Contains("omätt i kväll", report.Message, StringComparison.Ordinal);
    }

    /// <summary>An evening where OBS ran to the end has no step to read and says nothing.</summary>
    [Fact]
    public void AStackThatNeverCameOffIsSilent()
    {
        var monitor = new ObsVramFootprintMonitor();

        Play(monitor, from: 0, seconds: 120, vramPercent: 83.1, streaming: true, running: true);

        Assert.Null(monitor.Summary());
    }

    private static void Play(
        ObsVramFootprintMonitor monitor,
        int from,
        int seconds,
        double vramPercent,
        bool streaming,
        bool running)
    {
        for (var second = 0; second < seconds; second++)
        {
            var at = Start.AddSeconds(from + second);
            monitor.Observe(Obs(at, streaming, running));

            // Twice a second, the cadence the GPU log is written at.
            monitor.Observe(Adapter(at, vramPercent));
            monitor.Observe(Adapter(at.AddMilliseconds(500), vramPercent));
        }
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
            UsedVramBytes: (ulong)(Total * vramPercent / 100),
            TotalVramBytes: Total,
            EncoderUtilizationPercent: 0,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 55,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    private static ObsTelemetrySample Obs(DateTimeOffset at, bool streaming, bool running)
    {
        return new ObsTelemetrySample(
            at,
            IsConnected: running,
            ActiveFps: running ? 60 : null,
            AverageFrameRenderTimeMs: running ? 0.7 : null,
            RenderSkippedFrames: running ? 96 : null,
            OutputSkippedFrames: running ? 0 : null,
            CpuUsagePercent: running ? 3.4 : null,
            MemoryUsageMb: running ? 900 : null,
            IsStreaming: streaming,
            IsRecording: false,
            IsProcessRunning: running);
    }
}
