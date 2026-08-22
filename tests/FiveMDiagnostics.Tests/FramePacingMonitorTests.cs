namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The monitor exists because the spike detector went blind in a sustained bad patch, so the cases that
/// matter are the ones a relative threshold gets wrong: a deliberate frame rate cap that is being met,
/// and a slow degradation that never produces a spike at all.
/// </summary>
public sealed class FramePacingMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 21, 21, 42, 0, TimeSpan.Zero);

    /// <summary>
    /// The field configuration: 60 fps cap on a 120 Hz panel. Frame time is double the refresh
    /// interval by design, and the cap is realised as several milliseconds of wait per frame.
    /// </summary>
    [Fact]
    public void CappedFrameRateOnAFasterDisplayIsHealthy()
    {
        var session = new Session(Options()).Feed(minutes: 3, frameTimeMs: 16.67, cpuWaitMs: 7.7).Flush();

        Assert.Equal(3, session.Windows.Count);
        Assert.All(session.Windows, window => Assert.Equal(FramePacingState.Healthy, window.State));
        Assert.InRange(session.Summary.TargetFps, 59.5, 60.5);
    }

    /// <summary>
    /// The session this was written for: frame time rises from 16.7 to 20.5 ms and the wait collapses
    /// to nothing. No single frame is a spike, and the frame rate is 20 % down for half an hour.
    /// </summary>
    [Fact]
    public void CollapsedCpuWaitWithWorseCadenceIsSaturated()
    {
        var session = new Session(Options())
            .Feed(minutes: 3, frameTimeMs: 16.67, cpuWaitMs: 7.7)
            .Feed(minutes: 3, frameTimeMs: 20.5, cpuWaitMs: 0.14)
            .Flush();

        var healthy = session.Windows.Take(3).ToArray();
        var degraded = session.Windows.Skip(3).ToArray();

        Assert.All(healthy, window => Assert.Equal(FramePacingState.Healthy, window.State));
        Assert.NotEmpty(degraded);
        Assert.All(degraded, window => Assert.Equal(FramePacingState.Saturated, window.State));
        Assert.True(degraded[0].IsTransition);
        Assert.Equal(2, degraded[1].SustainedWindows);
        Assert.InRange(degraded[0].AchievedFps, 46, 50);
    }

    /// <summary>
    /// An uncapped game is CPU-bound by definition and has no wait to speak of. Without the cadence
    /// guard the collapsed wait alone would call a perfectly healthy 200 fps session saturated.
    /// </summary>
    [Fact]
    public void UncappedFrameRateWithNoWaitIsNotSaturated()
    {
        var session = new Session(Options()).Feed(minutes: 3, frameTimeMs: 5.0, cpuWaitMs: 0.05).Flush();

        Assert.NotEmpty(session.Windows);
        Assert.All(session.Windows, window => Assert.NotEqual(FramePacingState.Saturated, window.State));
    }

    /// <summary>
    /// The failure that made this class necessary: a rolling baseline follows the damage upwards and
    /// stops seeing it. The target here may only ratchet down, so half an hour of degradation cannot
    /// become the new normal.
    /// </summary>
    [Fact]
    public void SustainedDegradationDoesNotBecomeTheNewTarget()
    {
        var session = new Session(Options())
            .Feed(minutes: 2, frameTimeMs: 16.67, cpuWaitMs: 7.7)
            .Feed(minutes: 30, frameTimeMs: 21.0, cpuWaitMs: 0.14)
            .Flush();

        Assert.All(session.Windows.Skip(2), window => Assert.Equal(FramePacingState.Saturated, window.State));
        Assert.Equal(30, session.Summary.SaturatedWindows);
        Assert.Equal(30, session.Summary.LongestSaturatedRun);
        Assert.InRange(session.Summary.TargetFps, 59.5, 60.5);
    }

    /// <summary>
    /// PresentMon 1.x carries no CPU/GPU breakdown, so cadence is the only signal. It is weaker, and
    /// the threshold it has to clear is correspondingly further from the target.
    /// </summary>
    [Fact]
    public void FallsBackToCadenceWhenTheBreakdownIsMissing()
    {
        var session = new Session(Options())
            .Feed(minutes: 2, frameTimeMs: 16.67, cpuWaitMs: null)
            .Feed(minutes: 2, frameTimeMs: 18.5, cpuWaitMs: null)
            .Feed(minutes: 2, frameTimeMs: 25.0, cpuWaitMs: null)
            .Flush();

        Assert.Equal(FramePacingState.Healthy, session.Windows[0].State);
        Assert.Equal(FramePacingState.Marginal, session.Windows[2].State);
        Assert.Equal(FramePacingState.Saturated, session.Windows[4].State);
    }

    /// <summary>A loading screen or an alt-tab is not a verdict about the machine.</summary>
    [Fact]
    public void WindowWithTooFewFramesIsNotClassified()
    {
        var session = new Session(Options())
            .FeedSparse(frames: 20, spacing: TimeSpan.FromSeconds(12), frameTimeMs: 16.67, cpuWaitMs: 7.7);

        Assert.NotEmpty(session.Windows);
        Assert.All(session.Windows, window => Assert.Equal(FramePacingState.Unknown, window.State));
        Assert.Equal(0, session.Summary.TotalWindows);
    }

    [Fact]
    public void SummaryCountsEachStateSeparately()
    {
        var session = new Session(Options())
            .Feed(minutes: 4, frameTimeMs: 16.67, cpuWaitMs: 7.7)
            .Feed(minutes: 2, frameTimeMs: 20.5, cpuWaitMs: 0.14)
            .Feed(minutes: 1, frameTimeMs: 17.4, cpuWaitMs: 2.0)
            .Flush();

        var summary = session.Summary;

        Assert.Equal(7, summary.TotalWindows);
        Assert.Equal(4, summary.HealthyWindows);
        Assert.Equal(2, summary.SaturatedWindows);
        Assert.Equal(1, summary.MarginalWindows);
        Assert.InRange(summary.SaturatedShare, 0.28, 0.29);
        Assert.Contains("2 av 7", summary.Describe(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void DisabledMonitorClassifiesNothing()
    {
        var session = new Session(Options() with { Enabled = false })
            .Feed(minutes: 3, frameTimeMs: 20.5, cpuWaitMs: 0.14)
            .Flush();

        Assert.Empty(session.Windows);
        Assert.Equal(0, session.Summary.TotalWindows);
    }

    /// <summary>
    /// A PresentMon restart leaves a hole no frame describes. Judging the window anyway reports the
    /// missing seconds as a frame rate the game never had.
    /// </summary>
    [Fact]
    public void WindowWithACaptureGapIsNotClassified()
    {
        var session = new Session(Options() with { MinimumFrames = 600 })
            .Feed(minutes: 2, frameTimeMs: 16.67, cpuWaitMs: 7.7)
            .Gap(TimeSpan.FromSeconds(40))
            .Feed(minutes: 2, frameTimeMs: 16.67, cpuWaitMs: 7.7)
            .Flush();

        Assert.Contains(session.Windows, window => window.State == FramePacingState.Unknown);
        Assert.All(
            session.Windows.Where(window => window.State != FramePacingState.Unknown),
            window => Assert.Equal(FramePacingState.Healthy, window.State));
    }

    /// <summary>
    /// A freeze is not a gap. The interval that contains it is one enormous frame time, and the window
    /// still describes the machine — dropping it would discard the worst minutes of a session.
    /// </summary>
    [Fact]
    public void WindowContainingALongFreezeIsStillClassified()
    {
        var session = new Session(Options() with { MinimumFrames = 600 })
            .Feed(minutes: 1, frameTimeMs: 16.67, cpuWaitMs: 7.7)
            .Freeze(TimeSpan.FromSeconds(3))
            .Feed(minutes: 1, frameTimeMs: 16.67, cpuWaitMs: 7.7)
            .Flush();

        Assert.DoesNotContain(session.Windows, window => window.State == FramePacingState.Unknown);
    }

    private static FramePacingOptions Options()
    {
        return new FramePacingOptions
        {
            WindowLength = TimeSpan.FromMinutes(1),
            MinimumFrames = 600,
        };
    }

    /// <summary>
    /// Feeds one continuous stream of frames into a monitor and collects every window that closed.
    /// </summary>
    /// <remarks>
    /// Continuity is the point. A window is closed by the first frame that runs past its end, so
    /// restarting the clock between phases would hand the next phase a window still holding the
    /// previous phase's frames — which is exactly the kind of boundary the monitor has to get right,
    /// and exactly the kind of thing a test that fakes the clock can accidentally hide.
    /// </remarks>
    private sealed class Session
    {
        private readonly FramePacingMonitor _monitor;
        private DateTimeOffset _now = Start;

        public Session(FramePacingOptions options, double? refreshRateHz = 120)
        {
            _monitor = new FramePacingMonitor(options, refreshRateHz);
        }

        public List<FramePacingWindow> Windows { get; } = [];

        public FramePacingSummary Summary => _monitor.Summary;

        public Session Feed(int minutes, double frameTimeMs, double? cpuWaitMs)
        {
            var end = _now.AddMinutes(minutes);
            while (_now < end)
            {
                if (_monitor.Observe(Frame(_now, frameTimeMs, cpuWaitMs)) is { } window)
                {
                    Windows.Add(window);
                }

                _now = _now.AddMilliseconds(frameTimeMs);
            }

            return this;
        }

        public Session FeedSparse(int frames, TimeSpan spacing, double frameTimeMs, double? cpuWaitMs)
        {
            for (var index = 0; index < frames; index++)
            {
                if (_monitor.Observe(Frame(_now, frameTimeMs, cpuWaitMs)) is { } window)
                {
                    Windows.Add(window);
                }

                _now = _now.Add(spacing);
            }

            return this;
        }

        /// <summary>Advances the clock without emitting frames, as a stopped capture would.</summary>
        public Session Gap(TimeSpan length)
        {
            _now = _now.Add(length);
            return this;
        }

        /// <summary>
        /// Emits one frame whose interval covers the whole freeze, as PresentMon does when the game
        /// stalls but the capture keeps running.
        /// </summary>
        public Session Freeze(TimeSpan length)
        {
            _now = _now.Add(length);
            if (_monitor.Observe(Frame(_now, length.TotalMilliseconds, 0)) is { } window)
            {
                Windows.Add(window);
            }

            return this;
        }

        public Session Flush()
        {
            if (_monitor.Flush() is { } window)
            {
                Windows.Add(window);
            }

            return this;
        }
    }

    private static FrameTelemetrySample Frame(DateTimeOffset timestamp, double frameTimeMs, double? cpuWaitMs)
    {
        return new FrameTelemetrySample(
            timestamp,
            frameTimeMs,
            GpuBusyMs: 7.5,
            DisplayLatencyMs: null,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuWaitMs is { } wait ? Math.Max(frameTimeMs - wait, 0) : null,
            CpuWaitMs: cpuWaitMs);
    }
}
