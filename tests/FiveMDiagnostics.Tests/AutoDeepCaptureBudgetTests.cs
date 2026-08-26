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

    [Fact]
    public void OrdinaryHitchesDoNotSpendACapture()
    {
        var budget = new AutoDeepCaptureBudget(Options());

        // 965 frames in the reference session were over 33 ms and 120 over 100 ms. If either could spend
        // a capture the budget would be gone within minutes of the session starting.
        Assert.False(budget.TryReserve(Start, frameTimeMs: 40, out var refusal));
        Assert.False(budget.TryReserve(Start, frameTimeMs: 120, out _));
        Assert.False(budget.TryReserve(Start, frameTimeMs: 299, out _));

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
        Assert.Equal(5, budget.Remaining);
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
        var options = Options(item => item.AutoCaptureCooldown = TimeSpan.FromMinutes(10));
        var budget = new AutoDeepCaptureBudget(options);

        // Below the override threshold, so this is the ordinary cooldown being tested rather than the
        // way past it.
        Assert.True(budget.TryReserve(Start, 350, out _));
        Assert.False(budget.TryReserve(Start.AddMinutes(9), 350, out var refusal));
        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);

        Assert.True(budget.TryReserve(Start.AddMinutes(11), 350, out _));
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
        // per frame, and nowhere near the session ceiling of six.
        Assert.Equal(4, budget.Spent);
    }

    /// <summary>
    /// Saturation has no frame to be catastrophic, so it can never take the shorter path. One stretch of
    /// unrecoverable frame rate is one event however long it runs.
    /// </summary>
    [Fact]
    public void SustainedSaturationCannotBreakTheCooldown()
    {
        var budget = new AutoDeepCaptureBudget(Options());

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

        Assert.Equal(300, options.AutoCaptureFrameTimeMs);
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
        var options = Options(item =>
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
        var budget = new AutoDeepCaptureBudget(Options());

        Assert.True(budget.TryReserve(Start, frameTimeMs: 900, out _));

        // Same ceiling, same spacing: a bad patch that is both saturated and hitching must not get two
        // parallel allowances.
        Assert.False(budget.TryReserveForSustainedSaturation(Start.AddMinutes(2), out var refusal));
        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, budget.Spent);
    }

    /// <summary>
    /// The default has to be worth the disk it costs. Six captures of a few hundred megabytes across an
    /// evening is the trade this feature was sized for.
    /// </summary>
    [Fact]
    public void TheDefaultBuysAHandfulOfCapturesNotAStream()
    {
        var options = Options();

        Assert.True(options.CaptureAutoIncidents);
        Assert.Equal(6, options.MaxAutoCapturesPerSession);
        Assert.Equal(300, options.AutoCaptureFrameTimeMs);

        // Scheduler stacks cost retention but locate the blocking call chain. The 768 MB default still
        // clears a manual reaction comfortably, while automatic capture stops at the hitch itself.
        Assert.True(options.EstimatedRingBufferSeconds > 15, $"ring buffer holds only {options.EstimatedRingBufferSeconds:F0}s");
    }
}
