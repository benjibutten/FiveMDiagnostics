namespace FiveMDiagnostics.Fakes;

using FiveMDiagnostics.Core;

public enum FakeScenarioKind
{
    ObsGpuContention,
    FiveMResourceSpike,
    NetworkIssue,
    GpuVramPressure,
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
            FakeScenarioKind.GpuVramPressure => CreateVramScenario(environment, markerTime),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>
    /// VRAM exhaustion looks nothing like GPU contention in the frame data: the frames are long but
    /// neither the CPU nor the GPU is doing work, because the time goes into the driver moving
    /// resources across PCIe. That is the signature this scenario reproduces.
    /// </summary>
    private static FakeScenario CreateVramScenario(EnvironmentMetadata environment, DateTimeOffset markerTime)
    {
        var events = new List<TelemetryEvent>();
        const ulong totalVram = 10UL * 1024 * 1024 * 1024;

        for (var offset = -30; offset <= 60; offset++)
        {
            var timestamp = markerTime.AddSeconds(offset);
            var stalling = offset is >= 0 and <= 6;
            var frameTime = stalling ? 120 + (offset * 20) : 8.3;

            events.Add(new FrameTelemetrySample(
                timestamp,
                frameTime,
                GpuBusyMs: stalling ? 2.1 : 7.4,
                DisplayLatencyMs: stalling ? 118 : 9,
                MsBetweenPresents: frameTime,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess",
                SwapChainLatencyMs: stalling ? 96 : 1.2,
                CpuBusyMs: stalling ? 1.8 : 6.9,
                CpuWaitMs: stalling ? 115 : 0.8,
                GpuWaitMs: stalling ? 3.2 : 0.4,
                GpuLatencyMs: stalling ? 110 : 2.1,
                FlipDelayMs: stalling ? 84 : 0.3,
                InputLatencyMs: stalling ? 140 : 12));

            var usedVram = stalling ? (ulong)(totalVram * 0.985) : (ulong)(totalVram * 0.93);
            events.Add(new GpuTelemetrySample(
                timestamp,
                IsAvailable: true,
                "NVIDIA GeForce RTX 3080",
                UtilizationPercent: stalling ? 41 : 96,
                MemoryBandwidthUtilizationPercent: stalling ? 88 : 62,
                UsedVramBytes: usedVram,
                TotalVramBytes: totalVram,
                EncoderUtilizationPercent: 38,
                DecoderUtilizationPercent: 0,
                TemperatureCelsius: 71,
                ThrottleReasons: []));

            events.Add(new SystemTelemetrySample(timestamp, 46, BuildCoreUsage(46, 58), 61, 9800, [], []));
            events.Add(new ProcessTelemetrySample(timestamp, 9152, "FiveM_b3407_GTAProcess", 34, 9L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024, 152, 4 * 1024 * 1024, 1 * 1024 * 1024));
            events.Add(new ObsTelemetrySample(timestamp, true, 60, 6.2, 40, 12, 11, 940, true, false));
        }

        return new FakeScenario("GPU VRAM pressure demo", environment, events, [], markerTime, IncidentSeverity.Severe);
    }

    private static Dictionary<string, double> BuildCoreUsage(double baseline, double peak)
    {
        var usage = new Dictionary<string, double>();
        for (var core = 0; core < 16; core++)
        {
            usage[core.ToString()] = core == 0 ? peak : baseline;
        }

        return usage;
    }

