namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// FiveM's anti-cheat gets one line for the session instead of being rediscovered once per trace.
/// </summary>
/// <remarks>
/// The per-trace attribution has named <c>adhesive.dll</c> since 29 August, and three separate reviews
/// have written it up as a new finding. Across the twelve traces of 4 September it held 0.37 to 0.85
/// cores with a median of 0.47 — present in the quiet traces and the bad ones alike, which is what makes
/// it a fixed fee rather than a cause. Stating the range once is what retires the question.
/// </remarks>
public sealed class AntiCheatCostTests
{
    /// <summary>The evening's figures, summed into the sentence a reader can act on — by not acting.</summary>
    [Fact]
    public void TheModuleIsSummedAcrossTheSessionsTraces()
    {
        var monitor = new AntiCheatCostMonitor();

        foreach (var cores in new[] { 0.47, 0.46, 0.85, 0.37, 0.52, 0.44 })
        {
            monitor.Observe(new Dictionary<string, double>
            {
                ["cpuBusiestThreadCores_adhesive.dll"] = cores,
                ["antiCheatFileOperationsPerSecond"] = 1_650,
            });
        }

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(6, report.Traces);
        Assert.Equal(0.37, report.MinCores, 2);
        Assert.Equal(0.85, report.MaxCores, 2);
        Assert.False(report.IsUnusual);
        Assert.Contains("fast avgift", report.Message, StringComparison.Ordinal);
        Assert.Contains("filoperationer i sekunden", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason the range is stated at all: a figure that leaves it is visible without opening twelve
    /// traces by hand.
    /// </summary>
    [Fact]
    public void AFigureOutsideEveryMeasuredSessionIsFlagged()
    {
        var monitor = new AntiCheatCostMonitor();

        monitor.Observe(new Dictionary<string, double> { ["cpuBusiestThreadCores_adhesive.dll"] = 0.47 });
        monitor.Observe(new Dictionary<string, double> { ["cpuBusiestThreadCores_adhesive.dll"] = 2.40 });

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.True(report.IsUnusual);
        Assert.Contains("värt att titta på", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trace where the module was not on the busiest thread still counts as a trace, so the line can
    /// say how many of them showed it — which is itself a measurement.
    /// </summary>
    [Fact]
    public void TracesWithoutTheModuleAreStillCounted()
    {
        var monitor = new AntiCheatCostMonitor();

        monitor.Observe(new Dictionary<string, double> { ["cpuBusiestThreadCores_adhesive.dll"] = 0.50 });
        monitor.Observe(new Dictionary<string, double> { ["cpuBusiestThreadCores_nvlddmkm.sys"] = 0.90 });

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(2, report.Traces);
        Assert.Equal(1, report.TracesWithModule);
        Assert.Contains("1 av 2 traces", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session whose traces never showed the module says nothing rather than reporting a zero, which
    /// would read as "the anti-cheat cost nothing" instead of "it was not measured".
    /// </summary>
    [Fact]
    public void WithoutAMeasurementThereIsNoLine()
    {
        var monitor = new AntiCheatCostMonitor();
        monitor.Observe(new Dictionary<string, double> { ["cpuBusiestThreadCores_ntoskrnl.exe"] = 0.30 });

        Assert.Null(monitor.Summary());
    }
}
