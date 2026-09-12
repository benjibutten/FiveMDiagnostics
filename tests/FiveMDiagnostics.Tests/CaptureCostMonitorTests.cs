namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// What the instrument costs the thing it is measuring, which the app had never reported.
/// </summary>
/// <remarks>
/// Counted by hand on the 29 August session: ten captures, and the minute after each flush held hitches
/// at four times the rate of the rest of the evening — 222 against 80 per hour at the 33 ms threshold.
/// The large ones were untouched at 6.0 against 6.4 per hour, so the flush is not what causes freezes;
/// it is what makes a well-captured evening look slightly worse than a poorly captured one.
/// </remarks>
public sealed class CaptureCostMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 20, 46, 57, TimeSpan.Zero);

    [Fact]
    public void HitchesAfterAFlushAreSeparatedFromTheRest()
    {
        var monitor = new CaptureCostMonitor(new HitchThreshold(60));

        // One flush, half an hour in. Recorded before the frames it affects, as it is live: the capture
        // finishes writing and the frames after it are then observed against it.
        monitor.RecordCaptureWritten(Start.AddMinutes(30));

        // An hour of play with a hitch every two minutes, and four more inside the minute after the flush.
        for (var minute = 0; minute < 60; minute++)
        {
            monitor.Observe(Frame(Start.AddMinutes(minute), 16.7));
            if (minute % 2 == 0)
            {
                monitor.Observe(Frame(Start.AddMinutes(minute).AddSeconds(30), 40));
            }

            if (minute == 30)
            {
                for (var index = 0; index < 4; index++)
                {
                    monitor.Observe(Frame(Start.AddMinutes(30).AddSeconds(5 + index * 10), 40));
                }
            }
        }

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report!.CaptureCount);

        // The four, plus the ordinary one that happens to fall in the same minute.
        Assert.Equal(5, report.HitchesNearCapture);
        Assert.True(
            report.NearCaptureHitchesPerHour > report.ElsewhereHitchesPerHour * 4,
            $"{report.NearCaptureHitchesPerHour:F0}/h near a flush against {report.ElsewhereHitchesPerHour:F0}/h elsewhere");
    }

    /// <summary>A session that took no captures has nothing to report, and must not print a line saying so.</summary>
    [Fact]
    public void ASessionWithoutCapturesReportsNothing()
    {
        var monitor = new CaptureCostMonitor(new HitchThreshold(60));
        for (var minute = 0; minute < 60; minute++)
        {
            monitor.Observe(Frame(Start.AddMinutes(minute), 40));
        }

        Assert.Null(monitor.Summary());
    }

    /// <summary>
    /// A hitch is two frame intervals rather than a fixed number of milliseconds, and the interval is
    /// the one the session actually holds — not the one the display is capable of.
    /// </summary>
    /// <remarks>
    /// Two refreshes of the panel is the right threshold only when the game runs at the panel's rate. On
    /// a 120 Hz display with the game capped to 60 fps it lands at 16.67 ms, which is the cadence
    /// itself, so every frame of a perfectly smooth evening counted as a hitch and the line reported two
    /// indistinguishable five-figure rates.
    /// </remarks>
    [Fact]
    public void TheHitchThresholdFollowsTheCadenceTheSessionHolds()
    {
        var uncappedHitch = new HitchThreshold(120);
        var cappedHitch = new HitchThreshold(120);
        var uncapped = new CaptureCostMonitor(uncappedHitch);
        var cappedToSixty = new CaptureCostMonitor(cappedHitch);

        uncapped.RecordCaptureWritten(Start);
        cappedToSixty.RecordCaptureWritten(Start);

        // An hour on the same 120 Hz panel, with a 20 ms frame once a minute in both sessions. The bar
        // is fed the same frames the session feeds it, which is what makes the two sessions differ.
        for (var second = 0; second < 3600; second++)
        {
            var at = Start.AddSeconds(second);
            var slow = second % 60 == 0;
            uncappedHitch.Observe(slow ? 20 : 8.3);
            cappedHitch.Observe(slow ? 20 : 16.7);
            uncapped.Observe(Frame(at, slow ? 20 : 8.3));
            cappedToSixty.Observe(Frame(at, slow ? 20 : 16.7));
        }

        // Running at the panel's rate, 20 ms is two refreshes and over.
        var report = uncapped.Summary();
        Assert.NotNull(report);
        Assert.Equal(16.7, report!.HitchThresholdMs, 1);
        Assert.Equal(60, report.Hitches);

        // Capped to half of it, 20 ms is the cadence and a little jitter.
        Assert.Null(cappedToSixty.Summary());
    }

    /// <summary>
    /// The flush is timed on the task that took the capture while the frames arrive on the telemetry
    /// pump, so the two collections genuinely meet. Adding to the capture list during the loop over it
    /// threw out of the pump loop and stopped the session's telemetry for the rest of the evening.
    /// </summary>
    [Fact]
    public async Task CapturesRecordedWhileFramesArriveDoNotThrow()
    {
        var monitor = new CaptureCostMonitor(new HitchThreshold(60));

        // One frame in four is a hitch, which is what makes an observation walk the capture list at all.
        var frames = Task.Run(() =>
        {
            for (var index = 0; index < 20_000; index++)
            {
                monitor.Observe(Frame(Start.AddMilliseconds(index * 100), index % 4 == 0 ? 40 : 16.7));
            }
        });

        var captures = Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                monitor.RecordCaptureWritten(Start.AddMilliseconds(index * 100));
            }
        });

        await Task.WhenAll(frames, captures);

        // The assertion is that neither task threw, and that the summary can still be read afterwards.
        _ = monitor.Summary();
    }

    /// <summary>
    /// A session so short that every frame sits inside a capture window has no comparison to make, and
    /// reporting a rate against a denominator of zero would be worse than saying nothing.
    /// </summary>
    [Fact]
    public void ASessionEntirelyInsideCaptureWindowsReportsNothing()
    {
        var monitor = new CaptureCostMonitor(new HitchThreshold(60));
        monitor.RecordCaptureWritten(Start);

        for (var index = 0; index < 30; index++)
        {
            monitor.Observe(Frame(Start.AddSeconds(index), index % 5 == 0 ? 40 : 16.7));
        }

        Assert.Null(monitor.Summary());
    }


    /// <summary>
    /// The evening of 10 September, where the game crashed and relaunched mid-session and two of the
    /// four captures were taken in the minutes around the reload.
    /// </summary>
    /// <remarks>
    /// The line reported 75 hitches an hour near a capture against 39 in the rest of the session — an
    /// overstatement of what the instrument costs, built almost entirely out of a game loading. Loading
    /// hitches for reasons of its own and belongs in neither side of the comparison.
    /// </remarks>
    [Fact]
    public void HitchesWhileTheGameIsLoadingCountAgainstNeitherSide()
    {
        var withLoadingKnown = Evening(noteGameStart: true);
        var asItWasReported = Evening(noteGameStart: false);

        Assert.NotNull(withLoadingKnown);
        Assert.NotNull(asItWasReported);

        // The reload's hitches are set aside, and the line says so rather than dropping them silently.
        Assert.Equal(240, withLoadingKnown.HitchesWhileLoading);
        Assert.Equal(40, withLoadingKnown.LoadingTime.TotalMinutes, 0);
        Assert.Contains("borträknade ur båda sidorna", withLoadingKnown.Message, StringComparison.Ordinal);

        // Both sides were carrying the reload before. The capture taken two minutes into it was charged
        // for the loading hitches around it, and the rest of the session's rate was raised by the other
        // thirty-eight minutes of them — which is how an evening reports a hitch rate no part of it had.
        Assert.True(
            asItWasReported.HitchesNearCapture > withLoadingKnown.HitchesNearCapture,
            "the capture taken during the reload should no longer be charged for it");

        // Both hours of play held sixty hitches an hour; the reload held three hundred and sixty. The
        // old reading reported a session rate no part of the evening ever had.
        Assert.InRange(withLoadingKnown.ElsewhereHitchesPerHour, 50, 70);
        Assert.True(
            asItWasReported.ElsewhereHitchesPerHour > 120,
            $"the reload inflated the session rate to {asItWasReported.ElsewhereHitchesPerHour:F0}/h "
            + $"against {withLoadingKnown.ElsewhereHitchesPerHour:F0}/h once it is set aside");
    }

    /// <summary>
    /// An hour of play, a forty-minute reload with a capture two minutes into it, and an hour of play
    /// with a capture in the middle.
    /// </summary>
    private static CaptureCostReport? Evening(bool noteGameStart)
    {
        var monitor = new CaptureCostMonitor(new HitchThreshold(60));

        if (noteGameStart)
        {
            monitor.NoteGameStart(Start.AddMinutes(60));
        }

        monitor.RecordCaptureWritten(Start.AddMinutes(62));
        monitor.RecordCaptureWritten(Start.AddMinutes(130));

        Play(monitor, fromMinute: 0, minutes: 60, hitchesPerMinute: 1);
        Play(monitor, fromMinute: 60, minutes: 40, hitchesPerMinute: 6);
        Play(monitor, fromMinute: 100, minutes: 60, hitchesPerMinute: 1);

        return monitor.Summary();
    }


    /// <summary>Frames at the cadence the session actually holds, with hitches sprinkled through them.</summary>
    private static void Play(CaptureCostMonitor monitor, int fromMinute, int minutes, int hitchesPerMinute)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            var at = Start.AddMinutes(fromMinute + minute);

            // Thirty presents a minute is enough to fix the median at the cadence without making the
            // test a benchmark.
            for (var frame = 0; frame < 30; frame++)
            {
                monitor.Observe(Frame(at.AddSeconds(frame * 2), 16.7));
            }

            for (var hitch = 0; hitch < hitchesPerMinute; hitch++)
            {
                monitor.Observe(Frame(at.AddSeconds(1 + hitch * 8), 40));
            }
        }
    }

    private static FrameTelemetrySample Frame(DateTimeOffset at, double frameTimeMs)
    {
        return new FrameTelemetrySample(
            at,
            frameTimeMs,
            GpuBusyMs: 7.7,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe");
    }
}
