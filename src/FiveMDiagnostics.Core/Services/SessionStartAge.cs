namespace FiveMDiagnostics.Core;

/// <summary>
/// Says how long the machine and the game had been running when the measurement began.
/// </summary>
/// <remarks>
/// <para>
/// Written after the evening of 10 September, which produced the largest single result of the
/// investigation and could not be interpreted. The machine had crashed and been restarted before the
/// session; the settings were byte-for-byte those of the evening of 8 September; and the hitch rate came
/// in at 61.8 an hour against that evening's 167.4. The leading explanation was that a freshly booted
/// machine hitches less — and it could not be tested, because nothing in the session recorded how long
/// the machine had been up. Process ids do not answer it either: <c>csrss</c> held 1424 on both evenings.
/// </para>
/// <para>
/// The game's own age is here for the same reason and a smaller one. That evening the session was
/// attached to a game that had already been running for an unknown time, and the cleanest sub-check of
/// the finding — that the older of the evening's two game processes was the quieter one — rested on not
/// knowing how much older it was.
/// </para>
/// <para>
/// Both figures are approximations and are written as such. Windows Fast Startup makes a power-on a
/// resume from hibernation, and an uptime that survives it describes the last full boot rather than the
/// last time the user pressed the button. That is a caveat on the number, not a reason to omit it: the
/// question is whether two evenings differed by hours, and it answers that.
/// </para>
/// </remarks>
public static class SessionStartAge
{
    /// <summary>
    /// The session-start line, or null when neither figure could be read.
    /// </summary>
    /// <param name="machineUptime">How long Windows had been running; null when it could not be read.</param>
    /// <param name="gameStartedAt">When the game process started; null when the game was not running.</param>
    /// <param name="sessionStartedAt">When the measurement began, which is what both ages are measured to.</param>
    public static string? Describe(TimeSpan? machineUptime, DateTimeOffset? gameStartedAt, DateTimeOffset sessionStartedAt)
    {
        var machine = machineUptime is { } uptime && uptime > TimeSpan.Zero
            ? $"Maskinen har varit uppe i {Humanise(uptime)}"
            : null;

        // A game that started after the session did not exist to be measured; the clamp keeps a clock
        // that drifted a second from printing a negative age.
        var gameAge = gameStartedAt is { } startedAt && sessionStartedAt > startedAt
            ? sessionStartedAt - startedAt
            : (TimeSpan?)null;

        var game = gameAge is { } age
            ? $"spelet har kört i {Humanise(age)} när mätningen startar"
            : gameStartedAt is null ? "spelet kördes inte när mätningen startade" : null;

        var body = (machine, game) switch
        {
            (null, null) => null,
            (not null, null) => machine + " när mätningen startar.",
            (null, not null) => char.ToUpperInvariant(game![0]) + game[1..] + ".",
            _ => $"{machine} och {game}.",
        };

        return body is null
            ? null
            : body
                + " Drifttiden är läst ur Windows egen räknare; med Fast Startup räknar den från den"
                + " senaste riktiga omstarten och inte från senaste avstängningen.";
    }

    /// <summary>Hours and minutes, or minutes alone below the hour — the resolution the question needs.</summary>
    public static string Humanise(TimeSpan span)
    {
        var totalMinutes = (int)Math.Round(span.TotalMinutes);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        return hours == 0
            ? $"{minutes} min"
            : $"{hours} h {minutes} min";
    }
}
