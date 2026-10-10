namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.Core;

/// <summary>One program in the pre-launch list: its tick box, whether it runs, and its remove button.</summary>
public sealed class PreLaunchAppViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isRunning;

    public PreLaunchAppViewModel(PreLaunchAppEntry entry, Action changed, Action<PreLaunchAppViewModel> remove)
    {
        Entry = entry;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public PreLaunchAppEntry Entry { get; }

    public string Name => Entry.Name;

    public RelayCommand RemoveCommand { get; }

    public bool IsChecked
    {
        get => Entry.Close;
        set
        {
            if (Entry.Close == value)
            {
                return;
            }

            Entry.Close = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }
}

/// <summary>A program the previous session measured as heavy, offered for the pre-launch list.</summary>
public sealed class PreLaunchSuggestionViewModel
{
    public PreLaunchSuggestionViewModel(HeavyProcess process, string reason, Action<PreLaunchSuggestionViewModel> add)
    {
        Process = process;
        Reason = reason;
        AddCommand = new RelayCommand(() => add(this));
    }

    public HeavyProcess Process { get; }

    public string Name => Process.ProcessName;

    public string Reason { get; }

    public RelayCommand AddCommand { get; }
}
