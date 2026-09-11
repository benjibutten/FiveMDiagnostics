namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The 815.9 ms reading that three reviews walked past.
/// </summary>
/// <remarks>
/// <para>
/// 10 September, 02:38:58: <c>D:</c> answered in 815.9 ms with an empty queue, in the last incident of
/// the evening, and sixty-four seconds later the machine stopped writing data. The line was written
/// correctly and sat among fifty-two identically shaped lines saying <c>F:</c> answered in 13 ms. The
/// same volume had produced 686.6 ms on 8 September and 223.6 ms on 9 September; none of the three was
/// noticed until four evenings were compared by hand.
/// </para>
/// <para>
/// <c>F:</c>, where the game actually lives, is the control: 377 windows across four evenings, median
/// 13.2–13.8 ms, worst ever 17.1. Slow and utterly steady. Any rule that calls that a fault is useless.
/// </para>
/// </remarks>
public sealed class DiskLatencyMonitorTests
{
    [Fact]
    public void TheVolumeThatAnsweredOnceAndBadlyIsNamed()
    {
        var monitor = new DiskLatencyMonitor();

        // The evening as measured: F: slowest in forty-seven windows, never above 15.8 ms.
        for (var window = 0; window < 47; window++)
        {
            monitor.Observe("2 F:", 12.4 + (window % 7) * 0.5);
        }

        monitor.Observe("0 D:", 815.9);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.True(report.HasOutlier);
        Assert.Equal("0 D:", Assert.Single(report.Outliers).Volume);
        Assert.Contains("815,9 ms", report.Message, StringComparison.Ordinal);
        Assert.Contains("47 fönster", report.Message, StringComparison.Ordinal);

        // Spin-up is the likelier reading and is offered as such; the event log is what settles it.
        Assert.Contains("varvar upp", report.Message, StringComparison.Ordinal);
        Assert.Contains("händelselogg", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mechanical disk the game streams from. Slow every single window, and there is nothing wrong
    /// with it.
    /// </summary>
    [Fact]
    public void ASlowButSteadyVolumeIsNotAnOutlier()
    {
        var monitor = new DiskLatencyMonitor();

        foreach (var latency in new[] { 13.2, 13.6, 13.8, 15.0, 15.2, 15.8, 12.9, 14.1 })
        {
            monitor.Observe("2 F:", latency);
        }

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.False(report.HasOutlier);
        Assert.Contains("median 14,1 ms", report.Message, StringComparison.Ordinal);
        Assert.Contains("värsta 15,8", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The system SSD answers in hundredths and occasionally in whole milliseconds. That is a ratio of a
    /// hundred and it means nothing, which is what the floor is for.
    /// </summary>
    [Fact]
    public void JitterOnAFastVolumeIsNotAnOutlier()
    {
        var monitor = new DiskLatencyMonitor();

        foreach (var latency in new[] { 0.04, 0.05, 0.11, 0.21, 6.0 })
        {
            monitor.Observe("1 E: C:", latency);
        }

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.False(report.HasOutlier);
    }

    [Fact]
    public void NothingMeasuredMeansNoReport()
    {
        var monitor = new DiskLatencyMonitor();

        monitor.Observe(volume: null, 815.9);
        monitor.Observe("0 D:", latencyMs: null);

        Assert.Null(monitor.Summary());
    }
}
