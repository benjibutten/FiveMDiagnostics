namespace FiveMDiagnostics.App.Wpf;

using FiveMDiagnostics.Export;

/// <summary>A recorded session as the share list shows it: local start date and time, and its end.</summary>
public sealed record SessionChoice(RecordedSession Session)
{
    public string Label
    {
        get
        {
            var start = Session.StartedAt.ToLocalTime();
            var end = Session.LastWrittenAt.ToLocalTime();
            var endText = end.Date == start.Date ? end.ToString("HH:mm") : end.ToString("d MMM HH:mm");
            return $"{start:ddd d MMM HH:mm} – {endText}";
        }
    }
}
