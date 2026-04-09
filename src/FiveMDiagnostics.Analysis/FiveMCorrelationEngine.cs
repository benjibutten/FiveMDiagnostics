using System.Globalization;
using System.Text.Json;

namespace FiveMDiagnostics.Analysis;

using FiveMDiagnostics.Core;

public sealed class FiveMCorrelationEngine : IAnalysisEngine
{
    public IncidentAnalysis Analyze(IncidentRecord incident)
    {
        var frameSamples = incident.GetEvents<FrameTelemetrySample>();
        var systemSamples = incident.GetEvents<SystemTelemetrySample>();
        var processSamples = incident.GetEvents<ProcessTelemetrySample>();
        var obsSamples = incident.GetEvents<ObsTelemetrySample>();
        var networkProbes = incident.GetEvents<NetworkProbeSample>();
        var networkEndpoints = incident.GetEvents<NetworkEndpointSample>();
        var artifacts = incident.GetEvents<ArtifactEvidence>();

        var metrics = BuildFrameMetrics(frameSamples);
        var hypotheses = new List<HypothesisScore>();

        AddObsHypothesis(hypotheses, metrics, obsSamples);
        AddGpuHypothesis(hypotheses, metrics, obsSamples, systemSamples);
        AddResourceHypothesis(hypotheses, metrics, processSamples, artifacts, obsSamples, systemSamples);
        AddNetworkHypothesis(hypotheses, metrics, networkProbes, networkEndpoints, systemSamples, obsSamples);
        AddDiskHypothesis(hypotheses, metrics, processSamples, systemSamples, artifacts);
        AddExternalProcessHypothesis(hypotheses, systemSamples);
        AddOsLatencyHypothesis(hypotheses, metrics, artifacts, systemSamples, obsSamples);
        AddCorruptionHypothesis(hypotheses, artifacts);

        hypotheses = hypotheses
            .OrderByDescending(item => item.Confidence)
            .ToList();

        if (hypotheses.Count == 0 || hypotheses[0].Confidence < 0.35)
        {
            hypotheses.Insert(0, new HypothesisScore(
                RootCauseCategory.InsufficientEvidence,
                0.2,
                ["Det fanns inte tillräckligt med samstämmig telemetry för en säker klassificering."]));
        }

        var highlights = BuildHighlights(incident, metrics, hypotheses.First(), artifacts, obsSamples, networkProbes);
        var top = hypotheses[0];
        var summary = BuildSummary(top, metrics, obsSamples, artifacts, networkProbes);

        return new IncidentAnalysis(
            hypotheses,
            top.Category == RootCauseCategory.InsufficientEvidence,
            summary,
            highlights);
    }

    private static FrameMetrics BuildFrameMetrics(IReadOnlyList<FrameTelemetrySample> frameSamples)
    {
        if (frameSamples.Count == 0)
        {
            return new FrameMetrics(0, 0, 0, 0, 0, 0);
        }

        var sorted = frameSamples.Select(item => item.FrameTimeMs).OrderBy(value => value).ToArray();
        var p95Index = Math.Clamp((int)Math.Floor(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);
        var p99Index = Math.Clamp((int)Math.Floor(sorted.Length * 0.99) - 1, 0, sorted.Length - 1);
        var spikeCount = frameSamples.Count(item => item.FrameTimeMs >= 25);
        var severeCount = frameSamples.Count(item => item.FrameTimeMs >= 40);
        var longestSpike = frameSamples.Where(item => item.FrameTimeMs >= 25).Select(item => item.FrameTimeMs).DefaultIfEmpty().Max();

        return new FrameMetrics(
            frameSamples.Average(item => item.FrameTimeMs),
            sorted[p95Index],
            sorted[p99Index],
            spikeCount,
            severeCount,
            longestSpike);
    }

    private static void AddObsHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, IReadOnlyList<ObsTelemetrySample> obsSamples)
    {
        if (obsSamples.Count == 0)
        {
            return;
        }

        var connectedSamples = obsSamples.Where(item => item.IsConnected).ToArray();
        if (connectedSamples.Length == 0)
        {
            return;
        }

        var evidence = new List<string>();
        double confidence = 0;

        var skippedRender = Delta(connectedSamples.Select(item => item.RenderSkippedFrames));
        var skippedOutput = Delta(connectedSamples.Select(item => item.OutputSkippedFrames));
        var maxRenderTime = connectedSamples.Select(item => item.AverageFrameRenderTimeMs ?? 0).DefaultIfEmpty().Max();

        if (skippedRender > 0)
        {
            confidence += 0.35;
            evidence.Add($"OBS render skipped frames ökade med {skippedRender} under incidentfönstret.");
        }

        if (skippedOutput > 0)
        {
            confidence += 0.25;
            evidence.Add($"OBS output skipped frames ökade med {skippedOutput} under incidentfönstret.");
        }

        if (maxRenderTime >= 18)
        {
            confidence += 0.2;
            evidence.Add($"OBS average frame render time toppade på {maxRenderTime:F1} ms.");
        }

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.15;
            evidence.Add($"Frametime hade {metrics.SevereSpikeCount} spikes över 40 ms samtidigt som OBS var aktivt.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.ObsRenderOutputContention, Math.Min(confidence, 0.97), evidence));
        }
    }

