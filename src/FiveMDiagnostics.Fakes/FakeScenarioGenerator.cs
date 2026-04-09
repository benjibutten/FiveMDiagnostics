namespace FiveMDiagnostics.Fakes;

using FiveMDiagnostics.Core;

public enum FakeScenarioKind
{
    ObsGpuContention,
    FiveMResourceSpike,
    NetworkIssue,
}

public sealed record FakeScenario(
    string Name,
    EnvironmentMetadata Environment,
    IReadOnlyList<TelemetryEvent> Events,
    IReadOnlyList<ArtifactAttachment> Attachments,
    DateTimeOffset MarkerTime,
    IncidentSeverity Severity)
{
    public IncidentRecord ToIncidentRecord()
    {
        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), MarkerTime, Severity, Name),
            MarkerTime.AddSeconds(-30),
            MarkerTime.AddSeconds(60),
            Environment,
            Events.OrderBy(item => item.Timestamp).ToArray(),
            Analysis: null,
            Attachments);
    }
}

public static class FakeScenarioGenerator
{
    public static FakeScenario Create(FakeScenarioKind kind, DateTimeOffset? baseTime = null)
    {
        var markerTime = (baseTime ?? DateTimeOffset.UtcNow).AddSeconds(30);
        var environment = new EnvironmentMetadata(
            "Windows 11 23H2",
            "AMD Ryzen 7 7800X3D",
            32UL * 1024 * 1024 * 1024,
            "NVIDIA GeForce RTX 4070",
            "555.12",
            165,
            "Enabled",
            kind == FakeScenarioKind.ObsGpuContention,
            "Example Server",
            markerTime.AddSeconds(-30),
            markerTime.AddSeconds(60));

        return kind switch
        {
            FakeScenarioKind.ObsGpuContention => CreateObsScenario(environment, markerTime),
            FakeScenarioKind.FiveMResourceSpike => CreateResourceScenario(environment, markerTime),
            FakeScenarioKind.NetworkIssue => CreateNetworkScenario(environment, markerTime),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static FakeScenario CreateObsScenario(EnvironmentMetadata environment, DateTimeOffset markerTime)
    {
        var events = new List<TelemetryEvent>();
        for (var offset = -30; offset <= 60; offset++)
        {
            var timestamp = markerTime.AddSeconds(offset);
            var spike = offset is >= 0 and <= 5 ? 42 + (offset * 3) : 16.6;
            events.Add(new FrameTelemetrySample(timestamp, spike, 19 + Math.Max(offset, 0), 8 + Math.Max(offset, 0), spike, spike > 30, "FiveM_b2944_GTAProcess"));
            events.Add(new SystemTelemetrySample(timestamp, 62, new Dictionary<string, double> { ["0"] = 55, ["1"] = 61 }, 58, 14320, [new ProcessActivity("obs64", 8120, 21, 4 * 1024 * 1024)], [new ProcessActivity("obs64", 8120, 21, 4 * 1024 * 1024)]));
            events.Add(new ProcessTelemetrySample(timestamp, 9152, "FiveM_b2944_GTAProcess", 38, 6L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 148, 6 * 1024 * 1024, 2 * 1024 * 1024));
            events.Add(new ObsTelemetrySample(timestamp, true, 58, offset is >= 0 and <= 5 ? 24 : 7, 120 + Math.Max(offset, 0), 18 + Math.Max(offset, 0), 12, 812, true, false));
        }

        return new FakeScenario("OBS/GPU contention demo", environment, events, [], markerTime, IncidentSeverity.Normal);
    }

    private static FakeScenario CreateResourceScenario(EnvironmentMetadata environment, DateTimeOffset markerTime)
    {
        var events = new List<TelemetryEvent>();
        for (var offset = -30; offset <= 60; offset++)
        {
            var timestamp = markerTime.AddSeconds(offset);
            var spike = offset is >= -1 and <= 4 ? 38 + (offset + 1) * 4 : 16.2;
            events.Add(new FrameTelemetrySample(timestamp, spike, 10, 6, spike, spike > 30, "FiveM_b2944_GTAProcess"));
            events.Add(new SystemTelemetrySample(timestamp, 54, new Dictionary<string, double> { ["0"] = 49, ["1"] = 51 }, 52, 15800, [new ProcessActivity("FiveM_b2944_GTAProcess", 9152, offset is >= -1 and <= 4 ? 67 : 32, 12 * 1024 * 1024)], [new ProcessActivity("FiveM_b2944_GTAProcess", 9152, 0, 12 * 1024 * 1024)]));
            events.Add(new ProcessTelemetrySample(timestamp, 9152, "FiveM_b2944_GTAProcess", offset is >= -1 and <= 4 ? 72 : 31, 6L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, offset is >= -1 and <= 4 ? 190 : 142, 12 * 1024 * 1024, 3 * 1024 * 1024));
            events.Add(new ObsTelemetrySample(timestamp, false, null, null, null, null, null, null, false, false));
        }

        var profilerArtifact = new ArtifactAttachment("scenario://profiler.json", ArtifactKind.ProfilerJson, "profiler.json", markerTime, Sensitive: false);
        events.Add(new ArtifactEvidence(markerTime.AddSeconds(2), ArtifactKind.ProfilerJson, "Profiler JSON pekade ut resource 'inventory' med 78.0 ms.", new Dictionary<string, double> { ["topResourceMs"] = 78 }, profilerArtifact.FilePath));
        events.Add(new ArtifactEvidence(markerTime.AddSeconds(3), ArtifactKind.ResmonSnapshot, "resmon/export antyder resource-spike: inventory 11.4ms", new Dictionary<string, double>(), "scenario://resmon.txt"));

        return new FakeScenario("FiveM resource spike demo", environment with { ObsDetectedAtStart = false }, events, [profilerArtifact], markerTime, IncidentSeverity.Normal);
    }

    private static FakeScenario CreateNetworkScenario(EnvironmentMetadata environment, DateTimeOffset markerTime)
    {
        var events = new List<TelemetryEvent>();
        for (var offset = -30; offset <= 60; offset++)
        {
            var timestamp = markerTime.AddSeconds(offset);
            events.Add(new FrameTelemetrySample(timestamp, 17, 8, 5, 17, false, "FiveM_b2944_GTAProcess"));
            events.Add(new SystemTelemetrySample(timestamp, 43, new Dictionary<string, double> { ["0"] = 39, ["1"] = 44 }, 49, 16384, [], []));
            events.Add(new ProcessTelemetrySample(timestamp, 9152, "FiveM_b2944_GTAProcess", 27, 6L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 138, 3 * 1024 * 1024, 1 * 1024 * 1024));
            events.Add(new ObsTelemetrySample(timestamp, false, null, null, null, null, null, null, false, false));
            events.Add(new NetworkEndpointSample(timestamp, 9152, [new RemoteEndpointInfo("TCP", "203.0.113.14", 30120, "Example Server")], [30120]));
            events.Add(new NetworkProbeSample(timestamp, "203.0.113.14", offset is >= 0 and <= 10 ? 145 + offset : 28, true));
        }

        return new FakeScenario("Network issue demo", environment with { ObsDetectedAtStart = false }, events, [], markerTime, IncidentSeverity.Normal);
    }
}