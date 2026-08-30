namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The live view exists because the question "which program is holding the card" was only ever
/// answerable the following day, from a CSV.
/// </summary>
/// <remarks>
/// The numbers here are the 29 August session's. Voicemod held 669 MB, flat, across 2 712 samples over
/// three hours and 47 minutes, then took 734 MB in twenty seconds at 02:33:32 and kept it for the rest
/// of the evening. The game's row grew too, from 2 673 MB to 5 509 MB, and must not be reported as the
/// same kind of event — which is the distinction these tests are mostly about.
/// </remarks>
public sealed class LiveVramTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 20, 46, 57, TimeSpan.Zero);

    [Fact]
    public void AStepChangeIsReportedWithWhatItTookAndWhatItHoldsNow()
    {
        var tracker = new LiveVramTracker();

        // Flat at 669 MB for a while, exactly as the session logged it.
        for (var index = 0; index < 12; index++)
        {
            Assert.Empty(tracker.Observe(Sample(Start.AddSeconds(index * 5), 669)).Growth);
        }

        // The step: 669 to 1 403 MB over twenty seconds, then flat — which is where the size of it is
        // finally known, and where the report has to come from.
        var growth = new List<LiveVramGrowth>();
        foreach (var (offsetSeconds, megabytes) in
                 new[] { (65, 784), (70, 934), (75, 1_099), (80, 1_258), (85, 1_403), (90, 1_403) })
        {
            growth.AddRange(tracker.Observe(Sample(Start.AddSeconds(offsetSeconds), megabytes)).Growth);
        }

        var alert = Assert.Single(growth);
        Assert.Equal("Voicemod", alert.ProcessName);
        Assert.Equal(734, alert.TakenBytes / 1024d / 1024, 0);
        Assert.Equal(669, alert.BaselineBytes / 1024d / 1024, 0);
        Assert.Equal(1_403, alert.CurrentBytes / 1024d / 1024, 0);
    }

    /// <summary>
    /// Once per process. The step stays in the numbers afterwards, and repeating it every five seconds
    /// for the rest of the evening would bury the next one.
    /// </summary>
    [Fact]
    public void TheSameStepIsNotReportedTwice()
    {
        var tracker = new LiveVramTracker();
        var growth = new List<LiveVramGrowth>();

        growth.AddRange(tracker.Observe(Sample(Start, 669)).Growth);
        growth.AddRange(tracker.Observe(Sample(Start.AddSeconds(20), 1_403)).Growth);

        for (var index = 1; index < 40; index++)
        {
            growth.AddRange(tracker.Observe(Sample(Start.AddSeconds(20 + index * 5), 1_440)).Growth);
        }

        Assert.Single(growth);
    }

    /// <summary>
    /// The game fills its texture pool to a ceiling every session and that is not a step change. This is
    /// the fastest fill measured — High on 25 August, 3 985 MB to 7 200 MB over twenty minutes — replayed
    /// at its real rate.
    /// </summary>
    [Fact]
    public void TheGameFillingItsTexturePoolIsNotAStepChange()
    {
        var tracker = new LiveVramTracker();
        var growth = new List<LiveVramGrowth>();

        for (var index = 0; index <= 240; index++)
        {
            var megabytes = 3_985 + (7_200 - 3_985) * index / 240;
            growth.AddRange(tracker
                .Observe(Sample(Start.AddSeconds(index * 5), megabytes, "FiveM_b3407_GTAProcess", processId: 5264))
                .Growth);
        }

        Assert.Empty(growth);
    }

    /// <summary>
    /// A row the session has proved impossible stays in the view and stays labelled. Hiding it makes the
    /// fault unfindable, and this table is where obs64 claiming 39.9 GB of a 10 GB card gets noticed.
    /// </summary>
    [Fact]
    public void AnUnbelievableRowIsListedButMarkedUntrusted()
    {
        var tracker = new LiveVramTracker();
        var sample = new GpuProcessMemorySample(
            Start,
            true,
            [
                new GpuProcessMemoryUsage(9_000, "obs64", 39_934UL * 1024 * 1024, 0),
                new GpuProcessMemoryUsage(5_264, "FiveM_b3407_GTAProcess", 5_397UL * 1024 * 1024, 0),
            ],
            DoubleCountedProcessIds: [9_000]);

        var rows = tracker.Observe(sample).Rows;

        Assert.Equal("obs64", rows[0].ProcessName);
        Assert.False(rows[0].IsTrusted);
        Assert.True(rows[1].IsTrusted);
    }

    /// <summary>
    /// And an impossible row must not raise a growth alarm on its way up, because its bytes are not real.
    /// </summary>
    [Fact]
    public void AnUnbelievableRowDoesNotRaiseAGrowthAlarm()
    {
        var tracker = new LiveVramTracker();

        for (var index = 0; index < 10; index++)
        {
            var sample = new GpuProcessMemorySample(
                Start.AddSeconds(index * 5),
                true,
                [new GpuProcessMemoryUsage(9_000, "obs64", (ulong)(575 + index * 4_400) * 1024 * 1024, 0)],
                DoubleCountedProcessIds: [9_000]);

            Assert.Empty(tracker.Observe(sample).Growth);
        }
    }

    /// <summary>Rows too small to act on are left out, so the view stays a view and not a process list.</summary>
    [Fact]
    public void SmallRowsAreNotListed()
    {
        var tracker = new LiveVramTracker();
        var sample = new GpuProcessMemorySample(
            Start,
            true,
            [
                new GpuProcessMemoryUsage(5_264, "FiveM_b3407_GTAProcess", 5_397UL * 1024 * 1024, 0),
                new GpuProcessMemoryUsage(1_100, "TextInputHost", 4UL * 1024 * 1024, 0),
            ]);

        var row = Assert.Single(tracker.Observe(sample).Rows);
        Assert.Equal("FiveM_b3407_GTAProcess", row.ProcessName);
    }

    /// <summary>Growth since the session started travels with each row, which is what makes the view readable.</summary>
    [Fact]
    public void EachRowCarriesWhatItHasTakenSinceTheSessionStarted()
    {
        var tracker = new LiveVramTracker();
        tracker.Observe(Sample(Start, 669));

        var row = Assert.Single(tracker.Observe(Sample(Start.AddSeconds(20), 1_403)).Rows);

        Assert.Equal(1_403, row.DedicatedMegabytes, 0);
        Assert.Equal(734, row.GrowthMegabytes, 0);
        Assert.Equal(1_403UL * 1024 * 1024, row.PeakBytes);
    }

    /// <summary>
    /// Windows reuses process ids within an evening, and the history is keyed on them. A different
    /// program landing on a dead one's id must not inherit its baseline, its peak or — worst of the
    /// three — the flag saying its step change has already been reported.
    /// </summary>
    [Fact]
    public void ADifferentProgramOnAReusedProcessIdStartsFromNothing()
    {
        var tracker = new LiveVramTracker();
        tracker.Observe(Sample(Start, 669));
        tracker.Observe(Sample(Start.AddSeconds(5), 1_403));

        // Voicemod exits and Chrome starts on the same id.
        var row = Assert.Single(tracker.Observe(Sample(Start.AddSeconds(10), 900, "chrome")).Rows);

        Assert.Equal("chrome", row.ProcessName);
        Assert.Equal(900, row.BaselineBytes / 1024d / 1024, 0);
        Assert.Equal(900, row.PeakBytes / 1024d / 1024, 0);
        Assert.Equal(0, row.GrowthMegabytes, 0);
    }

    /// <summary>
    /// The same program restarting onto its own old id carries the same name, so the name alone cannot
    /// catch it. Its absence from the samples in between can, and it also bounds the dictionary over a
    /// long evening.
    /// </summary>
    [Fact]
    public void AProcessGoneForLongEnoughIsForgotten()
    {
        var tracker = new LiveVramTracker();
        tracker.Observe(Sample(Start, 669));

        // Six minutes of samples it does not appear in.
        tracker.Observe(new GpuProcessMemorySample(Start.AddMinutes(6), true, []));

        var row = Assert.Single(tracker.Observe(Sample(Start.AddMinutes(7), 1_403)).Rows);

        Assert.Equal(1_403, row.BaselineBytes / 1024d / 1024, 0);
        Assert.Equal(0, row.GrowthMegabytes, 0);
    }

    /// <summary>
    /// The list carries the largest holders only, so a live process can drop off it for a sample or two.
    /// That is not a restart, and re-baselining it would lose the growth the row exists to show.
    /// </summary>
    [Fact]
    public void AProcessThatDropsOffTheListBrieflyKeepsItsHistory()
    {
        var tracker = new LiveVramTracker();
        tracker.Observe(Sample(Start, 669));
        tracker.Observe(new GpuProcessMemorySample(Start.AddSeconds(30), true, []));

        var row = Assert.Single(tracker.Observe(Sample(Start.AddMinutes(1), 1_403)).Rows);

        Assert.Equal(669, row.BaselineBytes / 1024d / 1024, 0);
        Assert.Equal(734, row.GrowthMegabytes, 0);
    }

    private static GpuProcessMemorySample Sample(
        DateTimeOffset at,
        int megabytes,
        string processName = "Voicemod",
        int processId = 7_412)
    {
        return new GpuProcessMemorySample(
            at,
            true,
            [new GpuProcessMemoryUsage(processId, processName, (ulong)megabytes * 1024 * 1024, 0)]);
    }
}
