namespace FiveMDiagnostics.Tests;

using System.Text.Json;

using FiveMDiagnostics.Core;

/// <summary>
/// The one rule for where a lost connection points, which the session log and the incidents both use.
/// </summary>
public sealed class ConnectionFaultReadingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 23, 41, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, 0, ConnectionFaultPlace.Unknown)]
    [InlineData(20, 0, 0, ConnectionFaultPlace.BeyondGateway)]
    [InlineData(20, 1, 1, ConnectionFaultPlace.BeyondGateway)]
    [InlineData(20, 3, 1, ConnectionFaultPlace.BeyondGateway)]
    [InlineData(20, 3, 3, ConnectionFaultPlace.AtOrBeforeGateway)]
    [InlineData(20, 10, 2, ConnectionFaultPlace.AtOrBeforeGateway)]
    [InlineData(3, 1, 1, ConnectionFaultPlace.BeyondGateway)]
    [InlineData(3, 2, 2, ConnectionFaultPlace.AtOrBeforeGateway)]
    public void TheGatewayIsReadBySustainedSilence(int probes, int failures, int run, ConnectionFaultPlace expected)
    {
        Assert.Equal(expected, ConnectionFaultReading.Place(probes, failures, run));
    }

    [Fact]
    public void ScatteredMissesAreNotALoss()
    {
        var internet = Probes(Enumerable.Range(0, 30).Select(i => i is not (3 or 10 or 20)).ToArray());

        Assert.Null(ConnectionFaultReading.ReadInternetLoss([], internet));
    }

    [Fact]
    public void ALossReportsItsLongestStretch()
    {
        var internet = Probes(Enumerable.Range(0, 30).Select(i => i is not (2 or >= 10 and < 20)).ToArray());

        var loss = ConnectionFaultReading.ReadInternetLoss(Probes(Enumerable.Repeat(true, 30).ToArray()), internet);

        Assert.NotNull(loss);
        Assert.Equal(11, loss.Failures);
        Assert.Equal(30, loss.Probes);
        Assert.Equal(10, loss.LongestRun);
        Assert.Equal(TimeSpan.FromSeconds(18), loss.LongestSilence);
        Assert.Equal(TimeSpan.FromSeconds(18), ConnectionFaultReading.LongestSilence(internet));
        Assert.Equal(ConnectionFaultPlace.BeyondGateway, loss.Place);
    }

    /// <summary>
    /// A gateway that hiccuped at the start of the window says nothing about the connection going 40
    /// seconds later. The first version read the whole window and blamed the computer.
    /// </summary>
    [Fact]
    public void GatewayMissesOutsideTheSilenceAreNotCounted()
    {
        var gateway = Probes(Enumerable.Range(0, 30).Select(i => i is >= 3).ToArray());
        var internet = Probes(Enumerable.Range(0, 30).Select(i => i is not (>= 15 and < 25)).ToArray());

        var loss = ConnectionFaultReading.ReadInternetLoss(gateway, internet);

        Assert.NotNull(loss);
        Assert.Equal(ConnectionFaultPlace.BeyondGateway, loss.Place);
    }

    [Fact]
    public void GatewayMissesDuringTheSilenceAreCounted()
    {
        var gateway = Probes(Enumerable.Range(0, 30).Select(i => i is not (>= 15 and < 20)).ToArray());
        var internet = Probes(Enumerable.Range(0, 30).Select(i => i is not (>= 15 and < 25)).ToArray());

        var loss = ConnectionFaultReading.ReadInternetLoss(gateway, internet);

        Assert.NotNull(loss);
        Assert.Equal(ConnectionFaultPlace.AtOrBeforeGateway, loss.Place);
    }

    /// <summary>
    /// The collector stamps each probe when it is sent and asks the gateway first, so the gateway probe of
    /// the first silent poll lies just before the silence and still belongs to it.
    /// </summary>
    [Fact]
    public void TheGatewayProbeOfTheSamePollBelongsToTheSilence()
    {
        var internet = Probes([false, false, false, true]);
        var gateway = internet
            .Select(item => new NetworkProbeSample(
                item.Timestamp.AddMilliseconds(-300),
                "192.168.1.1",
                null,
                Success: false,
                "TimedOut",
                IsReferenceHost: true))
            .Take(3)
            .ToArray();

        var loss = ConnectionFaultReading.ReadInternetLoss(gateway, internet);

        Assert.NotNull(loss);
        Assert.Equal(ConnectionFaultPlace.AtOrBeforeGateway, loss.Place);
    }

    [Fact]
    public void NoGatewayDuringTheSilenceCannotBePlaced()
    {
        var loss = ConnectionFaultReading.ReadInternetLoss([], Probes([false, false, false]));

        Assert.NotNull(loss);
        Assert.Equal(ConnectionFaultPlace.Unknown, loss.Place);
    }

    /// <summary>
    /// An update must not start contacting a third party on its own, and a new install should get the
    /// measurement. The settings class says the two are told apart this way.
    /// </summary>
    [Fact]
    public void OnlyANewInstallStartsWithAnInternetAddress()
    {
        Assert.Equal("1.1.1.1", DiagnosticsSettings.CreateDefault().InternetReferenceHost);
        Assert.Null(new DiagnosticsSettings().InternetReferenceHost);
        Assert.Null(JsonSerializer.Deserialize<DiagnosticsSettings>("{}")!.InternetReferenceHost);
    }

    /// <summary>
    /// A journal written before the flag existed reads as not being the internet address.
    /// </summary>
    [Fact]
    public void AnOldProbeRecordReadsAsNotTheInternetAddress()
    {
        var old = """
            {"$type":"probe","Timestamp":"2026-11-01T23:41:00+00:00","Host":"192.168.1.1","RoundTripTimeMs":1,"Success":true,"IsReferenceHost":true,"Source":"Probe"}
            """;

        var sample = Assert.IsType<NetworkProbeSample>(JsonSerializer.Deserialize<TelemetryEvent>(old));

        Assert.True(sample.IsReferenceHost);
        Assert.False(sample.IsInternetReference);
    }

    private static NetworkProbeSample[] Probes(bool[] answers)
    {
        return answers
            .Select((answered, index) => new NetworkProbeSample(
                Start.AddSeconds(index * 2),
                "1.1.1.1",
                answered ? 9 : null,
                answered,
                answered ? null : "TimedOut",
                IsReferenceHost: true,
                IsInternetReference: true))
            .ToArray();
    }
}
