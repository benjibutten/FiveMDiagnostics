namespace FiveMDiagnostics.Collectors;

using System.Xml.Linq;

/// <summary>
/// One reading of the graphics settings file: which copy was read, when it was last written, and what
/// it says.
/// </summary>
/// <param name="Path">The file this reading came from, which is not always the one anybody edits.</param>
/// <param name="LastWriteTimeUtc">
/// When the game last wrote the file. The single most important field here: settings recorded from a
/// file nobody has written since March describe a session that happened in March.
/// </param>
/// <param name="Values">
/// The values read, keyed by element name, so two readings can be compared without parsing a sentence.
/// </param>
public sealed record GameGraphicsSettings(
    string Path,
    DateTimeOffset LastWriteTimeUtc,
    IReadOnlyDictionary<string, string> Values,
    string WindowModeNote)
{
    /// <summary>The values on one line, in the order they were asked for.</summary>
    public string Summary => string.Join(", ", Values.Select(entry => $"{entry.Key} {entry.Value}"));

    /// <summary>
    /// Whether two readings describe the same file in the same state.
    /// </summary>
    /// <remarks>
    /// The write time is part of the comparison and not merely the values: a file rewritten with the
    /// same values still means the game restarted and wrote its configuration, which is worth a line
    /// when it happens mid-session.
    /// </remarks>
    public bool Matches(GameGraphicsSettings other)
    {
        return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
            && LastWriteTimeUtc == other.LastWriteTimeUtc
            && Values.Count == other.Values.Count
            && Values.All(entry => other.Values.TryGetValue(entry.Key, out var value) && value == entry.Value);
    }

    /// <summary>
    /// The line written to the session log.
    /// </summary>
    /// <param name="sessionStartUtc">
    /// When the session began, so the line can say whether the file predates it. That clause is the
    /// whole point: a reader who sees "senast skriven 2026-08-24 21:11, alltså före den här sessionen"
    /// knows without being told that these values may be describing an evening nobody played.
    /// </param>
    /// <param name="olderCandidates">
    /// The other copies that exist on the machine, which are named so a stale one on a non-redirected
    /// path can be recognised and deleted rather than quietly re-read every session.
    /// </param>
    public string Describe(DateTimeOffset? sessionStartUtc, IReadOnlyList<GameSettingsCandidate> olderCandidates)
    {
        var written = $"senast skriven {Local(LastWriteTimeUtc)}";
        if (sessionStartUtc is { } start && LastWriteTimeUtc < start)
        {
            written += ", alltså före den här sessionen";
        }

        var others = olderCandidates.Count == 0
            ? string.Empty
            : $" Ytterligare {olderCandidates.Count} kopia/kopior finns och är äldre: "
                + string.Join("; ", olderCandidates.Select(item => $"{item.Path} ({Local(item.LastWriteTimeUtc)})"))
                + ". Den nyaste är den som lästes.";

        var stale = IsStale(sessionStartUtc)
            ? $" VARNING: filen är {DescribeAge(sessionStartUtc)} gammal och beskriver därför inte den här "
                + "sessionen. Värdena ovan ska inte användas som facit i någon jämförelse — läs av "
                + "inställningarna i spelets meny i stället."
            : string.Empty;

        return $"Spelets grafikinställningar ({Path}, {written}): {Summary}.{WindowModeNote}{stale}{others}";
    }

    /// <summary>
    /// Age beyond which the file is describing some other evening entirely.
    /// </summary>
    /// <remarks>
    /// A file written before the session but during the same week is the ordinary case — the settings
    /// were changed and the game has not been restarted since. A week is where that stops being the
    /// explanation. On 1 September the only copy on the machine was written 2025-06-04, fifteen months
    /// earlier, and the app reported its values as this session's settings; three consecutive reviews
    /// compared evenings against a file from the summer before.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>Whether the file is too old to describe the session that read it.</summary>
    public bool IsStale(DateTimeOffset? sessionStartUtc)
    {
        return sessionStartUtc is { } start && start - LastWriteTimeUtc > StaleAfter;
    }

    private string DescribeAge(DateTimeOffset? sessionStartUtc)
    {
        var days = sessionStartUtc is { } start ? (start - LastWriteTimeUtc).TotalDays : 0;
        return days >= 60 ? $"{days / 30.44:F0} månader" : $"{days:F0} dagar";
    }

    internal static string Local(DateTimeOffset timestamp) => $"{timestamp.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>One copy of settings.xml that exists on the machine, and when it was last written.</summary>
public sealed record GameSettingsCandidate(string Path, DateTimeOffset LastWriteTimeUtc);

/// <summary>
/// Reads the graphics settings the game is running, so a session records them instead of relying on
/// somebody's memory of what was changed a week ago.
/// </summary>
/// <remarks>
/// <para>
/// Every comparison in this investigation has rested on a remembered setting. "High to Medium plus
/// extended texture budget one notch down" was written in a note, and the sessions on either side of it
/// were compared against that sentence; when the question later became whether the step could be taken
/// back, the answer needed the exact values and nobody had them. The same applies to the window mode,
/// which took four sessions and a hardware-level argument to establish and is one integer in this file.
/// </para>
/// <para>
/// The newest copy wins, and the line says when it was written. Taking the first candidate that existed
/// was wrong on exactly the machine this app was written for: Documents is redirected into OneDrive, a
/// file is routinely left behind on the non-redirected path, and the reader would then log settings
/// nobody had used for months — with nothing in the line to show it. Enumerating every candidate and
/// taking the latest <see cref="FileInfo.LastWriteTimeUtc"/> fixes the choice; printing that timestamp
/// makes the remaining failure self-evident rather than silent.
/// </para>
/// <para>
/// Best effort by design. The file may be absent, redirected into OneDrive, or written by a build that
/// names its elements differently, and none of those is worth a warning: the reader returns null and
/// the session is exactly as good as it was before. What it must not do is guess — a wrong setting in
/// the record is worse than no setting, because the next comparison would be made against it.
/// </para>
/// <para>
/// Not complete, and the gap matters. <c>Extended Texture Budget</c> is FiveM's own setting and is not
/// in this file — verified against a real install, which has no element for it in any section. It was
/// one of the two knobs moved on 27 August and it is likely the larger half of the 1 776 MB that change
/// took out of the game, so a reader must not treat the line below as the whole graphics configuration.
/// </para>
/// </remarks>
public static class GameGraphicsSettingsReader
{
    /// <summary>
    /// The elements worth recording, being the ones that cost VRAM or decide how the game presents.
    /// </summary>
    /// <remarks>
    /// Not the whole file. Two dozen values that never move would bury the four that the next decision
    /// turns on, and the point of the line is that it can be read at a glance in a session log.
    /// </remarks>
    private static readonly string[] GraphicsKeys =
    [
        "TextureQuality",
        "ShaderQuality",
        "ShadowQuality",
        "ReflectionQuality",
        "ReflectionMSAA",
        "MSAA",
        "GrassQuality",
        "AnisotropicFiltering",
    ];

    private static readonly string[] VideoKeys =
    [
        "ScreenWidth",
        "ScreenHeight",
        "RefreshRate",
        "Windowed",
        "VSync",
        "PauseOnFocusLoss",
    ];

    /// <summary>
    /// Returns a one-line summary of the game's graphics settings, or null when no candidate could be
    /// read as one.
    /// </summary>
    /// <param name="candidatePaths">
    /// Where to look. Defaults to the machine's own candidates; supplied by tests, which have to be able
    /// to run on a machine that has the game installed without reading its settings instead of the
    /// fixture's.
    /// </param>
    /// <param name="sessionStartUtc">When the session began, for the "before this session" clause.</param>
    public static string? Describe(IEnumerable<string>? candidatePaths = null, DateTimeOffset? sessionStartUtc = null)
    {
        var settings = Read(candidatePaths, out var olderCandidates);
        return settings?.Describe(sessionStartUtc, olderCandidates);
    }

    /// <summary>
    /// Reads the newest readable copy of the settings file, and reports the older ones it passed over.
    /// </summary>
    /// <remarks>
    /// Sorted by write time rather than by candidate order, and only then parsed. A newest copy that
    /// turns out not to be this format falls through to the next newest, which is the same rule the
    /// ordered walk had — it is only the order that changes.
    /// </remarks>
    public static GameGraphicsSettings? Read(
        IEnumerable<string>? candidatePaths,
        out IReadOnlyList<GameSettingsCandidate> olderCandidates)
    {
        olderCandidates = [];

        var existing = ExistingCandidates(candidatePaths ?? CandidatePaths())
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ToArray();

        for (var index = 0; index < existing.Length; index++)
        {
            if (TryRead(existing[index]) is not { } settings)
            {
                continue;
            }

            olderCandidates = existing.Skip(index + 1).ToArray();
            return settings;
        }

        return null;
    }

    /// <summary>
    /// Every candidate that exists right now, with its write time.
    /// </summary>
    /// <remarks>
    /// Duplicates are dropped: the OneDrive root and the redirected Documents path can resolve to the
    /// same file, and naming it twice as "another older copy" would invent a problem that is not there.
    /// </remarks>
    private static IEnumerable<GameSettingsCandidate> ExistingCandidates(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            GameSettingsCandidate candidate;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || !seen.Add(info.FullName))
                {
                    continue;
                }

                candidate = new GameSettingsCandidate(info.FullName, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A path that cannot even be stat'ed is the same as no file.
                continue;
            }

            yield return candidate;
        }
    }

    private static GameGraphicsSettings? TryRead(GameSettingsCandidate candidate)
    {
        try
        {
            var document = XDocument.Load(candidate.Path);
            var graphics = Read(document, "graphics", GraphicsKeys);
            var video = Read(document, "video", VideoKeys);
            if (graphics.Count == 0 && video.Count == 0)
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in graphics.Concat(video))
            {
                values[entry.Key] = entry.Value;
            }

            return new GameGraphicsSettings(
                candidate.Path,
                candidate.LastWriteTimeUtc,
                values,
                WindowModeNote(video));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // A file that exists but cannot be read as this format is the same as no file. The next
            // candidate still gets its turn.
            return null;
        }
    }

    /// <summary>
    /// Spells out the window mode, which is the one value here that nobody can read off an integer.
    /// </summary>
    /// <remarks>
    /// It cost four sessions. The in-game menu on this machine showed "Fullscreen" and sometimes
    /// "Fullscreen (Borderless)" for the same configuration, and whether the game could ever get an
    /// independent flip turned on which of them was true. The file has known it all along.
    /// </remarks>
    private static string WindowModeNote(IReadOnlyDictionary<string, string> video)
    {
        if (!video.TryGetValue("Windowed", out var value))
        {
            return string.Empty;
        }

        return value switch
        {
            "0" => " Windowed 0 = exklusiv fullskärm.",
            "1" => " Windowed 1 = fönsterläge med ram; ett sådant fönster kan aldrig få independent flip.",
            "2" => " Windowed 2 = fullskärm utan ram.",
            _ => string.Empty,
        };
    }

    private static Dictionary<string, string> Read(XDocument document, string section, string[] keys)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var element = document.Root?.Element(section);
        if (element is null)
        {
            return values;
        }

        foreach (var key in keys)
        {
            if (element.Element(key)?.Attribute("value")?.Value is { Length: > 0 } value)
            {
                values[key] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Where the file is looked for. No longer in priority order, because there is no priority any more:
    /// every one of these that exists is a candidate and the newest of them wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Documents folder is the ordinary place for it; the two OneDrive paths are there because
    /// Documents is redirected into OneDrive on a great many machines, including the one this app was
    /// written for — and on such a machine the non-redirected path usually still holds a copy from
    /// before the redirection, which is exactly the file that must not be picked.
    /// </para>
    /// <para>
    /// FiveM was assumed to write the same file a plain GTA V install does, and on the machine this app
    /// was written for it does not. The only copy under Documents there was last written in June 2025
    /// while the settings were demonstrably changed in August 2026, so the client is keeping its
    /// configuration under its own data directories — which is why those are searched too. They are
    /// listed after the Rockstar paths only for readability: order carries no priority any more, the
    /// newest file wins whichever root it came from.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> CandidatePaths()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        var oneDriveCommercial = Environment.GetEnvironmentVariable("OneDriveCommercial");

        var roots = new[] { documents, oneDrive, oneDriveCommercial, Path.Combine(profile, "Documents") };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            yield return Path.Combine(root, "Rockstar Games", "GTA V", "settings.xml");

            // The redirected case nests Documents inside the OneDrive root.
            yield return Path.Combine(root, "Documents", "Rockstar Games", "GTA V", "settings.xml");
        }

        foreach (var path in FiveMCandidatePaths())
        {
            yield return path;
        }
    }

    /// <summary>
    /// Where the FiveM client keeps a copy of the game's settings under its own data directories.
    /// </summary>
    /// <remarks>
    /// Several layouts, because the client has used more than one and an installation moved to another
    /// drive keeps none of them. A path that does not exist costs one <c>File.Exists</c>, so the list is
    /// allowed to be generous; what is not allowed is the previous behaviour, which searched none of
    /// them and reported a fifteen-month-old file as the session's settings.
    /// </remarks>
    private static IEnumerable<string> FiveMCandidatePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "citizen", "settings.xml");
            yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "data", "game-storage", "settings.xml");
            yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "data", "cache", "settings.xml");
            yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "settings.xml");
        }

        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData, "CitizenFX", "settings.xml");
            yield return Path.Combine(roamingAppData, "CitizenFX", "Rockstar Games", "GTA V", "settings.xml");
        }
    }
}

