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

    [Fact]
    public void FiresOnASpikeOnceBaselineIsKnown()
    {
        var detector = new AutoIncidentDetector(new AutoDetectOptions(), 60);

        var trigger = Feed(detector, frameCount: 200, frameTimeMs: 16.7, finalFrameMs: 40);

        Assert.NotNull(trigger);
        Assert.Equal(IncidentSeverity.Normal, trigger!.Severity);
        Assert.Contains("40 ms", trigger.Label);
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
            var frameTimeMs = i is 200 or 250 ? 60 : 16.7;
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
        Assert.Equal(60, second.Trigger.FrameTimeMs, 1);

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
