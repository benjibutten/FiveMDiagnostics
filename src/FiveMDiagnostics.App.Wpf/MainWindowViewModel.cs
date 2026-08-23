using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Fakes;
using FiveMDiagnostics.Integrations.PresentMon;

public sealed class MainWindowViewModel : ObservableObject
{
    private const int MaxStatusEntries = 100;
    private const int MaxStatusEntriesPerFlush = 12;

    private readonly DiagnosticsSessionManager _sessionManager;
    private readonly SettingsStore _settingsStore;
    private readonly IUserDialogService _dialogService;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly ConcurrentQueue<DiagnosticStatusEntry> _pendingStatusEntries = new();
    private readonly HashSet<Guid> _pendingIncidentIds = [];

    private IncidentRecord? _selectedIncident;
    private bool _isSessionActive;
    private string _activeProcessText = Strings.WaitingForProcess;
    private string _serverProfileName = string.Empty;
    private string? _probeHost;
    private string? _endpointHint;
    private string? _presentMonExecutablePath;
    private string _exportDirectory = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _artifactDirectory = string.Empty;
    private bool _includeSensitiveFields;
    private bool _includeAttachedArtifacts;
    private bool _autoDetectEnabled;
    private bool _captureNormalManualIncidents;
    private bool _isReadyForIncident;
    private bool _stateRefreshPending;
    private string _selectedLanguage = string.Empty;
    private string _presentMonStatusText = string.Empty;
    private string _captureFeedbackText = string.Empty;
    private DateTimeOffset _lastStateRefreshUtc = DateTimeOffset.MinValue;
    private string _liveCpuText = Strings.LiveStatsIdle;
    private string _liveMemoryText = Strings.LiveStatsIdle;
    private string _liveVramText = Strings.LiveStatsIdle;
    private string _pacingSaturatedText = Strings.PacingIdle;
    private string _pacingWorstText = string.Empty;
    private string _pacingCurrentText = string.Empty;
    private string _pacingCaptureBudgetText = string.Empty;
    private bool _hasPacingData;

    public MainWindowViewModel(DiagnosticsSessionManager sessionManager, SettingsStore settingsStore, DiagnosticsSettings settings, IUserDialogService dialogService)
    {
        _sessionManager = sessionManager;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.ContextIdle, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _uiRefreshTimer.Tick += (_, _) => FlushUiUpdates();
        _uiRefreshTimer.Start();
        Settings = settings;

        _serverProfileName = settings.ServerProfile.Name;
        _probeHost = settings.ServerProfile.ProbeHost;
        _endpointHint = settings.ServerProfile.EndpointHint;
        _presentMonExecutablePath = settings.PresentMon.ExecutablePath;
        _exportDirectory = settings.ExportDirectory;
        _workingDirectory = settings.WorkingDirectory;
        _artifactDirectory = settings.ArtifactDirectory;
        _includeSensitiveFields = settings.Privacy.IncludeSensitiveFieldsInExport;
        _includeAttachedArtifacts = settings.Privacy.IncludeAttachedArtifactsInExport;
        _autoDetectEnabled = settings.AutoDetect.Enabled;
        _captureNormalManualIncidents = settings.DeepCapture.CaptureNormalManualIncidents;
        _selectedLanguage = settings.Language;

        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, () => !IsSessionActive);
        StopSessionCommand = new AsyncRelayCommand(StopSessionAsync, () => IsSessionActive);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ImportArtifactsCommand = new AsyncRelayCommand(ImportArtifactsAsync);
        ExportSelectedIncidentCommand = new AsyncRelayCommand(ExportSelectedIncidentAsync, () => SelectedIncident is not null || _sessionManager.LatestIncident is not null);
        MarkStutterCommand = new RelayCommand(() => MarkIncident(IncidentSeverity.Normal), () => IsSessionActive);
        MarkSevereStutterCommand = new RelayCommand(() => MarkIncident(IncidentSeverity.Severe), () => IsSessionActive);
        SimulateObsScenarioCommand = new RelayCommand(() => AddScenario(FakeScenarioKind.ObsGpuContention));
        SimulateResourceScenarioCommand = new RelayCommand(() => AddScenario(FakeScenarioKind.FiveMResourceSpike));
        SimulateNetworkScenarioCommand = new RelayCommand(() => AddScenario(FakeScenarioKind.NetworkIssue));

