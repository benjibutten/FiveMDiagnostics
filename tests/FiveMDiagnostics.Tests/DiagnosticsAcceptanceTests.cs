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
    public void CorrelationEngine_ClassifiesVramPressureScenario()
    {
        var scenario = FakeScenarioGenerator.Create(FakeScenarioKind.GpuVramPressure);
        var analysis = _engine.Analyze(scenario.ToIncidentRecord());

        Assert.Equal(RootCauseCategory.GpuVramPressure, analysis.Hypotheses[0].Category);
        Assert.True(analysis.Hypotheses[0].Confidence >= 0.6);
    }

    [Fact]
    public void CorrelationEngine_AlwaysReturnsInsufficientEvidenceWithoutFrames()
    {
        var baseTime = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero);
        var discord = new[]
        {
            new ProcessActivity("Discord", 101, 0, 0),
            new ProcessActivity("DiscordCanary", 102, 0, 0),
        };
        var events = new TelemetryEvent[]
        {
            new SystemTelemetrySample(baseTime, 15, new Dictionary<string, double> { ["0"] = 20 }, 40, 16_000, discord, []),
        };

        var analysis = _engine.Analyze(BuildIncident(baseTime, events, 60));

        Assert.True(analysis.InsufficientEvidence);
        Assert.Equal(RootCauseCategory.InsufficientEvidence, Assert.Single(analysis.Hypotheses).Category);
        Assert.Empty(analysis.SuspectedProcesses);
    }

    [Fact]
    public void CorrelationEngine_UsesStrongIoFallbackWhenDiskCountersAreUnavailable()
    {
        var baseTime = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero);
        var events = new List<TelemetryEvent>();
        for (var index = 0; index < 60; index++)
        {
            events.Add(new FrameTelemetrySample(
                baseTime.AddMilliseconds(index * 16.6),
                index == 40 ? 100 : 16.6,
                8,
                5,
                index == 40 ? 100 : 16.6,
                false,
                "FiveM",
                CpuBusyMs: 7));
        }

        events.Add(new ProcessTelemetrySample(baseTime, 42, "FiveM", 30, 1, 1, 20, 60 * 1024 * 1024, 0));
        events.Add(new SystemTelemetrySample(
            baseTime,
            40,
            new Dictionary<string, double> { ["0"] = 50 },
            50,
            8_000,
            [],
            [new ProcessActivity("Indexer", 84, 5, 30 * 1024 * 1024)]));

        var analysis = _engine.Analyze(BuildIncident(baseTime, events, 60));
        var disk = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        Assert.NotNull(disk);
        Assert.Contains(disk.Evidence, item => item.Contains("counters saknades", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The fixed 25 ms spike threshold could never fire on a high-refresh display: a game targeting
    /// 8.3 ms that hits 20 ms is visibly hitching but stayed invisible to the engine.
    /// </summary>
    [Fact]
    public void CorrelationEngine_DetectsSpikesRelativeToRefreshRate()
    {
        var baseTime = new DateTimeOffset(2026, 8, 17, 20, 0, 0, TimeSpan.Zero);
        var events = new List<TelemetryEvent>();

        for (var offset = 0; offset < 120; offset++)
        {
            // Steady 120 fps with a handful of 21 ms frames, all well under the old 25 ms threshold.
            var frameTime = offset is 40 or 41 or 42 or 43 ? 21 : 8.3;
            events.Add(new FrameTelemetrySample(
                baseTime.AddMilliseconds(offset * 8.3),
                frameTime,
                GpuBusyMs: frameTime is 21 ? 18.5 : 7.2,
                DisplayLatencyMs: 9,
                MsBetweenPresents: frameTime,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess",
                CpuBusyMs: frameTime is 21 ? 3.1 : 6.4));
        }

        var incident = new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), baseTime, IncidentSeverity.Normal, "Stutter"),
            baseTime.AddSeconds(-30),
            baseTime.AddSeconds(60),
            CreateEnvironment(baseTime) with { DisplayRefreshRateHz = 120 },
            events,
            Analysis: null,
            Attachments: []);

        var analysis = _engine.Analyze(incident);
        var gpu = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.GpuFrametimeContention);

        Assert.NotNull(gpu);
        Assert.Contains(gpu.Evidence, evidence => evidence.Contains("GPU-bundna", StringComparison.Ordinal));
    }

    /// <summary>
    /// The redaction default exists so a bundle can be shared. Structured fields alone are not enough:
    /// the analysis prose is generated from those fields and repeats the address verbatim.
    /// </summary>
    [Fact]
    public async Task Exporter_RedactsServerAddressFromEveryPartOfBundle()
    {
        const string serverIp = "203.0.113.14";
        var scenario = FakeScenarioGenerator.Create(FakeScenarioKind.NetworkIssue);
        var record = scenario.ToIncidentRecord();
        var incident = record with { Analysis = _engine.Analyze(record) };

        // Guard: the unredacted analysis must actually contain the address, or this proves nothing.
        Assert.Contains(serverIp, string.Join('\n', incident.Analysis!.TimelineHighlights.Select(item => item.Summary)), StringComparison.Ordinal);

        var outputDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
        var zipPath = await new IncidentBundleExporter().ExportAsync(
            incident,
            new ExportBundleOptions(outputDirectory, IncludeSensitiveFields: false, IncludeAttachedArtifacts: false),
            CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var content = await reader.ReadToEndAsync();
            Assert.DoesNotContain(serverIp, content, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// PresentMon v1 reports GPU time but no CPU time. Treating that as a full breakdown turned every
    /// spike into a GPU-bound or present-bound verdict the data cannot support.
    /// </summary>
    [Fact]
    public void CorrelationEngine_DoesNotAttributeSpikes_WhenCpuBreakdownIsMissing()
    {
        var baseTime = new DateTimeOffset(2026, 8, 17, 20, 0, 0, TimeSpan.Zero);
        var events = new List<TelemetryEvent>();

        for (var offset = 0; offset < 120; offset++)
        {
            var frameTime = offset is >= 40 and <= 45 ? 60.0 : 8.3;
            events.Add(new FrameTelemetrySample(
                baseTime.AddMilliseconds(offset * 8.3),
                frameTime,
                GpuBusyMs: 1.2,
                DisplayLatencyMs: 9,
                MsBetweenPresents: frameTime,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess",
                CpuBusyMs: null));
        }

        // High VRAM plus unattributed spikes must not be enough to blame VRAM.
        for (var offset = 0; offset < 10; offset++)
        {
            events.Add(new GpuTelemetrySample(
                baseTime.AddSeconds(offset),
                IsAvailable: true,
                "NVIDIA GeForce RTX 3080",
                UtilizationPercent: 55,
                MemoryBandwidthUtilizationPercent: 60,
                UsedVramBytes: (ulong)(10UL * 1024 * 1024 * 1024 * 0.97),
                TotalVramBytes: 10UL * 1024 * 1024 * 1024,
                EncoderUtilizationPercent: 30,
                DecoderUtilizationPercent: 0,
                TemperatureCelsius: 70,
                ThrottleReasons: []));
        }

        var incident = BuildIncident(baseTime, events, 120);
        var analysis = _engine.Analyze(incident);

        var vram = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.GpuVramPressure);
        Assert.True(
            vram is null || vram.Evidence.Any(text => text.Contains("--v2_metrics", StringComparison.Ordinal)),
            "VRAM pressure must not claim present-bound attribution without CPU data.");

        var gpu = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.GpuFrametimeContention);
        Assert.True(
            gpu is null || gpu.Evidence.All(text => !text.Contains("GPU-bundna", StringComparison.Ordinal)),
            "Spikes must not be reported as GPU-bound without a CPU figure to compare against.");
    }

    /// <summary>
    /// Importing net_statsFile is the documented way to strengthen network evidence, so its metrics have
    /// to actually reach the network hypothesis.
    /// </summary>
    [Fact]
    public void CorrelationEngine_UsesNetStatsMetricsInNetworkHypothesis()
    {
        var baseTime = new DateTimeOffset(2026, 8, 17, 20, 0, 0, TimeSpan.Zero);
        var events = new List<TelemetryEvent>
        {
            new ArtifactEvidence(
                baseTime,
                ArtifactKind.NetStatsCsv,
                "net_statsFile visade ping 140 ms, jitter 55 ms och packet loss 4.2%",
                new Dictionary<string, double>
                {
                    ["avgPingMs"] = 140,
                    ["max_avgPingMs"] = 190,
                    ["avgJitterMs"] = 55,
                    ["avgPacketLossPercent"] = 4.2,
                }),
        };

        for (var offset = 0; offset < 60; offset++)
        {
            events.Add(new FrameTelemetrySample(
                baseTime.AddMilliseconds(offset * 8.3),
                8.3,
                GpuBusyMs: 4,
                DisplayLatencyMs: 9,
                MsBetweenPresents: 8.3,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess",
                CpuBusyMs: 5));
        }

        var analysis = _engine.Analyze(BuildIncident(baseTime, events, 120));
        var network = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.NetworkJitterOrPacketLoss);

        Assert.NotNull(network);
        Assert.Contains(network.Evidence, text => text.Contains("packet loss", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(network.Evidence, text => text.Contains("jitter", StringComparison.OrdinalIgnoreCase));
        Assert.True(network.Confidence >= 0.6);
    }

    private IncidentRecord BuildIncident(DateTimeOffset baseTime, IReadOnlyList<TelemetryEvent> events, double refreshRateHz)
    {
        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), baseTime, IncidentSeverity.Normal, "Stutter"),
            baseTime.AddSeconds(-30),
            baseTime.AddSeconds(60),
            CreateEnvironment(baseTime) with { DisplayRefreshRateHz = refreshRateHz },
            events,
            Analysis: null,
            Attachments: []);
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

    [Fact]
    public async Task Exporter_IncludesPerCoreDiskObsAndCaptureHealth()
    {
        var baseTime = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero);
        var events = new List<TelemetryEvent>();
        for (var second = -30; second <= 60; second++)
        {
            events.Add(new FrameTelemetrySample(baseTime.AddSeconds(second), 16.6, 8, 5, 16.6, false, "FiveM", CpuBusyMs: 7));
        }

        events.Add(new SystemTelemetrySample(baseTime, 50, new Dictionary<string, double> { ["0"] = 99 }, 55, 8_000, [], [], 24, 3, 120));
        events.Add(new ObsTelemetrySample(baseTime, true, 60, 2, 0, 0, 2, 500, true, false, true));
        events.Add(new CaptureHealthTelemetrySample(baseTime, 91, baseTime.AddSeconds(-30), baseTime.AddSeconds(60), 1, 90, 1, true));
        var incident = BuildIncident(baseTime, events, 60);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
        var zipPath = await new IncidentBundleExporter().ExportAsync(incident, new ExportBundleOptions(outputDirectory, false, false), CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        using var metricsReader = new StreamReader(zip.GetEntry("metrics.csv")!.Open());
        var metrics = await metricsReader.ReadToEndAsync();
        using var reportReader = new StreamReader(zip.GetEntry("incident-report.txt")!.Open());
        var report = await reportReader.ReadToEndAsync();

        Assert.Contains("cpuCore.0.usagePercent", metrics, StringComparison.Ordinal);
        Assert.Contains("diskAverageLatencyMs", metrics, StringComparison.Ordinal);
        Assert.Contains("isProcessRunning", metrics, StringComparison.Ordinal);
        Assert.Contains("isWebSocketConnected", metrics, StringComparison.Ordinal);
        Assert.Contains("Window coverage: pre-buffer complete, post-window complete, full window yes", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporter_DoesNotCallSparseBoundaryFramesFullCoverageOrUseSessionGapTotals()
    {
        var baseTime = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero);
        var events = new TelemetryEvent[]
        {
            new FrameTelemetrySample(baseTime.AddSeconds(-30), 16.6, 8, 5, 16.6, false, "FiveM", CpuBusyMs: 7),
            new FrameTelemetrySample(baseTime.AddSeconds(60), 16.6, 8, 5, 16.6, false, "FiveM", CpuBusyMs: 7),
            new CaptureHealthTelemetrySample(baseTime, 5000, baseTime.AddHours(-1), baseTime, 600, 0, 4, true, 12),
        };
        var incident = BuildIncident(baseTime, events, 60);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
        var zipPath = await new IncidentBundleExporter().ExportAsync(incident, new ExportBundleOptions(outputDirectory, false, false), CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("incident-report.txt")!.Open());
        var report = await reader.ReadToEndAsync();

        Assert.Contains("full window no", report, StringComparison.Ordinal);
        Assert.Contains("incident gaps 1, largest incident gap 90.00 s", report, StringComparison.Ordinal);
        Assert.DoesNotContain("incident gaps 12", report, StringComparison.Ordinal);
        Assert.DoesNotContain("largest incident gap 600", report, StringComparison.Ordinal);
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
            "Example Server",
            baseTime.AddSeconds(-30),
            baseTime.AddSeconds(60));
    }
}
