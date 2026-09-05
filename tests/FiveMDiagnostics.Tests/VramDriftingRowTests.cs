namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// A row that grows faster than the card it is supposed to be inside is a fault in the counter, and it
/// is visible long before the number becomes impossible.
/// </summary>
/// <remarks>
/// The three faults this codebase has met were all slow. <c>obs64</c> went 0.59 GB to 579 GB across seven
/// hours and was only caught when it passed the card's own size; <c>Voicemod</c> went 9.36 to 16.44 the
/// same way; the game's own row went 4.3 to 8.2 GB while the adapter moved a tenth of a gigabyte, and was
/// never caught at all — which is the one that mattered, because the budget is the card's figure minus
/// that row.
/// </remarks>
public sealed class VramDriftingRowTests
{
    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    private static readonly DateTimeOffset Start = new(2026, 9, 4, 21, 10, 0, TimeSpan.Zero);

    /// <summary>
    /// The game's row climbs a gigabyte and a half while the card stands still. Nothing about that is
    /// arithmetically impossible; it is still the counter rather than the game.
    /// </summary>
    [Fact]
    public void ARowThatOutgrowsTheCardIsNamed()
    {
        var monitor = new VramAccountingMonitor();

        Observe(monitor, Start, gameGigabytes: 4.3, cardGigabytes: 8.0);
        Assert.Empty(monitor.ObserveDrift(Sample(Start, gameGigabytes: 4.3)));

        var later = Start.AddMinutes(40);
        Observe(monitor, later, gameGigabytes: 5.9, cardGigabytes: 8.1);

        var drifting = monitor.ObserveDrift(Sample(later, gameGigabytes: 5.9));

        Assert.Single(drifting);
        Assert.Equal("FiveM_b3407_GTAProcess", drifting[0].Process.ProcessName);
        Assert.Contains("driver", drifting[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A game filling its texture budget grows and so does the card. That is what an honest allocation
    /// looks like, and it must never be reported as drift — it is most of the first hour of every
    /// session.
    /// </summary>
    [Fact]
    public void AGameFillingItsBudgetIsNotDrifting()
    {
        var monitor = new VramAccountingMonitor();

        for (var minute = 0; minute <= 60; minute += 5)
        {
            var at = Start.AddMinutes(minute);
            var game = 4.3 + (minute * 0.03);
            Observe(monitor, at, gameGigabytes: game, cardGigabytes: 6.0 + (minute * 0.03));

            Assert.Empty(monitor.ObserveDrift(Sample(at, gameGigabytes: game)));
        }
    }

    /// <summary>
    /// Said once. The counter keeps producing the same row, so a verdict repeated per sample would be
    /// written every five seconds for the rest of the evening.
    /// </summary>
    [Fact]
    public void TheVerdictIsWrittenOnce()
    {
        var monitor = new VramAccountingMonitor();

        Observe(monitor, Start, gameGigabytes: 4.3, cardGigabytes: 8.0);
        monitor.ObserveDrift(Sample(Start, gameGigabytes: 4.3));

        var later = Start.AddMinutes(40);
        Observe(monitor, later, gameGigabytes: 6.5, cardGigabytes: 8.0);
        Assert.Single(monitor.ObserveDrift(Sample(later, gameGigabytes: 6.5)));

        var laterStill = Start.AddMinutes(50);
        Observe(monitor, laterStill, gameGigabytes: 7.2, cardGigabytes: 8.0);
        Assert.Empty(monitor.ObserveDrift(Sample(laterStill, gameGigabytes: 7.2)));
    }

    /// <summary>
    /// The mark travels on the sample, and the budget monitor refuses to split against it rather than
    /// printing a division built on a number that has been shown not to hold.
    /// </summary>
    [Fact]
    public void TheBudgetRefusesToSplitAgainstADriftingGameRow()
    {
        var budget = new VramBudgetMonitor();
        budget.Observe(Adapter(Start, usedGigabytes: 8.0));

        var report = budget.Observe(Sample(Start, gameGigabytes: 5.9) with
        {
            DriftingProcessIds = [18704],
        });

        Assert.NotNull(report);
        Assert.Contains("kan inte delas upp", report.Message, StringComparison.Ordinal);
        Assert.Contains("driftande", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("skrivbordet håller", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same refusal: a game row above the whole card's own figure. Floored at zero
    /// this printed "skrivbordet håller 0,0 GB" all evening on 4 September while dwm held 1.29, and the
    /// closing recommendation was computed against that empty card.
    /// </summary>
    [Fact]
    public void TheBudgetRefusesToSplitWhenTheGameRowExceedsTheCard()
    {
        var budget = new VramBudgetMonitor();
        budget.Observe(Adapter(Start, usedGigabytes: 8.1));

        var report = budget.Observe(Sample(Start, gameGigabytes: 8.2));

        Assert.NotNull(report);
        Assert.Contains("kan inte delas upp", report.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, report.DesktopBytes);
        Assert.Equal(0UL, report.StreamStackBytes);

        // The card's own figures are still true and still stated.
        Assert.Equal((ulong)(8.1 * Gigabyte), report.AdapterUsedBytes);
    }

    /// <summary>
    /// The refusal is said once, and it must not spend the budget line's own slot: a session whose first
    /// sampling happened to be a bad one still gets its budget line once the table adds up.
    /// </summary>
    [Fact]
    public void TheRefusalDoesNotCostTheSessionItsBudgetLine()
    {
        var budget = new VramBudgetMonitor();
        budget.Observe(Adapter(Start, usedGigabytes: 8.1));

        Assert.NotNull(budget.Observe(Sample(Start, gameGigabytes: 8.2)));

        // Said once while it lasts.
        budget.Observe(Adapter(Start.AddSeconds(5), usedGigabytes: 8.1));
        Assert.Null(budget.Observe(Sample(Start.AddSeconds(5), gameGigabytes: 8.2)));

        // And the real line arrives as soon as the table adds up again.
        budget.Observe(Adapter(Start.AddSeconds(10), usedGigabytes: 8.0));
        var report = budget.Observe(Sample(Start.AddSeconds(10), gameGigabytes: 5.9));

        Assert.NotNull(report);
        Assert.Contains("skrivbordet håller", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A believable table still gets its split. The refusal is a state, not a new default.
    /// </summary>
    [Fact]
    public void ABelievableTableIsStillSplit()
    {
        var budget = new VramBudgetMonitor();
        budget.Observe(Adapter(Start, usedGigabytes: 8.0));

        var report = budget.Observe(Sample(Start, gameGigabytes: 5.9));

        Assert.NotNull(report);
        Assert.Contains("skrivbordet håller", report.Message, StringComparison.Ordinal);
        Assert.Equal(2.1, (report.DesktopBytes + report.StreamStackBytes) / (double)Gigabyte, precision: 2);
    }

    /// <summary>
    /// A process id recycled to a different program inherits no drift verdict.
    /// </summary>
    /// <remarks>
    /// Windows reuses process ids, and this verdict lasts the session. The double-count proof beside it
    /// has dropped a recycled id since it was written; the drift proof kept the name and never compared
    /// it, so an id that had drifted handed a permanent verdict to whatever started next. If that
    /// happened to be the game, <c>VramBudgetMonitor</c> would refuse to split the budget for the rest of
    /// the evening on the strength of its predecessor's counter.
    /// </remarks>
    [Fact]
    public void ARecycledProcessIdDoesNotInheritTheDriftVerdict()
    {
        var monitor = new VramAccountingMonitor();

        Observe(monitor, Start, gameGigabytes: 4.3, cardGigabytes: 8.0);

        // The first reading is where the row and the card are anchored against each other.
        Assert.Empty(monitor.ObserveDrift(Sample(Start, gameGigabytes: 4.3)));

        var later = Start.AddMinutes(40);
        Observe(monitor, later, gameGigabytes: 5.9, cardGigabytes: 8.1);
        Assert.Single(monitor.ObserveDrift(Sample(later, gameGigabytes: 5.9)));

        // The same id, a different program. Nothing about it has been measured.
        var after = later.AddMinutes(5);
        monitor.Observe(Adapter(after, usedGigabytes: 8.1));
        var annotated = monitor.Annotate(Recycled(after), out _);

        Assert.False(annotated.IsDrifting(annotated.Processes[0]));
        Assert.DoesNotContain(18704, annotated.DriftingProcessIds ?? []);
    }

    /// <summary>The drifting id, reused by an unrelated program.</summary>
    private static GpuProcessMemorySample Recycled(DateTimeOffset timestamp)
    {
        return new GpuProcessMemorySample(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(18704, "chrome", (ulong)(0.4 * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(4204, "dwm", (ulong)(1.29 * Gigabyte), 0, 1),
            ]);
    }

    private static void Observe(VramAccountingMonitor monitor, DateTimeOffset at, double gameGigabytes, double cardGigabytes)
    {
        monitor.Observe(Adapter(at, cardGigabytes));
        monitor.Annotate(Sample(at, gameGigabytes), out _);
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double usedGigabytes)
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
            AdapterCount: 1);
    }

    private static GpuProcessMemorySample Sample(DateTimeOffset timestamp, double gameGigabytes)
    {
        return new GpuProcessMemorySample(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(18704, "FiveM_b3407_GTAProcess", (ulong)(gameGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(4204, "dwm", (ulong)(1.29 * Gigabyte), 0, 1),
            ]);
    }
}
