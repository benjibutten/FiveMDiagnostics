namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Collectors;

/// <summary>
/// Every comparison in this investigation rested on a remembered setting, and one of them — the window
/// mode — took four sessions to establish from a menu that showed two different answers for the same
/// configuration. It is one integer in a file the game writes.
/// </summary>
/// <remarks>
/// The fixture is the shape of a real install, checked against one: root <c>Settings</c>, a
/// <c>graphics</c> section and a <c>video</c> section, each value carried on a <c>value</c> attribute.
/// </remarks>
public sealed class GameGraphicsSettingsReaderTests
{
    [Fact]
    public void TheSettingsThatCostVramAndDecidePresentationAreRead()
    {
        using var install = new FakeInstall(Fixture(windowed: "2"));

        var described = install.Describe();

        Assert.NotNull(described);
        Assert.Contains("TextureQuality 2", described, StringComparison.Ordinal);
        Assert.Contains("MSAA 2", described, StringComparison.Ordinal);
        Assert.Contains("ReflectionMSAA 8", described, StringComparison.Ordinal);
        Assert.Contains("ScreenWidth 2560", described, StringComparison.Ordinal);
        Assert.Contains("RefreshRate 143", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The value nobody can read off an integer, spelled out. 2 is the borderless fullscreen the machine
    /// turned out to be running, after the in-game menu had shown "Fullscreen" and sometimes
    /// "Fullscreen (Borderless)" for the same configuration.
    /// </summary>
    [Theory]
    [InlineData("0", "exklusiv fullskärm")]
    [InlineData("1", "fönsterläge med ram")]
    [InlineData("2", "fullskärm utan ram")]
    public void TheWindowModeIsSpelledOut(string windowed, string expected)
    {
        using var install = new FakeInstall(Fixture(windowed));

        Assert.Contains(expected, install.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// No file is the ordinary case on a machine without the game, and it must be silent rather than a
    /// warning — the session is exactly as good as it was before.
    /// </summary>
    [Fact]
    public void AMissingFileIsSilent()
    {
        using var install = new FakeInstall(contents: null);

        Assert.Null(install.Describe());
    }

    /// <summary>
    /// A file that exists but is not this format is the same as no file. It must not throw into session
    /// start, and it must not report half a reading.
    /// </summary>
    [Fact]
    public void AFileThatIsNotSettingsXmlIsSilent()
    {
        using var install = new FakeInstall("<Settings><rendering><Foo value=\"1\" /></rendering></Settings>");

        Assert.Null(install.Describe());
    }

    [Fact]
    public void MalformedXmlIsSilent()
    {
        using var install = new FakeInstall("<Settings><graphics><MSAA value=");

        Assert.Null(install.Describe());
    }

    /// <summary>
    /// The failure this replaced. Documents is redirected into OneDrive on this machine, a copy is left
    /// behind on the non-redirected path, and the reader took the first candidate that existed — so the
    /// session recorded settings nobody had used for months, with nothing in the line to show it.
    /// </summary>
    [Fact]
    public void TheNewestCopyWinsRatherThanTheFirstCandidate()
    {
        using var install = new FakeInstall(Fixture(windowed: "0"), name: "stale");
        using var current = new FakeInstall(Fixture(windowed: "2"), name: "onedrive");

        install.SetWritten(new DateTime(2026, 6, 24, 21, 11, 0, DateTimeKind.Utc));
        current.SetWritten(new DateTime(2026, 8, 31, 18, 22, 0, DateTimeKind.Utc));

        // Candidate order deliberately puts the stale copy first, which is what CandidatePaths does.
        var described = GameGraphicsSettingsReader.Describe([install.FilePath, current.FilePath]);

        Assert.NotNull(described);
        Assert.Contains("fullskärm utan ram", described!, StringComparison.Ordinal);
        Assert.DoesNotContain("exklusiv fullskärm", described, StringComparison.Ordinal);
        Assert.Contains("och är äldre", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the timestamp is printed, which is what makes the remaining failure self-evident instead of
    /// silent: a file last written in June cannot describe an evening in August.
    /// </summary>
    [Fact]
    public void TheWriteTimeIsPrintedAndComparedAgainstTheSession()
    {
        using var install = new FakeInstall(Fixture(windowed: "2"));
        install.SetWritten(new DateTime(2026, 8, 24, 19, 11, 0, DateTimeKind.Utc));

        var described = GameGraphicsSettingsReader.Describe(
            [install.FilePath],
            sessionStartUtc: new DateTimeOffset(2026, 8, 31, 18, 23, 0, TimeSpan.Zero));

        Assert.NotNull(described);
        Assert.Contains("senast skriven", described!, StringComparison.Ordinal);
        Assert.Contains("alltså före den här sessionen", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The computer is switched off rather than stopped, so a change has to be noticed while the session
    /// runs. Telemetry either side of it describes two different configurations.
    /// </summary>
    [Fact]
    public void AChangeDuringTheSessionIsReported()
    {
        using var install = new FakeInstall(Fixture(windowed: "2"));
        var monitor = new GameGraphicsSettingsMonitor(
            new DateTimeOffset(2026, 8, 31, 18, 23, 0, TimeSpan.Zero),
            [install.FilePath]);

        Assert.NotNull(monitor.DescribeInitial());
        Assert.Null(monitor.DescribeChange());

        install.Rewrite(Fixture(windowed: "2").Replace("<TextureQuality value=\"2\" />", "<TextureQuality value=\"1\" />"));

        var change = monitor.DescribeChange();

        Assert.NotNull(change);
        Assert.Contains("TextureQuality 2 → 1", change!, StringComparison.Ordinal);
        Assert.Null(monitor.DescribeChange());
    }

    private static string Fixture(string windowed) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <Settings>
           <version value="27" />
           <graphics>
             <ShadowQuality value="2" />
             <ReflectionQuality value="2" />
             <ReflectionMSAA value="8" />
             <AnisotropicFiltering value="16" />
             <MSAA value="2" />
             <TextureQuality value="2" />
             <GrassQuality value="2" />
             <ShaderQuality value="2" />
           </graphics>
           <video>
             <ScreenWidth value="2560" />
             <ScreenHeight value="1440" />
             <RefreshRate value="143" />
             <Windowed value="{windowed}" />
             <VSync value="0" />
             <PauseOnFocusLoss value="1" />
           </video>
         </Settings>
         """;

    /// <summary>
    /// Writes a settings.xml into a temporary folder and points the reader straight at it.
    /// </summary>
    /// <remarks>
    /// The path is injected rather than the environment redirected. The build machine has a real GTA V
    /// install, and an earlier version of this fixture redirected USERPROFILE and still read that
    /// install — because <see cref="Environment.SpecialFolder.MyDocuments"/> is resolved by the shell
    /// and ignores the variable. Three of these tests passed against someone's actual settings without
    /// touching the fixture at all.
    /// </remarks>
    private sealed class FakeInstall : IDisposable
    {
        private readonly string _root;
        private readonly string? _path;

        public FakeInstall(string? contents, string name = "install")
        {
            _root = Path.Combine(Path.GetTempPath(), $"fivemdiag-{name}-" + Guid.NewGuid().ToString("N"));

            if (contents is null)
            {
                _path = Path.Combine(_root, "Rockstar Games", "GTA V", "settings.xml");
                return;
            }

            var directory = Path.Combine(_root, "Rockstar Games", "GTA V");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "settings.xml");
            File.WriteAllText(_path, contents);
        }

        public string FilePath => _path!;

        public string? Describe() => GameGraphicsSettingsReader.Describe([_path!]);

        public void SetWritten(DateTime utc) => File.SetLastWriteTimeUtc(_path!, utc);

        public void Rewrite(string contents)
        {
            File.WriteAllText(_path!, contents);
            File.SetLastWriteTimeUtc(_path!, File.GetLastWriteTimeUtc(_path!).AddMinutes(1));
        }

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
            }
        }
    }
}
