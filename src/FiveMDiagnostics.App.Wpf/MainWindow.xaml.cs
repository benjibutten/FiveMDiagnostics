using System.ComponentModel;
using System.Windows;

namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.App.Wpf.Services;

public partial class MainWindow : Window
{
    private readonly TrayIconService _trayIconService = new();
    private readonly GlobalHotKeyManager _hotKeyManager = new();
    private bool _allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;

        _trayIconService.ShowRequested += (_, _) => RestoreFromTray();
        _trayIconService.ExitRequested += (_, _) => ExitApplication();
        _hotKeyManager.Triggered += OnHotKeyTriggered;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            _hotKeyManager.Attach(this, viewModel.Settings.HotKeys);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _trayIconService.ShowBalloon("FiveM Diagnostics", "Tray mode and global hotkeys are active.");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray("Window minimized to tray.");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray("App hidden to tray. Diagnostics can keep running in the background.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hotKeyManager.Dispose();
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
        _trayIconService.ShowBalloon("FiveM Diagnostics", message);
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void OnHotKeyTriggered(object? sender, HotKeyAction action)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        switch (action)
        {
            case HotKeyAction.MarkStutter when viewModel.MarkStutterCommand.CanExecute(null):
                viewModel.MarkStutterCommand.Execute(null);
                break;
            case HotKeyAction.MarkSevereStutter when viewModel.MarkSevereStutterCommand.CanExecute(null):
                viewModel.MarkSevereStutterCommand.Execute(null);
                break;
            case HotKeyAction.ExportCurrentIncident when viewModel.ExportSelectedIncidentCommand.CanExecute(null):
                viewModel.ExportSelectedIncidentCommand.Execute(null);
                break;
        }
    }
}