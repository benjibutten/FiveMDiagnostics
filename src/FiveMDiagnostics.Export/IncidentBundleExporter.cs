using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FiveMDiagnostics.Export;

using FiveMDiagnostics.Core;

public sealed class IncidentBundleExporter : IIncidentExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,

        // Numeric enums make an export depend on declaration order: inserting a category shifts the
        // meaning of every previously written file. Names are stable and readable on their own.
        Converters = { new JsonStringEnumConverter() },
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
        var captureHealth = BuildCaptureHealth(incident);
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
            CaptureHealth = captureHealth,
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
        var health = BuildCaptureHealth(incident);
        builder.AppendLine(FormattableString.Invariant($"Capture health: {health.FrameCount} frames, range {health.FirstFrameAt:O} -> {health.LastFrameAt:O} ({health.FrameSpanSeconds:F1} s), incident gaps {health.GapCount}, largest incident gap {health.LargestGapSeconds:F2} s, session restarts at incident end {health.SessionRestartCountAtEnd}."));
        builder.AppendLine($"Window coverage: pre-buffer {(health.PreWindowCovered ? "complete" : "incomplete")}, post-window {(health.PostWindowCovered ? "complete" : "incomplete")}, full window {(health.FullWindowCovered ? "yes" : "no")}.");
        builder.AppendLine();
        builder.AppendLine(incident.Analysis?.Summary ?? "Ingen analys kördes före export.");
        builder.AppendLine();
        builder.AppendLine("Top hypotheses:");

        foreach (var hypothesis in incident.Analysis?.Hypotheses.Take(5) ?? [])
        {
            builder.AppendLine(FormattableString.Invariant($"- {hypothesis.Category}: {hypothesis.Confidence:P0}"));
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
        // Collect the addresses before redacting them: the analysis text is generated from these events
        // and embeds the server address verbatim in summaries, timeline entries and hypothesis evidence.
        // Redacting only the structured fields would still ship the IP in prose.
        var sensitiveHosts = CollectSensitiveHosts(incident);
        var sanitizedEvents = incident.Events.Select(SanitizeEvent).ToArray();
        var sanitizedAttachments = incident.Attachments
            .Select(item => item with { FilePath = Path.GetFileName(item.FilePath) })
            .ToArray();

        return incident with
        {
            Events = sanitizedEvents,
            Attachments = sanitizedAttachments,
            Analysis = SanitizeAnalysis(incident.Analysis, sensitiveHosts),
        };
    }

    private static IReadOnlyCollection<string> CollectSensitiveHosts(IncidentRecord incident)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var telemetryEvent in incident.Events)
        {
            switch (telemetryEvent)
            {
                case NetworkProbeSample probe when !string.IsNullOrWhiteSpace(probe.Host):
                    hosts.Add(probe.Host);
                    break;
                case NetworkEndpointSample endpoints:
                    foreach (var endpoint in endpoints.RemoteEndpoints)
                    {
                        if (!string.IsNullOrWhiteSpace(endpoint.RemoteAddress))
                        {
                            hosts.Add(endpoint.RemoteAddress);
                        }
                    }

                    break;
            }
        }

        // Longest first, so an address is not partially replaced by a shorter overlapping match.
        return hosts.OrderByDescending(item => item.Length).ToArray();
    }

    private static IncidentAnalysis? SanitizeAnalysis(IncidentAnalysis? analysis, IReadOnlyCollection<string> sensitiveHosts)
    {
        if (analysis is null || sensitiveHosts.Count == 0)
        {
            return analysis;
        }

        return analysis with
        {
            Summary = Redact(analysis.Summary, sensitiveHosts),
            Hypotheses = analysis.Hypotheses
                .Select(item => item with { Evidence = item.Evidence.Select(text => Redact(text, sensitiveHosts)).ToArray() })
                .ToArray(),
            TimelineHighlights = analysis.TimelineHighlights
                .Select(item => item with { Summary = Redact(item.Summary, sensitiveHosts) })
                .ToArray(),
        };
    }

    private static string Redact(string value, IReadOnlyCollection<string> sensitiveHosts)
    {
        foreach (var host in sensitiveHosts)
        {
            value = value.Replace(host, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static TelemetryEvent SanitizeEvent(TelemetryEvent telemetryEvent)
    {
        return telemetryEvent switch
        {
            NetworkEndpointSample network => network with
            {
                RemoteEndpoints = network.RemoteEndpoints.Select(item => item with { RemoteAddress = "[redacted]" }).ToArray(),
            },
            NetworkProbeSample probe => probe with { Host = "[redacted]" },
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
                ("frameTimeMs", frame.FrameTimeMs.ToString("F2", CultureInfo.InvariantCulture)),
                ("cpuBusyMs", frame.CpuBusyMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("cpuWaitMs", frame.CpuWaitMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("gpuBusyMs", frame.GpuBusyMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("gpuWaitMs", frame.GpuWaitMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("gpuLatencyMs", frame.GpuLatencyMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("displayLatencyMs", frame.DisplayLatencyMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("flipDelayMs", frame.FlipDelayMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("inputLatencyMs", frame.InputLatencyMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("msBetweenDisplayChange", frame.MsBetweenDisplayChange?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty),
                ("presentMode", frame.PresentMode ?? string.Empty),
                ("dropped", frame.Dropped.ToString()),
            ],
            GpuTelemetrySample gpu =>
            [
                ("isAvailable", gpu.IsAvailable.ToString()),
                ("adapterName", gpu.AdapterName ?? string.Empty),
                ("utilizationPercent", gpu.UtilizationPercent?.ToString("F0", CultureInfo.InvariantCulture) ?? string.Empty),
                ("vramUsedBytes", gpu.UsedVramBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("vramTotalBytes", gpu.TotalVramBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("vramUsagePercent", gpu.VramUsagePercent?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty),
                ("encoderUtilizationPercent", gpu.EncoderUtilizationPercent?.ToString("F0", CultureInfo.InvariantCulture) ?? string.Empty),
                ("decoderUtilizationPercent", gpu.DecoderUtilizationPercent?.ToString("F0", CultureInfo.InvariantCulture) ?? string.Empty),
                ("temperatureCelsius", gpu.TemperatureCelsius?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("throttleReasons", string.Join(';', gpu.ThrottleReasons)),
            ],
            SystemTelemetrySample system => FlattenSystem(system),
            ProcessTelemetrySample process =>
            [
                ("processName", process.ProcessName),
                ("cpuUsagePercent", process.CpuUsagePercent.ToString("F1", CultureInfo.InvariantCulture)),
                ("privateBytes", process.PrivateBytes.ToString(CultureInfo.InvariantCulture)),
                ("workingSetBytes", process.WorkingSetBytes.ToString(CultureInfo.InvariantCulture)),
                ("threadCount", process.ThreadCount.ToString(CultureInfo.InvariantCulture)),
                ("readBytesPerSecond", process.ReadBytesPerSecond.ToString(CultureInfo.InvariantCulture)),
                ("writeBytesPerSecond", process.WriteBytesPerSecond.ToString(CultureInfo.InvariantCulture)),
            ],
            ObsTelemetrySample obs =>
            [
                ("isConnected", obs.IsConnected.ToString()),
                ("activeFps", obs.ActiveFps?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty),
                ("averageFrameRenderTimeMs", obs.AverageFrameRenderTimeMs?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty),
                ("renderSkippedFrames", obs.RenderSkippedFrames?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("outputSkippedFrames", obs.OutputSkippedFrames?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("isProcessRunning", obs.IsProcessRunning.ToString()),
                ("isWebSocketConnected", obs.IsConnected.ToString()),
                ("isStreaming", obs.IsStreaming.ToString()),
                ("isRecording", obs.IsRecording.ToString()),
            ],
            CaptureHealthTelemetrySample health =>
            [
                ("frameCount", health.FrameCount.ToString(CultureInfo.InvariantCulture)),
                ("firstFrameAt", health.FirstFrameAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                ("lastFrameAt", health.LastFrameAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                ("largestFrameGapSeconds", health.LargestFrameGapSeconds.ToString("F3", CultureInfo.InvariantCulture)),
                ("continuousFrameSpanSeconds", health.ContinuousFrameSpanSeconds.ToString("F1", CultureInfo.InvariantCulture)),
                ("restartCount", health.RestartCount.ToString(CultureInfo.InvariantCulture)),
                ("captureProcessRunning", health.CaptureProcessRunning.ToString()),
                ("frameGapCount", health.FrameGapCount.ToString(CultureInfo.InvariantCulture)),
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
                ("rttMs", probe.RoundTripTimeMs?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty),
                ("failureReason", probe.FailureReason ?? string.Empty),
            ],
            ArtifactEvidence artifact =>
            [
                ("kind", artifact.Kind.ToString()),
                ("summary", artifact.Summary),
                ("metrics", string.Join(';', artifact.Metrics.Select(item => FormattableString.Invariant($"{item.Key}={item.Value:F2}")))),
            ],
            _ => [("summary", telemetryEvent.Source)],
        };
    }

    private static IEnumerable<(string Key, string Value)> FlattenSystem(SystemTelemetrySample system)
    {
        yield return ("totalCpuUsagePercent", system.TotalCpuUsagePercent.ToString("F1", CultureInfo.InvariantCulture));
        yield return ("memoryCommitPercent", system.MemoryCommitPercent.ToString("F1", CultureInfo.InvariantCulture));
        yield return ("availableMemoryMb", system.AvailableMemoryMb.ToString(CultureInfo.InvariantCulture));
        yield return ("diskAverageLatencyMs", system.DiskAverageLatencyMs?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty);
        yield return ("diskQueueLength", system.DiskQueueLength?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty);
        yield return ("hardFaultPagesPerSecond", system.HardFaultPagesPerSecond?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty);
        yield return ("topCpuProcesses", string.Join(';', system.TopCpuProcesses.Select(item => FormattableString.Invariant($"{item.ProcessName}:{item.CpuPercent:F1}%"))));
        yield return ("topDiskProcesses", string.Join(';', system.TopDiskProcesses.Select(item => FormattableString.Invariant($"{item.ProcessName}:{item.IoBytesPerSecond}"))));
        foreach (var core in system.PerCoreUsagePercent.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            yield return ($"cpuCore.{core.Key}.usagePercent", core.Value.ToString("F1", CultureInfo.InvariantCulture));
        }
    }

    private static CaptureHealthSummary BuildCaptureHealth(IncidentRecord incident)
    {
        var frames = incident.GetEvents<FrameTelemetrySample>();
        var healthSamples = incident.GetEvents<CaptureHealthTelemetrySample>();
        var first = frames.FirstOrDefault()?.Timestamp;
        var last = frames.LastOrDefault()?.Timestamp;
        var largestObservedGap = frames.Zip(frames.Skip(1), (left, right) => Math.Max(0, (right.Timestamp - left.Timestamp).TotalSeconds)).DefaultIfEmpty().Max();
        var observedGapCount = frames.Zip(frames.Skip(1), (left, right) => (right.Timestamp - left.Timestamp).TotalSeconds > 2 ? 1 : 0).Sum();
        var tolerance = TimeSpan.FromSeconds(2);
        var preCovered = HasContinuousCoverage(frames, incident.WindowStart, incident.Marker.MarkedAt, tolerance);
        var postCovered = HasContinuousCoverage(frames, incident.Marker.MarkedAt, incident.WindowEnd, tolerance);
        var fullWindowCovered = preCovered && postCovered && largestObservedGap <= tolerance.TotalSeconds;

        return new CaptureHealthSummary(
            frames.Count,
            first,
            last,
            first is { } start && last is { } end ? Math.Max(0, (end - start).TotalSeconds) : 0,
            largestObservedGap,
            observedGapCount,
            healthSamples.Select(item => item.RestartCount).DefaultIfEmpty().Max(),
            preCovered,
            postCovered,
            fullWindowCovered);
    }

    private static bool HasContinuousCoverage(
        IReadOnlyList<FrameTelemetrySample> frames,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan tolerance)
    {
        var segment = frames.Where(item => item.Timestamp >= start && item.Timestamp <= end).ToArray();
        if (segment.Length == 0
            || segment[0].Timestamp > start + tolerance
            || segment[^1].Timestamp < end - tolerance)
        {
            return false;
        }

        return segment
            .Zip(segment.Skip(1), (left, right) => right.Timestamp - left.Timestamp)
            .All(gap => gap <= tolerance);
    }

    private sealed record CaptureHealthSummary(
        int FrameCount,
        DateTimeOffset? FirstFrameAt,
        DateTimeOffset? LastFrameAt,
        double FrameSpanSeconds,
        double LargestGapSeconds,
        int GapCount,
        int SessionRestartCountAtEnd,
        bool PreWindowCovered,
        bool PostWindowCovered,
        bool FullWindowCovered);

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
