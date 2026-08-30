namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.Core;

/// <summary>
/// One process's line in the live VRAM table.
/// </summary>
/// <remarks>
/// Mutable and updated in place rather than replaced, because the table refreshes every few seconds and
/// a reader would otherwise lose their place in it each time. The process set barely changes during a
/// session, so in the ordinary case nothing is added or removed at all.
/// </remarks>
public sealed class LiveVramRowViewModel(LiveVramRow row) : ObservableObject
{
    private LiveVramRow _row = row;

    public string ProcessName => _row.ProcessName;

    public string NowText => Megabytes(_row.DedicatedMegabytes);

    /// <summary>
    /// What the process has added since the session first saw it, blank when it has added nothing.
    /// </summary>
    /// <remarks>
    /// Blank rather than "0 MB" on purpose: most rows never move, and a column of zeroes would bury the
    /// one row that does. The 29 August session is the case in point — twenty-odd flat rows and Voicemod
    /// quietly taking 734 MB in twenty seconds.
    /// </remarks>
    public string TakenText => _row.GrowthMegabytes >= 1 ? "+" + Megabytes(_row.GrowthMegabytes) : string.Empty;

    public string PeakText => Megabytes(_row.PeakBytes / 1024d / 1024);

    /// <summary>Set for a row this session has proved impossible or double counted; it stays listed and labelled.</summary>
    public string NoteText => _row.IsTrusted ? string.Empty : Strings.LiveVramUntrustedNote;

    public bool IsTrusted => _row.IsTrusted;

    public void Update(LiveVramRow row)
    {
        var previous = _row;
        _row = row;

        if (previous.ProcessName != row.ProcessName)
        {
            OnPropertyChanged(nameof(ProcessName));
        }

        if (previous.DedicatedBytes != row.DedicatedBytes)
        {
            OnPropertyChanged(nameof(NowText));
            OnPropertyChanged(nameof(TakenText));
        }

        if (previous.PeakBytes != row.PeakBytes)
        {
            OnPropertyChanged(nameof(PeakText));
        }

        if (previous.IsTrusted != row.IsTrusted)
        {
            OnPropertyChanged(nameof(NoteText));
            OnPropertyChanged(nameof(IsTrusted));
        }
    }

    /// <summary>
    /// Megabytes throughout, including for the rows that would read more naturally in gigabytes.
    /// </summary>
    /// <remarks>
    /// The column is read down, comparing rows against each other, and mixing units in it makes 669 MB
    /// look larger than 5.4 GB at a glance. What the reader is doing here is deciding what to close.
    /// </remarks>
    private static string Megabytes(double megabytes) => $"{megabytes:N0} MB";
}
