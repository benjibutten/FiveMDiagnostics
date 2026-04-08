using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FiveMDiagnostics.Export;

using FiveMDiagnostics.Core;

public sealed class IncidentBundleExporter : IIncidentExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<string> ExportAsync(IncidentRecord incident, ExportBundleOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var exportName = $"incident_{incident.Marker.MarkedAt:yyyyMMdd_HHmmss}_{incident.Marker.Severity}";
        var stagingDirectory = Path.Combine(options.OutputDirectory, exportName);
        var zipPath = Path.Combine(options.OutputDirectory, exportName + ".zip");

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        Directory.CreateDirectory(stagingDirectory);

        var sanitizedIncident = options.IncludeSensitiveFields ? incident : Sanitize(incident);
        var summaryPath = Path.Combine(stagingDirectory, "summary.json");
        var metricsPath = Path.Combine(stagingDirectory, "metrics.csv");
        var reportPath = Path.Combine(stagingDirectory, "incident-report.txt");

        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(BuildSummaryModel(sanitizedIncident), JsonOptions), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(metricsPath, BuildMetricsCsv(sanitizedIncident), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(reportPath, BuildReport(sanitizedIncident), cancellationToken).ConfigureAwait(false);

        if (options.IncludeAttachedArtifacts)
        {
            await CopyArtifactsAsync(sanitizedIncident.Attachments, stagingDirectory, cancellationToken).ConfigureAwait(false);
        }

        ZipFile.CreateFromDirectory(stagingDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        Directory.Delete(stagingDirectory, recursive: true);
        return zipPath;
    }

    private static object BuildSummaryModel(IncidentRecord incident)
    {
        return new
        {
            incident.Id,
            incident.Marker,
            incident.WindowStart,
            incident.WindowEnd,
            incident.Environment,
            Analysis = incident.Analysis,
            Attachments = incident.Attachments.Select(item => new { item.DisplayName, item.Kind, item.ImportedAt, item.Sensitive }),
            EventCounts = incident.Events.GroupBy(item => item.Source).ToDictionary(group => group.Key, group => group.Count()),
        };
    }

    private static string BuildMetricsCsv(IncidentRecord incident)
    {
        var builder = new StringBuilder();
        builder.AppendLine("timestamp,source,key,value");

        foreach (var telemetryEvent in incident.Events.OrderBy(item => item.Timestamp))
        {
            foreach (var row in FlattenEvent(telemetryEvent))
            {
                builder.AppendLine($"{telemetryEvent.Timestamp:O},{telemetryEvent.Source},{Escape(row.Key)},{Escape(row.Value)}");
            }
        }

        return builder.ToString();
    }

    private static string BuildReport(IncidentRecord incident)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Incident: {incident.Marker.Label}");
        builder.AppendLine($"Severity: {incident.Marker.Severity}");
        builder.AppendLine($"Marked at: {incident.Marker.MarkedAt:O}");
        builder.AppendLine($"Window: {incident.WindowStart:O} -> {incident.WindowEnd:O}");
        builder.AppendLine($"Server profile: {incident.Environment.ServerProfileName}");
        builder.AppendLine();
        builder.AppendLine(incident.Analysis?.Summary ?? "Ingen analys kördes före export.");
        builder.AppendLine();
        builder.AppendLine("Top hypotheses:");

        foreach (var hypothesis in incident.Analysis?.Hypotheses.Take(5) ?? [])
        {
            builder.AppendLine($"- {hypothesis.Category}: {hypothesis.Confidence:P0}");
            foreach (var evidence in hypothesis.Evidence)
            {
                builder.AppendLine($"  * {evidence}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Timeline:");
        foreach (var highlight in incident.Analysis?.TimelineHighlights ?? [])
        {
            builder.AppendLine($"- {highlight.Timestamp:HH:mm:ss} [{highlight.Category}] {highlight.Summary}");
        }

        return builder.ToString();
    }

    private static async Task CopyArtifactsAsync(IReadOnlyList<ArtifactAttachment> attachments, string stagingDirectory, CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        var artifactDirectory = Path.Combine(stagingDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);

        foreach (var attachment in attachments)
        {
            if (!File.Exists(attachment.FilePath))
            {
                continue;
            }

            var targetPath = Path.Combine(artifactDirectory, attachment.DisplayName);
            await using var source = File.OpenRead(attachment.FilePath);
            await using var target = File.Create(targetPath);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IncidentRecord Sanitize(IncidentRecord incident)
    {
        var sanitizedEvents = incident.Events.Select(SanitizeEvent).ToArray();
        var sanitizedAttachments = incident.Attachments
            .Select(item => item with { FilePath = Path.GetFileName(item.FilePath) })
            .ToArray();

        return incident with { Events = sanitizedEvents, Attachments = sanitizedAttachments };
    }

    private static TelemetryEvent SanitizeEvent(TelemetryEvent telemetryEvent)
    {
        return telemetryEvent switch
        {
            NetworkEndpointSample network => network with
            {
                RemoteEndpoints = network.RemoteEndpoints.Select(item => item with { RemoteAddress = "[redacted]" }).ToArray(),
            },
            ArtifactEvidence artifact => artifact with { SourceFile = artifact.SourceFile is null ? null : Path.GetFileName(artifact.SourceFile) },
            _ => telemetryEvent,
        };
    }

    private static IEnumerable<(string Key, string Value)> FlattenEvent(TelemetryEvent telemetryEvent)
    {
        return telemetryEvent switch
        {
            FrameTelemetrySample frame =>
            [
                ("frameTimeMs", frame.FrameTimeMs.ToString("F2")),
                ("gpuBusyMs", frame.GpuBusyMs?.ToString("F2") ?? string.Empty),
                ("displayLatencyMs", frame.DisplayLatencyMs?.ToString("F2") ?? string.Empty),
                ("dropped", frame.Dropped.ToString()),
            ],
            SystemTelemetrySample system =>
            [
                ("totalCpuUsagePercent", system.TotalCpuUsagePercent.ToString("F1")),
                ("memoryCommitPercent", system.MemoryCommitPercent.ToString("F1")),
                ("availableMemoryMb", system.AvailableMemoryMb.ToString()),
                ("topCpuProcesses", string.Join(';', system.TopCpuProcesses.Select(item => $"{item.ProcessName}:{item.CpuPercent:F1}%"))),
                ("topDiskProcesses", string.Join(';', system.TopDiskProcesses.Select(item => $"{item.ProcessName}:{item.IoBytesPerSecond}"))),
            ],
            ProcessTelemetrySample process =>
            [
                ("processName", process.ProcessName),
                ("cpuUsagePercent", process.CpuUsagePercent.ToString("F1")),
                ("privateBytes", process.PrivateBytes.ToString()),
                ("workingSetBytes", process.WorkingSetBytes.ToString()),
                ("threadCount", process.ThreadCount.ToString()),
                ("readBytesPerSecond", process.ReadBytesPerSecond.ToString()),
                ("writeBytesPerSecond", process.WriteBytesPerSecond.ToString()),
            ],
            ObsTelemetrySample obs =>
            [
                ("isConnected", obs.IsConnected.ToString()),
                ("activeFps", obs.ActiveFps?.ToString("F1") ?? string.Empty),
                ("averageFrameRenderTimeMs", obs.AverageFrameRenderTimeMs?.ToString("F1") ?? string.Empty),
                ("renderSkippedFrames", obs.RenderSkippedFrames?.ToString() ?? string.Empty),
                ("outputSkippedFrames", obs.OutputSkippedFrames?.ToString() ?? string.Empty),
                ("isStreaming", obs.IsStreaming.ToString()),
                ("isRecording", obs.IsRecording.ToString()),
            ],
            NetworkEndpointSample network =>
            [
                ("remoteEndpoints", string.Join(';', network.RemoteEndpoints.Select(item => $"{item.Protocol}:{item.RemoteAddress}:{item.RemotePort}"))),
                ("udpPorts", string.Join(';', network.UdpLocalPorts)),
            ],
            NetworkProbeSample probe =>
            [
                ("host", probe.Host),
                ("success", probe.Success.ToString()),
                ("rttMs", probe.RoundTripTimeMs?.ToString("F1") ?? string.Empty),
                ("failureReason", probe.FailureReason ?? string.Empty),
            ],
            ArtifactEvidence artifact =>
            [
                ("kind", artifact.Kind.ToString()),
                ("summary", artifact.Summary),
                ("metrics", string.Join(';', artifact.Metrics.Select(item => $"{item.Key}={item.Value:F2}"))),
            ],
            _ => [("summary", telemetryEvent.Source)],
        };
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}