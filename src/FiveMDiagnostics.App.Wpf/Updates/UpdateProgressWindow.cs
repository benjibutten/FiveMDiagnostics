using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FiveMDiagnostics.App.Wpf.Updates;

using FiveMDiagnostics.App.Wpf.Properties;

// WinForms is referenced for the tray icon, so these names have to be pinned to WPF.
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using ProgressBar = System.Windows.Controls.ProgressBar;

/// <summary>
/// The only thing shown while an update is downloading or being installed. It is built
/// in code rather than XAML because the installer runs it from a copy of the exe in a
/// temp folder, before the normal application resources are anywhere in play.
/// </summary>
internal sealed class UpdateProgressWindow : Window, IProgress<UpdateProgress>
{
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;

    public UpdateProgressWindow(Window? owner)
    {
        Title = Strings.UpdateTitle;
        if (owner is not null)
        {
            Owner = owner;
        }

        Width = 440;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Bahnschrift SemiCondensed");
        Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x12, 0x18));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF8, 0xFB));

        _statusText = new TextBlock
        {
            Text = Strings.UpdateStatusPreparing,
            Margin = new Thickness(20, 18, 20, 12),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _progressBar = new ProgressBar
        {
            Height = 10,
            Margin = new Thickness(20, 0, 20, 20),
            IsIndeterminate = true,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x27, 0x36)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xD6, 0xC2)),
        };
        Content = new StackPanel { Children = { _statusText, _progressBar } };
    }

    public void Report(UpdateProgress value)
    {
        _statusText.Text = value.Status;
        _progressBar.IsIndeterminate = value.Percentage is null;
        if (value.Percentage is double percentage)
        {
            _progressBar.Value = percentage;
        }
    }
}
