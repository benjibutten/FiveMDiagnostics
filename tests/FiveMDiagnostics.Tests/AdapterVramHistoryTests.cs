namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// The occupancy a trace is interpreted against belongs to this session, and to this card.
/// </summary>
/// <remarks>
/// The history exists so a finished ETL can be told whether the driver was moving memory because the
/// card was full. Two things make that answer wrong rather than absent: a reading from an adapter the
/// game does not render on, and a reading from an evening that is over. The parsers are constructed
/// once and outlive every session on them, so both are reachable from the app as it is used — a quick
/// restart, or an ETL imported by hand after the session was stopped.
/// </remarks>
public sealed class AdapterVramHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 2, 21, 0, 0, TimeSpan.Zero);

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
    /// NVML reports device index 0 for the whole session while the trace describes whichever adapter
    /// the game renders on. With a second device present those need not be the same card, and the
    /// occupancy that decides an eviction verdict is not allowed to come from the other one.
    /// </summary>
    [Fact]
    public async Task OnlyAConfirmedSingleAdapterMachineFillsTheHistory()
    {
        var parser = new VramAwareStubParser();
        var collector = new GpuSampleCollector(
            [Sample(Origin, percent: 20, adapterCount: 2),
             Sample(Origin.AddSeconds(1), percent: 20, adapterCount: 2),
             Sample(Origin.AddSeconds(2), percent: 20, adapterCount: null),
             Sample(Origin.AddSeconds(3), percent: 90, adapterCount: 1)]);

        await using var manager = CreateManager(parser, collector);
        await manager.StartSessionAsync();
        await collector.Completed;

        var measured = await WaitForReadingAsync(parser);

        // 90 is the one sample that named a single adapter. A 20 here would be the other card.
        Assert.Equal(90, measured);

        await manager.StopSessionAsync();
    }

    /// <summary>
    /// Stopping takes the probe away and forgets what it was reading from. Either half left behind
    /// answers the next trace with the previous session's card.
    /// </summary>
    [Fact]
    public async Task StoppingTheSessionTakesTheHistoryWithIt()
    {
        var parser = new VramAwareStubParser();
        var collector = new GpuSampleCollector([Sample(Origin, percent: 90, adapterCount: 1)]);

        await using var manager = CreateManager(parser, collector);
        await manager.StartSessionAsync();
        await collector.Completed;
        await WaitForReadingAsync(parser);
        await manager.StopSessionAsync();

        // An ETL imported by hand between two sessions has nothing to be interpreted against.
        Assert.Null(parser.AdapterVramPercent);

        // And the next session starts empty rather than inheriting the last one's occupancy: this
        // collector has already emitted everything it has, so a reading here came from the evening
        // that ended above.
        await manager.StartSessionAsync();
        Assert.NotNull(parser.AdapterVramPercent);
        Assert.Null(parser.AdapterVramPercent!.Invoke());

        await manager.StopSessionAsync();
    }

    /// <summary>
    /// The window mode explains the compositor for the session that read it, and for no other. The
    /// analysis engine is the same instance the next session gets.
    /// </summary>
    [Fact]
    public async Task TheComposedPresentExplanationDoesNotOutliveTheSession()
    {
        var engine = new FiveMCorrelationEngine { ComposedPresentExplainedAt = _ => true };
        var collector = new GpuSampleCollector([]);

        await using var manager = CreateManager(new VramAwareStubParser(), collector, engine);
        await manager.StartSessionAsync();
        await manager.StopSessionAsync();

        Assert.Null(engine.ComposedPresentExplainedAt);
    }

    /// <summary>
    /// Polls until the probe has an answer, which is as soon as the pump has read the samples the
    /// collector wrote. Fails rather than hangs when it never gets one.
    /// </summary>
    private static async Task<double> WaitForReadingAsync(VramAwareStubParser parser)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (parser.AdapterVramPercent?.Invoke() is { } reading)
            {
                return reading;
            }

            await Task.Delay(10);
        }

        Assert.Fail("the adapter occupancy never reached the trace analysis");
        return 0;
    }

    private DiagnosticsSessionManager CreateManager(
        VramAwareStubParser parser,
        GpuSampleCollector collector,
        FiveMCorrelationEngine? engine = null)
    {
        Directory.CreateDirectory(_workingDirectory);

        var settings = DiagnosticsSettings.CreateDefault();
        settings.WorkingDirectory = _workingDirectory;
        settings.ExportDirectory = Path.Combine(_workingDirectory, "Exports");
        settings.FramePacing.Enabled = false;
        settings.DeepCapture.Enabled = false;

        return new DiagnosticsSessionManager(
            settings,
            new StubEnvironmentMetadataProvider(),
            engine ?? new FiveMCorrelationEngine(),
            new StubIncidentExporter(),
            new StubDeepCaptureService(),
            collectors: [collector],
            artifactParsers: [parser],
            new StubProcessResolver());
    }

    private static GpuTelemetrySample Sample(DateTimeOffset timestamp, double percent, int? adapterCount)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 41,
            MemoryBandwidthUtilizationPercent: 15,
            UsedVramBytes: (ulong)(Total * percent / 100),
            TotalVramBytes: Total,
            EncoderUtilizationPercent: 0,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: adapterCount);
    }

    /// <summary>Writes its samples once, then stays alive until the session is stopped.</summary>
    private sealed class GpuSampleCollector(IReadOnlyList<GpuTelemetrySample> samples) : ITelemetryCollector
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _runs;

        public string Name => "GpuSamples";

        public Task Completed => _completed.Task;

        public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                foreach (var sample in samples)
                {
                    await context.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                _completed.TrySetResult();
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A parser that parses nothing and only holds the probe the session hands it.</summary>
    private sealed class VramAwareStubParser : IArtifactParser, IVramAwareTraceAnalysis
    {
        public Func<double?>? AdapterVramPercent { get; set; }

        public bool CanParse(string path) => false;

        public Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<ArtifactParseResult?>(null);
    }

    private sealed class StubEnvironmentMetadataProvider : IEnvironmentMetadataProvider
    {
        public Task<EnvironmentMetadata> CollectAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
        {
            return Task.FromResult(new EnvironmentMetadata(
                "Windows 11",
                "CPU",
                16UL * 1024 * 1024 * 1024,
                "NVIDIA GeForce RTX 3080",
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
