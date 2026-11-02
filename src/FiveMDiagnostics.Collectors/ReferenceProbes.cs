using System.Net;
using System.Net.Sockets;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

/// <summary>
/// Probes the gateway and the address on the internet for one session, whatever the game server does.
/// </summary>
/// <remarks>
/// Neither can speak for the path to the server, which is usually refusing ICMP anyway. Together they narrow
/// an unanswerable question: the gateway separates "the whole connection stalled" from "we measured nothing",
/// and the address beyond it says which side of the router the stall was on.
/// </remarks>
internal sealed class ReferenceProbes(
    string? internetHost,
    Func<string, bool, CancellationToken, Task<NetworkProbeSample>> probe,
    Func<string?> findGateway,
    IDiagnosticStatusSink status,
    string source,
    Func<DateTimeOffset> utcNow)
{
    /// <summary>
    /// Its own source, because a status line clears the banner of the source that wrote it. Sharing one with
    /// the rest of the collector took the outage off the screen with any line about the server probe.
    /// </summary>
    internal const string InternetSource = "NetworkTelemetry.Internet";

    /// <summary>At the default poll interval this is about a minute of silence.</summary>
    private const int ProbesBeforeQuiet = 30;

    /// <summary>At the default poll interval this is one probe every half minute.</summary>
    private const int PollsBetweenQuietProbes = 15;

    /// <summary>
    /// How often the gateway is looked up again. One that answered and then stopped is looked up on every
    /// poll: it may be the router that went, or the machine that moved to another network. One that has
    /// never answered is not, or a router that drops ping costs a lookup every poll for the whole evening.
    /// </summary>
    private static readonly TimeSpan GatewayLookupInterval = TimeSpan.FromMinutes(1);

    private readonly ReferenceProbeTarget? _internet = internetHost is null
        ? null
        : new ReferenceProbeTarget(internetHost, isInternet: true, ProbesBeforeQuiet, PollsBetweenQuietProbes);

    private readonly InternetOutageTracker? _outage = internetHost is null ? null : new InternetOutageTracker(internetHost);

    private ReferenceProbeTarget? _gateway;
    private DateTimeOffset _lastGatewayLookup = DateTimeOffset.MinValue;
    private bool _gatewayMissedLastProbe;

    /// <summary>The gateway currently being asked, or null when there is none worth quoting.</summary>
    public string? ActiveGatewayHost => _gateway is { IsQuiet: false } gateway ? gateway.Host : null;

    /// <summary>
    /// The value to use from the setting, or null when it is empty or not a public IP address.
    /// </summary>
    /// <remarks>
    /// A name would need DNS, and DNS failing would then read as the connection failing. It would also end
    /// up in status lines the session share has no reason to redact. A local address keeps answering through
    /// an outage, and reports would then say the connection was fine. The written-out form is required
    /// because the parser reads "1.1.1" as 1.1.0.1, so a typo would quietly ping someone else.
    /// </remarks>
    internal static string? ParseInternetHost(string? value)
    {
        var trimmed = value?.Trim();
        return IPAddress.TryParse(trimmed, out var parsed)
            && string.Equals(parsed.ToString(), trimmed, StringComparison.OrdinalIgnoreCase)
            && IsPublic(parsed)
                ? trimmed
                : null;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal
                || address.IsIPv6Multicast);
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 or 10 or 127 or >= 224 => false,
            100 => octets[1] is < 64 or > 127,
            169 => octets[1] != 254,
            172 => octets[1] is < 16 or > 31,
            192 => octets[1] != 168,
            _ => true,
        };
    }

    /// <summary>Runs one poll and returns the samples that count as evidence.</summary>
    public async Task<IReadOnlyList<NetworkProbeSample>> PollAsync(CancellationToken cancellationToken)
    {
        RefreshGateway();

        var gateway = await ProbeAsync(_gateway, cancellationToken).ConfigureAwait(false);
        var internet = await ProbeAsync(_internet, cancellationToken).ConfigureAwait(false);

        if (internet is not null && _outage?.Observe(internet.Timestamp, internet.Success, gateway?.Success) is { } notice)
        {
            status.Report(notice.Level, InternetSource, notice.Message);
        }

        return new[] { gateway, internet }.OfType<NetworkProbeSample>().ToArray();
    }

    private void RefreshGateway()
    {
        var now = utcNow();
        if (!_gatewayMissedLastProbe && now - _lastGatewayLookup < GatewayLookupInterval)
        {
            return;
        }

        _lastGatewayLookup = now;

        // Null keeps the one we have. No route at all is what a pulled cable looks like, and the probes going
        // unanswered is then the finding.
        if (findGateway() is { } found && !string.Equals(found, _gateway?.Host, StringComparison.OrdinalIgnoreCase))
        {
            _gateway = new ReferenceProbeTarget(found, isInternet: false, ProbesBeforeQuiet, PollsBetweenQuietProbes);
            _gatewayMissedLastProbe = false;
        }
    }

    private async Task<NetworkProbeSample?> ProbeAsync(ReferenceProbeTarget? target, CancellationToken cancellationToken)
    {
        if (target is null || !target.ShouldProbe())
        {
            return null;
        }

        var sample = await probe(target.Host, target.IsInternet, cancellationToken).ConfigureAwait(false);
        if (!target.IsInternet)
        {
            _gatewayMissedLastProbe = !sample.Success && target.HasAnswered;
        }

        var wasQuiet = target.IsQuiet;
        var counts = target.Observe(sample.Success);
        if (target.IsQuiet != wasQuiet)
        {
            ReportQuietChange(target);
        }

        return counts ? sample : null;
    }

    private void ReportQuietChange(ReferenceProbeTarget target)
    {
        var name = target.IsInternet ? $"Referensadressen {target.Host}" : $"Nätets gateway ({target.Host})";

        status.Report(
            StatusLevel.Info,
            source,
            target.IsQuiet
                ? $"{name} har inte svarat på någon av {ProbesBeforeQuiet} prober sedan sessionen började, så den "
                    + "provas nu bara då och då. Antingen svarar den inte på ICMP, eller så pågick ett nätavbrott redan "
                    + "när sessionen startade. Tystnaden räknas inte som ett avbrott förrän den har svarat en gång."
                : $"{name} svarar nu, efter att inte ha svarat sedan sessionen började. Slutar den svara igen räknas "
                    + "det som ett avbrott.");
    }
}
