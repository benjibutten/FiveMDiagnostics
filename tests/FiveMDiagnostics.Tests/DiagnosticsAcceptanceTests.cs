using System.IO.Compression;

namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Export;
using FiveMDiagnostics.Fakes;

public sealed class DiagnosticsAcceptanceTests
{
    private readonly FiveMCorrelationEngine _engine = new();

    [Fact]
    public void IncidentMaterializer_CapturesThirtySecondsBeforeAndSixtySecondsAfterMarker()
    {
        var ringBuffer = new TimeWindowRingBuffer<TelemetryEvent>(TimeSpan.FromMinutes(3), item => item.Timestamp);
        var baseTime = new DateTimeOffset(2026, 4, 8, 20, 0, 0, TimeSpan.Zero);
        var environment = CreateEnvironment(baseTime);
        var materializer = new IncidentMaterializer(ringBuffer, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

        for (var second = -35; second <= 0; second++)
        {
            var sample = new FrameTelemetrySample(baseTime.AddSeconds(second), 16.6, 8, 5, 16.6, false, "FiveM");
            ringBuffer.Add(sample);
        }

        var marker = materializer.MarkIncident(baseTime, IncidentSeverity.Normal);
        var completedIncidents = new List<IncidentRecord>();

        for (var second = 1; second <= 60; second++)
        {
            var sample = new FrameTelemetrySample(baseTime.AddSeconds(second), second == 5 ? 42 : 16.6, 8, 5, 16.6, false, "FiveM");
            ringBuffer.Add(sample);
            completedIncidents.AddRange(materializer.OnTelemetry(sample, environment, []));
        }

        completedIncidents.AddRange(materializer.FinalizeDue(baseTime.AddSeconds(61), environment, []));
        var incidents = completedIncidents;
        var incident = Assert.Single(incidents);

        Assert.Equal(marker.Id, incident.Id);
        Assert.Equal(baseTime.AddSeconds(-30), incident.WindowStart);
        Assert.Equal(baseTime.AddSeconds(60), incident.WindowEnd);
        Assert.Contains(incident.Events, item => item.Timestamp == baseTime.AddSeconds(-30));
        Assert.Contains(incident.Events, item => item.Timestamp == baseTime.AddSeconds(60));
        Assert.DoesNotContain(incident.Events, item => item.Timestamp == baseTime.AddSeconds(-35));
    }

    [Fact]
    public void CorrelationEngine_ClassifiesObsGpuScenario()
    {
        var scenario = FakeScenarioGenerator.Create(FakeScenarioKind.ObsGpuContention);
        var analysis = _engine.Analyze(scenario.ToIncidentRecord());

        Assert.Equal(RootCauseCategory.ObsRenderOutputContention, analysis.Hypotheses[0].Category);
        Assert.True(analysis.Hypotheses[0].Confidence >= 0.6);
    }

    [Fact]
    public void CorrelationEngine_ClassifiesFiveMResourceScenario()
    {
        var scenario = FakeScenarioGenerator.Create(FakeScenarioKind.FiveMResourceSpike);
        var analysis = _engine.Analyze(scenario.ToIncidentRecord());

        Assert.Equal(RootCauseCategory.FiveMResourceSpike, analysis.Hypotheses[0].Category);
        Assert.True(analysis.Hypotheses[0].Confidence >= 0.6);
    }

    [Fact]
    public async Task Exporter_CreatesIncidentBundleZip()
    {
        var scenario = FakeScenarioGenerator.Create(FakeScenarioKind.NetworkIssue);
        var incident = scenario.ToIncidentRecord() with { Analysis = _engine.Analyze(scenario.ToIncidentRecord()) };
        var exporter = new IncidentBundleExporter();
        var outputDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));

        var zipPath = await exporter.ExportAsync(incident, new ExportBundleOptions(outputDirectory, IncludeSensitiveFields: false, IncludeAttachedArtifacts: false), CancellationToken.None);

        Assert.True(File.Exists(zipPath));

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains(zip.Entries, entry => entry.FullName == "summary.json");
        Assert.Contains(zip.Entries, entry => entry.FullName == "metrics.csv");
        Assert.Contains(zip.Entries, entry => entry.FullName == "incident-report.txt");
    }

    private static EnvironmentMetadata CreateEnvironment(DateTimeOffset baseTime)
    {
        return new EnvironmentMetadata(
            "Windows 11",
            "AMD Ryzen 7",
            32UL * 1024 * 1024 * 1024,
            "RTX 4070",
            "555.12",
            165,
            "Enabled",
            true,
            "The Path",
            baseTime.AddSeconds(-30),
            baseTime.AddSeconds(60));
    }
}