namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The evening of 9 September, where a neighbour tripled halfway through and nothing said so.
/// </summary>
/// <remarks>
/// <c>FiveM_ChromeBrowser</c> — the process behind the server's NUI layers — held 0.27 and 0.41 cores in
/// the evening's first two captures and 1.50, 1.34, 1.18, 1.17 and 1.14 in the five after, with no
/// return. The hitch rate over the same boundary went from 184 to 229 an hour. Every one of those
/// figures was printed by the app, one capture at a time, where a level says nothing at all.
/// </remarks>
public sealed class NeighbourCpuTrendTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 9, 20, 21, 0, TimeSpan.Zero);

    [Fact]
    public void TheStepIsFoundAndPlacedBetweenTwoCaptures()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        Observe(monitor, 0, browserCores: 0.27);
        Observe(monitor, 84, browserCores: 0.41);
        Observe(monitor, 112, browserCores: 1.50);
        Observe(monitor, 156, browserCores: 1.34);
        Observe(monitor, 177, browserCores: 1.18);
        Observe(monitor, 183, browserCores: 1.17);
        Observe(monitor, 216, browserCores: 1.14);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal("FiveM_ChromeBrowser", report.ProcessName);
        Assert.Equal(0.41, report.Before, 2);
        Assert.Equal(1.14, report.After, 2);
        Assert.Equal(1.50, report.AfterPeak, 2);
        Assert.Equal(2, report.TracesBefore);
        Assert.Equal(5, report.TracesAfter);
        Assert.Equal(Start.AddMinutes(84), report.SteppedBetween.From);
        Assert.Equal(Start.AddMinutes(112), report.SteppedBetween.To);
        Assert.Contains("gick aldrig ner igen", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anti-cheat, which held 0.35 cores in every trace of both evenings. A monitor that finds a step
    /// here finds one everywhere.
    /// </summary>
    [Fact]
    public void AProcessThatHeldItsLevelIsNotAStep()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        foreach (var (minutes, cores) in new[] { (0, 0.35), (84, 0.34), (112, 0.35), (156, 0.36), (177, 0.35) })
        {
            Observe(monitor, minutes, browserCores: cores);
        }

        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// A level that went up and came back down is not what the line is for: the question is whether the
    /// session's two halves ran on the same machine.
    /// </summary>
    [Fact]
    public void ASpikeThatCameBackDownIsNotAStep()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        Observe(monitor, 0, browserCores: 0.30);
        Observe(monitor, 40, browserCores: 1.40);
        Observe(monitor, 80, browserCores: 1.35);
        Observe(monitor, 120, browserCores: 0.33);
        Observe(monitor, 160, browserCores: 0.31);

        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// Two traces cannot show a step, however far apart their two readings are — half the session is one
    /// measurement on each side.
    /// </summary>
    [Fact]
    public void TwoTracesAreNotEnoughToClaimAnything()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        Observe(monitor, 0, browserCores: 0.30);
        Observe(monitor, 60, browserCores: 1.50);

        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// An idle process blipping from 0.02 to 0.06 clears the ratio and nothing else. The absolute rise is
    /// what keeps the line about loads that matter.
    /// </summary>
    [Fact]
    public void AnIdleProcessBlippingIsNotAStep()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        Observe(monitor, 0, browserCores: 0.02);
        Observe(monitor, 40, browserCores: 0.02);
        Observe(monitor, 80, browserCores: 0.06);
        Observe(monitor, 120, browserCores: 0.07);
        Observe(monitor, 160, browserCores: 0.06);

        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// The same evening imported by hand the next day, newest file first. The order the readings are
    /// compared in is the whole finding, and the order they were parsed in is not the order they happened
    /// in — seven ETLs picked out of a folder arrive a minute apart in whatever order the dialog listed
    /// them.
    /// </summary>
    [Fact]
    public void TheOrderIsTheTracesOwnWindowRatherThanTheOrderTheyWereRead()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        var evening = new[] { 0.27, 0.41, 1.50, 1.34, 1.18, 1.17, 1.14 };
        for (var capture = evening.Length - 1; capture >= 0; capture--)
        {
            // Parsed last to first, a minute apart; covering the evening's own half-hours.
            Observe(
                monitor,
                minutes: evening.Length - capture,
                browserCores: evening[capture],
                coveredMinutes: capture * 30);
        }

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(0.41, report.Before, 2);
        Assert.Equal(1.14, report.After, 2);
        Assert.Equal(2, report.TracesBefore);
        Assert.Equal(5, report.TracesAfter);
        Assert.Equal(Start.AddMinutes(30), report.SteppedBetween.From);
        Assert.Equal(Start.AddMinutes(60), report.SteppedBetween.To);
    }


    /// <summary>
    /// The evening of 10 September, where the step the monitor reported was two different processes.
    /// </summary>
    /// <remarks>
    /// The game crashed at 00:09 and relaunched, taking <c>FiveM_ChromeBrowser</c> with it. The monitor
    /// saw 0.34 cores in the evening's first trace and 0.88–1.37 in the three that followed, and wrote
    /// that the process "never came down again" and that the machine the later half ran on was not the
    /// one the earlier half had. Two of those three traces were a new instance, and one of them was that
    /// instance loading the game. Split on the pid, neither run is long enough to claim anything — which
    /// is the honest answer for that evening.
    /// </remarks>
    [Fact]
    public void AStepThatSpansARestartIsNotAStep()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        Observe(monitor, 0, browserCores: 0.34, browserPid: 18944);
        Observe(monitor, 57, browserCores: 1.37, browserPid: 18944);
        Observe(monitor, 72, browserCores: 0.88, browserPid: 27868);
        Observe(monitor, 170, browserCores: 1.36, browserPid: 27868);

        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// The same shape inside one instance is still the finding it always was, and the line says the
    /// series had a break in it so the trace counts are not read as the whole evening.
    /// </summary>
    [Fact]
    public void AStepInsideOneInstanceSurvivesARestartElsewhereInTheSession()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        Observe(monitor, 0, browserCores: 0.30, browserPid: 18944);
        Observe(monitor, 20, browserCores: 0.41, browserPid: 27868);
        Observe(monitor, 40, browserCores: 0.38, browserPid: 27868);
        Observe(monitor, 60, browserCores: 1.50, browserPid: 27868);
        Observe(monitor, 80, browserCores: 1.34, browserPid: 27868);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(0.41, report.Before, 2);
        Assert.Equal(1.34, report.After, 2);
        Assert.Equal(2, report.TracesBefore);
        Assert.Equal(2, report.TracesAfter);
        Assert.Equal(1, report.RestartsSeen);
        Assert.Contains("startades om en gång", report.Message, StringComparison.Ordinal);
        Assert.Contains("inom en och samma instans", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Traces written before 11 September carry no pid, and an evening of them must behave exactly as it
    /// did — a missing pid continues the run rather than splitting it.
    /// </summary>
    [Fact]
    public void TracesWithoutAPidAreOneSeriesAsBefore()
    {
        var monitor = new NeighbourCpuTrendMonitor();

        foreach (var (minutes, cores) in new[] { (0, 0.27), (84, 0.41), (112, 1.50), (156, 1.34), (177, 1.18) })
        {
            Observe(monitor, minutes, browserCores: cores);
        }

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(0, report.RestartsSeen);
        Assert.Contains("sessionens första", report.Message, StringComparison.Ordinal);
    }

    private static void Observe(
        NeighbourCpuTrendMonitor monitor,
        int minutes,
        double browserCores,
        int? coveredMinutes = null,
        int? browserPid = null)
    {
        var metrics = new Dictionary<string, double>
        {
            ["cpuProcessCores_FiveM_ChromeBrowser"] = browserCores,

            // The rest of the machine, steady, so the report has to pick the one that moved.
            ["cpuProcessCores_obs64.exe"] = 0.34,
            ["cpuProcessCores_dwm.exe"] = 0.30,
            ["cpuSubjectProcessCores"] = 3.6,
        };

        if (browserPid is { } pid)
        {
            metrics["cpuProcessPid_FiveM_ChromeBrowser"] = pid;
        }

        if (coveredMinutes is { } covered)
        {
            metrics["traceCoveredStartUnixMs"] = Start.AddMinutes(covered).ToUnixTimeMilliseconds();
        }

        monitor.Observe(Start.AddMinutes(minutes), metrics);
    }
}
