using System.Windows;

namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.App.Wpf.Services;

/// <summary>
/// The first-run guide: asks whether the player streams, and shows what the app can measure on this
/// machine before the first session.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public SetupWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Most players do not stream, and having OBS installed says nothing about whether this one does.
        StreamsNo.IsChecked = true;

        AdminText.Text = viewModel.IsElevated ? Strings.ReadinessAdminOk : Strings.ReadinessAdminMissing;
        RestartButton.Visibility = viewModel.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.CompleteSetupAsync(StreamsYes.IsChecked == true);
        Close();
    }

    /// <summary>Saves the answer first, so the elevated instance does not ask again.</summary>
    private async void OnRestartClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.CompleteSetupAsync(StreamsYes.IsChecked == true);
        Close();
        _viewModel.RestartAsAdministratorCommand.Execute(null);
    }
}
