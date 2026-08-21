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

    /// <summary>
    /// Deep captures started by a marker. They run detached from the call that marked the incident, but
    /// stopping the session still has to wait for them: they report their outcome through
    /// <see cref="Report"/>, and a report arriving after the journal is closed is simply lost.
    /// </summary>
    private readonly List<Task> _deepCaptureTasks = [];

    /// <summary>
    /// Completed incidents waiting to be analysed. The correlation engine sorts frame data and makes
    /// several LINQ passes over a 90 second window, which is far too much work to run on the telemetry
    /// pump: doing so stalls ingestion for every collector behind the bounded channel and produces a CPU
    /// and GC spike in the middle of the very stutter being recorded.
    /// </summary>
    private const int AnalysisQueueCapacity = 64;

    private CancellationTokenSource? _sessionCts;
    private Channel<IncidentRecord>? _analysisChannel;
    private Task? _analysisTask;
    private Channel<TelemetryEvent>? _channel;
    private TimeWindowRingBuffer<TelemetryEvent>? _ringBuffer;
    private IncidentMaterializer? _incidentMaterializer;
    private Task? _pumpTask;
    private Task? _finalizeTask;
    private Task[] _collectorTasks = [];
    private volatile bool _isSessionActive;
    private AutoIncidentDetector? _autoDetector;
    private volatile IReadOnlyList<ArtifactAttachment>? _attachmentsSnapshot;

    /// <summary>
    /// Written to for as long as a session runs. Read without a lock from <see cref="Report"/>, which
    /// collectors call from their own threads, so the field is volatile and the journal itself is
    /// responsible for being thread safe.
    /// </summary>
    private volatile SessionJournal? _journal;

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

    /// <summary>
    /// Raised with the incidents dropped to keep the history within
    /// <see cref="DiagnosticsSettings.MaxRetainedIncidents"/>, so views holding their own copy can drop
    /// them too instead of pinning the telemetry this cap exists to release.
    /// </summary>
    public event EventHandler<IReadOnlyList<IncidentRecord>>? IncidentsEvicted;

    /// <summary>Raised when an existing incident is re-analysed, e.g. after an artifact import.</summary>
    public event EventHandler<IncidentRecord>? IncidentUpdated;

    public event EventHandler<SystemTelemetrySample>? SystemTelemetryUpdated;

    public event EventHandler<GpuTelemetrySample>? GpuTelemetryUpdated;

    public event EventHandler<CaptureHealthTelemetrySample>? CaptureHealthUpdated;

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

        // Opened before anything else can report, so the warnings a session produces while starting up
        // are in the file too. They are the ones that explain an empty history afterwards.
        OpenJournal();

        // Everything below builds session state that only StopSessionAsync tears down, and
        // StopSessionAsync returns immediately while _isSessionActive is false. A start that throws —
        // the token firing during the WPR ring buffer start is the realistic case, since that step waits
        // on an external process — would therefore leave the journal open, the channels alive and
        // possibly a WPR session recording, with nothing left that would ever clean them up. So the
        // start undoes its own work before it lets the exception out.
        try
        {
            _ringBuffer = new TimeWindowRingBuffer<TelemetryEvent>(_settings.RingBufferRetention, item => item.Timestamp);
            _incidentMaterializer = new IncidentMaterializer(_ringBuffer, _settings.PreIncidentWindow, _settings.PostIncidentWindow);

            // Settings can reach this point from anywhere, and degenerate thresholds make the detector fire
            // on nearly every frame, so the invariant is re-established rather than assumed.
            if (_settings.AutoDetect.Normalize())
            {
                Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), "Ogiltiga auto-detect-värden justerades till tillåtna gränser.");
            }

            // Same reasoning for the ring buffer: its size is non-paged memory the machine gives up for the
            // whole session, and a hand-edited value is not allowed to ask for an arbitrary amount of it.
            _settings.DeepCapture.Normalize();

            _autoDetector = new AutoIncidentDetector(_settings.AutoDetect, Environment?.DisplayRefreshRateHz);
            _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(32768)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            _analysisChannel = Channel.CreateBounded<IncidentRecord>(new BoundedChannelOptions(AnalysisQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var context = new CollectorContext(_channel.Writer, _settings, this, _processResolver, () => DateTimeOffset.UtcNow);

            // Deliberately not tied to the session token: the queue has to drain on stop, or the last
            // incidents of a session would be discarded exactly when the user goes looking for them.
            // Started before the collectors so the buffer is already accumulating by the time the first
            // frames arrive. The whole point is that a marker has history behind it, and history the session
            // spent starting up is history a marker cannot use.
            await StartDeepCaptureRingBufferAsync(_sessionCts.Token).ConfigureAwait(false);

            _analysisTask = Task.Run(() => AnalysisLoopAsync(_analysisChannel.Reader));
            _pumpTask = Task.Run(() => PumpAsync(_channel.Reader, _sessionCts.Token));
            _finalizeTask = Task.Run(() => FinalizeLoopAsync(_sessionCts.Token));
            _collectorTasks = _collectors.Select(collector => Task.Run(() => RunCollectorSafeAsync(collector, context, _sessionCts.Token))).ToArray();
            _isSessionActive = true;
        }
        catch (Exception ex)
        {
            await AbortStartAsync(ex).ConfigureAwait(false);
            throw;
        }

        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Session started for profile '{_settings.ServerProfile.Name}'.");
        OnStateChanged();
    }

    /// <summary>
    /// Rolls a half-built session back after <see cref="StartSessionAsync"/> failed, so the next start
    /// begins from the state a clean stop would have left behind.
    /// </summary>
    private async Task AbortStartAsync(Exception failure)
    {
        Report(
            StatusLevel.Warning,
            nameof(DiagnosticsSessionManager),
            $"Sessionen kunde inte startas: {failure.Message}. Det som hann startas städas bort.");

        _sessionCts?.Cancel();
        _channel?.Writer.TryComplete();
        _analysisChannel?.Writer.TryComplete();

        // The loops may never have been created; the ones that were observe the token just cancelled.
        Task?[] pending = [.. _collectorTasks, _pumpTask, _finalizeTask, _analysisTask];
        try
        {
            await Task.WhenAll(pending.Where(task => task is not null)!).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each loop reports its own failures, and this path is already unwinding.
        }

        // Before the journal closes, so a WPR teardown problem is still written down.
        await StopDeepCaptureRingBufferAsync().ConfigureAwait(false);

        _sessionCts?.Dispose();
        _sessionCts = null;
        _channel = null;
        _analysisChannel = null;
        _analysisTask = null;
        _pumpTask = null;
        _finalizeTask = null;
        _autoDetector = null;
        _ringBuffer = null;
        _incidentMaterializer = null;
        _collectorTasks = [];
        _isSessionActive = false;

        CloseJournal();
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

        await FlushPendingIncidentsAsync(DateTimeOffset.MaxValue).ConfigureAwait(false);

        _analysisChannel?.Writer.TryComplete();
        if (_analysisTask is not null)
        {
            await _analysisTask.ConfigureAwait(false);
        }

        await WaitForDeepCapturesAsync().ConfigureAwait(false);

        // After the in-flight captures, not before: stopping the ring buffer takes the WPR gate, and a
        // capture still writing its ETL needs to finish holding it first.
        await StopDeepCaptureRingBufferAsync().ConfigureAwait(false);

        cancellationTokenSource?.Dispose();
        _sessionCts = null;
        _channel = null;
        _analysisChannel = null;
        _analysisTask = null;
        _autoDetector = null;
        _collectorTasks = [];
        _isSessionActive = false;

        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), "Session stopped.");
        CloseJournal();
        OnStateChanged();
    }

    /// <summary>
    /// Waits out the deep captures this session started, so their status entries reach the journal
    /// before it is closed and land ahead of the session-end line rather than after it. The captures
    /// observe the session token, which is already cancelled here, so this is a short wait — and it also
    /// keeps the token source alive until nothing is using its token any more.
    /// </summary>
    private async Task WaitForDeepCapturesAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            pending = _deepCaptureTasks.ToArray();
            _deepCaptureTasks.Clear();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // CaptureDeepTraceAsync reports its own failures; there is nothing to add here.
        }
    }

    /// <summary>
    /// Brings up the background WPR ring buffer, reporting whatever came of it.
    /// </summary>
    /// <remarks>
    /// Never throws into session startup. Deep capture is an optional extra — a missing wpr.exe, an
    /// unelevated app or a profile WPR rejects must all leave the rest of the telemetry running, since
    /// frame data without a trace is far better than neither.
    /// </remarks>
    private async Task StartDeepCaptureRingBufferAsync(CancellationToken cancellationToken)
    {
        if (!_settings.DeepCapture.Enabled)
        {
            return;
        }

        try
        {
            var result = await _deepCaptureService.StartRingBufferAsync(_settings, cancellationToken).ConfigureAwait(false);
            Report(
                result.Started ? StatusLevel.Info : result.RequiresElevation ? StatusLevel.Warning : StatusLevel.Error,
                nameof(IDeepCaptureService),
                result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report(StatusLevel.Error, nameof(IDeepCaptureService), $"Ringbufferten kunde inte startas: {ex.Message}");
        }
    }

    private async Task StopDeepCaptureRingBufferAsync()
    {
        try
        {
            await _deepCaptureService.StopRingBufferAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(StatusLevel.Warning, nameof(IDeepCaptureService), $"Ringbufferten kunde inte stoppas rent: {ex.Message}");
        }
    }

    private void OpenJournal()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var journal = SessionJournal.TryOpen(_settings.WorkingDirectory, startedAt, out var error);
        if (journal is null)
        {
            Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), $"Sessionsloggen kunde inte skapas: {error}");
            return;
        }

        _journal = journal;
        journal.WriteSessionStart(Environment, _settings, startedAt);
        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Sessionslogg skrivs till {journal.Path}.");
    }

    private void CloseJournal()
    {
        var journal = _journal;
        if (journal is null)
        {
            return;
        }

        journal.WriteSessionEnd(DateTimeOffset.UtcNow);
        _journal = null;
        journal.Dispose();
    }

    public IncidentMarker? MarkIncident(IncidentSeverity severity)
    {
        if (!IsSessionActive || _incidentMaterializer is null)
        {
            Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), "Sessionen måste vara aktiv innan du kan markera en incident.");
            return null;
        }

        return CreateMarker(DateTimeOffset.UtcNow, severity, label: null, allowDeepCapture: true);
    }

    /// <summary>
    /// Marks an incident the detector found. Deep capture stays off: WPR is affordable once on demand
    /// and not once every couple of minutes for a whole stream.
    /// </summary>
    private void MarkAutoIncident(DateTimeOffset timestamp, AutoIncidentTrigger trigger)
    {
        if (_incidentMaterializer is null)
        {
            return;
        }

        CreateMarker(timestamp, trigger.Severity, trigger.Label, allowDeepCapture: false);
    }

    private IncidentMarker? CreateMarker(DateTimeOffset timestamp, IncidentSeverity severity, string? label, bool allowDeepCapture)
    {
        if (_incidentMaterializer is null)
        {
            return null;
        }

        var marker = _incidentMaterializer.MarkIncident(timestamp, severity, label);
        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Incident markerad: {marker.Label} ({marker.Severity}).");

        var shouldDeepCapture = severity == IncidentSeverity.Severe || _settings.DeepCapture.CaptureNormalManualIncidents;
        if (allowDeepCapture && shouldDeepCapture && _settings.DeepCapture.Enabled && _sessionCts is not null)
        {
            // The token is read here rather than inside the task: by the time the task runs, stopping
            // the session may already have replaced the source with null.
            var sessionToken = _sessionCts.Token;
            var capture = Task.Run(() => CaptureDeepTraceAsync(marker, sessionToken));

            lock (_sync)
            {
                _deepCaptureTasks.RemoveAll(task => task.IsCompleted);
                _deepCaptureTasks.Add(capture);
            }
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
                InvalidateAttachmentsSnapshot();
            }

            results.Add(result);
            foreach (var evidence in result.Evidence)
            {
                // Two distinct destinations. The channel feeds incidents whose post-window is still
                // open. An incident that has already been materialized will never see the channel
                // again, so it has to be updated directly — otherwise importing net_stats for the
                // stutter you just marked silently does nothing, which is the normal workflow.
                _ = _channel?.Writer.TryWrite(evidence);
                TryAttachEvidenceToLatestIncident(evidence, result.Attachment);
            }

            Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Artifact importerad: {result.Attachment.DisplayName}.");
        }

        OnStateChanged();
        return results;
    }

    public IncidentRecord AddSyntheticIncident(IncidentRecord incident)
    {
        // Demo scenarios are user-initiated and expected to appear immediately, so this one path keeps
        // analysing inline instead of going through the worker queue.
        var analyzed = incident.Analysis is null ? Analyze(incident) : incident;
        var evicted = AddIncidentWithinCap(analyzed);

        IncidentCompleted?.Invoke(this, analyzed);

        if (evicted.Count > 0)
        {
            IncidentsEvicted?.Invoke(this, evicted);
        }

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

        // The in-memory list keeps only the last 200 entries and dies with the process, so the journal
        // is the only place a status entry from the start of a six hour session still exists.
        var journal = _journal;
        journal?.WriteStatus(entry);

        StatusReported?.Invoke(this, entry);

        // A journal that failed to write is itself worth one status entry. Reporting it re-enters this
        // method exactly once: taking the failure clears it, so the nested call finds nothing left to
        // take and stops there.
        if (journal is not null && journal.TryTakeFailure(out var failure))
        {
            Report(StatusLevel.Warning, nameof(SessionJournal), failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);

        foreach (var disposable in _collectors.OfType<IDisposable>())
        {
            disposable.Dispose();
        }

        (_deepCaptureService as IDisposable)?.Dispose();
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

                if (telemetryEvent is SystemTelemetrySample systemSample)
                {
                    SystemTelemetryUpdated?.Invoke(this, systemSample);
                }
                else if (telemetryEvent is GpuTelemetrySample gpuSample)
                {
                    GpuTelemetryUpdated?.Invoke(this, gpuSample);
                }
                else if (telemetryEvent is CaptureHealthTelemetrySample healthSample)
                {
                    CaptureHealthUpdated?.Invoke(this, healthSample);
                }
                else if (telemetryEvent is FrameTelemetrySample frameSample && _autoDetector is not null)
                {
                    // The marker has to be raised before the materializer sees this event, so the frame
                    // that triggered the incident lands inside its own window rather than one event
                    // short of it.
                    if (_autoDetector.Observe(frameSample) is { } trigger)
                    {
                        MarkAutoIncident(frameSample.Timestamp, trigger);
                    }
                }

                if (_incidentMaterializer is not null && Environment is not null)
                {
                    var attachments = GetAttachments();
                    var completed = _incidentMaterializer.OnTelemetry(telemetryEvent, Environment, attachments);
                    await QueueForAnalysisAsync(completed).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task FinalizeLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await FlushPendingIncidentsAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
    }

    private async Task FlushPendingIncidentsAsync(DateTimeOffset now)
    {
        if (_incidentMaterializer is null || Environment is null)
        {
            return;
        }

        var completed = _incidentMaterializer.FinalizeDue(now, Environment, GetAttachments());
        await QueueForAnalysisAsync(completed).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands completed incidents to the analysis worker. The queue is bounded, so a burst of incidents
    /// slows the producer down instead of letting unanalysed windows pile up without limit.
    /// </summary>
    private async Task QueueForAnalysisAsync(IReadOnlyList<IncidentRecord> completedIncidents)
    {
        if (completedIncidents.Count == 0)
        {
            return;
        }

        var writer = _analysisChannel?.Writer;
        foreach (var incident in completedIncidents)
        {
            if (writer is null)
            {
                // No session running: analyse inline rather than lose the incident.
                PublishIncident(Analyze(incident));
                continue;
            }

            await writer.WriteAsync(incident, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task AnalysisLoopAsync(ChannelReader<IncidentRecord> reader)
    {
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (reader.TryRead(out var incident))
            {
                PublishIncident(Analyze(incident));
            }
        }
    }

    private IncidentRecord Analyze(IncidentRecord incident)
    {
        try
        {
            return incident with { Analysis = _analysisEngine.Analyze(incident) };
        }
        catch (Exception ex)
        {
            // An incident without an analysis is still evidence worth keeping and exporting.
            Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), $"Analysen av incidenten misslyckades: {ex.Message}");
            return incident;
        }
    }

    private void PublishIncident(IncidentRecord analyzed)
    {
        var evicted = AddIncidentWithinCap(analyzed);

        // Written before the history is touched by anything else: an incident evicted by the retention
        // cap, or dropped when the app closes, still leaves its summary on disk.
        _journal?.WriteIncident(analyzed);

        IncidentCompleted?.Invoke(this, analyzed);
        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Incident färdigställd: {analyzed.Marker.Label} {analyzed.Marker.MarkedAt:HH:mm:ss}.");

        if (evicted.Count > 0)
        {
            IncidentsEvicted?.Invoke(this, evicted);
        }

        OnStateChanged();
    }

    /// <summary>
    /// Appends an incident and drops the oldest ones beyond
    /// <see cref="DiagnosticsSettings.MaxRetainedIncidents"/>. Each retained incident holds its whole 90
    /// second window — thousands of frame samples — and the auto detector's per-session ceiling does
    /// nothing across sessions, so without a global cap the history only ever grows.
    /// </summary>
    private IReadOnlyList<IncidentRecord> AddIncidentWithinCap(IncidentRecord incident)
    {
        var cap = Math.Clamp(_settings.MaxRetainedIncidents, 1, 1000);

        lock (_sync)
        {
            _incidents.Add(incident);
            if (_incidents.Count <= cap)
            {
                return [];
            }

            var removeCount = _incidents.Count - cap;
            var evicted = _incidents.GetRange(0, removeCount);
            _incidents.RemoveRange(0, removeCount);
            return evicted;
        }
    }

    /// <summary>
    /// Attachments change only on import, but the telemetry pump needs them on every event. Caching the
    /// snapshot keeps that path free of a lock and an array copy per event.
    /// </summary>
    private IReadOnlyList<ArtifactAttachment> GetAttachments()
    {
        var cached = _attachmentsSnapshot;
        if (cached is not null)
        {
            return cached;
        }

        lock (_sync)
        {
            return _attachmentsSnapshot = _attachments.ToArray();
        }
    }

    private void InvalidateAttachmentsSnapshot()
    {
        _attachmentsSnapshot = null;
    }

    /// <summary>
    /// Adds imported evidence to the most recent completed incident and re-runs the analysis, so the
    /// ranking reflects the new input rather than the state at materialization time.
    /// </summary>
    private void TryAttachEvidenceToLatestIncident(ArtifactEvidence evidence, ArtifactAttachment attachment)
    {
        IncidentRecord updated;

        lock (_sync)
        {
            if (_incidents.Count == 0)
            {
                return;
            }

            var latest = _incidents[^1];
            if (latest.Attachments.Any(item => string.Equals(item.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase))
                && latest.Events.OfType<ArtifactEvidence>().Any(item => item.Summary == evidence.Summary))
            {
                return;
            }

            latest = latest with
            {
                Events = latest.Events.Concat([evidence]).OrderBy(item => item.Timestamp).ToArray(),
                Attachments = latest.Attachments.Any(item => string.Equals(item.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase))
                    ? latest.Attachments
                    : latest.Attachments.Concat([attachment]).ToArray(),
            };

            updated = latest with { Analysis = _analysisEngine.Analyze(latest) };
            _incidents[^1] = updated;
        }

        // The journal line written when the incident completed describes the analysis as it stood before
        // this import, so the conclusion the import produced — usually the one that actually explains
        // the incident — would otherwise exist only in memory.
        _journal?.WriteIncidentUpdate(updated);

        IncidentUpdated?.Invoke(this, updated);
        OnStateChanged();
    }

    private async Task CaptureDeepTraceAsync(IncidentMarker marker, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _deepCaptureService.CaptureAsync(marker, _settings, cancellationToken).ConfigureAwait(false);
            Report(result.RequiresElevation ? StatusLevel.Warning : StatusLevel.Info, nameof(DiagnosticsSessionManager), result.Message);

            // File.Exists as well as a non-empty path: a capture that failed late can name the ETL it
            // was going to write, and an attachment pointing at nothing follows the incident all the way
            // into the export bundle.
            if (!string.IsNullOrWhiteSpace(result.CapturePath) && File.Exists(result.CapturePath))
            {
                lock (_sync)
                {
                    _attachments.Add(new ArtifactAttachment(result.CapturePath, ArtifactKind.EtlTrace, Path.GetFileName(result.CapturePath), DateTimeOffset.UtcNow, Sensitive: true));
                    InvalidateAttachmentsSnapshot();
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
