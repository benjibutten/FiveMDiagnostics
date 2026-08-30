namespace FiveMDiagnostics.Collectors;

using System.Xml.Linq;

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
    /// Returns a one-line summary of the game's graphics settings, or null when the file was not found
    /// or could not be read as one.
    /// </summary>
    /// <param name="candidatePaths">
    /// Where to look, in order. Defaults to the machine's own candidates; supplied by tests, which have
    /// to be able to run on a machine that has the game installed without reading its settings instead
    /// of the fixture's.
    /// </param>
    public static string? Describe(IEnumerable<string>? candidatePaths = null)
    {
        foreach (var path in candidatePaths ?? CandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var document = XDocument.Load(path);
                var graphics = Read(document, "graphics", GraphicsKeys);
                var video = Read(document, "video", VideoKeys);
                if (graphics.Count == 0 && video.Count == 0)
                {
                    continue;
                }

                var parts = graphics.Concat(video).Select(entry => $"{entry.Key} {entry.Value}");
                var mode = WindowModeNote(video);

                return $"Spelets grafikinställningar ({path}): {string.Join(", ", parts)}.{mode}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // A file that exists but cannot be read as this format is the same as no file. The next
                // candidate still gets its turn.
            }
        }

        return null;
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
    /// Where the file is looked for, in order.
    /// </summary>
    /// <remarks>
    /// FiveM runs the game's own settings code, so it writes the same file a plain GTA V install does.
    /// The Documents folder is the ordinary place for it; the two OneDrive paths are there because
    /// Documents is redirected into OneDrive on a great many machines, including the one this app was
    /// written for, and the redirected path is the only one that exists when it is.
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
    }
}
