namespace FiveMDiagnostics.Core;

/// <summary>
/// What the card gives back when the game closes.
/// </summary>
/// <remarks>
/// <para>
/// Every VRAM figure this investigation has is from minutes the game was running, and all of them
/// answer the same question — how full the card was — without answering whose memory it was. The
/// minutes after the game exits answer it directly and cost nothing to take: whatever the card is
/// still holding once the process is gone belongs to something else.
/// </para>
/// <para>
/// It matters because the band is the mechanism. A card that falls to 40% the moment FiveM exits was
/// full of the game; one that stays at 89% was already most of the way into the band before the game
/// started, and the evening's pressure was decided by what is running beside it.
/// </para>
/// <para>
/// Single-adapter readings only, the same guard the adapter history keeps. NVML reports device index 0
/// for the whole session, and on a machine with a second NVIDIA device that is not necessarily the card
/// the game rendered on.
/// </para>
/// </remarks>
public sealed class PostGameVramRelease
{
    private readonly object _sync = new();

    private Reading? _whileRunning;
    private DateTimeOffset? _exitedAt;
    private Reading? _lowestAfter;
    private Reading? _latestAfter;
    private int _samplesAfter;

    public void Observe(GpuTelemetrySample sample)
    {
        if (!sample.IsAvailable || !sample.IsSingleAdapterMachine || sample.VramUsagePercent is not { } percent)
        {
            return;
        }

        var reading = new Reading(sample.Timestamp, percent, (sample.UsedVramBytes ?? 0) / (double)(1024 * 1024 * 1024));

        lock (_sync)
        {
            if (_exitedAt is null)
            {
                _whileRunning = reading;
                return;
            }

            // A reading stamped before the exit was sampled before it. The poll and the exit check run on
            // separate loops, so the first sample through here can still describe the running game.
            if (reading.At < _exitedAt)
            {
                return;
            }

            _samplesAfter++;
            _latestAfter = reading;

            if (_lowestAfter is not { } lowest || reading.Percent < lowest.Percent)
            {
                _lowestAfter = reading;
            }
        }
    }

    public void NoteGameExit(DateTimeOffset at)
    {
        lock (_sync)
        {
            _exitedAt ??= at;
        }
    }

    /// <summary>
    /// Takes back an exit the game came back from.
    /// </summary>
    /// <remarks>
    /// A restart inside the tail is one evening, not two, and the minutes between the two processes are
    /// a game loading rather than a card releasing. Measuring them as a release would report the deepest
    /// dip of the evening as what the game gave back, when the game took it straight back again.
    /// </remarks>
    public void NoteGameRunning()
    {
        lock (_sync)
        {
            _exitedAt = null;
            _lowestAfter = null;
            _latestAfter = null;
            _samplesAfter = 0;
        }
    }

    public PostGameVramReport? Summary()
    {
        lock (_sync)
        {
            if (_exitedAt is not { } exitedAt
                || _whileRunning is not { } running
                || _lowestAfter is not { } lowest
                || _latestAfter is not { } latest)
            {
                return null;
            }

            return new PostGameVramReport(
                running.Percent,
                running.UsedGb,
                lowest.Percent,
                lowest.UsedGb,
                lowest.At - exitedAt,
                latest.At - exitedAt,
                _samplesAfter);
        }
    }

    private readonly record struct Reading(DateTimeOffset At, double Percent, double UsedGb);
}

/// <param name="TimeToLowest">How long after the exit the card reached its lowest point.</param>
/// <param name="Measured">How much of the tail was measured, which is what a "stayed at" claim rests on.</param>
public sealed record PostGameVramReport(
    double PercentWhileRunning,
    double UsedGbWhileRunning,
    double LowestPercentAfter,
    double LowestUsedGbAfter,
    TimeSpan TimeToLowest,
    TimeSpan Measured,
    int Samples)
{
    /// <summary>Whether the card never left the band the evening's hitches cluster in.</summary>
    public bool StillHeld => LowestPercentAfter >= VramPressureBandMonitor.BandPercent;

    public string Message => StillHeld
        ? $"VRAM efter spelet: kortet stannade på {LowestPercentAfter:F0} % ({LowestUsedGbAfter:F1} GB) under "
            + $"{Measured.TotalMinutes:F0} min efter att spelet stängdes, mot {PercentWhileRunning:F0} % medan det "
            + $"kördes. Det som ligger kvar är inte spelets, och bandet på {VramPressureBandMonitor.BandPercent:F0} % "
            + "är alltså redan halvfullt när nästa kväll börjar."
        : $"VRAM efter spelet: kortet gick från {PercentWhileRunning:F0} % ({UsedGbWhileRunning:F1} GB) medan spelet "
            + $"kördes till {LowestPercentAfter:F0} % ({LowestUsedGbAfter:F1} GB) {TimeToLowest.TotalMinutes:F1} min "
            + $"efter att det stängdes, mätt på {Samples} avläsningar. Minnet var spelets.";
}
