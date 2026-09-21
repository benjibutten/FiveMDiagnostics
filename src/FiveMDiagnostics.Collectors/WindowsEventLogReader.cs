namespace FiveMDiagnostics.Collectors;

using System.Diagnostics.Eventing.Reader;
using System.Text;

/// <summary>
/// One entry in a Windows event log, reduced to what a review needs.
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

    /// <summary>Whether the entry names <paramref name="processName"/>, with or without its extension.</summary>
    public bool Mentions(string processName) =>
        Message.Contains(processName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the Windows event logs around a moment the session already found interesting.
/// </summary>
/// <remarks>
/// <para>
/// Six review notes in a row carried the same action: go and look in Loggboken around the second a
/// volume answered in two seconds, and decide whether it was a disk spinning up or a drive losing
/// contact. It was never done — it is tedious, it has to happen while the timestamp still means
/// something, and the app that knows the exact second was sitting on the same machine the whole time.
/// So it does the lookup itself now. The evening of 20 September added the second case: the game
/// process vanished without writing a crash dump, and the one place that could have said why was the
/// Application log, which nothing asked.
/// </para>
/// <para>
/// The System and Application logs are readable without elevation for ordinary users; the Security log
/// is not, and is not read. A failure to read is reported rather than swallowed, because a silent empty
/// answer here would read as "nothing in the log", which is the opposite conclusion.
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

    /// <summary>
    /// The providers that say a process died badly. Windows names the faulting module in these; a
    /// process ended by TerminateProcess leaves none of them, which is itself an answer.
    /// </summary>
    private static readonly string[] CrashProviders =
    [
        "Application Error",
        "Application Hang",
        "Windows Error Reporting",
        ".NET Runtime",
    ];

    /// <summary>The crash providers as one phrase, so the "nothing found" line can name what was asked.</summary>
    private const string CrashProviderNames =
        "Application Error, Application Hang, Windows Error Reporting eller .NET Runtime";

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
    /// been written yet. That matters most in the cases these lines exist for: a volume stalls as the
    /// game is closing and the session writes its summaries a second later, or the game process
    /// disappears and Windows files its error report a few seconds afterwards.
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
        var read = ReadWindow("System", StorageProviders, moment, window ?? DefaultWindow);
        if (read.Failure is { } failure)
        {
            return $"Windows händelselogg kunde inte läsas kring {what}: {failure}";
        }

        if (read.Entries.Count == 0)
        {
            return $"Windows händelselogg kring {what}: inga poster från disk-, filsystem- eller "
                + $"PnP-källor mellan {read.Period}. "
                + (read.CutShort
                    ? "Fönstret slutar där sessionen slutade, så minuterna efter hann aldrig skrivas — "
                        + "att det är tomt säger därför ingenting om vad som hände sedan."
                    : "Ingenting i loggen pekar på att enheten tappade kontakten — uppvarvning är då "
                        + "den förklaring som står kvar.");
        }

        var builder = new StringBuilder();
        builder.Append($"Windows händelselogg kring {what}: {read.Entries.Count} post(er) från disk-, "
            + $"filsystem- eller PnP-källor mellan {read.Period}. ");
        builder.Append(List(read.Entries));

        builder.Append(Grade(read.Entries, quietWhenOrdinary: false));

        return builder.ToString();
    }

    /// <summary>
    /// The session-log line for what the logs say about a game process that disappeared: whether Windows
    /// caught a crash, and whether the drive the game reads from misbehaved in the same minutes.
    /// </summary>
    /// <remarks>
    /// Both questions are asked because on 20 September both were open and neither was answered. The
    /// crash left no dump, so the Application log was the only remaining witness to whether Windows saw
    /// a fault at all; and the volume the game is installed on had been reset by its USB controller
    /// nineteen minutes earlier, which nothing connected the two of.
    /// </remarks>
    public static string DescribeProcessExit(
        string processName,
        int processId,
        DateTimeOffset moment,
        TimeSpan? window = null)
    {
        var span = window ?? DefaultWindow;
        var builder = new StringBuilder();
        builder.Append($"Windows händelselogg kring att {processName} (PID {processId}) försvann "
            + $"kl. {moment.ToLocalTime():HH:mm:ss}. ");
        builder.Append(DescribeCrashLog(processName, moment, span));
        builder.Append(' ');
        builder.Append(DescribeStorageAround(moment, span));
        return builder.ToString();
    }

    /// <summary>What the Application log holds about the process, or what it means that it holds nothing.</summary>
    private static string DescribeCrashLog(string processName, DateTimeOffset moment, TimeSpan span)
    {
        var read = ReadWindow("Application", CrashProviders, moment, span);
        if (read.Failure is { } failure)
        {
            return $"Applikationsloggen kunde inte läsas: {failure}";
        }

        var mine = read.Entries.Where(entry => entry.Mentions(processName)).ToList();
        if (mine.Count > 0)
        {
            return $"Applikationsloggen: {mine.Count} post(er) som nämner processen mellan "
                + $"{read.Period}. {List(mine)}";
        }

        // The absence is worth as much as a hit, but only if it is said precisely: Windows files a
        // report for a fault it catches, and for nothing else. A process ended by TerminateProcess, or
        // one whose error reporting is switched off, leaves exactly this.
        var nothing = read.Entries.Count == 0
            ? $"Applikationsloggen: ingen post från {CrashProviderNames} mellan {read.Period}."
            : $"Applikationsloggen: {read.Entries.Count} post(er) från {CrashProviderNames} mellan "
                + $"{read.Period}, men ingen av dem nämner processen.";

        return nothing + (read.CutShort
            ? " Fönstret slutar i nuet, så sekunderna efter hann aldrig skrivas — frågan ställs om på "
                + "nästa pass."
            : " Windows fångade alltså inget programfel i den här processen. Det utesluter inte en "
                + "krasch — en process som avslutas via TerminateProcess, eller vars felrapportering är "
                + "avstängd, lämnar ingen post — men det utesluter den sortens krasch Windows "
                + "rapporterar.");
    }

    /// <summary>What the System log holds about the drives in the same minutes.</summary>
    private static string DescribeStorageAround(DateTimeOffset moment, TimeSpan span)
    {
        var read = ReadWindow("System", StorageProviders, moment, span);
        if (read.Failure is { } failure)
        {
            return $"Systemloggen kunde inte läsas: {failure}";
        }

        if (read.Entries.Count == 0)
        {
            return "Systemloggen: inga poster från disk-, filsystem- eller PnP-källor i samma fönster.";
        }

        return $"Systemloggen: {read.Entries.Count} post(er) från disk-, filsystem- eller PnP-källor i "
            + $"samma fönster. {List(read.Entries)}"
            + Grade(read.Entries, quietWhenOrdinary: true);
    }

    /// <summary>The entries on one line, capped so a session line stays readable.</summary>
    private static string List(IReadOnlyList<WindowsEventEntry> entries)
    {
        var text = string.Join(" | ", entries.Take(MaxEntries).Select(entry => entry.Describe()));
        return entries.Count > MaxEntries ? $"{text} (+{entries.Count - MaxEntries} till)" : text;
    }

    /// <summary>
    /// The verdict on a set of storage entries: a real error first, a device reset second, and ordinary
    /// chatter last.
    /// </summary>
    /// <remarks>
    /// Shared by both callers because the order matters and getting it wrong is silent. The exit line
    /// used to append the reset sentence unconditionally, so a window holding a genuine <c>disk</c>
    /// error alongside a reset announced "none of them is graded as an error" — and that line had no
    /// way of saying "at least one is an error" at all, so a real disk fault around a crash was listed
    /// and never flagged.
    /// </remarks>
    /// <param name="quietWhenOrdinary">
    /// True where the entries are context rather than the subject. The disk line exists to decide
    /// whether a stall was spin-up, so "none of them is an error" is its answer; the exit line has
    /// already given its answer above and adds nothing by grading chatter.
    /// </param>
    internal static string Grade(IReadOnlyList<WindowsEventEntry> entries, bool quietWhenOrdinary)
    {
        if (entries.Any(entry => entry.Level is "Error" or "Critical"))
        {
            return " Minst en är ett fel, alltså inte bara uppvarvning — läs hela posten i Loggboken.";
        }

        if (DescribeResets(entries) is { } reset)
        {
            return reset;
        }

        return quietWhenOrdinary ? string.Empty : " Ingen av dem är ett fel.";
    }

    /// <summary>
    /// The sentence for entries that say a device dropped off its bus and was brought back, or null
    /// when none of them do.
    /// </summary>
    /// <remarks>
    /// These arrive graded as warnings, and the line used to end "none of them is an error" — which on
    /// 20 September dismissed a <c>UASPStor</c> bus reset plus fifty-two <c>disk</c> I/O retries as the
    /// spin-up three earlier notes had already called them. A reset is not spin-up whatever it is
    /// graded as: the drive stopped answering, the port was reset, and the I/O was reissued. The whole
    /// point of reading the log was to tell those two apart, so the summary sentence has to as well.
    /// </remarks>
    internal static string? DescribeResets(IReadOnlyList<WindowsEventEntry> entries)
    {
        var resets = entries.Count(IsBusReset);
        var retries = entries.Count(IsIoRetry);
        if (resets == 0 && retries == 0)
        {
            return null;
        }

        var what = (resets, retries) switch
        {
            (> 0, > 0) => $"{resets} säger att enheten återställdes och {retries} att en I/O-åtgärd "
                + "fick göras om",
            (> 0, _) => $"{resets} säger att enheten återställdes",
            _ => $"{retries} säger att en I/O-åtgärd fick göras om",
        };

        return $" Ingen av dem är graderad som fel, men {what}. Enheten tappade alltså kontakten och "
            + "kom tillbaka, och det är inte uppvarvning — det är kabel, port, hubb eller styrkrets.";
    }

    /// <summary>
    /// A storage port reset (id 129, written by the port driver) or a surprise removal (id 157): the
    /// controller gave up on the device and restarted the link, or the device simply went away.
    /// </summary>
    private static bool IsBusReset(WindowsEventEntry entry) => entry switch
    {
        { EventId: 129, Provider: "UASPStor" or "USBSTOR" or "storahci" or "stornvme" or "iaStorA" } => true,
        { EventId: 157, Provider: "disk" or "Disk" } => true,
        _ => false,
    };

    /// <summary>The storage stack reissuing an operation the device did not complete (id 153).</summary>
    private static bool IsIoRetry(WindowsEventEntry entry) =>
        entry is { EventId: 153, Provider: "disk" or "Disk" };

    /// <summary>A window of a log, or the reason it could not be read.</summary>
    /// <param name="Period">The window as local clock times, for the line that reports it.</param>
    /// <param name="CutShort">Whether the window ran into the future and was stopped at now.</param>
    private readonly record struct LogWindow(
        List<WindowsEventEntry> Entries,
        string Period,
        bool CutShort,
        string? Failure);

    /// <summary>
    /// Reads one log's matching entries around a moment, stopping the window at the present.
    /// </summary>
    private static LogWindow ReadWindow(
        string logName,
        string[] providers,
        DateTimeOffset moment,
        TimeSpan span)
    {
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

        var period = $"{from.ToLocalTime():HH:mm:ss}–{to.ToLocalTime():HH:mm:ss}";

        try
        {
            return new LogWindow(Read(logName, providers, from, to), period, cutShort, null);
        }
        catch (EventLogException ex)
        {
            return new LogWindow([], period, cutShort, ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return new LogWindow([], period, cutShort,
                "åtkomst nekad. Loggen kräver normalt ingen behörighet — kör appen som vanlig användare "
                + "i gruppen Event Log Readers.");
        }
    }

    /// <summary>
    /// The matching entries in the window, oldest first.
    /// </summary>
    /// <remarks>
    /// Filtered in the query rather than in the loop. The logs on this machine hold months of entries,
    /// and walking them in managed code to keep four of them would cost seconds inside a session that is
    /// measuring milliseconds.
    /// </remarks>
    private static List<WindowsEventEntry> Read(
        string logName,
        string[] providers,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var names = string.Join(" or ", providers.Select(name => $"@Name='{name}'"));
        var query = "*[System["
            + $"Provider[{names}]"
            + $" and TimeCreated[@SystemTime>='{from.UtcDateTime:O}' and @SystemTime<='{to.UtcDateTime:O}']"
            + "]]";

        var entries = new List<WindowsEventEntry>();
        using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

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
