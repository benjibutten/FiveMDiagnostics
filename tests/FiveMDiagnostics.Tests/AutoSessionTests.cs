namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The session used to begin and end with a button, and on the machine under investigation only the
/// first half of that happened: the computer is switched off with the session still running, so the
/// closing summaries and the journal's session-end line were written on a minority of evenings.
/// </summary>
public sealed class AutoSessionTests
{
    private static readonly DateTimeOffset Evening = new(2026, 9, 11, 20, 14, 0, TimeSpan.Zero);

    /// <summary>
    /// The launcher is not the game. The resolver already returns null for it, and this is the other
    /// half of that: no target, no session, so a download does not open an evening.
    /// </summary>
    [Fact]
    public void TheSessionStartsWhenTheGameAppears()
    {
        var policy = new AutoSessionPolicy();

        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: false, game: null, Evening));
        Assert.Equal(AutoSessionAction.Start, policy.Evaluate(sessionActive: false, Game(), Evening.AddSeconds(1)));
    }

    [Fact]
    public void TheSessionEndsOnceTheGameHasStayedAway()
    {
        var policy = new AutoSessionPolicy();
        var exited = Evening.AddHours(4);

        // The evening itself, evaluated as it actually is: once a second, with the game there.
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, Game(), Evening));
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, Game(), exited.AddSeconds(-1)));

        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, game: null, exited));
        Assert.True(policy.InPostGameTail);

        // Counted from the last time the game was seen rather than from the first tick that missed it:
        // a game that dies just after a tick was already gone for that tick's worth of the wait.
        var lastSeen = exited.AddSeconds(-1);
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, game: null, lastSeen + PostGameWindow.Duration));
        Assert.Equal(AutoSessionAction.Stop, policy.Evaluate(sessionActive: true, game: null, lastSeen + PostGameWindow.Duration.Add(TimeSpan.FromSeconds(1))));
        Assert.False(policy.InPostGameTail);
    }

    /// <summary>
    /// The reason the wait is ten minutes and not one. Everything measured per process is two series
    /// across a restart and the session says so; two sessions would instead be two journals, two sets
    /// of per-session shares, and an evening that reads as two worse ones.
    /// </summary>
    [Fact]
    public void ARestartInsideTheWaitIsTheSameEvening()
    {
        var policy = new AutoSessionPolicy();
        var exited = Evening.AddMinutes(90);
        policy.Evaluate(sessionActive: true, Game(23688), Evening);
        policy.Evaluate(sessionActive: true, Game(23688), exited.AddSeconds(-1));

        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, game: null, exited));

        // The launcher, the game and the join together — well inside the wait, and a new process id.
        var restarted = exited.AddMinutes(6);
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, Game(33540), restarted));
        Assert.False(policy.InPostGameTail);

        // The wait is measured from the second exit, not the first.
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, game: null, restarted.AddMinutes(9)));
        Assert.Equal(AutoSessionAction.Stop, policy.Evaluate(sessionActive: true, game: null, restarted.AddMinutes(11)));
    }

    /// <summary>
    /// Stop has to end the evening rather than flicker it. Without the suppression the next evaluation,
    /// a second later, finds a game and no session and starts one.
    /// </summary>
    [Fact]
    public void StoppingByHandHoldsUntilTheGameGoesAway()
    {
        var policy = new AutoSessionPolicy();
        var game = Game();
        policy.Evaluate(sessionActive: true, game, Evening);

        policy.SuppressFor(game);
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: false, game, Evening.AddMinutes(1)));
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: false, game, Evening.AddHours(2)));

        // A restart is a new decision: the user stopped measuring a process, not the whole evening.
        Assert.Equal(AutoSessionAction.Start, policy.Evaluate(sessionActive: false, Game(33540), Evening.AddHours(2)));
    }

    [Fact]
    public void ClosingTheGameArmsTheAutomationAgain()
    {
        var policy = new AutoSessionPolicy();
        var game = Game();
        policy.SuppressFor(game);

        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: false, game, Evening));
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: false, game: null, Evening.AddMinutes(1)));
        Assert.Equal(AutoSessionAction.Start, policy.Evaluate(sessionActive: false, game, Evening.AddMinutes(2)));
    }

    /// <summary>
    /// A session started before the game — an OBS baseline, or the machine measured on its own — is the
    /// one deliberate use of the buttons left, and ending it after ten quiet minutes would take it away.
    /// </summary>
    [Fact]
    public void ASessionThatHasNotSeenTheGameIsNeverEnded()
    {
        var policy = new AutoSessionPolicy();

        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, game: null, Evening));
        Assert.Equal(AutoSessionAction.None, policy.Evaluate(sessionActive: true, game: null, Evening.AddHours(1)));
        Assert.False(policy.InPostGameTail);
    }

    /// <summary>
    /// Switching the automation off must not leave the tail flag standing.
    /// </summary>
    /// <remarks>
    /// The flag is maintained inside <see cref="AutoSessionPolicy.Evaluate"/>, which stops being called
    /// the moment the toggle goes off. Left standing it tells the window the game has closed while the
    /// game is running: the per-process VRAM table is emptied once a second and the status line says the
    /// evening is over.
    /// </remarks>
    [Fact]
    public void SwitchingTheAutomationOffClosesTheTail()
    {
        var policy = new AutoSessionPolicy();
        policy.Evaluate(sessionActive: true, Game(), Evening);
        policy.Evaluate(sessionActive: true, game: null, Evening.AddMinutes(1));
        Assert.True(policy.InPostGameTail);

        policy.Reset();

        Assert.False(policy.InPostGameTail);
    }

    [Fact]
    public void TheCardIsStillMeasuredForTheLengthOfTheWait()
    {
        var window = new PostGameWindow();

        Assert.True(window.IsOpen(gameRunning: true, Evening));
        Assert.True(window.IsOpen(gameRunning: false, Evening.AddMinutes(9)));
        Assert.False(window.IsOpen(gameRunning: false, Evening + PostGameWindow.Duration.Add(TimeSpan.FromSeconds(1))));
    }

    /// <summary>
    /// Collectors are held by the session manager and run again for every session, so a window the last
    /// evening left open would sample this one before its game appears.
    /// </summary>
    [Fact]
    public void TheWaitDoesNotCarryIntoTheNextSession()
    {
        var window = new PostGameWindow();
        window.IsOpen(gameRunning: true, Evening);

        window.Reset();

        Assert.False(window.IsOpen(gameRunning: false, Evening.AddSeconds(1)));
    }

    private static TargetProcessInfo Game(int processId = 23688)
    {
        return new TargetProcessInfo(processId, "FiveM_b3407_GTAProcess", null, Evening, Evening);
    }
}
