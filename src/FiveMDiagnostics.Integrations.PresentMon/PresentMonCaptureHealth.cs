namespace FiveMDiagnostics.Integrations.PresentMon;

/// <summary>
/// Decides when a PresentMon capture that is alive but mute should be restarted, and when restarting
/// has to stop.
/// </summary>
/// <remarks>
/// A capture that stops producing frames while FiveM is running is usually a lost ETW session, but it
/// also looks exactly like a legitimate pause: an alt-tab, a minimised window or a long loading screen
/// present nothing at all. Restarting on a fixed 15 second silence therefore risked killing and
/// respawning PresentMon every 15 seconds for as long as the player stayed in the menu, which costs
/// more ETW and process churn than the missing frames it was trying to recover.
/// <para>
/// The policy keeps the fast first reaction but doubles the tolerated silence after every restart, so a
/// genuinely dead session is recovered within seconds while a long pause quickly stops being retried.
/// After <see cref="MaxConsecutiveRestarts"/> fruitless restarts the collector gives up entirely until
/// the game process changes or a new session starts; a capture that then runs healthily for
/// <see cref="StableRunBeforeReset"/> clears the ladder again.
/// </para>
/// </remarks>
public sealed class PresentMonCaptureHealth
{
    /// <summary>Silence tolerated before the first restart. Long enough to survive a short alt-tab.</summary>
    public static readonly TimeSpan BaseSilenceBeforeRestart = TimeSpan.FromSeconds(15);

    /// <summary>Ceiling for the doubling ladder, so the last attempts are minutes apart rather than seconds.</summary>
    public static readonly TimeSpan MaxSilenceBeforeRestart = TimeSpan.FromMinutes(4);

    /// <summary>How long a capture has to keep delivering frames before earlier restarts stop counting.</summary>
    public static readonly TimeSpan StableRunBeforeReset = TimeSpan.FromMinutes(2);

    /// <summary>Restarts attempted for one target process before automatic restarts are suspended.</summary>
    public const int MaxConsecutiveRestarts = 5;

    /// <summary>Restarts tolerated before the warning escalates from "restarted" to "something is wrong".</summary>
    public const int RestartsBeforeEscalation = 3;

    private DateTimeOffset _lastProgressUtc;
    private DateTimeOffset _captureStartedUtc;
    private DateTimeOffset _nextRestartAllowedUtc;

    /// <summary>Process id of the game the ladder currently describes; 0 when no target has been seen.</summary>
    public int TargetProcessId { get; private set; }

    /// <summary>True once a capture has been started for the current target, i.e. the next start is a restart.</summary>
    public bool HasStartedCapture { get; private set; }

    public int RestartCount { get; private set; }

    /// <summary>True once restarting has been given up on for the current target.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>Silence tolerated right now, which grows with every restart already attempted.</summary>
    public TimeSpan SilenceThreshold => Backoff(RestartCount);

    /// <summary>When the current capture last proved it was alive.</summary>
    public DateTimeOffset LastProgressUtc => _lastProgressUtc;

    /// <summary>Forgets everything, e.g. between sessions.</summary>
    public void Reset() => OnTargetChanged(0);

    /// <summary>
    /// Points the policy at another game process. The ladder describes one capture target, not the
    /// lifetime of the collector, so a relaunched FiveM starts from a clean slate.
    /// </summary>
    public void OnTargetChanged(int processId)
    {
        TargetProcessId = processId;
        HasStartedCapture = false;
        RestartCount = 0;
        IsSuspended = false;
        _nextRestartAllowedUtc = DateTimeOffset.MinValue;
    }

    public void OnCaptureStarted(DateTimeOffset utcNow)
    {
        HasStartedCapture = true;
        _captureStartedUtc = utcNow;
        _lastProgressUtc = utcNow;
    }

    /// <summary>
    /// Records that the capture is demonstrably alive. Returns true when this progress cleared the
    /// restart ladder, which is worth telling the user about after a run of failures.
    /// </summary>
    public bool OnProgress(DateTimeOffset utcNow)
    {
        _lastProgressUtc = utcNow;

        if (RestartCount == 0 || utcNow - _captureStartedUtc < StableRunBeforeReset)
        {
            return false;
        }

        RestartCount = 0;
        IsSuspended = false;
        _nextRestartAllowedUtc = DateTimeOffset.MinValue;
        return true;
    }

    /// <summary>True when the capture has produced nothing for longer than the current threshold.</summary>
    public bool IsSilent(DateTimeOffset utcNow) => utcNow - _lastProgressUtc >= SilenceThreshold;

    /// <summary>True when a restart is both allowed and no longer held back by the backoff.</summary>
    public bool CanRestart(DateTimeOffset utcNow) => !IsSuspended && utcNow >= _nextRestartAllowedUtc;

    /// <summary>
    /// Claims one restart attempt. Returns false while the backoff is still running or once restarts
    /// have been suspended, in which case the caller must leave the capture alone.
    /// </summary>
    public bool TryBeginRestart(DateTimeOffset utcNow)
    {
        if (!CanRestart(utcNow))
        {
            return false;
        }

        RestartCount++;
        _nextRestartAllowedUtc = utcNow + Backoff(RestartCount);

        if (RestartCount >= MaxConsecutiveRestarts)
        {
            IsSuspended = true;
        }

        return true;
    }

    private static TimeSpan Backoff(int restartCount)
    {
        if (restartCount <= 0)
        {
            return BaseSilenceBeforeRestart;
        }

        var scaled = BaseSilenceBeforeRestart * Math.Pow(2, Math.Min(restartCount, 16));
        return scaled > MaxSilenceBeforeRestart ? MaxSilenceBeforeRestart : scaled;
    }
}
