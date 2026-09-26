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
    /// 2026-09-13 02:21: the card dropped 521 MB at 02:21:12, OBS was seen gone at 02:21:16, and the game
    /// refilled six seconds after the drop. Read around the moment OBS was seen gone, that came out at
    /// −109 MB.
    /// </summary>
    [Fact]
    public void MemoryReleasedBeforeTheProcessIsSeenGoneIsStillTheStep()
    {
        var monitor = new ObsVramFootprintMonitor();

        Play(monitor, from: 0, seconds: 30, vramPercent: 83.4, streaming: true, running: true);
        Play(monitor, from: 30, seconds: 60, vramPercent: 81.0, streaming: false, running: true);

        // OBS lets go of its memory while shutting down, and is seen gone four seconds later.
        Play(monitor, from: 90, seconds: 4, vramPercent: 76.0, streaming: false, running: true);
        Play(monitor, from: 94, seconds: 3, vramPercent: 76.0, streaming: false, running: false);

        // The game takes the space back.
        Play(monitor, from: 97, seconds: 60, vramPercent: 78.6, streaming: false, running: false);

        var report = monitor.Summary();

        Assert.NotNull(report?.RestOfStack);
        Assert.Equal(5.0, report.RestOfStack.PercentagePoints, 1);
        Assert.Equal(2.4, report.Encoder!.PercentagePoints, 1);
    }

    /// <summary>
    /// 2026-09-24: OBS quit at 01:35:49, the game presented its last frame at 01:35:54 and the card fell
    /// 6.8 GB from 01:35:56. The step read across the game's release and came out at 7 GB.
    /// </summary>
    [Fact]
    public void TheGamesOwnReleaseIsNotCountedAsTheStacks()
    {
        var monitor = new ObsVramFootprintMonitor();

        Play(monitor, from: 0, seconds: 20, vramPercent: 83.9, streaming: true, running: true, gamePresenting: true);
        Play(monitor, from: 20, seconds: 30, vramPercent: 80.9, streaming: false, running: true, gamePresenting: true);
        Play(monitor, from: 50, seconds: 5, vramPercent: 75.9, streaming: false, running: false, gamePresenting: true);

        // The game's last frame, and its memory going two seconds later.
        Play(monitor, from: 55, seconds: 2, vramPercent: 75.9, streaming: false, running: false);
        Play(monitor, from: 57, seconds: 30, vramPercent: 12.8, streaming: false, running: false);

        var report = monitor.Summary();

        Assert.NotNull(report?.RestOfStack);
        Assert.Equal(5.0, report.RestOfStack.PercentagePoints, 1);
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
        Assert.Contains("OBS-processen har inte avslutats", report.Message, StringComparison.Ordinal);
        Assert.Contains("omätt än så länge", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 2026-09-25 03:23:54: the stream stopped, OBS quit eight seconds later and the machine was switched
    /// off five seconds after that.
    /// </summary>
    /// <remarks>
    /// The card read 83.6 → 81.4 % for the encoder and 81.4 → 77.1 % for the rest of the stack. The line
    /// said "6,6 pp ≈ 678 MB, encodern" and that OBS had not quit.
    /// </remarks>
    [Fact]
    public void AnOBSQuitSecondsAfterTheStreamAndASessionEndingSecondsLaterAreBothRead()
    {
        var monitor = new ObsVramFootprintMonitor();

        Play(monitor, from: 0, seconds: 30, vramPercent: 83.6, streaming: true, running: true, gamePresenting: true);
        Play(monitor, from: 30, seconds: 8, vramPercent: 81.4, streaming: false, running: true, gamePresenting: true);
        Play(monitor, from: 38, seconds: 6, vramPercent: 77.1, streaming: false, running: false, gamePresenting: true);

        var report = monitor.Summary(sessionEnding: true);

        Assert.NotNull(report?.Encoder);
        Assert.Equal(2.2, report.Encoder.PercentagePoints, 1);

        Assert.NotNull(report.RestOfStack);
        Assert.Equal(4.3, report.RestOfStack.PercentagePoints, 1);

        Assert.Contains("webbkällor", report.Message, StringComparison.Ordinal);
        Assert.Contains("VARNING: stegen låg 8 s isär", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("har inte avslutats", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Before the session ends, OBS having quit is still said, with its step not yet read.
    /// </summary>
    [Fact]
    public void AQuitWhoseStepIsNotReadYetIsNotCalledARunningOBS()
    {
        var monitor = new ObsVramFootprintMonitor();

        Play(monitor, from: 0, seconds: 30, vramPercent: 83.6, streaming: true, running: true, gamePresenting: true);
        Play(monitor, from: 30, seconds: 8, vramPercent: 81.4, streaming: false, running: true, gamePresenting: true);
        Play(monitor, from: 38, seconds: 6, vramPercent: 77.1, streaming: false, running: false, gamePresenting: true);

        var report = monitor.Summary();

        Assert.NotNull(report?.Encoder);
        Assert.Equal(2.2, report.Encoder.PercentagePoints, 1);
        Assert.Null(report.RestOfStack);
        Assert.Contains("inte avläst", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("har inte avslutats", report.Message, StringComparison.Ordinal);
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
        bool running,
        bool gamePresenting = false)
    {
        for (var second = 0; second < seconds; second++)
        {
            var at = Start.AddSeconds(from + second);
            monitor.Observe(Obs(at, streaming, running));

            if (gamePresenting)
            {
                monitor.ObserveGameFrame(at);
            }

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
