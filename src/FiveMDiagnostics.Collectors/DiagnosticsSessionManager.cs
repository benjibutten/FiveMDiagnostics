using System.Threading.Channels;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

public sealed class DiagnosticsSessionManager : IDiagnosticStatusSink, IAsyncDisposable
{
    private readonly object _sync = new();

    /// <summary>
    /// How far back the adapter occupancy is kept for a finished trace to be interpreted against.
    /// </summary>
    /// <remarks>
    /// A capture's tail, the write of most of a gigabyte and the parse of it add up to a minute or two,
    /// so this has to reach back past all three to cover the seconds the trace actually holds.
    /// </remarks>
    private static readonly TimeSpan AdapterVramWindow = TimeSpan.FromMinutes(3);

    private readonly object _adapterVramSync = new();
    private readonly List<(DateTimeOffset At, double Percent)> _adapterVram = [];
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
    private AutoDeepCaptureBudget? _autoCaptureBudget;
    private FramePacingMonitor? _framePacing;
    private VramAccountingMonitor? _vramAccounting;
    private VramBudgetMonitor? _vramBudget;
    private LiveVramTracker? _liveVram;
    private DisplayCadenceMonitor? _displayCadence;
    private CaptureCostMonitor? _captureCost;
    private VramPressureBandMonitor? _vramPressure;
    private SlowFrameWaitProfile? _slowFrameWaits;
    private IncidentVerdictTally? _verdicts;
    private GameGraphicsSettingsMonitor? _gameSettings;

    /// <summary>
    /// Every window mode this session has been in, and when each of them started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry per change, which over an evening is one or two. It exists because the analysis engine
    /// outlives the session and runs off a queue: an incident marked at 21:14 is classified when the
    /// worker reaches it, and asking "is the compositor explained?" then answers about the window mode
    /// at classification time. Alt-Enter is one keystroke and this machine's user does it.
    /// </para>
    /// <para>
    /// Stamped when the change is <em>noticed</em> rather than when the file was written, which is up to
    /// one settings-check interval late. That is a known and bounded inaccuracy on a signal that only
    /// decides whether one explanatory sentence is printed; the alternative is to trust a file's write
    /// time as the moment the game changed mode, which it is not.
    /// </para>
    /// </remarks>
    private readonly List<(DateTimeOffset At, bool Explained)> _windowModeHistory = [];

    private readonly object _windowModeSync = new();

    /// <summary>
    /// When the graphics settings file is next re-read.
    /// </summary>
    /// <remarks>
    /// Read and written only from the finalize loop, which is a single task. The check exists because
    /// this machine is switched off rather than stopped: anything written only at session end is
    /// written on one evening in ten.
    /// </remarks>
    private DateTimeOffset _nextGameSettingsCheck = DateTimeOffset.MaxValue;

    /// <summary>When the running summaries are next due.</summary>
    private DateTimeOffset _nextInterimSummary = DateTimeOffset.MaxValue;

    /// <summary>
    /// The last line written for each summary source, so an unchanged summary is not repeated.
    /// </summary>
    /// <remarks>
    /// The summaries are written on a cadence now, and most of them say the same thing for stretches at
    /// a time. Repeating an identical sentence every quarter of an hour would bury the ones that moved.
    /// </remarks>
    private readonly Dictionary<string, string> _lastSummaryLine = new(StringComparer.Ordinal);

    /// <summary>Wall clock of the most recent frame long enough to count as a stall in progress.</summary>
    /// <remarks>
    /// Received time rather than the frame's own timestamp: the question a deep capture's tail asks is
    /// "is it still happening now", and PresentMon's clock answers a different one.
    /// </remarks>
    private DateTimeOffset? _lastStallFrameAtUtc;

    private double _stallFrameThresholdMs = DefaultStallFrameMs;

    /// <summary>Whether the game has been seen at all, and whether its exit has been reported.</summary>
    private bool _targetProcessSeen;
    private bool _reportedTargetProcessExit;

    /// <summary>How often the graphics settings file is compared against what the session started with.</summary>
    private static readonly TimeSpan GameSettingsCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the session's running summaries are written to the journal.
    /// </summary>
    /// <remarks>
    /// They used to be written only by <c>StopSession</c>, and this machine's sessions do not stop: the
    /// computer is switched off with one still running. On 1 September the game exited at 01:14 with the
    /// session open, and all five summaries — the VRAM band, the slow-frame wait profile, the verdict
    /// tally, the capture cost and the display cadence — were simply never written, on the evening whose
    /// review needed every one of them. The journal is append-only, so writing them repeatedly costs a
    /// few lines an hour and the last one written is the one that counts.
    /// </remarks>
    private static readonly TimeSpan InterimSummaryInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Quiet period after a large frame before the stall is considered over.
    /// </summary>
    /// <remarks>
    /// Long enough to bridge the gap between two frames of a stuttering sequence — the 1 September freeze
    /// produced late frames roughly every half second for nine seconds — and short enough that an
    /// isolated hitch does not hold a capture open.
    /// </remarks>
    private static readonly TimeSpan StallQuietPeriod = TimeSpan.FromMilliseconds(1500);

    /// <summary>Frame time from which a stall counts as still running, before the session has a level.</summary>
    private const double DefaultStallFrameMs = 60;

    /// <summary>
    /// Trace evidence that arrived before the incident it belongs to had been published.
    /// </summary>
    /// <remarks>
    /// A capture is triggered at the marker and finishes about thirty seconds later, while the incident
    /// window stays open for sixty — so the ordinary case is that the trace is ready first and there is
    /// nothing yet to attach it to. Held under <c>_sync</c> and taken in the same critical section that
    /// publishes an incident, which is what closes the gap: either the capture finds the incident in the
    /// list, or it leaves the evidence here and publication picks it up. There is no order of the two in
    /// which the trace is lost.
    /// </remarks>
    private readonly Dictionary<Guid, List<(ArtifactEvidence Evidence, ArtifactAttachment Attachment)>> _pendingTraceEvidence = [];
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

    public event EventHandler<GpuProcessMemorySample>? GpuProcessMemoryUpdated;

    /// <summary>
    /// The live table of what is holding the adapter's memory, refreshed with every process sample.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GpuProcessMemoryUpdated"/> because it carries what a raw sample cannot:
    /// what each process held when the session started, so growth reads without anyone diffing a CSV.
    /// </remarks>
    public event EventHandler<LiveVramSnapshot>? LiveVramUpdated;

    public event EventHandler<CaptureHealthTelemetrySample>? CaptureHealthUpdated;

    /// <summary>Raised once per classified frame pacing window, healthy ones included.</summary>
    public event EventHandler<FramePacingWindow>? FramePacingWindowCompleted;

    /// <summary>
    /// How the session has been spent so far, window by window. Empty until the first window closes,
    /// and reset with the session.
    /// </summary>
    public FramePacingSummary FramePacing => _framePacing?.Summary ?? FramePacingSummary.Empty;

    /// <summary>
    /// Automatic deep captures still available this session. Surfaced so the UI can say why a bad patch
    /// has stopped producing traces, rather than leaving the user to wonder whether it broke.
    /// </summary>
    public int RemainingAutoCaptures => _autoCaptureBudget?.Remaining ?? _settings.DeepCapture.MaxAutoCapturesPerSession;

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
        IReadOnlyList<string> initialGameSettingsLines = [];
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

