using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Fakes;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly DiagnosticsSessionManager _sessionManager;
    private readonly SettingsStore _settingsStore;
    private readonly IUserDialogService _dialogService;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _stateRefreshTimer;

    private IncidentRecord? _selectedIncident;
    private bool _isSessionActive;
    private string _activeProcessText = "Waiting for FiveM/GTA process";
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

    public MainWindowViewModel(DiagnosticsSessionManager sessionManager, SettingsStore settingsStore, DiagnosticsSettings settings, IUserDialogService dialogService)
    {
        _sessionManager = sessionManager;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _stateRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _stateRefreshTimer.Tick += (_, _) => FlushStateRefresh();
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

        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, () => !IsSessionActive);
        StopSessionCommand = new AsyncRelayCommand(StopSessionAsync, () => IsSessionActive);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ImportArtifactsCommand = new AsyncRelayCommand(ImportArtifactsAsync);
        ExportSelectedIncidentCommand = new AsyncRelayCommand(ExportSelectedIncidentAsync, () => SelectedIncident is not null || _sessionManager.LatestIncident is not null);
        MarkStutterCommand = new RelayCommand(() => _sessionManager.MarkIncident(IncidentSeverity.Normal), () => IsSessionActive);
        MarkSevereStutterCommand = new RelayCommand(() => _sessionManager.MarkIncident(IncidentSeverity.Severe), () => IsSessionActive);
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

    public string SessionStateText => IsSessionActive ? "Session active. Collectors sample only while FiveM/GTA is running." : "Session idle.";

    public string ActiveProcessText
    {
        get => _activeProcessText;
        private set => SetProperty(ref _activeProcessText, value);
    }

    public string HotKeyLegend => "Ctrl+Alt+F9 mark stutter, Ctrl+Alt+F10 mark severe stutter, Ctrl+Alt+F11 export latest incident";

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

    public string SelectedIncidentSummary => SelectedIncident?.Analysis?.Summary ?? "Select an incident to inspect its summary and supporting evidence.";

    public IReadOnlyList<TimelineHighlight> SelectedTimeline => SelectedIncident?.Analysis?.TimelineHighlights ?? [];

    public IReadOnlyList<HypothesisScore> SelectedHypotheses => SelectedIncident?.Analysis?.Hypotheses ?? [];

    private async Task StartSessionAsync()
    {
        await _sessionManager.StartSessionAsync().ConfigureAwait(true);
        RefreshState();
    }

    private async Task StopSessionAsync()
    {
        await _sessionManager.StopSessionAsync().ConfigureAwait(true);
        RefreshState();
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(Settings).ConfigureAwait(true);
        _sessionManager.Report(StatusLevel.Info, nameof(MainWindowViewModel), $"Inställningar sparade till {_settingsStore.SettingsPath}.");
    }

    private async Task ImportArtifactsAsync()
    {
        var files = _dialogService.PickArtifactFiles();
        if (files.Length == 0)
        {
            return;
        }

        await _sessionManager.ImportArtifactsAsync(files).ConfigureAwait(true);
        RefreshState();
    }

    private async Task ExportSelectedIncidentAsync()
    {
        var output = await _sessionManager.ExportIncidentAsync(SelectedIncident ?? _sessionManager.LatestIncident, IncludeSensitiveFields, IncludeAttachedArtifacts).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(output))
        {
            _dialogService.ShowInfo("Incident exported", output);
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
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        RequestStateRefresh();
    }

    private void OnStatusReported(object? sender, DiagnosticStatusEntry status)
    {
        _dispatcher.Invoke(() =>
        {
            StatusEntries.Insert(0, status);
            while (StatusEntries.Count > 200)
            {
                StatusEntries.RemoveAt(StatusEntries.Count - 1);
            }
        });
    }

    private void OnIncidentCompleted(object? sender, IncidentRecord incident)
    {
        _dispatcher.Invoke(() =>
        {
            Incidents.Insert(0, incident);
            SelectedIncident ??= incident;
        });
    }

    private void RefreshState()
    {
        IsSessionActive = _sessionManager.IsSessionActive;
        ActiveProcessText = _sessionManager.ActiveProcess is { } process
            ? $"{process.ProcessName} (PID {process.ProcessId})"
            : "Waiting for FiveM/GTA process";

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
            if (!_stateRefreshTimer.IsEnabled)
            {
                _stateRefreshTimer.Start();
            }

            return;
        }

        _dispatcher.BeginInvoke(RequestStateRefresh);
    }

    private void FlushStateRefresh()
    {
        if (!_stateRefreshPending)
        {
            _stateRefreshTimer.Stop();
            return;
        }

        _stateRefreshPending = false;
        RefreshState();
    }
}