    private static void AddGpuHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, IReadOnlyList<ObsTelemetrySample> obsSamples, IReadOnlyList<SystemTelemetrySample> systemSamples)
    {
        var evidence = new List<string>();
        double confidence = 0;

        if (metrics.P99FrameTime >= 30)
        {
            confidence += 0.25;
            evidence.Add($"P99 frametime låg på {metrics.P99FrameTime:F1} ms.");
        }

        if (metrics.LongestSpikeMs >= 45)
        {
            confidence += 0.2;
            evidence.Add($"Längsta frametime-spiken nådde {metrics.LongestSpikeMs:F1} ms.");
        }

        if (systemSamples.Any(item => item.TotalCpuUsagePercent < 85) && obsSamples.All(item => !item.IsConnected))
        {
            confidence += 0.15;
            evidence.Add("Lokalt CPU-tryck såg inte extremt ut och OBS var inte en medverkande faktor.");
        }

        if (metrics.SpikeCount >= 4)
        {
            confidence += 0.2;
            evidence.Add($"Det fanns {metrics.SpikeCount} frametime-spikes över 25 ms i fönstret.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.GpuFrametimeContention, Math.Min(confidence, 0.85), evidence));
        }
    }

    private static void AddResourceHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, IReadOnlyList<ProcessTelemetrySample> processSamples, IReadOnlyList<ArtifactEvidence> artifacts, IReadOnlyList<ObsTelemetrySample> obsSamples, IReadOnlyList<SystemTelemetrySample> systemSamples)
    {
        var evidence = new List<string>();
        double confidence = 0;

        var profilerEvidence = artifacts.Where(item => item.Kind is ArtifactKind.ProfilerJson or ArtifactKind.ResmonSnapshot).ToArray();
        if (profilerEvidence.Length > 0)
        {
            confidence += 0.45;
            evidence.AddRange(profilerEvidence.Select(item => item.Summary));
        }

        var maxFiveMCpu = processSamples.Select(item => item.CpuUsagePercent).DefaultIfEmpty().Max();
        if (maxFiveMCpu >= 55)
        {
            confidence += 0.2;
            evidence.Add($"FiveM-processen toppade på {maxFiveMCpu:F0}% CPU under incidenten.");
        }

        if (obsSamples.All(item => !item.IsConnected) && systemSamples.Any(item => item.TotalCpuUsagePercent < 80))
        {
            confidence += 0.15;
            evidence.Add("OBS var inte aktivt och systemet i stort såg relativt stabilt ut, vilket talar för FiveM/resource-sida.");
        }

        if (metrics.SpikeCount > 0)
        {
            confidence += 0.1;
            evidence.Add($"Frametime-problemet syns tydligt lokalt med {metrics.SpikeCount} spikes.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.FiveMResourceSpike, Math.Min(confidence, 0.98), evidence));
        }
    }

    private static void AddNetworkHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, IReadOnlyList<NetworkProbeSample> probes, IReadOnlyList<NetworkEndpointSample> endpoints, IReadOnlyList<SystemTelemetrySample> systemSamples, IReadOnlyList<ObsTelemetrySample> obsSamples)
    {
        var evidence = new List<string>();
        double confidence = 0;

        var successfulProbes = probes.Where(item => item.Success && item.RoundTripTimeMs is not null).ToArray();
        var failedProbes = probes.Count(item => !item.Success);
        var maxRtt = successfulProbes.Select(item => item.RoundTripTimeMs ?? 0).DefaultIfEmpty().Max();
        var avgRtt = successfulProbes.Select(item => item.RoundTripTimeMs ?? 0).DefaultIfEmpty().Average();

        if (maxRtt >= 120)
        {
            confidence += 0.3;
            evidence.Add($"RTT toppade på {maxRtt:F0} ms.");
        }

        if (successfulProbes.Length >= 3 && maxRtt - avgRtt >= 40)
        {
            confidence += 0.2;
            evidence.Add($"RTT-jitter på minst {maxRtt - avgRtt:F0} ms observerades.");
        }

        if (failedProbes > 0)
        {
            confidence += 0.2;
            evidence.Add($"{failedProbes} probe-förfrågningar misslyckades under incidenten.");
        }

        if (endpoints.Any(item => item.RemoteEndpoints.Count > 0))
        {
            confidence += 0.05;
            evidence.Add("Aktiva remote endpoints fanns under incidenten.");
        }

        if (metrics.P95FrameTime < 28 && systemSamples.Any(item => item.TotalCpuUsagePercent < 75) && obsSamples.All(item => !item.IsConnected))
        {
            confidence += 0.2;
            evidence.Add("Lokal maskin såg stabil ut trots försämrat nätbeteende.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.NetworkJitterOrPacketLoss, Math.Min(confidence, 0.9), evidence));
        }
    }

    private static void AddDiskHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, IReadOnlyList<ProcessTelemetrySample> processSamples, IReadOnlyList<SystemTelemetrySample> systemSamples, IReadOnlyList<ArtifactEvidence> artifacts)
    {
        var evidence = new List<string>();
        double confidence = 0;

        var maxRead = processSamples.Select(item => item.ReadBytesPerSecond).DefaultIfEmpty().Max();
        var competingIo = systemSamples.SelectMany(item => item.TopDiskProcesses).Where(item => !item.ProcessName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)).ToArray();
        var maxCompetingIo = competingIo.Select(item => item.IoBytesPerSecond).DefaultIfEmpty().Max();
        var streamingHints = artifacts.Where(item => item.Summary.Contains("stream", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (maxRead >= 50 * 1024 * 1024)
        {
            confidence += 0.25;
            evidence.Add($"FiveM läste upp till {ToMegabytes(maxRead):F1} MB/s.");
        }

        if (maxCompetingIo >= 20 * 1024 * 1024)
        {
            confidence += 0.25;
            evidence.Add($"En konkurrerande process låg på {ToMegabytes(maxCompetingIo):F1} MB/s disk-I/O.");
        }

        if (streamingHints.Length > 0)
        {
            confidence += 0.2;
            evidence.AddRange(streamingHints.Select(item => item.Summary));
        }

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.1;
            evidence.Add("Frametime-spikes sammanföll med disk- eller streaming-signaler.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.StreamingOrDiskStall, Math.Min(confidence, 0.88), evidence));
        }
    }

    private static void AddExternalProcessHypothesis(List<HypothesisScore> hypotheses, IReadOnlyList<SystemTelemetrySample> systemSamples)
    {
        var offenders = systemSamples
            .SelectMany(item => item.TopCpuProcesses.Concat(item.TopDiskProcesses))
            .Where(item =>
                !item.ProcessName.Contains("FiveM", StringComparison.OrdinalIgnoreCase) &&
                !item.ProcessName.Contains("obs", StringComparison.OrdinalIgnoreCase) &&
                (item.CpuPercent >= 20 || item.IoBytesPerSecond >= 20 * 1024 * 1024))
            .GroupBy(item => item.ProcessName)
            .OrderByDescending(group => group.Max(entry => Math.Max(entry.CpuPercent, ToMegabytes(entry.IoBytesPerSecond))))
            .ToArray();

        if (offenders.Length == 0)
        {
            return;
        }

        var evidence = offenders
            .Take(3)
            .Select(group => $"Processen {group.Key} konkurrerade med CPU/disk under incidenten.")
            .ToArray();

        hypotheses.Add(new HypothesisScore(RootCauseCategory.ExternalProcessInterference, 0.55, evidence));
    }

    private static void AddOsLatencyHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, IReadOnlyList<ArtifactEvidence> artifacts, IReadOnlyList<SystemTelemetrySample> systemSamples, IReadOnlyList<ObsTelemetrySample> obsSamples)
    {
        var latencyArtifacts = artifacts.Where(item => item.Kind == ArtifactKind.EtlTrace || item.Summary.Contains("DPC", StringComparison.OrdinalIgnoreCase) || item.Summary.Contains("ISR", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (latencyArtifacts.Length == 0)
        {
            return;
        }

        double confidence = 0.35;
        var evidence = latencyArtifacts.Select(item => item.Summary).Distinct().ToList();

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.15;
            evidence.Add("Severe frametime-spikes syns samtidigt som ETW-latency-signaler.");
        }

        if (systemSamples.Any(item => item.TotalCpuUsagePercent < 80) && obsSamples.All(item => !item.IsConnected))
        {
            confidence += 0.1;
            evidence.Add("Ingen annan stark lokal contention-stack överröstade ETW-fynden.");
        }

        hypotheses.Add(new HypothesisScore(RootCauseCategory.OsOrDriverLatency, Math.Min(confidence, 0.82), evidence));
    }

    private static void AddCorruptionHypothesis(List<HypothesisScore> hypotheses, IReadOnlyList<ArtifactEvidence> artifacts)
    {
        var corruption = artifacts.Where(item => item.Summary.Contains("cache", StringComparison.OrdinalIgnoreCase) || item.Summary.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || item.Summary.Contains("failed to load", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (corruption.Length == 0)
        {
            return;
        }

        hypotheses.Add(new HypothesisScore(
            RootCauseCategory.PossibleCacheOrResourceCorruption,
            Math.Min(0.45 + (corruption.Length * 0.08), 0.8),
            corruption.Select(item => item.Summary).Distinct().ToArray()));
    }

    private static IReadOnlyList<TimelineHighlight> BuildHighlights(IncidentRecord incident, FrameMetrics metrics, HypothesisScore top, IReadOnlyList<ArtifactEvidence> artifacts, IReadOnlyList<ObsTelemetrySample> obsSamples, IReadOnlyList<NetworkProbeSample> probes)
    {
        var highlights = new List<TimelineHighlight>
        {
            new(incident.Marker.MarkedAt, "Marker", $"{incident.Marker.Label} markerad {incident.Marker.MarkedAt:HH:mm:ss}."),
            new(incident.Marker.MarkedAt, "Frame", $"P95 {metrics.P95FrameTime:F1} ms, P99 {metrics.P99FrameTime:F1} ms, {metrics.SpikeCount} spikes över 25 ms."),
        };

        var obs = obsSamples.LastOrDefault(item => item.IsConnected);
        if (obs is not null)
        {
            highlights.Add(new(obs.Timestamp, "OBS", $"OBS render time {obs.AverageFrameRenderTimeMs:F1} ms, render skipped {obs.RenderSkippedFrames}, output skipped {obs.OutputSkippedFrames}."));
        }

        var probe = probes.OrderByDescending(item => item.RoundTripTimeMs ?? 0).FirstOrDefault();
        if (probe is not null)
        {
            highlights.Add(new(probe.Timestamp, "Network", probe.Success
                ? $"RTT mot {probe.Host} nådde {probe.RoundTripTimeMs:F0} ms."
                : $"Probe mot {probe.Host} misslyckades: {probe.FailureReason ?? "okänt fel"}."));
        }

        highlights.AddRange(artifacts.Take(3).Select(item => new TimelineHighlight(item.Timestamp, item.Kind.ToString(), item.Summary)));
        highlights.Add(new(incident.Marker.MarkedAt, "Classification", $"Högst rankad hypotes: {ToLabel(top.Category)} ({top.Confidence:P0})."));
        return highlights.OrderBy(item => item.Timestamp).ToArray();
    }

    private static string BuildSummary(HypothesisScore top, FrameMetrics metrics, IReadOnlyList<ObsTelemetrySample> obsSamples, IReadOnlyList<ArtifactEvidence> artifacts, IReadOnlyList<NetworkProbeSample> probes)
    {
        if (top.Category == RootCauseCategory.InsufficientEvidence)
        {
            return "Insufficient evidence. Kör gärna en ny session i grundläge igen. Om du vill ha djupare framedata kan du lägga till PresentMon om det finns tillgängligt, eller bifoga profiler/net_stats eller en kort deep capture för severe stutters.";
        }

        var obsActive = obsSamples.Any(item => item.IsConnected) ? "OBS var aktivt." : "OBS var inte aktivt.";
        var artifactHint = artifacts.Count > 0 ? $" {artifacts.Count} importerade artifacts bidrog till bedömningen." : string.Empty;
        var probeHint = probes.Any() ? " Nätprober fanns tillgängliga i incidentfönstret." : string.Empty;

        return $"Trolig rotorsak: {ToLabel(top.Category)} ({top.Confidence:P0}). Frametime-fönstret hade P95 {metrics.P95FrameTime:F1} ms och P99 {metrics.P99FrameTime:F1} ms. {obsActive}{artifactHint}{probeHint}";
    }

    private static long Delta(IEnumerable<long?> samples)
    {
        var values = samples.Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        return values.Length >= 2 ? values[^1] - values[0] : 0;
    }

    private static double ToMegabytes(long bytes)
    {
        return bytes / 1024d / 1024d;
    }

    private static string ToLabel(RootCauseCategory category)
    {
        return category switch
        {
            RootCauseCategory.GpuFrametimeContention => "GPU/frametime contention",
            RootCauseCategory.ObsRenderOutputContention => "OBS/render/output contention",
            RootCauseCategory.FiveMResourceSpike => "FiveM resource/script spike",
            RootCauseCategory.NetworkJitterOrPacketLoss => "Network jitter/packet loss/routing issue",
            RootCauseCategory.StreamingOrDiskStall => "Streaming/disk stall",
            RootCauseCategory.ExternalProcessInterference => "External process interference",
            RootCauseCategory.OsOrDriverLatency => "OS/driver latency",
            RootCauseCategory.PossibleCacheOrResourceCorruption => "Possible cache/resource corruption",
            _ => "Insufficient evidence",
        };
    }

    private sealed record FrameMetrics(double AverageFrameTime, double P95FrameTime, double P99FrameTime, int SpikeCount, int SevereSpikeCount, double LongestSpikeMs);
}

public sealed class NetStatsCsvArtifactParser : IArtifactParser
{
    public bool CanParse(string path)
    {
        return Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).Contains("net", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        if (lines.Length < 2)
        {
            return CreateResult(path, ArtifactKind.NetStatsCsv, [], ["CSV-filen var tom eller saknade datapunkter."]);
        }

        var headers = lines[0].Split(',').Select(item => item.Trim()).ToArray();
        var rows = lines.Skip(1).Select(line => line.Split(',')).ToArray();
        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        ExtractColumnMetric(headers, rows, metrics, "ping", "avgPingMs");
        ExtractColumnMetric(headers, rows, metrics, "jitter", "avgJitterMs");
        ExtractColumnMetric(headers, rows, metrics, "loss", "avgPacketLossPercent");

        var evidence = new List<ArtifactEvidence>();
        if (metrics.Count > 0)
        {
            var summary = $"net_statsFile visade ping {metrics.GetValueOrDefault("avgPingMs", 0):F0} ms, jitter {metrics.GetValueOrDefault("avgJitterMs", 0):F0} ms och packet loss {metrics.GetValueOrDefault("avgPacketLossPercent", 0):F1}%";
            evidence.Add(new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.NetStatsCsv, summary, metrics, path));
        }

        return CreateResult(path, ArtifactKind.NetStatsCsv, evidence, []);
    }

    private static void ExtractColumnMetric(string[] headers, string[][] rows, Dictionary<string, double> metrics, string nameHint, string outputKey)
    {
        var index = Array.FindIndex(headers, header => header.Contains(nameHint, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        var values = rows
            .Where(row => row.Length > index && double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(row => double.Parse(row[index], CultureInfo.InvariantCulture))
            .ToArray();

        if (values.Length > 0)
        {
            metrics[outputKey] = values.Average();
            metrics[$"max_{outputKey}"] = values.Max();
        }
    }

    private static ArtifactParseResult CreateResult(string path, ArtifactKind kind, IReadOnlyList<ArtifactEvidence> evidence, IReadOnlyList<string> notes)
    {
        return new ArtifactParseResult(
            new ArtifactAttachment(path, kind, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
            evidence,
            notes);
    }
}

public sealed class ProfilerJsonArtifactParser : IArtifactParser
{
    public bool CanParse(string path)
    {
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).Contains("profile", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var evidence = new List<ArtifactEvidence>();
        if (document.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            var heaviest = resources.EnumerateArray()
                .Select(resource => new
                {
                    Name = resource.TryGetProperty("name", out var name) ? name.GetString() : "unknown",
                    TimeMs = TryGetNumber(resource, "timeMs") ?? TryGetNumber(resource, "cpuMs") ?? 0,
                })
                .OrderByDescending(item => item.TimeMs)
                .FirstOrDefault();

            if (heaviest is not null)
            {
                evidence.Add(new ArtifactEvidence(
                    DateTimeOffset.UtcNow,
                    ArtifactKind.ProfilerJson,
                    $"Profiler JSON pekade ut resource '{heaviest.Name}' med {heaviest.TimeMs:F1} ms.",
                    new Dictionary<string, double> { ["topResourceMs"] = heaviest.TimeMs },
                    path));
            }
        }

        if (evidence.Count == 0)
        {
            evidence.Add(new ArtifactEvidence(
                DateTimeOffset.UtcNow,
                ArtifactKind.ProfilerJson,
                "Profiler JSON importerades men kunde bara analyseras generiskt. Kontrollera filformatet för mer detaljerad resursklassning.",
                new Dictionary<string, double>(),
                path));
        }

        return new ArtifactParseResult(
            new ArtifactAttachment(path, ArtifactKind.ProfilerJson, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
            evidence,
            []);
    }

    private static double? TryGetNumber(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : null;
    }
}

public sealed class ResmonArtifactParser : IArtifactParser
{
    public bool CanParse(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Contains("resmon", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("resource", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var suspiciousLine = lines.FirstOrDefault(line => line.Contains("ms", StringComparison.OrdinalIgnoreCase) || line.Contains("cpu", StringComparison.OrdinalIgnoreCase));
        var summary = suspiciousLine is null
            ? "resmon/export importerades som manuellt bevis."
            : $"resmon/export antyder resource-spike: {suspiciousLine.Trim()}";

        return new ArtifactParseResult(
            new ArtifactAttachment(path, ArtifactKind.ResmonSnapshot, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
            [new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.ResmonSnapshot, summary, new Dictionary<string, double>(), path)],
            []);
    }
}

public sealed class LogArtifactParser : IArtifactParser
{
    private static readonly string[] Keywords = ["cache", "corrupt", "stream", "timeout", "failed to load", "resource"];

    public bool CanParse(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var hits = lines.Where(line => Keywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase))).Take(10).ToArray();

        var evidence = hits.Length == 0
            ? [new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.LogFile, "Loggfil importerades utan starka signaturer i snabbparsen.", new Dictionary<string, double>(), path)]
            : hits.Select(line => new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.LogFile, $"Logghint: {line.Trim()}", new Dictionary<string, double>(), path)).ToArray();

        return new ArtifactParseResult(
            new ArtifactAttachment(path, ArtifactKind.LogFile, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
            evidence,
            []);
    }
}