            // Absolute rather than relative, so it keeps working in exactly the sustained bad patch
            // where the spike detector's rolling baseline drifts up with the damage and goes quiet.
            _settings.FramePacing.Normalize();

            _autoDetector = new AutoIncidentDetector(_settings.AutoDetect, Environment?.DisplayRefreshRateHz);
            _autoCaptureBudget = new AutoDeepCaptureBudget(_settings.DeepCapture);
            _framePacing = new FramePacingMonitor(_settings.FramePacing, Environment?.DisplayRefreshRateHz);
            _displayCadence = new DisplayCadenceMonitor(Environment?.DisplayRefreshRateHz);
            _captureCost = new CaptureCostMonitor(Environment?.DisplayRefreshRateHz);
            _vramAccounting = new VramAccountingMonitor();
            _vramBudget = new VramBudgetMonitor();
            _vramPressure = new VramPressureBandMonitor(Environment?.DisplayRefreshRateHz);
            _slowFrameWaits = new SlowFrameWaitProfile();
            _verdicts = new IncidentVerdictTally();
            _liveVram = new LiveVramTracker();

            _lastSummaryLine.Clear();
            _lastStallFrameAtUtc = null;
            _targetProcessSeen = false;
            _reportedTargetProcessExit = false;

            // Half the capture threshold: a frame that large is unambiguously part of a stall, while an
            // ordinary two-refresh hitch is not, and holding a trace open for those would keep every
            // capture running to its ceiling.
            _stallFrameThresholdMs = Math.Max(DefaultStallFrameMs, _settings.DeepCapture.AutoCaptureFrameTimeMs / 2);

            // Lets a capture keep recording while frames are still arriving late. Optional: a capture
            // backend that cannot do it simply keeps the fixed tail.
            if (_deepCaptureService is IStallAwareDeepCapture stallAware)
            {
                stallAware.StallInProgress = IsStallInProgress;
            }

            // Lets the trace analysis tell eviction from any other reason the driver moves memory. The
            // trace holds the driver's rate and nothing about how full the card was, and the conclusion
            // needs both.
            foreach (var vramAware in _artifactParsers.OfType<IVramAwareTraceAnalysis>())
            {
                vramAware.AdapterVramPercent = RecentAdapterVramPercent;
            }

            // Lets each incident be judged against the window mode it happened in rather than the one
            // the game is in when the queue reaches it. Attached before any incident can be marked, and
            // detached in DetachWindowModeHistory once the queue has drained.
            lock (_windowModeSync)
            {
                _windowModeHistory.Clear();
            }

            if (_analysisEngine is IWindowModeAwareAnalysis startWindowAware)
            {
                startWindowAware.ComposedPresentExplainedAt = ComposedPresentExplainedAt;
            }

            // A capture whose incident never published — the session was stopped while it was still
            // being written — would otherwise wait here for a marker id the next session cannot produce.
            lock (_sync)
            {
                _pendingTraceEvidence.Clear();
            }
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

            // Read before the collectors run rather than after they have been started. The budget
            // monitor is handed the game's ceiling here, and a monitor that gets it a moment later has
            // already seen process samples without it: the first budget line of the session is then
            // written against the card alone, which is the one line somebody picks a graphics preset
            // from. The lines themselves are still written below, after the session-start line, where
            // they read as the header they are.
            var sessionStartedAt = DateTimeOffset.UtcNow;
            _gameSettings = new GameGraphicsSettingsMonitor(sessionStartedAt);
            _nextGameSettingsCheck = sessionStartedAt + GameSettingsCheckInterval;
            _nextInterimSummary = sessionStartedAt + InterimSummaryInterval;
            initialGameSettingsLines = _gameSettings.DescribeInitial();
            ApplyGameSettingsToMonitors();

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

        // At the start rather than in the summary, because it is the one finding of this investigation
        // that is fixed before playing rather than analysed afterwards.
        if (RefreshRateMismatch.Describe(Environment?.Displays) is { } mismatch)
        {
            Report(StatusLevel.Warning, "DisplayCadence", mismatch);
        }

        // Every comparison so far has been made against a remembered setting. Silent when the file is
        // not where it is looked for, which costs nothing that was not already missing. The session
        // start is passed in so the line can say whether the file predates it, which is the difference
        // between "these are the settings" and "these are the settings from some evening in August".
        foreach (var line in initialGameSettingsLines)
        {
            Report(StatusLevel.Info, "GameSettings", line);
        }

        // Said once here instead of on every incident. Eleven sessions carried "everything went through
        // the compositor" as though it were a finding; it is what a borderless window does, and the
        // measurement that matters is that it costs nothing.
        if (_gameSettings?.ExplainsComposedPresent == true)
        {
            Report(
                StatusLevel.Info,
                "GameSettings",
                "Spelet kör i fönsterläge, så varje frame komponeras av DWM i stället för att få en egen flip "
                + "— present-läget kommer att stå på \"Composed\" hela sessionen och är förklarat av den "
                + "inställningen, inte ett fel. Kadensen mot skärmen mäts separat i DisplayCadence-raden; "
                + "det är den som säger vad kompositorn faktiskt kostar.");
        }
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
        _autoCaptureBudget = null;
        _ringBuffer = null;
        _incidentMaterializer = null;
        _vramPressure = null;
        _slowFrameWaits = null;
        _verdicts = null;
        _gameSettings = null;
        _nextGameSettingsCheck = DateTimeOffset.MaxValue;
        _nextInterimSummary = DateTimeOffset.MaxValue;
        _collectorTasks = [];
        _isSessionActive = false;

        _vramBudget?.SetTextureBudget(null);

        if (_deepCaptureService is IStallAwareDeepCapture stallAware)
        {
            stallAware.StallInProgress = null;
        }

        DetachAdapterVramHistory();
        DetachWindowModeHistory();

        FinalizeFramePacing();
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
        _autoCaptureBudget = null;
        _collectorTasks = [];
        _isSessionActive = false;

        _nextInterimSummary = DateTimeOffset.MaxValue;
        if (_deepCaptureService is IStallAwareDeepCapture stopStallAware)
        {
            stopStallAware.StallInProgress = null;
        }

        // The parsers outlive the session, and an ETL imported by hand afterwards would otherwise be
        // told how full the card was during an evening that has already ended. The probe is detached
        // and the history dropped together: either alone leaves the other able to answer.
        DetachAdapterVramHistory();

        FinalizeFramePacing();
        WriteSessionSummaries(final: true);
        FinalizeGameSettings();
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

