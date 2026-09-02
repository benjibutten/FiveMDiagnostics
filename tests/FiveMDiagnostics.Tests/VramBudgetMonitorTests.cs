namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The one figure in six sessions of investigation that predicted an evening rather than describing it.
/// </summary>
/// <remarks>
/// Measured on 28 August: with OBS running the card held 8.37 GB of 10 with the game at 6.32 GB, and
/// half an hour after OBS closed the same card held 7.29 GB with the game at 6.12 GB. The stream stack
/// was the difference, at about a gigabyte, and the desktop underneath it about 1.1 GB. Written as a
/// budget those two numbers say what preset the card can carry before the session starts — 7.2 GB of
/// game at High plus 2.1 GB of everything else is 9.3 of 10, and that session measured a median of
/// 88.1%.
/// </remarks>
public sealed class VramBudgetMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 19, 38, 44, TimeSpan.Zero);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    [Fact]
    public void TheBudgetIsStatedOnceTheSessionHasBothFigures()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));

        var report = monitor.Observe(Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02));

        Assert.NotNull(report);
        Assert.Contains("VRAM-budget", report!.Message, StringComparison.Ordinal);

        // Everything the game does not hold, split into the stack that can be closed and the desktop
        // underneath it: 8.37 − 6.32 = 2.05, of which OBS is 1.02.
        Assert.Equal(1.02, report.StreamStackBytes / (double)Gigabyte, 2);
        Assert.Equal(1.03, report.DesktopBytes / (double)Gigabyte, 2);

        // And what that leaves the game, which is the number a graphics preset is chosen against.
        Assert.Equal(7.95, report.GameHeadroomBytes / (double)Gigabyte, 2);
    }

    /// <summary>
    /// Said once, not every five seconds. It is a budget the session is spent inside, not a reading.
    /// </summary>
    [Fact]
    public void ItIsNotRepeatedWhileNothingChanges()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));
        Assert.NotNull(monitor.Observe(Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02)));

        var later = Start.AddMinutes(20);
        monitor.Observe(Adapter(later, usedGigabytes: 8.40));
        Assert.Null(monitor.Observe(Sample(later, gameGigabytes: 6.35, obsGigabytes: 1.03)));
    }

    /// <summary>
    /// The one term that moves during an evening, and it moves by a gigabyte. The session of 28 August
    /// only learned what the stream stack cost because OBS happened to close while the app was running.
    /// </summary>
    /// <remarks>
    /// Stated on the third agreeing sample, not the first. See
    /// <see cref="AFlickeringStreamRowProducesNoTransition"/> for what the first one used to cost.
    /// </remarks>
    [Fact]
    public void ClosingTheStreamStackRestatesTheBudget()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));
        monitor.Observe(Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02));

        VramBudgetReport? report = null;
        for (var index = 1; index <= 3; index++)
        {
            var at = Start.AddHours(4).AddSeconds(index * 5);
            monitor.Observe(Adapter(at, usedGigabytes: 7.29));
            report = monitor.Observe(Sample(at, gameGigabytes: 6.12, obsGigabytes: 0));

            // The first two samples agree with each other and not yet with the reported state.
            Assert.Equal(index == 3, report is not null);
        }

        Assert.NotNull(report);
        Assert.Contains("Streamstacken avslutades", report!.Message, StringComparison.Ordinal);
        Assert.Equal(1.17, report.DesktopBytes / (double)Gigabyte, 2);
    }

    /// <summary>
    /// Eighteen "the stream stack started/stopped" lines were written between 21:48 and 22:04 on an
    /// evening when OBS neither started nor stopped. The row was alternating between believable and
    /// excluded, and every flip restated the budget as though a gigabyte had moved.
    /// </summary>
    [Fact]
    public void AFlickeringStreamRowProducesNoTransition()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));
        Assert.NotNull(monitor.Observe(Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02)));

        for (var index = 1; index <= 18; index++)
        {
            var at = Start.AddSeconds(index * 5);
            monitor.Observe(Adapter(at, usedGigabytes: 8.37));

            var sample = Sample(at, gameGigabytes: 6.32, obsGigabytes: 1.02);
            var flickering = index % 2 == 0
                ? sample with { DoubleCountedProcessIds = [9012] }
                : sample;

            Assert.Null(monitor.Observe(flickering));
        }
    }

    /// <summary>
    /// An excluded row must not take its memory out of the budget with it. This is the line that said
    /// "the game has 8.1 GB left of the card's 10.0" while the card stood at 88–92%: obs64 was excluded
    /// as double counting, so the stream stack was booked at nothing and the gigabyte it held silently
    /// became headroom.
    /// </summary>
    [Fact]
    public void AnExcludedStreamRowIsReplacedByTheDifferenceTheCardRequires()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));

        var sample = Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02) with
        {
            DoubleCountedProcessIds = [9012],
        };

        var report = monitor.Observe(sample);

        Assert.NotNull(report);

        // The card says 2.05 GB is not the game's, and explorer accounts for 0.17 of it. The rest has
        // to be somewhere, and the somewhere is the process whose own counter cannot be read.
        Assert.Equal(0.17, report!.DesktopBytes / (double)Gigabyte, 2);
        Assert.Equal(1.88, report.StreamStackBytes / (double)Gigabyte, 2);
        Assert.True(report.StreamStackDerived);
        Assert.Contains("kan inte mätas per process", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whatever the table says, the room left is the card's own figure. This is the invariant that would
    /// have caught the wrong line on its own.
    /// </summary>
    [Fact]
    public void TheBudgetNeverReportsMoreRoomThanTheCardHasLeft()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 9.21));

        // A table that has lost most of its rows: only the game and a sliver of desktop are believable,
        // so a budget built from the table alone would report the missing memory as free.
        var sample = Sample(Start, gameGigabytes: 6.90, obsGigabytes: 1.30) with
        {
            DoubleCountedProcessIds = [9012],
        };

        var report = monitor.Observe(sample);

        Assert.NotNull(report);
        Assert.Equal(0.79, report!.FreeBytes / (double)Gigabyte, 2);
        Assert.Equal(
            report.FreeBytes,
            report.GameHeadroomBytes - report.GameBytes);
        Assert.True(report.GameHeadroomBytes - report.GameBytes <= report.AdapterTotalBytes - report.AdapterUsedBytes);
    }

    /// <summary>
    /// A row that has been proved to double count must not reach the budget. <c>dwm</c> at 6.1 GB would
    /// otherwise be counted as desktop and turn a 1.1 GB figure into a 7 GB one.
    /// </summary>
    [Fact]
    public void ADoubleCountedRowDoesNotReachTheDesktopFigure()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));

        var sample = Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02) with
        {
            Processes =
            [
                .. Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02).Processes,
                new GpuProcessMemoryUsage(4204, "dwm", (ulong)(6.08 * Gigabyte), 0, 1),
            ],
            DoubleCountedProcessIds = [4204],
        };

        var report = monitor.Observe(sample);

        Assert.NotNull(report);
        Assert.Equal(1.03, report!.DesktopBytes / (double)Gigabyte, 2);
    }

    /// <summary>
    /// Nothing to budget for before the game has allocated anything. A line about the desktop on its own
    /// answers no question anybody has.
    /// </summary>
    [Fact]
    public void NothingIsSaidBeforeTheGameHoldsMemory()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 1.2));

        Assert.Null(monitor.Observe(Sample(Start, gameGigabytes: 0, obsGigabytes: 0.3)));
    }

    /// <summary>
    /// A budget is a number somebody picks a graphics preset against, so on a machine where the two
    /// figures may describe different cards there is no honest budget to state.
    /// </summary>
    /// <remarks>
    /// NVML reads device index 0 for the whole session while the process table is anchored on whichever
    /// adapter the game holds memory on. With a second NVIDIA device present the subtraction is one
    /// card's total minus another card's row. The reconciliation elsewhere publishes its comparison with
    /// a caveat because nobody acts on it directly; this is acted on.
    /// </remarks>
    [Fact]
    public void ASecondGpuLeavesTheBudgetUnstated()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37, adapterCount: 2));

        Assert.Null(monitor.Observe(Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02)));
    }

    /// <summary>
    /// The table is cut to the largest holders, and the stream stack is several processes of which the
    /// browser sources are small. What the cut costs is the split, not the budget — the desktop figure
    /// is a residual, so bytes below the cut land in it and the total the game does not get is unchanged.
    /// Saying so is what keeps the two halves usable, since the advice is given against them.
    /// </summary>
    [Fact]
    public void BytesBelowTheTablesCutAreDeclared()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));

        // The counter set accounted for 0.4 GB more than the rows the table kept.
        var truncated = Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02) with
        {
            AllProcessesDedicatedBytes = (ulong)((6.32 + 1.02 + 0.171 + 0.40) * Gigabyte),
        };

        var report = monitor.Observe(truncated);

        Assert.NotNull(report);
        Assert.Contains("under topplistans gräns", report!.Message, StringComparison.Ordinal);

        // And the number the preset is chosen against is unaffected by the cut.
        Assert.Equal(7.95, report.GameHeadroomBytes / (double)Gigabyte, 2);
    }

    /// <summary>
    /// The budget a setting is chosen against is the measured edge, not the card's physical size.
    /// </summary>
    /// <remarks>
    /// Two sessions put that edge at 88% occupancy: above it the hitch rate multiplies, and on
    /// 1 September 76% of every frame over 100 ms fell inside the 17% of the evening spent there. The
    /// line as it read that night offered the game 8.2 GB of a 10 GB card while the measured ceiling was
    /// 6.9, and it is this line that answers "can I keep Very High".
    /// </remarks>
    [Fact]
    public void TheBudgetIsQuotedAgainstTheMeasuredEdgeAndNotTheCardsSize()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));

        var report = monitor.Observe(Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02));

        Assert.NotNull(report);

        // 88% of 10 GB is 8.8, less the 2.05 GB the desktop and the stream stack hold.
        Assert.Equal(8.8 - 2.05, report!.GameBandHeadroomBytes / (double)Gigabyte, 2);
        Assert.True(
            report.GameBandHeadroomBytes < report.GameHeadroomBytes,
            "the measured edge has to be tighter than the card's physical size, or it says nothing");

        Assert.Contains("ryms utan tryck upp till", report.Message, StringComparison.Ordinal);
        Assert.Contains("88 %", report.Message, StringComparison.Ordinal);
    }

    /// <summary>The ordinary case says nothing about it, because there is nothing to say.</summary>
    [Fact]
    public void AnUntruncatedTableIsNotAnnotated()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.37));

        var report = monitor.Observe(Sample(Start, gameGigabytes: 6.32, obsGigabytes: 1.02));

        Assert.NotNull(report);
        Assert.DoesNotContain("topplistans", report!.Message, StringComparison.Ordinal);
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

    private static GpuProcessMemorySample Sample(DateTimeOffset timestamp, double gameGigabytes, double obsGigabytes)
    {
        var processes = new List<GpuProcessMemoryUsage>();
        if (gameGigabytes > 0)
        {
            processes.Add(new GpuProcessMemoryUsage(18704, "FiveM_b3407_GTAProcess", (ulong)(gameGigabytes * Gigabyte), 0, 1));
        }

        if (obsGigabytes > 0)
        {
            processes.Add(new GpuProcessMemoryUsage(9012, "obs64", (ulong)(obsGigabytes * Gigabyte), 0, 1));
        }

        processes.Add(new GpuProcessMemoryUsage(2244, "explorer", 175UL * 1024 * 1024, 0, 1));

        return new GpuProcessMemorySample(timestamp, IsAvailable: true, processes);
    }
}
