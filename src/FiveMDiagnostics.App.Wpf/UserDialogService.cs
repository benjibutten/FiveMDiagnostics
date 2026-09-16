namespace FiveMDiagnostics.App.Wpf;

public interface IUserDialogService
{
    string[] PickArtifactFiles();

    void ShowInfo(string title, string message);

    /// <summary>Asks before something irreversible. False is the answer a closed dialog gives.</summary>
    bool Confirm(string title, string message);
}

public sealed class UserDialogService : IUserDialogService
{
    public string[] PickArtifactFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Diagnostics artifacts|*.csv;*.json;*.log;*.txt;*.etl|All files|*.*",
            Title = "Import FiveM diagnostics artifacts",
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public void ShowInfo(string title, string message)
    {
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    /// <summary>
    /// Owned by the main window when there is one to own it. The app can sit in the tray with no visible
    /// window, and an owned dialog would then never be shown at all.
    /// </summary>
    public bool Confirm(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is { IsVisible: true }
            ? System.Windows.MessageBox.Show(owner, message, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            : System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        return result == System.Windows.MessageBoxResult.Yes;
    }
}