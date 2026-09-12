namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// Settings are a JSON file the user can edit, and degenerate auto-detect values are not merely
/// useless: a zero cooldown or a spike multiplier of 0 makes almost every frame a trigger, and every
/// trigger snapshots a 90 second window, writes a status entry and refreshes the UI.
/// </summary>
public sealed class AutoDetectOptionsTests
{
    [Fact]
    public void Normalize_LeavesDefaultsAlone()
    {
        var options = new AutoDetectOptions();

        Assert.False(options.Normalize());
        Assert.Equal(new AutoDetectOptions(), options);
    }

    [Fact]
    public void Normalize_RejectsThresholdsThatWouldFireOnEveryFrame()
    {
        var options = new AutoDetectOptions
        {
            SpikeMultiplier = 0,
            SevereMultiplier = 0,
            DroppedFrameRun = 0,
            Cooldown = TimeSpan.Zero,
        };

        Assert.True(options.Normalize());
        Assert.True(options.SpikeMultiplier > 1);
        Assert.True(options.SevereMultiplier >= options.SpikeMultiplier);
        Assert.True(options.DroppedFrameRun >= 2);
        Assert.True(options.Cooldown >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Normalize_BoundsTheBaselineWindowAllocation()
    {
        var options = new AutoDetectOptions { BaselineWindowFrames = int.MaxValue };

        Assert.True(options.Normalize());
        Assert.Equal(AutoDetectOptions.MaxBaselineWindowFrames, options.BaselineWindowFrames);
    }

    /// <summary>The observed sample count saturates at the window size, so a larger minimum never arms.</summary>
    [Fact]
    public void Normalize_KeepsTheMinimumWithinTheBaselineWindow()
    {
        var options = new AutoDetectOptions { BaselineWindowFrames = 300, MinimumSamples = 5000 };

        Assert.True(options.Normalize());
        Assert.Equal(300, options.MinimumSamples);
    }

    /// <summary>
    /// Zero is a real setting and means the multiplier decides alone, which is what the detector did
    /// before the floor existed. A floor of several seconds would be the detector switched off.
    /// </summary>
    [Fact]
    public void Normalize_BoundsTheIncidentFloor()
    {
        var off = new AutoDetectOptions { IncidentFloorMs = 0 };
        var absurd = new AutoDetectOptions { IncidentFloorMs = 60_000 };
        var nonFinite = new AutoDetectOptions { IncidentFloorMs = double.NaN };

        Assert.False(off.Normalize());
        Assert.Equal(0, off.IncidentFloorMs);

        Assert.True(absurd.Normalize());
        Assert.Equal(1000, absurd.IncidentFloorMs);

        Assert.True(nonFinite.Normalize());
        Assert.Equal(100, nonFinite.IncidentFloorMs);
    }

    [Fact]
    public void Normalize_ReplacesNonFiniteMultipliers()
    {
        var options = new AutoDetectOptions { SpikeMultiplier = double.NaN, SevereMultiplier = double.PositiveInfinity };

        Assert.True(options.Normalize());
        Assert.Equal(2.0, options.SpikeMultiplier);
        Assert.Equal(4.0, options.SevereMultiplier);
    }

    /// <summary>An oversized window used to allocate two arrays of that size before anything clamped it.</summary>
    [Fact]
    public void Detector_ClampsTheBaselineWindowEvenWhenOptionsWereNotNormalized()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions { BaselineWindowFrames = int.MaxValue }, 60);

        Assert.NotNull(detector);
    }
}
