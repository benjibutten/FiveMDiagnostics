namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.App.Wpf.Services;
using FiveMDiagnostics.Core;

/// <summary>
/// The pre-launch list is the player's own, built from what they add and from what the previous session
/// measured; a settings file from before the list could be edited keeps the ticks it had.
/// </summary>
public sealed class PreLaunchListTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);
    private const ulong Megabyte = 1024UL * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AnOlderSettingsFileKeepsItsTicksAsItsOwnList()
    {
        var settings = new DiagnosticsSettings { PreLaunchClose = ["Steam", "Spotify"] };

        Assert.True(PreLaunch.MigrateList(settings));

        var list = settings.PreLaunchApps!;
        Assert.Equal(PreLaunch.StarterApps.Count, list.Count);
        Assert.True(list.Single(app => app.Name == "Steam").Close);
        Assert.True(list.Single(app => app.Name == "Spotify").Close);
        Assert.False(list.Single(app => app.Name == "OneDrive").Close);
        Assert.Equal(["steam", "steamwebhelper", "steamservice"], list.Single(app => app.Name == "Steam").ProcessNames);
        Assert.Null(settings.PreLaunchClose);
    }

    [Fact]
    public void AnOlderSettingsFileThatNeverChoseGetsTheStarterTicks()
    {
        var settings = new DiagnosticsSettings();

        PreLaunch.MigrateList(settings);

        Assert.True(settings.PreLaunchApps!.Single(app => app.Name == "Steam").Close);
        Assert.False(settings.PreLaunchApps!.Single(app => app.Name == "Chrome").Close);
    }

    [Fact]
    public void AnOwnListIsLeftAlone()
    {
        var settings = DiagnosticsSettings.CreateDefault();

        Assert.False(PreLaunch.MigrateList(settings));
        Assert.Empty(settings.PreLaunchApps!);
    }

    [Theory]
    [InlineData("Spotify", true)]
    [InlineData("chrome", true)]
    [InlineData("FiveM_b3407_GTAProcess", false)]
    [InlineData("FiveM_ChromeBrowser", false)]
    [InlineData("dwm", false)]
    [InlineData("explorer", false)]
    [InlineData("PresentMon-2.4.1-x64", false)]
    [InlineData("FiveMDiagnostics", false)]
    public void OnlyProgramsThePlayerCouldCloseAreCandidates(string processName, bool candidate)
    {
        Assert.Equal(candidate, BackgroundProgram.IsCandidate(processName));
    }

    [Fact]
    public void TheTallyKeepsPeakVramPerNameAndAverageCpuOverTheSession()
    {
        var tally = new HeavyProcessTally();

        // Two chrome processes are one program; the game and the compositor are never suggestions.
        tally.Observe(Vram(("chrome", 200), ("chrome", 250), ("FiveM_b3407_GTAProcess", 6000), ("dwm", 900)));
        tally.Observe(Vram(("chrome", 100), ("Spotify", 120)));

        // Spotify busy in one sample of four: an average of 1.5 %, not its 6 % peak.
        tally.Observe(Cpu(("Spotify", 6)));
        tally.Observe(Cpu());
        tally.Observe(Cpu(("OneDrive", 4), ("OneDrive", 4)));
        tally.Observe(Cpu(("OneDrive", 4)));

        var results = tally.Results().ToDictionary(process => process.ProcessName);

        Assert.Equal(450, results["chrome"].PeakVramMegabytes, precision: 0);
        Assert.Equal(1.5, results["Spotify"].AverageCpuPercent, precision: 2);
        Assert.Equal(3, results["OneDrive"].AverageCpuPercent, precision: 2);
        Assert.DoesNotContain("FiveM_b3407_GTAProcess", results.Keys);
        Assert.DoesNotContain("dwm", results.Keys);

        Assert.True(results["chrome"].IsWorthClosing);
        Assert.True(results["OneDrive"].IsWorthClosing);
        Assert.False(results["Spotify"].IsWorthClosing);
    }

    [Fact]
    public void TheTallySurvivesTheTripToTheNextSession()
    {
        HeavyProcessTally.Save(_directory, [new HeavyProcess("chrome", 812, 2.25)]);

        var loaded = Assert.Single(HeavyProcessTally.TryLoad(_directory));

        Assert.Equal("chrome", loaded.ProcessName);
        Assert.Equal(812, loaded.PeakVramMegabytes);
        Assert.Equal(2.25, loaded.AverageCpuPercent);
    }

    [Fact]
    public void NoEarlierSessionMeansNoSuggestions()
    {
        Assert.Empty(HeavyProcessTally.TryLoad(_directory));
    }

    private static GpuProcessMemorySample Vram(params (string Name, double Megabytes)[] processes) =>
        new(
            At,
            IsAvailable: true,
            processes.Select((process, index) => new GpuProcessMemoryUsage(1000 + index, process.Name, (ulong)(process.Megabytes * Megabyte), 0, 1)).ToArray());

    private static SystemTelemetrySample Cpu(params (string Name, double Percent)[] processes) =>
        new(
            At,
            TotalCpuUsagePercent: 30,
            PerCoreUsagePercent: new Dictionary<string, double>(),
            MemoryCommitPercent: 50,
            AvailableMemoryMb: 8000,
            processes.Select((process, index) => new ProcessActivity(process.Name, 2000 + index, process.Percent, 0)).ToArray(),
            []);
}
