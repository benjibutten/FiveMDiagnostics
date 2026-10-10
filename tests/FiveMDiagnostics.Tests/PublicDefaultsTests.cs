namespace FiveMDiagnostics.Tests;

using System.Text.Json;

using FiveMDiagnostics.App.Wpf.Services;
using FiveMDiagnostics.Core;

/// <summary>
/// A new install starts with defaults meant for anyone, while a settings file written before those
/// settings existed keeps behaving as it did.
/// </summary>
public sealed class PublicDefaultsTests
{
    [Fact]
    public void ANewInstallClosesNothingPassesNoArgumentsAndMarksWithF9()
    {
        var settings = DiagnosticsSettings.CreateDefault();

        Assert.Empty(settings.PreLaunchApps!);
        Assert.Equal(string.Empty, settings.FiveMLaunchArguments);
        Assert.Equal(string.Empty, settings.Hotkeys.Stutter);
        Assert.Equal("F9", settings.Hotkeys.Severe);
        Assert.Null(settings.Streams);
    }

    [Fact]
    public void AnOlderSettingsFileKeepsItsLaunchArgumentsHotkeysAndStream()
    {
        var settings = JsonSerializer.Deserialize<DiagnosticsSettings>("""{ "Language": "sv", "SessionRetentionDays": 3 }""")!;

        Assert.Null(settings.PreLaunchClose);
        Assert.Equal("-pure_1", settings.FiveMLaunchArguments);
        Assert.Equal("F13", settings.Hotkeys.Stutter);
        Assert.Equal("F14", settings.Hotkeys.Severe);
        Assert.True(settings.MeasuresStream);
        Assert.Equal(3, settings.SessionRetentionDays);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F9", 0x2u | 0x4u, 0x78u)]
    [InlineData("F13", 0u, 0x7Cu)]
    [InlineData(" alt+F10 ", 0x1u, 0x79u)]
    public void HotkeysAreReadAsGestures(string text, uint modifiers, uint virtualKey)
    {
        Assert.True(GlobalHotkeyService.TryParse(text, out var parsedModifiers, out var parsedKey));
        Assert.Equal(modifiers, parsedModifiers);
        Assert.Equal(virtualKey, parsedKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("banana")]
    [InlineData("Ctrl+")]
    public void TextThatIsNotAKeyIsRefused(string text)
    {
        Assert.False(GlobalHotkeyService.TryParse(text, out _, out _));
    }
}
