namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// A trace nothing parses is a file, not evidence.
/// </summary>
/// <remarks>
/// The rule that a deep capture overrules <c>MsCPUBusy</c> has been implemented for three sessions and
/// has fired in none of them. The reason turned out to be one line: an automatic capture's ETL was
/// attached to the incident and never handed to a parser, so the analysis it was taken for never saw
/// it. The session of 27 August wrote five ETLs and not one of its 154 incidents carried a single line
/// of ETL evidence, while 151 of them were ranked as script spikes — including the freeze whose trace
/// shows the main thread off the processor for 178.0 of its 178 ms.
/// <para>
/// End-to-end on purpose. Every piece of this worked in isolation; what did not work was the seam.
/// </para>
/// </remarks>
public sealed class AutomaticCaptureAnalysisTests : IDisposable
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
    public async Task ACaptureTheAppTookItselfReachesTheIncidentAnalysis()
    {
        var parser = new StubEtlParser();
        var incident = await RunSessionAsync(parser);

        Assert.True(parser.Parsed, "the ETL the app wrote was never handed to a parser");
        var traceEvidence = incident.Events.OfType<ArtifactEvidence>().Where(item => item.Kind == ArtifactKind.EtlTrace).ToArray();
        Assert.Equal(2, traceEvidence.Length);
        Assert.Contains(traceEvidence, item => item.Summary.Contains("Schemaläggning", StringComparison.Ordinal));
        Assert.Contains(traceEvidence, item => item.Summary.Contains("CPU-sampling", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the point of it reaching the analysis: the verdict changes. The window looks entirely
    /// CPU-bound to PresentMon, which is what a blocked thread looks like to PresentMon.
    /// </summary>
    [Fact]
    public async Task TheTraceOverrulesTheCpuBoundAttribution()
    {
        var incident = await RunSessionAsync(new StubEtlParser());

        Assert.NotNull(incident.Analysis);
        Assert.Equal(RootCauseCategory.FiveMThreadWait, incident.Analysis!.Hypotheses[0].Category);
    }

    /// <summary>
    /// The escape hatch. Reading a 900 MB ETL costs a burst of CPU while the session is still running,
    /// so it has to be possible to go back to importing traces by hand.
    /// </summary>
    [Fact]
    public async Task TurningTheAnalysisOffLeavesTheTraceOnDisk()
    {
        var parser = new StubEtlParser();
        var incident = await RunSessionAsync(parser, analyzeAutomaticCaptures: false);

        Assert.False(parser.Parsed);
        Assert.DoesNotContain(incident.Events.OfType<ArtifactEvidence>(), item => item.Kind == ArtifactKind.EtlTrace);
        Assert.Single(Directory.GetFiles(_workingDirectory, "*.etl"));
    }

    private async Task<IncidentRecord> RunSessionAsync(StubEtlParser parser, bool analyzeAutomaticCaptures = true)
    {
        Directory.CreateDirectory(_workingDirectory);
        var capturePath = Path.Combine(_workingDirectory, "deep_20260827_224500.etl");
        await File.WriteAllTextAsync(capturePath, "not really an ETL; the parser is a stub");

        var settings = DiagnosticsSettings.CreateDefault();
        settings.WorkingDirectory = _workingDirectory;
        settings.ExportDirectory = Path.Combine(_workingDirectory, "Exports");
        settings.FramePacing.Enabled = false;
        settings.DeepCapture.Enabled = true;
        settings.DeepCapture.AnalyzeAutomaticCaptures = analyzeAutomaticCaptures;

        var collector = new OneHitchCollector(frameTimeMs: 178);

        await using var manager = new DiagnosticsSessionManager(
            settings,
            new StubEnvironmentMetadataProvider(),
            new FiveMCorrelationEngine(),
            new StubIncidentExporter(),
            new CapturingDeepCaptureService(capturePath),
            collectors: [collector],
            artifactParsers: [parser],
            new StubProcessResolver());

        await manager.StartSessionAsync();
        await collector.Completed;
        await manager.StopSessionAsync();

        return Assert.Single(manager.GetRecentIncidents());
    }

    /// <summary>
    /// One freeze on an otherwise healthy timeline, with the numbers PresentMon reported for the one of
    /// 28 August: 178 ms of frame time, essentially all of it counted as CPU busy.
    /// </summary>
    private sealed class OneHitchCollector(double frameTimeMs) : ITelemetryCollector
    {
        private const double HealthyFrameMs = 16.67;
        private static readonly DateTimeOffset Origin = new(2026, 8, 28, 0, 45, 0, TimeSpan.Zero);

        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "OneHitch";

        public Task Completed => _completed.Task;

        /// <summary>When the hitch was presented, so the stub trace can overlap it.</summary>
        public static DateTimeOffset HitchAt => Origin.AddSeconds(10);

        public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
        {
            var totalFrames = (int)(100 * 1000 / HealthyFrameMs);
            var hitchIndex = (int)(10 * 1000 / HealthyFrameMs);

            for (var index = 0; index < totalFrames; index++)
            {
                var isHitch = index == hitchIndex;
                var frameTime = isHitch ? frameTimeMs : HealthyFrameMs;

                await context.Writer.WriteAsync(
                    new FrameTelemetrySample(
                        isHitch ? HitchAt : Origin.AddMilliseconds(index * HealthyFrameMs),
                        frameTime,
                        GpuBusyMs: 7.5,
                        DisplayLatencyMs: null,
                        MsBetweenPresents: frameTime,
                        Dropped: false,
                        ProcessName: "FiveM_b3407_GTAProcess.exe",
                        CpuBusyMs: Math.Max(frameTime - 0.4, 0),
                        CpuWaitMs: 0.4),
                    cancellationToken).ConfigureAwait(false);
            }

            _completed.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A capture service that records nothing and names a file that exists.</summary>
    private sealed class CapturingDeepCaptureService(string capturePath) : IDeepCaptureService
    {
        public Task<DeepCaptureResult> StartRingBufferAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new DeepCaptureResult(true, false, "ringbuffert igång"));

        public Task StopRingBufferAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new DeepCaptureResult(true, false, "capture sparad", capturePath));
    }

    /// <summary>
    /// What the real parser reports for a freeze like this: the game's own thread off the processor for
    /// the whole frame.
    /// </summary>
    private sealed class StubEtlParser : IArtifactParser
    {
        public bool Parsed { get; private set; }

        public bool CanParse(string path) => Path.GetExtension(path).Equals(".etl", StringComparison.OrdinalIgnoreCase);

        public Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
        {
            Parsed = true;
            var hitch = OneHitchCollector.HitchAt;

            return Task.FromResult<ArtifactParseResult?>(new ArtifactParseResult(
                new ArtifactAttachment(path, ArtifactKind.EtlTrace, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
                [new ArtifactEvidence(
                    DateTimeOffset.UtcNow,
                    ArtifactKind.EtlTrace,
                    "Schemaläggning: aktiv GTA-tråd låg av CPU:n i 178,0 ms.",
                    new Dictionary<string, double>
                    {
                        ["gameThreadWaitThreadId"] = 18688,
                        ["gameThreadLongWaitCount"] = 1,
                        ["gameThreadUserRequestWaitCount"] = 1,
                        ["gameThreadMaxWaitMs"] = 178,
                        ["gameThreadWaitIntervalCount"] = 1,
                        ["gameThreadWait0StartUnixMs"] = hitch.ToUnixTimeMilliseconds(),
                        ["gameThreadWait0EndUnixMs"] = hitch.AddMilliseconds(178).ToUnixTimeMilliseconds(),
                        ["gameThreadWait0DurationMs"] = 178,
                        ["gameThreadWait0UserRequest"] = 1,
                    },
                    path),
                 new ArtifactEvidence(
                    DateTimeOffset.UtcNow,
                    ArtifactKind.EtlTrace,
                    "CPU-sampling: GTAProcess höll 0,42 kärnor.",
                    new Dictionary<string, double>
                    {
                        ["cpuSampleCount"] = 16_000,
                        ["cpuSubjectIsGame"] = 1,
                        ["cpuSubjectProcessCores"] = 0.42,
                    },
                    path)],
                []));
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

    private sealed class StubProcessResolver : ITargetProcessResolver
    {
        public TargetProcessInfo? TryGetTargetProcess()
            => new(1234, "FiveM_b3407_GTAProcess", null, DateTimeOffset.UtcNow);
    }
}
