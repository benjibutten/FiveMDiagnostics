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
    [Fact]
    public void ABurstSpendsOneCaptureNotTwenty()
    {
        var budget = new AutoDeepCaptureBudget(Options());
        var timestamp = Start;

        for (var i = 0; i < 23; i++)
        {
            budget.TryReserve(timestamp, frameTimeMs: 600, out _);
            timestamp = timestamp.AddSeconds(5);
        }

        Assert.Equal(1, budget.Spent);
    }

    [Fact]
    public void TheCooldownExpiresAndTheNextEventIsCaptured()
    {
        var options = Options(item => item.AutoCaptureCooldown = TimeSpan.FromMinutes(10));
        var budget = new AutoDeepCaptureBudget(options);

        Assert.True(budget.TryReserve(Start, 600, out _));
        Assert.False(budget.TryReserve(Start.AddMinutes(9), 600, out var refusal));
        Assert.Contains("cooldown", refusal!, StringComparison.OrdinalIgnoreCase);

        Assert.True(budget.TryReserve(Start.AddMinutes(11), 600, out _));
        Assert.Equal(2, budget.Spent);
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