    /// <summary>
    /// Closes the pacing window in progress and reports the session total, then releases the monitor.
    /// </summary>
    /// <remarks>
    /// Its own step, called before the journal closes, because it has to run before the field is
    /// cleared and both teardown paths clear a block of fields at once. Doing this inside
    /// <see cref="CloseJournal"/> looked tidier and was silently dead: the field was already null by
    /// the time it ran, so the last window never reached the journal and the end-of-session total was
    /// always empty.
    /// <para>
    /// The partial window matters. A session stopped because the evening turned bad ends inside the
    /// patch that made the user stop, and that is the minute worth keeping.
    /// </para>
    /// </remarks>
    private void FinalizeFramePacing()
    {
        var monitor = _framePacing;
        if (monitor is null)
        {
            return;
        }

        if (monitor.Flush() is { } finalWindow)
        {
            _journal?.WritePacingWindow(finalWindow);
            FramePacingWindowCompleted?.Invoke(this, finalWindow);
        }

        var summary = monitor.Summary;
        if (summary.TotalWindows > 0)
        {
            Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), summary.Describe(_settings.FramePacing.WindowLength));
        }

        _framePacing = null;
    }

    /// <summary>
    /// Writes how many of the session's frames reached the screen in step with the display.
    /// </summary>
    /// <remarks>
    /// At the end rather than during, because it is a property of the whole evening and barely moves
    /// after the first minutes. It belongs next to the pacing summary: pacing says whether the frames
    /// were produced on time and this says whether they were shown on time, and for eight sessions the
    /// first said yes while nobody asked the second.
    /// </remarks>
    private void FinalizeDisplayCadence(bool final)
    {
        if (_displayCadence?.Snapshot() is { } report && ShouldWriteSummary("DisplayCadence", report.Message))
        {
            Report(
                report.IsOffCadence ? StatusLevel.Warning : StatusLevel.Info,
                "DisplayCadence",
                report.Message);
        }

        // The mismatch warning goes out before the first frame exists, so this is the first moment the
        // session knows whether the condition it stated was true. Only withdrawn, never repeated: the
        // warning that still applies has already been read, and the cadence line above measures it.
        if (_displayCadence?.ComposedShare is { } composedShare
            && RefreshRateMismatch.DescribeWithdrawal(Environment?.Displays, composedShare) is { } withdrawal
            && ShouldWriteSummary("DisplayCadence.Withdrawal", withdrawal))
        {
            Report(StatusLevel.Info, "DisplayCadence", withdrawal);
        }

        if (final)
        {
            _displayCadence = null;
        }
    }

    /// <summary>
    /// Writes what this session's own deep captures coincided with.
    /// </summary>
    /// <remarks>
    /// Info rather than Warning. It is not a fault — the captures are how the largest hitches get
    /// explained at all — but two evenings are not comparable without it, and a reader who does not know
    /// one of them took ten captures and the other took two will read the difference as the machine.
    /// </remarks>
    private void FinalizeCaptureCost(bool final)
    {
        if (_captureCost?.Summary() is { } report && ShouldWriteSummary("DeepCapture.Cost", report.Message))
        {
            Report(StatusLevel.Info, "DeepCapture.Cost", report.Message);
        }

        if (final)
        {
            _captureCost = null;
        }
    }

    /// <summary>
    /// Writes how much of the session the card spent inside the VRAM band, and what it cost there.
    /// </summary>
    /// <remarks>
    /// A Warning when the band was genuinely occupied, because it is the one line of this summary that
    /// names something fixable before the next session. Every session so far has warned only about
    /// processes whose VRAM grew, which is silent on the evening where the memory was taken before the
    /// game started — and that was the evening.
    /// </remarks>
    private void FinalizeVramPressure(bool final)
    {
        if (_vramPressure?.Summary() is { } report && ShouldWriteSummary("GpuVram.Pressure", report.Message))
        {
            Report(
                report.IsPressured ? StatusLevel.Warning : StatusLevel.Info,
                "GpuVram.Pressure",
                report.Message);
        }

        if (final)
        {
            _vramPressure = null;
        }
    }

    /// <summary>
    /// Writes how many of the session's largest frames still had CPU slack, which is the line that
    /// separates a blocked thread from a pipeline working flat out.
    /// </summary>
    private void FinalizeSlowFrameWaits(bool final)
    {
        if (_slowFrameWaits?.Summary() is { } report && ShouldWriteSummary("FramePacing.Wait", report.Message))
        {
            Report(StatusLevel.Info, "FramePacing.Wait", report.Message);
        }

        if (final)
        {
            _slowFrameWaits = null;
        }
    }

    /// <summary>
    /// Writes what the engine concluded across the whole session, with the VRAM verdict on its own line.
    /// </summary>
    private void FinalizeVerdicts(bool final)
    {
        if (_verdicts?.Summary() is { } report)
        {
            if (report.VramPressureMessage is { } vram && ShouldWriteSummary("Analysis.Verdicts.Vram", vram))
            {
                Report(StatusLevel.Warning, "Analysis.Verdicts", vram);
            }

            if (ShouldWriteSummary("Analysis.Verdicts", report.Message))
            {
                Report(StatusLevel.Info, "Analysis.Verdicts", report.Message);
            }
        }

        if (final)
        {
            _verdicts = null;
        }
    }

    /// <summary>
    /// Writes the session's running summaries, and on the final pass releases the monitors behind them.
    /// </summary>
    /// <remarks>
    /// Called on a cadence, when the game exits, and once more if the session is stopped properly. The
    /// three occasions exist because only the last one used to, and it is the one that happens least
    /// often on the machine this app was written for.
    /// </remarks>
    private void WriteSessionSummaries(bool final)
    {
        FinalizeDisplayCadence(final);
        FinalizeCaptureCost(final);
        FinalizeVramPressure(final);
        FinalizeSlowFrameWaits(final);
        FinalizeVerdicts(final);
    }

    /// <summary>Whether this summary says anything its own last line did not.</summary>
    private bool ShouldWriteSummary(string source, string message)
    {
        if (_lastSummaryLine.TryGetValue(source, out var previous) && string.Equals(previous, message, StringComparison.Ordinal))
        {
            return false;
        }

        _lastSummaryLine[source] = message;
        return true;
    }

    /// <summary>
    /// True while frames are still arriving late, which is what keeps a deep capture's tail running.
    /// </summary>
    private bool IsStallInProgress()
    {
        return _lastStallFrameAtUtc is { } at && DateTimeOffset.UtcNow - at <= StallQuietPeriod;
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

        // severityGated: a manual Normal marker only captures when the user has opted in, which is the
        // historical behaviour and the reason CaptureNormalManualIncidents exists.
        return CreateMarker(DateTimeOffset.UtcNow, severity, label: null, allowDeepCapture: true, severityGated: true);
    }

    /// <summary>
    /// Marks an incident the detector found, and spends a deep capture on it when the hitch is severe
    /// enough and the session's budget allows one.
    /// </summary>
    /// <remarks>
    /// Deep capture used to be refused outright here. Saving the ring buffer is genuinely expensive —
    /// a few hundred megabytes and an empty buffer afterwards — but refusing every automatic incident
    /// meant a session could raise eighteen severe ones and produce no trace at all, which is what
    /// happened and what left five hitch clusters unexplained. <see cref="AutoDeepCaptureBudget"/> now
    /// decides, and the settings it reads are tight enough that a bad evening spends a handful.
    /// </remarks>
    /// <param name="sustainedSaturation">
    /// Set when the trigger came from <see cref="FramePacingMonitor"/> rather than from one bad frame.
    /// Such an incident carries no frame time to weigh, and the condition it describes — a frame rate
    /// that is not recovering — is exactly the one a per-frame threshold cannot see.
    /// </param>
    private void MarkAutoIncident(DateTimeOffset timestamp, AutoIncidentTrigger trigger, bool sustainedSaturation = false)
    {
        if (_incidentMaterializer is null)
        {
            return;
        }

        var captureThis = sustainedSaturation
            ? TryReserveSaturationCapture(timestamp)
            : TryReserveAutoCapture(timestamp, trigger);

        CreateMarker(
            timestamp,
            trigger.Severity,
            trigger.Label,
            allowDeepCapture: captureThis,
            trigger.FrameTimeMs,

            // A pacing incident carries no frame time, so its bar starts at zero and the next suppressed
            // frame of any size would clear it — replacing "FPS-taket nått i 15 min" with "Auto: 40 ms
            // frame", which says far less about the same window.
            allowFrameEscalation: !sustainedSaturation);
    }

    /// <summary>
    /// Folds a threshold-crossing frame that arrived inside an already open incident into that incident.
    /// </summary>
    /// <remarks>
    /// The cooldown that suppressed this trigger is doing its job — a burst of hitches is one event, not
    /// twenty incidents. What it must not do is throw the observation away. The session this was written
    /// for lost both of its worst frames that way, a 2 846 ms and a 1 683 ms, because each landed inside
    /// a window opened seconds earlier by something trivial. Escalating renames the incident after the
    /// worst frame it actually contains and raises its severity, so the export and the journal describe
    /// the event rather than its opening act.
    /// </remarks>
    private void EscalateOpenIncident(DateTimeOffset timestamp, AutoIncidentTrigger trigger, AutoIncidentSuppression suppression)
    {
        if (_incidentMaterializer is null)
        {
            return;
        }

        var outcome = _incidentMaterializer.TryEscalate(
            timestamp,
            trigger.Severity,
            trigger.Label,
            trigger.FrameTimeMs,
            out var escalated);

        if (outcome == IncidentEscalation.NoOpenIncident)
        {
            MarkSuppressedFrameWithNoOpenWindow(timestamp, trigger, suppression);
            return;
        }

        if (outcome == IncidentEscalation.AlreadyWorse || escalated is null)
        {
            return;
        }

        Report(
            StatusLevel.Info,
            nameof(DiagnosticsSessionManager),
            $"Incident uppgraderad: {escalated.Label} ({escalated.Severity}). En värre frame inträffade inuti ett öppet fönster.");

        // A frame this large is exactly what the budget exists for, and it is the reason the incident is
        // worth a trace at all — so the escalation gets its own shot at one rather than inheriting the
        // decision made for the smaller frame that opened the window.
        if (TryReserveAutoCapture(timestamp, trigger) && _settings.DeepCapture.Enabled && _sessionCts is not null)
        {
            StartDeepCapture(escalated, _sessionCts.Token);
        }

        OnStateChanged();
    }

    /// <summary>
    /// Records a suppressed frame that arrived when no incident window was open to absorb it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The detector's cooldown is two minutes and an incident window closes sixty seconds after its
    /// marker, so roughly a minute of every cycle has neither an incident collecting telemetry nor a
    /// detector willing to raise one. A frame landing there used to vanish completely — no incident, no
    /// trace, no journal line — which is the same silence that lost a 2 846 ms frame, just from the
    /// other direction.
    /// </para>
    /// <para>
    /// Only the cooldown is overridden, and only because it expires. Its stated purpose is to stop
    /// incidents that mostly re-describe each other's telemetry, and that reasoning ends the moment the
    /// previous window closes: a new incident then covers ninety seconds nothing else has looked at. An
    /// exhausted <see cref="AutoDetectOptions.MaxIncidentsPerWindow"/> is refused outright — it is the
    /// rate ceiling that keeps a bad hour from flooding the session, and a path around it would make it
    /// no ceiling at all.
    /// </para>
    /// <para>
    /// What qualifies is deliberately narrow: a frame worth a trace on its own, or a run of frames that
    /// never reached the screen. The latter needs naming separately because its frame times are ordinary
    /// by construction — the freeze is the run — so judging it by milliseconds would rank a visible
    /// freeze below a mild spike and drop it here.
    /// </para>
    /// </remarks>
    private void MarkSuppressedFrameWithNoOpenWindow(
        DateTimeOffset timestamp,
        AutoIncidentTrigger trigger,
        AutoIncidentSuppression suppression)
    {
        if (suppression != AutoIncidentSuppression.Cooldown)
        {
            return;
        }

        // The budget's own threshold rather than the configured constant: it follows the session's
        // frames upwards, and asking a different question here than the budget will ask a line later
        // would open windows for frames the budget then refuses to trace.
        var captureThreshold = _autoCaptureBudget?.EffectiveFrameTimeMs ?? _settings.DeepCapture.AutoCaptureFrameTimeMs;
        var worthItsOwnIncident = trigger.Severity == IncidentSeverity.Severe
            || trigger.Kind == AutoIncidentKind.DroppedFrameRun
            || trigger.FrameTimeMs >= captureThreshold;

        if (!worthItsOwnIncident)
        {
            return;
        }

        var captureThis = TryReserveAutoCapture(timestamp, trigger);
        CreateMarker(timestamp, trigger.Severity, trigger.Label, allowDeepCapture: captureThis, trigger.FrameTimeMs);
    }

    /// <summary>
    /// Asks the session's capture budget for this hitch, reporting the reason when it refuses for a
    /// reason the user would otherwise have to guess at.
    /// </summary>
    private bool TryReserveAutoCapture(DateTimeOffset timestamp, double frameTimeMs)
    {
        var budget = _autoCaptureBudget;
        return budget is not null && ReportRefusal(budget.TryReserve(timestamp, frameTimeMs, out var refusal), refusal);
    }

    /// <summary>
    /// Reserves according to what the detector observed. A dropped-frame run is a freeze made from
    /// ordinary frame times, so it deliberately skips the millisecond gate while sharing the same
    /// cooldown and session/window budgets as every other automatic capture.
    /// </summary>
    private bool TryReserveAutoCapture(DateTimeOffset timestamp, AutoIncidentTrigger trigger)
    {
        var budget = _autoCaptureBudget;
        if (budget is null)
        {
            return false;
        }

        return trigger.Kind == AutoIncidentKind.DroppedFrameRun
            ? ReportRefusal(budget.TryReserveForDroppedFrameRun(timestamp, out var refusal), refusal)
            : TryReserveAutoCapture(timestamp, trigger.FrameTimeMs);
    }

    /// <summary>As above, for a frame rate that has stopped recovering rather than one bad frame.</summary>
    private bool TryReserveSaturationCapture(DateTimeOffset timestamp)
    {
        var budget = _autoCaptureBudget;
        return budget is not null && ReportRefusal(budget.TryReserveForSustainedSaturation(timestamp, out var refusal), refusal);
    }

    private bool ReportRefusal(bool reserved, string? refusal)
    {
        if (!reserved && refusal is not null)
        {
            // Only the budget and cooldown refusals carry a reason; an ordinary spike below the capture
            // threshold returns null precisely so it does not fill the log.
            Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), refusal);
        }

        return reserved;
    }

    /// <summary>
    /// Records a classified pacing window and raises an incident when the session enters, or stays in,
    /// a state where the frame rate target is not being met.
    /// </summary>
    /// <remarks>
    /// Every window is journalled, healthy ones included. The per-minute state of a whole session is
    /// the single most useful artefact this app produces — "104 of 391 minutes could not hold 60 fps"
    /// is the finding, and it can only be seen by looking at the minutes that were fine next to the
    /// ones that were not. Incidents are raised sparingly by comparison: once when a bad patch starts,
    /// then on a reminder cadence, because a half hour of saturation is one condition rather than
    /// thirty of them.
    /// </remarks>
    private void OnPacingWindow(FramePacingWindow window)
    {
        _journal?.WritePacingWindow(window);
        FramePacingWindowCompleted?.Invoke(this, window);

        if (window.State != FramePacingState.Saturated)
        {
            return;
        }

        // Counted from the transition rather than from zero. The modulus has to be against the number
        // of windows *since* the bad patch began, or an interval of one — meaning "every window" —
        // matches nothing, because every integer is divisible by one.
        var windowsSinceTransition = window.SustainedWindows - 1;
        var isReminder = windowsSinceTransition > 0
            && windowsSinceTransition % _settings.FramePacing.SustainedReminderWindows == 0;
        if (!window.IsTransition && !isReminder)
        {
            return;
        }

        var minutes = window.SustainedWindows * _settings.FramePacing.WindowLength.TotalMinutes;
        var label = window.IsTransition
            ? $"Auto: FPS-taket nått, {window.AchievedFps:F0} fps mot {window.TargetFps:F0}"
            : $"Auto: FPS-taket nått i {minutes:F0} min, {window.AchievedFps:F0} fps mot {window.TargetFps:F0}";

        // Severe on the sustained reminder rather than at the transition: a single minute below target
        // is common, and what makes this the worst thing in a session is that it does not recover.
        var severity = window.IsTransition ? IncidentSeverity.Normal : IncidentSeverity.Severe;
        MarkAutoIncident(
            window.End,
            new AutoIncidentTrigger(severity, label),
            sustainedSaturation: severity == IncidentSeverity.Severe);
        Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), window.Describe());
    }

    /// <param name="allowDeepCapture">
    /// Whether this marker is permitted to save the ring buffer at all. Manual markers always are, and
    /// then the severity rules below decide; automatic ones arrive here having already been through
    /// <see cref="AutoDeepCaptureBudget"/>, so a true here means the budget was spent and the capture
    /// must happen regardless of severity.
    /// </param>
    private IncidentMarker? CreateMarker(
        DateTimeOffset timestamp,
        IncidentSeverity severity,
        string? label,
        bool allowDeepCapture,
        double frameTimeMs = 0,
        bool severityGated = false,
        bool allowFrameEscalation = true)
    {
        if (_incidentMaterializer is null)
        {
            return null;
        }

        var marker = _incidentMaterializer.MarkIncident(timestamp, severity, label, frameTimeMs, allowFrameEscalation);
        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Incident markerad: {marker.Label} ({marker.Severity}).");

        var shouldDeepCapture = !severityGated
            || severity == IncidentSeverity.Severe
            || _settings.DeepCapture.CaptureNormalManualIncidents;
        if (allowDeepCapture && shouldDeepCapture && _settings.DeepCapture.Enabled && _sessionCts is not null)
        {
            StartDeepCapture(marker, _sessionCts.Token);
        }

        OnStateChanged();
        return marker;
    }

    /// <summary>
    /// Runs a deep capture for a marker on its own task, tracked so stopping the session waits for it.
    /// </summary>
    /// <remarks>
    /// The token is read by the caller rather than inside the task: by the time the task runs, stopping
    /// the session may already have replaced the source with null.
    /// </remarks>
    private void StartDeepCapture(IncidentMarker marker, CancellationToken sessionToken)
    {
        var capture = Task.Run(() => CaptureDeepTraceAsync(marker, sessionToken));

        lock (_sync)
        {
            _deepCaptureTasks.RemoveAll(task => task.IsCompleted);
            _deepCaptureTasks.Add(capture);
        }
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

        // Counted like every other incident. A demo scenario added during a live session ends up in the
        // session's own history, so leaving it out of the tally made the end-of-session ranking
        // disagree with the incident list it is supposed to summarise.
        _verdicts?.Record(analyzed.Marker.Id, analyzed.Analysis?.Hypotheses.FirstOrDefault()?.Category);

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

    /// <summary>
    /// Writes the periodic reconciliation between the per-process VRAM table and the adapter's own
    /// figure into the session journal.
    /// </summary>
    /// <remarks>
    /// Info while the gap is the ordinary one, Warning once the sum exceeds what the card says is in
    /// use — a state that is impossible rather than merely surprising, and that went unnoticed for a
    /// whole session because every individual row still looked reasonable.
    /// </remarks>
    /// <summary>
    /// Marks process rows that have been proved to double count, naming each one the first time.
    /// </summary>
    /// <remarks>
    /// The comparison needs both collectors, so it cannot live in either: the process table comes from
    /// the Windows counter set and the card's own figure from NVML, and this pump is where they meet.
    /// </remarks>
    private GpuProcessMemorySample AnnotateVramAccounting(GpuProcessMemorySample sample)
    {
        if (_vramAccounting is not { } accounting)
        {
            return sample;
        }

        var annotated = accounting.Annotate(sample, out var newlyProven);

        foreach (var row in newlyProven)
        {
            var process = row.Process;
            var proof = row.Proof == DoubleCountProof.ExceedsAdapter
                ? $"rapporterar {process.DedicatedGigabytes:F1} GB VRAM, vilket är mer än kortet självt anger som "
                    + "använt. En enskild process kan inte hålla mer än hela kortet, så raden dubbelräknar minne som "
                    + "tillhör någon annan"
                : $"håller {process.DedicatedGigabytes:F1} GB enligt räknaren, och det är precis den post som gör att "
                    + "processtabellen summerar högre än vad kortet rapporterar som använt. Räknar man bort just den "
                    + "raden stämmer tabellen mot kortet igen, så den dubbelräknar minne som tillhör någon annan";

            Report(
                StatusLevel.Warning,
                "GpuProcessMemory.Accounting",
                $"{process.ProcessName} {proof} — typiskt kompositorn som håller en referens till spelets ytor. "
                + "Raden loggas men utesluts ur rapporternas topplistor och ur VRAM-budgeten för resten av "
                + "sessionen, så att den process som faktiskt växer syns.");
        }

        return annotated;
    }

    private void ReportVramAccounting(GpuProcessMemorySample sample)
    {
        if (_vramAccounting?.Observe(sample) is not { } report)
        {
            return;
        }

        Report(
            report.IsImplausible ? StatusLevel.Warning : StatusLevel.Info,
            "GpuProcessMemory.Accounting",
            report.Message);
    }

    /// <summary>
    /// Writes what the card's memory is committed to before the game asks for any.
    /// </summary>
    /// <remarks>
    /// Info rather than Warning: it is a budget, not a fault. Written once the session has both figures
    /// and the game has allocated something, and again when the stream stack starts or stops, which is
    /// the only term of it that moves during an evening.
    /// </remarks>
    /// <summary>
    /// Keeps the recent adapter occupancy so a finished trace can be told what the card was doing.
    /// </summary>
    /// <remarks>
    /// Trimmed to <see cref="AdapterVramWindow"/> on every sample, which at a two hertz cadence is a few
    /// hundred entries and never grows.
    /// </remarks>
    private void RecordAdapterVram(GpuTelemetrySample sample)
    {
        // The same guard the budget monitor keeps, and for the same reason. NVML reports device index 0
        // for the whole session while the trace describes whichever adapter the game renders on, so on a
        // machine with a second NVIDIA device this history would answer "how full was the card during
        // that trace" with a different card's occupancy — and the answer decides whether the driver's
        // paging is read as eviction. Unconfirmed counts are treated as multi-adapter: the parser
        // already says only what it measured when the probe returns null.
        if (!sample.IsAvailable || !sample.IsSingleAdapterMachine || sample.VramUsagePercent is not { } percent)
        {
            return;
        }

        lock (_adapterVramSync)
        {
            _adapterVram.Add((sample.Timestamp, percent));

            var cutoff = sample.Timestamp - AdapterVramWindow;
            var stale = 0;
            while (stale < _adapterVram.Count && _adapterVram[stale].At < cutoff)
            {
                stale++;
            }

            if (stale > 0)
            {
                _adapterVram.RemoveRange(0, stale);
            }
        }
    }

    /// <summary>
    /// How full the card has been over the last few minutes, or null when nothing was measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The median rather than the latest reading, and a window rather than an instant, because of when
    /// this is asked. A deep capture runs its tail, writes several hundred megabytes and is then parsed,
    /// so the question "how full was the card during that trace" arrives a minute or two after the
    /// trace ended. A single reading taken at parse time can easily belong to a different part of the
    /// evening; the median of the window the capture sits inside cannot.
    /// </para>
    /// <para>
    /// The median also refuses to be moved by the capture's own cost. Writing the file evicts nothing,
    /// but the game keeps allocating while it is written, and the last sample before the parse is the
    /// one most likely to have drifted.
    /// </para>
    /// </remarks>
    private double? RecentAdapterVramPercent()
    {
        double[] readings;
        lock (_adapterVramSync)
        {
            if (_adapterVram.Count == 0)
            {
                return null;
            }

            readings = _adapterVram.Select(entry => entry.Percent).ToArray();
        }

        Array.Sort(readings);
        return readings[readings.Length / 2];
    }

    /// <summary>
    /// Takes the adapter occupancy away from the trace parsers and forgets it, so nothing outside a
    /// running session can be interpreted against it.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The parsers are constructed once and outlive every session on them, so a
    /// probe left attached answers an ETL imported an hour later with whatever the last session
    /// measured — and a history left behind answers the next session's first trace with the previous
    /// one's card. Either produces an eviction verdict about an evening that is over.
    /// </remarks>
    private void DetachAdapterVramHistory()
    {
        foreach (var vramAware in _artifactParsers.OfType<IVramAwareTraceAnalysis>())
        {
            vramAware.AdapterVramPercent = null;
        }

        lock (_adapterVramSync)
        {
            _adapterVram.Clear();
        }
    }

    private void ReportVramBudget(GpuProcessMemorySample sample)
    {
        if (_vramBudget?.Observe(sample) is { } report)
        {
            Report(StatusLevel.Info, "GpuProcessMemory.Budget", report.Message);
        }

        // A warning rather than an Info: it says the card will enter the band before the game stops
        // growing, which is a prediction somebody can still act on while the evening is running.
        if (_vramBudget?.DescribeBudgetApproach() is { } approach)
        {
            Report(StatusLevel.Warning, "GpuProcessMemory.Budget", approach);
        }
    }

    /// <summary>
    /// Refreshes the live VRAM view and says so when a process takes a large amount of the card at once.
    /// </summary>
    /// <remarks>
    /// The status line is deliberately the quieter half of this. Someone mid-stream is not reading the
    /// log, so the table is what they will actually open the app for; the line exists so the step is in
    /// the session record afterwards, where the 29 August Voicemod step had to be found by diffing a
    /// column of CSV.
    /// </remarks>
    private void ReportLiveVram(GpuProcessMemorySample sample)
    {
        if (_liveVram?.Observe(sample) is not { } snapshot)
        {
            return;
        }

        foreach (var growth in snapshot.Growth)
        {
            Report(StatusLevel.Info, "GpuProcessMemory.Live", growth.Message);
        }

        LiveVramUpdated?.Invoke(this, snapshot);
    }

    private async Task PumpAsync(ChannelReader<TelemetryEvent> reader, CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var telemetryEvent))
            {
                // Before the ring buffer, so an incident window holds the annotated table rather than
                // the raw one: the rows that cannot be believed have to be marked in the copy the
                // reports are built from, not only in the copy the live view shows.
                if (telemetryEvent is GpuProcessMemorySample rawGpuProcessSample)
                {
                    telemetryEvent = AnnotateVramAccounting(rawGpuProcessSample);
                }

                _ringBuffer?.Add(telemetryEvent);

                if (telemetryEvent is SystemTelemetrySample systemSample)
                {
                    SystemTelemetryUpdated?.Invoke(this, systemSample);
                }
                else if (telemetryEvent is GpuTelemetrySample gpuSample)
                {
                    RecordAdapterVram(gpuSample);
                    _vramAccounting?.Observe(gpuSample);
                    _vramBudget?.Observe(gpuSample);
                    _vramPressure?.Observe(gpuSample);
                    GpuTelemetryUpdated?.Invoke(this, gpuSample);
                }
                else if (telemetryEvent is GpuProcessMemorySample gpuProcessSample)
                {
                    ReportVramAccounting(gpuProcessSample);
                    ReportVramBudget(gpuProcessSample);
                    ReportLiveVram(gpuProcessSample);
                    GpuProcessMemoryUpdated?.Invoke(this, gpuProcessSample);
                }
                else if (telemetryEvent is CaptureHealthTelemetrySample healthSample)
                {
                    CaptureHealthUpdated?.Invoke(this, healthSample);
                }
                else if (telemetryEvent is FrameTelemetrySample frameSample)
                {
                    // Every frame, not only the ones that trigger something: the capture thresholds are
                    // derived from the session's own distribution, and a sample taken only from frames
                    // that already crossed a threshold would describe the threshold rather than the
                    // evening.
                    _autoCaptureBudget?.Observe(frameSample.Timestamp, frameSample.FrameTimeMs);

                    if (frameSample.FrameTimeMs >= _stallFrameThresholdMs)
                    {
                        _lastStallFrameAtUtc = DateTimeOffset.UtcNow;
                    }

                    _displayCadence?.Observe(frameSample);
                    _captureCost?.Observe(frameSample);
                    _vramPressure?.Observe(frameSample);
                    _slowFrameWaits?.Observe(frameSample);

                    // The marker has to be raised before the materializer sees this event, so the frame
                    // that triggered the incident lands inside its own window rather than one event
                    // short of it.
                    if (_autoDetector?.Observe(frameSample) is { } observation)
                    {
                        if (observation.IsSuppressed)
                        {
                            EscalateOpenIncident(frameSample.Timestamp, observation.Trigger, observation.Suppression);
                        }
                        else
                        {
                            MarkAutoIncident(frameSample.Timestamp, observation.Trigger);
                        }
                    }

                    if (_framePacing?.Observe(frameSample) is { } pacingWindow)
                    {
                        OnPacingWindow(pacingWindow);
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
            var now = DateTimeOffset.UtcNow;
            await FlushPendingIncidentsAsync(now).ConfigureAwait(false);
            CheckGameSettings(now);
            CheckTargetProcess();
            CheckInterimSummaries(now);
        }
    }

    /// <summary>
    /// Re-reads the graphics settings file when it is due, and reports whatever moved.
    /// </summary>
    /// <remarks>
    /// On the cadence rather than at session end, because the session on this machine does not end: the
    /// computer is switched off with it still running, so the final read runs on a minority of evenings.
    /// A settings change mid-session splits the telemetry into two configurations, and a reader
    /// comparing the evening against another one needs to know that before doing the arithmetic.
    /// </remarks>
    /// <summary>
    /// Writes the running summaries when they are due.
    /// </summary>
    private void CheckInterimSummaries(DateTimeOffset now)
    {
        if (now < _nextInterimSummary)
        {
            return;
        }

        _nextInterimSummary = now + InterimSummaryInterval;
        WriteSessionSummaries(final: false);
    }

    /// <summary>
    /// Says once that the game has closed, and writes the summaries while the evening's numbers are
    /// still in memory.
    /// </summary>
    /// <remarks>
    /// The moment the session's measurements stop changing, and therefore the last moment at which they
    /// are worth writing down. Before this the app said nothing about it as such: three collectors each
    /// reported a process that had gone away, from their own point of view, none of which reads as
    /// "the evening is over".
    /// </remarks>
    private void CheckTargetProcess()
    {
        var target = _processResolver.TryGetTargetProcess();
        if (target is not null)
        {
            _targetProcessSeen = true;
            _reportedTargetProcessExit = false;
            return;
        }

        if (!_targetProcessSeen || _reportedTargetProcessExit)
        {
            return;
        }

        _reportedTargetProcessExit = true;
        Report(
            StatusLevel.Info,
            "Spelprocess",
            "Spelet avslutades. Mätningen pausas här och sessionen fortsätter tills du stoppar den; "
            + "sammanfattningarna nedan gäller det som hann mätas.");

        WriteSessionSummaries(final: false);
    }

    private void CheckGameSettings(DateTimeOffset now)
    {
        if (_gameSettings is not { } monitor || now < _nextGameSettingsCheck)
        {
            return;
        }

        _nextGameSettingsCheck = now + GameSettingsCheckInterval;

        if (monitor.DescribeChange() is { } change)
        {
            Report(StatusLevel.Warning, "GameSettings", change);
        }

        // The re-read is what makes this check worth doing, and until now only the log saw it. A texture
        // budget raised between two attempts, or a window put into fullscreen, changed the line written
        // here and nothing that reasons about either — the budget monitor kept advising against the
        // ceiling the session started with, for hours.
        ApplyGameSettingsToMonitors();
    }

    /// <summary>
    /// Pushes the settings as last read into the parts of the session that reason about them.
    /// </summary>
    /// <remarks>
    /// Called at start and after every re-read, and it assigns rather than sets: the window mode can
    /// stop explaining the compositor as well as start, and the analysis engine outlives the session, so
    /// a flag that is only ever raised carries the last session that ran windowed into every one after
    /// it and quietly drops present-mode evidence from incidents nothing has explained.
    /// </remarks>
    private void ApplyGameSettingsToMonitors()
    {
        // The budget monitor states the game's ceiling against the card, and the ceiling is a number in
        // a config file rather than something to be inferred from a plateau three hours later.
        _vramBudget?.SetTextureBudget(_gameSettings?.ClientConfig);

        RecordWindowMode(_gameSettings?.ExplainsComposedPresent == true);
    }

    /// <summary>
    /// Notes the window mode as it stands now, so an incident can later be asked about its own moment.
    /// </summary>
    private void RecordWindowMode(bool explained)
    {
        lock (_windowModeSync)
        {
            if (_windowModeHistory.Count > 0 && _windowModeHistory[^1].Explained == explained)
            {
                return;
            }

            _windowModeHistory.Add((DateTimeOffset.UtcNow, explained));
        }
    }

    /// <summary>
    /// Whether the window mode in force at a given moment explained a composed present path.
    /// </summary>
    /// <remarks>
    /// A timestamp before the first reading gets that reading's answer. The first entry is written at
    /// session start, and the events an incident is built from come out of a ring buffer that reaches
    /// back before it — so the alternative is to tell the session's first incident that nothing was
    /// known, which is false: the settings file was read before a single frame was collected.
    /// </remarks>
    private bool ComposedPresentExplainedAt(DateTimeOffset at)
    {
        lock (_windowModeSync)
        {
            if (_windowModeHistory.Count == 0)
            {
                return false;
            }

            var explained = _windowModeHistory[0].Explained;
            foreach (var entry in _windowModeHistory)
            {
                if (entry.At > at)
                {
                    break;
                }

                explained = entry.Explained;
            }

            return explained;
        }
    }

    /// <summary>
    /// Takes the window mode away from the analysis engine and forgets it.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="DetachAdapterVramHistory"/>, and the same pair of halves: the engine
    /// is the instance the next session will use, so a delegate left attached would answer that session
    /// with this evening's window modes — and a history left behind would answer it even after the
    /// delegate was replaced. Safe here because the analysis queue is drained and its worker awaited
    /// before this runs, so no incident is still waiting for an answer.
    /// </remarks>
    private void DetachWindowModeHistory()
    {
        if (_analysisEngine is IWindowModeAwareAnalysis windowAware)
        {
            windowAware.ComposedPresentExplainedAt = null;
        }

        lock (_windowModeSync)
        {
            _windowModeHistory.Clear();
        }
    }

    /// <summary>
    /// Takes one last look at the settings file, for the sessions that do get stopped properly.
    /// </summary>
    private void FinalizeGameSettings()
    {
        if (_gameSettings?.DescribeChange() is { } change)
        {
            Report(StatusLevel.Warning, "GameSettings", change);
        }

        _gameSettings = null;
        _nextGameSettingsCheck = DateTimeOffset.MaxValue;

        // With no settings behind it there is nothing to explain a compositor or measure a budget
        // against, and the analysis engine is the same instance the next session will use. The history
        // is dropped rather than extended with a "no longer known" entry: every incident that could ask
        // about it has already been analysed by the time this runs.
        _vramBudget?.SetTextureBudget(null);
        DetachWindowModeHistory();
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
        IReadOnlyList<IncidentRecord> evicted;

        // Taking the pending evidence and adding the incident have to be one critical section, or a
        // capture finishing between them would find no incident to attach to and leave its evidence in a
        // map nothing reads again.
        lock (_sync)
        {
            if (_pendingTraceEvidence.Remove(analyzed.Marker.Id, out var pending))
            {
                foreach (var (evidence, attachment) in pending)
                {
                    analyzed = WithEvidence(analyzed, evidence, attachment);
                }

                analyzed = Analyze(analyzed);
            }

            evicted = AddIncidentWithinCap(analyzed);
        }

        // Counted by marker rather than by publication, so a re-analysis replaces the verdict instead of
        // adding a second one for the same incident.
        _verdicts?.Record(analyzed.Marker.Id, analyzed.Analysis?.Hypotheses.FirstOrDefault()?.Category);

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
    /// Folds one piece of evidence and the file behind it into an incident that is already complete.
    /// </summary>
    /// <remarks>
    /// The attachment is added only when it is new, so importing the same file twice does not grow the
    /// export bundle by a duplicate of itself.
    /// </remarks>
    private static IncidentRecord WithEvidence(IncidentRecord incident, ArtifactEvidence evidence, ArtifactAttachment attachment)
    {
        return incident with
        {
            Events = incident.Events.Concat([evidence]).OrderBy(item => item.Timestamp).ToArray(),
            Attachments = incident.Attachments.Any(item => string.Equals(item.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase))
                ? incident.Attachments
                : incident.Attachments.Concat([attachment]).ToArray(),
        };
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
        TryAttachEvidenceToIncident(markerId: null, evidence, attachment);
    }

    /// <param name="markerId">
    /// The incident the evidence belongs to, or null for the most recent one. A capture the app took
    /// itself knows exactly which marker it was started for, and by the time the ETL has been parsed
    /// that incident need no longer be the latest — a five hundred megabyte trace takes long enough to
    /// read that another hitch can arrive first, and attaching a trace of one freeze to a different
    /// freeze is worse than attaching it to nothing.
    /// </param>
    private void TryAttachEvidenceToIncident(Guid? markerId, ArtifactEvidence evidence, ArtifactAttachment attachment)
    {
        IncidentRecord updated;

        lock (_sync)
        {
            var index = markerId is { } id
                ? _incidents.FindLastIndex(item => item.Marker.Id == id)
                : _incidents.Count - 1;

            if (index < 0)
            {
                // A capture whose incident has not been published yet, which is the ordinary case for a
                // trace the app took itself. Held until publication rather than written to the telemetry
                // channel: the window may already have been finalized, and then the channel goes
                // nowhere.
                if (markerId is { } pendingId)
                {
                    if (!_pendingTraceEvidence.TryGetValue(pendingId, out var pending))
                    {
                        pending = [];
                        _pendingTraceEvidence[pendingId] = pending;
                    }

                    if (!pending.Any(item => item.Evidence.Summary == evidence.Summary
                        && string.Equals(item.Attachment.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        pending.Add((evidence, attachment));
                    }
                }

                return;
            }

            var latest = _incidents[index];
            if (latest.Attachments.Any(item => string.Equals(item.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase))
                && latest.Events.OfType<ArtifactEvidence>().Any(item => item.Summary == evidence.Summary))
            {
                return;
            }

            updated = Analyze(WithEvidence(latest, evidence, attachment));
            _incidents[index] = updated;
        }

        _verdicts?.Record(updated.Marker.Id, updated.Analysis?.Hypotheses.FirstOrDefault()?.Category);

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
                // Timed here rather than when the capture was requested: what disturbs the game is the
                // flush of the ring buffer to disk, which is what has just finished.
                _captureCost?.RecordCaptureWritten(DateTimeOffset.UtcNow);

                // And the budget, which otherwise has to assume this capture is still recording and
                // holds the next extreme frame off for the longest tail the options allow.
                _autoCaptureBudget?.NoteCaptureWritten(DateTimeOffset.UtcNow);

                lock (_sync)
                {
                    _attachments.Add(new ArtifactAttachment(result.CapturePath, ArtifactKind.EtlTrace, Path.GetFileName(result.CapturePath), DateTimeOffset.UtcNow, Sensitive: true));
                    InvalidateAttachmentsSnapshot();
                }

                await AnalyzeCapturedTraceAsync(marker, result.CapturePath!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), $"Deep capture misslyckades: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a capture the app took itself back into the incident that triggered it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A trace nothing parses is a file, not evidence. Attaching the ETL and stopping there is what left
    /// the rule that a trace overrules <c>MsCPUBusy</c> — the one this app has been asked for four
    /// sessions running — with nothing to act on: the traces were taken, and every incident was still
    /// ranked on a figure PresentMon derives from the gap between presents and which reads identically
    /// for a thread executing and a thread asleep.
    /// </para>
    /// <para>
    /// The evidence is stamped with the marker rather than with the time the parse finished, because it
    /// describes the hitch and belongs at that point of the timeline. Both destinations are used for the
    /// same reason importing by hand uses both: an incident whose window is still open takes it through
    /// the channel, and one already materialized has to be updated in place or the trace would reach
    /// nothing. Whichever runs second finds the evidence already there and stops.
    /// </para>
    /// </remarks>
    private async Task AnalyzeCapturedTraceAsync(IncidentMarker marker, string capturePath, CancellationToken cancellationToken)
    {
        if (!_settings.DeepCapture.AnalyzeAutomaticCaptures)
        {
            return;
        }

        var parser = _artifactParsers.FirstOrDefault(candidate => candidate.CanParse(capturePath));
        if (parser is null)
        {
            return;
        }

        ArtifactParseResult? result;
        try
        {
            result = await parser.ParseAsync(capturePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The session is stopping. The ETL is on disk and can still be imported by hand.
            return;
        }
        catch (Exception ex)
        {
            Report(StatusLevel.Warning, nameof(DiagnosticsSessionManager), $"Deep capture kunde inte analyseras: {ex.Message}");
            return;
        }

        if (result is null || result.Evidence.Count == 0)
        {
            return;
        }

        foreach (var evidence in result.Evidence)
        {
            var stamped = new ArtifactEvidence(
                marker.MarkedAt,
                evidence.Kind,
                evidence.Summary + DescribeMarkerAgainstTrace(marker, evidence.Metrics),
                evidence.Metrics,
                evidence.SourceFile);
            TryAttachEvidenceToIncident(marker.Id, stamped, result.Attachment);
        }

        Report(StatusLevel.Info, nameof(DiagnosticsSessionManager), $"Deep capture analyserad: {result.Evidence[0].Summary}");
    }

    /// <summary>
    /// Says where the marker falls relative to the window the trace actually covers.
    /// </summary>
    /// <remarks>
    /// The parser knows what the file holds and nothing about why it was taken; this call knows the
    /// marker and nothing about the file. Putting the two in one sentence is what makes an attachment
    /// checkable — "the trace covers 20:23:31–20:23:52; the hitch happened at 20:23:48" is read in a
    /// second, where the same judgement previously took opening the ETL. It is also the other half of
    /// the 30 August finding, where an incident carried the label of a frame that happened 37 seconds
    /// after its trace ended.
    /// </remarks>
    private static string DescribeMarkerAgainstTrace(IncidentMarker marker, IReadOnlyDictionary<string, double> metrics)
    {
        if (!metrics.TryGetValue("traceCoveredStartUnixMs", out var startMs)
            || !metrics.TryGetValue("traceCoveredEndUnixMs", out var endMs)
            || endMs <= startMs)
        {
            return string.Empty;
        }

        var start = DateTimeOffset.FromUnixTimeMilliseconds((long)startMs);
        var end = DateTimeOffset.FromUnixTimeMilliseconds((long)endMs);
        var markedAt = marker.MarkedAt;
        var verdict = markedAt >= start && markedAt <= end
            ? "alltså inuti det fönster tracen täcker"
            : markedAt < start
                ? $"alltså {(start - markedAt).TotalSeconds:F0} s före tracens början — bilagan beskriver inte händelsen"
                : $"alltså {(markedAt - end).TotalSeconds:F0} s efter tracens slut — bilagan beskriver inte händelsen";

        return $" Markeringen gjordes {markedAt.ToLocalTime():HH:mm:ss}, {verdict}.";
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
