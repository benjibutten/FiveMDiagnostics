namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// Minutes that held a hitch series are a mechanism of their own, and the band comparison says what it
/// looks like without them before it calls anything a counter-proof.
/// </summary>
public sealed class VramBandHitchSeriesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The shape of 22 September: a band that hitches twice as often as the rest, and ten minutes of a
    /// series below the band that lift the rest past it.
    /// </summary>
    [Fact]
    public void ASeriesOutsideTheBandDoesNotExonerateIt()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));

        Play(monitor, Start, minutes: 40, vramPercent: 90, hitchesPerMinute: 2);
        Play(monitor, Start.AddMinutes(40), minutes: 100, vramPercent: 80, hitchesPerMinute: 1);
        Play(monitor, Start.AddMinutes(140), minutes: 10, vramPercent: 80, hitchesPerMinute: 40);
        Play(monitor, Start.AddMinutes(150), minutes: 100, vramPercent: 80, hitchesPerMinute: 1);

        var report = monitor.Summary()!;

        Assert.True(report.HitchRatio < 1);
        Assert.Equal(10, report.SeriesMinutes);
        Assert.InRange(report.HitchRatioWithoutSeries!.Value, 1.8, 1.9);
        Assert.False(report.BandCostNothing);
        Assert.DoesNotContain("motbevis", report.Message, StringComparison.Ordinal);
        Assert.Contains("minuterna med hackserier drar upp resten", report.Message, StringComparison.Ordinal);
        Assert.Contains("Utan dem är kvoten", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the series left out neither side hitched, so there is no flip to blame on it either.
    /// </summary>
    [Fact]
    public void WithoutAComparisonLeftTheLineDrawsNoConclusion()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));

        Play(monitor, Start, minutes: 40, vramPercent: 90, hitchesPerMinute: 0);
        Play(monitor, Start.AddMinutes(40), minutes: 100, vramPercent: 80, hitchesPerMinute: 0);
        Play(monitor, Start.AddMinutes(140), minutes: 10, vramPercent: 80, hitchesPerMinute: 40);

        var report = monitor.Summary()!;

        Assert.True(report.HitchRatio < 1);
        Assert.Null(report.HitchRatioWithoutSeries);
        Assert.False(report.BandCostNothing);
        Assert.DoesNotContain("bara för att", report.Message, StringComparison.Ordinal);
        Assert.Contains("ingen slutsats åt något håll", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A series inside the band stays in the band's figure. Leaving it out is a second number beside the
    /// first, never a replacement: on 20 September the band's cost arrived as exactly such a cluster.
    /// </summary>
    [Fact]
    public void ASeriesInsideTheBandIsStatedBesideTheRatioNotRemovedFromIt()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));

        Play(monitor, Start, minutes: 60, vramPercent: 80, hitchesPerMinute: 1);
        Play(monitor, Start.AddMinutes(60), minutes: 10, vramPercent: 92, hitchesPerMinute: 30);

        var report = monitor.Summary()!;

        Assert.True(report.HitchRatio > 1);
        Assert.Equal(10, report.SeriesMinutes);
        Assert.Null(report.HitchRatioWithoutSeries);
        Assert.Contains("högre än i resten", report.Message, StringComparison.Ordinal);
        Assert.Contains("utan dem går kvoten inte att räkna", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One adapter reading every five seconds and one frame a second, <paramref name="hitchesPerMinute"/>
    /// of them 90 ms.
    /// </summary>
    private static void Play(VramPressureBandMonitor monitor, DateTimeOffset from, int minutes, double vramPercent, int hitchesPerMinute)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            var minuteStart = from.AddMinutes(minute);

            for (var reading = 0; reading < 12; reading++)
            {
                monitor.Observe(Adapter(minuteStart.AddSeconds(reading * 5), vramPercent));
            }

            for (var frame = 0; frame < 60; frame++)
            {
                monitor.Observe(Frame(minuteStart.AddSeconds(frame), frame < hitchesPerMinute ? 90 : 16.7));
            }
        }
    }

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

    private static FrameTelemetrySample Frame(DateTimeOffset timestamp, double frameTimeMs)
    {
        return new FrameTelemetrySample(
            timestamp,
            frameTimeMs,
            GpuBusyMs: 8,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe");
    }
}