/// <summary>
/// Holds the settings a session started with and says so when they change while it is running.
/// </summary>
/// <remarks>
/// <para>
/// Re-read on a cadence rather than only at the end, and that is deliberate. This machine is not shut
/// down through the app — the computer is switched off with the session still running — so anything
/// written exclusively at session end is written on an evening in ten. A change of texture quality
/// mid-session is exactly the kind of thing the next comparison will turn on, and the cheapest way to
/// have it on record is to look every few minutes.
/// </para>
/// <para>
/// Silent while nothing moves. The check is a stat and, at most, a small XML parse, which is nothing
/// against a five second telemetry cadence.
/// </para>
/// </remarks>
public sealed class GameGraphicsSettingsMonitor
{
    private readonly IReadOnlyList<string>? _candidatePaths;
    private readonly DateTimeOffset _sessionStartUtc;
    private readonly object _sync = new();

    private GameGraphicsSettings? _current;

    public GameGraphicsSettingsMonitor(DateTimeOffset sessionStartUtc, IEnumerable<string>? candidatePaths = null)
    {
        _sessionStartUtc = sessionStartUtc;
        _candidatePaths = candidatePaths?.ToArray();
    }

    /// <summary>
    /// Reads the settings for the first time and returns the line for the session log, or null when no
    /// candidate could be read.
    /// </summary>
    public string? DescribeInitial()
    {
        var settings = GameGraphicsSettingsReader.Read(_candidatePaths, out var older);
        lock (_sync)
        {
            _current = settings;
        }

        return settings?.Describe(_sessionStartUtc, older);
    }

