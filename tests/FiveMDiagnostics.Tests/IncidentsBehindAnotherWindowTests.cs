namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// An incident marked while the game was behind another window is ruled out rather than explained.
/// </summary>
/// <remarks>
/// The engine will happily explain such a window, and its explanation will be true and useless: the
/// start menu really was holding a core, the shell really was drawing, the counters really do look like
/// interference. On 4 September that produced <c>ExternalProcessInterference</c> as the evening's most
/// common verdict, 86 of 200 incidents, while never once naming the process — and the reader was left to
/// work out by hand that five of the nine measured stalls happened with the game in the background. The
/// foreground window settles it in one reading.
/// </remarks>
public sealed class IncidentsBehindAnotherWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 23, 59, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset MarkedAt = Start.AddSeconds(46);

    /// <summary>
    /// The start menu had the foreground. The window is not about the game, and the verdict says so
    /// instead of ranking it against nine hypotheses that are all about a game nobody was watching.
    /// </summary>
    [Fact]
    public void TheStartMenuHoldingTheForegroundIsTheVerdict()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            Focus(Start, gameHasFocus: true),
            Focus(Start.AddSeconds(40), gameHasFocus: false, "StartMenuExperienceHost"),
            Focus(Start.AddSeconds(70), gameHasFocus: true)));

        Assert.Equal(RootCauseCategory.GameNotInFocus, analysis.Hypotheses[0].Category);
        Assert.Contains(
            analysis.Hypotheses[0].Evidence,
            item => item.Contains("StartMenuExperienceHost", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same window with the game in front is judged as it always was. The rule only fires on a
    /// measurement, so a session without the focus collector loses nothing.
    /// </summary>
    [Fact]
    public void WithTheGameInFrontNothingChanges()
    {
        var withFocus = new FiveMCorrelationEngine().Analyze(Incident(
            Focus(Start, gameHasFocus: true),
            Focus(Start.AddSeconds(60), gameHasFocus: true)));

        var withoutTelemetry = new FiveMCorrelationEngine().Analyze(Incident());

        Assert.DoesNotContain(withFocus.Hypotheses, item => item.Category == RootCauseCategory.GameNotInFocus);
        Assert.DoesNotContain(withoutTelemetry.Hypotheses, item => item.Category == RootCauseCategory.GameNotInFocus);
    }

    /// <summary>
    /// Marked a fraction of a second before Windows finished changing the foreground — the Windows key
    /// case, where the shell starts drawing first and the handover is recorded afterwards.
    /// </summary>
    [Fact]
    public void AMarkerJustAheadOfTheHandoverIsStillTheHandover()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            Focus(Start, gameHasFocus: true),
            Focus(MarkedAt.AddMilliseconds(200), gameHasFocus: false, "StartMenuExperienceHost"),
            Focus(MarkedAt.AddSeconds(20), gameHasFocus: true)));

        Assert.Equal(RootCauseCategory.GameNotInFocus, analysis.Hypotheses[0].Category);
    }

    /// <summary>
    /// Tabbing back in is its own case: the frames are real and the player saw them, but they are the
    /// price of the switch, so the sentence says that rather than either counting or hiding them.
    /// </summary>
    [Fact]
    public void ComingBackIsNamedAsTheSwitchAndNotAsAFault()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            Focus(Start, gameHasFocus: true),
            Focus(Start.AddSeconds(20), gameHasFocus: false, "chrome"),
            Focus(MarkedAt.AddMilliseconds(-500), gameHasFocus: true)));

        Assert.Equal(RootCauseCategory.GameNotInFocus, analysis.Hypotheses[0].Category);
        Assert.Contains(
            analysis.Hypotheses[0].Evidence,
            item => item.Contains("swapchainen", StringComparison.Ordinal));
    }

    private static WindowFocusSample Focus(DateTimeOffset at, bool gameHasFocus, string process = "FiveM_b3407_GTAProcess") =>
        new(at, gameHasFocus, gameHasFocus ? 24400 : 9876, process);

    /// <summary>A window of ordinary frames with one 281 ms hitch in it, plus whatever focus is given.</summary>
    private static IncidentRecord Incident(params WindowFocusSample[] focusSamples)
    {
        var events = new List<TelemetryEvent>();

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

        events.AddRange(focusSamples);

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), MarkedAt, IncidentSeverity.Severe, "Auto: 281 ms frame"),
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
                ObsDetectedAtStart: false,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
