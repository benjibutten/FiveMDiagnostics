using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.App.Wpf.Services;

public partial class MainWindow : Window
{
    private readonly TrayIconService _trayIconService = new();
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

        _trayIconService.ShowRequested += (_, _) => RestoreFromTray();
        _trayIconService.StartSessionRequested += (_, _) => ExecuteTrayCommand(_viewModel.StartSessionCommand, Strings.TraySessionStartingMessage);
        _trayIconService.StopSessionRequested += (_, _) => ExecuteTrayCommand(_viewModel.StopSessionCommand, Strings.TraySessionStoppedMessage);
        _trayIconService.MarkStutterRequested += (_, _) => ExecuteTrayCommand(_viewModel.MarkStutterCommand, Strings.TrayMarkStutterMessage);
        _trayIconService.MarkSevereRequested += (_, _) => ExecuteTrayCommand(_viewModel.MarkSevereStutterCommand, Strings.TrayMarkSevereMessage);
        _trayIconService.ExportLatestRequested += (_, _) => ExecuteTrayCommand(_viewModel.ExportSelectedIncidentCommand, Strings.TrayExportStartingMessage);
        _trayIconService.ExitRequested += (_, _) => ExitApplication();

        _viewModel.StartSessionCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.StopSessionCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.MarkStutterCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.MarkSevereStutterCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
        _viewModel.ExportSelectedIncidentCommand.CanExecuteChanged += OnCommandAvailabilityChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTrayMenuState();
        _trayIconService.ShowBalloon(Strings.AppTitle, Strings.TrayReadyMessage);
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
        _viewModel.StartSessionCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.StopSessionCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.MarkStutterCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.MarkSevereStutterCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _viewModel.ExportSelectedIncidentCommand.CanExecuteChanged -= OnCommandAvailabilityChanged;
        _trayIconService.Dispose();
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
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