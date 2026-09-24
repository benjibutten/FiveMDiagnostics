namespace FiveMDiagnostics.Core;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>What a line from the client log turned out to be about.</summary>
/// <remarks>
/// Only the kinds the investigation has a use for. Everything else that is coloured as an error keeps
/// its <see cref="Other"/> label rather than being dropped: the shapes below were read off one evening's
/// log, and a failure this app has never seen must not be filtered out by a list written from the
/// failures it has.
/// </remarks>
public enum FiveMClientLogKind
{
    /// <summary>The client could not download an asset from the server.</summary>
    DownloadFailure,

    /// <summary>The game asked for a model and gave up waiting for it.</summary>
    ModelTimeout,

    /// <summary>A resource file could not be mounted into the game's file system.</summary>
    MountFailure,

    /// <summary>A streaming pool or streaming memory ran out.</summary>
    PoolExhaustion,

    /// <summary>A resource's Lua threw.</summary>
    ScriptError,

    /// <summary>Coloured as an error and none of the above.</summary>
    Other,
}

/// <summary>One line of the client log, with the colour codes taken out.</summary>
/// <param name="At">
/// Wall clock, when the log said what it started at; otherwise the offset alone is carried and this is
/// null. Every line in the file is stamped with milliseconds since the client started, and the only
/// absolute time in it is the <c>BEGIN LOGGING AT</c> banner.
/// </param>
/// <param name="Asset">
/// The file or model the line is about, when it names one. It is the difference between "five downloads
/// failed" and "the MLO's .ytyp never arrived".
/// </param>
public sealed record FiveMClientLogEntry(
    FiveMClientLogKind Kind,
    TimeSpan Offset,
    DateTimeOffset? At,
    string Channel,
    string Text,
    string? Asset)
{
    /// <summary>The entry on one line, cut to something a session line can hold.</summary>
    public string Describe()
    {
        var when = At is { } at ? at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : $"+{Offset:hh\\:mm\\:ss}";
        var text = Text.Length > 160 ? Text[..160].TrimEnd() + "…" : Text;
        return $"{when} {text}";
    }
}