        _sessionManager.StateChanged += OnSessionStateChanged;
        _sessionManager.StatusReported += OnStatusReported;
        _sessionManager.IncidentCompleted += OnIncidentCompleted;
        _sessionManager.IncidentUpdated += OnIncidentUpdated;
        _sessionManager.IncidentsEvicted += OnIncidentsEvicted;
        _sessionManager.SystemTelemetryUpdated += OnSystemTelemetryUpdated;
        _sessionManager.GpuTelemetryUpdated += OnGpuTelemetryUpdated;
        _sessionManager.CaptureHealthUpdated += OnCaptureHealthUpdated;
        _sessionManager.FramePacingWindowCompleted += OnFramePacingWindowCompleted;

        foreach (var incident in _sessionManager.GetRecentIncidents())
        {
            Incidents.Add(incident);
        }

        foreach (var status in _sessionManager.GetStatusEntries().Reverse())
        {
            StatusEntries.Add(status);
        }

        RefreshPresentMonStatus();
        CaptureFeedbackText = Strings.CaptureFeedbackHint;
        RefreshState();
    }

    public DiagnosticsSettings Settings { get; }

    public ObservableCollection<IncidentRecord> Incidents { get; } = [];

    public ObservableCollection<DiagnosticStatusEntry> StatusEntries { get; } = [];

    public AsyncRelayCommand StartSessionCommand { get; }

    public AsyncRelayCommand StopSessionCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public AsyncRelayCommand ImportArtifactsCommand { get; }

    public AsyncRelayCommand ExportSelectedIncidentCommand { get; }

    public RelayCommand MarkStutterCommand { get; }

    public RelayCommand MarkSevereStutterCommand { get; }

    public RelayCommand SimulateObsScenarioCommand { get; }

    public RelayCommand SimulateResourceScenarioCommand { get; }

    public RelayCommand SimulateNetworkScenarioCommand { get; }

    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set => SetProperty(ref _isSessionActive, value);
    }

    public string SessionStateText => IsSessionActive
        ? IsReadyForIncident ? Strings.SessionReady : Strings.SessionWarmingUp
        : Strings.SessionIdle;

    public bool IsReadyForIncident
    {
        get => _isReadyForIncident;
        private set
        {
            if (SetProperty(ref _isReadyForIncident, value))
            {
                OnPropertyChanged(nameof(SessionStateText));
            }
        }
    }

    public string ActiveProcessText
    {
        get => _activeProcessText;
        private set => SetProperty(ref _activeProcessText, value);
    }

    public string PresentMonStatusText
    {
        get => _presentMonStatusText;
        private set => SetProperty(ref _presentMonStatusText, value);
    }

    public string CaptureFeedbackText
    {
        get => _captureFeedbackText;
        private set => SetProperty(ref _captureFeedbackText, value);
    }

    public string LiveCpuText
    {
        get => _liveCpuText;
        private set => SetProperty(ref _liveCpuText, value);
    }

    public string LiveMemoryText
    {
        get => _liveMemoryText;
        private set => SetProperty(ref _liveMemoryText, value);
    }

    public string LiveVramText
    {
        get => _liveVramText;
        private set => SetProperty(ref _liveVramText, value);
    }

    /// <summary>
    /// How much of the session did not hold the frame rate. This is the headline number: "0 saturated
    /// minutes out of 341" is the answer to "did tonight go well", and three sessions of averages hid a
    /// problem that this one line makes obvious.
    /// </summary>
    public string PacingSaturatedText
    {
        get => _pacingSaturatedText;
        private set => SetProperty(ref _pacingSaturatedText, value);
    }

    public string PacingWorstText
    {
        get => _pacingWorstText;
        private set => SetProperty(ref _pacingWorstText, value);
    }

    public string PacingCurrentText
    {
        get => _pacingCurrentText;
        private set => SetProperty(ref _pacingCurrentText, value);
    }

    public string PacingCaptureBudgetText
    {
        get => _pacingCaptureBudgetText;
        private set => SetProperty(ref _pacingCaptureBudgetText, value);
    }

    /// <summary>False until the first window closes, so the panel shows a hint rather than zeroes.</summary>
    public bool HasPacingData
    {
        get => _hasPacingData;
        private set => SetProperty(ref _hasPacingData, value);
    }

    public string ServerProfileName
    {
        get => _serverProfileName;
        set
        {
            if (SetProperty(ref _serverProfileName, value))
            {
                Settings.ServerProfile.Name = value;
            }
        }
    }

    public string? ProbeHost
    {
        get => _probeHost;
        set
        {
            if (SetProperty(ref _probeHost, value))
            {
                Settings.ServerProfile.ProbeHost = value;
            }
        }
    }

    public string? EndpointHint
    {
        get => _endpointHint;
        set
        {
            if (SetProperty(ref _endpointHint, value))
            {
                Settings.ServerProfile.EndpointHint = value;
            }
        }
    }

    public string? PresentMonExecutablePath
    {
        get => _presentMonExecutablePath;
        set
        {
            if (SetProperty(ref _presentMonExecutablePath, value))
            {
                Settings.PresentMon.ExecutablePath = value;
                RefreshPresentMonStatus();
            }
        }
    }

    public string ExportDirectory
    {
        get => _exportDirectory;
        set
        {
            if (SetProperty(ref _exportDirectory, value))
            {
                Settings.ExportDirectory = value;
            }
        }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (SetProperty(ref _workingDirectory, value))
            {
                Settings.WorkingDirectory = value;
            }
        }
    }

    public string ArtifactDirectory
    {
        get => _artifactDirectory;
        set
        {
            if (SetProperty(ref _artifactDirectory, value))
            {
                Settings.ArtifactDirectory = value;
            }
        }
    }

    public bool IncludeSensitiveFields
    {
        get => _includeSensitiveFields;
        set
        {
            if (SetProperty(ref _includeSensitiveFields, value))
            {
                Settings.Privacy.IncludeSensitiveFieldsInExport = value;
            }
        }
    }

    public bool IncludeAttachedArtifacts
    {
        get => _includeAttachedArtifacts;
        set
        {
            if (SetProperty(ref _includeAttachedArtifacts, value))
            {
                Settings.Privacy.IncludeAttachedArtifactsInExport = value;
            }
        }
    }


    /// <summary>
    /// Bound live rather than only on save: if the detector misbehaves mid-session the user needs to
    /// silence it without stopping the session or editing settings.json.
    /// </summary>
    public bool AutoDetectEnabled
    {
        get => _autoDetectEnabled;
        set
        {
            if (SetProperty(ref _autoDetectEnabled, value))
            {
                Settings.AutoDetect.Enabled = value;
            }
        }
    }

    public bool CaptureNormalManualIncidents
    {
        get => _captureNormalManualIncidents;
        set
        {
            if (SetProperty(ref _captureNormalManualIncidents, value))
            {
                Settings.DeepCapture.CaptureNormalManualIncidents = value;
            }
        }
    }

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } = [new("en", Strings.EnglishLanguageName), new("sv", Strings.SwedishLanguageName)];

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                Settings.Language = value;
            }
        }
    }

    public IncidentRecord? SelectedIncident
    {
        get => _selectedIncident;
        set
        {
            if (SetProperty(ref _selectedIncident, value))
            {
                OnPropertyChanged(nameof(SelectedIncidentSummary));
                OnPropertyChanged(nameof(SelectedTimeline));
                OnPropertyChanged(nameof(SelectedHypotheses));
                ExportSelectedIncidentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedIncidentSummary => SelectedIncident?.Analysis?.Summary ?? Strings.SelectIncidentHint;

    public IReadOnlyList<TimelineHighlight> SelectedTimeline => SelectedIncident?.Analysis?.TimelineHighlights ?? [];

    public IReadOnlyList<HypothesisScore> SelectedHypotheses => SelectedIncident?.Analysis?.Hypotheses ?? [];

    private async Task StartSessionAsync()
    {
        await Task.Yield();
        await _sessionManager.StartSessionAsync().ConfigureAwait(false);
        await _dispatcher.InvokeAsync(RefreshState, DispatcherPriority.Background);
        CaptureFeedbackText = Strings.CaptureFeedbackSessionStarted;
    }

    private async Task StopSessionAsync()
    {
        await _sessionManager.StopSessionAsync().ConfigureAwait(false);
        await _dispatcher.InvokeAsync(RefreshState, DispatcherPriority.Background);
        _pendingIncidentIds.Clear();
        IsReadyForIncident = false;
        CaptureFeedbackText = Strings.CaptureFeedbackHint;
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(Settings).ConfigureAwait(true);
        _sessionManager.Report(StatusLevel.Info, nameof(MainWindowViewModel), string.Format(Strings.SettingsSavedFormat, _settingsStore.SettingsPath));
    }

    private async Task ImportArtifactsAsync()
    {
        var files = _dialogService.PickArtifactFiles();
        if (files.Length == 0)
        {
            return;
        }

        await _sessionManager.ImportArtifactsAsync(files).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(RefreshState, DispatcherPriority.Background);
    }

    private async Task ExportSelectedIncidentAsync()
    {
        var output = await _sessionManager.ExportIncidentAsync(SelectedIncident ?? _sessionManager.LatestIncident, IncludeSensitiveFields, IncludeAttachedArtifacts).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(output))
        {
            _dialogService.ShowInfo(Strings.IncidentExportedTitle, output);
        }
    }

    private void AddScenario(FakeScenarioKind kind)
    {
        var incident = _sessionManager.AddSyntheticIncident(FakeScenarioGenerator.Create(kind).ToIncidentRecord());
        if (!Incidents.Contains(incident))
        {
            Incidents.Insert(0, incident);
        }

        SelectedIncident = incident;
        CaptureFeedbackText = Strings.CaptureFeedbackSyntheticReady;
    }

    private void MarkIncident(IncidentSeverity severity)
    {
        var marker = _sessionManager.MarkIncident(severity);
        if (marker is null)
        {
            return;
        }

        _pendingIncidentIds.Add(marker.Id);
        var readyAt = marker.MarkedAt.ToLocalTime().Add(Settings.PostIncidentWindow);
        CaptureFeedbackText = string.Format(
            Strings.CaptureFeedbackCollectingFormat,
            marker.Label,
            readyAt.ToString("HH:mm:ss"),
            _pendingIncidentIds.Count);
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        RequestStateRefresh();
    }

    private void OnSystemTelemetryUpdated(object? sender, SystemTelemetrySample sample)
    {
        var cpu = $"{sample.TotalCpuUsagePercent:F0}%";
        var memory = string.Format(Strings.LiveRamFreeFormat, sample.AvailableMemoryMb.ToString("N0"));
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            LiveCpuText = cpu;
            LiveMemoryText = memory;
        }));
    }

    private void OnGpuTelemetryUpdated(object? sender, GpuTelemetrySample sample)
    {
        var text = sample is { IsAvailable: true, UsedVramBytes: { } used, TotalVramBytes: { } total } && total > 0
            ? string.Format(
                Strings.LiveVramFormat,
                (used / 1024d / 1024d / 1024d).ToString("F1"),
                (total / 1024d / 1024d / 1024d).ToString("F1"),
                (sample.VramUsagePercent ?? 0).ToString("F0"))
            : Strings.LiveVramUnavailable;

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => LiveVramText = text));
    }

    private void OnCaptureHealthUpdated(object? sender, CaptureHealthTelemetrySample sample)
    {
        var ready = sample.CaptureProcessRunning
            && sample.LastFrameAt is { } lastFrame
            && sample.Timestamp - lastFrame <= TimeSpan.FromSeconds(2)
            && sample.ContinuousFrameSpanSeconds >= Settings.PreIncidentWindow.TotalSeconds;

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            IsReadyForIncident = ready;
            if (_pendingIncidentIds.Count == 0)
            {
                CaptureFeedbackText = ready
                    ? Strings.CaptureFeedbackIncidentReady
                    : string.Format(Strings.CaptureFeedbackWarmingFormat, Math.Min(sample.ContinuousFrameSpanSeconds, Settings.PreIncidentWindow.TotalSeconds), Settings.PreIncidentWindow.TotalSeconds);
            }
        }));
    }

    /// <summary>
    /// Refreshes the pacing panel once per classified window — about once a minute, so this can format
    /// on the dispatcher without the batching the per-frame paths need.
    /// </summary>
    private void OnFramePacingWindowCompleted(object? sender, FramePacingWindow window)
    {
        var summary = _sessionManager.FramePacing;
        var windowMinutes = _sessionManager.Settings.FramePacing.WindowLength.TotalMinutes;
        var notHealthy = summary.SaturatedWindows + summary.MarginalWindows;

        var saturated = summary.TotalWindows == 0
            ? Strings.PacingIdle
            : string.Format(
                Strings.PacingSaturatedFormat,
                summary.SaturatedWindows,
                summary.TotalWindows,
                summary.SaturatedShare.ToString("P0"),
                summary.MarginalWindows,
                (summary.LongestSaturatedRun * windowMinutes).ToString("F0"));

        var worst = summary.TotalWindows == 0
            ? string.Empty
            : string.Format(Strings.PacingWorstFormat, summary.WorstFps.ToString("F1"), summary.TargetFps.ToString("F1"));

        // PresentMon v1 supplies no CPU wait, and the interpolation for a null double renders as nothing
        // at all — leaving "Saturated — 45.0 fps, CPU headroom  ms" on screen. The measurement is the
        // whole point of the panel, so its absence gets its own wording rather than a blank.
        var current = window.MedianCpuWaitMs is { } wait
            ? string.Format(Strings.PacingCurrentFormat, window.State, window.AchievedFps.ToString("F1"), wait.ToString("F1"))
            : string.Format(Strings.PacingCurrentNoHeadroomFormat, window.State, window.AchievedFps.ToString("F1"));

        var budget = string.Format(
            Strings.PacingCaptureBudgetFormat,
            _sessionManager.RemainingAutoCaptures,
            _sessionManager.Settings.DeepCapture.MaxAutoCapturesPerSession);

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            HasPacingData = summary.TotalWindows > 0 || notHealthy > 0;
            PacingSaturatedText = saturated;
            PacingWorstText = worst;
            PacingCurrentText = current;
            PacingCaptureBudgetText = budget;
        }));
    }

    private void OnStatusReported(object? sender, DiagnosticStatusEntry status)
    {
        _pendingStatusEntries.Enqueue(status);
    }

    private void OnIncidentCompleted(object? sender, IncidentRecord incident)
    {
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // A synthetic scenario inserts itself before this queued callback runs, so the list would
            // otherwise show it twice.
            if (Incidents.Any(item => item.Id == incident.Id))
            {
                return;
            }

            Incidents.Insert(0, incident);
            SelectedIncident ??= incident;
            _pendingIncidentIds.Remove(incident.Id);
            CaptureFeedbackText = _pendingIncidentIds.Count > 0
                ? string.Format(Strings.CaptureFeedbackReadyWithPendingFormat, incident.Marker.Label, _pendingIncidentIds.Count)
                : string.Format(Strings.CaptureFeedbackReadyFormat, incident.Marker.Label);
            ExportSelectedIncidentCommand.RaiseCanExecuteChanged();
        }));
    }

    /// <summary>
    /// Incidents are immutable records, so a re-analysis produces a new instance that has to replace the
    /// old one in the list — and be re-selected if the user was looking at it.
    /// </summary>
    private void OnIncidentUpdated(object? sender, IncidentRecord incident)
    {
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var index = -1;
            for (var position = 0; position < Incidents.Count; position++)
            {
                if (Incidents[position].Id == incident.Id)
                {
                    index = position;
                    break;
                }
            }

            if (index < 0)
            {
                return;
            }

            var wasSelected = SelectedIncident?.Id == incident.Id;
            Incidents[index] = incident;

            if (wasSelected)
            {
                SelectedIncident = incident;
            }
        }));
    }

    /// <summary>
    /// Drops incidents the session manager has aged out. The list is the only other strong reference to
    /// their telemetry, so keeping them here would defeat the retention cap entirely.
    /// </summary>
    private void OnIncidentsEvicted(object? sender, IReadOnlyList<IncidentRecord> evicted)
    {
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var evictedIds = evicted.Select(item => item.Id).ToHashSet();

            for (var position = Incidents.Count - 1; position >= 0; position--)
            {
                if (evictedIds.Contains(Incidents[position].Id))
                {
                    Incidents.RemoveAt(position);
                }
            }

            if (SelectedIncident is { } selected && evictedIds.Contains(selected.Id))
            {
                SelectedIncident = Incidents.FirstOrDefault();
            }

            _pendingIncidentIds.RemoveWhere(evictedIds.Contains);
        }));
    }

    private void RefreshState()
    {
        IsSessionActive = _sessionManager.IsSessionActive;
        ActiveProcessText = _sessionManager.ActiveProcess is { } process
            ? $"{process.ProcessName} (PID {process.ProcessId})"
            : Strings.WaitingForProcess;

        if (!IsSessionActive)
        {
            LiveCpuText = Strings.LiveStatsIdle;
            LiveMemoryText = Strings.LiveStatsIdle;
            LiveVramText = Strings.LiveStatsIdle;

            // Pacing figures belong to the session that produced them. The session manager builds a fresh
            // FramePacingMonitor on every start, but the view model keeps whatever it was last told —
            // so without this the next session shows the previous evening's saturation share until its
            // first window closes a minute later, which is exactly when someone is looking.
            ResetPacing();
        }

        OnPropertyChanged(nameof(SessionStateText));
        StartSessionCommand.RaiseCanExecuteChanged();
        StopSessionCommand.RaiseCanExecuteChanged();
        MarkStutterCommand.RaiseCanExecuteChanged();
        MarkSevereStutterCommand.RaiseCanExecuteChanged();
        ExportSelectedIncidentCommand.RaiseCanExecuteChanged();
    }

    private void ResetPacing()
    {
        HasPacingData = false;
        PacingSaturatedText = Strings.PacingIdle;
        PacingWorstText = string.Empty;
        PacingCurrentText = string.Empty;
        PacingCaptureBudgetText = string.Empty;
    }

    private void RequestStateRefresh()
    {
        if (_dispatcher.CheckAccess())
        {
            _stateRefreshPending = true;
            return;
        }

        _dispatcher.BeginInvoke(RequestStateRefresh);
    }

    private void FlushUiUpdates()
    {
        FlushPendingStatusEntries();

        if (!_stateRefreshPending && (!_sessionManager.IsSessionActive || DateTimeOffset.UtcNow - _lastStateRefreshUtc < TimeSpan.FromSeconds(1)))
        {
            return;
        }

        _stateRefreshPending = false;
        _lastStateRefreshUtc = DateTimeOffset.UtcNow;
        RefreshState();
    }

    private void FlushPendingStatusEntries()
    {
        var processed = 0;
        while (processed < MaxStatusEntriesPerFlush && _pendingStatusEntries.TryDequeue(out var status))
        {
            StatusEntries.Insert(0, status);
            processed++;
        }

        while (StatusEntries.Count > MaxStatusEntries)
        {
            StatusEntries.RemoveAt(StatusEntries.Count - 1);
        }
    }

    private void RefreshPresentMonStatus()
    {
        var discovery = PresentMonLocator.Discover(Settings.PresentMon.ExecutablePath);
        PresentMonStatusText = discovery.Kind switch
        {
            PresentMonDiscoveryKind.Configured => Strings.PresentMonStatusConfigured,
            PresentMonDiscoveryKind.AutoDetected => Strings.PresentMonStatusAutoDetected,
            _ => Strings.PresentMonStatusMissing,
        };
    }
}
