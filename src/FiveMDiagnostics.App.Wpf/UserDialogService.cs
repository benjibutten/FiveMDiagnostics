namespace FiveMDiagnostics.App.Wpf;

public interface IUserDialogService
{
    string[] PickArtifactFiles();

    void ShowInfo(string title, string message);
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
}