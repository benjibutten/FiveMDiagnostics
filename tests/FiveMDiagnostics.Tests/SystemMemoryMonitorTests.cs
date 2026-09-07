namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// Free RAM reaches the session log, which it did not for the first thirty sessions.
/// </summary>
/// <remarks>
/// The figure was sampled every second, shown in the window and discarded. On 5 September the evening's
/// root cause was Windows trimming working sets out to a slow paging file, and how much memory the
/// machine had left was the one question the journal could not answer afterwards.
/// <para>
/// The monitor no longer has a cadence of its own — the session's interim summary writes the line, and
/// when both did, it went out twice a quarter of an hour all evening. So what these cover is the
/// arithmetic: the summary describes the session's worst minute rather than its last sample.
/// </para>
/// </remarks>
public sealed class SystemMemoryMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 21, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The summary carries the session's lowest free RAM and highest commit rather than whatever the
    /// last sample happened to read.
    /// </summary>
    [Fact]
    public void TheSummaryCarriesTheWorstReadingAndNotTheLast()
    {
        var monitor = new SystemMemoryMonitor();

        monitor.Observe(Sample(Start, availableMb: 9_000, commitPercent: 62));

        // The excursion. It is over in ninety seconds and it is the whole finding.
        monitor.Observe(Sample(Start.AddMinutes(7), availableMb: 1_100, commitPercent: 94));
        monitor.Observe(Sample(Start.AddMinutes(8), availableMb: 8_400, commitPercent: 63));
        monitor.Observe(Sample(Start.AddMinutes(16), availableMb: 8_600, commitPercent: 61));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(1_100UL, report!.LowestAvailableMb);
        Assert.Equal(94, report.HighestCommitPercent);
        Assert.True(report.IsTight);
        Assert.Contains("1,1 GB ledigt RAM", report.Message, StringComparison.Ordinal);
        Assert.Contains("94 %", report.Message, StringComparison.Ordinal);
    }

    /// <summary>Nothing measured is no line, rather than a line about a machine with no memory.</summary>
    [Fact]
    public void ASessionThatMeasuredNothingHasNothingToSay()
    {
        var monitor = new SystemMemoryMonitor();

        Assert.Null(monitor.Summary());

        monitor.Observe(Sample(Start, availableMb: 0, commitPercent: 0));

        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// An evening with room to spare says so without the paging warning attached to it.
    /// </summary>
    [Fact]
    public void AMachineWithHeadroomIsNotWarnedAbout()
    {
        var monitor = new SystemMemoryMonitor();

        monitor.Observe(Sample(Start, availableMb: 12_000, commitPercent: 48));
        monitor.Observe(Sample(Start.AddMinutes(16), availableMb: 11_400, commitPercent: 51));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.False(report!.IsTight);
        Assert.DoesNotContain("växlingsfilen", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reading of zero is the interop call having failed. Letting it through would pin the session
    /// minimum at zero for the rest of the evening and make every later line say the machine was out of
    /// memory.
    /// </summary>
    [Fact]
    public void AFailedReadingIsNotAMachineWithNoMemoryLeft()
    {
        var monitor = new SystemMemoryMonitor();

        monitor.Observe(Sample(Start, availableMb: 9_000, commitPercent: 60));
        monitor.Observe(Sample(Start.AddMinutes(2), availableMb: 0, commitPercent: 0));
        monitor.Observe(Sample(Start.AddMinutes(16), availableMb: 8_800, commitPercent: 61));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(8_800UL, report!.LowestAvailableMb);
        Assert.Contains("mätt på 2 avläsningar", report.Message, StringComparison.Ordinal);
    }

    private static SystemTelemetrySample Sample(DateTimeOffset at, ulong availableMb, double commitPercent)
    {
        return new SystemTelemetrySample(
            at,
            TotalCpuUsagePercent: 38,
            new Dictionary<string, double>(),
            commitPercent,
            availableMb,
            TopCpuProcesses: [],
            TopDiskProcesses: []);
    }
}
