namespace FiveMDiagnostics.Core;

/// <summary>
/// Remembers the resource list the server sent last session, so a new one can say what the server
/// added or removed.
/// </summary>
/// <remarks>
/// The same idea as <see cref="PreviousSessionProcessLog"/> and for the same reason: the comparison is
/// what carries the information, and the only thing missing was keeping one evening's list around long
/// enough to make it. "Har servern ändrats?" has stood as the investigation's one unanswerable question
/// for a month — the server cannot be measured from this machine — but the list of what it asks the
/// client to load arrives on every connect, and two of them side by side answer it exactly.
/// </remarks>
public static class PreviousSessionResourceLog
{
    private const string FileName = "last-session-resources.txt";

    /// <summary>The resource names the previous session saw, or empty when there is no earlier file.</summary>
    public static IReadOnlySet<string> TryLoad(string workingDirectory)
    {
        try
        {
            var path = PathIn(workingDirectory);
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)),
                StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Overwrites the file with this session's resource names, for the next session to read.
    /// </summary>
    /// <remarks>
    /// Best effort, and skipped entirely for an empty list. A session that never reached the server has
    /// no list, and writing an empty one would erase the comparison the next evening was going to make.
    /// </remarks>
    public static void Save(string workingDirectory, IReadOnlyCollection<string> resourceNames)
    {
        if (resourceNames.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(workingDirectory);
            var ordered = resourceNames
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
