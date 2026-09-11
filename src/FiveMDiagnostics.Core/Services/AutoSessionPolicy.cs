namespace FiveMDiagnostics.Core;

/// <summary>What the session should do about the game process right now.</summary>
public enum AutoSessionAction
{
    None,
    Start,
    Stop,
}

/// <summary>
/// Starts the session when the game appears and ends it once the game has stayed away.
/// </summary>
/// <remarks>
/// <para>
/// Both ends used to be a button, and on this machine only the first half of that ever happened: the
/// computer is switched off with the session still running, so the closing summaries and the journal's
/// session-end line were written on a minority of evenings. Following the game process is what makes
/// an evening a complete record without anyone having to remember anything.
/// </para>
/// <para>
/// The game process rather than the app's own start, which is the other obvious trigger and the wrong
/// one. A session carries a snapshot taken when it opens — machine uptime, whether OBS was up, the
/// graphics settings file, the displays — and a session that began at boot answers every one of those
/// about the wrong moment. It would also leave WPR's ring buffer recording all day on the machine
/// whose GPU behaviour is the thing under investigation.
/// </para>
/// </remarks>
public sealed class AutoSessionPolicy
{
    private readonly PostGameWindow _tail = new();
    private bool _gameSeen;
    private int? _suppressedProcessId;

    /// <summary>Whether the session is running on past a game that has closed.</summary>
    public bool InPostGameTail { get; private set; }

    public AutoSessionAction Evaluate(bool sessionActive, TargetProcessInfo? game, DateTimeOffset nowUtc)
    {
        if (!sessionActive)
        {
            InPostGameTail = false;
            return EvaluateIdle(game);
        }

        if (game is not null)
        {
            _gameSeen = true;
            _suppressedProcessId = null;
        }

        var tailOpen = _tail.IsOpen(game is not null, nowUtc);

        // A session started before the game — an OBS baseline, a measurement of the machine alone — has
        // nothing for the tail to be measured from, and ending it after ten quiet minutes would take the
        // one deliberate use of the buttons away.
        InPostGameTail = game is null && _gameSeen && tailOpen;

        if (game is not null || !_gameSeen || tailOpen)
        {
            return AutoSessionAction.None;
        }

        ResetTail();
        return AutoSessionAction.Stop;
    }

    /// <summary>
    /// Remembers that this game's session was ended by hand, so it is not started again underneath the
    /// user.
    /// </summary>
    /// <remarks>
    /// Without it Stop means nothing at all while the game is running: the next evaluation, a second
    /// later, finds a game and no session and starts one. The suppression is tied to the process id
    /// rather than to a timer, so it lasts exactly as long as the game the user stopped measuring —
    /// closing the game, or restarting it, is what arms the automation again.
    /// </remarks>
    public void SuppressFor(TargetProcessInfo? game)
    {
        ResetTail();
        _suppressedProcessId = game?.ProcessId;
    }

    /// <summary>
    /// Forgets this evening entirely: no game seen, no tail open, nothing suppressed.
    /// </summary>
    /// <remarks>
    /// For the automation being switched off and on again. <see cref="InPostGameTail"/> is maintained
    /// inside <see cref="Evaluate"/>, which is not called while the toggle is off, so a tail open at that
    /// moment would still read as open when the toggle comes back — and the window would go on emptying
    /// the per-process table and calling the evening over while the game was running.
    /// </remarks>
    public void Reset()
    {
        ResetTail();
        _suppressedProcessId = null;
    }

    private AutoSessionAction EvaluateIdle(TargetProcessInfo? game)
    {
        if (game is null)
        {
            ResetTail();
            _suppressedProcessId = null;
            return AutoSessionAction.None;
        }

        return _suppressedProcessId == game.ProcessId ? AutoSessionAction.None : AutoSessionAction.Start;
    }

    private void ResetTail()
    {
        _gameSeen = false;
        InPostGameTail = false;
        _tail.Reset();
    }
}
