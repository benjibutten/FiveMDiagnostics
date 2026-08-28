namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The evening of 27 August, replayed against the budget that refused it.
/// </summary>
/// <remarks>
/// That session produced five captures and skipped twenty-four, every one of them for the same reason:
/// one capture per sixty minutes was already taken. Among the refusals was the evening's second largest
/// frame, 484 ms at 22:10 — sixteen milliseconds under the 500 ms exception, in the window where a
/// browser was later shown to have held five cores, and the only large frame of the night with neither
/// an explanation nor a trace. These are the frames of that evening and the times they arrived.
/// </remarks>
public sealed class CaptureBudgetAgainstTheAugust27SessionTests
{
    /// <summary>21:38 local, when the session started.</summary>
    private static readonly DateTimeOffset SessionStart = new(2026, 8, 27, 19, 38, 0, TimeSpan.Zero);

    /// <summary>The frames the note lists, as minutes after the session started.</summary>
    private static readonly (double Minutes, double FrameTimeMs)[] LargeFrames =
    [
        (8, 528),
        (32, 484),
        (39, 284),
        (75, 217),
        (84, 138),
        (244, 285),
        (341, 166),
    ];

    [Fact]
    public void The484MsFrameGetsATrace()
    {
        var (_, captured) = Replay();

        Assert.Contains(484d, captured);
    }

    /// <summary>
    /// The two that were captured on the night stay captured and the next largest join them. What this
    /// must not do is turn the evening into a capture per hitch.
    /// </summary>
    [Fact]
    public void TheLargestFramesAreCapturedAndTheRestAreStillRationed()
    {
        var (budget, captured) = Replay();

        Assert.Contains(528d, captured);
        Assert.Contains(285d, captured);
        Assert.Contains(284d, captured);

        // The 138 ms frame is refused, and by the right gate: it arrived nine minutes after the 217 ms
        // one, inside the spacing that exists so a capture does not record a ring buffer that has not
        // refilled. That is the cooldown working, not the budget going blind again.
        Assert.DoesNotContain(138d, captured);
        Assert.True(budget.Remaining > 0, "the session ceiling was exhausted by an evening this quiet");
    }

    /// <summary>
    /// The other direction, and the reason the exception may be as low as 250 ms: an evening where
    /// frames that size are ordinary raises its own bar instead of capturing all of them.
    /// </summary>
    /// <remarks>
    /// Three an hour is the rate the exception is sized at, so after two hours of a 400 ms frame every
    /// thirty seconds the bar has moved to where the largest few actually are, and a 260 ms frame no
    /// longer breaks the cooldown the way it would at the constant.
    /// </remarks>
    [Fact]
    public void AnEveningFullOfLargeFramesRaisesItsOwnThreshold()
    {
        var budget = new AutoDeepCaptureBudget(NormalizedDefaults());

        for (var i = 0; i < 240; i++)
        {
            budget.Observe(SessionStart.AddSeconds(i * 30), 400 + (i % 7));
        }

        Assert.True(
            budget.EffectiveOverrideFrameTimeMs > 250,
            $"the exception stayed at {budget.EffectiveOverrideFrameTimeMs:F0} ms on an evening of 400 ms frames");
        Assert.True(
            budget.EffectiveFrameTimeMs > 120,
            $"the ordinary threshold stayed at {budget.EffectiveFrameTimeMs:F0} ms");
    }

    /// <summary>
    /// The adaptation may only ever raise. A quiet evening has no material to raise anything with, and
    /// dragging the threshold down into ordinary spikes is what the constants are the floor against.
    /// </summary>
    [Fact]
    public void AQuietEveningKeepsTheConfiguredThresholds()
    {
        var budget = new AutoDeepCaptureBudget(NormalizedDefaults());

        for (var i = 0; i < 20_000; i++)
        {
            budget.Observe(SessionStart.AddMilliseconds(i * 16.67), 16.67);
        }

        Assert.Equal(120, budget.EffectiveFrameTimeMs);
        Assert.Equal(250, budget.EffectiveOverrideFrameTimeMs);
    }

