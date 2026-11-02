namespace FiveMDiagnostics.Core;

/// <summary>
/// Where a lost connection points, as far as the two reference hosts can say.
/// </summary>
public enum ConnectionFaultPlace
{
    /// <summary>The gateway was not measured while the address on the internet was silent.</summary>
    Unknown,

    /// <summary>The gateway kept answering, so the computer and the way to the router were working.</summary>
    BeyondGateway,

    /// <summary>The gateway went silent as well.</summary>
    AtOrBeforeGateway,
}

/// <summary>
/// A stretch of silence from the address on the internet long enough to be read as a lost connection.
/// </summary>
public sealed record InternetLoss(int Failures, int Probes, int LongestRun, TimeSpan LongestSilence, ConnectionFaultPlace Place);

/// <summary>
/// The one rule for reading the gateway and the address on the internet together.
/// </summary>
/// <remarks>
/// The session log and the incident analysis both use it. With a rule each they disagreed about the same
/// minutes, and with "any failure at all" as the rule one lost echo to the gateway turned a 60 second fault
/// at the operator into a fault in the computer.
/// </remarks>
public static class ConnectionFaultReading
{
    /// <summary>
    /// Missed probes in a row before silence is read as a lost connection. One or two lost echoes are
    /// ordinary, and three is several seconds of nothing.
    /// </summary>
    public const int SustainedFailures = 3;

    /// <summary>
    /// How far before the address on the internet a gateway probe can be and still belong to the same poll.
    /// The gateway is asked first, and it has at most a few hundred milliseconds to answer.
    /// </summary>
    private static readonly TimeSpan SamePollTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Places a silence from what the gateway did during it.
    /// </summary>
    /// <remarks>
    /// The gateway counts as gone if it was silent for a stretch of its own or for at least half of the
    /// time. A stray miss is neither, and the half is what decides a silence too short to contain a stretch.
    /// </remarks>
    public static ConnectionFaultPlace Place(int gatewayProbes, int gatewayFailures, int longestGatewayRun)
    {
        if (gatewayProbes == 0)
        {
            return ConnectionFaultPlace.Unknown;
        }

        return longestGatewayRun >= SustainedFailures || gatewayFailures * 2 >= gatewayProbes
            ? ConnectionFaultPlace.AtOrBeforeGateway
            : ConnectionFaultPlace.BeyondGateway;
    }

    /// <summary>
    /// The sentence for a place. "Points to" on purpose: one address cannot prove where a fault is, since a
    /// fault on the way to that address, or an upload full enough to hold its echoes, looks the same.
    /// </summary>
    public static string Describe(ConnectionFaultPlace place)
    {
        return place switch
        {
            ConnectionFaultPlace.BeyondGateway => "Nätets gateway fortsatte svara, vilket pekar på routerns internetanslutning, "
                + "modemet, operatören eller vägen till just den adressen.",
            ConnectionFaultPlace.AtOrBeforeGateway => "Nätets gateway slutade också svara, vilket pekar på datorn, vägen till "
                + "routern eller routern själv.",
            _ => "Gatewayen mättes inte under tiden, så det går inte att säga om felet låg i routern eller hos operatören.",
        };
    }

    public static int LongestFailureRun(IEnumerable<NetworkProbeSample> probes)
    {
        var longest = 0;
        var current = 0;

        foreach (var probe in probes.OrderBy(item => item.Timestamp))
        {
            current = probe.Success ? 0 : current + 1;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    /// <summary>From the first to the last miss of the longest stretch of misses.</summary>
    public static TimeSpan LongestSilence(IEnumerable<NetworkProbeSample> probes)
    {
        var longest = TimeSpan.Zero;
        DateTimeOffset? started = null;

        foreach (var probe in probes.OrderBy(item => item.Timestamp))
        {
            if (probe.Success)
            {
                started = null;
                continue;
            }

            started ??= probe.Timestamp;
            var silence = probe.Timestamp - started.Value;
            longest = silence > longest ? silence : longest;
        }

        return longest;
    }

    /// <summary>
    /// Reads a window of probes, or returns null when the address on the internet was never silent for long.
    /// </summary>
    /// <remarks>
    /// Only the gateway probes taken during the longest silence are used. A miss at the start of the window
    /// says nothing about a loss 40 seconds later.
    /// </remarks>
    public static InternetLoss? ReadInternetLoss(
        IReadOnlyList<NetworkProbeSample> gateway,
        IReadOnlyList<NetworkProbeSample> internet)
    {
        var ordered = internet.OrderBy(item => item.Timestamp).ToArray();
        var longest = 0;
        var longestEnd = -1;
        var current = 0;

        for (var index = 0; index < ordered.Length; index++)
        {
            current = ordered[index].Success ? 0 : current + 1;
            if (current > longest)
            {
                longest = current;
                longestEnd = index;
            }
        }

        if (longest < SustainedFailures)
        {
            return null;
        }

        var firstMiss = ordered[longestEnd - longest + 1].Timestamp;
        var lastMiss = ordered[longestEnd].Timestamp;
        var during = gateway.Where(item => item.Timestamp >= firstMiss - SamePollTolerance && item.Timestamp <= lastMiss).ToArray();

        return new InternetLoss(
            ordered.Count(item => !item.Success),
            ordered.Length,
            longest,
            lastMiss - firstMiss,
            Place(during.Length, during.Count(item => !item.Success), LongestFailureRun(during)));
    }
}
