namespace FiveMDiagnostics.Tests;

using System.Threading.Channels;

using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// What one session's reference probes write and say, with the network replaced by a script.
/// </summary>
public sealed class ReferenceProbesTests
{
    private const string Gateway = "192.168.1.1";
    private const string Internet = "1.1.1.1";

    [Fact]
    public async Task BothHostsAreProbedAndCarryTheirFlags()
    {
        var rig = new Rig(Internet);

        var samples = await rig.PollAsync();

        Assert.Collection(
            samples,
            gateway =>
            {
                Assert.Equal(Gateway, gateway.Host);
                Assert.True(gateway.IsReferenceHost);
                Assert.False(gateway.IsInternetReference);
            },
            internet =>
            {
                Assert.Equal(Internet, internet.Host);
                Assert.True(internet.IsReferenceHost);
                Assert.True(internet.IsInternetReference);
            });
    }

    /// <summary>
    /// The collector's own probe path rather than the script's, against loopback so no network is needed.
    /// Whether the ping succeeds is not the point: metrics.csv reads the rows of one timestamp as one probe,
    /// and in a redacted export the flags are all that tell the two hosts apart.
    /// </summary>
    [Fact]
    public async Task TheCollectorStampsEachProbeAndKeepsItsFlags()
    {
        using var collector = new NetworkTelemetryCollector();
        var context = Context();

        var gateway = await collector.ProbeReferenceAsync(context, "127.0.0.1", isInternet: false, CancellationToken.None);
        var internet = await collector.ProbeReferenceAsync(context, "127.0.0.1", isInternet: true, CancellationToken.None);

        Assert.NotEqual(gateway.Timestamp, internet.Timestamp);
        Assert.True(gateway.IsReferenceHost);
        Assert.False(gateway.IsInternetReference);
        Assert.True(internet.IsReferenceHost);
        Assert.True(internet.IsInternetReference);
    }

    /// <summary>
    /// An empty host makes the ping throw, with no network involved. A failure that lost its flags would be
    /// filed under the game server.
    /// </summary>
    [Fact]
    public async Task AProbeThatThrowsKeepsItsFlagsAndReason()
    {
        using var collector = new NetworkTelemetryCollector();

        var sample = await collector.ProbeReferenceAsync(Context(), string.Empty, isInternet: true, CancellationToken.None);

        Assert.False(sample.Success);
        Assert.False(string.IsNullOrWhiteSpace(sample.FailureReason));
        Assert.True(sample.IsReferenceHost);
        Assert.True(sample.IsInternetReference);
    }

    /// <summary>
    /// A router that drops ping never answers, and looking it up on every poll cost 85 MB of allocations an
    /// hour on a machine with many adapters.
    /// </summary>
    [Fact]
    public async Task AGatewayThatHasNeverAnsweredIsNotLookedUpOnEveryPoll()
    {
        var rig = new Rig(Internet);
        rig.Down.Add(Gateway);

        for (var i = 0; i < 10; i++)
        {
            await rig.PollAsync();
        }

        Assert.Equal(1, rig.GatewayLookups);
    }

    [Fact]
    public async Task WithoutAnAddressOnlyTheGatewayIsAsked()
    {
        var rig = new Rig(internetHost: null);

        var samples = await rig.PollAsync();

        Assert.Equal(Gateway, Assert.Single(samples).Host);
        Assert.DoesNotContain(Internet, rig.Asked);
    }

    [Fact]
    public async Task AnAddressThatHasNeverAnsweredIsNotEvidenceAndIsNoOutage()
    {
        var rig = new Rig(Internet);
        rig.Down.Add(Internet);

        for (var i = 0; i < 40; i++)
        {
            Assert.DoesNotContain(await rig.PollAsync(), item => item.IsInternetReference);
        }

        Assert.DoesNotContain(rig.Status.Entries, item => item.Source == ReferenceProbes.InternetSource);

        // Said once, and it goes on being tried at a lower rate rather than being dropped.
        var line = Assert.Single(rig.Status.Entries);
        Assert.Equal(StatusLevel.Info, line.Level);
        Assert.Contains(Internet, line.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Asked.Skip(30), item => item == Internet);
        Assert.True(rig.Asked.Count(item => item == Internet) < 40);
    }

