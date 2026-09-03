namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The window mode an incident is judged against is the one the game was in when it happened.
/// </summary>
/// <remarks>
/// <para>
/// The flag that says "the compositor is explained" was global and timeless: the session set it from the
/// settings re-read, and the engine read it whenever the analysis worker reached the incident. Those are
/// different moments. Incidents queue on a bounded channel and each one waits for the ones in front of
/// it, while the settings file is re-read every few minutes — so alt-Enter into exclusive fullscreen
/// stripped the present-mode evidence from every incident still queued from the borderless hour, and
/// alt-Enter the other way told an hour of genuinely unexplained incidents that they were accounted for.
/// </para>
/// <para>
/// Neither is a small loss. "Composed: Copy with GPU GDI for 100% of frames" is either the explanation
/// of the whole evening or a finding worth chasing, and which one it is depends entirely on a window
/// mode that a keystroke changes.
/// </para>
/// </remarks>
public sealed class WindowModeBelongsToTheIncidentTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 21, 4, 12, TimeSpan.Zero);

    /// <summary>When the game left the window and the compositor stopped being explained.</summary>
    private static readonly DateTimeOffset Fullscreen = Start.AddMinutes(30);

    /// <summary>
    /// One engine, one delegate, two incidents an hour apart: the borderless one is explained and the
    /// fullscreen one is not, whichever order the worker happens to reach them in.
    /// </summary>
    [Fact]
    public void EachIncidentIsJudgedAgainstTheModeItHappenedIn()
    {
        var engine = new FiveMCorrelationEngine
        {
            ComposedPresentExplainedAt = at => at < Fullscreen,
        };

        // Analysed in the wrong order on purpose. The queue drains as it drains; the answer may not.
        var afterwards = engine.Analyze(Incident(Fullscreen.AddMinutes(15)));
        var before = engine.Analyze(Incident(Start));

        Assert.DoesNotContain("Present mode", before.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(before.TimelineHighlights, item => item.Category == "Present mode");

        Assert.Contains("Composed", afterwards.Summary, StringComparison.Ordinal);
        Assert.Contains(afterwards.TimelineHighlights, item => item.Category == "Present mode");
    }

    /// <summary>
    /// The delegate is asked about the marker, not about now. Stated on its own because the whole defect
    /// was one argument nobody passed.
    /// </summary>
    [Fact]
    public void TheQuestionIsAskedAboutTheMarkersOwnMoment()
    {
        var asked = new List<DateTimeOffset>();
        var engine = new FiveMCorrelationEngine
        {
            ComposedPresentExplainedAt = at =>
            {
                asked.Add(at);
                return true;
            },
        };

        var markedAt = Start.AddSeconds(46);
        engine.Analyze(Incident(Start));

        Assert.All(asked, at => Assert.Equal(markedAt, at));
        Assert.NotEmpty(asked);
    }

    /// <summary>
    /// Nothing attached means nothing established, and every incident keeps carrying the compositor.
    /// </summary>
    [Fact]
    public void WithNothingAttachedTheCompositorIsStillAFinding()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(Start));

        Assert.Contains("Composed", analysis.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A window of frames that all went through the compositor, and one hitch to classify.
    /// </summary>
    private static IncidentRecord Incident(DateTimeOffset windowStart)
    {
        var events = new List<TelemetryEvent>();
        var markedAt = windowStart.AddSeconds(46);

        for (var i = 0; i < 600; i++)
        {
            var slow = i is 460;
            events.Add(new FrameTelemetrySample(
                windowStart.AddMilliseconds(i * 16.9),
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

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Severe, "Auto: 281 ms frame"),
            windowStart,
            windowStart.AddSeconds(90),
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
                "Disabled",
                ObsDetectedAtStart: false,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
