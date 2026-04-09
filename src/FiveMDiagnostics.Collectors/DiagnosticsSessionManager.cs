using System.Threading.Channels;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

public sealed class DiagnosticsSessionManager : IDiagnosticStatusSink, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly DiagnosticsSettings _settings;
    private readonly IEnvironmentMetadataProvider _environmentMetadataProvider;
    private readonly IAnalysisEngine _analysisEngine;
    private readonly IIncidentExporter _incidentExporter;
    private readonly IDeepCaptureService _deepCaptureService;
    private readonly IReadOnlyList<ITelemetryCollector> _collectors;
    private readonly IReadOnlyList<IArtifactParser> _artifactParsers;
    private readonly ITargetProcessResolver _processResolver;

    private readonly List<DiagnosticStatusEntry> _statusEntries = [];
    private readonly List<IncidentRecord> _incidents = [];
    private readonly List<ArtifactAttachment> _attachments = [];

    private CancellationTokenSource? _sessionCts;
    private Channel<TelemetryEvent>? _channel;
    private TimeWindowRingBuffer<TelemetryEvent>? _ringBuffer;
    private IncidentMaterializer? _incidentMaterializer;
    private Task? _pumpTask;
    private Task? _finalizeTask;
    private Task[] _collectorTasks = [];
    private volatile bool _isSessionActive;

    public DiagnosticsSessionManager(
        DiagnosticsSettings settings,
        IEnvironmentMetadataProvider environmentMetadataProvider,
        IAnalysisEngine analysisEngine,
        IIncidentExporter incidentExporter,
        IDeepCaptureService deepCaptureService,
        IEnumerable<ITelemetryCollector> collectors,
        IEnumerable<IArtifactParser> artifactParsers,
        ITargetProcessResolver? processResolver = null)
    {
        _settings = settings;
        _environmentMetadataProvider = environmentMetadataProvider;
        _analysisEngine = analysisEngine;
        _incidentExporter = incidentExporter;
        _deepCaptureService = deepCaptureService;
        _collectors = collectors.ToArray();
        _artifactParsers = artifactParsers.ToArray();
        _processResolver = processResolver ?? new FiveMTargetProcessResolver();
    }

    public event EventHandler? StateChanged;

    public event EventHandler<DiagnosticStatusEntry>? StatusReported;

    public event EventHandler<IncidentRecord>? IncidentCompleted;

    public bool IsSessionActive => _isSessionActive;

    public DiagnosticsSettings Settings => _settings;

    public EnvironmentMetadata? Environment { get; private set; }

    public TargetProcessInfo? ActiveProcess => _processResolver.TryGetTargetProcess();

    public IncidentRecord? LatestIncident
    {
        get
        {
            lock (_sync)
            {
                return _incidents.LastOrDefault();
            }
        }
    }

    public IReadOnlyList<IncidentRecord> GetRecentIncidents()
    {
        lock (_sync)
        {
            return _incidents.OrderByDescending(item => item.Marker.MarkedAt).ToArray();
        }
    }

    public IReadOnlyList<DiagnosticStatusEntry> GetStatusEntries()
    {
        lock (_sync)
        {
            return _statusEntries.OrderByDescending(item => item.Timestamp).ToArray();
        }
    }

    public async Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        if (IsSessionActive)
        {
            return;
        }

        Environment = await _environmentMetadataProvider.CollectAsync(_settings, cancellationToken).ConfigureAwait(false);
        _ringBuffer = new TimeWindowRingBuffer<TelemetryEvent>(_settings.RingBufferRetention, item => item.Timestamp);
        _incidentMaterializer = new IncidentMaterializer(_ringBuffer, _settings.PreIncidentWindow, _settings.PostIncidentWindow);
        _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(32768)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new CollectorContext(_channel.Writer, _settings, this, _processResolver, () => DateTimeOffset.UtcNow);

        _pumpTask = Task.Run(() => PumpAsync(_channel.Reader, _sessionCts.Token));
        _finalizeTask = Task.Run(() => FinalizeLoopAsync(_sessionCts.Token));
        _collectorTasks = _collectors.Select(collector => Task.Run(() => RunCollectorSafeAsync(collector, context, _sessionCts.Token))).ToArray();
        _isSessionActive = true;

        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Session started for profile '{_settings.ServerProfile.Name}'.");
        OnStateChanged();
    }

    public async Task StopSessionAsync()
    {
        if (!IsSessionActive)
        {
            return;
        }

        var cancellationTokenSource = _sessionCts;
        var writer = _channel?.Writer;
        cancellationTokenSource?.Cancel();

        try
        {
            await Task.WhenAll(_collectorTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        writer?.TryComplete();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_finalizeTask is not null)
        {
            try
            {
                await _finalizeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (Environment is not null)
        {
            Environment = Environment with { SessionEndedAt = DateTimeOffset.UtcNow };
        }

        FlushPendingIncidents(DateTimeOffset.MaxValue);
        cancellationTokenSource?.Dispose();
        _sessionCts = null;
        _channel = null;
        _collectorTasks = [];
        _isSessionActive = false;

        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), "Session stopped.");
        OnStateChanged();
    }

    public IncidentMarker? MarkIncident(IncidentSeverity severity)
    {
        if (!IsSessionActive || _incidentMaterializer is null)
        {
            Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), "Sessionen måste vara aktiv innan du kan markera en incident.");
            return null;
        }

        var marker = _incidentMaterializer.MarkIncident(DateTimeOffset.UtcNow, severity);
        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Incident markerad: {marker.Label} ({marker.Severity}).");

        if (severity == IncidentSeverity.Severe && _settings.DeepCapture.Enabled && _sessionCts is not null)
        {
            _ = Task.Run(() => CaptureDeepTraceAsync(marker, _sessionCts.Token));
        }

        OnStateChanged();
        return marker;
    }

    public async Task<string?> ExportIncidentAsync(IncidentRecord? incident, bool includeSensitiveFields, bool includeAttachedArtifacts, CancellationToken cancellationToken = default)
    {
        if (incident is null)
        {
            return null;
        }

        var enriched = incident.Analysis is null ? incident with { Analysis = _analysisEngine.Analyze(incident) } : incident;
        var outputPath = await _incidentExporter.ExportAsync(
            enriched,
            new ExportBundleOptions(_settings.ExportDirectory, includeSensitiveFields, includeAttachedArtifacts),
            cancellationToken).ConfigureAwait(false);

        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Incident exporterad till {outputPath}.");
        return outputPath;
    }

    public async Task<IReadOnlyList<ArtifactParseResult>> ImportArtifactsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var results = new List<ArtifactParseResult>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parser = _artifactParsers.FirstOrDefault(candidate => candidate.CanParse(path));
            var result = parser is not null
                ? await parser.ParseAsync(path, cancellationToken).ConfigureAwait(false)
                : new ArtifactParseResult(
                    new ArtifactAttachment(path, ArtifactKind.ManualAttachment, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
                    [new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.ManualAttachment, "Manuell bilaga importerad som stödbevis.", new Dictionary<string, double>(), path)],
                    []);

            if (result is null)
            {
                continue;
            }

            lock (_sync)
            {
                _attachments.Add(result.Attachment);
            }

            results.Add(result);
            foreach (var evidence in result.Evidence)
            {
                if (_channel?.Writer.TryWrite(evidence) != true)
                {
                    TryAttachEvidenceToLatestIncident(evidence, result.Attachment);
                }
            }

            Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Artifact importerad: {result.Attachment.DisplayName}.");
        }

        OnStateChanged();
        return results;
    }

    public IncidentRecord AddSyntheticIncident(IncidentRecord incident)
    {
        var analyzed = incident with { Analysis = incident.Analysis ?? _analysisEngine.Analyze(incident) };
        lock (_sync)
        {
            _incidents.Add(analyzed);
        }

        IncidentCompleted?.Invoke(this, analyzed);
        OnStateChanged();
        return analyzed;
    }

    public void Report(StatusLevel level, string source, string message)
    {
        var entry = new DiagnosticStatusEntry(DateTimeOffset.Now, level, source, message);
        lock (_sync)
        {
            _statusEntries.Add(entry);
            if (_statusEntries.Count > 200)
            {
                _statusEntries.RemoveRange(0, _statusEntries.Count - 200);
            }
        }

        StatusReported?.Invoke(this, entry);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);

        foreach (var disposable in _collectors.OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }

    private async Task RunCollectorSafeAsync(ITelemetryCollector collector, CollectorContext context, CancellationToken cancellationToken)
    {
        try
        {
            await collector.RunAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Report(StatusLevel.Warning, collector.Name, ex.Message);
        }
    }

    private async Task PumpAsync(ChannelReader<TelemetryEvent> reader, CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var telemetryEvent))
            {
                _ringBuffer?.Add(telemetryEvent);

                if (_incidentMaterializer is not null && Environment is not null)
                {
                    var attachments = GetAttachments();
                    var completed = _incidentMaterializer.OnTelemetry(telemetryEvent, Environment, attachments);
                    AddCompletedIncidents(completed);
                }
            }
        }
    }

    private async Task FinalizeLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            FlushPendingIncidents(DateTimeOffset.UtcNow);
        }
    }

    private void FlushPendingIncidents(DateTimeOffset now)
    {
        if (_incidentMaterializer is null || Environment is null)
        {
            return;
        }

        var completed = _incidentMaterializer.FinalizeDue(now, Environment, GetAttachments());
        AddCompletedIncidents(completed);
    }

    private void AddCompletedIncidents(IReadOnlyList<IncidentRecord> completedIncidents)
    {
        foreach (var incident in completedIncidents)
        {
            var analyzed = incident with { Analysis = _analysisEngine.Analyze(incident) };
            lock (_sync)
            {
                _incidents.Add(analyzed);
            }

            IncidentCompleted?.Invoke(this, analyzed);
            Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Incident färdigställd: {analyzed.Marker.Label} {analyzed.Marker.MarkedAt:HH:mm:ss}.");
        }

        if (completedIncidents.Count > 0)
        {
            OnStateChanged();
        }
    }

    private IReadOnlyList<ArtifactAttachment> GetAttachments()
    {
        lock (_sync)
        {
            return _attachments.ToArray();
        }
    }

    private void TryAttachEvidenceToLatestIncident(ArtifactEvidence evidence, ArtifactAttachment attachment)
    {
        lock (_sync)
        {
            if (_incidents.Count == 0)
            {
                return;
            }

            var latest = _incidents[^1];
            latest = latest with
            {
                Events = latest.Events.Concat([evidence]).OrderBy(item => item.Timestamp).ToArray(),
                Attachments = latest.Attachments.Concat([attachment]).ToArray(),
            };

            latest = latest with { Analysis = _analysisEngine.Analyze(latest) };
            _incidents[^1] = latest;
        }
    }

    private async Task CaptureDeepTraceAsync(IncidentMarker marker, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _deepCaptureService.CaptureAsync(marker, _settings, cancellationToken).ConfigureAwait(false);
            Report(result.RequiresElevation ? StatusLevel.Warning : StatusLevel.Info, nameof(DiagnosticsSessionManager), result.Message);

            if (!string.IsNullOrWhiteSpace(result.CapturePath))
            {
                lock (_sync)
                {
                    _attachments.Add(new ArtifactAttachment(result.CapturePath, ArtifactKind.EtlTrace, Path.GetFileName(result.CapturePath), DateTimeOffset.UtcNow, Sensitive: true));
                }
            }
        }
        catch (Exception ex)
        {
            Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), $"Deep capture misslyckades: {ex.Message}");
        }
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}