using System.IO;

namespace FiveMDiagnostics.App.Wpf.Services;

/// <summary>What a clear or a prune actually did, counted rather than assumed.</summary>
/// <param name="Failures">
/// One sentence per file that would not go. Never empty because a folder was already empty — that is a
/// clear with nothing in it, not a failure.
/// </param>
public sealed record HousekeepingResult(int FilesDeleted, long BytesFreed, IReadOnlyList<string> Failures)
{
    public static readonly HousekeepingResult Nothing = new(0, 0, []);

    public bool AnythingFailed => Failures.Count > 0;
}

/// <summary>One of FiveM's cache folders, with what it currently holds.</summary>
public sealed record CacheFolder(string Name, string Path, long Bytes, int Files);

/// <summary>
/// Clears the folders FiveM refills by itself.
/// </summary>
/// <remarks>
/// Deliberately three folders and not the whole of <c>data\</c>. <c>game-storage</c> (1,9 GB when this
/// was written) carries the Rockstar login, and <c>nui-storage</c> (633 MB) carries each server's saved
/// UI state — clearing either turns a routine cache clear into an evening that starts with a login
/// screen. The three below cost nothing but a longer first connection, which is what "clear the FiveM
/// cache" is understood to mean.
/// </remarks>
public static class FiveMCache
{
    private static readonly string[] FolderNames = ["cache", "server-cache", "server-cache-priv"];

    /// <summary>The <c>data\</c> directory, or null when FiveM is not installed for this user.</summary>
    public static string? DataDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData
            ? Path.Combine(localAppData, "FiveM", "FiveM.app", "data")
            : null;

    /// <summary>
    /// The folders that exist right now, measured. A folder FiveM has never created is left out rather
    /// than reported as empty, so the UI can say "nothing to clear" and mean it.
    /// </summary>
    public static IReadOnlyList<CacheFolder> Folders(string? dataDirectory = null) =>
        ExistingFolders(dataDirectory)
            .Select(folder =>
            {
                var files = EnumerateFiles(folder.Path);
                return folder with { Bytes = files.Sum(file => file.Length), Files = files.Length };
            })
            .ToArray();

    /// <summary>
    /// Empties each folder without removing the folder itself. FiveM recreates a missing one, but leaving
    /// it in place keeps the clear to files the user can see and keeps a half-finished delete from taking
    /// the directory with it.
    /// </summary>
    public static HousekeepingResult Clear(string? dataDirectory = null)
    {
        var deleted = 0;
        long freed = 0;
        var failures = new List<string>();

        // Unmeasured: the size worth reporting is what was actually deleted, counted as it goes.
        foreach (var folder in ExistingFolders(dataDirectory))
        {
            DeleteContents(folder.Path, ref deleted, ref freed, failures);
        }

        return new HousekeepingResult(deleted, freed, failures);
    }

    private static IEnumerable<CacheFolder> ExistingFolders(string? dataDirectory)
    {
        var root = dataDirectory ?? DataDirectory;
        if (root is null)
        {
            return [];
        }

        return FolderNames
            .Select(name => new CacheFolder(name, Path.Combine(root, name), 0, 0))
            .Where(folder => Directory.Exists(folder.Path));
    }

    /// <summary>
    /// Files first, then the directories they were in. Enumerating the whole tree up front and deleting
    /// afterwards means a file that appears mid-clear is simply left for next time, rather than making
    /// the enumerator throw halfway through.
    /// </summary>
    /// <remarks>
    /// <see cref="FileInfo.Length"/> is read without a guard on purpose. It is filled in by the enumeration
    /// and not read back from the file, so a file locked, denied or deleted since the listing still
    /// reports its size — checked on .NET 10.0.12 before the guards that were here came out.
    /// </remarks>
    private static void DeleteContents(string path, ref int deleted, ref long freed, List<string> failures)
    {
        foreach (var file in EnumerateFiles(path))
        {
            var size = file.Length;
            try
            {
                file.Delete();
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{file.Name}: {ex.Message}");
            }
        }

        // Deepest first, so a directory is only removed once its children are gone.
        var directories = SafeEnumerate(() => new DirectoryInfo(path).GetDirectories("*", SearchOption.AllDirectories))
            .OrderByDescending(directory => directory.FullName.Length);

        foreach (var directory in directories)
        {
            try
            {
                directory.Delete(recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A directory that still holds a file whose delete failed above. The file is already
                // reported; a second sentence about its parent would only bury it.
            }
        }
    }

    private static FileInfo[] EnumerateFiles(string path) =>
        SafeEnumerate(() => new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories));

    private static T[] SafeEnumerate<T>(Func<T[]> enumerate)
    {
        try
        {
            return enumerate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Keeps this app's own session output from growing without end.
/// </summary>
/// <remarks>
/// A session leaves a journal, two GPU CSVs, a PresentMon CSV and up to six deep captures behind, and the
/// captures are around 900 MB each. An evening is therefore several gigabytes, and nothing has ever
/// removed one — the files are copied to FindTheproblem by hand the morning after and the originals stay.
/// </remarks>
public static class SessionArtifacts
{
    /// <summary>
    /// Deletes everything in the sessions folder last written before the cutoff.
    /// </summary>
    /// <param name="now">
    /// Passed in rather than read, because a retention rule that cannot be tested against a fixed clock
    /// is a retention rule nobody can check.
    /// </param>
    /// <remarks>
    /// Whole files by age, with no regard for which session they belong to: the artefacts of one evening
    /// are written within hours of each other, so an age cutoff keeps or drops them together. Retention
    /// of zero days or less deletes nothing, which is how the setting turns the feature off.
    /// </remarks>
    public static HousekeepingResult Prune(string directory, int retentionDays, DateTimeOffset now)
    {
        if (retentionDays <= 0 || !Directory.Exists(directory))
        {
            return HousekeepingResult.Nothing;
        }

        var cutoff = now.AddDays(-retentionDays).UtcDateTime;
        var deleted = 0;
        long freed = 0;
        var failures = new List<string>();

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory).GetFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HousekeepingResult.Nothing;
        }

        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            var size = file.Length;
            try
            {
                file.Delete();
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{file.Name}: {ex.Message}");
            }
        }

        return new HousekeepingResult(deleted, freed, failures);
    }
}
