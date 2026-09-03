namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// Three lines that should say more, and one that should stop saying anything.
/// </summary>
/// <remarks>
/// All four come out of the 2 September session. The game climbed from 5.2 GB to 7.0 GB over five hours
/// and nothing warned about it until the review; twelve captures were taken and three were read; the NUI
/// browser went from 0.32 to 1.23 cores and was named in none of 154 incidents; and every one of those
/// 154 carried "everything went through the compositor", which is what a borderless window does by
/// definition.
///
/// The ceiling it climbed towards was written here as 7.13 GB, on the 2021 divisor. The client's own
/// streaming budget at <c>vid_budgetScale 11</c> is 5.6, so the game spent the evening a gigabyte
/// <em>past</em> its budget rather than approaching it — which is what the monitor now says, and why it
/// has two lines instead of one.
/// </remarks>
public sealed class BudgetApproachAndQuietLinesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 19, 33, 37, TimeSpan.Zero);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    /// <summary>
    /// The warning that can be written hours before the worst frame: the game is past the budget it was
    /// configured with, and the card enters the band before it stops growing.
    /// </summary>
    /// <remarks>
    /// The evening measured: 10 GB card, 7.00 GB of game against a 5.62 streaming budget, 1.87 outside
    /// it. The overhead is 1.38 GB the slider does not govern, so the room under the band — 6.93 — has
    /// to hold that before it holds any budget at all, which leaves 10 rather than 11. That is one step
    /// down, roughly 50 % on the slider, and not the collapse to 30 % the old arithmetic argued for.
    /// </remarks>
    [Fact]
    public void AGamePastItsBudgetIsWarnedAboutBeforeTheCardFills()
    {
        var monitor = new VramBudgetMonitor();
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start, BudgetScale: 11));

        // Early in the evening: the game is still loading in and nothing is worth saying.
        monitor.Observe(Adapter(Start, usedGigabytes: 5.35));
        monitor.Observe(Sample(Start, gameGigabytes: 3.26));
        Assert.Null(monitor.DescribeBudgetApproach());
        Assert.Equal(0UL, monitor.OverheadBytes);

        // Five hours later, well past the budget and closing on the band.
        var late = Start.AddHours(5);
        monitor.Observe(Adapter(late, usedGigabytes: 8.87));
        monitor.Observe(Sample(late, gameGigabytes: 7.00));

        var approach = monitor.DescribeBudgetApproach();

        Assert.NotNull(approach);
        Assert.Contains("1,4 GB mer än sin streamingbudget på 5,6 GB", approach!, StringComparison.Ordinal);
        Assert.Contains("NUI, render targets och bildbuffertar", approach, StringComparison.Ordinal);
        Assert.Contains("för lite", approach, StringComparison.Ordinal);

        // One step down, not four. The overhead is subtracted before the value is chosen.
        Assert.Contains("Extended Texture Budget 10, ungefär 50 %", approach, StringComparison.Ordinal);

        // Once per session. It is a prediction, not a reading.
        Assert.Null(monitor.DescribeBudgetApproach());
    }

    /// <summary>
    /// Before the game has passed its budget the line is still a prediction, and it says what the budget
    /// does not cover rather than pretending it is the whole of the game's memory.
    /// </summary>
    [Fact]
    public void AGameApproachingItsBudgetIsToldWhatTheBudgetDoesNotCover()
    {
        var monitor = new VramBudgetMonitor();
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start, BudgetScale: 11));

        // 5.30 of a 5.62 budget: nine tenths of the way there, and nothing over it yet.
        monitor.Observe(Adapter(Start, usedGigabytes: 7.17));
        monitor.Observe(Sample(Start, gameGigabytes: 5.30));

        var approach = monitor.DescribeBudgetApproach();

        Assert.NotNull(approach);
        Assert.Contains("5,3 GB av sin streamingbudget på 5,6 GB", approach!, StringComparison.Ordinal);
        Assert.Contains("ligger ovanpå", approach, StringComparison.Ordinal);
        Assert.Equal(0UL, monitor.OverheadBytes);
    }

    /// <summary>
    /// Without a configured budget there is no ceiling to measure against, and the monitor says nothing
    /// rather than inventing one.
    /// </summary>
    [Fact]
    public void WithoutAConfiguredBudgetThereIsNoWarning()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 8.87));
        monitor.Observe(Sample(Start, gameGigabytes: 7.00));

        Assert.Null(monitor.DescribeBudgetApproach());
    }

    /// <summary>
    /// Twelve captures, of which the review read three. The line already priced them; now it says so.
    /// </summary>
    [Fact]
    public void MoreCapturesThanTheAnalysisNeedsIsSaidOutLoud()
    {
        var monitor = new CaptureCostMonitor(refreshRateHz: 59);
        Play(monitor, captures: 12);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Contains("fler än analysen behöver", report!.Message, StringComparison.Ordinal);
        // The setting has to be one that exists, or the advice sends the reader looking for it.
        Assert.Contains("MaxAutoCapturesPerSession", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DeepCapture.MaxCapturesPerWindow", report.Message, StringComparison.Ordinal);
    }

    /// <summary>An evening that stayed within the budget gets the cost line without the advice.</summary>
    [Fact]
    public void ASensibleNumberOfCapturesGetsNoAdvice()
    {
        var monitor = new CaptureCostMonitor(refreshRateHz: 59);
        Play(monitor, captures: 4);

        Assert.DoesNotContain("fler än analysen behöver", monitor.Summary()!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compositor is on every incident until something explains it, and then it is on none of them.
    /// </summary>
    [Fact]
    public void TheComposedPresentPathIsReportedUntilTheWindowModeExplainsIt()
    {
        var unexplained = new FiveMCorrelationEngine().Analyze(BuildIncident());
        Assert.Contains("Composed", unexplained.Summary, StringComparison.Ordinal);
        Assert.Contains(unexplained.TimelineHighlights, item => item.Category == "Present mode");

        var explained = new FiveMCorrelationEngine { ComposedPresentExplainedAt = _ => true }.Analyze(BuildIncident());
        Assert.DoesNotContain("Present mode", explained.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(explained.TimelineHighlights, item => item.Category == "Present mode");
    }

    /// <summary>
    /// FiveM's CEF host is a neighbour like any other. The name filter that keeps the game out of its
    /// own suspect list kept this out too, for as long as the filter has existed.
    /// </summary>
    [Fact]
    public void TheNuiBrowserIsAllowedToBeASuspect()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(BuildIncident());

        Assert.Contains(analysis.SuspectedProcesses, item => item.ProcessName == "FiveM_ChromeBrowser");

        // And the game's own process still is not one.
        Assert.DoesNotContain(analysis.SuspectedProcesses, item => item.ProcessName.Contains("GTAProcess", StringComparison.Ordinal));
    }

    private static void Play(CaptureCostMonitor monitor, int captures)
    {
        for (var minute = 0; minute < 120; minute++)
        {
            var at = Start.AddMinutes(minute);
            if (minute % (120 / captures) == 0 && minute > 0)
            {
                monitor.RecordCaptureWritten(at);
            }

            for (var frame = 0; frame < 60; frame++)
            {
                monitor.Observe(new FrameTelemetrySample(
                    at.AddSeconds(frame),
                    frame < 3 ? 90 : 16.9,
                    GpuBusyMs: 5,
                    DisplayLatencyMs: 20,
                    MsBetweenPresents: frame < 3 ? 90 : 16.9,
                    Dropped: false,
                    ProcessName: "FiveM_b3407_GTAProcess.exe",
                    CpuBusyMs: 7,
                    CpuWaitMs: 8));
            }
        }
    }

    private static IncidentRecord BuildIncident()
    {
        var events = new List<TelemetryEvent>();
        var markedAt = Start.AddSeconds(46);

        for (var i = 0; i < 600; i++)
        {
            var slow = i is 460;
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.9),
                slow ? 281.0 : 16.9,
                GpuBusyMs: slow ? 16.1 : 5.0,
                DisplayLatencyMs: 20,
                MsBetweenPresents: slow ? 281.0 : 16.9,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: slow ? 251.2 : 7.8,
                CpuWaitMs: slow ? 4.76 : 8.8,
                PresentMode: "Composed: Copy with GPU GDI"));
        }

        // 1.23 cores of sixteen, which is where the NUI browser ended the evening of 2 September.
        var nui = new ProcessActivity("FiveM_ChromeBrowser", 22132, 7.7, 2L * 1024 * 1024);
        var perCore = Enumerable.Range(0, 16).ToDictionary(index => index.ToString(), _ => 45d);

        for (var second = 0; second < 60; second++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 44,
                PerCoreUsagePercent: perCore,
                MemoryCommitPercent: 45,
                AvailableMemoryMb: 15_000,
                TopCpuProcesses: [nui],
                TopDiskProcesses: [nui],
                DiskAverageLatencyMs: 1.1,
                DiskQueueLength: 0.4,
                HardFaultPagesPerSecond: 3));
        }

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Severe, "Auto: 281 ms frame"),
            Start,
            Start.AddSeconds(90),
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
                "Disabled",
                ObsDetectedAtStart: true,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double usedGigabytes) =>
        new(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 40,
            MemoryBandwidthUtilizationPercent: 21,
            UsedVramBytes: (ulong)(usedGigabytes * Gigabyte),
            TotalVramBytes: 10UL * Gigabyte,
            EncoderUtilizationPercent: 36,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 59,
            ThrottleReasons: [],
            AdapterCount: 1);

    private static GpuProcessMemorySample Sample(DateTimeOffset timestamp, double gameGigabytes) =>
        new(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(31076, "FiveM_b3407_GTAProcess", (ulong)(gameGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(7548, "obs64", (ulong)(0.9 * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(2244, "dwm", (ulong)(1.33 * Gigabyte), 0, 1),
            ]);
}
