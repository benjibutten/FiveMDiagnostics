namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// Every VRAM figure the investigation has is from minutes the game was running, and none of them says
/// whose memory the card was full of. The minutes after the game exits do.
/// </summary>
public sealed class PostGameVramReleaseTests
{
    private static readonly DateTimeOffset Exit = new(2026, 9, 11, 23, 41, 0, TimeSpan.Zero);

    [Fact]
    public void MemoryTheGameGivesBackIsReportedAsTheGames()
    {
        var release = new PostGameVramRelease();
        release.Observe(Reading(Exit.AddSeconds(-2), 94));
        release.NoteGameExit(Exit);
        release.Observe(Reading(Exit.AddSeconds(30), 71));
        release.Observe(Reading(Exit.AddMinutes(2), 38));
        release.Observe(Reading(Exit.AddMinutes(4), 40));

        var report = release.Summary();

        Assert.NotNull(report);
        Assert.False(report!.StillHeld);
        Assert.Equal(38, report.LowestPercentAfter, 0);
        Assert.Equal(TimeSpan.FromMinutes(2), report.TimeToLowest);
        Assert.Contains("Minnet var spelets", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reading that changes what to do about an evening: the band is where the hitches cluster, and
    /// a card that never leaves it with the game closed was not filled by the game.
    /// </summary>
    [Fact]
    public void MemoryStillHeldAfterTheGameIsReportedAsSomebodyElses()
    {
        var release = new PostGameVramRelease();
        release.Observe(Reading(Exit.AddSeconds(-1), 94));
        release.NoteGameExit(Exit);
        release.Observe(Reading(Exit.AddMinutes(3), 90));
        release.Observe(Reading(Exit.AddMinutes(9), 89));

        var report = release.Summary();

        Assert.NotNull(report);
        Assert.True(report!.StillHeld);
        Assert.Equal(TimeSpan.FromMinutes(9), report.Measured);
        Assert.Contains("inte spelets", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restart inside the wait is one evening, and the dip between the two processes is a game
    /// loading rather than a card releasing.
    /// </summary>
    [Fact]
    public void ARestartTakesTheReleaseBack()
    {
        var release = new PostGameVramRelease();
        release.Observe(Reading(Exit.AddSeconds(-1), 94));
        release.NoteGameExit(Exit);
        release.Observe(Reading(Exit.AddMinutes(2), 37));

        release.NoteGameRunning();

        Assert.Null(release.Summary());
    }

    [Fact]
    public void NothingIsClaimedBeforeTheGameHasExited()
    {
        var release = new PostGameVramRelease();
        release.Observe(Reading(Exit.AddMinutes(-10), 92));

        Assert.Null(release.Summary());
    }

    /// <summary>
    /// NVML reports device index 0 for the whole session, which on a machine with a second NVIDIA
    /// device need not be the card the game rendered on.
    /// </summary>
    [Fact]
    public void OnlySingleAdapterReadingsCount()
    {
        var release = new PostGameVramRelease();
        release.Observe(Reading(Exit.AddSeconds(-1), 94, adapterCount: 2));
        release.NoteGameExit(Exit);
        release.Observe(Reading(Exit.AddMinutes(2), 38, adapterCount: 2));

        Assert.Null(release.Summary());
    }

    private static GpuTelemetrySample Reading(DateTimeOffset at, double percent, int? adapterCount = 1)
    {
        const ulong total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            at,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 40,
            MemoryBandwidthUtilizationPercent: 15,
            UsedVramBytes: (ulong)(total * percent / 100),
            TotalVramBytes: total,
            EncoderUtilizationPercent: 30,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 57,
            ThrottleReasons: [],
            AdapterCount: adapterCount);
    }
}
