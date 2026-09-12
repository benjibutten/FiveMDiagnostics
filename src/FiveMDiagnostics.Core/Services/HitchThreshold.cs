namespace FiveMDiagnostics.Core;

/// <summary>
/// The session's one answer to how slow a frame has to be before it is a hitch: twice the interval the
/// evening actually runs at, and never below two refresh intervals.
/// </summary>
/// <remarks>
/// <para>
/// Four monitors counted hitches against four thresholds of their own — the focus line on two refreshes
/// alone, capture cost and the VRAM band on two copies of one cadence median, the half-hour table on
/// whatever the focus line happened to hold. At 59.94 Hz with a 16.7 ms median they all land near
/// 33.3 ms, which is why the divergence never showed in a summary; it showed in the notes, where the
/// same evening's "time above 88 %" has been written up as both 1.4× and 3.4× because two lines counted
/// against two bars. Evenings are compared on the decimal, so the bar has to be one number.
/// </para>
/// <para>
/// This is the hitch threshold and nothing else. The incident thresholds are a separate question with
/// separate multipliers; <c>docs/ARCHITECTURE.md</c> says how the two differ and why.
/// </para>
/// </remarks>
public sealed class HitchThreshold
{
    /// <summary>
    /// Frames the session runs before the bar is fixed, matching <c>DisplayCadenceMonitor</c>'s own
    /// warm-up. A few seconds of play.
    /// </summary>
    private const int WarmupFrames = 600;

    /// <summary>
    /// What a hitch costs, in baselines. Twice the cadence is the figure every note since 2026-08-20 has
    /// been written against, so it is not a knob.
    /// </summary>
    private const double Multiplier = 2;

    private readonly object _sync = new();
    private readonly List<double> _warmup = new(WarmupFrames);
    private readonly double _refreshIntervalMs;

    private double _baselineMs;

    /// <param name="refreshRateHz">
    /// The display's rate, which floors the baseline: a frame inside one refresh cannot be seen however
    /// the game is capped, so half of one cannot be a hitch either.
    /// </param>
    public HitchThreshold(double? refreshRateHz)
    {
        _refreshIntervalMs = refreshRateHz is > 0 ? 1000d / refreshRateHz.Value : 1000d / 60;
    }

    /// <summary>
    /// The interval a session is judged against: the cadence it achieves, never below one refresh.
    /// </summary>
    /// <remarks>
    /// Take whichever is larger. A game locked to 60 fps on a 165 Hz panel is not stuttering, so the
    /// achieved cadence is the honest figure; a game that should hit 120 Hz must not be graded against a
    /// median a bad window has already dragged upwards. Shared with the incident thresholds, which
    /// multiply the same baseline by their own factors.
    /// </remarks>
    public static double BaselineFrom(double medianFrameTimeMs, double refreshIntervalMs) =>
        Math.Max(medianFrameTimeMs, refreshIntervalMs);

    /// <summary>What the session runs at, once <see cref="IsSettled"/>; zero before that.</summary>
    public double BaselineMs
    {
        get
        {
            lock (_sync)
            {
                return _baselineMs;
            }
        }
    }

    /// <summary>What counts as a hitch, in milliseconds; zero before the warm-up has settled.</summary>
    public double ThresholdMs
    {
        get
        {
            lock (_sync)
            {
                return _baselineMs * Multiplier;
            }
        }
    }

    /// <summary>Whether the bar is fixed. Monitors hold their frames back until it is.</summary>
    public bool IsSettled
    {
        get
        {
            lock (_sync)
            {
                return _baselineMs > 0;
            }
        }
    }

    /// <summary>
    /// Folds one frame of play into the warm-up, and fixes the bar once there are enough of them.
    /// </summary>
    /// <remarks>
    /// Fed once per frame by the session, from the frames the game was in focus for — an alt-tab
    /// produces frame times that would move the bar for the whole evening.
    /// </remarks>
    public void Observe(double frameTimeMs)
    {
        lock (_sync)
        {
            if (_baselineMs > 0)
            {
                return;
            }

            _warmup.Add(frameTimeMs);
            if (_warmup.Count >= WarmupFrames)
            {
                SettleCore();
            }
        }
    }

    /// <summary>
    /// Fixes the bar from whatever the warm-up holds, for a session that ended before it filled.
    /// </summary>
    /// <remarks>
    /// Idempotent, and called by every monitor's summary: a session of forty frames still has to be
    /// reported, and all four of them have to report it against the same bar.
    /// </remarks>
    public void Settle()
    {
        lock (_sync)
        {
            if (_baselineMs <= 0)
            {
                SettleCore();
            }
        }
    }

    /// <summary>Called under the lock.</summary>
    private void SettleCore()
    {
        // Median rather than mean: the warm-up is where a session's loading stutters live, and the whole
        // point of the figure is the interval the evening settles at.
        var sorted = _warmup.Order().ToArray();
        var cadenceMs = sorted.Length > 0 ? sorted[sorted.Length / 2] : _refreshIntervalMs;

        _baselineMs = BaselineFrom(cadenceMs, _refreshIntervalMs);
        _warmup.Clear();
        _warmup.TrimExcess();
    }
}
