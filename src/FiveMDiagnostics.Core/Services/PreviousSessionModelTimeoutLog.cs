namespace FiveMDiagnostics.Core;

/// <summary>The models that timed out in one client log, and which log that was.</summary>
/// <param name="LogFile">
/// The client log's file name. It carries the game's start time, so two app sessions that read the same
/// game run can tell they are not two runs.
/// </param>
public sealed record ModelTimeoutHistory(string LogFile, IReadOnlySet<string> Models);

/// <summary>
/// Remembers which models timed out last session, so a new session can say which of its own timeouts
/// are the same models again.
/// </summary>
/// <remarks>
/// Unlike <see cref="PreviousSessionResourceLog"/>, an empty list is written: a session that reached the
/// server and had no timeouts is an answer, and leaving an older list in place would make the next
/// session report a repeat that did not happen.
/// </remarks>
public static class PreviousSessionModelTimeoutLog
{
    private const string FileName = "last-session-model-timeouts.txt";

    /// <summary>What the previous session recorded, or null when no session has written the file.</summary>
    public static ModelTimeoutHistory? TryLoad(string workingDirectory)
    {
        try
        {
            var path = PathIn(workingDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path);
            return lines.Length == 0
                ? null
                : new ModelTimeoutHistory(
                    lines[0],
                    new HashSet<string>(lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)), StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Overwrites the file with the log's name and its timed-out models, empty or not. Best effort.
    /// </summary>
    public static void Save(string workingDirectory, string logFile, IReadOnlyCollection<string> models)
    {
        try
        {
            Directory.CreateDirectory(workingDirectory);
            File.WriteAllLines(PathIn(workingDirectory), models.Order(StringComparer.OrdinalIgnoreCase).Prepend(logFile));
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
