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
        Assert.Equal(13, report!.MeasuredMinutes);
        Assert.Equal(3, report.MinutesInBand);
        Assert.Equal(3, report.MinutesInDeepBand);
        Assert.True(report.IsPressured);

        // Ten an hour outside, six hundred inside.
        Assert.NotNull(report.HitchRatio);
        Assert.Equal(10, report.HitchRatio!.Value, 1);
        Assert.Contains("hitchfrekvensen", report.Message, StringComparison.Ordinal);
        Assert.Contains("3 av 13 minuter", report.Message, StringComparison.Ordinal);
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
    private static void Play(
        VramPressureBandMonitor monitor,
        DateTimeOffset from,
        int minutes,
        double vramPercent,
        int hitchesPerMinute)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            var minuteStart = from.AddMinutes(minute);

            for (var reading = 0; reading < 12; reading++)
            {
                monitor.Observe(Adapter(minuteStart.AddSeconds(reading * 5), vramPercent));
            }

            // Sixty frames a minute is enough to settle the cadence without making the fixture a
            // hundred thousand samples; the threshold only needs the median.
            for (var frame = 0; frame < 60; frame++)
            {
                var isHitch = frame < hitchesPerMinute;
                monitor.Observe(Frame(minuteStart.AddSeconds(frame), isHitch ? 90 : 16.7, cpuWaitMs: 6));
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
