using System.Globalization;
using System.Text.Json;

namespace FiveMDiagnostics.Analysis;

using FiveMDiagnostics.Core;

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
