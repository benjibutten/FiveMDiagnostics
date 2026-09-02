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

    private static async Task<string> ExportMetricsAsync()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
        var zipPath = await new IncidentBundleExporter().ExportAsync(
            Incident(),
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

        events.Add(new NetworkProbeSample(markedAt, "cfx.example.net", 28, Success: true));
        events.Add(new NetworkProbeSample(markedAt.AddSeconds(1), "192.168.1.1", 41, Success: true, FailureReason: null, IsReferenceHost: true));

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
