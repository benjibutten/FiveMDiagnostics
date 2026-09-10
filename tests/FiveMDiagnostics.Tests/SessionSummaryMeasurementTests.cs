namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The three numbers the 31 August review had to work out by hand from the CSV, each of which the app
/// already had every input for.
/// </summary>
public sealed class SessionSummaryMeasurementTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 18, 23, 0, TimeSpan.Zero);

    /// <summary>
    /// The line that would have made the whole review unnecessary: how much of the evening the card
    /// spent inside the band, and what a minute inside it cost against a minute outside it.
    /// </summary>
    /// <remarks>
    /// Built to the shape of that evening — a stretch at 92% carrying hitches at several times the rate
    /// of the stretch at 70%. The band is not a guess any more; it is these minutes compared with each
    /// other.
    /// </remarks>
    [Fact]
    public void TheBandIsMeasuredAgainstTheSessionsOwnHitches()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);

        // Ten quiet minutes at 70%, one hitch each.
        Play(monitor, Start, minutes: 10, vramPercent: 70, hitchesPerMinute: 1);

        // Three minutes at 92%, ten hitches each.
        Play(monitor, Start.AddMinutes(10), minutes: 3, vramPercent: 92, hitchesPerMinute: 10);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(13, report!.MeasuredMinutes, 1);
        Assert.Equal(3, report.MinutesInBand, 1);
        Assert.Equal(3, report.MinutesInDeepBand, 1);
        Assert.True(report.IsPressured);

        // Every frame found a reading of its own, so nothing is estimated and nothing is discarded.
        Assert.Equal(0, report.UnpairedFrames);
        Assert.True(report.PairedFrames > 40_000, $"{report.PairedFrames} frames paired against a reading");

        // Ten an hour outside, six hundred inside.
        Assert.NotNull(report.HitchRatio);
        Assert.Equal(10, report.HitchRatio!.Value, 1);
        Assert.Contains("hitchfrekvensen", report.Message, StringComparison.Ordinal);
        Assert.Contains("3,0 av 13 minuter", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure the time bucket produced: a card that crosses the band for half a minute at a time,
    /// which a bucket filed on whichever side it leant to along with every hitch in it.
    /// </summary>
    /// <remarks>
    /// Built to the shape of 2 September, where 125 of 351 minutes touched the band without a single one
    /// of them counting as inside it. Those minutes carried 654 of the session's 1 246 hitches, and the
    /// report came out at 1.4× against 3.4× measured per sample. Shortening the bucket to fifteen seconds
    /// reduced the error and did not remove it — 235 of the 1 480 intervals of 6 September still straddled
    /// the line. Pairing each frame with the reading nearest it removes it: half of every minute here is
    /// spent at 92% carrying twenty times the hitches of the half at 70%, and none of those hitches may
    /// land outside the band.
    /// </remarks>
    [Fact]
    public void HalfMinuteExcursionsIntoTheBandAreNotFiledAsTimeOutsideIt()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);

        for (var minute = 0; minute < 20; minute++)
        {
            var start = Start.AddMinutes(minute);

            // Thirty seconds at 70%, then thirty at 92%.
            Play(monitor, start, minutes: 1, vramPercent: 70, hitchesPerMinute: 0, seconds: 30);
            Play(monitor, start.AddSeconds(30), minutes: 1, vramPercent: 92, hitchesPerMinute: 10, seconds: 30);
        }

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.True(report!.IsPressured);

        // Half the evening, seen as half the evening rather than as none of it.
        Assert.Equal(10, report.MinutesInBand, 1);
        Assert.Equal(20, report.MeasuredMinutes, 1);

        // All of the hitches are inside the band, and none of them are attributed outside it.
        Assert.Equal(0, report.OutsideHitches);
        Assert.Contains("hitchade sessionen inte alls", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card full and the frames fine. The measurement clears VRAM rather than accusing it, and the
    /// line has to say so first and stop being a warning.
    /// </summary>
    /// <remarks>
    /// 5 September had the highest VRAM pressure of the whole investigation — half the session above the
    /// band — with its hitches concentrated outside the band, 789 an hour against 869. The line went out
    /// as a Warning starting "VRAM-tryck: kortet låg över 88 %" and was read as a VRAM warning for a day,
    /// while the actual cause was a paging read.
    /// </remarks>
    [Fact]
    public void ABandThatCostNothingLeadsWithTheConclusionAndIsNotAWarning()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);

        // Twenty minutes at 92% with one hitch each, then twenty at 70% with five — enough material to
        // state the conclusion rather than only to gesture at it. See the thin-data test below for why
        // the minutes matter here.
        Play(monitor, Start, minutes: 20, vramPercent: 92, hitchesPerMinute: 1);
        Play(monitor, Start.AddMinutes(20), minutes: 20, vramPercent: 70, hitchesPerMinute: 5);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.True(report!.BandCostNothing);
        Assert.False(report.IsPressured);
        Assert.StartsWith("Bandet kostade ingenting den här sessionen.", report.Message, StringComparison.Ordinal);
        Assert.Contains("motbevis", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same shape as above, but with too little of the session measured to state it. Exonerating the
    /// band on a few minutes of a session that may run for hours is a claim the data has not earned.
    /// </summary>
    /// <remarks>
    /// 8 September's quarter-hourly lines wrote "Bandet kostade ingenting den här sessionen" on 3-7
    /// minutes of material while the same session's closing line reported a genuine 3.1× on 324 minutes —
    /// the same sentence used for a session's whole confidence and for a sliver of it.
    /// </remarks>
    [Fact]
    public void ABandThatCostNothingOnThinMaterialSaysSoInsteadOfExonerating()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);

        // Same 1:5 hitch-rate shape as the test above, only a tenth of the minutes.
        Play(monitor, Start, minutes: 2, vramPercent: 92, hitchesPerMinute: 1);
        Play(monitor, Start.AddMinutes(2), minutes: 2, vramPercent: 70, hitchesPerMinute: 5);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.True(report!.BandCostNothing);
        Assert.StartsWith("Bara 4 minuter mätta hittills", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Bandet kostade ingenting", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same thin material with the ratio pointing the other way. The floor was written to cut both
    /// ways and only the exoneration was getting it: a session with a minute in the band wrote its ratio
    /// out as a finding while the identical measurement acquitting the band was held back.
    /// </summary>
    [Fact]
    public void AHigherRateInTheBandOnThinMaterialIsNotAFindingEither()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);

        Play(monitor, Start, minutes: 2, vramPercent: 92, hitchesPerMinute: 10);
        Play(monitor, Start.AddMinutes(2), minutes: 2, vramPercent: 70, hitchesPerMinute: 1);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.NotNull(report!.HitchRatio);
        Assert.True(report.HitchRatio!.Value > 1);
        Assert.Contains("ingen slutsats åt något håll", report.Message, StringComparison.Ordinal);

        // The card was in the deep band for two of those minutes, which is a measurement rather than a
        // comparison, and it stays a warning.
        Assert.True(report.IsPressured);
    }

    /// <summary>
    /// The exoneration on thin material may say so in the prose and may not quietly downgrade the line
    /// as well: the card sat in the deep band, and that much is measured rather than inferred.
    /// </summary>
    [Fact]
    public void AThinExonerationDoesNotDowngradeTheLine()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);

        Play(monitor, Start, minutes: 2, vramPercent: 92, hitchesPerMinute: 1);
        Play(monitor, Start.AddMinutes(2), minutes: 2, vramPercent: 70, hitchesPerMinute: 5);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.True(report!.BandCostNothing);
        Assert.True(report.IsPressured);
    }

    /// <summary>
    /// An evening that never reaches the band says so, and says nothing about a gradient it has no
    /// minutes to measure.
    /// </summary>
    [Fact]
    public void AnEveningBelowTheBandIsReportedAsSuch()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);
        Play(monitor, Start, minutes: 10, vramPercent: 62, hitchesPerMinute: 1);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(0, report!.MinutesInBand);
        Assert.False(report.IsPressured);
        Assert.Contains("höll sig under", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// "None of the 35 frames over 100 ms waited" — the sharpest single observation of the review, and
    /// one subtraction away from data the session already holds.
    /// </summary>
    [Fact]
    public void TheWaitDistributionOfTheLargestFramesIsCounted()
    {
        var profile = new SlowFrameWaitProfile();

        foreach (var frameTimeMs in new[] { 356d, 235, 291, 285, 431 })
        {
            profile.Observe(Frame(Start, frameTimeMs, cpuWaitMs: 0.1));
        }

        // And a hundred ordinary frames, which are not what the line is about.
        for (var index = 0; index < 100; index++)
        {
            profile.Observe(Frame(Start.AddSeconds(index), 16.7, cpuWaitMs: 8.2));
        }

        var report = profile.Summary();

        Assert.NotNull(report);
        Assert.Equal(5, report!.SlowFrames);
        Assert.Equal(0, report.Waited);
        Assert.True(report.NoneWaited);
        Assert.Contains("0 av 5 frames över 100 ms", report.Message, StringComparison.Ordinal);
        Assert.Contains("blockerad tråd förklarar dem inte", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture that carried no <c>MsCPUWait</c> at all still has a count worth printing, and the line
    /// has to say that nothing measured those frames rather than disappear.
    /// </summary>
    /// <remarks>
    /// PresentMon v1, or a v2 run that lost the column. The distribution is genuinely absent there; the
    /// 35 large frames are not, and dropping the whole report read exactly like an evening that had no
    /// large frames at all.
    /// </remarks>
    [Fact]
    public void LargeFramesWithoutTheColumnAreStillReported()
    {
        var profile = new SlowFrameWaitProfile();

        foreach (var frameTimeMs in new[] { 356d, 235, 291 })
        {
            profile.Observe(Frame(Start, frameTimeMs, cpuWaitMs: null));
        }

        var report = profile.Summary();

        Assert.NotNull(report);
        Assert.Equal(3, report!.SlowFrames);
        Assert.Equal(3, report.WithoutColumn);
        Assert.Equal(0, report.Measured);
        Assert.Null(report.MedianWaitMs);

        // Absent is not zero: nothing here rules a blocked thread out.
        Assert.False(report.NoneWaited);
        Assert.Contains("bar kolumnen", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every hitch of the evening inside the band and none outside it is the sharpest gradient a session
    /// can produce, not a comparison that could not be made.
    /// </summary>
    /// <remarks>
    /// Both sides have frames here, so nothing is missing; the ratio is simply not finite. Reporting
    /// that as "den ena sidan saknar frames" said the opposite of what the minutes showed.
    /// </remarks>
    [Fact]
    public void AnEveningWhoseHitchesAreAllInTheBandSaysSo()
    {
        var monitor = new VramPressureBandMonitor(refreshRateHz: 60);
        Play(monitor, Start, minutes: 10, vramPercent: 70, hitchesPerMinute: 0);
        Play(monitor, Start.AddMinutes(10), minutes: 3, vramPercent: 92, hitchesPerMinute: 10);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(0d, report!.OutsideHitchesPerHour!.Value);
        Assert.Null(report.HitchRatio);
        Assert.DoesNotContain("saknar frames", report.Message, StringComparison.Ordinal);
        Assert.Contains("hitchade sessionen inte alls", report.Message, StringComparison.Ordinal);
    }

    /// <summary>The other direction: an evening whose large frames did wait must not be described as one that did not.</summary>
    [Fact]
    public void FramesThatWaitedAreCountedAsHavingWaited()
    {
        var profile = new SlowFrameWaitProfile();
        profile.Observe(Frame(Start, 262, cpuWaitMs: 240));
        profile.Observe(Frame(Start, 178, cpuWaitMs: 0.4));

        var report = profile.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report!.Waited);
        Assert.False(report.NoneWaited);
    }

    /// <summary>
    /// The engine ranked the card's memory highest in 26 of 119 incidents and was right about the
    /// evening before anybody looked. That verdict has to be readable without opening the jsonl.
    /// </summary>
    [Fact]
    public void TheVramVerdictGetsItsOwnLine()
    {
        var tally = new IncidentVerdictTally();

        for (var index = 0; index < 26; index++)
        {
            tally.Record(Guid.NewGuid(), RootCauseCategory.GpuVramPressure);
        }

        for (var index = 0; index < 93; index++)
        {
            tally.Record(Guid.NewGuid(), RootCauseCategory.FiveMResourceSpike);
        }

        var report = tally.Summary();

        Assert.NotNull(report);
        Assert.Equal(119, report!.Incidents);
        Assert.Equal(26, report.VramPressureIncidents);
        Assert.Contains("26 av 119", report.VramPressureMessage!, StringComparison.Ordinal);
        Assert.Contains("FiveMResourceSpike 93", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An incident re-analysed after its trace arrives has one verdict, not two. That is the ordinary
    /// path for every automatic capture.
    /// </summary>
    [Fact]
    public void AReanalysedIncidentIsCountedOnce()
    {
        var tally = new IncidentVerdictTally();
        var marker = Guid.NewGuid();

        tally.Record(marker, RootCauseCategory.FiveMResourceSpike);
        tally.Record(marker, RootCauseCategory.GpuVramPressure);

        var report = tally.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report!.Incidents);
        Assert.Equal(1, report.VramPressureIncidents);
    }

    /// <summary>
    /// Feeds one minute per minute of adapter readings and frames, at the given VRAM level and hitch
    /// rate. The frame times are the session's own cadence, so the hitch threshold settles at 33 ms.
    /// </summary>
    /// <param name="seconds">
    /// How long each "minute" of the fixture actually runs, so a stretch shorter than a minute can be
    /// played. Readings and frames stay at their per-second rate, which is what the monitor buckets.
    /// </param>
    private static void Play(
        VramPressureBandMonitor monitor,
        DateTimeOffset from,
        int minutes,
        double vramPercent,
        int hitchesPerMinute,
        int seconds = 60)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            var minuteStart = from.AddSeconds(minute * seconds);

            // Twice a second, the rate NVML is actually polled at. It matters at a crossing: a frame is
            // counted against the reading nearest it, so each crossing carries up to half a sampling
            // interval of frames over to the other side — a quarter of a second here, and five seconds
            // if the fixture pretended the card was read once every ten.
            for (var reading = 0; reading * 0.5 < seconds; reading++)
            {
                monitor.Observe(Adapter(minuteStart.AddSeconds(reading * 0.5), vramPercent));
            }

            // Frames that tile the time they are meant to cover, because the hitch rate is measured
            // against their own intervals: a fixture presenting one frame a second and calling it 16.7 ms
            // would describe a minute as a second of play.
            var at = minuteStart;
            var emitted = 0;
            while ((at - minuteStart).TotalSeconds < seconds)
            {
                var frameTimeMs = emitted < hitchesPerMinute ? 90 : 16.7;
                monitor.Observe(Frame(at, frameTimeMs, cpuWaitMs: 6));
                at = at.AddMilliseconds(frameTimeMs);
                emitted++;
            }
        }
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double vramPercent)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 60,
            MemoryBandwidthUtilizationPercent: 20,
            UsedVramBytes: (ulong)(Total * vramPercent / 100),
            TotalVramBytes: Total,
            EncoderUtilizationPercent: 12,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 60,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    private static FrameTelemetrySample Frame(DateTimeOffset timestamp, double frameTimeMs, double? cpuWaitMs)
    {
        return new FrameTelemetrySample(
            timestamp,
            frameTimeMs,
            GpuBusyMs: 5,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: Math.Max(frameTimeMs - (cpuWaitMs ?? 0), 0),
            CpuWaitMs: cpuWaitMs);
    }
}