    /// <summary>
    /// Re-reads and returns a line when something moved since the last reading, null otherwise.
    /// </summary>
    /// <remarks>
    /// A file that disappears is reported too. It means the copy the session's record points at is gone
    /// — a OneDrive move, most likely — and the values above no longer have a source anybody can check.
    /// </remarks>
    public string? DescribeChange()
    {
        var settings = GameGraphicsSettingsReader.Read(_candidatePaths, out var older);

        GameGraphicsSettings? previous;
        lock (_sync)
        {
            previous = _current;
            if (settings is not null)
            {
                _current = settings;
            }
        }

        if (settings is null)
        {
            return previous is null
                ? null
                : $"Spelets grafikinställningar kan inte längre läsas; filen {previous.Path} finns inte kvar. "
                    + "Värdena i sessionsstarten står kvar som det sist lästa.";
        }

        if (previous is null)
        {
            return settings.Describe(_sessionStartUtc, older)
                + " Filen fanns inte när sessionen startade.";
        }

        if (previous.Matches(settings))
        {
            return null;
        }

        var changed = settings.Values
            .Where(entry => !previous.Values.TryGetValue(entry.Key, out var before) || before != entry.Value)
            .Select(entry => previous.Values.TryGetValue(entry.Key, out var before)
                ? $"{entry.Key} {before} → {entry.Value}"
                : $"{entry.Key} {entry.Value} (ny)")
            .Concat(previous.Values
                .Where(entry => !settings.Values.ContainsKey(entry.Key))
                .Select(entry => $"{entry.Key} {entry.Value} → borta"))
            .ToArray();

        var what = changed.Length > 0
            ? $"Ändrat: {string.Join(", ", changed)}."
            : "Inget värde ändrades, men filen skrevs om — spelet startades sannolikt om under sessionen.";

        return $"Spelets grafikinställningar skrevs om under sessionen ({settings.Path}, "
            + $"senast skriven {GameGraphicsSettings.Local(settings.LastWriteTimeUtc)}). {what} "
            + "Telemetrin före och efter den tidpunkten beskriver två olika konfigurationer.";
    }
}
