namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The detector has to survive a six hour session in which roughly a thousand hitches occur, so the
/// interesting cases are not "does it fire" but "does it stay quiet when it should".
/// </summary>
public sealed class AutoIncidentDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 21, 20, 0, TimeSpan.Zero);

    [Fact]
    public void DoesNotFireBeforeBaselineIsEstablished()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions { MinimumSamples = 120 }, 60);

        // A huge frame in the first few samples is a level load, not a stutter.
        var trigger = Feed(detector, frameCount: 10, frameTimeMs: 16.7, finalFrameMs: 500);

        Assert.Null(trigger);
    }

    /// <summary>
    /// Played at the 45 ms cadence of 11 September, because the incident floor leaves no room for a
    /// Normal incident at 60 fps: a frame that clears 100 ms against a 16.7 ms baseline has already
    /// cleared four times it.
    /// </summary>
    [Fact]
    public void FiresOnASpikeOnceBaselineIsKnown()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        var trigger = Feed(detector, frameCount: 200, frameTimeMs: 45, finalFrameMs: 120);

        Assert.NotNull(trigger);
        Assert.Equal(IncidentSeverity.Normal, trigger!.Severity);
        Assert.Contains("120 ms", trigger.Label);
    }

    /// <summary>
    /// The floor, which is the whole of this change: a frame may be twice the baseline and still be
    /// nothing a player could point at.
    /// </summary>
    /// <remarks>
    /// 11 September produced 123 auto incidents with a median of 45 ms, 74 of them under 50 ms, and one
    /// of those was a 36 ms frame ruled <c>ExternalProcessInterference</c> against <c>SearchIndexer</c>
    /// at confidence 0.51. Twice a 16.9 ms baseline is 33.8 ms, so the multiplier alone called that a
    /// hitch worth a 90 second window and fourteen hypotheses.
    /// </remarks>
    [Fact]
    public void AFrameUnderTheFloorIsAHitchButNotAnIncident()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        var trigger = Feed(detector, frameCount: 200, frameTimeMs: 16.9, finalFrameMs: 36);

        Assert.Null(trigger);
        Assert.Equal(0, detector.TriggerCount);

        // Not discarded either — the session writes the count at the end, or an incident total that
        // fell by two orders of magnitude would read as the machine having got better.
        Assert.Equal(1, detector.HitchesBelowFloor);
    }

    /// <summary>Above the floor the multipliers decide, exactly as they did before it existed.</summary>
    [Fact]
    public void AboveTheFloorTheMultipliersStillDecideSeverity()
    {
        var normal = new AutoIncidentDetector(new AutoDetectOptions(), 60);
        var severe = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        var normalTrigger = Feed(normal, frameCount: 200, frameTimeMs: 45, finalFrameMs: 120);
        var severeTrigger = Feed(severe, frameCount: 200, frameTimeMs: 45, finalFrameMs: 180);

        Assert.Equal(IncidentSeverity.Normal, normalTrigger!.Severity);
        Assert.Equal(IncidentSeverity.Severe, severeTrigger!.Severity);
        Assert.Equal(0, normal.HitchesBelowFloor);
    }

    [Fact]
    public void EscalatesToSevereOnALongFreeze()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        var trigger = Feed(detector, frameCount: 200, frameTimeMs: 16.7, finalFrameMs: 120);

        Assert.NotNull(trigger);
        Assert.Equal(IncidentSeverity.Severe, trigger!.Severity);
    }

    /// <summary>
    /// The 60 fps cap on a 120 Hz panel seen in the field: the baseline must follow the cadence the
    /// machine achieves, otherwise every ordinary 16.7 ms frame reads as a 2x spike against 8.3 ms.
    /// </summary>
    [Fact]
    public void BaselineFollowsAchievedCadenceNotPanelRefresh()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), displayRefreshRateHz: 120);

        var trigger = Feed(detector, frameCount: 300, frameTimeMs: 16.7, finalFrameMs: 16.7);

        Assert.Null(trigger);
        Assert.InRange(detector.BaselineMs, 16.0, 17.5);
    }

    /// <summary>
    /// The cooldown still allows only one incident, but the frame it suppresses is now reported rather
    /// than discarded — the session manager folds it into the incident already open. Losing it entirely
    /// is how a 2 846 ms frame, the worst of its session, ended up recorded nowhere.
    /// </summary>
    [Fact]
    public void CooldownSuppressesTheSecondSpikeButStillReportsIt()
    {
        var detector = new AutoIncidentDetector(
            new AutoDetectOptions { Cooldown = TimeSpan.FromMinutes(2) },
            60);

        var timestamp = Start;
        AutoIncidentObservation? first = null;
        AutoIncidentObservation? second = null;

        for (var i = 0; i < 400; i++)
        {
            // Two spikes twenty seconds apart; only the first may raise an incident.
            var frameTimeMs = i is 200 or 250 ? 160 : 16.7;
            var result = detector.Observe(Frame(timestamp, frameTimeMs));
            if (i == 200)
            {
                first = result;
            }
            else if (i == 250)
            {
                second = result;
            }

            timestamp = timestamp.AddMilliseconds(400);
        }

        Assert.NotNull(first);
        Assert.False(first!.IsSuppressed);

        Assert.NotNull(second);
        Assert.True(second!.IsSuppressed);
        Assert.Equal(160, second.Trigger.FrameTimeMs, 1);

        // Suppressed observations must not spend the budget, or a burst would disarm the detector.
        Assert.Equal(1, detector.TriggerCount);
    }

    [Fact]
    public void StopsAfterTheWindowBudget()
    {
        var detector = new AutoIncidentDetector(
            new AutoDetectOptions
            {
                Cooldown = TimeSpan.Zero,
                MaxIncidentsPerWindow = 3,
                IncidentBudgetWindow = TimeSpan.FromHours(1),
            },
            60);

        var timestamp = Start;
        for (var i = 0; i < 200; i++)
        {
            detector.Observe(Frame(timestamp, 16.7));
            timestamp = timestamp.AddMilliseconds(16.7);
        }

        // 20 spikes half a minute apart, so all of them fall inside the same hour.
        for (var i = 0; i < 20; i++)
        {
            detector.Observe(Frame(timestamp, 200));
            timestamp = timestamp.AddSeconds(30);
        }

        Assert.Equal(3, detector.TriggerCount);
    }

    /// <summary>
    /// The failure a session-wide cap produced: a bad first hour spent the whole budget and the detector
    /// stayed disarmed for the rest of a four hour stream. The budget has to come back.
    /// </summary>
    [Fact]
    public void BudgetRefillsOnceTheWindowHasPassed()
    {
        var detector = new AutoIncidentDetector(
            new AutoDetectOptions
            {
                Cooldown = TimeSpan.Zero,
                MaxIncidentsPerWindow = 3,
                IncidentBudgetWindow = TimeSpan.FromHours(1),
            },
            60);

        var timestamp = Start;
        for (var i = 0; i < 200; i++)
        {
            detector.Observe(Frame(timestamp, 16.7));
            timestamp = timestamp.AddMilliseconds(16.7);
        }

        for (var i = 0; i < 10; i++)
        {
            detector.Observe(Frame(timestamp, 200));
            timestamp = timestamp.AddSeconds(30);
        }

        Assert.Equal(3, detector.TriggerCount);
        Assert.Equal(3, detector.TriggersInCurrentWindow);

        timestamp = timestamp.AddHours(2);
        var afterTheWindow = detector.Observe(Frame(timestamp, 200));

        Assert.NotNull(afterTheWindow);
        Assert.Equal(4, detector.TriggerCount);
        Assert.Equal(1, detector.TriggersInCurrentWindow);
    }

    /// <summary>A ceiling from a settings file written before the budget became time-windowed.</summary>
    [Fact]
    public void LegacySessionCeilingBecomesTheWindowBudget()
    {
        var options = new AutoDetectOptions { MaxIncidentsPerSession = 7 };

        options.Normalize();

        Assert.Equal(7, options.MaxIncidentsPerWindow);
        Assert.Null(options.MaxIncidentsPerSession);
    }

    [Fact]
    public void ARunOfUndisplayedFramesCountsAsAFreeze()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions { DroppedFrameRun = 3 }, 60);

        var timestamp = Start;
        for (var i = 0; i < 200; i++)
        {
            detector.Observe(Frame(timestamp, 16.7));
            timestamp = timestamp.AddMilliseconds(16.7);
        }

        // Frame times stay healthy — only the display path is stalled.
        AutoIncidentObservation? observation = null;
        for (var i = 0; i < 3; i++)
        {
            observation = detector.Observe(Frame(timestamp, 16.7, dropped: true));
            timestamp = timestamp.AddMilliseconds(16.7);
        }

        Assert.NotNull(observation);
        Assert.False(observation!.IsSuppressed);
        Assert.Contains("aldrig skärmen", observation.Trigger.Label);
    }

    [Fact]
    public void DisabledDetectorNeverFires()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions { Enabled = false }, 60);

        var trigger = Feed(detector, frameCount: 300, frameTimeMs: 16.7, finalFrameMs: 500);

        Assert.Null(trigger);
        Assert.Equal(0, detector.TriggerCount);
    }

    /// <summary>
    /// A 50 ms frame every 21st frame, as between 22:42 and 22:58 on 22 September: no frame reaches the
    /// floor, pacing averages 57–59 fps, and the minute is still the worst of the evening.
    /// </summary>
    [Fact]
    public void TwentyHitchesInsideAMinuteAreAnIncident()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        var triggers = FeedTrain(detector, hitches: AutoIncidentDetector.HitchSeriesPerMinute, framesBetween: 20);

        var series = Assert.Single(triggers);
        Assert.Equal(AutoIncidentKind.HitchSeries, series.Kind);
        Assert.Equal(0, series.FrameTimeMs);
        Assert.Contains("20 hitches på 7 s", series.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void NineteenHitchesInsideAMinuteAreNot()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        Assert.Empty(FeedTrain(detector, hitches: AutoIncidentDetector.HitchSeriesPerMinute - 1, framesBetween: 20));
    }

    /// <summary>Twenty hitches spread over more than a minute are an ordinary stretch of evening.</summary>
    [Fact]
    public void TwentyHitchesSpreadOverTwoMinutesAreNot()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        // One every 6.3 s: 20 of them span two minutes, and no rolling minute holds more than ten.
        Assert.Empty(FeedTrain(detector, hitches: AutoIncidentDetector.HitchSeriesPerMinute, framesBetween: 375));
    }

    /// <summary>
    /// A series is one observation however long it runs, and a new one once the minute has cleared.
    /// </summary>
    [Fact]
    public void ASeriesIsReportedOnceUntilTheMinuteClears()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);
        var observations = new List<AutoIncidentObservation>();
        var timestamp = Start;

        FeedTrain(detector, hitches: 100, framesBetween: 20, observations, ref timestamp);
        Assert.Single(observations, observation => observation.Trigger.Kind == AutoIncidentKind.HitchSeries);

        // Two quiet minutes, then the same timer again.
        for (var i = 0; i < 7200; i++)
        {
            detector.Observe(Frame(timestamp, 16.7));
            timestamp = timestamp.AddMilliseconds(16.7);
        }

        FeedTrain(detector, hitches: 20, framesBetween: 20, observations, ref timestamp);
        Assert.Equal(2, observations.Count(observation => observation.Trigger.Kind == AutoIncidentKind.HitchSeries));
    }

    /// <summary>A frame over the floor is an incident of its own and still one of the series' hitches.</summary>
    [Fact]
    public void AHitchOverTheFloorCountsTowardsTheSeries()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);
        var observations = new List<AutoIncidentObservation>();
        var timestamp = Start;

        FeedTrain(detector, hitches: 1, framesBetween: 20, observations, ref timestamp, hitchMs: 150);
        FeedTrain(detector, hitches: AutoIncidentDetector.HitchSeriesPerMinute - 1, framesBetween: 20, observations, ref timestamp);

        Assert.Contains(observations, observation => observation.Trigger.Kind == AutoIncidentKind.HitchSeries);
    }

    /// <summary>
    /// Settles the baseline, then feeds <paramref name="hitches"/> 50 ms frames with ordinary frames
    /// between them, and returns every trigger the detector acted on.
    /// </summary>
    private static List<AutoIncidentTrigger> FeedTrain(AutoIncidentDetector detector, int hitches, int framesBetween)
    {
        var observations = new List<AutoIncidentObservation>();
        var timestamp = Start;
        FeedTrain(detector, hitches, framesBetween, observations, ref timestamp);

        return observations.Where(observation => !observation.IsSuppressed).Select(observation => observation.Trigger).ToList();
    }

    /// <summary>
    /// Settles the baseline on the first call, then feeds <paramref name="hitches"/> frames of
    /// <paramref name="hitchMs"/> with ordinary frames between them, collecting every observation.
    /// </summary>
    private static void FeedTrain(
        AutoIncidentDetector detector,
        int hitches,
        int framesBetween,
        List<AutoIncidentObservation> observations,
        ref DateTimeOffset timestamp,
        double hitchMs = 50)
    {
        var at = timestamp;

        void Present(double frameTimeMs)
        {
            if (detector.Observe(Frame(at, frameTimeMs)) is { } observation)
            {
                observations.Add(observation);
            }

            at = at.AddMilliseconds(frameTimeMs);
        }

        if (at == Start)
        {
            for (var i = 0; i < 200; i++)
            {
                Present(16.7);
            }
        }

        for (var hitch = 0; hitch < hitches; hitch++)
        {
            Present(hitchMs);
            for (var i = 0; i < framesBetween; i++)
            {
                Present(16.7);
            }
        }

        timestamp = at;
    }

    /// <summary>
    /// Returns the trigger the detector acted on, so a suppressed observation reads as "nothing fired"
    /// here exactly as a null did before <see cref="AutoIncidentObservation"/> existed. Tests that care
    /// about suppression call <see cref="AutoIncidentDetector.Observe"/> directly.
    /// </summary>
    private static AutoIncidentTrigger? Feed(AutoIncidentDetector detector, int frameCount, double frameTimeMs, double finalFrameMs)
    {
        var timestamp = Start;
        AutoIncidentObservation? last = null;

        for (var i = 0; i < frameCount; i++)
        {
            var value = i == frameCount - 1 ? finalFrameMs : frameTimeMs;
            last = detector.Observe(Frame(timestamp, value));
            timestamp = timestamp.AddMilliseconds(value);
        }

        return last is { IsSuppressed: false } ? last.Trigger : null;
    }

    private static FrameTelemetrySample Frame(DateTimeOffset timestamp, double frameTimeMs, bool dropped = false)
    {
        return new FrameTelemetrySample(
            timestamp,
            frameTimeMs,
            GpuBusyMs: 8,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: dropped,
            ProcessName: "FiveM_b3407_GTAProcess.exe");
    }
}
