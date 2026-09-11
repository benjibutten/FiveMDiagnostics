using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FiveMDiagnostics.App.Wpf.Updates;

using FiveMDiagnostics.App.Wpf.Properties;
using MessageBox = System.Windows.MessageBox;

/// <summary>
/// The half of the update that runs in the copied executable, outside the install
/// folder. It waits for the real app to exit, swaps the files, and starts the new
/// build. Every file it replaces is backed up first, so a failure partway through
/// puts the previous install back rather than leaving half of one version and half
/// of another.
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>Runs the copied exe as the updater.</summary>
    public const string ApplyArgument = "--apply-update";

    /// <summary>Runs the installed exe as a janitor for a failed update.</summary>
    public const string CleanupArgument = "--cleanup-update";

    /// <summary>Passed to the freshly installed app so it can drop the updater's temp folder.</summary>
    public const string PostInstallCleanupArgument = "--update-cleanup";

    /// <summary>Names the folder under %TEMP% that the updater is allowed to work in.</summary>
    public const string WorkDirectoryPrefix = "FiveMDiagnostics-update-";

    private const int FileOperationAttempts = 20;
    private static readonly TimeSpan FileOperationDelay = TimeSpan.FromMilliseconds(250);

    public static bool IsUpdateMode(string[] args) =>
        args.Contains(ApplyArgument, StringComparer.OrdinalIgnoreCase);

    public static bool IsCleanupMode(string[] args) =>
        args.Contains(CleanupArgument, StringComparer.OrdinalIgnoreCase);

    public static async Task RunCleanupAsync(string[] args)
    {
        try
        {
            var processId = int.Parse(GetRequiredArgument(args, "--process-id"));
            var workDirectory = GetValidatedWorkDirectory(Path.Combine(
                GetRequiredArgument(args, "--work-directory"),
                "update.zip"));

            await Task.Run(() => WaitForProcessToExit(processId)).ConfigureAwait(false);
            await DeleteWorkDirectoryAsync(workDirectory).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best effort and must never start the normal application.
        }
    }

    public static async Task RunAsync(string[] args)
    {
        var progressWindow = new UpdateProgressWindow(owner: null);
        progressWindow.Show();
        var progress = new Progress<UpdateProgress>(progressWindow.Report);

        try
        {
            await Task.Run(() => Apply(args, progress)).ConfigureAwait(true);
        }
        catch (UpdateRollbackException ex)
        {
            // No cleanup here on purpose: the work folder holds the backup, which is
            // now the only copy of the files the failed install replaced. Deleting it
            // would take the user's way back with it.
            MessageBox.Show(
                ex.Message,
                Strings.UpdateTitle,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            // Everything else leaves the previous install intact — either nothing was
            // replaced yet, or the rollback put it all back — so the app is started
            // again. A failed update is no reason to leave the machine without the
            // diagnostics session it had a minute ago.
            MessageBox.Show(
                string.Format(Strings.Culture, Strings.UpdateInstallFailedFormat, ex.Message),
                Strings.UpdateTitle,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            TryRestartInstalledApp(args);
            LaunchCleanupProcess(args);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    /// <summary>One replaced file: where it went, and where its previous copy is kept.</summary>
    internal readonly record struct InstalledFile(string Destination, string? Backup);

    /// <summary>
    /// Replaces the installed files with the staged ones, backing up each file before
    /// it is overwritten. Throws <see cref="UpdateRollbackException"/> if the unwind
    /// after a failure could not put every file back — the caller has to keep the
    /// backup folder in that case, because it holds the only copy of what was there.
    /// </summary>
    internal static void InstallFiles(string stagingDirectory, string installDirectory, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var installedFiles = new List<InstalledFile>();

        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagingDirectory, sourcePath);
                var destinationPath = Path.Combine(installDirectory, relativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (destinationDirectory is not null)
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                string? backupPath = null;
                if (File.Exists(destinationPath))
                {
                    backupPath = Path.Combine(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    Retry(() => File.Copy(destinationPath, backupPath, overwrite: true));
                }

                ReplaceFile(sourcePath, destinationPath);
                installedFiles.Add(new InstalledFile(destinationPath, backupPath));
            }
        }
        catch (Exception installException)
        {
            var unrestoredFiles = RestoreBackups(installedFiles);

            // A rollback that could not finish is not the same failure as one that put
            // everything back: the install is now a mix of two versions, and saying so
            // is the difference between a user who can recover and one who cannot.
            if (unrestoredFiles.Count > 0)
            {
                throw new UpdateRollbackException(backupDirectory, installDirectory, unrestoredFiles, installException);
            }

            throw;
        }
    }

    /// <summary>
    /// Puts the previous install back, newest file first so that one which existed in
    /// both versions ends up as the older one. Every file is attempted even after one
    /// of them fails — stopping at the first would leave more of the install rolled
    /// forward than carrying on does — and the ones that could not be restored are
    /// returned rather than swallowed.
    /// </summary>
    internal static IReadOnlyList<string> RestoreBackups(IReadOnlyList<InstalledFile> installedFiles)
    {
        var unrestoredFiles = new List<string>();

        for (var index = installedFiles.Count - 1; index >= 0; index--)
        {
            var (destinationPath, backupPath) = installedFiles[index];
            try
            {
                if (backupPath is not null)
                {
                    ReplaceFile(backupPath, destinationPath);
                }
                else
                {
                    Retry(() => File.Delete(destinationPath));
                }
            }
            catch
            {
                unrestoredFiles.Add(destinationPath);
            }
        }

        return unrestoredFiles;
    }

    /// <summary>
    /// Called by the freshly started app: the updater that unpacked it is still exiting,
    /// so its temp folder is deleted in the background rather than on the startup path.
    /// </summary>
    public static void ScheduleCleanup(string[] args)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, PostInstallCleanupArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return;
        }

        string workDirectory;
        try
        {
            workDirectory = GetValidatedWorkDirectory(Path.Combine(args[index + 1], "update.zip"));
        }
        catch
        {
            return;
        }

        _ = DeleteWorkDirectoryAsync(workDirectory);
    }

    private static void Apply(string[] args, IProgress<UpdateProgress> progress)
    {
        var processId = int.Parse(GetRequiredArgument(args, "--process-id"));
        var zipPath = Path.GetFullPath(GetRequiredArgument(args, "--zip-path"));
        var expectedHash = GetRequiredArgument(args, "--expected-hash");
        var installDirectory = Path.GetFullPath(GetRequiredArgument(args, "--install-directory"));
        var executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
        var workDirectory = GetValidatedWorkDirectory(zipPath);

        progress.Report(new UpdateProgress(Strings.UpdateStatusWaitingForExit));
        WaitForProcessToExit(processId);

        // Checked a second time, here rather than only before launch: between the two
        // runs the file has been sitting in a world-writable temp folder.
        progress.Report(new UpdateProgress(Strings.UpdateStatusVerifying));
        VerifyArchive(zipPath, expectedHash);

        var stagingDirectory = Path.Combine(workDirectory, "staging");
        var backupDirectory = Path.Combine(workDirectory, "backup");
        progress.Report(new UpdateProgress(Strings.UpdateStatusUnpacking));
        ZipFile.ExtractToDirectory(zipPath, stagingDirectory, overwriteFiles: true);

        var executableName = Path.GetFileName(executablePath);
        if (!File.Exists(Path.Combine(stagingDirectory, executableName)))
        {
            throw new InvalidDataException($"The update archive does not contain {executableName}.");
        }

        progress.Report(new UpdateProgress(Strings.UpdateStatusInstalling));
        InstallFiles(stagingDirectory, installDirectory, backupDirectory);

        progress.Report(new UpdateProgress(Strings.UpdateStatusRestarting));
        var restart = CreateRestartStartInfo(executablePath, installDirectory);
        restart.ArgumentList.Add(PostInstallCleanupArgument);
        restart.ArgumentList.Add(workDirectory);
        _ = Process.Start(restart)
            ?? throw new InvalidOperationException("The updated app could not be started again.");
    }

    private static ProcessStartInfo CreateRestartStartInfo(string executablePath, string installDirectory) =>
        new(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = installDirectory,
        };

    /// <summary>
    /// Puts the previous version back on screen after an update that failed without
    /// damaging the install. Best effort: the failure has already been reported, and a
    /// second one here has nothing left to tell the user that the first did not.
    /// </summary>
    private static void TryRestartInstalledApp(string[] args)
    {
        try
        {
            var executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
            var installDirectory = Path.GetFullPath(GetRequiredArgument(args, "--install-directory"));
            _ = Process.Start(CreateRestartStartInfo(executablePath, installDirectory));
        }
        catch
        {
            // Nothing useful left to do; the failure is already in front of the user.
        }
    }

    /// <summary>
    /// Copies next to the destination and then moves over it. The move is the only step
    /// that touches the live file, so a half-written copy can never end up as the
    /// installed one.
    /// </summary>
    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        var incomingPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.update-{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(sourcePath, incomingPath, overwrite: true);
            Retry(() => File.Move(incomingPath, destinationPath, overwrite: true));
        }
        finally
        {
            try
            {
                File.Delete(incomingPath);
            }
            catch
            {
                // The move already consumed it in the normal case.
            }
        }
    }

    /// <summary>
    /// Windows keeps files locked for a moment after a process exits — antivirus and
    /// Explorer both do it — so a lock is a reason to wait rather than to give up.
    /// </summary>
    private static void Retry(Action operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < FileOperationAttempts)
            {
                Thread.Sleep(FileOperationDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < FileOperationAttempts)
            {
                Thread.Sleep(FileOperationDelay);
            }
        }
    }

    private static void WaitForProcessToExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // It already exited before the updater started waiting.
        }
    }

    private static void VerifyArchive(string zipPath, string expectedHash)
    {
        using var stream = File.OpenRead(zipPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update failed its SHA-256 check.");
        }
    }

    private static string GetRequiredArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"The update argument {name} is missing.");
        }

        return args[index + 1];
    }

    /// <summary>
    /// The arguments arrive on a command line, and the install step deletes and
    /// overwrites whatever they point at. Only a folder this app itself created
    /// directly under %TEMP% is accepted.
    /// </summary>
    private static string GetValidatedWorkDirectory(string zipPath)
    {
        var workDirectory = Path.GetDirectoryName(Path.GetFullPath(zipPath))
            ?? throw new InvalidOperationException("The update's working folder is not valid.");
        var tempDirectory = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var expectedPrefix = Path.Combine(tempDirectory, WorkDirectoryPrefix);

        if (!workDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(workDirectory)?.TrimEnd(Path.DirectorySeparatorChar),
                tempDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update's working folder cannot be trusted.");
        }

        return workDirectory;
    }

    /// <summary>
    /// The failed updater cannot delete the folder it is itself running from, so the
    /// installed app is asked to do it once this process is gone.
    /// </summary>
    private static void LaunchCleanupProcess(string[] args)
    {
        try
        {
            var executablePath = Path.GetFullPath(GetRequiredArgument(args, "--executable-path"));
            var zipPath = Path.GetFullPath(GetRequiredArgument(args, "--zip-path"));
            var workDirectory = GetValidatedWorkDirectory(zipPath);

            var cleanup = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            };
            cleanup.ArgumentList.Add(CleanupArgument);
            cleanup.ArgumentList.Add("--process-id");
            cleanup.ArgumentList.Add(Environment.ProcessId.ToString());
            cleanup.ArgumentList.Add("--work-directory");
            cleanup.ArgumentList.Add(workDirectory);
            _ = Process.Start(cleanup);
        }
        catch
        {
            // The update failure has already been reported; cleanup is best effort.
        }
    }

    private static async Task DeleteWorkDirectoryAsync(string workDirectory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            try
            {
                Directory.Delete(workDirectory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch
            {
                // Still locked by the exiting updater; try again.
            }
        }
    }
}