/// <summary>
/// What FiveM's own log says about the evening, which is the one source in the investigation that can
/// tell a streaming problem from a download problem.
/// </summary>
/// <remarks>
/// <para>
/// Written after 20 September, where a texture budget was raised a step to fix MLOs that would not load,
/// the raise did not fix them, and nothing on the machine could say why — because the only witness was a
/// file the app had never opened. It had been opened once, by hand, on 13 September, to date a crash.
/// </para>
/// <para>
/// That same file already held the answer the investigation was missing. Its
/// <c>ResourceCacheDevice reporting failure downloading k4mb1_post.ytyp: Failed to connect to … port
/// 30120 after 21029 ms</c> is an MLO's type definition that never arrived over the network, and no
/// texture budget on any card fixes an asset the client does not have. The budget governs what may stay
/// resident; it has no opinion about what was downloaded.
/// </para>
/// <para>
/// The <c>Required resources</c> line is the other half. The server sends its whole resource list on
/// connect — some four hundred names, map and MLO resources among them — so two evenings' logs side by
/// side say exactly what the server added or removed. "Did the server change?" has been unanswerable
/// from this machine for a month, and it was in the log all along.
/// </para>
/// </remarks>
/// <param name="CacheEntriesSaved">
/// <c>ResourceCache::AddEntry</c> lines: files the client wrote into its cache because they were not
/// already there.
/// </param>
public sealed record FiveMClientLog(
    string Path,
    DateTimeOffset? StartedAtUtc,
    IReadOnlyList<string> Resources,
    IReadOnlyList<FiveMClientLogEntry> Entries,
    int CacheEntriesSaved = 0)
{
    /// <summary>
    /// Cache entries a session writes when it starts from an empty cache.
    /// </summary>
    /// <remarks>
    /// The three logs the investigation holds: 5 669 on 12 September and 6 500 on 22 September, both
    /// evenings the cache had been emptied before the game started, against 327 on 21 September with the
    /// cache left alone. A server update adds what changed, which is hundreds, not thousands.
    /// </remarks>
    public const int ColdCacheEntries = 2000;

    /// <summary>Whether the client built its cache up from empty during this log.</summary>
    public bool StartedWithColdCache => CacheEntriesSaved >= ColdCacheEntries;

    /// <summary>The models the game asked for and gave up waiting for, each named once.</summary>
    public IReadOnlyList<string> TimedOutModels =>
        Entries.Where(entry => entry.Kind == FiveMClientLogKind.ModelTimeout)
            .Select(entry => entry.Asset)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary><c>[      140] [         FiveM]             MainThrd/ text</c>.</summary>
    /// <remarks>
    /// The thread field is not one word — <c>UV loop: httpClient</c> is a thread name, and it is the one
    /// the download failures are written on. It is taken as everything up to the first slash instead,
    /// which no thread name contains and many messages do.
    /// </remarks>
    private static readonly Regex LinePattern = new(
        @"^\[\s*(?<ms>\d+)\]\s*\[\s*(?<channel>[^\]]*?)\s*\]\s*(?<thread>[^/]*?)/\s?(?<text>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary><c>--- BEGIN LOGGING AT Sat Sep 12 21:01:04 2026 ---</c>, the file's only wall clock.</summary>
    private static readonly Regex BeganPattern = new(
        @"BEGIN LOGGING AT (?<when>[A-Za-z]{3} [A-Za-z]{3} +\d+ \d{2}:\d{2}:\d{2} \d{4})",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>FiveM's console colour codes, which belong on a console and not in a session log.</summary>
    private static readonly Regex ColourPattern = new(@"\^\d", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary><c>ResourceCacheDevice reporting failure downloading k4mb1_post.ytyp: …</c>.</summary>
    private static readonly Regex DownloadPattern = new(
        @"ResourceCacheDevice reporting failure downloading (?<asset>\S+?):",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// <c>Requesting of a model timed out "2003410943:v_9_kitchen_unit"</c>. The hash is signed, so it
    /// can also read <c>"-941653984:v_31_walltext005"</c>.
    /// </summary>
    private static readonly Regex ModelTimeoutPattern = new(
        """Requesting of a model timed out "(?:-?\d+:)?(?<asset>[^"]+)""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary><c>failed loading resources:/ultra-voltlab/audiodata/dlchei4_sounds.dat in data file mounter …</c>.</summary>
    private static readonly Regex MountPattern = new(
        @"failed loading (?<asset>resources:/\S+)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// What a streaming pool running dry might be called.
    /// </summary>
    /// <remarks>
    /// A guess, and marked as one wherever it is reported. No log in this investigation has ever carried
    /// such a line, so there is no real example to match against — which is exactly why an unmatched
    /// error line is kept as <see cref="FiveMClientLogKind.Other"/> rather than discarded. If the
    /// wording turns out to be something else, the line still reaches the session log; it simply reaches
    /// it under the wrong heading, which a reader can fix and a filter cannot.
    /// </remarks>
    private static readonly string[] PoolVocabulary =
    [
        "streaming memory",
        "out of streaming",
        "streaming pool",
        "pool is full",
        "pool full",
        "pool overflow",
        "store is full",
    ];

    /// <summary>The entries worth calling a streaming problem, in the order they happened.</summary>
    public IReadOnlyList<FiveMClientLogEntry> StreamingFailures =>
        Entries.Where(entry => entry.Kind
            is FiveMClientLogKind.DownloadFailure
            or FiveMClientLogKind.ModelTimeout
            or FiveMClientLogKind.MountFailure
            or FiveMClientLogKind.PoolExhaustion).ToArray();

    /// <summary>Everything else the log coloured as an error.</summary>
    public IReadOnlyList<FiveMClientLogEntry> Errors =>
        Entries.Where(entry => entry.Kind is FiveMClientLogKind.ScriptError or FiveMClientLogKind.Other).ToArray();

    /// <summary>The most entries worth naming in one session line.</summary>
    private const int MaxNamed = 6;

    /// <summary>
    /// What the server asked the client to load, and what changed since the last session.
    /// </summary>
    /// <remarks>
    /// The diff is the point. A count says the server is large, which everyone knew; the names say what
    /// arrived between two evenings, which is the only way this machine can answer "did the server
    /// change?" — and that question has been the standing reservation of the whole investigation.
    /// </remarks>
    public string DescribeResources(IReadOnlySet<string> previous)
    {
        if (Resources.Count == 0)
        {
            return "FiveM:s klientlogg har ingen \"Required resources\"-rad, så serverns resurslista kan "
                + "inte jämföras mot förra sessionen. Raden skrivs när klienten ansluter; en logg utan "
                + "den är från en start som aldrig kom in på servern.";
        }

        var line = $"Servern begärde {Resources.Count} resurser den här sessionen.";

        if (previous.Count == 0)
        {
            return line + " Ingen lista från förra sessionen att jämföra mot — den här sparas, så nästa "
                + "session kan säga vad som ändrats.";
        }

        var added = Resources.Where(name => !previous.Contains(name)).ToArray();
        var removed = previous.Where(name => !Resources.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();

        if (added.Length == 0 && removed.Length == 0)
        {
            return line + $" Exakt samma lista som förra sessionen ({previous.Count} resurser): servern "
                + "har inte lagt till eller tagit bort något mellan de två kvällarna.";
        }

        var parts = new List<string>();
        if (added.Length > 0)
        {
            parts.Add($"{added.Length} tillkom ({Name(added)})");
        }

        if (removed.Length > 0)
        {
            parts.Add($"{removed.Length} föll bort ({Name(removed)})");
        }

        return line + $" Mot förra sessionens {previous.Count}: {string.Join(", ", parts)}. En resurs som "
            + "tillkommit är innehåll klienten inte hade förut, och kartor och MLO:er ligger i den här "
            + "listan tillsammans med allt annat.";
    }

    /// <summary>
    /// What the client said about streaming, including — deliberately — what it did not say.
    /// </summary>
    /// <remarks>
    /// The two failures are not the same problem and the line must not blur them. A download that timed
    /// out means the client never got the file, and no texture budget on any card holds a file that was
    /// never received; a pool running dry means it got the file and had nowhere to put it, and that one
    /// is the slider's. Twelve evenings of advice about the slider rested on nobody being able to tell
    /// those apart.
    /// </remarks>
    /// <param name="timedOutInAnEarlierRun">
    /// The models that timed out in the previous session's client log, or null when there is none to
    /// compare with. The caller passes null for a list read from this same log.
    /// </param>
    public string DescribeStreaming(IReadOnlySet<string>? timedOutInAnEarlierRun = null)
    {
        var failures = StreamingFailures;
        if (failures.Count == 0)
        {
            return "FiveM:s klientlogg: inga misslyckade nedladdningar, modelltimeouts eller "
                + "monteringsfel. Ingenting i klientens egen logg säger att den saknade något den "
                + "bad om.";
        }

        var parts = new List<string>();

        foreach (var (kind, what) in new[]
        {
            (FiveMClientLogKind.DownloadFailure, "kunde inte laddas ned från servern"),
            (FiveMClientLogKind.ModelTimeout, "begärdes men kom aldrig"),
            (FiveMClientLogKind.MountFailure, "kunde inte monteras"),
            (FiveMClientLogKind.PoolExhaustion, "rapporterades som slut på strömningsminne"),
        })
        {
            var of = failures.Where(entry => entry.Kind == kind).ToArray();
            if (of.Length == 0)
            {
                continue;
            }

            var assets = of.Select(entry => entry.Asset).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            // Files first, lines second, and only when they differ. The count used to be the line count
            // alone: the 09-21 log's eleven timeouts on three models read as eleven missing files, next
            // to a list of three names. Which number is the interesting one depends on the question, and
            // one model failing seven times is not seven models failing once.
            parts.Add((assets.Length, of.Length) switch
            {
                (0, _) => $"{of.Length} {what} ({of[0].Describe()})",
                var (files, lines) when files == lines => $"{files} {what} ({Name(assets)})",
                var (files, lines) => $"{files} {what}, på {lines} rader ({Name(assets)})",
            });
        }

        var line = $"FiveM:s klientlogg: {string.Join("; ", parts)}.";

        // One sentence per mechanism present, not one exclusive chain. The chain counted a model timeout
        // as a download failure, so on 2026-09-21 — zero downloads missing, eleven models timing out —
        // the line asserted nine times that "klienten fick aldrig filerna", which was false about the
        // one question that decides where to look next. It also had no arm for mount failures at all, so
        // a log carrying only those got no mechanism sentence.
        foreach (var sentence in Mechanisms(failures, StartedWithColdCache))
        {
            line += " " + sentence;
        }

        var again = TimedOutModels.Where(model => timedOutInAnEarlierRun?.Contains(model) == true).ToArray();
        if (again.Length > 0)
        {
            line += $" {Name(again)} fastnade också i förra sessionens klientlogg. Ett slumpmässigt läsfel "
                + "väljer inte ut samma modell två spelstarter i rad.";
        }

        return line;
    }

    /// <summary>
    /// What each kind of streaming failure means, for the kinds this log actually carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A log can carry all four and they have four different levers, so each gets a sentence saying what
    /// it <em>is</em>. None of those sentences rules anything out: that was the whole defect in the
    /// exclusive chain this replaced, which let one kind's verdict stand for the whole file.
    /// </para>
    /// <para>
    /// The one eliminative conclusion the investigation needs — a model that never became available with
    /// nothing else in the log — is drawn once, at the end, and only when the log has nothing else in it
    /// to explain the timeout. Written without that guard it claimed "kvar står cachen eller disken" on
    /// the 2026-09-21 log, whose next sentence points at seven mount failures and the server.
    /// </para>
    /// <para>
    /// The elimination names the request itself beside the cache and the disk. A model asked for while
    /// it is not registered — its definition not loaded at that moment — times out with every file
    /// delivered and every pool empty, and an unchanged server repeats it evening after evening.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Mechanisms(IReadOnlyList<FiveMClientLogEntry> failures, bool coldCache)
    {
        bool Any(FiveMClientLogKind kind) => failures.Any(entry => entry.Kind == kind);

        var downloads = Any(FiveMClientLogKind.DownloadFailure);
        var timeouts = Any(FiveMClientLogKind.ModelTimeout);
        var mounts = Any(FiveMClientLogKind.MountFailure);
        var pool = Any(FiveMClientLogKind.PoolExhaustion);

        if (downloads)
        {
            yield return "Det är nedladdning, inte videominne: klienten fick aldrig de filerna. "
                + "Texturbudgeten styr vad som får ligga kvar i minnet och kan inte hålla kvar något som "
                + "aldrig kom in. Ett .ytyp eller .ymap i listan är en MLO:s egen definition.";
        }

        if (timeouts)
        {
            yield return "Modelltimeouterna säger att modellen begärdes och inte blev tillgänglig i tid. "
                + "Raden i sig säger inte varför.";
        }

        if (mounts)
        {
            yield return "Monteringsfelen är resursens innehåll: filen kom fram men gick inte att läsa "
                + "som det den utger sig för att vara, och det åtgärdas hos den som driver servern.";
        }

        if (pool)
        {
            yield return "Mättat strömningsminne är det enda i listan Extended Texture Budget styr: "
                + "filerna kom fram och fick inte plats. Där är Extended Texture Budget rätt reglage.";
        }

        if (timeouts && coldCache)
        {
            yield return "Klienten byggde upp sin cache från tomt den här sessionen och modellerna fastnade "
                + "ändå, så cachen är inte orsaken.";
        }

        if (timeouts && !downloads && !mounts && !pool)
        {
            var remaining = coldCache
                ? "Då står disken spelet läses från kvar"
                : "Då står klientens egen strömningsväg kvar — cachen, eller disken spelet läses från —";

            yield return "Och eftersom loggen varken har nedladdningsfel, monteringsfel eller rader om "
                + $"mättat strömningsminne, kom filerna fram och fick plats. {remaining} eller själva "
                + "begäran: ett skript som ber om en modell som inte är registrerad just då, och det är "
                + "serverns sak.";
        }
        else if (timeouts)
        {
            yield return "Vad som orsakade timeouterna säger loggen inte, för den har fler former än en "
                + "— och var och en av de andra raderna kan göra att en modell aldrig blir tillgänglig. "
                + "De ska räknas var för sig innan något reglage rörs.";
        }
    }

    /// <summary>Everything the client coloured red or yellow, grouped so one repeated line is one line.</summary>
    public string? DescribeErrors()
    {
        var errors = Errors;
        if (errors.Count == 0)
        {
            return null;
        }

        var grouped = errors
            .GroupBy(entry => entry.Text, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ToArray();

        var named = grouped.Take(MaxNamed)
            .Select(group => group.Count() > 1 ? $"{group.Count()}× {group.First().Describe()}" : group.First().Describe());

        var more = grouped.Length > MaxNamed ? $" (+{grouped.Length - MaxNamed} andra)" : string.Empty;

        return $"FiveM:s klientlogg: {errors.Count} felrad(er) i {grouped.Length} varianter. "
            + string.Join(" | ", named) + more;
    }

    /// <summary>A handful of names, and a count for the rest.</summary>
    private static string Name(IReadOnlyList<string> names)
    {
        var shown = string.Join(", ", names.Take(MaxNamed));
        return names.Count > MaxNamed ? $"{shown} +{names.Count - MaxNamed} till" : shown;
    }

    /// <summary>
    /// Reads a client log into the handful of things worth keeping.
    /// </summary>
    /// <remarks>
    /// Line by line and by regular expression rather than by grammar, for the same reason
    /// <c>FiveMClientConfigReader</c> does it: twelve thousand lines of startup chatter, of which about
    /// twenty matter, and a parser for the rest would be code with no reader.
    /// </remarks>
    public static FiveMClientLog Parse(string path, IEnumerable<string> lines)
    {
        DateTimeOffset? startedAt = null;
        var resources = Array.Empty<string>();
        var cacheEntries = 0;
        var entries = new List<FiveMClientLogEntry>();

        foreach (var raw in lines)
        {
            var match = LinePattern.Match(raw);
            if (!match.Success)
            {
                continue;
            }

            var text = ColourPattern.Replace(match.Groups["text"].Value, string.Empty).Trim();
            var channel = match.Groups["channel"].Value;
            var offset = TimeSpan.FromMilliseconds(double.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture));

            if (startedAt is null && BeganPattern.Match(text) is { Success: true } began)
            {
                startedAt = ParseBanner(began.Groups["when"].Value);
                continue;
            }

            if (resources.Length == 0 && text.StartsWith("Required resources:", StringComparison.Ordinal))
            {
                resources = text["Required resources:".Length..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                continue;
            }

            if (text.StartsWith("ResourceCache::AddEntry", StringComparison.Ordinal))
            {
                cacheEntries++;
                continue;
            }

            if (Classify(raw, text) is not { } classified)
            {
                continue;
            }

            entries.Add(new FiveMClientLogEntry(
                classified.Kind,
                offset,
                startedAt is { } start ? start + offset : null,
                channel,
                text,
                classified.Asset));
        }

        return new FiveMClientLog(path, startedAt, resources, entries, cacheEntries);
    }

    /// <summary>
    /// What the line is about, or null when it is ordinary chatter.
    /// </summary>
    /// <param name="raw">
    /// The line as written, colour codes and all. The colours are how the client grades its own output,
    /// and <c>^1</c> is the difference between a message about an error and an error.
    /// </param>
    private static (FiveMClientLogKind Kind, string? Asset)? Classify(string raw, string text)
    {
        if (DownloadPattern.Match(text) is { Success: true } download)
        {
            return (FiveMClientLogKind.DownloadFailure, download.Groups["asset"].Value);
        }

        if (ModelTimeoutPattern.Match(text) is { Success: true } model)
        {
            return (FiveMClientLogKind.ModelTimeout, model.Groups["asset"].Value);
        }

        if (MountPattern.Match(text) is { Success: true } mount)
        {
            return (FiveMClientLogKind.MountFailure, mount.Groups["asset"].Value);
        }

        if (text.StartsWith("SCRIPT ERROR", StringComparison.Ordinal))
        {
            return (FiveMClientLogKind.ScriptError, null);
        }

        // The stack under a script error, one frame per line and coloured like the error itself. The
        // frames say nothing the error above them has not already said, and counting them as errors of
        // their own turns one failing resource into six entries and pushes the rest off the line.
        if (text.StartsWith("> ", StringComparison.Ordinal))
        {
            return null;
        }

        // Red and yellow are the client's own verdict on its output, and the only honest default for a
        // line nothing above recognised.
        if (!raw.Contains("^1", StringComparison.Ordinal) && !raw.Contains("^3", StringComparison.Ordinal))
        {
            return null;
        }

        // Last, behind the colour gate and behind SCRIPT ERROR, because this one is a guess and the
        // others are not. It used to run first and against every line, matching on "overflow" and "ran
        // out of" — so a Lua `SCRIPT ERROR: … stack overflow` was filed as an exhausted streaming pool,
        // and the line then advised the texture slider. Telling those two apart is the entire reason
        // this class exists, and a vocabulary loose enough to confuse them is worse than none.
        return PoolVocabulary.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))
            ? (FiveMClientLogKind.PoolExhaustion, null)
            : (FiveMClientLogKind.Other, null);
    }

    /// <summary>
    /// <c>Sat Sep 12 21:01:04 2026</c> as an instant, or null when it will not parse.
    /// </summary>
    /// <remarks>
    /// Read as UTC, which is what the file name is: the banner of the 12 September log says 21:01:04 and
    /// the app's own capture line puts the same moment at 23:01 local. The day is space padded for
    /// single digits, so the run of spaces is collapsed before parsing.
    /// </remarks>
    private static DateTimeOffset? ParseBanner(string value)
    {
        var normalized = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return DateTimeOffset.TryParseExact(
            normalized,
            "ddd MMM d HH:mm:ss yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
