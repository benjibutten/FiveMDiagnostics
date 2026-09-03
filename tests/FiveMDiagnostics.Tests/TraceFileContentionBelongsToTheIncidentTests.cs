namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The file system contention a trace measured is evidence about the seconds that trace recorded.
/// </summary>
/// <remarks>
/// <para>
/// Same reasoning as <see cref="TracePressureBelongsToTheIncidentTests"/>, and the same omission: the
/// operation rate was read out of whichever attached trace showed the most of it, across the whole
/// attachment list, with nothing checking that the file had been recording while the incident happened.
/// </para>
/// <para>
/// It is worth 0.3 on its own and it is the only storage signal that fires when latency, queue depth
/// and megabytes are all unremarkable — so on an evening that writes a capture a minute, an indexer
/// running through an unrelated trace was enough to hand the incident in front of the reader to
/// <see cref="RootCauseCategory.StreamingOrDiskStall"/>.
/// </para>
/// </remarks>
public sealed class TraceFileContentionBelongsToTheIncidentTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 21, 14, 3, TimeSpan.Zero);

    /// <summary>The trace was recording while it happened, so 48 000 operations a second counts.</summary>
    [Fact]
    public void ATraceThatCoveredTheWindowClassifiesTheIncident()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(coveredFrom: Start));

        var disk = Assert.Single(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        Assert.Contains(disk.Evidence, item => item.Contains("filsystemsoperationer", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same file and the same rate, recorded five minutes before the incident opened. Windows
    /// Search indexing then says nothing about the frames under examination now.
    /// </summary>
    [Fact]
    public void ATraceFromAnotherWindowDoesNotClassifyThisOne()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(coveredFrom: Start.AddMinutes(-5)));

        Assert.DoesNotContain(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.StreamingOrDiskStall);
    }

    /// <summary>
    /// A trace that never said what it covers is kept, exactly as it is for the video memory figure.
    /// The attachment was still captured for this marker; silent is not the same as elsewhere.
    /// </summary>
    [Fact]
    public void ATraceThatNamesNoWindowIsStillEvidence()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(coveredFrom: null));

        Assert.Contains(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.StreamingOrDiskStall);
    }

    /// <summary>
    /// A burst that happened while the frames were being lost, which is what the rate was always meant
    /// to mean.
    /// </summary>
    [Fact]
    public void AContendingBurstInsideTheWindowClassifiesTheIncident()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(
            Incident(coveredFrom: Start.AddMinutes(-5), contendingFrom: Start.AddSeconds(25), coveredSeconds: 400));

        var disk = Assert.Single(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        Assert.Contains(disk.Evidence, item => item.Contains("under incidentfönstret", StringComparison.Ordinal));
    }

    /// <summary>
    /// The case the overlap check cannot see. A ring buffer holds tens of seconds and an incident window
    /// is ninety, so a capture written for an earlier hitch overlaps this one almost by construction —
    /// and the indexer that ran through its first ten seconds had stopped long before these frames.
    /// </summary>
    [Fact]
    public void AContendingBurstOutsideTheWindowDoesNotClassifyIt()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(
            Incident(coveredFrom: Start.AddMinutes(-5), contendingFrom: Start.AddMinutes(-4), coveredSeconds: 400));

        Assert.DoesNotContain(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.StreamingOrDiskStall);
    }

    /// <summary>
    /// A trace that timed the traffic and found no contending second at all says nothing, whatever its
    /// average over the whole file works out to.
    /// </summary>
    [Fact]
    public void ATraceThatTimedTheTrafficAndFoundNoneIsNotEvidence()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(
            Incident(coveredFrom: Start, contendingFrom: null, timedTheTraffic: true));

        Assert.DoesNotContain(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.StreamingOrDiskStall);
    }

    /// <summary>
    /// A quiet timeline whose only storage signal is the trace: no competing throughput, no latency,
    /// no queue, so the covered span is the one variable that decides the hypothesis.
    /// </summary>
    /// <param name="contendingFrom">
    /// When the neighbour's burst started, for a trace that resolved the traffic in time. Null leaves the
    /// trace in the older shape, which carries an average over everything it recorded and nothing about
    /// when any of it happened.
    /// </param>
    /// <param name="timedTheTraffic">
    /// True with no burst: the parser looked, and the neighbour never held a contending second.
    /// </param>
    private static IncidentRecord Incident(
        DateTimeOffset? coveredFrom,
        DateTimeOffset? contendingFrom = null,
        bool timedTheTraffic = false,
        double coveredSeconds = 40)
    {
        var markedAt = Start.AddSeconds(30);
        var windowEnd = Start.AddSeconds(90);
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.67),
                16.67,
                GpuBusyMs: 4.5,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 6.9,
                CpuWaitMs: 0.3));
        }

        var metrics = new Dictionary<string, double>
        {
            ["fileOperationsNeighbourPerSecond"] = 48_000,
            ["fileOperationsNeighbourContending"] = 1,
        };

        if (coveredFrom is { } from)
        {
            metrics["traceCoveredStartUnixMs"] = from.ToUnixTimeMilliseconds();
            metrics["traceCoveredEndUnixMs"] = from.AddSeconds(coveredSeconds).ToUnixTimeMilliseconds();
        }

        if (contendingFrom is { } burst)
        {
            // One five second stretch, every second of it over the parser's bar.
            metrics["fileOperationsNeighbourIntervalCount"] = 1;
            metrics["fileOperationsNeighbourInterval0StartUnixMs"] = burst.ToUnixTimeMilliseconds();
            metrics["fileOperationsNeighbourInterval0EndUnixMs"] = burst.AddSeconds(5).ToUnixTimeMilliseconds();
            metrics["fileOperationsNeighbourInterval0PerSecond"] = 62_759;
        }
        else if (timedTheTraffic)
        {
            metrics["fileOperationsNeighbourIntervalCount"] = 0;
        }

        events.Add(new ArtifactEvidence(
            markedAt,
            ArtifactKind.EtlTrace,
            "Deep capture: 48 000 filoperationer i sekunden fran SearchIndexer.exe.",
            metrics,
            "capture.etl"));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Normal, "Auto: 190 ms frame"),
            Start,
            windowEnd,
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
                "Disabled",
                ObsDetectedAtStart: false,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
