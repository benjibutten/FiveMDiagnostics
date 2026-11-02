namespace FiveMDiagnostics.Collectors;

/// <summary>
/// One host probed for reference rather than as the game server, and whether its silence means anything.
/// </summary>
/// <remarks>
/// A failed probe only says something about the connection if the host has answered before. Routers and
/// firewalls that drop ping outright are ordinary, and without this a host that was never going to reply
/// reads as a network that was down for the whole session. It is never dropped for good, because the other
/// way to get here is a session started during an outage, and that connection will come back.
/// </remarks>
internal sealed class ReferenceProbeTarget(string host, bool isInternet, int probesBeforeQuiet, int pollsBetweenQuietProbes)
{
    private int _unanswered;
    private int _pollsSinceProbe;

    public string Host { get; } = host;

    /// <summary>True for an address on the internet, false for the network's own gateway.</summary>
    public bool IsInternet { get; } = isInternet;

    /// <summary>True while the host has stayed silent since the start for long enough to ask it less often.</summary>
    public bool IsQuiet { get; private set; }

    public bool HasAnswered { get; private set; }

    /// <summary>Whether to probe the host on this poll.</summary>
    public bool ShouldProbe()
    {
        if (!IsQuiet || ++_pollsSinceProbe >= pollsBetweenQuietProbes)
        {
            _pollsSinceProbe = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Takes in one probe's outcome and returns whether it counts as evidence.
    /// </summary>
    public bool Observe(bool answered)
    {
        if (answered)
        {
            HasAnswered = true;
            IsQuiet = false;
            return true;
        }

        if (HasAnswered)
        {
            return true;
        }

        IsQuiet = ++_unanswered >= probesBeforeQuiet;
        return false;
    }
}
