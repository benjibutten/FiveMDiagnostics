namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The rows the timeline of 6 September did not carry, each about a figure the session had already
/// sampled thousands of times.
/// </summary>
/// <remarks>
/// Its 140 incidents wrote Network, Marker, Frame, GPU, Memory, Classification, VRAM per process, OBS,
/// Processes, Display change and EtlTrace. Six of them concluded a storage stall with no disk anywhere
/// in the timeline, 92 concluded game lag with no statement of whether the game was in front, and every
/// VRAM table listed its rows without adding them up against the card they had to fit in.
/// </remarks>
public sealed class IncidentTimelineRowsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 22, 13, 40, TimeSpan.Zero);

    private static readonly DateTimeOffset FrameAt = Start.AddSeconds(21);

    [Theory]
    [InlineData("wpr", 0, 1000, true)]
    [InlineData("WPR.exe", 0, 1000, true)]
    [InlineData("wpr", 10, 1000, false)]
    [InlineData("wpr", 0, 0, false)]
    [InlineData("wprOther", 0, 1000, false)]
    public void HardFaultAttributionUsesSystemProcessActivity(string name, int offsetSeconds, long io, bool expected)
    {
        var incident = Incident();
        var peak = incident.GetEvents<SystemTelemetrySample>().First();
        incident = incident with
        {
            Events = incident.Events.Where(item => item is not SystemTelemetrySample)
                .Concat(new TelemetryEvent[]
                {
                    peak with { HardFaultPagesPerSecond = 250000, TopDiskProcesses = [] },
                    peak with
                    {
                        Timestamp = peak.Timestamp.AddSeconds(offsetSeconds),
                        HardFaultPagesPerSecond = 0,
                        TopDiskProcesses = [new ProcessActivity(name, 123, 0, io)],
                    },
                }).ToArray(),
        };

        var disk = new FiveMCorrelationEngine().Analyze(incident).TimelineHighlights.Single(item => item.Category == "Disk");
        Assert.Equal(expected, disk.Summary.Contains("sammanfaller med", StringComparison.Ordinal));
    }

    /// <summary>
    /// The counters have been collected per physical disk since 5 September and reached nothing. The
    /// line names the disk, its latency and its queue, which is what a storage verdict has to rest on.
    /// </summary>
    [Fact]
    public void TheDiskCountersReachTheTimeline()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        var disk = analysis.TimelineHighlights.Single(item => item.Category == "Disk");

        Assert.Contains("1 E: C:", disk.Summary, StringComparison.Ordinal);
        Assert.Contains("0,3 ms", disk.Summary, StringComparison.Ordinal);
        Assert.Contains("kö 0,4", disk.Summary, StringComparison.Ordinal);
        Assert.Contains("Sidinläsningar högst 12/s", disk.Summary, StringComparison.Ordinal);

        // A disk answering in tenths of a millisecond is a disk that is answering, and the line does not
        // suggest otherwise.
        Assert.DoesNotContain("köar", disk.Summary, StringComparison.Ordinal);
    }

    /// <summary>The worst sample of the window, not an average over it: a stall is an excursion.</summary>
    [Fact]
    public void TheSlowestSampleIsTheOneReported()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(worstDiskLatencyMs: 46));

        var disk = analysis.TimelineHighlights.Single(item => item.Category == "Disk");

        Assert.Contains("46,0 ms", disk.Summary, StringComparison.Ordinal);
        Assert.Contains("en disk som köar", disk.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdict is decided on the focus readings and the timeline said nothing about them either way.
    /// </summary>
    [Fact]
    public void TheFocusRowSaysWhoOwnedTheForegroundAtTheWorstFrame()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        var focus = analysis.TimelineHighlights.Single(item => item.Category == "Focus");

        Assert.Contains("Spelet låg i förgrunden", focus.Summary, StringComparison.Ordinal);
        Assert.Contains($"kl. {FrameAt.ToLocalTime():HH:mm:ss}", focus.Summary, StringComparison.Ordinal);
    }

    /// <summary>And the other way, where it names the window that had it instead.</summary>
    [Fact]
    public void AWindowInFrontOfTheGameIsNamed()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(foreground: "SearchHost"));

        var focus = analysis.TimelineHighlights.Single(item => item.Category == "Focus");

        Assert.Contains("låg inte i förgrunden", focus.Summary, StringComparison.Ordinal);
        Assert.Contains("SearchHost", focus.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing measured is no row, rather than a row saying the game was in front. The monitor fails
    /// open, and printing that default as an observation would state a missing collector as evidence.
    /// </summary>
    [Fact]
    public void WithoutFocusTelemetryThereIsNoRow()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(withFocus: false));

        Assert.DoesNotContain(analysis.TimelineHighlights, item => item.Category == "Focus");
    }

    /// <summary>
    /// The whole of the 6 September verdict, said in advance: three unremarkable rows that add up to
    /// more card than the card has.
    /// </summary>
    [Fact]
    public void TheVramTableIsAddedUpAgainstTheCard()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        var vram = analysis.TimelineHighlights.Single(item => item.Category == "VRAM per process");

        Assert.Contains("Summa 10,2 av 10,0 GB", vram.Summary, StringComparison.Ordinal);
        Assert.Contains("Kortet är fullt", vram.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The marker row and the label beside it are the same clock. They were not: the label formats its
    /// own suffix in local time and the marker time was printed in UTC, so the worst incident of the
    /// evening read as two hours between a marker and the frame twelve seconds after it.
    /// </summary>
    [Fact]
    public void TheMarkerRowIsInTheSameClockAsTheLabel()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        var marker = analysis.TimelineHighlights.Single(item => item.Category == "Marker");

        Assert.Contains($"markerad {Start.ToLocalTime():HH:mm:ss}", marker.Summary, StringComparison.Ordinal);
    }

    private static IncidentRecord Incident(
        double worstDiskLatencyMs = 0.3,
        string? foreground = null,
        bool withFocus = true)
    {
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(Frame(Start.AddMilliseconds(i * 16.7), 16.7));
        }

        events.Add(Frame(FrameAt, 425));

        for (var second = 0; second < 30; second++)
        {
            // One sample carries the excursion, the rest are the disk answering normally. The worst is
            // the one the line has to describe.
            var latencyMs = second == 12 ? worstDiskLatencyMs : 0.04;

            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 38,
                new Dictionary<string, double>(),
                MemoryCommitPercent: 58,
                AvailableMemoryMb: 10_500,
                TopCpuProcesses: [],
                TopDiskProcesses: [],
                DiskAverageLatencyMs: latencyMs,
                DiskQueueLength: second == 12 ? 0.4 : 0.02,
                HardFaultPagesPerSecond: second == 12 ? 12 : 0,
                WorstDiskInstance: "1 E: C:"));

            if (withFocus)
            {
                events.Add(new WindowFocusSample(
                    Start.AddSeconds(second),
                    GameHasFocus: foreground is null,
                    ForegroundProcessId: 32_932,
                    ForegroundProcessName: foreground ?? "FiveM_b3407_GTAProcess.exe"));
            }
        }

        events.Add(Adapter(Start.AddSeconds(20)));
        events.Add(VramTable(Start.AddSeconds(20)));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), Start, IncidentSeverity.Severe, "Auto: 425 ms frame", FrameAt),
            Start,
            Start.AddSeconds(90),
            PagingStallAgainstTheSeptember5SessionTests.Environment(),
            events,
            Analysis: null,
            Attachments: []);
    }

    /// <summary>The game, the desktop and the stream stack, on a card with 10 GB.</summary>
    private static GpuProcessMemorySample VramTable(DateTimeOffset at)
    {
        return new GpuProcessMemorySample(
            at,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(32_932, "FiveM_b3407_GTAProcess", Gigabytes(8.4), Gigabytes(0.2)),
                new GpuProcessMemoryUsage(1_120, "dwm", Gigabytes(1.0), 0),
                new GpuProcessMemoryUsage(9_004, "obs-browser-page", Gigabytes(0.8), 0),
            ]);
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset at)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            at,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 44,
            MemoryBandwidthUtilizationPercent: 14,
            UsedVramBytes: (ulong)(Total * 0.892),
            TotalVramBytes: Total,
            EncoderUtilizationPercent: 34,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    private static ulong Gigabytes(double value) => (ulong)(value * 1024 * 1024 * 1024);

    private static FrameTelemetrySample Frame(DateTimeOffset at, double frameTimeMs)
    {
        return new FrameTelemetrySample(
            at,
            frameTimeMs,
            GpuBusyMs: 4.9,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: frameTimeMs > 100 ? frameTimeMs - 12 : 8.0,
            CpuWaitMs: 0.1);
    }
}
