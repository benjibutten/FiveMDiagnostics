namespace FiveMDiagnostics.Core;

/// <summary>
/// Finds the FiveM crash dumps no session has reported yet.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on file names rather than on the time of the last check. A dump is written while the game is
/// dying and can be half a file when it is first seen; one that does not parse yet is simply not
/// recorded, and the next check reads it again. A timestamp would have moved past it for good.
/// </para>
/// <para>
/// The very first check on a machine has no record, and reporting every dump FiveM has kept since it was
/// installed would bury the one worth reading. It reports the last week and records the rest as seen.
/// </para>
/// </remarks>
public static class FiveMCrashDumpLog
{
    private const string FileName = "reported-crash-dumps.txt";
    private static readonly TimeSpan FirstCheckLookback = TimeSpan.FromDays(7);

    /// <summary>How close together two dumps are written when FiveM writes both for the same crash.</summary>
    private static readonly TimeSpan SameCrash = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Serialises checks. The session start and the game-process watch can both ask within the same
    /// second, and two readers of the same record would report the same crash twice.
    /// </summary>
    private static readonly object Sync = new();

    /// <summary>Where FiveM writes its dumps, or null when there is no local app data folder.</summary>
    public static string? DefaultCrashDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData
            ? Path.Combine(localAppData, "FiveM", "FiveM.app", "crashes")
            : null;

    /// <summary>The dumps not reported before, oldest first, and records them as reported.</summary>
    public static IReadOnlyList<FiveMCrashDump> TakeNew(string crashDirectory, string workingDirectory, DateTimeOffset now)
    {
        lock (Sync)
        {
            try
            {
                if (!Directory.Exists(crashDirectory))
                {
                    return [];
                }

                var recordPath = Path.Combine(workingDirectory, FileName);
                var reported = File.Exists(recordPath)
                    ? new HashSet<string>(File.ReadAllLines(recordPath).Where(line => line.Length > 0), StringComparer.OrdinalIgnoreCase)
                    : null;
                var cutoff = reported is null ? now - FirstCheckLookback : DateTimeOffset.MinValue;

                var found = new List<FiveMCrashDump>();
                var recorded = new List<string>();

                foreach (var file in new DirectoryInfo(crashDirectory).GetFiles("*.dmp").OrderBy(file => file.LastWriteTimeUtc))
                {
                    if (reported?.Contains(file.Name) == true || file.LastWriteTimeUtc < cutoff.UtcDateTime)
                    {
                        recorded.Add(file.Name);
                        continue;
                    }

                    if (MinidumpReader.TryRead(file.FullName) is { } dump)
                    {
                        found.Add(dump);
                        recorded.Add(file.Name);
                    }
                }

                // Rewritten from what is on disk, so dumps FiveM has since deleted drop out of the record.
                Directory.CreateDirectory(workingDirectory);
                File.WriteAllLines(recordPath, recorded);
                return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing this costs a crash line, not the session it would have been written into.
                return [];
            }
        }
    }

    /// <summary>
    /// One session-log line per crash. FiveM can write a second dump a second after the first for the same
    /// crash, and it is named on the first one's line rather than read as a crash of its own.
    /// </summary>
    /// <param name="dumps">Dumps in any order; the lines come oldest crash first.</param>
    /// <param name="frames">Frames the session saw presented; see <see cref="FiveMCrashDump.Describe"/>.</param>
    public static IReadOnlyList<string> Describe(
        IReadOnlyList<FiveMCrashDump> dumps,
        DateTimeOffset now,
        IReadOnlyList<FrameTelemetrySample> frames)
    {
        var lines = new List<string>();
        FiveMCrashDump? first = null;
        foreach (var dump in dumps.OrderBy(dump => dump.CrashedAt))
        {
            if (first is not null && dump.CrashedAt - first.CrashedAt <= SameCrash)
            {
                var after = (dump.CrashedAt - first.CrashedAt).TotalSeconds;
                lines[^1] += $" En dump till från samma krasch, {after:F0} s senare: {dump.Location} ({dump.FileName}).";
                continue;
            }

            first = dump;
            lines.Add(dump.Describe(now, frames));
        }

        return lines;
    }
}
