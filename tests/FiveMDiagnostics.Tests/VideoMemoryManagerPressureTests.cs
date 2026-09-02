namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// The measurement that told a full card from a busy one for the first time.
/// </summary>
/// <remarks>
/// <para>
/// Across the four deep captures of 1 September <c>dxgmms2.sys</c> — the kernel driver that pages video
/// memory in and out of the card — tracked the card's own occupancy exactly: 0.05 cores at 84% VRAM,
/// 0.15 at 86%, 0.18 at 88%, 0.22 at 86% with a hitch cluster, 0.41 during a hitch at 91%, and 0.91
/// through the nine-second freeze at 92%. The game fell from 3.93 to 3.22 cores in the same seconds and
/// its render thread from 0.64 to 0.34.
/// </para>
/// <para>
/// That pairing is the whole point. PresentMon books those frames as CPU-bound because
/// <c>MsCPUBusy</c> is wall clock on the pipeline's CPU side, not consumed processor time — so a game
/// standing still inside a driver call reads identically to one computing flat out. The driver
/// climbing while the game falls is the signature that separates them.
/// </para>
/// </remarks>
public sealed class VideoMemoryManagerPressureTests
{
    /// <summary>The freeze, with the evening's own numbers.</summary>
    [Fact]
    public void ADriverAtNearlyOneCoreIsCalledPressureAndSaysWhatItCost()
    {
        var pressure = new VideoMemoryPressure(
            BaselineCores: 0.18,
            PeakCores: 0.91,
            SubjectProcess: "FiveM_b3407_GTAProcess.exe",
            SubjectCoresAtPeak: 3.22,
            SubjectBaselineCores: 3.93);

        Assert.True(pressure.IsPressured);
        Assert.True(pressure.SubjectWentQuiet);

        var described = pressure.Describe();
        Assert.Contains("0,91 kärnor", described, StringComparison.Ordinal);
        Assert.Contains("0,18", described, StringComparison.Ordinal);
        Assert.Contains("evakuerade ytor", described, StringComparison.Ordinal);
        Assert.Contains("vänta, inte till att räkna", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The quiet traces of the same evening. A capture taken at 84% has to read as "nothing happening",
    /// or the measurement says the same thing about every trace and distinguishes nothing.
    /// </summary>
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.18)]
    [InlineData(0.22)]
    public void TheQuietTracesOfTheSameEveningAreNotPressure(double peakCores)
    {
        var pressure = new VideoMemoryPressure(0.05, peakCores, "FiveM_b3407_GTAProcess.exe", 3.9, 3.9);

        Assert.False(pressure.IsPressured);
        Assert.Contains("ingen mätbar flyttning", pressure.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A driver working hard while the game works just as hard is not the waiting signature, and must
    /// not be described as one. Paging during a level load looks like this.
    /// </summary>
    [Fact]
    public void AGameThatKeptWorkingIsNotDescribedAsWaiting()
    {
        var pressure = new VideoMemoryPressure(0.20, 0.85, "FiveM_b3407_GTAProcess.exe", 3.90, 3.93);

        Assert.True(pressure.IsPressured);
        Assert.False(pressure.SubjectWentQuiet);
        Assert.DoesNotContain("vänta, inte till att räkna", pressure.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Without the per-process series there is a rate and nothing to pair it with.</summary>
    [Fact]
    public void AnUnpairedRateStillReportsTheDriver()
    {
        var pressure = new VideoMemoryPressure(0.18, 0.91, "FiveM_b3407_GTAProcess.exe", null, null);

        Assert.True(pressure.IsPressured);
        Assert.False(pressure.SubjectWentQuiet);
        Assert.Contains("0,91 kärnor", pressure.Describe(), StringComparison.Ordinal);
    }
}
