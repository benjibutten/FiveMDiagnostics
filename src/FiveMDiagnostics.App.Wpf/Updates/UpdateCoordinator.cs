using System.Windows;

namespace FiveMDiagnostics.App.Wpf.Updates;

using FiveMDiagnostics.App.Wpf.Properties;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

/// <summary>
/// The one place that decides what the user sees around an update. An automatic check
/// stays silent unless there is something to install — this app spends whole sessions
/// in the tray, and a dialog that only says "you are up to date" is pure interruption.
/// </summary>
internal static class UpdateCoordinator
{
    private static readonly GitHubUpdateService Service = new();
    private static int _checkInProgress;

    public static async Task CheckAsync(Window? owner, bool manual)
    {
        var currentVersion = AppVersion.Current;

        // A build from the source tree carries 1.0.0.0, so every release looks newer
        // than it. Installing a release over a working copy is not what anyone asked for.
        if (currentVersion is null || !AppVersion.IsReleaseBuild)
        {
            if (manual)
            {
                Show(owner, Strings.UpdateOnlyInReleaseBuilds, MessageBoxImage.Information);
            }

            return;
        }

        if (Interlocked.Exchange(ref _checkInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var update = await Service.CheckAsync(currentVersion, manual).ConfigureAwait(true);
            if (update is null)
            {
                if (manual)
                {
                    Show(owner, Strings.UpdateUpToDate, MessageBoxImage.Information);
                }

                return;
            }

            var question = string.Format(
                Strings.Culture,
                Strings.UpdateAvailableFormat,
                update.TagName,
                AppVersion.DisplayText);
            if (Ask(owner, question) != MessageBoxResult.Yes)
            {
                return;
            }

            await DownloadAndHandOverAsync(owner, update).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (manual)
            {
                Show(
                    owner,
                    string.Format(Strings.Culture, Strings.UpdateCheckFailedFormat, ex.Message),
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    private static async Task DownloadAndHandOverAsync(Window? owner, UpdateInfo update)
    {
        var visibleOwner = owner is { IsVisible: true } ? owner : null;
        var progressWindow = new UpdateProgressWindow(visibleOwner);
        if (visibleOwner is not null)
        {
            visibleOwner.IsEnabled = false;
        }

        progressWindow.Show();

        try
        {
            var progress = new Progress<UpdateProgress>(progressWindow.Report);
            await Service.LaunchInstallerAsync(update, progress).ConfigureAwait(true);

            // From here the copied updater is waiting for this process to exit before it
            // can touch a single file, so the shutdown has to be the ordinary one that
            // stops the collectors and drops the tray icon.
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ExitForUpdate();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
        finally
        {
            progressWindow.Close();
            if (visibleOwner is not null)
            {
                visibleOwner.IsEnabled = true;
            }
        }
    }

    private static void Show(Window? owner, string message, MessageBoxImage icon)
    {
        if (owner is { IsVisible: true })
        {
            MessageBox.Show(owner, message, Strings.UpdateTitle, MessageBoxButton.OK, icon);
        }
        else
        {
            MessageBox.Show(message, Strings.UpdateTitle, MessageBoxButton.OK, icon);
        }
    }

    private static MessageBoxResult Ask(Window? owner, string message) =>
        owner is { IsVisible: true }
            ? MessageBox.Show(owner, message, Strings.UpdateTitle, MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(message, Strings.UpdateTitle, MessageBoxButton.YesNo, MessageBoxImage.Information);
}
