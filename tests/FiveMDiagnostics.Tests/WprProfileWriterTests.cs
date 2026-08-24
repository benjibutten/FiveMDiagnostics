namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;
using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// The profile decides how many seconds of run-up a marker can save, and every stack keyword in it is
/// paid for out of that budget. These tests exist because both mistakes are silent: a profile that
/// collects too much still produces a valid trace, just a shorter one, and a profile missing a keyword
/// still produces a trace, just one that cannot answer the question.
/// </summary>
public sealed class WprProfileWriterTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(
        Path.GetTempPath(),
        "FiveMDiagnosticsTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The new capture is specifically meant to explain who put the game thread into a long wait, so
    /// scheduler events and the call stacks that initiated them both have to be present.
    /// </summary>
    [Fact]
    public void ProfileCollectsContextSwitchEventsAndStacksByDefault()
    {
        var profile = Write(new DiagnosticsSettings());

        Assert.Contains("<Keyword Value=\"CSwitch\" />", profile, StringComparison.Ordinal);
        Assert.Contains("<Keyword Value=\"ReadyThread\" />", profile, StringComparison.Ordinal);
        Assert.Contains("<Stack Value=\"CSwitch\" />", profile, StringComparison.Ordinal);
        Assert.Contains("<Stack Value=\"ReadyThread\" />", profile, StringComparison.Ordinal);
        Assert.Contains("<Stack Value=\"SampledProfile\" />", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextSwitchStacksCanBeDisabledWhenRetentionIsMoreImportant()
    {
        var settings = new DiagnosticsSettings();
        settings.DeepCapture.CollectContextSwitchStacks = false;

        var profile = Write(settings);

        Assert.Contains("<Keyword Value=\"CSwitch\" />", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("<Stack Value=\"CSwitch\" />", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("<Stack Value=\"ReadyThread\" />", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyCaptureDefaultsAreUpgradedOnce()
    {
        var options = new DeepCaptureOptions
        {
            RingBufferMegabytes = 256,
            PostMarkerTail = TimeSpan.FromSeconds(5),
            CollectContextSwitchStacks = false,
        };

        Assert.True(options.MigrateCaptureProfile());
        Assert.Equal(768, options.RingBufferMegabytes);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PostMarkerTail);
        Assert.True(options.CollectContextSwitchStacks);
        Assert.False(options.MigrateCaptureProfile());
    }

    /// <summary>
    /// File stacks are the measured 9% surcharge nothing in the app reads yet, so they must stay off
    /// until someone asks for them.
    /// </summary>
    [Fact]
    public void FileStacksAreOffByDefaultAndOnWhenRequested()
    {
        var settings = new DiagnosticsSettings();

        Assert.DoesNotContain("<Stack Value=\"FileCreate\" />", Write(settings), StringComparison.Ordinal);

        settings.DeepCapture.CollectFileStacks = true;
        Assert.Contains("<Stack Value=\"FileCreate\" />", Write(settings), StringComparison.Ordinal);
    }

    /// <summary>Memory logging sizes its ring from the profile, so the setting has to reach the XML.</summary>
    [Fact]
    public void RingBufferSizeFollowsTheSetting()
    {
        var settings = new DiagnosticsSettings();
        settings.DeepCapture.RingBufferMegabytes = 512;

        Assert.Contains("<Buffers Value=\"512\" />", Write(settings), StringComparison.Ordinal);
    }

    /// <summary>
    /// The default has to reach the profile too, and it has to buy enough history to survive human
    /// reaction time. A marker pressed six or seven seconds after a hitch — which is how long it takes to
    /// feel one and reach the key — must still have the hitch in the buffer. The capture that prompted
    /// this reached back 5.9 seconds and missed its own stall by 0.4.
    /// </summary>
    [Fact]
    public void TheDefaultRingBufferOutlastsHumanReactionTime()
    {
        var settings = new DiagnosticsSettings();
        settings.DeepCapture.Normalize();

        Assert.Contains($"<Buffers Value=\"{settings.DeepCapture.RingBufferMegabytes}\" />", Write(settings), StringComparison.Ordinal);

        // The tail keeps recording, so it comes out of the same buffer as the run-up.
        var runUpSeconds = settings.DeepCapture.EstimatedRingBufferSeconds - settings.DeepCapture.PostMarkerTail.TotalSeconds;
        Assert.True(runUpSeconds > 15, $"only {runUpSeconds:F0}s of run-up survives the tail");
    }

    private string Write(DiagnosticsSettings settings)
    {
        settings.WorkingDirectory = _workingDirectory;

        var path = WprProfileWriter.TryWrite(settings, out var error);

        Assert.Null(error);
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }
}
