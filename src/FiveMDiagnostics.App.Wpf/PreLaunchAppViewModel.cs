namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.App.Wpf.Services;

/// <summary>One tick box in the pre-launch list.</summary>
public sealed class PreLaunchAppViewModel(PreLaunchApp app, bool isChecked, Action changed) : ObservableObject
{
    private bool _isChecked = isChecked;
    private bool _isRunning;

    public PreLaunchApp App { get; } = app;

    public string Name => App.Name;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                changed();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }
}
