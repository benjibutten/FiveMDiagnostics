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

    [Theory]
    [InlineData("continuous", true)]
    [InlineData("gap", false)]
    [InlineData("missing", false)]
    [InlineData("unavailable", false)]
    [InlineData("stale", false)]
    public void RecoveryRequiresContinuousValidObservations(string interruption, bool expectedRecovery)
    {
        var monitor = new VramAccountingMonitor();
        Observe(monitor, Start, 4.3, 8.0);
        monitor.ObserveDrift(Sample(Start, 4.3));
        var driftAt = Start.AddMinutes(40);
        Observe(monitor, driftAt, 5.9, 8.1);
        Assert.Single(monitor.ObserveDrift(Sample(driftAt, 5.9))!.Rows);

        var recoveryAt = driftAt.AddSeconds(5);
        for (var seconds = 0; seconds <= 900; seconds += 5)
        {
            if (interruption == "gap" && seconds > 0 && seconds < 900)
                continue;

            var at = recoveryAt.AddSeconds(seconds);
            monitor.Observe(Adapter(interruption == "stale" && seconds == 450 ? at.AddSeconds(-20) : at, 8.1));
            var sample = Sample(at, 4.3);
            if (seconds == 450 && interruption == "missing")
                sample = sample with { Processes = sample.Processes.Where(row => row.ProcessId != 18704).ToArray() };
            if (seconds == 450 && interruption == "unavailable")
                sample = sample with { IsAvailable = false };
            monitor.ObserveDrift(sample);
        }

        var annotated = monitor.Annotate(Sample(recoveryAt.AddSeconds(900), 4.3), out _);
        Assert.Equal(!expectedRecovery, annotated.IsDrifting(annotated.Processes[0]));
    }

    /// <summary>
    /// The game's row climbs a gigabyte and a half while the card stands still. Nothing about that is
    /// arithmetically impossible; it is still the counter rather than the game.
    /// </summary>
    [Fact]
    public void ARowThatOutgrowsTheCardIsNamed()
    {
        var monitor = new VramAccountingMonitor();

        Observe(monitor, Start, gameGigabytes: 4.3, cardGigabytes: 8.0);
        Assert.Null(monitor.ObserveDrift(Sample(Start, gameGigabytes: 4.3)));

        var later = Start.AddMinutes(40);
        Observe(monitor, later, gameGigabytes: 5.9, cardGigabytes: 8.1);

        var drift = monitor.ObserveDrift(Sample(later, gameGigabytes: 5.9));

        Assert.NotNull(drift);
        Assert.Single(drift!.Rows);
        Assert.Equal("FiveM_b3407_GTAProcess", drift.Rows[0].Process.ProcessName);
        Assert.Contains("driver", drift.Rows[0].Message, StringComparison.Ordinal);
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

            Assert.Null(monitor.ObserveDrift(Sample(at, gameGigabytes: game)));
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
        Assert.Single(monitor.ObserveDrift(Sample(later, gameGigabytes: 6.5))!.Rows);

        var laterStill = Start.AddMinutes(50);
        Observe(monitor, laterStill, gameGigabytes: 7.2, cardGigabytes: 8.0);
        Assert.Null(monitor.ObserveDrift(Sample(laterStill, gameGigabytes: 7.2)));
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
    /// A card that gives memory back does not turn every stationary row into a drifter.
    /// </summary>
    /// <remarks>
    /// The excess is the row's growth minus the card's, so a card that shrank makes the excess positive
    /// for a row that has not moved at all. On 5 September the card released 0.71 GB and the session log
    /// filled with twenty warnings, fourteen of them about rows that had grown 0.00 GB — and they buried
    /// the two that had grown 80 GB and 0.32. The rows that stayed put are counted in one sentence.
    /// </remarks>
    [Fact]
    public void RowsThatDidNotGrowAreCountedRatherThanNamed()
    {
        var monitor = new VramAccountingMonitor();

        Observe(monitor, Start, gameGigabytes: 6.3, cardGigabytes: 8.7);
        Assert.Null(monitor.ObserveDrift(Sample(Start, gameGigabytes: 6.3)));

        // Twenty minutes later the card has given back three quarters of a gigabyte, the game's row has
        // climbed a gigabyte, and every other row is exactly where it was.
        var later = Start.AddMinutes(20);
        Observe(monitor, later, gameGigabytes: 7.3, cardGigabytes: 8.0);

        var drift = monitor.ObserveDrift(Sample(later, gameGigabytes: 7.3));

        Assert.NotNull(drift);
        Assert.Single(drift!.Rows);
        Assert.Equal("FiveM_b3407_GTAProcess", drift.Rows[0].Process.ProcessName);

        // dwm stood still and is accounted for without a paragraph of its own.
        Assert.Equal(1, drift.SteadyRows);
        Assert.Contains("Ytterligare 1 processrader", drift.SteadySummary!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row that gained what the card gained is not drifting, however fast both of them grew.
    /// </summary>
    /// <remarks>
    /// The rate test cannot tell "this row took memory nobody else saw" from "this row and the card took
    /// the same memory, read a few seconds apart". On 5 September the game's row went +5.58 GB while the
    /// card went +5.20; the 0.38 GB between them is the skew between two collectors, and over a quarter
    /// of an hour it extrapolates to 1.5 GB an hour — past the bar. The game's own row was called
    /// drifting, and <c>VramBudgetMonitor</c> then refused to split the budget for the rest of the
    /// evening.
    /// </remarks>
    [Fact]
    public void ARowThatGrewWithTheCardIsNotDrifting()
    {
        var monitor = new VramAccountingMonitor();

        Observe(monitor, Start, gameGigabytes: 2.0, cardGigabytes: 3.0);
        Assert.Null(monitor.ObserveDrift(Sample(Start, gameGigabytes: 2.0)));

        // A quarter of an hour of the game filling its texture budget, with the card following it.
        var later = Start.AddMinutes(15);
        Observe(monitor, later, gameGigabytes: 7.58, cardGigabytes: 8.20);

        Assert.Null(monitor.ObserveDrift(Sample(later, gameGigabytes: 7.58)));
    }

    /// <summary>
    /// The sentence accounting for the steady rows says why they were steady, for both reasons.
    /// </summary>
    /// <remarks>
    /// A row is counted steady either because it barely grew or because the card gained the same memory,
    /// and the summary used to claim the first about both. A game filling its texture budget at a dozen
    /// gigabytes an hour was reported to the reader as having grown slower than half a gigabyte an hour — in the same
    /// line as a row that really was drifting, which is where the reader goes to check.
    /// </remarks>
    [Fact]
    public void TheSteadySummaryDoesNotClaimARowGrewSlowlyWhenTheCardKeptUp()
    {
        var monitor = new VramAccountingMonitor();

        monitor.Observe(Adapter(Start, usedGigabytes: 3.0));
        Assert.Null(monitor.ObserveDrift(Rows(Start, gameGigabytes: 2.0, voicemodGigabytes: 0.5)));

        // A quarter of an hour of the game filling its budget with the card following it. Voicemod took
        // less than the card gained, so its anchor moves here and the game's does not.
        var filling = Start.AddMinutes(15);
        monitor.Observe(Adapter(filling, usedGigabytes: 8.2));
        Assert.Null(monitor.ObserveDrift(Rows(filling, gameGigabytes: 7.58, voicemodGigabytes: 3.0)));

        // Another quarter of an hour: the card has stopped moving and Voicemod's row has not.
        var drifting = filling.AddMinutes(15);
        monitor.Observe(Adapter(drifting, usedGigabytes: 8.25));
        var drift = monitor.ObserveDrift(Rows(drifting, gameGigabytes: 7.58, voicemodGigabytes: 4.2));

        Assert.NotNull(drift);
        Assert.Equal("Voicemod", drift!.Rows.Single().Process.ProcessName);

        // The game's row cleared both rate bars — 12 GB/h of its own growth — and is steady only because
        // the card gained the same memory alongside it.
        Assert.Equal(1, drift.SteadyRows);
        Assert.Contains("i takt med kortets egen tillväxt", drift.SteadySummary!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row that stepped once and then tracked the card perfectly must still recover without the process
    /// restarting.
    /// </summary>
    /// <remarks>
    /// Before the anchor was periodically refreshed, a row already marked drifting was judged forever
    /// against the divergence measured at the moment it was first proven — so "excess &lt;= 0" was asking
    /// whether the row had given the whole step back, which a row that has merely stopped taking more can
    /// never do. On 2026-09-11 the game's row stepped once, the game never restarted for the rest of the
    /// eight-hour session, and the budget breakdown stayed refused the whole time; on 2026-09-10 a restart
    /// reset the anchor to zero and recovery happened "for free", which is what made it look
    /// restart-dependent rather than broken. This reproduces the 2026-09-11 shape directly: no restart,
    /// row and card both flat after the step.
    /// </remarks>
    [Fact]
    public void ARowThatStepsOnceAndThenTracksTheCardRecoversWithoutARestart()
    {
        var monitor = new VramAccountingMonitor();

        Observe(monitor, Start, gameGigabytes: 4.3, cardGigabytes: 8.0);
        Assert.Null(monitor.ObserveDrift(Sample(Start, gameGigabytes: 4.3)));

        var driftAt = Start.AddMinutes(40);
        Observe(monitor, driftAt, gameGigabytes: 5.9, cardGigabytes: 8.1);
        Assert.Single(monitor.ObserveDrift(Sample(driftAt, gameGigabytes: 5.9))!.Rows);

        GpuProcessMemorySample annotated = monitor.Annotate(Sample(driftAt, gameGigabytes: 5.9), out _);
        Assert.True(annotated.IsDrifting(annotated.Processes[0]), "the step itself should still be flagged");

        // Forty minutes with the row exactly where it stepped to and the card exactly where it was — no
        // further growth on either side, and no process restart. Sampled every five seconds, the process
        // collector's real cadence: the recovery streak is dropped once ten seconds pass without a
        // reading, so a coarser test cadence would fail for a reason that has nothing to do with the fix.
        for (var seconds = 5; seconds <= 40 * 60; seconds += 5)
        {
            var at = driftAt.AddSeconds(seconds);
            monitor.Observe(Adapter(at, usedGigabytes: 8.1));
            monitor.ObserveDrift(Sample(at, gameGigabytes: 5.9));
            annotated = monitor.Annotate(Sample(at, gameGigabytes: 5.9), out _);
        }

        Assert.False(
            annotated.IsDrifting(annotated.Processes[0]),
            "a row that tracked the card perfectly for forty minutes never recovered without a process restart");
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
        Assert.Null(monitor.ObserveDrift(Sample(Start, gameGigabytes: 4.3)));

        var later = Start.AddMinutes(40);
        Observe(monitor, later, gameGigabytes: 5.9, cardGigabytes: 8.1);
        Assert.Single(monitor.ObserveDrift(Sample(later, gameGigabytes: 5.9))!.Rows);

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

    /// <summary>The game and one neighbour, both of which the caller moves.</summary>
    private static GpuProcessMemorySample Rows(DateTimeOffset timestamp, double gameGigabytes, double voicemodGigabytes)
    {
        return new GpuProcessMemorySample(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(18704, "FiveM_b3407_GTAProcess", (ulong)(gameGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(9128, "Voicemod", (ulong)(voicemodGigabytes * Gigabyte), 0, 1),
            ]);
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
