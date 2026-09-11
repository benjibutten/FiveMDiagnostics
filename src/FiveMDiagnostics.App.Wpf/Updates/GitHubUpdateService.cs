using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace FiveMDiagnostics.App.Wpf.Updates;

using FiveMDiagnostics.App.Wpf.Properties;

internal sealed record UpdateInfo(
    Version Version,
    string TagName,
    Uri DownloadUri,
    Uri ChecksumUri);

internal sealed record UpdateProgress(string Status, double? Percentage = null);

/// <summary>
/// Asks GitHub whether a newer release exists and, when the user says yes, hands the
/// work over to a copy of this executable running in a temp folder. The running app
/// cannot overwrite its own files while it is running, so the copy is what waits for
/// this process to exit and swaps the install.
/// </summary>
internal sealed class GitHubUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/benjibutten/FiveMDiagnostics/releases/latest";

    /// <summary>How long an automatic check waits before hitting GitHub again.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly string _statePath;

    public GitHubUpdateService(HttpClient? httpClient = null, string? statePath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FiveMDiagnostics-Updater/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FiveMDiagnostics",
            "update-check.txt");
    }

    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, bool force, CancellationToken cancellationToken = default)
    {
        if (!force && !IsCheckDue())
        {
            return null;
        }

        using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        SaveCheckTime();

        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tagName, out var releaseVersion) || releaseVersion <= currentVersion)
        {
            return null;
        }

        var zipName = GetReleaseZipName(releaseVersion!);
        var checksumName = $"{zipName}.sha256";
        Uri? zipUri = null;
        Uri? checksumUri = null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (string.Equals(name, zipName, StringComparison.OrdinalIgnoreCase))
            {
                zipUri = uri;
            }
            else if (string.Equals(name, checksumName, StringComparison.OrdinalIgnoreCase))
            {
                checksumUri = uri;
            }
        }

        // A release without both assets is not installable. Saying "no update" is the
        // honest answer: the alternative is offering an update that would fail halfway
        // through, or one that cannot be verified before it is installed.
        if (zipUri is null || checksumUri is null)
        {
            return null;
        }

        return new UpdateInfo(releaseVersion!, tagName, zipUri, checksumUri);
    }

    public async Task LaunchInstallerAsync(
        UpdateInfo update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), $"{UpdateInstaller.WorkDirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var zipPath = Path.Combine(workDirectory, "update.zip");
        var updaterPath = Path.Combine(workDirectory, "FiveMDiagnostics.Update.exe");

        try
        {
            progress?.Report(new UpdateProgress(Strings.UpdateStatusDownloading, 0));
            await DownloadFileAsync(update.DownloadUri, zipPath, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new UpdateProgress(Strings.UpdateStatusVerifying));
            var checksumText = await _httpClient.GetStringAsync(update.ChecksumUri, cancellationToken).ConfigureAwait(false);
            var expectedHash = ParseChecksum(checksumText);
            await using (var zipStream = File.OpenRead(zipPath))
            {
                await VerifySha256Async(zipStream, expectedHash, cancellationToken).ConfigureAwait(false);
            }

            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine where the running executable lives.");
            var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            // Installs under Program Files need an elevated updater. Asking for it only
            // when the probe says the folder is read-only keeps the common case — the
            // exe sitting in a folder in the user's profile — free of UAC prompts.
            var requiresElevation = !CanWriteToDirectory(installDirectory);

            progress?.Report(new UpdateProgress(Strings.UpdateStatusPreparingInstall));
            File.Copy(executablePath, updaterPath);

            var startInfo = new ProcessStartInfo(updaterPath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDirectory,
            };
            if (requiresElevation)
            {
                startInfo.Verb = "runas";
            }

            startInfo.ArgumentList.Add(UpdateInstaller.ApplyArgument);
            startInfo.ArgumentList.Add("--process-id");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--zip-path");
            startInfo.ArgumentList.Add(zipPath);
            startInfo.ArgumentList.Add("--expected-hash");
            startInfo.ArgumentList.Add(expectedHash);
            startInfo.ArgumentList.Add("--install-directory");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--executable-path");
            startInfo.ArgumentList.Add(executablePath);

            progress?.Report(new UpdateProgress(
                requiresElevation ? Strings.UpdateStatusWaitingForApproval : Strings.UpdateStatusStartingInstall));
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the updater.");
        }
        catch
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch
            {
                // A leftover folder in %TEMP% is not worth masking the real failure.
            }

            throw;
        }
    }

    internal static bool TryParseVersion(string tagName, out Version? version) =>
        Version.TryParse(tagName.Trim().TrimStart('v', 'V'), out version);

    /// <summary>Must match the asset name produced by .github/workflows/release.yml.</summary>
    internal static string GetReleaseZipName(Version version) =>
        $"FiveMDiagnostics-{version}-win-x64.zip";

    internal static string ParseChecksum(string value)
    {
        var hash = value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? string.Empty;
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException("The release's checksum file is not valid.");
        }

        return hash.ToUpperInvariant();
    }

    internal static async Task VerifySha256Async(
        Stream stream,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded update failed its SHA-256 check.");
        }
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string path,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(path);

        var totalBytes = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long downloadedBytes = 0;
        var lastReportedPercentage = -1;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            downloadedBytes += bytesRead;

            if (totalBytes > 0)
            {
                var percentage = (int)Math.Min(100, downloadedBytes * 100 / totalBytes.Value);
                if (percentage != lastReportedPercentage)
                {
                    lastReportedPercentage = percentage;
                    progress?.Report(new UpdateProgress(Strings.UpdateStatusDownloading, percentage));
                }
            }
        }
    }

    private bool IsCheckDue()
    {
        try
        {
            return !File.Exists(_statePath)
                || !DateTimeOffset.TryParse(File.ReadAllText(_statePath), out var lastCheck)
                || DateTimeOffset.UtcNow - lastCheck >= CheckInterval;
        }
        catch
        {
            return true;
        }
    }

    private void SaveCheckTime()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Losing the timestamp only costs one extra request next launch.
        }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch
            {
                // DeleteOnClose already removed it in the normal case.
            }
        }
    }
}
