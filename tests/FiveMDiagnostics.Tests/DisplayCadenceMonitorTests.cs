namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The measurement that ended the investigation, replayed from the two sessions that bracket the fix.
/// </summary>
/// <remarks>
/// 28 August: primary display at 120 Hz, secondary at 60, game at 60 fps. 88.68% of frames reached the
/// screen two refreshes apart as they should, 5.80% arrived one refresh early, 4.36% one refresh late
/// and 1.12% two — 11.32% off cadence, and none of it visible in frame time, where 97.16% of presents
/// landed exactly on cadence.
/// <para>
/// 30 August: both displays at about 60 Hz. 99.59% on cadence, 0.41% off.
/// </para>
/// </remarks>
public sealed class DisplayCadenceMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 20, 46, 57, TimeSpan.Zero);

    /// <summary>
    /// A 60 fps game on a 120 Hz panel holds two refreshes between display changes, and a frame that
    /// slips is one refresh out — 8.3 ms, less than a frame time, which is why no frame-time threshold
    /// ever caught it.
    /// </summary>
    [Fact]
    public void TheMismatchedRefreshRateEveningIsMeasuredAtElevenPercent()
    {
        var report = Replay(
            refreshRateHz: 120,
            distribution: new Dictionary<double, int>
            {
                [8.33] = 5_795,
                [16.67] = 88_684,
                [25.00] = 4_357,
                [33.33] = 1_119,
            });

        Assert.NotNull(report);
        Assert.Equal(2, report!.ModalRefreshes);
        Assert.Equal(0.1128, report.OffCadenceShare, 3);
        Assert.Equal(0.0580, report.EarlyShare, 3);

        // One refresh late plus two refreshes late; both are the screen holding a frame too long.
        Assert.Equal(0.0548, report.LateShare, 3);
        Assert.True(report.IsOffCadence);
    }

    /// <summary>
    /// The same game on two synced 60 Hz displays holds one refresh, and the defect is gone. The two
    /// evenings must be comparable despite the cadence itself changing, which is why this is counted in
    /// refreshes rather than milliseconds.
    /// </summary>
    [Fact]
    public void TheSyncedEveningIsMeasuredUnderHalfAPercent()
    {
        var report = Replay(
            refreshRateHz: 59.94,
            distribution: new Dictionary<double, int>
            {
                [8.34] = 136,
                [16.68] = 99_587,
                [33.36] = 247,
            });

        Assert.NotNull(report);
        Assert.Equal(1, report!.ModalRefreshes);
        Assert.Equal(0.0038, report.OffCadenceShare, 3);
        Assert.False(report.IsOffCadence);
    }

    /// <summary>
    /// A freeze is a hitch, not a cadence miss. Frame time reports hitches and reports them better, and
    /// letting a handful of them into this figure would swamp the small constant defect it exists for.
    /// </summary>
    [Fact]
    public void AFreezeIsNotCountedAsACadenceMiss()
    {
        var monitor = new DisplayCadenceMonitor(59.94);
        for (var index = 0; index < 1_000; index++)
        {
            monitor.Observe(Frame(index, 16.68));
        }

        monitor.Observe(Frame(1_000, 1_034.2));

        var report = monitor.Snapshot();
        Assert.NotNull(report);
        Assert.Equal(0, report!.OffCadenceShare, 5);
        Assert.Equal(1_000, report.FrameCount);
    }

    /// <summary>
    /// Frames without a display-change reading contribute nothing rather than counting as on cadence.
    /// PresentMon writes "NA" there for a frame that never reached the screen.
    /// </summary>
    [Fact]
    public void FramesThatNeverReachedTheScreenAreNotCounted()
    {
        var monitor = new DisplayCadenceMonitor(59.94);
        for (var index = 0; index < 700; index++)
        {
            monitor.Observe(Frame(index, index % 2 == 0 ? 16.68 : null));
        }

        Assert.Null(monitor.Snapshot());
    }

    /// <summary>Too few frames means no figure, because the ratio moves percentage points per frame.</summary>
    [Fact]
    public void AShortWindowReportsNothing()
    {
        var monitor = new DisplayCadenceMonitor(59.94);
        for (var index = 0; index < 100; index++)
        {
            monitor.Observe(Frame(index, 16.68));
        }

        Assert.Null(monitor.Snapshot());
    }

    /// <summary>
    /// The advice only appears when there is something to act on. It names the cause the investigation
    /// found, and that is a strong claim to put in front of someone whose cadence is already fine.
    /// </summary>
    [Fact]
    public void TheCauseIsOnlyNamedWhenTheCadenceIsActuallyOff()
    {
        var bad = Replay(120, new Dictionary<double, int> { [8.33] = 600, [16.67] = 4_400 });
        var good = Replay(59.94, new Dictionary<double, int> { [16.68] = 9_959, [33.36] = 41 });

        Assert.Contains("uppdateringsfrekvens", bad!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("uppdateringsfrekvens", good!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The composed share is what says whether the refresh-rate warning written at session start applied
    /// at all, so it counts every frame that named a mode — including the ones with no display-change
    /// figure, which the cadence distribution itself has to skip.
    /// </summary>
    [Fact]
    public void TheComposedShareCountsEveryFrameThatNamedAMode()
    {
        var monitor = new DisplayCadenceMonitor(120);

        Assert.Null(monitor.ComposedShare);

        for (var index = 0; index < 10; index++)
        {
            // Half the frames carry no display-change figure, and none of them carry a cadence.
            monitor.Observe(Frame(index, displayChangeMs: null) with
            {
                PresentMode = index < 3 ? "Composed: Copy with GPU GDI" : "Hardware: Independent Flip",
            });
        }

        // A frame from a capture that never classified the mode contributes to neither side.
        monitor.Observe(Frame(10, displayChangeMs: null));

        Assert.Equal(0.3, monitor.ComposedShare!.Value, 3);
    }

    private static DisplayCadenceReport? Replay(double refreshRateHz, Dictionary<double, int> distribution)
    {
        var monitor = new DisplayCadenceMonitor(refreshRateHz);
        var index = 0;

        foreach (var (displayChangeMs, count) in distribution)
        {
            for (var repeat = 0; repeat < count; repeat++)
            {
                monitor.Observe(Frame(index++, displayChangeMs));
            }
        }

        return monitor.Snapshot();
    }

    private static FrameTelemetrySample Frame(int index, double? displayChangeMs)
    {
        return new FrameTelemetrySample(
            Start.AddMilliseconds(index * 16.68),
            16.68,
            GpuBusyMs: 7.7,
            DisplayLatencyMs: 20,
            MsBetweenPresents: 16.68,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            MsBetweenDisplayChange: displayChangeMs);
    }
}
