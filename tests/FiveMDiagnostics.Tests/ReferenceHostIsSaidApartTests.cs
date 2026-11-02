namespace FiveMDiagnostics.Tests;

using System.IO.Compression;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Export;

/// <summary>
/// The gateway is a reference measurement, and everywhere it is shown it has to say so.
/// </summary>
/// <remarks>
/// The probe against the local gateway exists to answer one question: whether latency the game sees
/// starts inside the flat or out on the path. It answers it only while the reader can tell the two
/// hosts apart — an unlabelled "RTT mot 192.168.1.1 nådde 41 ms" on the Network line reads as a
/// measurement of the connection to the server, and the gateway's RTT is frequently the worst of the
/// window, so it is the line that gets picked.
/// </remarks>
public sealed class ReferenceHostIsSaidApartTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 22, 29, 28, TimeSpan.Zero);

    private static readonly string BeyondGateway = ConnectionFaultReading.Describe(ConnectionFaultPlace.BeyondGateway);

    /// <summary>
    /// Both hosts reach the timeline, each under its own name, whichever of them was slowest.
    /// </summary>
    [Fact]
    public void TheTimelineNamesWhichHostEachRttBelongsTo()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        var network = analysis.TimelineHighlights
            .Where(item => item.Category == "Network")
            .Select(item => item.Summary)
            .ToArray();

        Assert.Equal(2, network.Length);

        var gateway = Assert.Single(network, item => item.Contains("192.168.1.1", StringComparison.Ordinal));
        Assert.Contains("gateway", gateway, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inte spelservern", gateway, StringComparison.Ordinal);

        var server = Assert.Single(network, item => item.Contains("cfx.example.net", StringComparison.Ordinal));
        Assert.Contains("spelservern", server, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway", server, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the export says it too, so the raw rows stay readable once the bundle has left the machine.
    /// </summary>
    /// <remarks>
    /// metrics.csv carried host, success, RTT and failure reason. Which of those hosts was the reference
    /// is not recoverable from a hostname by anyone who was not there, and the whole point of the export
    /// is that someone who was not there can read it — less still from an ordinary export, where the
    /// host is redacted and the two probes are told apart by their round trip times and nothing else.
    /// That is the export this reads.
    /// </remarks>
    [Fact]
    public async Task TheExportCarriesTheReferenceFlag()
    {
        var probes = (await ExportMetricsAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split(','))
            .Where(fields => fields.Length == 4 && fields[1] == "Probe")

            // The file is one key per row, so a probe is the group of rows sharing a timestamp.
            .GroupBy(fields => fields[0])
            .ToDictionary(
                group => group.Single(fields => fields[2] == "rttMs")[3],
                group => group.Single(fields => fields[2] == "isReferenceHost")[3]);

        Assert.Equal("True", probes["41.0"]);
        Assert.Equal("False", probes["28.0"]);
    }

    /// <summary>
    /// A window where most probes got through and some did not has to say the second part.
    /// </summary>
    /// <remarks>
    /// The line was picked by highest round trip time, and a failed probe has none, so it lost to every
    /// probe that answered. The timeline of a dropped connection then said "RTT nådde 1 ms".
    /// </remarks>
    [Fact]
    public void TheTimelineSaysAProbeFailedEvenIfOthersAnswered()
    {
        var markedAt = Start.AddSeconds(30);
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            new NetworkProbeSample(markedAt, "192.168.1.1", 1, Success: true, IsReferenceHost: true),
            new NetworkProbeSample(markedAt.AddSeconds(2), "192.168.1.1", null, Success: false, "TimedOut", IsReferenceHost: true),
            new NetworkProbeSample(markedAt.AddSeconds(4), "192.168.1.1", null, Success: false, "TimedOut", IsReferenceHost: true),
            new NetworkProbeSample(markedAt.AddSeconds(6), "192.168.1.1", 2, Success: true, IsReferenceHost: true)));

        var line = Assert.Single(analysis.TimelineHighlights, item => item.Category == "Network");

        Assert.Contains("2 av 4", line.Summary, StringComparison.Ordinal);
        Assert.Contains("TimedOut", line.Summary, StringComparison.Ordinal);
        Assert.Equal(markedAt.AddSeconds(2), line.Timestamp);

        // And the failure does not take the slowest answer off the line.
        Assert.Contains("toppade på 2 ms", line.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gateway up and the internet gone is the night this probe was added for.
    /// </summary>
    [Fact]
    public void AnInternetLossWithTheGatewayUpIsPlacedBeyondTheComputer()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(InternetAndGateway(internetFailures: 5, gatewayAnswers: true)));

        Assert.Contains("5 av 10 prober mot en extern adress", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains(BeyondGateway, analysis.Summary, StringComparison.Ordinal);

        // One line each on the timeline, and neither is taken for the other.
        var network = analysis.TimelineHighlights.Where(item => item.Category == "Network").Select(item => item.Summary).ToArray();
        Assert.Equal(2, network.Length);
        Assert.Single(network, item => item.Contains("1.1.1.1", StringComparison.Ordinal) && item.Contains("extern adress", StringComparison.Ordinal));
        Assert.Single(network, item => item.Contains("192.168.1.1", StringComparison.Ordinal) && item.Contains("gateway", StringComparison.Ordinal));

        var hypothesis = Assert.Single(analysis.Hypotheses, item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);
        Assert.Contains(hypothesis.Evidence, item => item.Contains(BeyondGateway, StringComparison.Ordinal));
    }

    [Fact]
    public void AnInternetLossWithTheGatewayDownIsPlacedOnThisSideOfTheRouter()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(InternetAndGateway(internetFailures: 5, gatewayAnswers: false)));

        Assert.Contains(ConnectionFaultReading.Describe(ConnectionFaultPlace.AtOrBeforeGateway), analysis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(BeyondGateway, analysis.Summary, StringComparison.Ordinal);

        // One event, counted once, even though both hosts saw it.
        var hypothesis = Assert.Single(analysis.Hypotheses, item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);
        Assert.Equal(0.2, hypothesis.Confidence, precision: 2);
    }

    /// <summary>
    /// Half a minute of nothing in a 90 second window is a lost connection, however calm the rest of the
    /// window was. Six seconds can be a full upload, and at full weight it took the verdict from a window of
    /// CPU-bound hitches. Polls here are two seconds apart.
    /// </summary>
    [Theory]
    [InlineData(3, 0.2)]
    [InlineData(10, 0.2)]
    [InlineData(11, 0.35)]
    [InlineData(15, 0.35)]
    public void ALostConnectionIsScoredOnItsLengthAndNotItsShareOfTheWindow(int polls, double expected)
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(InternetAndGateway(internetFailures: polls, gatewayAnswers: true, polls: 45)));

        var hypothesis = Assert.Single(analysis.Hypotheses, item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);
        Assert.Equal(expected, hypothesis.Confidence, precision: 2);
    }

    /// <summary>
    /// The gateway has a 300 ms timeout and loses the odd echo. One miss in 45 used to put "Network" at the
    /// top of a window with nothing else wrong and claim the whole connection had gone.
    /// </summary>
    [Fact]
    public void AStrayGatewayMissIsNotALostConnection()
    {
        var markedAt = Start.AddSeconds(30);
        var probes = Enumerable.Range(0, 45)
            .Select(i => new NetworkProbeSample(
                markedAt.AddSeconds(i - 30),
                "192.168.1.1",
                i == 20 ? null : 1,
                i != 20,
                i == 20 ? "TimedOut" : null,
                IsReferenceHost: true))
            .ToArray();

        var analysis = new FiveMCorrelationEngine().Analyze(Incident(probes));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);
    }

    /// <summary>
    /// With nothing beyond it to compare against, a gateway gone silent is still the whole connection.
    /// </summary>
    [Fact]
    public void ASilentGatewayWithNoInternetAddressIsALostConnection()
    {
        var probes = InternetAndGateway(internetFailures: 15, gatewayAnswers: false, polls: 20).Where(item => !item.IsInternetReference).ToArray();

        var hypothesis = Assert.Single(
            new FiveMCorrelationEngine().Analyze(Incident(probes)).Hypotheses,
            item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);

        Assert.Equal(0.35, hypothesis.Confidence, precision: 2);
        Assert.Contains(hypothesis.Evidence, item => item.Contains("hela anslutningen", StringComparison.Ordinal));
    }

    /// <summary>
    /// Three misses on a 300 ms timeout are not enough to claim the whole connection went, and that is the
    /// only network reading an install without an internet address has.
    /// </summary>
    [Fact]
    public void AShortGatewaySilenceIsEvidenceAndNotAVerdict()
    {
        var probes = InternetAndGateway(internetFailures: 3, gatewayAnswers: false).Where(item => !item.IsInternetReference).ToArray();

        var hypothesis = Assert.Single(
            new FiveMCorrelationEngine().Analyze(Incident(probes)).Hypotheses,
            item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);

        Assert.Equal(0.2, hypothesis.Confidence, precision: 2);
    }

    /// <summary>
    /// A gateway silent while the internet answers cannot be the connection going, because the connection
    /// demonstrably worked. It is an address the machine has moved away from, or one that stopped answering
    /// ICMP, and it used to score higher than a real outage.
    /// </summary>
    [Fact]
    public void ASilentGatewayWhileTheInternetAnswersIsNotALostConnection()
    {
        var markedAt = Start.AddSeconds(30);
        var probes = new[]
        {
            // Something real to keep the hypothesis alive, so its evidence can be read.
            new NetworkProbeSample(markedAt, "cfx.example.net", 180, Success: true),
        }
        .Concat(Enumerable.Range(0, 10).SelectMany(i => new[]
        {
            new NetworkProbeSample(markedAt.AddSeconds(i * 2), "192.168.1.1", null, Success: false, "TimedOut", IsReferenceHost: true),
            new NetworkProbeSample(markedAt.AddSeconds(i * 2), "1.1.1.1", 9, Success: true, IsReferenceHost: true, IsInternetReference: true),
        }))
        .ToArray();

        var analysis = new FiveMCorrelationEngine().Analyze(Incident(probes));

        var hypothesis = Assert.Single(analysis.Hypotheses, item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);
        Assert.Equal(0.3, hypothesis.Confidence, precision: 2);
        Assert.Contains(hypothesis.Evidence, item => item.Contains("säger ingenting om anslutningen", StringComparison.Ordinal));
        Assert.DoesNotContain(hypothesis.Evidence, item => item.Contains("hela anslutningen", StringComparison.Ordinal));

        // The summary gives the answer beside the gateway's silence, or the silence reads as a lost connection.
        Assert.Contains("Samtliga 10 prober mot nätets gateway", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("Alla 10 prober mot en extern adress", analysis.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A verdict quotes the first line of evidence as its reason. With the reference lines first, a window
    /// with the server at 180 ms was explained by "alla prober mot gatewayen svarade".
    /// </summary>
    [Fact]
    public void HealthyReferenceHostsComeAfterWhatWasActuallyMeasured()
    {
        var markedAt = Start.AddSeconds(30);
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(
            InternetAndGateway(internetFailures: 0, gatewayAnswers: true)
                .Append(new NetworkProbeSample(markedAt, "cfx.example.net", 180, Success: true))
                .ToArray()));

        var hypothesis = Assert.Single(analysis.Hypotheses, item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);

        Assert.StartsWith("RTT toppade", hypothesis.Evidence[0], StringComparison.Ordinal);
        Assert.Contains(hypothesis.Evidence, item => item.Contains("nätets egen gateway svarade", StringComparison.Ordinal));
        Assert.Contains(hypothesis.Evidence, item => item.Contains("extern adress svarade", StringComparison.Ordinal));
    }

    /// <summary>
    /// A missed echo or two on a path that long is ordinary. It is reported as a count, and neither placed
    /// nor scored.
    /// </summary>
    [Fact]
    public void AStrayInternetFailureIsCountedButNotReadAsAFault()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(InternetAndGateway(internetFailures: 2, gatewayAnswers: true)));

        Assert.Contains("2 av 10 prober mot en extern adress", analysis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(BeyondGateway, analysis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);
    }

    /// <summary>
    /// A healthy connection stays out of the summary, or it is two more sentences under every incident of
    /// the evening. The timeline still has it.
    /// </summary>
    [Fact]
    public void HealthyReferenceHostsStayOutOfTheSummary()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(InternetAndGateway(internetFailures: 0, gatewayAnswers: true)));

        Assert.DoesNotContain("extern adress", analysis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway", analysis.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(analysis.TimelineHighlights, item => item.Summary.Contains("1.1.1.1", StringComparison.Ordinal));
        Assert.Contains(analysis.TimelineHighlights, item => item.Summary.Contains("192.168.1.1", StringComparison.Ordinal));
    }

    /// <summary>
    /// Redacted, the two reference hosts are the same word, so the flag is all that tells them apart.
    /// </summary>
    [Fact]
    public async Task TheExportTellsTheInternetAddressFromTheGateway()
    {
        var markedAt = Start.AddSeconds(30);
        var probes = (await ExportMetricsAsync(Incident(
                new NetworkProbeSample(markedAt, "192.168.1.1", 1, Success: true, IsReferenceHost: true),
                new NetworkProbeSample(markedAt.AddSeconds(1), "1.1.1.1", 9, Success: true, IsReferenceHost: true, IsInternetReference: true))))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split(','))
            .Where(fields => fields.Length == 4 && fields[1] == "Probe")
            .GroupBy(fields => fields[0])
            .ToDictionary(
                group => group.Single(fields => fields[2] == "rttMs")[3],
                group => group.Single(fields => fields[2] == "isInternetReference")[3]);

        Assert.Equal("False", probes["1.0"]);
        Assert.Equal("True", probes["9.0"]);
    }

    /// <summary>
    /// Pairs of probes two seconds apart, the internet address failing the first of them in a row.
    /// </summary>
    private static NetworkProbeSample[] InternetAndGateway(int internetFailures, bool gatewayAnswers, int polls = 10)
    {
        var first = Start.AddSeconds(30 - polls);
        var probes = new List<NetworkProbeSample>();

        for (var i = 0; i < polls; i++)
        {
            var at = first.AddSeconds(i * 2);
            var gatewayUp = gatewayAnswers || i >= internetFailures;
            probes.Add(new NetworkProbeSample(
                at,
                "192.168.1.1",
                gatewayUp ? 1 : null,
                gatewayUp,
                gatewayUp ? null : "TimedOut",
                IsReferenceHost: true));

            var internetUp = i >= internetFailures;
            probes.Add(new NetworkProbeSample(
                at,
                "1.1.1.1",
                internetUp ? 9 : null,
                internetUp,
                internetUp ? null : "TimedOut",
                IsReferenceHost: true,
                IsInternetReference: true));
        }

        return probes.ToArray();
    }

    private static Task<string> ExportMetricsAsync() => ExportMetricsAsync(Incident());

    private static async Task<string> ExportMetricsAsync(IncidentRecord incident)
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
        var zipPath = await new IncidentBundleExporter().ExportAsync(
            incident,
            new ExportBundleOptions(outputDirectory, IncludeSensitiveFields: false, IncludeAttachedArtifacts: false),
            CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("metrics.csv")!.Open());

        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// A quiet window with two probes in it: the gateway answering slowest, which is the ordinary case
    /// on a machine whose router deprioritises ICMP.
    /// </summary>
    private static IncidentRecord Incident()
    {
        var markedAt = Start.AddSeconds(30);

        return Incident(
            new NetworkProbeSample(markedAt, "cfx.example.net", 28, Success: true),
            new NetworkProbeSample(markedAt.AddSeconds(1), "192.168.1.1", 41, Success: true, FailureReason: null, IsReferenceHost: true));
    }

    private static IncidentRecord Incident(params NetworkProbeSample[] probes)
    {
        var markedAt = Start.AddSeconds(30);
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.67),
                i == 300 ? 120 : 16.67,
                GpuBusyMs: 4.5,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 6.9,
                CpuWaitMs: 0.3));
        }

        events.AddRange(probes);

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Normal, "Auto: 120 ms frame"),
            Start,
            Start.AddSeconds(90),
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
                "Disabled",
                ObsDetectedAtStart: false,
                ServerProfileName: "Example",
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
