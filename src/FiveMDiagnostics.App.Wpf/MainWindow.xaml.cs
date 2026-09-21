using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.App.Wpf.Services;
using FiveMDiagnostics.App.Wpf.Updates;

public partial class MainWindow : Window
{
    private readonly TrayIconService _trayIconService = new();
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;

        // After the handle exists, which is what SourceInitialized means. RegisterHotKey against a
        // window that has none binds to the thread instead, and nothing in this process pumps that.
        SourceInitialized += OnSourceInitialized;
        _hotkeys.Pressed += OnHotkeyPressed;

        _trayIconService.ShowRequested += (_, _) => RestoreFromTray();
        _trayIconService.StartSessionRequested += (_, _) => ExecuteTrayCommand(_viewModel.StartSessionCommand, Strings.TraySessionStartingMessage);
        _trayIconService.StopSessionRequested += (_, _) => ExecuteTrayCommand(_viewModel.StopSessionCommand, Strings.TraySessionStoppedMessage);
        _trayIconService.MarkStutterRequested += (_, _) => ExecuteTrayCommand(_viewModel.MarkStutterCommand, Strings.TrayMarkStutterMessage);
        _trayIconService.MarkSevereRequested += (_, _) => ExecuteTrayCommand(_viewModel.MarkSevereStutterCommand, Strings.TrayMarkSevereMessage);
        _trayIconService.ExportLatestRequested += (_, _) => ExecuteTrayCommand(_viewModel.ExportSelectedIncidentCommand, Strings.TrayExportStartingMessage);
        _trayIconService.CheckForUpdatesRequested += async (_, _) => await UpdateCoordinator.CheckAsync(this, manual: true);
        _trayIconService.ExitRequested += (_, _) => ExitApplication();

        _viewModel.TrayNoticeRequested += OnTrayNoticeRequested;

        _viewModel.StartSessionCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.StopSessionCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.MarkStutterCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.MarkSevereStutterCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.ExportSelectedIncidentCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTrayMenuState();

        // The hotkey problem instead of the ready message, not after it. Both go to the same
        // NotifyIcon, which shows one balloon at a time, and SourceInitialized runs before Loaded — so
        // a warning shown where it was first written was replaced by "ready" a moment later and never
        // seen. That is the silent failure the warning exists to prevent, arriving by a different
        // route: a hotkey nobody knows is dead gives an evening with no marks, and no marks reads as
        // "nothing was bad enough to mark". Nothing is lost by dropping the ready message in that
        // case; it says less than the warning does.
        _trayIconService.ShowBalloon(
            Strings.AppTitle,
            _hotkeys.RegistrationProblem ?? Strings.TrayReadyMessage);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hotkeys.Register(new WindowInteropHelper(this).Handle);
    }

    private void OnHotkeyPressed(object? sender, HotkeyMark mark)
    {
        var severity = mark == HotkeyMark.SevereStutter ? IncidentSeverity.Severe : IncidentSeverity.Normal;
        var command = mark == HotkeyMark.SevereStutter ? _viewModel.MarkSevereStutterCommand : _viewModel.MarkStutterCommand;

        if (!command.CanExecute(null))
        {
            // Pressed with no session running. Worth a word: the key is on a stream deck, out of sight
            // of the app, and the only feedback that the press did nothing is this.
            _trayIconService.ShowBalloon(Strings.AppTitle, Strings.HotkeyNoSessionMessage);
            return;
        }

        _viewModel.MarkStutterFromHotkey(severity);
        _trayIconService.ShowBalloon(
            Strings.AppTitle,
            mark == HotkeyMark.SevereStutter ? Strings.TrayMarkSevereMessage : Strings.TrayMarkStutterMessage);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray(Strings.WindowMinimizedToTrayMessage);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray(Strings.AppHiddenToTrayMessage);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.TrayNoticeRequested -= OnTrayNoticeRequested;
        _viewModel.StartSessionCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.StopSessionCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.MarkStutterCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.MarkSevereStutterCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.ExportSelectedIncidentCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _hotkeys.Pressed -= OnHotkeyPressed;
        _hotkeys.Dispose();
        _trayIconService.Dispose();
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ActivateFromExternalRequest()
    {
        if (!IsVisible || !ShowInTaskbar || WindowState == WindowState.Minimized)
        {
            RestoreFromTray();
        }
        else
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray(string message)
    {
        ShowInTaskbar = false;
        Hide();
        _trayIconService.ShowBalloon(Strings.AppTitle, message);
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// Closes for real rather than hiding to the tray. The updater is already waiting
    /// for this process to exit before it can replace a single file, and a window that
    /// only hid itself would leave it waiting forever.
    /// </summary>
    public void ExitForUpdate() => ExitApplication();

    private void OnTrayNoticeRequested(object? sender, string message)
    {
        _trayIconService.ShowBalloon(Strings.AppTitle, message);
    }

    private void OnCommandAvailabilityChanged(object? sender, EventArgs e)
    {
        UpdateTrayMenuState();
    }

    private void UpdateTrayMenuState()
    {
        _trayIconService.UpdateDiagnosticsActions(
            _viewModel.StartSessionCommand.CanExecute(null),
            _viewModel.StopSessionCommand.CanExecute(null),
            _viewModel.MarkStutterCommand.CanExecute(null),
            _viewModel.MarkSevereStutterCommand.CanExecute(null),
            _viewModel.ExportSelectedIncidentCommand.CanExecute(null));
    }

    private void ExecuteTrayCommand(ICommand command, string balloonMessage)
    {
        if (!command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
        UpdateTrayMenuState();
        _trayIconService.ShowBalloon(Strings.AppTitle, balloonMessage);
    }
}