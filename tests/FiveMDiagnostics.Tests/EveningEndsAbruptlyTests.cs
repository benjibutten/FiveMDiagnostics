namespace FiveMDiagnostics.Tests;

using System.Collections.Concurrent;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// 2026-09-13 03:36: the game exited at :21, the app was gone at :25, and the half-hour table, the OBS
/// footprint and every other closing line went with it.
/// </summary>
public sealed class EveningEndsAbruptlyTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 13, 1, 33, 0, TimeSpan.Zero);

    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<DiagnosticStatusEntry> _statuses = new();

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    /// <summary>The stream stopped 03:33:18 and the line waited for a summary that never came due.</summary>
    [Fact]
    public async Task TheObsFootprintIsWrittenWhenTheStepCompletes()
    {
        var collector = new ReplayCollector(ObsStreamStops());
        await using var manager = CreateManager(collector, teardownDelay: TimeSpan.Zero);

        await manager.StartSessionAsync();
        var line = await WaitForAsync("Obs.VramFootprint", TimeSpan.FromSeconds(10));

        Assert.Contains("strömmen stoppades", line, StringComparison.Ordinal);
        Assert.Contains("encodern", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A collector that takes its time stopping stands in for PresentMon and the WPR teardown, which is
    /// what the app's two-second exit budget used to run out on before a single summary was written.
    /// </summary>
    [Fact]
    public async Task TheSummariesAreWrittenBeforeASlowTeardown()
    {
        var collector = new ReplayCollector(Frames());
        await using var manager = CreateManager(collector, teardownDelay: TimeSpan.FromSeconds(5));

        await manager.StartSessionAsync();
        await collector.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        var stopping = manager.StopSessionAsync();
        var table = await WaitForAsync("Session.HalfHours", TimeSpan.FromSeconds(2));

        Assert.StartsWith("Halvtimmar", table, StringComparison.Ordinal);
        Assert.False(stopping.IsCompleted, "the teardown should still be running when the table is on disk");

        // A second caller, the way Windows shutting down and the tray's Exit can overlap, joins the stop.
        Assert.Same(stopping, manager.StopSessionAsync());
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task<string> WaitForAsync(string source, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_statuses.FirstOrDefault(entry => entry.Source == source) is { } entry)
            {
                return entry.Message;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Ingen {source}-rad inom {timeout.TotalSeconds:F0} s.");
    }

    private static IEnumerable<TelemetryEvent> ObsStreamStops()
    {
        for (var halfSeconds = 0; halfSeconds < 80; halfSeconds++)
        {
            var at = Start.AddMilliseconds(halfSeconds * 500);
            var streaming = halfSeconds < 36;

            if (halfSeconds % 2 == 0)
            {
                yield return new ObsTelemetrySample(at, true, 60, 0.7, 11, 0, 3.4, 900, IsStreaming: streaming, IsRecording: false, IsProcessRunning: true);
            }

            yield return Adapter(at, streaming ? 86.2 : 83.9);
        }
    }

    private static IEnumerable<TelemetryEvent> Frames()
    {
        for (var index = 0; index < 1200; index++)
        {
            yield return new FrameTelemetrySample(
                Start.AddMilliseconds(index * 16.67),
                16.67,
                GpuBusyMs: 4.9,
                DisplayLatencyMs: null,
                MsBetweenPresents: 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 7.8,
                CpuWaitMs: 8.8);
        }
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset at, double percent)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;
        return new GpuTelemetrySample(at, true, "NVIDIA GeForce RTX 3080", 40, 15, (ulong)(Total * percent / 100), Total, 0, 0, 58, [], AdapterCount: 1);
    }

    private DiagnosticsSessionManager CreateManager(ReplayCollector collector, TimeSpan teardownDelay)
    {
        collector.TeardownDelay = teardownDelay;

        var settings = DiagnosticsSettings.CreateDefault();
        settings.WorkingDirectory = _workingDirectory;
        settings.ExportDirectory = Path.Combine(_workingDirectory, "Exports");
        settings.DeepCapture.Enabled = false;
        settings.AutoDetect.Enabled = false;

        var manager = new DiagnosticsSessionManager(
            settings,
            new StubEnvironmentMetadataProvider(),
            new FiveMCorrelationEngine(),
            new StubIncidentExporter(),
            new StubDeepCaptureService(),
            [collector],
            artifactParsers: [],
            new StubProcessResolver());

        manager.StatusReported += (_, entry) => _statuses.Enqueue(entry);
        return manager;
    }

    /// <summary>Writes its events as fast as the channel takes them, then waits to be stopped.</summary>
    private sealed class ReplayCollector(IEnumerable<TelemetryEvent> events) : ITelemetryCollector
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "Replay";

        public Task Completed => _completed.Task;

        public TimeSpan TeardownDelay { get; set; }

        public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
        {
            foreach (var item in events)
            {
                await context.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }

            _completed.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TeardownDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private sealed class StubEnvironmentMetadataProvider : IEnvironmentMetadataProvider
    {
        public Task<EnvironmentMetadata> CollectAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new EnvironmentMetadata("Windows 11", "CPU", 16UL * 1024 * 1024 * 1024, "GPU", null, 60, null, false, string.Empty, DateTimeOffset.UtcNow, null));
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
