namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The per-process VRAM table drifted a gigabyte away from the adapter's own figure between two
/// sessions, and nothing in the app noticed.
/// </summary>
/// <remarks>
/// Measured on the same machine one evening apart: the sum of every process except the one known-bad
/// row sat +0.17 GB over the adapter on 25 August and +1.11 GB over it on 26 August. A sum above the
/// adapter's used figure is impossible — a process cannot hold VRAM the card does not report as in use
/// — so the second reading means a row is being counted twice. It was found by exporting both logs and
/// doing the arithmetic by hand, a session later.
/// </remarks>
public sealed class VramAccountingMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 17, 58, 1, TimeSpan.Zero);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    [Fact]
    public void TheFirstComparisonIsReportedImmediately()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44));

        var report = monitor.Observe(Processes(Start, sumGigabytes: 7.30));

        Assert.NotNull(report);
        Assert.False(report!.IsImplausible);
        Assert.Contains("VRAM-avstämning", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 25 August shape: a sum a little under the adapter's, which is what the two accountings are
    /// supposed to look like. VRAM also holds framebuffers belonging to no running process.
    /// </summary>
    [Fact]
    public void TheOrdinaryGapIsNotFlagged()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.83));

        var report = monitor.Observe(Processes(Start, sumGigabytes: 9.00));

        Assert.NotNull(report);
        Assert.False(report!.IsImplausible);
        Assert.DoesNotContain("dubbelräknar", report.Message, StringComparison.Ordinal);
    }

    /// <summary>The 26 August shape: +1.11 GB, which cannot be a measurement.</summary>
    [Fact]
    public void ASumAboveTheAdapterIsFlaggedAsDoubleCounting()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44));

        var report = monitor.Observe(Processes(Start, sumGigabytes: 8.55));

        Assert.NotNull(report);
        Assert.True(report!.IsImplausible, $"a sum {report.DifferenceBytes / 1024d / 1024 / 1024:F2} GB over the adapter passed");
        Assert.Contains("dubbelräknar", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItReportsOnACadenceRatherThanEverySample()
    {
        var monitor = new VramAccountingMonitor(TimeSpan.FromMinutes(30));

        monitor.Observe(Adapter(Start, usedGigabytes: 7.44));
        Assert.NotNull(monitor.Observe(Processes(Start, sumGigabytes: 7.30)));

        // Five seconds later, the next process sample: nothing to say yet.
        monitor.Observe(Adapter(Start.AddSeconds(5), usedGigabytes: 7.45));
        Assert.Null(monitor.Observe(Processes(Start.AddSeconds(5), sumGigabytes: 7.31)));

        monitor.Observe(Adapter(Start.AddMinutes(31), usedGigabytes: 7.60));
        Assert.NotNull(monitor.Observe(Processes(Start.AddMinutes(31), sumGigabytes: 7.40)));
    }

    /// <summary>
    /// Two readings far apart in time measure the gap between two clocks, not between two accountings.
    /// Silence is the right answer when the comparison cannot be made.
    /// </summary>
    [Fact]
    public void AStaleAdapterReadingProducesNoComparison()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44));

        Assert.Null(monitor.Observe(Processes(Start.AddMinutes(2), sumGigabytes: 8.55)));
    }

    [Fact]
    public void NothingIsReportedBeforeAnAdapterReadingArrives()
    {
        var monitor = new VramAccountingMonitor();

        Assert.Null(monitor.Observe(Processes(Start, sumGigabytes: 7.30)));
    }

    /// <summary>
    /// The impossible row is already excluded from the sum, so it must not be what trips the check —
    /// otherwise every session with a 209 GB row reports double counting and says nothing about the
    /// rows that are actually wrong.
    /// </summary>
    [Fact]
    public void TheKnownImpossibleRowDoesNotTripTheCheck()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44));

        var sample = new GpuProcessMemorySample(
            Start,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(7768, "FiveM_b3407_GTAProcess", (ulong)(5.18 * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(4656, "obs64", 209UL * Gigabyte, 0, 1),
                new GpuProcessMemoryUsage(24020, "Voicemod", (ulong)(1.17 * Gigabyte), 0, 1),
            ]);

        var report = monitor.Observe(sample);

        Assert.NotNull(report);
        Assert.False(report!.IsImplausible);
    }

    /// <summary>
    /// NVML reads device index 0 for the whole session while the process table is anchored on the
    /// adapter the game holds memory on. With a second GPU present those need not be the same card, and
    /// their difference is then a fact about the hardware rather than about the accounting.
    /// </summary>
    [Fact]
    public void AMultiGpuMachineIsReportedButNeverAccused()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44, adapterCount: 2));

        var report = monitor.Observe(Processes(Start, sumGigabytes: 8.55));

        Assert.NotNull(report);
        Assert.False(report!.IsImplausible, "a two-GPU machine was accused of double counting");
        Assert.Contains("mer än en GPU", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A driver that reported no count is unconfirmed, not assumed safe.
    /// </summary>
    [Fact]
    public void AnUnknownAdapterCountIsTreatedAsUnconfirmed()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44, adapterCount: 0));

        var report = monitor.Observe(Processes(Start, sumGigabytes: 8.55));

        Assert.NotNull(report);
        Assert.False(report!.IsImplausible);
    }

    /// <summary>
    /// The table is cut to the largest holders, so summing it answers "how much do the biggest hold"
    /// rather than "how much is accounted for". Double counting in a process below the cut has to reach
    /// the comparison, or the check is blind exactly where the table is.
    /// </summary>
    [Fact]
    public void TheComparisonUsesTheUntruncatedTotalRatherThanTheTopList()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44));

        var sample = new GpuProcessMemorySample(
            Start,
            IsAvailable: true,
            [new GpuProcessMemoryUsage(7768, "FiveM_b3407_GTAProcess", (ulong)(5.18 * Gigabyte), 0, 1)],
            UnavailableReason: null,
            AllProcessesDedicatedBytes: (ulong)(8.55 * Gigabyte));

        var report = monitor.Observe(sample);

        Assert.NotNull(report);
        Assert.True(report!.IsImplausible, "the overshoot outside the top list was not seen");
        Assert.Equal((ulong)(8.55 * Gigabyte), report.ProcessSumBytes);
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double usedGigabytes, int adapterCount = 1)
    {
        return new GpuTelemetrySample(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 49,
            MemoryBandwidthUtilizationPercent: 16,
            UsedVramBytes: (ulong)(usedGigabytes * Gigabyte),
            TotalVramBytes: 10UL * Gigabyte,
            EncoderUtilizationPercent: 34,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 62,
            ThrottleReasons: [],
            AdapterCount: adapterCount);
    }

    private static GpuProcessMemorySample Processes(DateTimeOffset timestamp, double sumGigabytes)
    {
        return new GpuProcessMemorySample(
            timestamp,
            IsAvailable: true,
            [new GpuProcessMemoryUsage(7768, "FiveM_b3407_GTAProcess", (ulong)(sumGigabytes * Gigabyte), 0, 1)]);
    }
}