    private static FakeScenario CreateObsScenario(EnvironmentMetadata environment, DateTimeOffset markerTime)
    {
        var events = new List<TelemetryEvent>();
        for (var offset = -30; offset <= 60; offset++)
        {
            var timestamp = markerTime.AddSeconds(offset);
            var spiking = offset is >= 0 and <= 5;
            var spike = spiking ? 42 + (offset * 3) : 16.6;

            // GPU busy dominates the frame time: the encoder and the game are fighting over the GPU.
            events.Add(new FrameTelemetrySample(
                timestamp,
                spike,
                GpuBusyMs: spiking ? spike * 0.82 : 9.1,
                DisplayLatencyMs: 8 + Math.Max(offset, 0),
                MsBetweenPresents: spike,
                Dropped: spike > 30,
                ProcessName: "FiveM_b2944_GTAProcess",
                SwapChainLatencyMs: 1.4,
                CpuBusyMs: spiking ? spike * 0.14 : 6.2,
                CpuWaitMs: spiking ? spike * 0.6 : 1.1,
                GpuWaitMs: spiking ? 7.5 : 0.5,
                GpuLatencyMs: spiking ? spike * 0.9 : 3.2,
                FlipDelayMs: 0.4,
                InputLatencyMs: spiking ? 60 : 18));

            events.Add(new GpuTelemetrySample(
                timestamp,
                IsAvailable: true,
                "NVIDIA GeForce RTX 4070",
                UtilizationPercent: spiking ? 99 : 88,
                MemoryBandwidthUtilizationPercent: 70,
                UsedVramBytes: 7UL * 1024 * 1024 * 1024,
                TotalVramBytes: 12UL * 1024 * 1024 * 1024,
                EncoderUtilizationPercent: spiking ? 74 : 45,
                DecoderUtilizationPercent: 0,
                TemperatureCelsius: 68,
                ThrottleReasons: []));

            events.Add(new SystemTelemetrySample(timestamp, 62, new Dictionary<string, double> { ["0"] = 55, ["1"] = 61 }, 58, 14320, [new ProcessActivity("obs64", 8120, 21, 4 * 1024 * 1024)], [new ProcessActivity("obs64", 8120, 21, 4 * 1024 * 1024)]));
            events.Add(new ProcessTelemetrySample(timestamp, 9152, "FiveM_b2944_GTAProcess", 38, 6L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 148, 6 * 1024 * 1024, 2 * 1024 * 1024));
            events.Add(new ObsTelemetrySample(timestamp, true, 58, spiking ? 24 : 7, 120 + Math.Max(offset, 0), 18 + Math.Max(offset, 0), 12, 812, true, false));
        }

        return new FakeScenario("OBS/GPU contention demo", environment, events, [], markerTime, IncidentSeverity.Normal);
    }

    private static FakeScenario CreateResourceScenario(EnvironmentMetadata environment, DateTimeOffset markerTime)
    {
        var events = new List<TelemetryEvent>();
        for (var offset = -30; offset <= 60; offset++)
        {
            var timestamp = markerTime.AddSeconds(offset);
            var spiking = offset is >= -1 and <= 4;
            var spike = spiking ? 38 + (offset + 1) * 4 : 16.2;

            // CPU busy dominates and one core is pinned: FiveM's script thread, not the GPU.
            events.Add(new FrameTelemetrySample(
                timestamp,
                spike,
                GpuBusyMs: spiking ? 9.5 : 10,
                DisplayLatencyMs: 6,
                MsBetweenPresents: spike,
                Dropped: spike > 30,
                ProcessName: "FiveM_b2944_GTAProcess",
                SwapChainLatencyMs: 1.1,
                CpuBusyMs: spiking ? spike * 0.88 : 12.4,
                CpuWaitMs: spiking ? 2.2 : 1.4,
                GpuWaitMs: 0.6,
                GpuLatencyMs: 4.1,
                FlipDelayMs: 0.2,
                InputLatencyMs: spiking ? 55 : 20));

            events.Add(new SystemTelemetrySample(timestamp, 54, BuildCoreUsage(38, spiking ? 98 : 62), 52, 15800, [new ProcessActivity("FiveM_b2944_GTAProcess", 9152, spiking ? 67 : 32, 12 * 1024 * 1024)], [new ProcessActivity("FiveM_b2944_GTAProcess", 9152, 0, 12 * 1024 * 1024)]));
            events.Add(new ProcessTelemetrySample(timestamp, 9152, "FiveM_b2944_GTAProcess", spiking ? 72 : 31, 6L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, spiking ? 190 : 142, 12 * 1024 * 1024, 3 * 1024 * 1024));
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