namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

/// <summary>
/// Says when the address on the internet stopped answering, and which side of the router that points to.
/// </summary>
/// <remarks>
/// On 2 November the game and the stream lost the connection within two seconds of each other at 00:41:59.
/// The gateway answered every probe and the router logged nothing, and that was as far as the session could
/// go: with nothing measured beyond the router, a fault in its internet connection or at the operator looked
/// exactly like a healthy line. Incidents cannot carry this on their own either, because an incident needs a
/// bad frame and a lost connection does not have to produce one.
/// </remarks>
internal sealed class InternetOutageTracker(string host)
{
    private DateTimeOffset _firstFailureAt;
    private int _failures;
    private int _gatewayProbes;
    private int _gatewayFailures;
    private int _gatewayRun;
    private int _longestGatewayRun;
    private bool _reported;

    /// <param name="timestamp">When the address on the internet was asked.</param>
    /// <param name="internetAnswered">Whether it answered.</param>
    /// <param name="gatewayAnswered">Whether the gateway answered, or null if it was not measured.</param>
    /// <returns>A line for the session log when an outage starts or ends, otherwise null.</returns>
    public InternetOutageNotice? Observe(DateTimeOffset timestamp, bool internetAnswered, bool? gatewayAnswered)
    {
        if (internetAnswered)
        {
            var ended = _reported
                ? new InternetOutageNotice(
                    StatusLevel.Info,
                    $"{host} på internet svarar igen kl. {timestamp.ToLocalTime():HH:mm:ss}, efter "
                    + $"{(timestamp - _firstFailureAt).TotalSeconds:F0} s utan svar. {DescribePlace()}")
                : null;

            _failures = 0;
            _gatewayProbes = 0;
            _gatewayFailures = 0;
            _gatewayRun = 0;
            _longestGatewayRun = 0;
            _reported = false;
            return ended;
        }

        if (_failures++ == 0)
        {
            _firstFailureAt = timestamp;
        }

        if (gatewayAnswered is { } answered)
        {
            _gatewayProbes++;
            _gatewayFailures += answered ? 0 : 1;
            _gatewayRun = answered ? 0 : _gatewayRun + 1;
            _longestGatewayRun = Math.Max(_longestGatewayRun, _gatewayRun);
        }

        if (_reported || _failures < ConnectionFaultReading.SustainedFailures)
        {
            return null;
        }

        _reported = true;
        return new InternetOutageNotice(
            StatusLevel.Warning,
            $"{host} på internet slutade svara kl. {_firstFailureAt.ToLocalTime():HH:mm:ss} och har missat "
            + $"{_failures} prober i rad. {DescribePlace()}");
    }

    private string DescribePlace()
    {
        return ConnectionFaultReading.Describe(
            ConnectionFaultReading.Place(_gatewayProbes, _gatewayFailures, _longestGatewayRun));
    }
}

internal sealed record InternetOutageNotice(StatusLevel Level, string Message);
