namespace FiveMDiagnostics.Collectors;

using System.Diagnostics.Eventing.Reader;
using System.Text;

/// <summary>
/// One entry in the Windows System log, reduced to what a review needs.
/// </summary>
public sealed record WindowsEventEntry(DateTimeOffset At, string Provider, int EventId, string Level, string Message)
{
    /// <summary>The entry on one line, with the message cut to something a log line can hold.</summary>
    public string Describe()
    {
        var text = Message.ReplaceLineEndings(" ").Trim();
        if (text.Length > 180)
        {
            text = text[..180].TrimEnd() + "…";
        }

        return $"{At.ToLocalTime():HH:mm:ss} {Provider} ({Level}, id {EventId}): {text}";
    }
}

/// <summary>
/// Reads the Windows System log around a moment the session already found interesting.
/// </summary>
/// <remarks>
/// <para>
/// Six review notes in a row carried the same action: go and look in Loggboken around the second a
/// volume answered in two seconds, and decide whether it was a disk spinning up or a drive losing
/// contact. It was never done — it is tedious, it has to happen while the timestamp still means
/// something, and the app that knows the exact second was sitting on the same machine the whole time.
/// So it does the lookup itself now.
/// </para>
/// <para>
/// The System log is readable without elevation for ordinary users; the Security log is not, and is not
/// read. A failure to read is reported rather than swallowed, because a silent empty answer here would
/// read as "nothing in the log", which is the opposite conclusion.
/// </para>
/// </remarks>
public static class WindowsEventLogReader
{
    /// <summary>
    /// The providers that say something about a drive. Storage stack, file system, and the PnP source
    /// that writes when a device drops off the bus and comes back.
    /// </summary>
    private static readonly string[] StorageProviders =
    [
        "disk",
        "Disk",
        "Ntfs",
        "volmgr",
        "partmgr",
        "storahci",
        "stornvme",
        "USBSTOR",
        "UASPStor",
        "Microsoft-Windows-Kernel-PnP",
        "Microsoft-Windows-Ntfs",
        "Microsoft-Windows-DiskDiagnosticDataCollector",
    ];

    /// <summary>How far either side of the moment to look.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(2);

    /// <summary>The most entries worth writing into one session line.</summary>
    private const int MaxEntries = 5;

    /// <summary>
    /// Whether the window around <paramref name="moment"/> has finished happening, and the log around it
    /// can therefore be read once and for all.
    /// </summary>
    /// <remarks>
    /// A lookup made while the second half of the window is still in the future reads a log that has not
    /// been written yet. That matters most in the case the line exists for: a volume stalls as the game
    /// is closing, the session writes its summaries a second later, and the <c>volmgr</c> entry that
    /// would have settled it arrives half a minute after the lookup.
    /// </remarks>
    public static bool WindowHasElapsed(DateTimeOffset moment, TimeSpan? window = null) =>
        DateTimeOffset.UtcNow >= moment + (window ?? DefaultWindow);

    /// <summary>
    /// The session-log line for what the System log holds around <paramref name="moment"/>. Always a
    /// line: a log that cannot be read says so, because silence here would read as "nothing happened".
    /// </summary>
    /// <param name="what">
    /// What the lookup is about, named in the line so it can be matched to the reading that prompted it
    /// — "F: svarade på 2 406 ms", not "a lookup happened".
    /// </param>
    public static string Describe(string what, DateTimeOffset moment, TimeSpan? window = null)
    {
        var span = window ?? DefaultWindow;
        var from = moment - span;
        var to = moment + span;

        // The future half of the window has not been written yet, so the reading stops at now and says
        // so rather than reporting an empty stretch of log as an absence of events.
        var now = DateTimeOffset.UtcNow;
        var cutShort = to > now;
        if (cutShort)
        {
            to = now;
        }

        List<WindowsEventEntry> entries;
        try
        {
            entries = Read(from, to);
        }
        catch (EventLogException ex)
        {
            return $"Windows händelselogg kunde inte läsas kring {what}: {ex.Message}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Windows händelselogg kunde inte läsas kring {what}: åtkomst nekad. "
                + "Systemloggen kräver normalt ingen behörighet — kör appen som vanlig användare i "
                + "gruppen Event Log Readers.";
        }

        var period = $"{from.ToLocalTime():HH:mm:ss}–{to.ToLocalTime():HH:mm:ss}";
        if (entries.Count == 0)
        {
            return $"Windows händelselogg kring {what}: inga poster från disk-, filsystem- eller "
                + $"PnP-källor mellan {period}. "
                + (cutShort
                    ? "Fönstret slutar där sessionen slutade, så minuterna efter hann aldrig skrivas — "
                        + "att det är tomt säger därför ingenting om vad som hände sedan."
                    : "Ingenting i loggen pekar på att enheten tappade kontakten — uppvarvning är då "
                        + "den förklaring som står kvar.");
        }

        var builder = new StringBuilder();
        builder.Append($"Windows händelselogg kring {what}: {entries.Count} post(er) från disk-, "
            + $"filsystem- eller PnP-källor mellan {period}. ");
        builder.Append(string.Join(" | ", entries.Take(MaxEntries).Select(entry => entry.Describe())));
        if (entries.Count > MaxEntries)
        {
            builder.Append($" (+{entries.Count - MaxEntries} till)");
        }

        builder.Append(entries.Any(entry => entry.Level is "Error" or "Critical")
            ? " Minst en är ett fel, alltså inte bara uppvarvning — läs hela posten i Loggboken."
            : " Ingen av dem är ett fel.");

        return builder.ToString();
    }

    /// <summary>
    /// The matching System-log entries in the window, oldest first.
    /// </summary>
    /// <remarks>
    /// Filtered in the query rather than in the loop. The System log on this machine holds months of
    /// entries, and walking it in managed code to keep four of them would cost seconds inside a session
    /// that is measuring milliseconds.
    /// </remarks>
    private static List<WindowsEventEntry> Read(DateTimeOffset from, DateTimeOffset to)
    {
        var providers = string.Join(" or ", StorageProviders.Select(name => $"@Name='{name}'"));
        var query = "*[System["
            + $"Provider[{providers}]"
            + $" and TimeCreated[@SystemTime>='{from.UtcDateTime:O}' and @SystemTime<='{to.UtcDateTime:O}']"
            + "]]";

        var entries = new List<WindowsEventEntry>();
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));

        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                entries.Add(new WindowsEventEntry(
                    record.TimeCreated is { } at ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero) : from,
                    record.ProviderName ?? "okänd källa",
                    record.Id,
                    DescribeLevel(record),
                    SafeDescription(record)));
            }
        }

        entries.Sort((left, right) => left.At.CompareTo(right.At));
        return entries;
    }

    /// <summary>
    /// The level as a word, since the numeric one means nothing in a log line.
    /// </summary>
    private static string DescribeLevel(EventRecord record)
    {
        return record.Level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            5 => "Verbose",
            _ => "okänd nivå",
        };
    }

    /// <summary>
    /// The rendered message, or the reason there is none.
    /// </summary>
    /// <remarks>
    /// <see cref="EventRecord.FormatDescription()"/> throws when the provider's message resource is not
    /// installed, which happens for drivers that have been uninstalled since they wrote the entry —
    /// precisely the case a disk investigation cares about. The id and provider still identify it.
    /// </remarks>
    private static string SafeDescription(EventRecord record)
    {
        try
        {
            return record.FormatDescription() ?? "(ingen meddelandetext)";
        }
        catch (EventLogException)
        {
            return "(meddelandetexten kunde inte hämtas)";
        }
    }
}
