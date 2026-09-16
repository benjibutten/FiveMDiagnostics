namespace FiveMDiagnostics.Tests;

using System.IO;
using FiveMDiagnostics.App.Wpf.Services;

/// <summary>
/// Both of these delete files the user cannot get back, so the cases that matter are the ones where
/// something must survive.
/// </summary>
public sealed class HousekeepingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory left behind is not a failed test.
        }
    }

    private string WriteFile(string relativePath, int bytes, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    // ---- FiveMCache ------------------------------------------------------------------------

    /// <summary>
    /// The three folders are the whole of it. game-storage carries the Rockstar login and nui-storage
    /// each server's saved UI state, and an evening that starts with a login screen is not a cache clear.
    /// </summary>
    [Fact]
    public void OnlyTheThreeCacheFoldersAreCleared()
    {
        WriteFile(@"cache\a.bin", 100, Now);
        WriteFile(@"server-cache\b.bin", 200, Now);
        WriteFile(@"server-cache-priv\c.bin", 300, Now);
        var login = WriteFile(@"game-storage\ros.dat", 400, Now);
        var uiState = WriteFile(@"nui-storage\state.ldb", 500, Now);

        var result = FiveMCache.Clear(_root);

        Assert.Equal(3, result.FilesDeleted);
        Assert.Equal(600, result.BytesFreed);
        Assert.False(result.AnythingFailed);

        Assert.True(File.Exists(login));
        Assert.True(File.Exists(uiState));
    }

    [Fact]
    public void ClearingReachesIntoSubfoldersAndRemovesThem()
    {
        WriteFile(@"cache\files\deep\resource.rpf", 64, Now);

        var result = FiveMCache.Clear(_root);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "cache")));

        // The folder itself stays. FiveM recreates a missing one, but a clear that removes the directory
        // makes a half-finished delete look like an uninstall.
        Assert.True(Directory.Exists(Path.Combine(_root, "cache")));
    }

    [Fact]
    public void FoldersReportsWhatIsThereAndSkipsWhatIsNot()
    {
        WriteFile(@"cache\a.bin", 1024, Now);
        WriteFile(@"cache\b.bin", 1024, Now);

        var folders = FiveMCache.Folders(_root);

        var cache = Assert.Single(folders);
        Assert.Equal("cache", cache.Name);
        Assert.Equal(2048, cache.Bytes);
        Assert.Equal(2, cache.Files);
    }

    /// <summary>
    /// A user with no FiveM installed clicks the button too. Nothing to clear is an empty result, not a
    /// crash and not a directory created on the way past.
    /// </summary>
    [Fact]
    public void AMissingInstallClearsNothing()
    {
        var missing = Path.Combine(_root, "not-installed");

        var result = FiveMCache.Clear(missing);

        Assert.Equal(0, result.FilesDeleted);
        Assert.False(result.AnythingFailed);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void AFileHeldOpenIsReportedRatherThanThrown()
    {
        var locked = WriteFile(@"cache\locked.bin", 128, Now);
        WriteFile(@"cache\free.bin", 128, Now);

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = FiveMCache.Clear(_root);

            // The one that could go, went.
            Assert.Equal(1, result.FilesDeleted);
            Assert.True(result.AnythingFailed);
            Assert.Contains("locked.bin", Assert.Single(result.Failures), StringComparison.Ordinal);
        }
    }

    // ---- SessionArtifacts ------------------------------------------------------------------

    /// <summary>
    /// Three days, counted from the file's own last write. The evening's files are copied to
    /// FindTheproblem the morning after, so anything older has already been taken.
    /// </summary>
    [Fact]
    public void OnlyFilesPastTheRetentionAreDeleted()
    {
        var old = WriteFile("deep_old.etl", 900, Now.AddDays(-4));
        var justOld = WriteFile("session_old.jsonl", 100, Now.AddDays(-3).AddMinutes(-1));
        var recent = WriteFile("session_recent.jsonl", 100, Now.AddDays(-2));
        var today = WriteFile("presentmon_today.csv", 100, Now);

        var result = SessionArtifacts.Prune(_root, retentionDays: 3, Now);

        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(1000, result.BytesFreed);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(justOld));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(today));
    }

    /// <summary>Zero is the off switch, and it has to be, or the setting has no way to say "keep all".</summary>
    [Fact]
    public void RetentionOfZeroDeletesNothing()
    {
        var ancient = WriteFile("deep_ancient.etl", 900, Now.AddYears(-1));

        var result = SessionArtifacts.Prune(_root, retentionDays: 0, Now);

        Assert.Equal(0, result.FilesDeleted);
        Assert.True(File.Exists(ancient));
    }

    /// <summary>
    /// A negative day count is a typo in settings.json, and reading it as "delete everything" would empty
    /// the folder the investigation's own material sits in.
    /// </summary>
    [Fact]
    public void ANegativeRetentionDeletesNothing()
    {
        var ancient = WriteFile("deep_ancient.etl", 900, Now.AddYears(-1));

        Assert.Equal(0, SessionArtifacts.Prune(_root, retentionDays: -7, Now).FilesDeleted);
        Assert.True(File.Exists(ancient));
    }

    /// <summary>
    /// Only the sessions folder itself. Exports and Artifacts are siblings of it under the same root, and
    /// a prune that recursed would take an export the user saved deliberately.
    /// </summary>
    [Fact]
    public void PruningDoesNotRecurseIntoSubfolders()
    {
        var kept = WriteFile(@"Exports\bundle.zip", 100, Now.AddDays(-30));
        WriteFile("session_old.jsonl", 100, Now.AddDays(-30));

        var result = SessionArtifacts.Prune(_root, retentionDays: 3, Now);

        Assert.Equal(1, result.FilesDeleted);
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void AMissingSessionsFolderPrunesNothing()
    {
        var result = SessionArtifacts.Prune(Path.Combine(_root, "gone"), retentionDays: 3, Now);

        Assert.Equal(0, result.FilesDeleted);
        Assert.False(result.AnythingFailed);
    }
}
