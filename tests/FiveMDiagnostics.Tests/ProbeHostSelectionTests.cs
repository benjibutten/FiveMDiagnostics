namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// Cover for the probe host that spent a whole session measuring the wrong machine.
/// </summary>
/// <remarks>
/// The auto-detection latched onto 23.36.77.184:443 twenty-six seconds into a five hour session — a CDN
/// the embedded browser keeps a connection open to. It removed the false packet-loss hypotheses it was
/// written to remove, and replaced them with round-trip times that described a content delivery network
/// and were presented as evidence about the game server. Persistence cannot separate the two; both stay
/// open all evening. The port can.
/// </remarks>
public sealed class ProbeHostSelectionTests
{
    private static RemoteEndpointInfo Endpoint(string address, int port) => new("TCP", address, port);

    [Fact]
    public void TheDefaultFiveMPortWins()
    {
        var candidate = NetworkTelemetryCollector.SelectCandidate(
        [
            Endpoint("23.36.77.184", 443),
            Endpoint("135.125.160.15", 30120),
        ]);

        Assert.Equal("135.125.160.15", candidate?.RemoteAddress);
    }

    [Fact]
    public void ACdnOnPort443IsNeverACandidate()
    {
        var candidate = NetworkTelemetryCollector.SelectCandidate(
        [
            Endpoint("23.36.77.184", 443),
            Endpoint("104.18.2.1", 80),
            Endpoint("13.107.42.14", 443),
        ]);

        // Previously this returned the first routable endpoint as a last resort, which is exactly how the
        // CDN won. There is no last resort any more.
        Assert.Null(candidate);
    }

    [Fact]
    public void ACommunityServerOffTheDefaultPortIsStillACandidate()
    {
        var candidate = NetworkTelemetryCollector.SelectCandidate(
        [
            Endpoint("23.36.77.184", 443),
            Endpoint("51.68.204.60", 30125),
        ]);

        Assert.Equal("51.68.204.60", candidate?.RemoteAddress);
    }

    [Fact]
    public void LoopbackAndLinkLocalAreNotServers()
    {
        var candidate = NetworkTelemetryCollector.SelectCandidate(
        [
            Endpoint("127.0.0.1", 30120),
            Endpoint("169.254.10.5", 30120),
        ]);

        Assert.Null(candidate);
    }

    [Theory]
    [InlineData(30120, true)]
    [InlineData(30110, true)]
    [InlineData(30999, true)]
    [InlineData(30000, true)]
    [InlineData(443, false)]
    [InlineData(80, false)]
    [InlineData(31000, false)]
    [InlineData(29999, false)]
    public void OnlyTheFiveMPortRangeCounts(int port, bool expected)
    {
        Assert.Equal(expected, NetworkTelemetryCollector.IsGameServerPort(port));
    }
}
