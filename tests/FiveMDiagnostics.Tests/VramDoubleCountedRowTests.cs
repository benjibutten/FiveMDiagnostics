namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The row that passed the sanity cap and was wrong anyway.
/// </summary>
/// <remarks>
/// <c>dwm</c> reported a flat 6.1 GB on a 10 GB card for a whole session and was named "largest in
/// VRAM" in all 154 of that session's incidents, hiding the process that was actually growing. It
/// cleared the absolute 64 GB bound by an order of magnitude, so nothing objected. What settles it is
/// the arithmetic somebody did by hand a session later: the card reported 5.07 GB used at 21:38:44 and
/// one process claimed 6.08 GB of it, which no single process can. In <c>Composed: Copy with GPU GDI</c>
/// the compositor holds a reference to every frame it composes and the counter does not distinguish a
/// shared allocation from an owned one.
/// </remarks>
public sealed class VramDoubleCountedRowTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 19, 38, 44, TimeSpan.Zero);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    [Fact]
    public void ARowLargerThanTheCardsOwnUsageIsExcludedFromTheTopList()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 5.07));

        var annotated = monitor.Annotate(Sample(Start, dwmGigabytes: 6.08), out var proven);

        Assert.Equal("dwm", Assert.Single(proven).Process.ProcessName);
        Assert.Equal("FiveM_b3407_GTAProcess", annotated.Top(1).Single().ProcessName);
    }

    /// <summary>
    /// The proof does not expire. <c>dwm</c>'s row only exceeded the card's own figure while the game
    /// was still filling its texture memory; by 22:38 the card reported 7.33 GB used and the same 6.1 GB
    /// row sat underneath it. Re-deciding per sample would have excluded the row from the first incident
    /// of the evening and named it largest holder in the other 153.
    /// </summary>
    [Fact]
    public void TheExclusionHoldsForTheRestOfTheSessionOnceProved()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 5.07));
        monitor.Annotate(Sample(Start, dwmGigabytes: 6.08), out _);

        var later = Start.AddHours(1);
        monitor.Observe(Adapter(later, usedGigabytes: 7.33));
        var annotated = monitor.Annotate(Sample(later, dwmGigabytes: 6.14), out var proven);

        Assert.Empty(proven);
        Assert.Equal("FiveM_b3407_GTAProcess", annotated.Top(1).Single().ProcessName);
    }

    /// <summary>
    /// A row under the card's figure is a measurement and stays one. The compositor holding a gigabyte
    /// is entirely ordinary, and the rule is about arithmetic rather than about a process name.
    /// </summary>
    [Fact]
    public void AnOrdinaryCompositorRowIsLeftAlone()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 7.44));

        var annotated = monitor.Annotate(Sample(Start, dwmGigabytes: 1.17), out var proven);

        Assert.Empty(proven);
        Assert.Equal(2, annotated.Top(5).Count());
    }

    /// <summary>
    /// The same guard the reconciliation carries. On a machine with more than one GPU the card's figure
    /// and the process table can describe different adapters, and the difference is then a fact about
    /// the hardware rather than an accusation against a process.
    /// </summary>
    [Fact]
    public void ASecondGpuStopsTheAccusation()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 5.07, adapterCount: 2));

        var annotated = monitor.Annotate(Sample(Start, dwmGigabytes: 6.08), out var proven);

        Assert.Empty(proven);
        Assert.Equal("dwm", annotated.Top(1).Single().ProcessName);
    }

    /// <summary>
    /// Excluded from the reports, kept in the log. A row the app quietly discards makes the fault itself
    /// unfindable, and the fault is what says the aggregation is wrong.
    /// </summary>
    [Fact]
    public void TheExcludedRowIsStillCarriedAndNamed()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 5.07));
        var annotated = monitor.Annotate(Sample(Start, dwmGigabytes: 6.08), out _);

        Assert.Contains(annotated.Processes, process => process.ProcessName == "dwm");
        Assert.Equal("dwm", Assert.Single(annotated.UnbelievableProcesses).ProcessName);

        var report = monitor.Observe(annotated);
        Assert.NotNull(report);
        Assert.Contains("dwm", report!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows reuses process ids, and the verdict is about a program's counter rather than about a
    /// number.
    /// </summary>
    /// <remarks>
    /// The exclusion is deliberately permanent for the session, which makes a recycled id expensive: an
    /// unrelated process inheriting it would be dropped from every report for the rest of the evening,
    /// silently, and the row it was hiding is exactly the kind this rule exists to reveal.
    /// </remarks>
    [Fact]
    public void ARecycledProcessIdDoesNotInheritTheVerdict()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 5.07));
        monitor.Annotate(Sample(Start, dwmGigabytes: 6.08), out _);

        var later = Start.AddHours(1);
        monitor.Observe(Adapter(later, usedGigabytes: 7.33));

        // The same id, now held by something else entirely and holding an ordinary amount.
        var recycled = new GpuProcessMemorySample(
            later,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(4204, "chrome", (ulong)(0.08 * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(18704, "FiveM_b3407_GTAProcess", (ulong)(5.9 * Gigabyte), 0, 1),
            ]);

        var annotated = monitor.Annotate(recycled, out _);

        Assert.Empty(annotated.UnbelievableProcesses);
        Assert.Equal(2, annotated.Top(5).Count());
    }

    /// <summary>The same id under the same name is the same program, and keeps its verdict.</summary>
    [Fact]
    public void TheSameProgramKeepsTheVerdictAcrossSamples()
    {
        var monitor = new VramAccountingMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 5.07));
        monitor.Annotate(Sample(Start, dwmGigabytes: 6.08), out _);

        var later = Start.AddHours(1);
        monitor.Observe(Adapter(later, usedGigabytes: 7.33));
        var annotated = monitor.Annotate(Sample(later, dwmGigabytes: 6.14), out _);

        Assert.Equal("dwm", Assert.Single(annotated.UnbelievableProcesses).ProcessName);
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double usedGigabytes, int adapterCount = 1)
    {
        return new GpuTelemetrySample(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 44,
            MemoryBandwidthUtilizationPercent: 21,
            UsedVramBytes: (ulong)(usedGigabytes * Gigabyte),
            TotalVramBytes: 10UL * Gigabyte,
            EncoderUtilizationPercent: 0,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 61,
            ThrottleReasons: [],
            AdapterCount: adapterCount);
    }

    /// <summary>The two rows that matter, with the game where the session actually had it.</summary>
    private static GpuProcessMemorySample Sample(DateTimeOffset timestamp, double dwmGigabytes)
    {
        return new GpuProcessMemorySample(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(4204, "dwm", (ulong)(dwmGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(18704, "FiveM_b3407_GTAProcess", (ulong)(3.71 * Gigabyte), 0, 1),
            ]);
    }
}
