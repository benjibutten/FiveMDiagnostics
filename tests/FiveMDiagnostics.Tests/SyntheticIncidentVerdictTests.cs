namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Fakes;

/// <summary>
/// The end-of-session ranking has to describe the incidents the session actually holds.
/// </summary>
/// <remarks>
/// A demo scenario added while a session is running takes the one path into the history that never went
/// through the analysis queue, so it was analysed, published and retained without ever reaching the
/// tally. The totals then disagreed with the incident list on screen — "3 incidenter" under a list of
/// five — which is the one thing a summary line must not do.
/// </remarks>
public sealed class SyntheticIncidentVerdictTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(
        Path.GetTempPath(),
        "FiveMDiagnosticsTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ASyntheticIncidentIsCountedInTheSessionRanking()
    {
        await using var manager = CreateManager();
        await manager.StartSessionAsync();

        manager.AddSyntheticIncident(FakeScenarioGenerator.Create(FakeScenarioKind.NetworkIssue).ToIncidentRecord());
        manager.AddSyntheticIncident(FakeScenarioGenerator.Create(FakeScenarioKind.ObsGpuContention).ToIncidentRecord());

        await manager.StopSessionAsync();

        var ranking = manager.GetStatusEntries().FirstOrDefault(entry => entry.Source == "Analysis.Verdicts");

        Assert.NotNull(ranking);
        Assert.Contains("över 2 incidenter", ranking!.Message, StringComparison.Ordinal);
    }

    private DiagnosticsSessionManager CreateManager()
    {
        var settings = DiagnosticsSettings.CreateDefault();
        settings.WorkingDirectory = _workingDirectory;

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
