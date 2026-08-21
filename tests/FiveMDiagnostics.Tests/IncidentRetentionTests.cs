namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Fakes;

/// <summary>
/// MaxIncidentsPerSession only bounds the detector, and only within one session. Completed incidents
/// used to accumulate for the lifetime of the process, each holding its full 90 second event window.
/// </summary>
public sealed class IncidentRetentionTests
{
    [Fact]
    public async Task IncidentHistory_StopsAtTheRetentionCap()
    {
        await using var manager = CreateManager(maxRetainedIncidents: 3);
        var evicted = new List<IncidentRecord>();
        manager.IncidentsEvicted += (_, records) => evicted.AddRange(records);

        var added = new List<IncidentRecord>();
        for (var index = 0; index < 5; index++)
        {
            added.Add(manager.AddSyntheticIncident(FakeScenarioGenerator.Create(FakeScenarioKind.NetworkIssue).ToIncidentRecord()));
        }

        var retained = manager.GetRecentIncidents();

        Assert.Equal(3, retained.Count);
        Assert.Equal([added[0].Id, added[1].Id], evicted.Select(item => item.Id));
        Assert.DoesNotContain(retained, item => item.Id == added[0].Id);
        Assert.Contains(retained, item => item.Id == added[4].Id);
    }

    [Fact]
    public async Task RetentionCap_IsClampedWhenSettingsAskForZero()
    {
        await using var manager = CreateManager(maxRetainedIncidents: 0);

        manager.AddSyntheticIncident(FakeScenarioGenerator.Create(FakeScenarioKind.NetworkIssue).ToIncidentRecord());
        manager.AddSyntheticIncident(FakeScenarioGenerator.Create(FakeScenarioKind.NetworkIssue).ToIncidentRecord());

        Assert.Single(manager.GetRecentIncidents());
    }

    private static DiagnosticsSessionManager CreateManager(int maxRetainedIncidents)
    {
        var settings = DiagnosticsSettings.CreateDefault();
        settings.MaxRetainedIncidents = maxRetainedIncidents;

        return new DiagnosticsSessionManager(
            settings,
            new StubEnvironmentMetadataProvider(),
            new FiveMCorrelationEngine(),
            new StubIncidentExporter(),
            new StubDeepCaptureService(),
            collectors: [],
            artifactParsers: [],
            new StubProcessResolver());
    }

    private sealed class StubEnvironmentMetadataProvider : IEnvironmentMetadataProvider
    {
        public Task<EnvironmentMetadata> CollectAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
        {
            return Task.FromResult(new EnvironmentMetadata(
                "Windows 11",
                "CPU",
                16UL * 1024 * 1024 * 1024,
                "GPU",
                null,
                60,
                null,
                false,
                settings.ServerProfile.Name,
                DateTimeOffset.UtcNow,
                null));
        }
    }

    private sealed class StubIncidentExporter : IIncidentExporter
    {
        public Task<string> ExportAsync(IncidentRecord incident, ExportBundleOptions options, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
    }

    private sealed class StubDeepCaptureService : IDeepCaptureService
    {
        public Task<DeepCaptureResult> StartRingBufferAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new DeepCaptureResult(false, false, "stub"));

        public Task StopRingBufferAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new DeepCaptureResult(false, false, "stub"));
    }

    private sealed class StubProcessResolver : ITargetProcessResolver
    {
        public TargetProcessInfo? TryGetTargetProcess() => null;
    }
}
