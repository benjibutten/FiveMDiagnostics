namespace FiveMDiagnostics.Core;

/// <summary>
/// Remembers which processes held VRAM last session, so a new one can say what showed up that did not
/// show up before.
/// </summary>
/// <remarks>
/// The Voicemod finding of 2026-09-11 — and, in the same session, <c>TwitchOverlayHelper</c>, <c>Spotify</c>
/// and <c>ChatGPT</c>, none of them mentioned anywhere in the log — was made by sorting two evenings'
/// <c>gpuprocs</c> CSVs side by side by hand. Everything needed for that comparison is already collected
/// every session; the only thing missing was keeping one evening's process names around long enough to
/// compare against the next. A flat, one-name-per-line file is enough: this is a set, not a table, and
/// nothing here needs to survive being hand-edited or read by anything else.
/// </remarks>
public static class PreviousSessionProcessLog
{
    private const string FileName = "last-session-processes.txt";

    /// <summary>The set of process names the previous session saw, or empty when there is no earlier file.</summary>
    /// <remarks>
    /// Empty rather than null on any failure to read, including a missing file. The first session on a
    /// machine — or the first since the file format existed — has nothing to compare against, and that is
    /// a fact about history, not an error worth surfacing.
    /// </remarks>
    public static IReadOnlySet<string> TryLoad(string workingDirectory)
    {
        try
        {
            var path = PathIn(workingDirectory);
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)), StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Overwrites the file with this session's process names, for the next session to read.</summary>
    /// <remarks>
    /// Best effort. Losing this file costs one evening's comparison, not the session it belongs to, so a
    /// write failure here must not interrupt the shutdown it runs during.
    /// </remarks>
    public static void Save(string workingDirectory, IEnumerable<string> processNames)
    {
        try
        {
            Directory.CreateDirectory(workingDirectory);
            var ordered = processNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(PathIn(workingDirectory), ordered);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string PathIn(string workingDirectory) => Path.Combine(workingDirectory, FileName);
}
