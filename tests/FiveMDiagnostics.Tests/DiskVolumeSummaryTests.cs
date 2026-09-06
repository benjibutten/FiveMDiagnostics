namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// The one line the 5 September investigation was missing, and every input for it was already in the
/// trace the app imports.
/// </summary>
public sealed class DiskVolumeSummaryTests
{
    /// <summary>
    /// The sentence: the slowest operation with its size, its file and the process that issued it, then
    /// what the healthy volume charges for the same thing, then the thread's own wait.
    /// </summary>
    [Fact]
    public void TheSlowestOperationIsNamedAgainstTheHealthyVolume()
    {
        var summary = Summary();

        var described = summary.Describe(gameThreadMaxWaitMs: 454.1);

        Assert.Contains("454 ms för en 64 kB-läsning ur D:\\pagefile.sys, utfärdad av spelet", described, StringComparison.Ordinal);
        Assert.Contains("C: levererar samma sorts operation på 0,04 ms", described, StringComparison.Ordinal);
        Assert.Contains("Spelets huvudtråd låg av processorn 454,1 ms i samma sekund", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every volume gets its median and its max, which is what separates a disk that is working hard
    /// from a disk that is slow. Averaged together they read as one unremarkable drive.
    /// </summary>
    [Fact]
    public void EveryVolumeGetsItsOwnMedianAndMax()
    {
        var described = Summary().Describe(gameThreadMaxWaitMs: 454.1);

        Assert.Contains("D: 25 ops, median 10,11 ms, max 454 ms", described, StringComparison.Ordinal);
        Assert.Contains("C: 743 ops, median 0,04 ms, max 11 ms", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pairing is only claimed when the two figures agree. A wait that does not match the disk is a
    /// different event, and saying otherwise would turn the strongest evidence in the tool into a
    /// coincidence dressed up as a proof.
    /// </summary>
    [Fact]
    public void AWaitThatDoesNotMatchIsNotPairedWithTheDisk()
    {
        var described = Summary().Describe(gameThreadMaxWaitMs: 120);

        Assert.DoesNotContain("i samma sekund", described, StringComparison.Ordinal);
    }

    /// <summary>A trace whose thread wait was never measured still gets the disk sentence.</summary>
    [Fact]
    public void TheDiskSentenceStandsWithoutAThreadWait()
    {
        var described = Summary().Describe(gameThreadMaxWaitMs: null);

        Assert.Contains("D:\\pagefile.sys", described, StringComparison.Ordinal);
        Assert.DoesNotContain("i samma sekund", described, StringComparison.Ordinal);
    }

    /// <summary>The 23:04 trace of 5 September, as the parser summarizes it.</summary>
    private static DiskVolumeSummary Summary()
    {
        var slowest = new DiskOperation(
            "D:",
            @"D:\pagefile.sys",
            ServiceMs: 454.116,
            Bytes: 65_536,
            IsRead: true,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            IsGameProcess: true);

        return new DiskVolumeSummary(
            [
                new DiskVolumeStats("D:", 25, 10.113, 454.116, 1.3),
                new DiskVolumeStats("F:", 7, 9.975, 13.098, 0.5),
                new DiskVolumeStats("C:", 743, 0.04, 11.319, 8.5),
            ],
            [slowest],
            HardFaults: 104,
            GameHardFaults: 18,
            SlowestPagingOperation: slowest,
            SlowestHardFaultMs: 454.204);
    }
}
