namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// Two things the incidents of 10 September said badly, both found by reading fifty of them by hand.
/// </summary>
/// <remarks>
/// <para>
/// The first is a list that contradicted itself. Four incidents named <c>FiveM_ChromeBrowser</c> twice,
/// with two different CPU figures and — in the incident at 00:08:33 — the same sentence printed twice.
/// FiveM's NUI layer is Chromium and runs several processes under one name; the suspects were grouped by
/// name and process id, so each instance got its own row.
/// </para>
/// <para>
/// The second is the evening's one real freeze. At 01:47 the game stopped for 1 002 ms and then 2 973 ms,
/// and OBS's render-skip counter went from 78 to 379 across that single incident while moving forty steps
/// in the whole rest of the evening. OBS renders from its own canvas and does not skip because the game
/// stopped producing frames, so that burst is the only evidence in six evenings that a stall reached
/// underneath both processes — and the only way to see it was to read the absolute counter across twelve
/// consecutive incidents.
/// </para>
/// </remarks>
public sealed class September10IncidentReadabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 23, 47, 7, TimeSpan.Zero);

    private static readonly DateTimeOffset FrameAt = Start.AddSeconds(22);

    private readonly FiveMCorrelationEngine _engine = new();

    [Fact]
    public void OneProgramIsOneRowHoweverManyProcessesItRuns()
    {
        var analysis = _engine.Analyze(Incident());

        var browser = analysis.SuspectedProcesses
            .Where(item => item.ProcessName.Equals("FiveM_ChromeBrowser", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var row = Assert.Single(browser);

        // The three renderers held 8.5, 5.4 and 3.1 per cent of the machine at their peaks. What the
        // program cost is the sum; what any one row used to say was whichever instance sorted first.
        Assert.Equal(17.0, row.PeakCpuPercent, 1);
        Assert.Contains("3 processer med samma namn", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABurstOfSkippedRendersIsNamedInTheIncident()
    {
        var analysis = _engine.Analyze(Incident());

        var burst = Assert.Single(analysis.TimelineHighlights.Where(item =>
            item.Summary.Contains("hoppade över", StringComparison.Ordinal)));

        Assert.Contains("301 renderingar", burst.Summary, StringComparison.Ordinal);
        Assert.Contains("under båda processerna", burst.Summary, StringComparison.Ordinal);

        // Nothing reached the viewers, which is the half of it that is good news and has to be said too.
        Assert.Contains("tappade ingenting", burst.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// An evening where OBS drifts a frame at a time is every evening, and must stay quiet — otherwise
    /// the line means nothing when it does appear.
    /// </summary>
    [Fact]
    public void OrdinaryDriftIsNotABurst()
    {
        var analysis = _engine.Analyze(Incident(renderSkippedAtEnd: 81));

        Assert.DoesNotContain(
            analysis.TimelineHighlights,
            item => item.Summary.Contains("hoppade över", StringComparison.Ordinal));
    }

    private static IncidentRecord Incident(long renderSkippedAtEnd = 379)
    {
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.66),
                16.66,
                GpuBusyMs: 5.2,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 16.66,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 8.0,
                CpuWaitMs: 0.1));
        }

        events.Add(new FrameTelemetrySample(
            FrameAt,
            2973.4,
            GpuBusyMs: 9.0,
            DisplayLatencyMs: 20,
            MsBetweenPresents: 2973.4,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: 2973.0,
            CpuWaitMs: 0.2));

        // The NUI layer as it actually runs: three renderer processes under one name.
        ProcessActivity[] browsers =
        [
            new("FiveM_ChromeBrowser", 21044, 8.5, 71L * 1024 * 1024),
            new("FiveM_ChromeBrowser", 21120, 5.4, 12L * 1024 * 1024),
            new("FiveM_ChromeBrowser", 21188, 3.1, 4L * 1024 * 1024),
        ];

        for (var second = 0; second < 28; second++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 52,
                PerCoreUsagePercent: new Dictionary<string, double>(),
                MemoryCommitPercent: 52,
                AvailableMemoryMb: 13_400,
                TopCpuProcesses: browsers,
                TopDiskProcesses: browsers,
                DiskAverageLatencyMs: 13.8,
                DiskQueueLength: 0,
                HardFaultPagesPerSecond: 5));
        }

        // The counter as the evening recorded it: 78 at the previous incident, and wherever the window
        // leaves it.
        events.Add(Obs(Start, 78));
        events.Add(Obs(Start.AddSeconds(27), renderSkippedAtEnd));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), FrameAt, IncidentSeverity.Severe, "Auto: 2973 ms frame"),
            Start,
            Start.AddSeconds(28),
            PagingStallAgainstTheSeptember5SessionTests.Environment(),
            events,
            Analysis: null,
            Attachments: []);
    }

    private static ObsTelemetrySample Obs(DateTimeOffset at, long renderSkipped)
    {
        return new ObsTelemetrySample(
            at,
            IsConnected: true,
            ActiveFps: 60,
            AverageFrameRenderTimeMs: 1.3,
            RenderSkippedFrames: renderSkipped,
            OutputSkippedFrames: 0,
            CpuUsagePercent: 3.4,
            MemoryUsageMb: 900,
            IsStreaming: true,
            IsRecording: false,
            IsProcessRunning: true);
    }
}
