namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.PresentMon;

public sealed class PresentMonLocatorTests
{
    [Fact]
    public void Discover_UsesConfiguredPath_WhenItExists()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var configuredPath = Path.Combine(tempRoot, "PresentMon.exe");
            File.WriteAllText(configuredPath, string.Empty);

            var result = PresentMonLocator.Discover(configuredPath, pathEnvironmentVariable: null, additionalSearchPaths: []);

            Assert.Equal(PresentMonDiscoveryKind.Configured, result.Kind);
            Assert.Equal(configuredPath, result.ExecutablePath);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Discover_FallsBackToPathLookup_WhenConfiguredPathIsMissing()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var pathDirectory = Path.Combine(tempRoot, "tools");
            Directory.CreateDirectory(pathDirectory);
            var detectedPath = Path.Combine(pathDirectory, "PresentMon.exe");
            File.WriteAllText(detectedPath, string.Empty);

            var result = PresentMonLocator.Discover(
                configuredPath: Path.Combine(tempRoot, "missing", "PresentMon.exe"),
                pathEnvironmentVariable: pathDirectory,
                additionalSearchPaths: []);

            Assert.Equal(PresentMonDiscoveryKind.AutoDetected, result.Kind);
            Assert.Equal(detectedPath, result.ExecutablePath);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}