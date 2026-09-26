namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;
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
            SubjectBaselineCores: 3.93,
            PeakAt: null);

        Assert.True(pressure.IsPressured);
        Assert.True(pressure.SubjectWentQuiet);

        var described = pressure.Describe(adapterVramPercent: 92);
        Assert.Contains("0,91 kärnor", described, StringComparison.Ordinal);
        Assert.Contains("0,18", described, StringComparison.Ordinal);
        Assert.Contains("92,0 %", described, StringComparison.Ordinal);
        Assert.Contains("eviction", described, StringComparison.Ordinal);
        Assert.Contains("vänta, inte till att räkna", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same driver rate on a card that was half empty, which is not memory pressure and was reported
    /// as such for a whole session.
    /// </summary>
    /// <remarks>
    /// The 2 145 ms freeze of 2 September, nine minutes into the evening: 0.42 cores in the driver with
    /// the card at 54%, and the analysis wrote "so much movement means the card was full and the driver
    /// was evacuating surfaces over PCIe" underneath it. The trace was right about the driver and had no
    /// way to check the rest of the sentence — the real cause was Windows Search hammering the file
    /// system, which the card's own reading would have pointed away from immediately.
    /// </remarks>
    [Fact]
    public void TheSameRateOnAHalfEmptyCardIsNotCalledMemoryPressure()
    {
        var pressure = new VideoMemoryPressure(0.03, 0.42, "FiveM_b3407_GTAProcess.exe", 6.12, 2.75, null);

        var described = pressure.Describe(adapterVramPercent: 54);

        Assert.Contains("0,42 kärnor", described, StringComparison.Ordinal);
        Assert.Contains("54,0 %", described, StringComparison.Ordinal);
        Assert.Contains("inte minnestryck", described, StringComparison.Ordinal);

        // The claim that was made about this trace, and must not be made again.
        Assert.DoesNotContain("flyttningen är eviction", described, StringComparison.Ordinal);
        Assert.DoesNotContain("kortet var fullt", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a reading of the card the sentence states the measurement and defers the conclusion,
    /// rather than asserting the half of it the trace cannot see.
    /// </summary>
    [Fact]
    public void WithoutTheCardsOwnReadingTheConclusionIsDeferred()
    {
        var described = new VideoMemoryPressure(0.18, 0.91, "FiveM_b3407_GTAProcess.exe", null, null, null)
            .Describe();

        Assert.Contains("0,91 kärnor", described, StringComparison.Ordinal);
        Assert.Contains("ingen avläsning av kortets fyllnadsgrad", described, StringComparison.Ordinal);
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
        var pressure = new VideoMemoryPressure(0.05, peakCores, "FiveM_b3407_GTAProcess.exe", 3.9, 3.9, null);

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
        var pressure = new VideoMemoryPressure(0.20, 0.85, "FiveM_b3407_GTAProcess.exe", 3.90, 3.93, null);

        Assert.True(pressure.IsPressured);
        Assert.False(pressure.SubjectWentQuiet);
        Assert.DoesNotContain("vänta, inte till att räkna", pressure.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The busiest second is named in the sentence, so the reading can be told from the window median it
    /// used to be.
    /// </summary>
    [Fact]
    public void TheSecondTheReadingBelongsToIsNamed()
    {
        var pressure = new VideoMemoryPressure(
            BaselineCores: 0.22,
            PeakCores: 0.97,
            SubjectProcess: "FiveM_b3407_GTAProcess.exe",
            SubjectCoresAtPeak: 2.45,
            SubjectBaselineCores: 4.13,
            PeakAt: new DateTime(2026, 9, 10, 1, 57, 43, DateTimeKind.Local));

        var described = pressure.Describe(adapterVramPercent: 92.6);

        Assert.Contains("kl. 01:57:43", described, StringComparison.Ordinal);
        Assert.Contains("flyttningen är eviction", described, StringComparison.Ordinal);

        // The sentence that stood underneath a correct verdict of GPU VRAM pressure on that frame.
        Assert.DoesNotContain("inte minnestryck", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The evening of 21 September: the driver's busiest second of the whole series, with the game
    /// waiting through it, on a card that never reached the band.
    /// </summary>
    /// <remarks>
    /// 1.00 cores in <c>dxgmms2.sys</c> at 23:25:08 with the game down to 1.98 from 3.26, GPU
    /// utilisation at 3% and bandwidth at 1% — and 87.1% was the *peak* of the readings within two
    /// seconds either side, not an unlucky sample. The line said "det här var inte minnestryck" about
    /// the second-worst frame of the evening, which is a conclusion the trace does not support: what the
    /// occupancy fails to do is corroborate eviction, not establish that nothing waited.
    /// </remarks>
    [Fact]
    public void AGameThatStoppedComputingIsNotCalledUnaffectedJustBecauseTheCardWasBelowTheBand()
    {
        var pressure = new VideoMemoryPressure(
            BaselineCores: 0.18,
            PeakCores: 1.00,
            SubjectProcess: "FiveM_b3407_GTAProcess.exe",
            SubjectCoresAtPeak: 1.98,
            SubjectBaselineCores: 3.26,
            PeakAt: new DateTime(2026, 9, 21, 23, 25, 8, DateTimeKind.Local));

        var described = pressure.Describe(adapterVramPercent: 87.1);

        Assert.True(pressure.SubjectWentQuiet);
        Assert.Contains("1,00 kärnor", described, StringComparison.Ordinal);
        Assert.Contains("87,1 %", described, StringComparison.Ordinal);
        Assert.Contains("vänta, inte till att räkna", described, StringComparison.Ordinal);
        Assert.Contains("fyllnadsgraden förklarar inte flyttningen", described, StringComparison.Ordinal);

        // The sentence this test exists to prevent. The game demonstrably stopped; saying the moment
        // cost nothing contradicts the measurement in the same line.
        Assert.DoesNotContain("inte minnestryck", described, StringComparison.Ordinal);

        // And it still must not claim the thing the occupancy rules out.
        Assert.DoesNotContain("flyttningen är eviction", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reading just under the band is not rounded up to it in the sentence that says it is under it.
    /// </summary>
    /// <remarks>
    /// 25 September 23:15:46: the card at 87.6% with the driver on 0.66 cores, written as "bara på 88 %,
    /// alltså under 88 %".
    /// </remarks>
    [Fact]
    public void AReadingJustUnderTheBandIsNotRoundedUpToIt()
    {
        var pressure = new VideoMemoryPressure(0.03, 0.66, "FiveM_b3407_GTAProcess.exe", 3.5, 3.5, null);

        var described = pressure.Describe(adapterVramPercent: 87.6);

        Assert.Contains("87,6 %, alltså under", described, StringComparison.Ordinal);
        Assert.DoesNotContain("88 %, alltså under", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The band is the band monitor's, not a second copy of the number.
    /// </summary>
    /// <remarks>
    /// It was a <c>private const double CorroboratingVramPercent = 88</c> in the ETL project until
    /// 2026-09-22, with no reference to <see cref="VramPressureBandMonitor.BandPercent"/> — two
    /// hardcoded 88s in two projects that read to a user as one threshold and would have drifted apart
    /// the first time either was tuned.
    /// </remarks>
    [Fact]
    public void TheEvictionBandIsTheOneTheRestOfTheAppUses()
    {
        var pressure = new VideoMemoryPressure(0.10, 0.90, "FiveM_b3407_GTAProcess.exe", 3.9, 3.9, null);

        Assert.Contains(
            $"{VramPressureBandMonitor.BandPercent:F0} % där eviction börjar",
            pressure.Describe(adapterVramPercent: VramPressureBandMonitor.BandPercent - 1),
            StringComparison.Ordinal);

        Assert.Contains(
            "flyttningen är eviction",
            pressure.Describe(adapterVramPercent: VramPressureBandMonitor.BandPercent),
            StringComparison.Ordinal);
    }

    /// <summary>Without the per-process series there is a rate and nothing to pair it with.</summary>
    [Fact]
    public void AnUnpairedRateStillReportsTheDriver()
    {
        var pressure = new VideoMemoryPressure(0.18, 0.91, "FiveM_b3407_GTAProcess.exe", null, null, null);

        Assert.True(pressure.IsPressured);
        Assert.False(pressure.SubjectWentQuiet);
        Assert.Contains("0,91 kärnor", pressure.Describe(), StringComparison.Ordinal);
    }
}
