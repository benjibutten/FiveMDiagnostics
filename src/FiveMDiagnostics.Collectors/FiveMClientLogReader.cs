namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

/// <summary>
/// Finds the client log the running game is writing, reads it, and keeps a copy with the session.
/// </summary>
/// <remarks>
/// <para>
/// The file is open for writing by the game for the whole evening, so it is opened with the widest
/// share the platform allows. Anything narrower fails while the game runs, which is the only time the
/// answer is worth having.
/// </para>
/// <para>
/// The copy exists because every other artifact the session produces is kept next to the note that
/// reads it — traces, dumps, CSVs — and the one file that was not is the one that had to be fetched by
/// hand, once, nine days before anybody realised it held the answer. FiveM keeps a handful of logs and
/// rotates them; a copy is a few hundred kilobytes and it is the evening's own.
/// </para>
/// </remarks>
public static class FiveMClientLogReader
{
    /// <summary>
    /// How old a log may be and still be taken for this session's.
    /// </summary>
    /// <remarks>
    /// The newest file in the directory is the right one while the game runs and the wrong one an hour
    /// after it stopped, when it belongs to an evening that has already been written up. An hour is
    /// wide enough for a session started well after the game and narrow enough that yesterday's log is
    /// never mistaken for tonight's.
    /// </remarks>
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);

    /// <summary>
    /// Reads the newest client log written within the hour, or null when there is none to read.
    /// </summary>
    /// <param name="directories">
    /// Where to look. Defaults to the machine's own candidates; supplied by tests so they do not read
    /// the logs of a real install that happens to be on the build machine.
    /// </param>
    public static FiveMClientLog? Read(IEnumerable<string>? directories = null, DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow) - Freshness;

        foreach (var file in Newest(directories ?? CandidateDirectories()))
        {
            if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                return null;
            }

            if (TryRead(file.FullName) is { } log)
            {
                return log;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies the log next to the session's other artifacts, returning where it landed or null.
    /// </summary>
    /// <remarks>
    /// The name is kept as the client wrote it. It carries the start time, which is how the 13 September
    /// review anchored the file against the capture, and renaming it would throw that away.
    /// </remarks>
    public static string? CopyBeside(FiveMClientLog log, string workingDirectory)
    {
        try
        {
            Directory.CreateDirectory(workingDirectory);
            var destination = Path.Combine(workingDirectory, Path.GetFileName(log.Path));
            File.Copy(log.Path, destination, overwrite: true);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static FiveMClientLog? TryRead(string path)
    {
        try
        {
            // ReadWrite share, and Delete with it: the client rotates its own logs, and a handle that
            // blocks the rotation would make the app the reason the game misbehaved.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return FiveMClientLog.Parse(path, ReadLines(reader));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static IEnumerable<FileInfo> Newest(IEnumerable<string> directories)
    {
        var files = new List<FileInfo>();

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                files.AddRange(new DirectoryInfo(directory).GetFiles("CitizenFX_log_*.log"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Measured on this machine: twelve seconds of flushed writes into a log held open the way FiveM
        // holds its own, and the enumeration still reported the moment the handle was opened — 09:37:16
        // against a real 09:37:28. Windows does not push the write through to the directory entry while
        // a handle is open, and GetFiles hands out that entry. Refresh asks the file itself.
        //
        // It is not a cosmetic difference. The evening's log is opened when the game starts and written
        // to for hours, so a stale entry makes it look older by exactly as long as the evening has run:
        // an hour in, the freshness check below rejects the only log that matters, Read returns null,
        // and every FiveM line goes quiet for the rest of the session without saying why.
        foreach (var file in files)
        {
            try
            {
                file.Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files.OrderByDescending(file => file.LastWriteTimeUtc);
    }

    /// <summary>Where the client keeps its logs.</summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        yield return Path.Combine(localAppData, "FiveM", "FiveM.app", "logs");

        // Older builds wrote beside the executable rather than into a logs directory.
        yield return Path.Combine(localAppData, "FiveM", "FiveM.app");
        yield return Path.Combine(localAppData, "FiveM");
    }
}
