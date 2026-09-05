namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// Frames presented while the game sat behind another window are not stutter, and the session has to
/// stop counting them as such.
/// </summary>
/// <remarks>
/// The measurement this was written for is the evening of 4 September: five of nine deeply measured
/// stalls coincided with Windows drawing part of itself — the start menu at over a core in three of them
/// — and every one of those frames went into the same hitch rate as the freezes that happened in
/// traffic. Pressing the Windows key is not something a player is going to stop doing, so the app has to
/// tell the two apart instead of asking her to.
/// </remarks>
public sealed class GameFocusMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 21, 0, 0, TimeSpan.Zero);

    private const double SixtyHertz = 60;

    /// <summary>Two refreshes at 60 Hz, which is what the monitor calls a hitch.</summary>
    private const double HitchMs = 34;

    /// <summary>
    /// The whole point, stated once: a hitch with the start menu in front is counted apart from the
    /// hitches in play, and the summary says which is which.
    /// </summary>
    [Fact]
    public void HitchesBehindAnotherWindowAreCountedSeparately()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));
        Assert.True(monitor.ObserveFrame(Start.AddSeconds(1), HitchMs));

        monitor.Observe(Focus(Start.AddSeconds(10), gameHasFocus: false, "StartMenuExperienceHost"));
        Assert.False(monitor.ObserveFrame(Start.AddSeconds(11), 380));
        Assert.False(monitor.ObserveFrame(Start.AddSeconds(12), 240));

        monitor.Observe(Focus(Start.AddSeconds(20), gameHasFocus: true, "FiveM_b3407_GTAProcess"));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report.HitchesInPlay);
        Assert.Equal(2, report.HitchesExcluded);
        Assert.Equal(1, report.Excursions);
        Assert.Equal(TimeSpan.FromSeconds(10), report.Unfocused);
        Assert.Contains("StartMenuExperienceHost", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coming back costs frames too, and those frames are the switch rather than the game. They are held
    /// out of the measurements for <see cref="GameFocusMonitor.RegainGrace"/>.
    /// </summary>
    [Fact]
    public void TheSecondsAfterTabbingBackAreStillTheSwitch()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));
        monitor.Observe(Focus(Start.AddSeconds(5), gameHasFocus: false, "explorer"));
        monitor.Observe(Focus(Start.AddSeconds(15), gameHasFocus: true, "FiveM_b3407_GTAProcess"));

        Assert.Equal(GameFocusState.Settling, monitor.StateAt(Start.AddSeconds(15.5)));
        Assert.False(monitor.ObserveFrame(Start.AddSeconds(15.5), 420));

        // Past the grace the game is being measured again, spike and all.
        Assert.Equal(GameFocusState.InPlay, monitor.StateAt(Start.AddSeconds(18)));
        Assert.True(monitor.ObserveFrame(Start.AddSeconds(18), 420));
    }

    /// <summary>
    /// The Windows key costs frames before Windows has finished handing the foreground over, so a frame
    /// already counted is taken back when the loss is observed a fraction of a second later.
    /// </summary>
    [Fact]
    public void AFrameCountedJustBeforeTheSwitchIsReclaimed()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));

        // Counted as play: nothing yet knows the key has been pressed.
        Assert.True(monitor.ObserveFrame(Start.AddSeconds(9.9), 119));

        monitor.Observe(Focus(Start.AddSeconds(10), gameHasFocus: false, "StartMenuExperienceHost"));
        monitor.Observe(Focus(Start.AddSeconds(14), gameHasFocus: true, "FiveM_b3407_GTAProcess"));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(0, report.HitchesInPlay);
        Assert.Equal(1, report.HitchesExcluded);
        Assert.Equal(0, report.FramesInPlay);
    }

    /// <summary>
    /// A frame well outside <see cref="GameFocusMonitor.LossGrace"/> stays where it was counted. The
    /// correction reaches back half a second, not over the minute before the switch.
    /// </summary>
    [Fact]
    public void TheReclaimDoesNotReachBackFurtherThanTheSwitchItself()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));
        Assert.True(monitor.ObserveFrame(Start.AddSeconds(5), 500));

        monitor.Observe(Focus(Start.AddSeconds(10), gameHasFocus: false, "StartMenuExperienceHost"));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report.HitchesInPlay);
        Assert.Equal(0, report.HitchesExcluded);
    }

    /// <summary>
    /// With no focus telemetry at all the session measures exactly what it measured before this existed.
    /// A collector that fails must not silently delete an evening's hitches.
    /// </summary>
    [Fact]
    public void WithoutObservationsEveryFrameCounts()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        Assert.Equal(GameFocusState.Unknown, monitor.StateAt(Start));
        Assert.True(monitor.ObserveFrame(Start, 900));
        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// An excursion still open when the summary is written is counted up to the last reading. This
    /// machine's sessions end by the computer being switched off, so the open one is the ordinary case.
    /// </summary>
    [Fact]
    public void AnExcursionStillOpenIsCounted()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));
        monitor.Observe(Focus(Start.AddMinutes(1), gameHasFocus: false, "chrome"));
        monitor.Observe(Focus(Start.AddMinutes(4), gameHasFocus: false, "chrome"));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report.Excursions);
        Assert.Equal(TimeSpan.FromMinutes(3), report.Unfocused);
    }

    /// <summary>
    /// A brief excursion is excluded from the measurements but gets no line of its own; a real alt-tab
    /// gets one, and it names the window that took the foreground.
    /// </summary>
    [Fact]
    public void OnlyExcursionsWorthReadingAboutAreReported()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));
        monitor.Observe(Focus(Start.AddSeconds(10), gameHasFocus: false, "ShellExperienceHost"));
        Assert.Null(monitor.Observe(Focus(Start.AddSeconds(10.5), gameHasFocus: true, "FiveM_b3407_GTAProcess")));

        monitor.Observe(Focus(Start.AddSeconds(30), gameHasFocus: false, "VoiceRecorder"));
        var line = monitor.Observe(Focus(Start.AddSeconds(60), gameHasFocus: true, "FiveM_b3407_GTAProcess"));

        Assert.NotNull(line);
        Assert.Contains("VoiceRecorder", line, StringComparison.Ordinal);
        Assert.Contains("30 s", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Moving between two background windows is the same excursion continuing, not a second one.
    /// </summary>
    /// <remarks>
    /// The shape of an ordinary alt-tab: out to Discord, on to the browser, back to the game. Counted per
    /// window it reads as two excursions of ten seconds, so the count is inflated and — worse, because it
    /// is the figure the summary leads with — the longest excursion of the evening is reported at half
    /// its length. The total unfocused time comes out right either way, which is what let it pass.
    /// </remarks>
    [Fact]
    public void MovingBetweenTwoBackgroundWindowsIsOneExcursion()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));
        Assert.Null(monitor.Observe(Focus(Start.AddSeconds(10), gameHasFocus: false, "Discord")));

        // The switch from one background window to the next writes nothing: the excursion has not ended.
        Assert.Null(monitor.Observe(Focus(Start.AddSeconds(20), gameHasFocus: false, "chrome")));

        var line = monitor.Observe(Focus(Start.AddSeconds(30), gameHasFocus: true, "FiveM_b3407_GTAProcess"));

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report.Excursions);
        Assert.Equal(TimeSpan.FromSeconds(20), report.Unfocused);
        Assert.Equal(TimeSpan.FromSeconds(20), report.LongestExcursion);

        // One line for the switch, naming the window that actually held the foreground and saying how
        // many others there were rather than listing them.
        Assert.NotNull(line);
        Assert.Contains("20 s", line, StringComparison.Ordinal);
        Assert.Contains("1 fönster till", line, StringComparison.Ordinal);

        // Both windows are still credited with their own time, because that is what makes the exclusion
        // checkable against what the player remembers doing.
        Assert.Equal(2, report.TopForegroundProcesses.Count);
        Assert.All(report.TopForegroundProcesses, holder => Assert.Equal(TimeSpan.FromSeconds(10), holder.Held));
    }

    /// <summary>
    /// An excursion open across a summary is counted once per summary, not accumulated by writing one.
    /// </summary>
    /// <remarks>
    /// The interim summary runs every quarter of an hour and the machine's sessions end by the computer
    /// being switched off, so a summary written during an alt-tab is the ordinary case rather than the
    /// edge one. Reading it must not change what the next one says.
    /// </remarks>
    [Fact]
    public void WritingTheSummaryDuringAnExcursionDoesNotChangeIt()
    {
        var monitor = new GameFocusMonitor(SixtyHertz);

        monitor.Observe(Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"));
        monitor.Observe(Focus(Start.AddMinutes(1), gameHasFocus: false, "chrome"));
        monitor.Observe(Focus(Start.AddMinutes(4), gameHasFocus: false, "chrome"));

        var first = monitor.Summary();
        var second = monitor.Summary();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Excursions, second.Excursions);
        Assert.Equal(first.Unfocused, second.Unfocused);
        Assert.Equal(first.Message, second.Message);

        // The window in front right now is named, even though it has not stopped holding the foreground.
        Assert.Contains("chrome", first.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The classification the analysis engine runs over an incident window is the same rule, including
    /// the look-ahead the live path cannot use.
    /// </summary>
    [Fact]
    public void ClassifyLooksForwardIntoTheSwitchThatHadAlreadyBegun()
    {
        var readings = new[]
        {
            Focus(Start, gameHasFocus: true, "FiveM_b3407_GTAProcess"),
            Focus(Start.AddSeconds(30.2), gameHasFocus: false, "StartMenuExperienceHost"),
            Focus(Start.AddSeconds(41), gameHasFocus: true, "FiveM_b3407_GTAProcess"),
        };

        // Marked before Windows finished the handover; the reading a fifth of a second later is evidence
        // about it.
        Assert.Equal(GameFocusState.NotInFocus, GameFocusMonitor.Classify(readings, Start.AddSeconds(30)));

        Assert.Equal(GameFocusState.NotInFocus, GameFocusMonitor.Classify(readings, Start.AddSeconds(35)));
        Assert.Equal(GameFocusState.Settling, GameFocusMonitor.Classify(readings, Start.AddSeconds(41.5)));
        Assert.Equal(GameFocusState.InPlay, GameFocusMonitor.Classify(readings, Start.AddSeconds(20)));
        Assert.Equal(GameFocusState.Unknown, GameFocusMonitor.Classify(readings, Start.AddSeconds(-5)));
    }

    private static WindowFocusSample Focus(DateTimeOffset at, bool gameHasFocus, string process) =>
        new(at, gameHasFocus, gameHasFocus ? 24400 : 9876, process);
}