    /// <summary>
    /// A session started while the internet is down: the gateway answers and the address does not, which is
    /// the fault this exists to report and must not end up looking like a host that blocks ping for good.
    /// </summary>
    [Fact]
    public async Task AnAddressThatStartsAnsweringIsMeasuredFromThenOn()
    {
        var rig = new Rig(Internet);
        rig.Down.Add(Internet);
        for (var i = 0; i < 40; i++)
        {
            await rig.PollAsync();
        }

        rig.Down.Clear();
        IReadOnlyList<NetworkProbeSample> samples = [];
        for (var i = 0; i < 20 && !samples.Any(item => item.IsInternetReference); i++)
        {
            samples = await rig.PollAsync();
        }

        Assert.Contains(samples, item => item.IsInternetReference && item.Success);
        Assert.Equal(2, rig.Status.Entries.Count);

        rig.Down.Add(Internet);
        var failed = await rig.PollAsync();
        Assert.Contains(failed, item => item.IsInternetReference && !item.Success);
    }

    [Fact]
    public async Task ALostConnectionIsWarnedAboutUnderItsOwnSourceAndItsEndSaid()
    {
        var rig = new Rig(Internet);
        await rig.PollAsync();

        rig.Down.Add(Internet);
        await rig.PollAsync();
        await rig.PollAsync();
        Assert.Empty(rig.Status.Entries);

        var failed = await rig.PollAsync();
        Assert.Contains(failed, item => item.IsInternetReference && !item.Success);

        // Its own source, or any line about the server probe takes the outage off the banner.
        var warning = Assert.Single(rig.Status.Entries);
        Assert.Equal(StatusLevel.Warning, warning.Level);
        Assert.Equal(ReferenceProbes.InternetSource, warning.Source);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway), warning.Message, StringComparison.Ordinal);

        rig.Down.Clear();
        await rig.PollAsync();

        Assert.Equal(2, rig.Status.Entries.Count);
        Assert.Equal(StatusLevel.Info, rig.Status.Entries[1].Level);
        Assert.Equal(ReferenceProbes.InternetSource, rig.Status.Entries[1].Source);
    }

    /// <summary>
    /// The router going takes both hosts with it, and the reports have to point at this side of it.
    /// </summary>
    [Fact]
    public async Task BothGoingPointsAtThisSideOfTheRouter()
    {
        var rig = new Rig(Internet);
        await rig.PollAsync();

        rig.Down.Add(Internet);
        rig.Down.Add(Gateway);
        for (var i = 0; i < 3; i++)
        {
            await rig.PollAsync();
        }

        var warning = Assert.Single(rig.Status.Entries);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.AtOrBeforeGateway), warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine that has moved to another network keeps asking its old gateway forever if this is only
    /// looked up once, and every later report then blames the connection.
    /// </summary>
    [Fact]
    public async Task AGatewayThatStopsAnsweringIsLookedUpAgain()
    {
        var rig = new Rig(Internet);
        await rig.PollAsync();

        rig.Down.Add(Gateway);
        await rig.PollAsync();

        rig.CurrentGateway = "10.0.0.1";
        var samples = await rig.PollAsync();

        Assert.Contains(samples, item => item.Host == "10.0.0.1" && item.Success);
        Assert.Equal("10.0.0.1", rig.Probes.ActiveGatewayHost);
    }

    /// <summary>A pulled cable has no route, and the old gateway going unanswered is then the finding.</summary>
    [Fact]
    public async Task NoRouteKeepsTheGatewayItHad()
    {
        var rig = new Rig(Internet);
        await rig.PollAsync();

        rig.Down.Add(Gateway);
        rig.CurrentGateway = null;
        await rig.PollAsync();
        var samples = await rig.PollAsync();

        Assert.Contains(samples, item => item.Host == Gateway && !item.Success);
    }

    [Fact]
    public async Task AHealthyGatewayIsOnlyLookedUpNowAndThen()
    {
        var rig = new Rig(Internet);

        for (var i = 0; i < 10; i++)
        {
            await rig.PollAsync();
        }

        Assert.Equal(1, rig.GatewayLookups);

        rig.Advance(TimeSpan.FromMinutes(2));
        await rig.PollAsync();

        Assert.Equal(2, rig.GatewayLookups);
    }

    [Fact]
    public async Task WithoutAGatewayTheOutageSaysItCannotPlaceIt()
    {
        var rig = new Rig(Internet) { CurrentGateway = null };
        await rig.PollAsync();

        rig.Down.Add(Internet);
        for (var i = 0; i < 3; i++)
        {
            await rig.PollAsync();
        }

        var warning = Assert.Single(rig.Status.Entries);
        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.Unknown), warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1")]
    [InlineData(" 1.1.1.1 ", "1.1.1.1")]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData("172.32.0.1", "172.32.0.1")]
    [InlineData("100.128.0.1", "100.128.0.1")]
    [InlineData("2606:4700:4700::1111", "2606:4700:4700::1111")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("one.one.one.one", null)]

    // Forms the parser accepts as something other than what was typed.
    [InlineData("1", null)]
    [InlineData("1.1.1", null)]
    [InlineData("0x01010101", null)]

    // Local addresses go on answering through an outage.
    [InlineData("127.0.0.1", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData("10.0.0.1", null)]
    [InlineData("172.16.0.1", null)]
    [InlineData("192.168.1.1", null)]
    [InlineData("169.254.0.1", null)]
    [InlineData("100.64.0.1", null)]
    [InlineData("224.0.0.1", null)]
    [InlineData("::1", null)]
    [InlineData("fe80::1", null)]
    [InlineData("fd00::1", null)]
    public void TheSettingHasToBeAPublicIpAddress(string? value, string? expected)
    {
        Assert.Equal(expected, ReferenceProbes.ParseInternetHost(value));
    }

    private static CollectorContext Context()
    {
        var clock = new DateTimeOffset(2026, 11, 1, 23, 41, 0, TimeSpan.Zero);
        return new CollectorContext(
            Channel.CreateUnbounded<TelemetryEvent>().Writer,
            new DiagnosticsSettings(),
            new RecordingStatusSink(),
            new NoGame(),
            () => clock = clock.AddSeconds(1));
    }

    private sealed class NoGame : ITargetProcessResolver
    {
        public TargetProcessInfo? TryGetTargetProcess() => null;
    }

    private sealed class Rig
    {
        private DateTimeOffset _now = new(2026, 11, 1, 23, 41, 0, TimeSpan.Zero);

        public Rig(string? internetHost)
        {
            Probes = new ReferenceProbes(
                internetHost,
                ProbeAsync,
                () =>
                {
                    GatewayLookups++;
                    return CurrentGateway;
                },
                Status,
                "NetworkTelemetry",
                () => _now);
        }

        public ReferenceProbes Probes { get; }

        public RecordingStatusSink Status { get; } = new();

        public HashSet<string> Down { get; } = [];

        public List<string> Asked { get; } = [];

        public string? CurrentGateway { get; set; } = Gateway;

        public int GatewayLookups { get; private set; }

        public void Advance(TimeSpan by) => _now += by;

        public async Task<IReadOnlyList<NetworkProbeSample>> PollAsync()
        {
            var samples = await Probes.PollAsync(CancellationToken.None);
            Advance(TimeSpan.FromSeconds(2));
            return samples;
        }

        private Task<NetworkProbeSample> ProbeAsync(string host, bool isInternet, CancellationToken cancellationToken)
        {
            Asked.Add(host);
            var at = _now;
            Advance(TimeSpan.FromMilliseconds(5));

            var answered = !Down.Contains(host);
            return Task.FromResult(new NetworkProbeSample(
                at,
                host,
                answered ? 5 : null,
                answered,
                answered ? null : "TimedOut",
                IsReferenceHost: true,
                IsInternetReference: isInternet));
        }
    }

    private sealed class RecordingStatusSink : IDiagnosticStatusSink
    {
        public List<(StatusLevel Level, string Source, string Message)> Entries { get; } = [];

        public void Report(StatusLevel level, string source, string message) => Entries.Add((level, source, message));
    }
}