    /// <summary>
    /// The exception must never meet the rule it is an exception to.
    /// </summary>
    /// <remarks>
    /// The two thresholds adapt at different rates and therefore warm up at different times: twenty
    /// minutes in, twenty frames an hour is already four frames of material while three an hour is not
    /// yet one, so the ordinary threshold can climb past a still-constant override. Where they meet,
    /// every frame that clears the ordinary threshold also clears the override — and then every
    /// capture-worthy frame bypasses the cooldown and the hourly budget, leaving the session ceiling as
    /// the only gate. Four frames over 250 ms inside the first twenty minutes is an ordinary bad opening.
    /// </remarks>
    [Fact]
    public void TheOverrideThresholdStaysClearOfTheOrdinaryOne()
    {
        var budget = new AutoDeepCaptureBudget(NormalizedDefaults());

        // Twelve minutes of ordinary frames carrying four large ones, which is enough material for the
        // ordinary threshold to adapt and not enough for the override's.
        for (var i = 0; i < 43_000; i++)
        {
            budget.Observe(SessionStart.AddMilliseconds(i * 16.67), 16.67);
        }

        foreach (var frameTimeMs in new[] { 300d, 310, 320, 330 })
        {
            budget.Observe(SessionStart.AddMinutes(11), frameTimeMs);
        }

        Assert.True(
            budget.EffectiveOverrideFrameTimeMs > budget.EffectiveFrameTimeMs,
            $"the exception ({budget.EffectiveOverrideFrameTimeMs:F0} ms) met the rule "
            + $"({budget.EffectiveFrameTimeMs:F0} ms), which makes every capture-worthy frame an override");
    }

    /// <summary>
    /// And the consequence, measured rather than reasoned about: with the two thresholds equal, a run of
    /// qualifying frames spends captures at the sixty second override spacing instead of the ten minute
    /// cooldown, and the hourly budget never gets a say.
    /// </summary>
    [Fact]
    public void AdaptationCannotTurnEveryQualifyingFrameIntoAnOverride()
    {
        var budget = new AutoDeepCaptureBudget(NormalizedDefaults());

        for (var i = 0; i < 43_000; i++)
        {
            budget.Observe(SessionStart.AddMilliseconds(i * 16.67), 16.67);
        }

        foreach (var frameTimeMs in new[] { 300d, 310, 320, 330 })
        {
            budget.Observe(SessionStart.AddMinutes(11), frameTimeMs);
        }

        // Half an hour of 340 ms frames, one every two minutes: past the override spacing every time,
        // inside the cooldown and inside the hour every time.
        var spent = 0;
        for (var i = 0; i < 15; i++)
        {
            var timestamp = SessionStart.AddMinutes(12 + (i * 2));
            budget.Observe(timestamp, 340);
            if (budget.TryReserve(timestamp, 340, out _))
            {
                spent++;
            }
        }

        Assert.True(spent <= 3, $"{spent} captures were spent in half an hour against a budget of three");
    }

    private static (AutoDeepCaptureBudget Budget, List<double> Captured) Replay()
    {
        var budget = new AutoDeepCaptureBudget(NormalizedDefaults());
        var captured = new List<double>();

        foreach (var (minutes, frameTimeMs) in LargeFrames)
        {
            var timestamp = SessionStart.AddMinutes(minutes);

            // Ten seconds of ordinary frames ahead of each one, so the adaptive thresholds see the
            // distribution the large frames stand out from rather than only the large frames.
            for (var i = 600; i > 0; i--)
            {
                budget.Observe(timestamp.AddMilliseconds(-i * 16.67), 16.67);
            }

            budget.Observe(timestamp, frameTimeMs);
            if (budget.TryReserve(timestamp, frameTimeMs, out _))
            {
                captured.Add(frameTimeMs);
            }
        }

        return (budget, captured);
    }

    private static DeepCaptureOptions NormalizedDefaults()
    {
        var options = new DeepCaptureOptions();
        options.Normalize();
        return options;
    }
}
