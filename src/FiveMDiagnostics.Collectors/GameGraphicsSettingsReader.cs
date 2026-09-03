namespace FiveMDiagnostics.Collectors;

using System.Xml.Linq;

using FiveMDiagnostics.Core;

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
    /// True when the window mode accounts for every frame going through the compositor.
    /// </summary>
    /// <remarks>
    /// Windowed 1 and 2 are both windows, and a window cannot get an independent flip: DWM composes it,
    /// by definition, on every frame. Once this is known the analysis has no reason to report the
    /// composed present path as a finding on each incident — see <c>IWindowModeAwareAnalysis</c>.
    /// </remarks>
    public bool ExplainsComposedPresent =>
        Values.TryGetValue("Windowed", out var mode) && mode is "1" or "2";

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
/// Not complete on its own, and that gap turned out to be the investigation's answer.
/// <c>Extended Texture Budget</c> is FiveM's own setting and is not in this file — it is
/// <c>vid_budgetScale</c> in <c>fivem.cfg</c>, in the same directory, and it is the setting that
/// decides how much video memory the game will take. <see cref="FiveMClientConfigReader"/> reads it,
/// and both lines belong together: this one says what the picture costs, that one says what the card
/// is allowed to hold.
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
    /// <para>
    /// Several layouts, because the client has used more than one and an installation moved to another
    /// drive keeps none of them. A path that does not exist costs one <c>File.Exists</c>, so the list is
    /// allowed to be generous; what is not allowed is the previous behaviour, which searched none of
    /// them and reported a fifteen-month-old file as the session's settings.
    /// </para>
    /// <para>
    /// The file is named <c>gta5_settings.xml</c>, and that is the whole of what the previous version of
    /// this method got wrong. It searched <c>%appdata%\CitizenFX\</c> — the right directory, arrived at
    /// after three sessions of argument — for <c>settings.xml</c>, found nothing, and fell back to a
    /// Rockstar file from June 2025 for a twelfth consecutive session. Both names are listed now:
    /// the client has used the plain one in older builds, and a name that is not there costs a stat.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> FiveMCandidatePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string[] names = ["gta5_settings.xml", "settings.xml"];

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "citizen", name);
                yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "data", "game-storage", name);
                yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "data", "cache", name);
                yield return Path.Combine(localAppData, "FiveM", "FiveM.app", name);
            }
        }

        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(roamingAppData, "CitizenFX", name);
                yield return Path.Combine(roamingAppData, "CitizenFX", "Rockstar Games", "GTA V", name);
            }
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
    private readonly IReadOnlyList<string>? _configPaths;
    private readonly DateTimeOffset _sessionStartUtc;
    private readonly object _sync = new();

    private GameGraphicsSettings? _current;
    private FiveMClientConfig? _currentConfig;

    /// <summary>
    /// Whether the settings that were read account for a composed present path.
    /// </summary>
    /// <remarks>
    /// False when no settings file could be read, which is the conservative answer: an unexplained
    /// compositor stays on every incident until something explains it.
    /// </remarks>
    public bool ExplainsComposedPresent
    {
        get
        {
            lock (_sync)
            {
                return _current?.ExplainsComposedPresent ?? false;
            }
        }
    }

    public GameGraphicsSettingsMonitor(
        DateTimeOffset sessionStartUtc,
        IEnumerable<string>? candidatePaths = null,
        IEnumerable<string>? configPaths = null)
    {
        _sessionStartUtc = sessionStartUtc;
        _candidatePaths = candidatePaths?.ToArray();
        _configPaths = configPaths?.ToArray();
    }

    /// <summary>
    /// The client configuration as last read, so the VRAM budget can be stated against the texture
    /// budget the game was actually given. Null until <see cref="DescribeInitial"/> has run, and null
    /// afterwards on a machine with no <c>fivem.cfg</c>.
    /// </summary>
    public FiveMClientConfig? ClientConfig
    {
        get
        {
            lock (_sync)
            {
                return _currentConfig;
            }
        }
    }

    /// <summary>
    /// Reads the settings for the first time and returns the lines for the session log — the graphics
    /// file first, the client's texture budget second. Empty when neither could be read.
    /// </summary>
    /// <remarks>
    /// Two lines rather than one sentence, because they come from two files with two write times and
    /// either can be absent without saying anything about the other.
    /// </remarks>
    public IReadOnlyList<string> DescribeInitial()
    {
        var settings = GameGraphicsSettingsReader.Read(_candidatePaths, out var older);
        var config = FiveMClientConfigReader.Read(_configPaths);
        lock (_sync)
        {
            _current = settings;
            _currentConfig = config;
        }

        var lines = new List<string>(2);
        if (settings?.Describe(_sessionStartUtc, older) is { } graphics)
        {
            lines.Add(graphics);
        }

        if (config?.Describe() is { } budget)
        {
            lines.Add(budget);
        }

        return lines;
    }

    /// <summary>
    /// Re-reads and returns a line when something moved since the last reading, null otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file that disappears is reported too. It means the copy the session's record points at is gone
    /// — a OneDrive move, most likely — and the values above no longer have a source anybody can check.
    /// </para>
    /// <para>
    /// What is read is what is held, both files, whether or not the reading was a file. Keeping the last
    /// successful reading when a file vanished had two consequences and neither was the intended one:
    /// the disappearance was re-reported on every check for the rest of the evening, because the state
    /// it was compared against never moved, and everything that reasons off these readings — the window
    /// mode, the texture budget — went on being told the values of a file that is not there. The
    /// conservative answer is the honest one: nothing can be read, so nothing is known.
    /// </para>
    /// <para>
    /// Both files are reported in the same call rather than the first one that has something to say. The
    /// two readings were already taken together and both were already stored, so returning on the budget
    /// threw away a graphics change that had happened in the same interval — and it was thrown away for
    /// good, because the next check compares against the reading this one had just saved. A texture
    /// quality step taken in the same five minutes as a slider move is exactly the pair of changes a
    /// later comparison turns on.
    /// </para>
    /// </remarks>
    public string? DescribeChange()
    {
        var settings = GameGraphicsSettingsReader.Read(_candidatePaths, out var older);
        var config = FiveMClientConfigReader.Read(_configPaths);

        GameGraphicsSettings? previous;
        FiveMClientConfig? previousConfig;
        lock (_sync)
        {
            previous = _current;
            previousConfig = _currentConfig;
            _current = settings;
            _currentConfig = config;
        }

        var lines = new List<string>(2);

        if (DescribeConfigChange(previousConfig, config) is { } configChange)
        {
            lines.Add(configChange);
        }

        if (DescribeSettingsChange(previous, settings, older) is { } settingsChange)
        {
            lines.Add(settingsChange);
        }

        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>
    /// What moved in <c>fivem.cfg</c> since the last reading, or null when nothing did.
    /// </summary>
    /// <remarks>
    /// The texture budget is the setting somebody will be asked to change between two sessions, so a
    /// change to it mid-session is worth its own sentence rather than a footnote on the graphics one.
    /// </remarks>
    private string? DescribeConfigChange(FiveMClientConfig? previous, FiveMClientConfig? current)
    {
        if (current is null)
        {
            return previous is null
                ? null
                : $"FiveM:s klientkonfiguration kan inte längre läsas; filen {previous.Path} finns inte "
                    + "kvar. Texturbudgeten är inte längre känd, och ingenting i resten av sessionen "
                    + "räknas mot den.";
        }

        if (previous is null)
        {
            return $"{current.Describe()} Filen fanns inte när sessionen startade.";
        }

        if (previous.Matches(current))
        {
            return null;
        }

        return $"FiveM:s Extended Texture Budget ändrades under sessionen: "
            + $"{previous.BudgetScale?.ToString() ?? "standard"} → "
            + $"{current.BudgetScale?.ToString() ?? "standard"}. {current.Describe()} "
            + "Telemetrin före och efter den tidpunkten beskriver två olika texturbudgetar.";
    }

    /// <summary>What moved in the graphics settings file since the last reading, or null when nothing did.</summary>
    private string? DescribeSettingsChange(
        GameGraphicsSettings? previous,
        GameGraphicsSettings? settings,
        IReadOnlyList<GameSettingsCandidate> older)
    {
        if (settings is null)
        {
            return previous is null
                ? null
                : $"Spelets grafikinställningar kan inte längre läsas; filen {previous.Path} finns inte kvar. "
                    + "Värdena i sessionsstarten står kvar som det sist lästa, men ingenting i resten av "
                    + "sessionen bedöms mot dem.";
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
