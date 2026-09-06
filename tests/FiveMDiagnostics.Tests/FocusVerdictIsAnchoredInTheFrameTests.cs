namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The focus verdict describes the frame the incident is named after, not the moment it was marked.
/// </summary>
/// <remarks>
/// An incident is renamed when a worse frame lands inside its open window, and the marker cannot move
/// with it — the window bounds and every event collected in it hang off the marker. So the label says
/// "kl. 01:30:53" while the marker sits at 01:30:07, and classifying focus at the marker described a
/// moment up to 45 seconds away. It happened in 26 of the 47 focus verdicts of 5 September, and all four
/// of the worst were paging stalls hidden behind a half-second alt-tab.
/// </remarks>
public sealed class FocusVerdictIsAnchoredInTheFrameTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 23, 29, 30, TimeSpan.Zero);

    private static readonly DateTimeOffset MarkedAt = Start.AddSeconds(37);

    private static readonly DateTimeOffset WorstFrameAt = MarkedAt.AddSeconds(45);

    /// <summary>
    /// A StreamDeck press at the marker, the game in front for the whole of the frame 45 seconds later.
    /// The verdict is about the frame.
    /// </summary>
    [Fact]
    public void AFocusBlinkAtTheMarkerDoesNotRuleOutAFrameAMinuteLater()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            escalated: true,
            Focus(Start, gameHasFocus: true),
            Focus(MarkedAt.AddMilliseconds(-200), gameHasFocus: false, "StreamDecky"),
            Focus(MarkedAt.AddSeconds(3), gameHasFocus: true)));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.GameNotInFocus);
    }

    /// <summary>
    /// The other direction: a window that really was in the background at the frame is still ruled out.
    /// Moving the anchor must not turn the measurement off.
    /// </summary>
    [Fact]
    public void AWindowInTheBackgroundAtTheFrameIsStillRuledOut()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            escalated: true,
            Focus(Start, gameHasFocus: true),
            Focus(WorstFrameAt.AddSeconds(-10), gameHasFocus: false, "obs64"),
            Focus(WorstFrameAt.AddSeconds(20), gameHasFocus: true)));

        Assert.Equal(RootCauseCategory.GameNotInFocus, analysis.Hypotheses[0].Category);
        Assert.Contains(analysis.Hypotheses[0].Evidence, item => item.Contains("obs64", StringComparison.Ordinal));
    }

    /// <summary>
    /// An unescalated incident is unchanged: with no worse frame there is nothing to move the anchor to,
    /// and the marker is the frame.
    /// </summary>
    [Fact]
    public void AnUnescalatedIncidentClassifiesAtItsMarker()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            escalated: false,
            Focus(Start, gameHasFocus: true),
            Focus(MarkedAt.AddMilliseconds(-200), gameHasFocus: false, "StreamDecky"),
            Focus(MarkedAt.AddSeconds(3), gameHasFocus: true)));

        Assert.Equal(RootCauseCategory.GameNotInFocus, analysis.Hypotheses[0].Category);
    }

    /// <summary>
    /// A focus verdict excludes the window from the hitch statistics; it does not explain the frame. The
    /// verdict behind it is printed anyway.
    /// </summary>
    /// <remarks>
    /// On 5 September a <c>GameNotInFocus</c> at 0.95 silently displaced a <c>StreamingOrDiskStall</c> at
    /// 0.80 in the incident that was a 455 ms read out of the paging file, and the summary said only that
    /// the game had been behind another window.
    /// </remarks>
    [Fact]
    public void TheVerdictBehindAFocusVerdictIsStillPrinted()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            escalated: true,
            Focus(Start, gameHasFocus: true),
            Focus(WorstFrameAt.AddSeconds(-10), gameHasFocus: false, "obs64"),
            Focus(WorstFrameAt.AddSeconds(20), gameHasFocus: true)));

        Assert.Equal(RootCauseCategory.GameNotInFocus, analysis.Hypotheses[0].Category);
        Assert.Contains("förklarar inte framen", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains(ToLabelOfSecondPlace(analysis), analysis.Summary, StringComparison.Ordinal);
    }

    /// <summary>The runner-up's own label, so the assertion above cannot pass on an empty string.</summary>
    private static string ToLabelOfSecondPlace(IncidentAnalysis analysis)
    {
        Assert.True(analysis.Hypotheses.Count > 1, "the window produced no second verdict to print");
        return $"{analysis.Hypotheses[1].Confidence:P0}";
    }

    private static WindowFocusSample Focus(DateTimeOffset at, bool gameHasFocus, string foreground = "FiveM_b3407_GTAProcess")
    {
        return new WindowFocusSample(at, gameHasFocus, ForegroundProcessId: 1, foreground);
    }

    /// <param name="escalated">
    /// Whether the incident was renamed after a worse frame, which is what moves the anchor away from
    /// the marker.
    /// </param>
    private static IncidentRecord Incident(bool escalated, params WindowFocusSample[] focusSamples)
    {
        var frameAt = escalated ? WorstFrameAt : MarkedAt;
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(Frame(Start.AddMilliseconds(i * 16.7), 16.7));
        }

        events.Add(Frame(frameAt, 455, cpuBusyMs: 443));
        events.AddRange(focusSamples);

        // The trace the incident's own capture produced: a paging read out of D: whose service time is
        // the frame. It is what the focus verdict silences, and it is the evening's actual cause.
        events.Add(new ArtifactEvidence(
            frameAt,
            ArtifactKind.EtlTrace,
            "Disk: långsammaste diskoperation i spåret: 455 ms för en 64 kB-läsning ur D:\\pagefile.sys.",
            new Dictionary<string, double>
            {
                ["durationSeconds"] = 27,
                ["cpuSampleCount"] = 184_234,
                ["cpuSubjectIsGame"] = 1,
                ["cpuSubjectProcessCores"] = 3.67,
                ["diskGameHardFaults"] = 18,
                ["diskPagingReadMaxMs_D:"] = 455,
                ["diskVolumeOperations_C:"] = 743,
                ["diskVolumeMedianMs_C:"] = 0.04,
                ["gameThreadWaitThreadId"] = 28_144,
                ["gameThreadMaxWaitMs"] = 455,
                ["gameThreadWaitIntervalCount"] = 1,
                ["gameThreadWait0StartUnixMs"] = frameAt.ToUnixTimeMilliseconds(),
                ["gameThreadWait0EndUnixMs"] = frameAt.AddMilliseconds(455).ToUnixTimeMilliseconds(),
                ["gameThreadWait0DurationMs"] = 455,
                ["gameThreadWait0UserRequest"] = 0,
            }));

        // Enough of a storage signal to give the focus verdict something to stand in front of.
        for (var i = 0; i < 60; i++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(i),
                TotalCpuUsagePercent: 38,
                new Dictionary<string, double>(),
                MemoryCommitPercent: 91,
                AvailableMemoryMb: 1_500,
                TopCpuProcesses: [],
                TopDiskProcesses: [],
                DiskAverageLatencyMs: 62,
                DiskQueueLength: 3,
                HardFaultPagesPerSecond: 240,
                WorstDiskInstance: "1 D:"));
        }

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(
                Guid.NewGuid(),
                MarkedAt,
                IncidentSeverity.Severe,
                "Auto: 455 ms frame",
                escalated ? WorstFrameAt : null),
            Start,
            Start.AddSeconds(120),
            PagingStallAgainstTheSeptember5SessionTests.Environment(),
            events,
            Analysis: null,
            Attachments: []);
    }

    private static FrameTelemetrySample Frame(DateTimeOffset at, double frameTimeMs, double? cpuBusyMs = null)
    {
        return new FrameTelemetrySample(
            at,
            frameTimeMs,
            GpuBusyMs: 5.2,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuBusyMs ?? 9.2,
            CpuWaitMs: 0.1);
    }
}
