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
    private bool _stateRefreshPending;
    private string _selectedLanguage = string.Empty;
    private string _presentMonStatusText = string.Empty;
    private string _captureFeedbackText = string.Empty;
    private DateTimeOffset _lastStateRefreshUtc = DateTimeOffset.MinValue;

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

    public string SessionStateText => IsSessionActive ? Strings.SessionActive : Strings.SessionIdle;

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

    private void OnStatusReported(object? sender, DiagnosticStatusEntry status)
    {
        _pendingStatusEntries.Enqueue(status);
    }

    private void OnIncidentCompleted(object? sender, IncidentRecord incident)
    {
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Incidents.Insert(0, incident);
            SelectedIncident ??= incident;
            _pendingIncidentIds.Remove(incident.Id);
            CaptureFeedbackText = _pendingIncidentIds.Count > 0
                ? string.Format(Strings.CaptureFeedbackReadyWithPendingFormat, incident.Marker.Label, _pendingIncidentIds.Count)
                : string.Format(Strings.CaptureFeedbackReadyFormat, incident.Marker.Label);
            ExportSelectedIncidentCommand.RaiseCanExecuteChanged();
        }));
    }

    private void RefreshState()
    {
        IsSessionActive = _sessionManager.IsSessionActive;
        ActiveProcessText = _sessionManager.ActiveProcess is { } process
            ? $"{process.ProcessName} (PID {process.ProcessId})"
            : Strings.WaitingForProcess;

        OnPropertyChanged(nameof(SessionStateText));
        StartSessionCommand.RaiseCanExecuteChanged();
        StopSessionCommand.RaiseCanExecuteChanged();
        MarkStutterCommand.RaiseCanExecuteChanged();
        MarkSevereStutterCommand.RaiseCanExecuteChanged();
        ExportSelectedIncidentCommand.RaiseCanExecuteChanged();
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