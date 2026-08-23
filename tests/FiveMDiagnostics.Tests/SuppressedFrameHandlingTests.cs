using System.Text.Json;

namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// End-to-end cover for what happens to a frame the detector classified but refused to act on.
/// </summary>
/// <remarks>
/// These run a real session so the detector, the materializer and the capture budget are wired together
/// exactly as they are in the app. The unit tests around each piece all passed while the seam between
/// them dropped frames on the floor, which is the failure mode worth guarding against here.
/// </remarks>
public sealed class SuppressedFrameHandlingTests : IDisposable
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

    /// <summary>
    /// The dead zone between the incident window closing and the cooldown expiring.
    /// </summary>
    /// <remarks>
    /// The cooldown defaults to two minutes and an incident window closes sixty seconds after its marker,
    /// so roughly a minute of every cycle has no window open and a detector that will not raise one. A
    /// catastrophic frame landing there produced nothing at all — no incident, no trace, no journal line.
    /// </remarks>
    [Fact]
    public async Task ACatastrophicFrameInTheCooldownDeadZoneStillGetsAnIncident()
    {
        var settings = CreateSettings();
        var collector = new ScriptedFrameCollector(
        [
            // Opens an incident. Its window ends at 70 s; the cooldown runs to 130 s.
            new Hitch(AtSecond: 10, FrameTimeMs: 40),

            // At 100 s nothing is open any more, and the detector is still inside its cooldown.
            new Hitch(AtSecond: 100, FrameTimeMs: 2846),
        ]);

        await using var manager = CreateManager(settings, collector);
        await manager.StartSessionAsync();
        await collector.Completed;
        await manager.StopSessionAsync();

        var labels = IncidentLabels();

        Assert.Contains(labels, label => label.Contains("2846 ms", StringComparison.Ordinal));
        Assert.Equal(2, labels.Count);
    }

    /// <summary>
    /// The same dead zone, for a frame that is merely a spike. Overriding the cooldown for these would
    /// roughly double the incident count for no gain, so they stay suppressed.
    /// </summary>
    [Fact]
    public async Task AnOrdinarySpikeInTheDeadZoneStaysQuiet()
    {
        var settings = CreateSettings();
        var collector = new ScriptedFrameCollector(
        [
            new Hitch(AtSecond: 10, FrameTimeMs: 40),
            new Hitch(AtSecond: 100, FrameTimeMs: 45),
        ]);

        await using var manager = CreateManager(settings, collector);
        await manager.StartSessionAsync();
        await collector.Completed;
        await manager.StopSessionAsync();

        Assert.Single(IncidentLabels());
    }

    /// <summary>
    /// Inside the window the frame folds into the incident already open rather than opening a second one.
    /// </summary>
    [Fact]
    public async Task AWorseFrameInsideTheWindowEscalatesInsteadOfDuplicating()
    {
        var settings = CreateSettings();
        var collector = new ScriptedFrameCollector(
        [
            new Hitch(AtSecond: 10, FrameTimeMs: 41),
            new Hitch(AtSecond: 19, FrameTimeMs: 1018),
        ]);

        await using var manager = CreateManager(settings, collector);
        await manager.StartSessionAsync();
        await collector.Completed;
        await manager.StopSessionAsync();

        var label = Assert.Single(IncidentLabels());
        Assert.Contains("1018 ms", label);
    }

    /// <summary>
    /// The budget is a ceiling, not a spacing rule, so the dead-zone fallback must not walk around it.
    /// </summary>
    /// <remarks>
    /// Cooldown and exhausted budget both suppress a trigger, and treating them as one thing let severe
    /// frames keep raising incidents long after MaxIncidentsPerWindow was spent — defeating the only
    /// mechanism that bounds how many incidents a bad hour can produce.
    /// </remarks>
    [Fact]
    public async Task TheIncidentBudgetCannotBeBypassedByTheDeadZoneFallback()
    {
        var settings = CreateSettings();
        settings.AutoDetect.MaxIncidentsPerWindow = 2;
        settings.AutoDetect.IncidentBudgetWindow = TimeSpan.FromHours(1);

        // Severe frames, spaced far enough apart that the cooldown never suppresses them and each one
        // lands with no window open. Only the budget can stop these.
        var hitches = Enumerable.Range(0, 6)
            .Select(index => new Hitch(AtSecond: 10 + (index * 180), FrameTimeMs: 2000))
            .ToArray();

        var collector = new ScriptedFrameCollector(hitches);
        await using var manager = CreateManager(settings, collector);
        await manager.StartSessionAsync();
        await collector.Completed;
        await manager.StopSessionAsync();

        Assert.Equal(2, IncidentLabels().Count);
    }

    /// <summary>
    /// A run of frames that never reached the screen is a visible freeze, and its frame times are
    /// ordinary by construction — so a fallback that judges worth by milliseconds drops it.
    /// </summary>
    [Fact]
    public async Task AFreezeInTheDeadZoneIsNotJudgedByItsFrameTime()
    {
        var settings = CreateSettings();
        var collector = new ScriptedFrameCollector(
        [
            new Hitch(AtSecond: 10, FrameTimeMs: 40),
        ],
        DroppedRunAtSecond: 100);

        await using var manager = CreateManager(settings, collector);
        await manager.StartSessionAsync();
        await collector.Completed;
        await manager.StopSessionAsync();

        Assert.Contains(IncidentLabels(), label => label.Contains("aldrig skärmen", StringComparison.Ordinal));
    }

    private IReadOnlyList<string> IncidentLabels()
    {
        var path = Directory.GetFiles(_workingDirectory, "session_*.jsonl").Single();

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(line => line.GetProperty("type").GetString() == "incident")
            .Select(line => line.GetProperty("payload").GetProperty("label").GetString() ?? string.Empty)
            .ToArray();
    }

    private DiagnosticsSettings CreateSettings()
    {
        var settings = DiagnosticsSettings.CreateDefault();
        settings.WorkingDirectory = _workingDirectory;
        settings.ExportDirectory = Path.Combine(_workingDirectory, "Exports");
        settings.DeepCapture.Enabled = false;

        // Frame pacing needs whole minutes of frames to classify anything and would only add noise here.
        settings.FramePacing.Enabled = false;
        return settings;
    }

    private static DiagnosticsSessionManager CreateManager(DiagnosticsSettings settings, ITelemetryCollector collector)
    {
        return new DiagnosticsSessionManager(
            settings,
            new StubEnvironmentMetadataProvider(),
            new FiveMCorrelationEngine(),
            new StubIncidentExporter(),
            new StubDeepCaptureService(),
            collectors: [collector],
            artifactParsers: [],
            new StubProcessResolver());
    }

    private readonly record struct Hitch(double AtSecond, double FrameTimeMs);

    /// <summary>
    /// Plays a steady 60 fps timeline with hitches dropped in at chosen offsets.
    /// </summary>
    /// <remarks>
    /// Timestamps come from the script rather than the clock, so the cooldown and the incident window are
    /// exercised at their real durations without the test taking minutes to run. The trailing run of
    /// healthy frames pushes the finalizer past the last incident window so every incident completes.
    /// </remarks>
    private sealed class ScriptedFrameCollector(IReadOnlyList<Hitch> hitches, double? DroppedRunAtSecond = null) : ITelemetryCollector
    {
        private const double HealthyFrameMs = 16.67;
        private static readonly DateTimeOffset Origin = new(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "ScriptedFrames";

        public Task Completed => _completed.Task;

        public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
        {
            // Scripts start at ten seconds because the detector will not fire until it has seen
            // AutoDetect.MinimumSamples frames — an earlier hitch is silently ignored, which quietly
            // turned two of these tests green for the wrong reason while they were being written.
            var lastHitchSecond = Math.Max(hitches[^1].AtSecond, DroppedRunAtSecond ?? 0);
            var lastSecond = lastHitchSecond + context.Settings.PostIncidentWindow.TotalSeconds + 30;
            var totalFrames = (int)(lastSecond * 1000 / HealthyFrameMs);
            var pending = new Queue<Hitch>(hitches.OrderBy(item => item.AtSecond));

            for (var index = 0; index < totalFrames; index++)
            {
                var elapsedMs = index * HealthyFrameMs;
                var isHitch = pending.Count > 0 && elapsedMs >= pending.Peek().AtSecond * 1000;
                var frameTimeMs = isHitch ? pending.Dequeue().FrameTimeMs : HealthyFrameMs;

                // A short run of frames that present on time but never reach the screen. The frame times
                // stay at the healthy cadence, which is exactly what makes this case interesting.
                var dropped = DroppedRunAtSecond is { } freezeAt
                    && elapsedMs >= freezeAt * 1000
                    && elapsedMs < (freezeAt * 1000) + (4 * HealthyFrameMs);

                await context.Writer.WriteAsync(
                    new FrameTelemetrySample(
                        Origin.AddMilliseconds(elapsedMs),
                        frameTimeMs,
                        GpuBusyMs: 7.5,
                        DisplayLatencyMs: null,
                        MsBetweenPresents: frameTimeMs,
                        Dropped: dropped,
                        ProcessName: "FiveM_b3407_GTAProcess.exe",
                        CpuBusyMs: Math.Max(frameTimeMs - 7.7, 0),
                        CpuWaitMs: 7.7),
                    cancellationToken).ConfigureAwait(false);
            }

            _completed.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StubEnvironmentMetadataProvider : IEnvironmentMetadataProvider
    {
        public Task<EnvironmentMetadata> CollectAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new EnvironmentMetadata(
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
        public TargetProcessInfo? TryGetTargetProcess()
            => new(1234, "FiveM_b3407_GTAProcess", null, DateTimeOffset.UtcNow);
    }
}
