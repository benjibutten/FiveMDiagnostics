namespace FiveMDiagnostics.Core;

/// <summary>
/// Keeps how little RAM the machine had, for the session summary to write.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SystemTelemetrySample.AvailableMemoryMb"/> and
/// <see cref="SystemTelemetrySample.MemoryCommitPercent"/> have been collected every second since the
/// app was written, shown live in the window, and then thrown away. They reached no log, no incident and
/// no rule.
/// </para>
/// <para>
/// That cost the investigation of 5 September its root cause. The evening's every long freeze was the
/// game page-faulting against a slow disk, which happens when Windows has trimmed working sets — and
/// whether the machine was short of memory was the one question the session log could not answer, about
/// a figure the app had sampled sixteen thousand times.
/// </para>
/// <para>
/// A minimum and a maximum rather than an average: memory pressure is an excursion. Half an hour at
/// 12 GB free with ninety seconds at 300 MB averages to something comfortable and it is the ninety
/// seconds that trims the game's pages out to the paging file.
/// </para>
/// <para>
/// An accumulator and nothing more. It kept a fifteen minute cadence of its own as well, and the session
/// writes its summaries on a fifteen minute cadence that starts in the same second — so the line went
/// out twice, one to three seconds apart, forty-nine times on the evening of 6 September where it should
/// have gone out twenty-five. The duplicate got past the log's own repeat check because the two copies
/// differ: the sample count moves between them. One clock is enough, and the one that belongs to the
/// session is the one that has to stay.
/// </para>
/// </remarks>
public sealed class SystemMemoryMonitor
{
    private readonly object _sync = new();

    private ulong _lowestAvailableMb = ulong.MaxValue;
    private double _highestCommitPercent;
    private ulong _latestAvailableMb;
    private double _latestCommitPercent;
    private int _samples;

    /// <summary>
    /// Folds one reading in.
    /// </summary>
    /// <remarks>
    /// An unmeasured sample is dropped rather than folded in: letting one through would pin the session
    /// minimum at zero for the rest of the evening.
    /// </remarks>
    public void Observe(SystemTelemetrySample sample)
    {
        lock (_sync)
        {
            if (!sample.HasMemoryReading)
            {
                return;
            }

            _samples++;
            _latestAvailableMb = sample.AvailableMemoryMb;
            _latestCommitPercent = sample.MemoryCommitPercent;
            _lowestAvailableMb = Math.Min(_lowestAvailableMb, sample.AvailableMemoryMb);
            _highestCommitPercent = Math.Max(_highestCommitPercent, sample.MemoryCommitPercent);
        }
    }

    /// <summary>The session's figures so far, or null when nothing was measured.</summary>
    public SystemMemoryReport? Summary()
    {
        lock (_sync)
        {
            return _samples == 0 ? null : BuildLocked();
        }
    }

    /// <summary>Called under the lock.</summary>
    private SystemMemoryReport BuildLocked()
    {
        return new SystemMemoryReport(
            _samples,
            _lowestAvailableMb,
            _highestCommitPercent,
            _latestAvailableMb,
            _latestCommitPercent);
    }
}

/// <param name="LowestAvailableMb">The least free RAM the machine ever had this session.</param>
/// <param name="HighestCommitPercent">
/// The most of the commit limit that was ever promised. Commit above the machine's RAM is what makes
/// Windows trim working sets, so this is the figure that predicts paging rather than merely following
/// it.
/// </param>
public sealed record SystemMemoryReport(
    int Samples,
    ulong LowestAvailableMb,
    double HighestCommitPercent,
    ulong CurrentAvailableMb,
    double CurrentCommitPercent)
{
    /// <summary>
    /// Free RAM below which Windows starts trimming working sets out to the paging file.
    /// </summary>
    /// <remarks>
    /// Not a hard threshold in Windows and not presented as one. It is the level the 5 September session
    /// wanted to stay above — the review's own "loggas, och stannar över 3 GB" — and it is what turns a
    /// number in a log into a line somebody can act on.
    /// </remarks>
    public const ulong ComfortableAvailableMb = 3 * 1024;

    public bool IsTight => LowestAvailableMb < ComfortableAvailableMb;

    public string Message
    {
        get
        {
            var verdict = IsTight
                ? " Under 3 GB börjar Windows trimma arbetsmängder ut till växlingsfilen, och varje "
                    + "återbesök i det utsidade minnet kostar en diskläsning — det är den vägen ett "
                    + "spel fryser en halv sekund utan att någon räknare visar något."
                : string.Empty;

            return $"Minne: minst {LowestAvailableMb / 1024d:F1} GB ledigt RAM under sessionen, högsta commit "
                + $"{HighestCommitPercent:F0} %. Just nu {CurrentAvailableMb / 1024d:F1} GB ledigt och "
                + $"{CurrentCommitPercent:F0} % commit, mätt på {Samples} avläsningar.{verdict}";
        }
    }
}
