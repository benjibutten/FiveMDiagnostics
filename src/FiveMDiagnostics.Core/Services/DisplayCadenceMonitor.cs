namespace FiveMDiagnostics.Core;

/// <summary>
/// Measures how many of the game's frames reach the screen in step with the display, as opposed to a
/// refresh early or a refresh late.
/// </summary>
/// <remarks>
/// <para>
/// This is the measurement that ended a nine-session investigation, and for eight of those sessions
/// nothing computed it. Frame time said the game was healthy every evening — 16.67 ms median, 97% of
/// presents landing exactly on cadence — while the player reported constant stutter. The disagreement
/// was real and it was visible in data the app had been logging all along:
/// </para>
/// <code>
///                    presented on cadence    reached the screen on cadence
///   21 Aug                        —                       87.2%
///   22 Aug                        —                       79.0%
///   23 Aug                        —                       85.6%
///   24 Aug                        —                       88.9%
///   25 Aug                        —                       89.4%
///   26 Aug                        —                       86.3%
///   27 Aug                        —                       89.5%
///   28 Aug                    97.2%                       88.7%
///   30 Aug                    98.3%                       99.6%
/// </code>
/// <para>
/// On 28 August 5.8% of frames reached the screen a refresh <em>early</em> and 4.4% a refresh
/// <em>late</em> — an oscillation around the right moment, roughly one frame in nine, all evening. It
/// does not appear in frame time at all, because the frames themselves were on time. The cause was the
/// game's blt swapchain being composed by DWM while two monitors ran at different refresh rates, which
/// forces the compositor to resample; syncing the two rates took the figure to 0.41%.
/// </para>
/// <para>
/// Counted in whole refreshes rather than in milliseconds, which is what makes it comparable across
/// displays. A 60 fps game on a 120 Hz panel changes the screen every two refreshes and on a 60 Hz panel
/// every one; a frame that slips is one refresh out in both cases, but it is 8.3 ms in the first and
/// 16.7 ms in the second. A millisecond threshold would have to be retuned for every display and would
/// have read the two evenings above as more different than they were.
/// </para>
/// </remarks>
public sealed class DisplayCadenceMonitor
{
    /// <summary>
    /// Frames needed before a share is worth printing.
    /// </summary>
    /// <remarks>
    /// A few seconds of play. Below this the modal cadence itself is not established, and the share is
    /// a ratio of small numbers that moves several percentage points per frame.
    /// </remarks>
    private const int MinimumFrames = 600;

    /// <summary>
    /// How many refreshes a single display change may span and still be counted.
    /// </summary>
    /// <remarks>
    /// Beyond this it is a hitch, not a cadence miss, and the two are separate measurements with
    /// separate causes. Frame time already reports hitches and reports them better; letting a 1 049 ms
    /// frame land in the same bucket as a frame that arrived one refresh late would let a handful of
    /// freezes swamp a figure whose whole value is that it counts the small, constant defect.
    /// </remarks>
    private const int MaximumCountedRefreshes = 8;

    private readonly Dictionary<int, int> _byRefreshCount = [];
    private readonly double _refreshIntervalMs;

    private int _countedFrames;
    private int _classifiedFrames;
    private int _composedFrames;

    public DisplayCadenceMonitor(double? refreshRateHz)
    {
        _refreshIntervalMs = refreshRateHz is > 0 ? 1000d / refreshRateHz.Value : 1000d / 60;
    }

    /// <summary>
    /// Share of the session's frames that went through the compositor, or null when no frame carried a
    /// present mode.
    /// </summary>
    /// <remarks>
    /// Counted here rather than in the report, because it is what says whether the refresh-rate warning
    /// written at session start applied at all: a frame on its own hardware plane is not resampled by
    /// DWM and a mismatch between the panels costs it nothing. Counted over every frame that named a
    /// mode, including the ones with no display-change figure to place on the cadence.
    /// </remarks>
    public double? ComposedShare => _classifiedFrames > 0 ? (double)_composedFrames / _classifiedFrames : null;

    /// <summary>Folds one frame into the running distribution.</summary>
    public void Observe(FrameTelemetrySample sample)
    {
        if (sample.PresentMode is not null)
        {
            _classifiedFrames++;
            if (sample.IsComposedPresent)
            {
                _composedFrames++;
            }
        }

        if (sample.MsBetweenDisplayChange is not > 0 || _refreshIntervalMs <= 0)
        {
            return;
        }

        var refreshes = (int)Math.Round(sample.MsBetweenDisplayChange.Value / _refreshIntervalMs, MidpointRounding.AwayFromZero);
        if (refreshes is < 0 or > MaximumCountedRefreshes)
        {
            return;
        }

        _byRefreshCount[refreshes] = _byRefreshCount.GetValueOrDefault(refreshes) + 1;
        _countedFrames++;
    }

    /// <summary>
    /// The session's cadence so far, or null while there is too little of it to report.
    /// </summary>
    public DisplayCadenceReport? Snapshot()
    {
        if (_countedFrames < MinimumFrames || _byRefreshCount.Count == 0)
        {
            return null;
        }

        // The cadence the display actually holds, taken from the frames rather than assumed. A 60 fps
        // game on a 120 Hz panel holds two refreshes and on a 60 Hz panel one, and nothing outside this
        // class knows which it is looking at.
        var modal = _byRefreshCount.OrderByDescending(entry => entry.Value).First();
        var early = _byRefreshCount.Where(entry => entry.Key < modal.Key).Sum(entry => entry.Value);
        var late = _byRefreshCount.Where(entry => entry.Key > modal.Key).Sum(entry => entry.Value);

        return new DisplayCadenceReport(
            _countedFrames,
            modal.Key,
            _refreshIntervalMs,
            (double)early / _countedFrames,
            (double)late / _countedFrames);
    }
}

/// <summary>How the session's frames landed against the display's own cadence.</summary>
/// <param name="ModalRefreshes">Refreshes between display changes when the cadence is held.</param>
/// <param name="EarlyShare">Share that reached the screen sooner than the cadence.</param>
/// <param name="LateShare">Share that reached the screen later than the cadence.</param>
public sealed record DisplayCadenceReport(
    int FrameCount,
    int ModalRefreshes,
    double RefreshIntervalMs,
    double EarlyShare,
    double LateShare)
{
    /// <summary>Share of frames that did not reach the screen on cadence, in either direction.</summary>
    public double OffCadenceShare => EarlyShare + LateShare;

    /// <summary>
    /// Above this the cadence is worth acting on rather than noting.
    /// </summary>
    /// <remarks>
    /// Every session measured before the fix sat between 10.5% and 21%; the session after it measured
    /// 0.41%. There is no measurement anywhere in between, so the threshold is placed well clear of the
    /// good case and well under every bad one rather than tuned against a distribution nobody has.
    /// </remarks>
    public bool IsOffCadence => OffCadenceShare >= 0.03;

    public string Message
    {
        get
        {
            var cadence = $"{OffCadenceShare:P2} av {FrameCount} frames nådde skärmen ur takt "
                + $"({EarlyShare:P2} en uppdatering för tidigt, {LateShare:P2} för sent). "
                + $"Skärmen byter bild var {ModalRefreshes * RefreshIntervalMs:F1} ms när takten hålls.";

            return IsOffCadence
                ? cadence
                    + " Det syns inte i frametime — spelet levererar sina frames jämnt och de hålls sedan"
                    + " kvar i kompositorn. Vanligaste orsaken är att två skärmar går i olika"
                    + " uppdateringsfrekvens, vilket tvingar DWM att omsampla varje frame."
                : cadence;
        }
    }
}
