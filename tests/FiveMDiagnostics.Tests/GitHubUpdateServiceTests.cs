using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FiveMDiagnostics.App.Wpf.Updates;

namespace FiveMDiagnostics.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.42", 1, 0, 42)]
    [InlineData("1.2.3", 1, 2, 3)]
    public void TryParseVersion_ParsesReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void GetReleaseZipName_MatchesTheNameTheReleaseWorkflowUploads()
    {
        var result = GitHubUpdateService.GetReleaseZipName(new Version(1, 0, 42));

        Assert.Equal("FiveMDiagnostics-1.0.42-win-x64.zip", result);
    }

    [Fact]
    public void ParseChecksum_AcceptsStandardSha256File()
    {
        var hash = new string('a', 64);

        var result = GitHubUpdateService.ParseChecksum($"{hash}  FiveMDiagnostics-1.0.42-win-x64.zip");

        Assert.Equal(hash.ToUpperInvariant(), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")]
    public void ParseChecksum_RejectsInvalidContent(string value)
    {
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ParseChecksum(value));
    }

    [Fact]
    public async Task CheckAsync_UsesLatestReleaseAndSelectsMatchingAssets()
    {
        const string zipUrl = "https://github.com/benjibutten/FiveMDiagnostics/releases/download/v1.0.42/FiveMDiagnostics-1.0.42-win-x64.zip";
        const string checksumUrl = $"{zipUrl}.sha256";
        string? requestedUrl = null;
        var json = $$"""
            {
              "tag_name": "v1.0.42",
              "html_url": "https://github.com/benjibutten/FiveMDiagnostics/releases/tag/v1.0.42",
              "assets": [
                { "name": "FiveMDiagnostics-1.0.41-win-x64.zip", "browser_download_url": "https://example.test/wrong.zip" },
                { "name": "FiveMDiagnostics-1.0.42-win-x64.zip", "browser_download_url": "{{zipUrl}}" },
                { "name": "FiveMDiagnostics-1.0.42-win-x64.zip.sha256", "browser_download_url": "{{checksumUrl}}" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestedUrl = request.RequestUri?.AbsoluteUri;
            return Json(json);
        }));
        var stateRoot = CreateStateRoot();

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));
            var update = await service.CheckAsync(new Version(1, 0, 41), force: true);

            Assert.Equal("https://api.github.com/repos/benjibutten/FiveMDiagnostics/releases/latest", requestedUrl);
            Assert.NotNull(update);
            Assert.Equal(new Uri(zipUrl), update!.DownloadUri);
            Assert.Equal(new Uri(checksumUrl), update.ChecksumUri);
        }
        finally
        {
            Delete(stateRoot);
        }
    }

    [Fact]
    public async Task CheckAsync_IgnoresReleaseThatIsNotNewerThanTheInstalledBuild()
    {
        const string json = """
            {
              "tag_name": "v1.0.42",
              "html_url": "https://github.com/benjibutten/FiveMDiagnostics/releases/tag/v1.0.42",
              "assets": [
                { "name": "FiveMDiagnostics-1.0.42-win-x64.zip", "browser_download_url": "https://example.test/app.zip" },
                { "name": "FiveMDiagnostics-1.0.42-win-x64.zip.sha256", "browser_download_url": "https://example.test/app.zip.sha256" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(_ => Json(json)));
        var stateRoot = CreateStateRoot();

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));

            // The installed build is the stamped four-part version of the same release.
            Assert.Null(await service.CheckAsync(new Version(1, 0, 42, 0), force: true));
        }
        finally
        {
            Delete(stateRoot);
        }
    }

    /// <summary>
    /// The one case where offering an update would be worse than staying quiet: a
    /// release the app has no verified way to install.
    /// </summary>
    [Fact]
    public async Task CheckAsync_IgnoresReleaseWithoutChecksumAsset()
    {
        const string json = """
            {
              "tag_name": "v1.0.42",
              "html_url": "https://github.com/benjibutten/FiveMDiagnostics/releases/tag/v1.0.42",
              "assets": [
                { "name": "FiveMDiagnostics-1.0.42-win-x64.zip", "browser_download_url": "https://example.test/app.zip" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(_ => Json(json)));
        var stateRoot = CreateStateRoot();

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));

            Assert.Null(await service.CheckAsync(new Version(1, 0, 41), force: true));
        }
        finally
        {
            Delete(stateRoot);
        }
    }

    /// <summary>
    /// Without the interval the app would ask GitHub on every launch, which for an app
    /// that is started once per play session is a request per session for no new
    /// information.
    /// </summary>
    [Fact]
    public async Task CheckAsync_SkipsTheNetworkWhenAnAutomaticCheckIsNotDueYet()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return Json("""{ "tag_name": "v1.0.42", "html_url": "https://example.test", "assets": [] }""");
        }));
        var stateRoot = CreateStateRoot();

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));

            await service.CheckAsync(new Version(1, 0, 41), force: true);
            await service.CheckAsync(new Version(1, 0, 41), force: false);

            Assert.Equal(1, requestCount);
        }
        finally
        {
            Delete(stateRoot);
        }
    }

    [Fact]
    public async Task VerifySha256Async_AcceptsMatchAndRejectsMismatch()
    {
        var download = Encoding.UTF8.GetBytes("FiveM Diagnostics release archive");
        var expectedHash = Convert.ToHexString(SHA256.HashData(download));

        await GitHubUpdateService.VerifySha256Async(new MemoryStream(download), expectedHash);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GitHubUpdateService.VerifySha256Async(new MemoryStream(download), new string('0', 64)));
    }

    [Fact]
    public void InstallFiles_UpdatesReleaseFilesAndPreservesOtherFiles()
    {
        var root = CreateInstallRoot();
        var staging = Path.Combine(root, "staging");
        var install = Path.Combine(root, "install");
        var backup = Path.Combine(root, "backup");

        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(staging, "FiveMDiagnostics.exe"), "new version");
            File.WriteAllText(Path.Combine(install, "FiveMDiagnostics.exe"), "old version");
            File.WriteAllText(Path.Combine(install, "settings.json"), "keep me");

            UpdateInstaller.InstallFiles(staging, install, backup);

            Assert.Equal("new version", File.ReadAllText(Path.Combine(install, "FiveMDiagnostics.exe")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "settings.json")));
            Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "FiveMDiagnostics.exe")));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The staged tree here cannot be installed: "data" is a file in the install
    /// folder and a directory in the update, so creating it throws partway through.
    /// What matters is what the install looks like afterwards.
    /// </summary>
    [Fact]
    public void InstallFiles_PutsThePreviousVersionBackWhenTheInstallFailsPartway()
    {
        var root = CreateInstallRoot();
        var staging = Path.Combine(root, "staging");
        var install = Path.Combine(root, "install");
        var backup = Path.Combine(root, "backup");

        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(Path.Combine(staging, "data"));
            File.WriteAllText(Path.Combine(staging, "FiveMDiagnostics.exe"), "new version");
            File.WriteAllText(Path.Combine(staging, "data", "blocked.txt"), "cannot land");
            File.WriteAllText(Path.Combine(install, "FiveMDiagnostics.exe"), "old version");
            File.WriteAllText(Path.Combine(install, "data"), "a file, not a folder");

            Assert.ThrowsAny<Exception>(() => UpdateInstaller.InstallFiles(staging, install, backup));

            // The exe was replaced before the failure — the backup proves it — and then
            // put back, so the half-installed new version is gone rather than left next
            // to the old one.
            Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "FiveMDiagnostics.exe")));
            Assert.Equal("old version", File.ReadAllText(Path.Combine(install, "FiveMDiagnostics.exe")));
            Assert.Empty(Directory.GetFiles(install, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void RestoreBackups_ReportsNothingWhenEveryFileGoesBack()
    {
        var root = CreateInstallRoot();

        try
        {
            Directory.CreateDirectory(root);
            var replaced = Path.Combine(root, "FiveMDiagnostics.exe");
            var backup = Path.Combine(root, "FiveMDiagnostics.exe.backup");
            var added = Path.Combine(root, "added.txt");
            File.WriteAllText(replaced, "new version");
            File.WriteAllText(backup, "old version");
            File.WriteAllText(added, "did not exist before");

            var unrestored = UpdateInstaller.RestoreBackups(
            [
                new UpdateInstaller.InstalledFile(replaced, backup),
                new UpdateInstaller.InstalledFile(added, null),
            ]);

            Assert.Empty(unrestored);
            Assert.Equal("old version", File.ReadAllText(replaced));

            // A file the update added has no previous version, so putting things back
            // means removing it.
            Assert.False(File.Exists(added));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The case that decides whether the user can recover: if a file cannot be put
    /// back, the caller has to hear about it by name — silence there is what turns a
    /// failed update into a broken install with its backup deleted behind it.
    /// </summary>
    [Fact]
    public void RestoreBackups_ReportsTheFilesItCouldNotPutBack()
    {
        var root = CreateInstallRoot();

        try
        {
            Directory.CreateDirectory(root);
            var restorable = Path.Combine(root, "FiveMDiagnostics.exe");
            var restorableBackup = Path.Combine(root, "FiveMDiagnostics.exe.backup");
            var broken = Path.Combine(root, "FiveMDiagnostics.dll");
            File.WriteAllText(restorable, "new version");
            File.WriteAllText(restorableBackup, "old version");
            File.WriteAllText(broken, "new version");

            var unrestored = UpdateInstaller.RestoreBackups(
            [
                new UpdateInstaller.InstalledFile(restorable, restorableBackup),
                new UpdateInstaller.InstalledFile(broken, Path.Combine(root, "gone.backup")),
            ]);

            Assert.Equal([broken], unrestored);

            // The one that could be restored still was: a file it cannot save is no
            // reason to abandon the rest.
            Assert.Equal("old version", File.ReadAllText(restorable));
        }
        finally
        {
            Delete(root);
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string CreateStateRoot() =>
        Path.Combine(Path.GetTempPath(), $"FiveMDiagnostics-update-check-test-{Guid.NewGuid():N}");

    private static string CreateInstallRoot() =>
        Path.Combine(Path.GetTempPath(), $"FiveMDiagnostics-update-test-{Guid.NewGuid():N}");

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
