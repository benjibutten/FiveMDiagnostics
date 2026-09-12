namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// One evening, one bar. Four monitors counted hitches against four definitions of the word until
/// 2026-09-12 — two refreshes in the focus line, two copies of a cadence median in capture cost and the
/// VRAM band, and whatever the focus line held in the half-hour table. At 59.94 Hz they all landed near
/// 33.3 ms, so the divergence never showed in a summary; it showed in the notes, where the same evening
/// has been written up as 1.4× and as 3.4× for one figure. The notes compare evenings on the decimal.
/// </summary>
public sealed class HitchThresholdTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 19, 0, 0, TimeSpan.Zero);

    private const double SixtyHertz = 60;

    /// <summary>
    /// The same frames through all four monitors, which must produce one hitch count and one bar.
    /// </summary>
    /// <remarks>
    /// Frames are fed in the order the session feeds them, focus gate first and the bar after it, so the
    /// warm-up each monitor holds back is drained a frame apart and still has to come out even.
    /// </remarks>
    [Fact]
    public void AllFourMonitorsCountTheSameHitches()
    {
        var hitch = new HitchThreshold(SixtyHertz);
        var focus = new GameFocusMonitor(hitch);
        var captureCost = new CaptureCostMonitor(hitch);
        var band = new VramPressureBandMonitor(hitch);
        var halfHour = new HalfHourBreakdownMonitor(hitch);

        focus.Observe(Focus(Start, gameHasFocus: true));
        captureCost.RecordCaptureWritten(Start.AddMinutes(3));

        // Eight minutes at a quarter-second per frame, one frame in forty at 90 ms, and the card read
        // twice a second so every frame pairs with a reading of its own.
        const int Frames = 1920;
        const int HitchEvery = 40;

        for (var index = 0; index < Frames; index++)
        {
            var at = Start.AddMilliseconds(index * 250);
            var frameTimeMs = index % HitchEvery == HitchEvery - 1 ? 90 : 16.7;

            if (index % 2 == 0)
            {
                band.Observe(Adapter(at, vramPercent: index < Frames / 2 ? 70 : 92));
            }

            focus.ObserveFrame(at, frameTimeMs);
            hitch.Observe(frameTimeMs);
            captureCost.Observe(Frame(at, frameTimeMs));
            band.Observe(Frame(at, frameTimeMs));
            halfHour.ObserveFrame(at, frameTimeMs, cpuBusyMs: 10);
        }

        focus.Observe(Focus(Start.AddMinutes(8), gameHasFocus: true));

        var focusReport = focus.Summary();
        var captureReport = captureCost.Summary();
        var bandReport = band.Summary();
        var halfHourReport = halfHour.Summary();

        Assert.NotNull(focusReport);
        Assert.NotNull(captureReport);
        Assert.NotNull(bandReport);
        Assert.NotNull(halfHourReport);

        // A row carries the rate rather than the count, so the count is taken back out of it.
        var halfHourHitches = (int)halfHourReport.Rows.Sum(row => Math.Round(row.HitchesPerHour * row.PlayTime.TotalHours));

        Assert.Equal(Frames / HitchEvery, focusReport.HitchesInPlay);
        Assert.Equal(Frames / HitchEvery, captureReport.Hitches);
        Assert.Equal(Frames / HitchEvery, bandReport.InBandHitches + bandReport.OutsideHitches);
        Assert.Equal(Frames / HitchEvery, halfHourHitches);

        Assert.Equal(0, focusReport.HitchesExcluded);
        Assert.Equal(0, bandReport.UnpairedFrames);

        // Twice the cadence the evening actually held, which the lines quote at each other.
        Assert.Equal(33.4, focusReport.HitchThresholdMs, 1);
        Assert.Equal(focusReport.HitchThresholdMs, captureReport.HitchThresholdMs);
        Assert.Equal(focusReport.HitchThresholdMs, bandReport.HitchThresholdMs);
    }

    /// <summary>
    /// The incident floor is not the hitch bar. Hitch frequency is the measurement the notes compare
    /// evenings on, so it has to read the same before and after the detector stopped opening windows for
    /// frames under 100 ms.
    /// </summary>
    /// <remarks>
    /// The same series as <see cref="AllFourMonitorsCountTheSameHitches"/>, whose 90 ms hitches sit above
    /// twice the cadence and below the floor — the band where the two definitions could have been
    /// confused with each other. The monitors count every one of them; the detector opens nothing.
    /// </remarks>
    [Fact]
    public void TheIncidentFloorLeavesTheHitchCountAlone()
    {
        var hitch = new HitchThreshold(SixtyHertz);
        var focus = new GameFocusMonitor(hitch);
        var captureCost = new CaptureCostMonitor(hitch);
        var band = new VramPressureBandMonitor(hitch);
        var halfHour = new HalfHourBreakdownMonitor(hitch);
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), SixtyHertz);

        focus.Observe(Focus(Start, gameHasFocus: true));
        captureCost.RecordCaptureWritten(Start.AddMinutes(3));

        const int Frames = 1920;
        const int HitchEvery = 40;

        for (var index = 0; index < Frames; index++)
        {
            var at = Start.AddMilliseconds(index * 250);
            var frameTimeMs = index % HitchEvery == HitchEvery - 1 ? 90 : 16.7;

            if (index % 2 == 0)
            {
                band.Observe(Adapter(at, vramPercent: index < Frames / 2 ? 70 : 92));
            }

            focus.ObserveFrame(at, frameTimeMs);
            hitch.Observe(frameTimeMs);
            captureCost.Observe(Frame(at, frameTimeMs));
            band.Observe(Frame(at, frameTimeMs));
            halfHour.ObserveFrame(at, frameTimeMs, cpuBusyMs: 10);
            detector.Observe(Frame(at, frameTimeMs));
        }

        focus.Observe(Focus(Start.AddMinutes(8), gameHasFocus: true));

        var focusReport = focus.Summary();
        var captureReport = captureCost.Summary();
        var bandReport = band.Summary();
        var halfHourReport = halfHour.Summary();
        var halfHourHitches = (int)halfHourReport!.Rows.Sum(row => Math.Round(row.HitchesPerHour * row.PlayTime.TotalHours));

        Assert.Equal(Frames / HitchEvery, focusReport!.HitchesInPlay);
        Assert.Equal(Frames / HitchEvery, captureReport!.Hitches);
        Assert.Equal(Frames / HitchEvery, bandReport!.InBandHitches + bandReport.OutsideHitches);
        Assert.Equal(Frames / HitchEvery, halfHourHitches);

        Assert.Equal(0, detector.TriggerCount);

        // The detector holds its first 120 frames back for its own baseline, so the two hitches inside
        // that warm-up never reach the floor at all.
        Assert.Equal((Frames / HitchEvery) - 2, detector.HitchesBelowFloor);
    }

    /// <summary>
    /// The floor. A game running at half the panel's rate must not have every frame called a hitch, and
    /// a frame inside two refreshes must not be called one however slowly the session is running.
    /// </summary>
    [Fact]
    public void TheBarIsTwiceTheCadenceButNeverUnderTwoRefreshes()
    {
        var capped = new HitchThreshold(120);
        var fast = new HitchThreshold(120);

        for (var index = 0; index < 600; index++)
        {
            capped.Observe(16.7);
            fast.Observe(8.3);
        }

        Assert.Equal(33.4, capped.ThresholdMs, 1);

        // The cadence is under one refresh, so the refresh is what counts.
        Assert.Equal(16.7, fast.ThresholdMs, 1);
    }

    /// <summary>
    /// A session that ends before the warm-up fills still gets a bar, and every monitor gets the same
    /// one — an evening cut short by the machine going off is the normal way sessions end here.
    /// </summary>
    [Fact]
    public void ASessionShorterThanTheWarmUpStillGetsABar()
    {
        var hitch = new HitchThreshold(SixtyHertz);
        var halfHour = new HalfHourBreakdownMonitor(hitch);

        for (var index = 0; index < 40; index++)
        {
            var frameTimeMs = index % 10 == 9 ? 90 : 16.7;
            hitch.Observe(frameTimeMs);
            halfHour.ObserveFrame(Start.AddMilliseconds(index * 250), frameTimeMs, cpuBusyMs: 10);
        }

        var report = halfHour.Summary();

        Assert.NotNull(report);
        Assert.Equal(4, (int)report.Rows.Sum(row => Math.Round(row.HitchesPerHour * row.PlayTime.TotalHours)));
        Assert.Equal(33.4, hitch.ThresholdMs, 1);
    }

    private static WindowFocusSample Focus(DateTimeOffset at, bool gameHasFocus) =>
        new(at, gameHasFocus, gameHasFocus ? 24400 : 9876, "FiveM_b3407_GTAProcess");

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double vramPercent)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 60,
            MemoryBandwidthUtilizationPercent: 20,
            UsedVramBytes: (ulong)(Total * vramPercent / 100),
            TotalVramBytes: Total,
            EncoderUtilizationPercent: 12,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 60,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    private static FrameTelemetrySample Frame(DateTimeOffset timestamp, double frameTimeMs) =>
        new(
            timestamp,
            frameTimeMs,
            GpuBusyMs: 5,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: 10,
            CpuWaitMs: 6);
}
