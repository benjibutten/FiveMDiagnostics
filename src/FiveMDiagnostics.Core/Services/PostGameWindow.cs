namespace FiveMDiagnostics.Core;

/// <summary>
/// How long measurement continues after the game process goes away, and whether it still is.
/// </summary>
/// <remarks>
/// <para>
/// One duration behind three decisions that have to agree: how long the GPU collector keeps sampling
/// once the game is gone, how long the session waits before it ends itself, and how much of a release
/// <see cref="PostGameVramRelease"/> gets to see. A session that outlived the sampling would end on an
/// empty tail; one that ended first would throw the tail's samples away.
/// </para>
/// <para>
/// The three run on separate instances and separate clocks — the collector on its own poll, the policy
/// on the UI timer — so they agree to within a poll rather than exactly, which is about a second and
/// costs nothing at ten minutes. What keeps them from drifting apart is that the duration lives here and
/// nowhere else; a tail measured in one of the three against a number of its own is the way this breaks.
/// </para>
/// <para>
/// Ten minutes, and the restart sets it rather than the tail does. FiveM's launcher, the game and the
/// join take several minutes together, and a shorter wait would end the evening in the middle of one —
/// splitting the journal in two and with it every per-session share the report is built from, so the
/// night the game crashed twice would read as three evenings. Everything measured per process is
/// already two series across a restart; the session is deliberately not. What waiting too long costs
/// is a few idle minutes of an adapter poll that is running on purpose.
/// </para>
/// </remarks>
public sealed class PostGameWindow
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _lastSeenUtc;

    /// <summary>
    /// Whether the game is running, or left recently enough that what it left behind is still worth
    /// measuring.
    /// </summary>
    public bool IsOpen(bool gameRunning, DateTimeOffset nowUtc)
    {
        if (gameRunning)
        {
            _lastSeenUtc = nowUtc;
            return true;
        }

        return _lastSeenUtc is { } lastSeen && nowUtc - lastSeen <= Duration;
    }

    /// <summary>
    /// Forgets the previous evening. Collectors are held by the session manager and run again for every
    /// session, so a window left open by the last one would let this one sample before its game appears.
    /// </summary>
    public void Reset()
    {
        _lastSeenUtc = null;
    }
}
