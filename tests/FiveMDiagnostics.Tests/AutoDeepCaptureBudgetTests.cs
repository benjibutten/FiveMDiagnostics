namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The budget is what makes automatic deep capture safe enough to allow at all, so the cases that
/// matter are the refusals rather than the grants.
/// </summary>
public sealed class AutoDeepCaptureBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

    private static DeepCaptureOptions Options(Action<DeepCaptureOptions>? configure = null)
    {
        var options = new DeepCaptureOptions();
        configure?.Invoke(options);
        options.Normalize();
        return options;
    }

    /// <summary>
    /// Options with the window budget opened up, for the tests whose subject is the cooldown.
    /// </summary>
    /// <remarks>
    /// The window budget refuses a fourth ordinary frame in an hour before the cooldown gets to it. That
    /// is the intended order — the window is the tighter gate over an hour — but it makes the cooldown
    /// awkward to test on its own, and a test that cannot fail when the cooldown breaks is not testing
    /// the cooldown.
    /// </remarks>
    private static DeepCaptureOptions CooldownOnlyOptions(Action<DeepCaptureOptions>? configure = null)
    {
        return Options(item =>
        {
            item.MaxAutoCapturesPerWindow = 100;
            configure?.Invoke(item);
        });
    }

    [Fact]
    public void OrdinaryHitchesDoNotSpendACapture()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        // 2 125 frames in the 26 August session were over 33 ms and 500 over 50 ms. If either could
        // spend a capture the budget would be gone within minutes of the session starting.
        Assert.False(budget.TryReserve(Start, frameTimeMs: 40, out var refusal));
        Assert.False(budget.TryReserve(Start, frameTimeMs: 80, out _));
        Assert.False(budget.TryReserve(Start, frameTimeMs: 119, out _));

        // Silently, too: a reason on every ordinary spike would bury the two refusals worth reading.
        Assert.Null(refusal);
        Assert.Equal(0, budget.Spent);
    }

    [Fact]
    public void ACatastrophicFrameSpendsACapture()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserve(Start, frameTimeMs: 1018, out var refusal));

        Assert.Null(refusal);
        Assert.Equal(1, budget.Spent);
        Assert.Equal(11, budget.Remaining);
    }

    [Fact]
    public void DroppedFrameRunSkipsOnlyTheFrameTimeGate()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserveForDroppedFrameRun(Start, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(1, budget.Spent);

        Assert.False(budget.TryReserveForDroppedFrameRun(Start.AddSeconds(10), out refusal));
        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, budget.Spent);
    }

    /// <summary>
    /// 41% of the hitches over 100 ms in the reference session arrived within five seconds of another,
    /// and the worst cluster held 23 of them across two minutes. A burst is one event worth one trace.
    /// </summary>
    /// <remarks>
    /// Shaped like a real cluster rather than a uniform one: the clusters measured are mostly frames of
    /// 100–250 ms carrying one or two catastrophic ones, which is why the 300 ms threshold does most of
    /// the work here and the cooldown only has to collapse what gets past it. The two large frames sit
    /// 25 s apart because that is the measured spacing of the pair that closed the 25 August session —
    /// 384 ms at 01:21:04 and 778 ms at 01:21:29.
    /// </remarks>
    [Fact]
    public void ABurstSpendsOneCaptureNotTwenty()
    {
        var budget = new AutoDeepCaptureBudget(Options());
        var timestamp = Start;

        for (var i = 0; i < 23; i++)
        {
            budget.TryReserve(timestamp, frameTimeMs: i is 4 or 9 ? 606 : 180, out _);
            timestamp = timestamp.AddSeconds(5);
        }

        Assert.Equal(1, budget.Spent);
    }

    [Fact]
    public void TheCooldownExpiresAndTheNextEventIsCaptured()
    {
        var options = CooldownOnlyOptions(item => item.AutoCaptureCooldown = TimeSpan.FromMinutes(10));
        var budget = new AutoDeepCaptureBudget(options);

        // Below the override threshold of 250 ms, so this is the ordinary cooldown being tested rather
        // than the way past it.
        Assert.True(budget.TryReserve(Start, 200, out _));
        Assert.False(budget.TryReserve(Start.AddMinutes(9), 200, out var refusal));
        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);

        Assert.True(budget.TryReserve(Start.AddMinutes(11), 200, out _));
        Assert.Equal(2, budget.Spent);
    }

    /// <summary>
    /// The case the cooldown got wrong. On 25 August a 518 ms frame was refused with seven minutes of
    /// cooldown left, while the session still held an unspent capture and finished with one in hand. It
    /// is the third largest frame of that evening and it exists in no trace.
    /// </summary>
    [Fact]
    public void AFrameFarBeyondTheThresholdBreaksTheCooldown()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserve(Start, frameTimeMs: 1540, out _));

        // 2 min 45 s later — the real spacing between those two frames.
        Assert.True(budget.TryReserve(Start.AddSeconds(165), frameTimeMs: 518, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(2, budget.Spent);
    }

    /// <summary>
    /// What the override may not skip. A capture takes 28–32 s from trigger to written, and the ring
    /// buffer then needs about twenty more to refill, so a capture started seconds after one records a
    /// buffer holding almost no history — the exact failure the whole gate exists to prevent.
    /// </summary>
    /// <remarks>
    /// Measured on the frame that prompted it: 1 327 ms, six seconds after a capture had been triggered.
    /// Refusing it was right for a second reason too — the trace that trigger produced turned out to
    /// contain the frame anyway.
    /// </remarks>
    [Fact]
    public void TheOverrideStillWaitsForTheRingBufferToRefill()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserve(Start, frameTimeMs: 374, out _));
        Assert.False(budget.TryReserve(Start.AddSeconds(6), frameTimeMs: 1327, out var refusal));

        Assert.Contains("ringbufferten", refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, budget.Spent);
    }

    /// <summary>
    /// A burst that stays catastrophic for minutes is not the five-second cluster the cooldown was
    /// written for, and a second trace of it is worth having. The ceiling is what bounds the cost.
    /// </summary>
    [Fact]
    public void ASustainedCatastropheMaySpendASecondCaptureOnceTheBufferHasRefilled()
    {
        var budget = new AutoDeepCaptureBudget(Options());
        var timestamp = Start;

        for (var i = 0; i < 40; i++)
        {
            budget.TryReserve(timestamp, frameTimeMs: 900, out _);
            timestamp = timestamp.AddSeconds(5);
        }

        // Three and a bit minutes of unbroken 900 ms frames: one capture per override cooldown, not one
        // per frame, and nowhere near the session ceiling of twelve.
        Assert.Equal(4, budget.Spent);
    }

    /// <summary>
    /// Saturation has no frame to be catastrophic, so it can never take the shorter path. One stretch of
    /// unrecoverable frame rate is one event however long it runs.
    /// </summary>
    [Fact]
    public void SustainedSaturationCannotBreakTheCooldown()
    {
        var budget = new AutoDeepCaptureBudget(CooldownOnlyOptions());

        Assert.True(budget.TryReserveForSustainedSaturation(Start, out _));
        Assert.False(budget.TryReserveForSustainedSaturation(Start.AddMinutes(2), out var refusal));

        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, budget.Spent);
    }

    [Fact]
    public void TheSessionCeilingIsHardAndSaysSo()
    {
        var options = Options(item =>
        {
            item.MaxAutoCapturesPerSession = 2;
            item.AutoCaptureCooldown = TimeSpan.FromSeconds(30);
        });
        var budget = new AutoDeepCaptureBudget(options);
        var timestamp = Start;

        for (var i = 0; i < 5; i++)
        {
            budget.TryReserve(timestamp, frameTimeMs: 900, out _);
            timestamp = timestamp.AddHours(1);
        }

        Assert.Equal(2, budget.Spent);
        Assert.Equal(0, budget.Remaining);

        Assert.False(budget.TryReserve(timestamp, 900, out var refusal));
        Assert.Contains("budget", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurningAutoCaptureOffRestoresTheOldBehaviour()
    {
        var budget = new AutoDeepCaptureBudget(Options(item => item.CaptureAutoIncidents = false));

        Assert.False(budget.TryReserve(Start, frameTimeMs: 3000, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(0, budget.Spent);
    }

    [Fact]
    public void DeepCaptureBeingDisabledOutranksTheBudget()
    {
        var budget = new AutoDeepCaptureBudget(Options(item => item.Enabled = false));

        Assert.False(budget.TryReserve(Start, frameTimeMs: 3000, out _));
    }

    /// <summary>
    /// The threshold and cooldown come from a JSON file the user can edit, and a zero in either would
    /// turn every incident into a WPR flush for a whole stream.
    /// </summary>
    [Fact]
    public void DegenerateSettingsAreClamped()
    {
        var options = Options(item =>
        {
            item.AutoCaptureFrameTimeMs = 0;
            item.AutoCaptureCooldown = TimeSpan.Zero;
            item.MaxAutoCapturesPerSession = -5;
        });

        Assert.Equal(120, options.AutoCaptureFrameTimeMs);
        Assert.Equal(TimeSpan.FromSeconds(30), options.AutoCaptureCooldown);
        Assert.Equal(0, options.MaxAutoCapturesPerSession);

        // The override cooldown can never outlast the cooldown it is a way past, or it would be dead
        // code that reads like a feature.
        Assert.True(options.AutoCaptureOverrideCooldown <= options.AutoCaptureCooldown);
    }

    /// <summary>
    /// An override threshold below the ordinary one would make the cooldown unreachable, turning every
    /// capture-worthy frame in a burst into a capture — the behaviour the cooldown exists to prevent.
    /// </summary>
    /// <remarks>
    /// The second reservation has to be attempted after the override cooldown and before the ordinary
    /// one, or the test passes whether the bug is present or not. Clamping leaves the two thresholds
    /// equal, and equality has to mean "no override" rather than "override on everything".
    /// </remarks>
    [Fact]
    public void TheOverrideThresholdCannotUndercutTheOrdinaryOne()
    {
        var options = CooldownOnlyOptions(item =>
        {
            item.AutoCaptureFrameTimeMs = 400;
            item.AutoCaptureOverrideFrameTimeMs = 120;
        });

        Assert.Equal(400, options.AutoCaptureOverrideFrameTimeMs);
        Assert.False(options.OverridesCooldownFor(450));
        Assert.False(options.OverridesCooldownFor(9000));

        var budget = new AutoDeepCaptureBudget(options);
        Assert.True(budget.TryReserve(Start, frameTimeMs: 450, out _));

        // Two minutes: past the 60 s override spacing, well inside the ten minute cooldown.
        Assert.False(budget.TryReserve(Start.AddMinutes(2), frameTimeMs: 450, out var refusal));
        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, budget.Spent);
    }

    /// <summary>
    /// The override spacing exists to guarantee the ring buffer has refilled, so it has to scale with
    /// the buffer. At the 2 048 MB ceiling the refill alone is about 56 s, and a fixed sixty would
    /// approve a capture while the previous one was still draining — spending budget on a trace holding
    /// almost no history, which is the one outcome this gate exists to prevent.
    /// </summary>
    [Fact]
    public void TheOverrideSpacingGrowsWithTheRingBuffer()
    {
        var large = Options(item =>
        {
            item.RingBufferMegabytes = 2048;
            item.AutoCaptureOverrideCooldown = TimeSpan.FromSeconds(60);
        });

        // Refill plus the tail plus the drain — 28 s is the fastest of the five measured captures, so
        // clearing it is the weakest claim that is still true.
        var mustClear = large.EstimatedRingBufferSeconds + large.PostMarkerTail.TotalSeconds + 28;
        Assert.True(
            large.AutoCaptureOverrideCooldown.TotalSeconds >= mustClear,
            $"override spacing {large.AutoCaptureOverrideCooldown.TotalSeconds:F0}s does not clear the "
            + $"{mustClear:F0}s a capture costs before the buffer is worth reading again");

        var budget = new AutoDeepCaptureBudget(large);
        Assert.True(budget.TryReserve(Start, frameTimeMs: 900, out _));
        Assert.False(budget.TryReserve(Start.AddSeconds(60), frameTimeMs: 900, out _));

        // The default buffer is unaffected: sixty seconds already clears its 21 s refill.
        Assert.Equal(TimeSpan.FromSeconds(60), Options().AutoCaptureOverrideCooldown);
    }

    /// <summary>
    /// The condition a frame time threshold structurally cannot see: no single frame is remarkable, the
    /// frame rate has simply stopped recovering. A session spent 104 of 391 minutes like this and asked
    /// for no trace of any of them.
    /// </summary>
    [Fact]
    public void SustainedSaturationCanSpendACaptureWithoutABadFrame()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserveForSustainedSaturation(Start, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(1, budget.Spent);
    }

    [Fact]
    public void SaturationSharesTheBudgetAndCooldownWithFrameHitches()
    {
        var budget = new AutoDeepCaptureBudget(CooldownOnlyOptions());

        Assert.True(budget.TryReserve(Start, frameTimeMs: 900, out _));

        // Same ceiling, same spacing: a bad patch that is both saturated and hitching must not get two
        // parallel allowances.
        Assert.False(budget.TryReserveForSustainedSaturation(Start.AddMinutes(2), out var refusal));
        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, budget.Spent);
    }

    /// <summary>
    /// The default has to be worth the disk it costs. Twelve captures of a few hundred megabytes across
    /// an evening is the trade this feature was sized for.
    /// </summary>
    /// <remarks>
    /// Recalibrated after the session of 27 August, where one per hour refused twenty-four hitches and
    /// the 500 ms exception missed the evening's second largest frame by 16 ms. Three an hour against
    /// the ten minute cooldown cannot exceed three, so the cooldown still decides the volume.
    /// </remarks>
    [Fact]
    public void TheDefaultBuysAHandfulOfCapturesNotAStream()
    {
        var options = Options();

        Assert.True(options.CaptureAutoIncidents);
        Assert.Equal(12, options.MaxAutoCapturesPerSession);
        Assert.Equal(120, options.AutoCaptureFrameTimeMs);
        Assert.Equal(250, options.AutoCaptureOverrideFrameTimeMs);
        Assert.Equal(3, options.MaxAutoCapturesPerWindow);
        Assert.Equal(TimeSpan.FromHours(1), options.CaptureBudgetWindow);

        // Scheduler stacks cost retention but locate the blocking call chain. The 768 MB default still
        // clears a manual reaction comfortably, while automatic capture stops at the hitch itself.
        Assert.True(options.EstimatedRingBufferSeconds > 15, $"ring buffer holds only {options.EstimatedRingBufferSeconds:F0}s");
    }

    /// <summary>
    /// The failure the window budget exists to prevent, replayed against the frames that produced it.
    /// </summary>
    /// <remarks>
    /// The 26 August session held 67 frames over 120 ms, and 43 of them fell inside fourteen minutes of
    /// the opening hour — a cache rebuilt after a settings change, with a sync backlog on top. Against a
    /// session ceiling alone, that opening burst spends the entire budget and the remaining five hours
    /// record nothing. This is the same failure a session-wide incident ceiling produced one layer up,
    /// and the same fix.
    /// </remarks>
    [Fact]
    public void AnOpeningBurstCannotSpendTheWholeSessionsBudget()
    {
        var budget = new AutoDeepCaptureBudget(Options());
        var timestamp = Start;

        // Fourteen minutes of ordinary capture-worthy frames, one every twenty seconds.
        for (var i = 0; i < 42; i++)
        {
            budget.TryReserve(timestamp, frameTimeMs: 180, out _);
            timestamp = timestamp.AddSeconds(20);
        }

        // Two rather than three: the hour's allowance is three, and the ten minute cooldown is what
        // stops fourteen minutes of hitches from spending even that.
        Assert.Equal(2, budget.Spent);

        // Five hours later the session still has almost all of its budget, which is the entire point.
        Assert.Equal(10, budget.Remaining);
        Assert.True(budget.TryReserve(Start.AddHours(5), frameTimeMs: 180, out _));
    }

    /// <summary>
    /// The window budget rations ordinary frames, not catastrophic ones. On 26 August the largest frame
    /// of the evening — 586 ms — arrived 48 seconds after a 122 ms frame had taken the hour's capture.
    /// </summary>
    [Fact]
    public void ACatastrophicFrameIgnoresTheWindowBudget()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        // The hour's three ordinary captures, spaced past the cooldown so the window is what fills.
        Assert.True(budget.TryReserve(Start, frameTimeMs: 122, out _));
        Assert.True(budget.TryReserve(Start.AddMinutes(11), frameTimeMs: 130, out _));
        Assert.True(budget.TryReserve(Start.AddMinutes(22), frameTimeMs: 140, out _));

        // Ordinary frames are refused for the rest of the hour, and told why.
        Assert.False(budget.TryReserve(Start.AddMinutes(33), frameTimeMs: 200, out var refusal));
        Assert.Contains("per 60 min", refusal!, StringComparison.OrdinalIgnoreCase);

        // The catastrophic one is not — it answers to the ring buffer and the session ceiling only.
        Assert.True(budget.TryReserve(Start.AddMinutes(33), frameTimeMs: 586, out _));
        Assert.Equal(4, budget.Spent);
    }

    /// <summary>
    /// An override is exempt from the window budget, so it must not spend the window's slot either.
    /// Charging it would let one catastrophic frame buy silence for the rest of the hour — the exact
    /// failure the window exists to prevent, arriving by the path that is supposed to be exempt.
    /// </summary>
    [Fact]
    public void AnOverrideDoesNotChargeTheWindowItIgnores()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserve(Start, frameTimeMs: 900, out _));

        // Eleven minutes later: past the ordinary cooldown, still well inside the hour. The hour's three
        // ordinary slots all remain, which is the claim — had the override charged one, the third of
        // these would be refused.
        Assert.True(budget.TryReserve(Start.AddMinutes(11), frameTimeMs: 180, out var refusal));
        Assert.Null(refusal);
        Assert.True(budget.TryReserve(Start.AddMinutes(22), frameTimeMs: 180, out _));
        Assert.True(budget.TryReserve(Start.AddMinutes(33), frameTimeMs: 180, out _));
        Assert.Equal(4, budget.Spent);

        // And the ordinary captures did charge the window, so the next ordinary one waits.
        Assert.False(budget.TryReserve(Start.AddMinutes(44), frameTimeMs: 180, out var second));
        Assert.Contains("per 60 min", second!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The window has to roll rather than reset, or a capture at the top of the hour would buy silence
    /// until the next one.
    /// </summary>
    [Fact]
    public void TheWindowRollsRatherThanResetting()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserve(Start, frameTimeMs: 180, out _));
        Assert.True(budget.TryReserve(Start.AddMinutes(11), frameTimeMs: 180, out _));
        Assert.True(budget.TryReserve(Start.AddMinutes(22), frameTimeMs: 180, out _));

        Assert.False(budget.TryReserve(Start.AddMinutes(59), frameTimeMs: 180, out _));

        // The first of the three has aged out by now, so the hour has a slot again.
        Assert.True(budget.TryReserve(Start.AddMinutes(61), frameTimeMs: 180, out _));

        Assert.Equal(4, budget.Spent);
    }
}
