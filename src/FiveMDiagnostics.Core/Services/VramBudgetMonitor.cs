namespace FiveMDiagnostics.Core;

/// <summary>
/// Turns the three VRAM figures the session already logs into the budget they add up to.
/// </summary>
/// <remarks>
/// <para>
/// This is the one number in six sessions of investigation that predicted an evening instead of
/// describing one. The card's capacity is fixed, the desktop and the stream stack take what they take
/// before the game starts, and FiveM then fills its texture memory to a ceiling set by the graphics
/// preset and stops there. Written out, that is a subtraction: 7.2 GB of game at High plus 1.1 GB of
/// desktop plus 1.0 GB of stream is 9.3 of 10 GB, and the session measured a median of 88.1% with 4.24%
/// of it above 93%. The same subtraction at Medium predicts 8.2 GB, and that session measured 77.6%.
/// Both within a few percentage points, both computable at the start rather than a week later.
/// </para>
/// <para>
/// What makes it worth a line of its own is that it names the lever. The game's ceiling is the graphics
/// preset and costs image quality to move; the other two gigabytes are programs, and the largest single
/// row of them is usually something nobody is using. "The stream stack holds 1.0 GB" is an actionable
/// sentence in a way that "VRAM peaked at 91%" is not.
/// </para>
/// <para>
/// Deliberately measured as the card's own figure minus the game's row rather than as a sum of the
/// other rows. The per-process table double counts — see <see cref="VramAccountingMonitor"/> — and a
/// budget built by adding its rows together would inherit that error whole. The subtraction cannot: it
/// rests on the adapter's total, which is the one figure nothing in the table can inflate.
/// </para>
/// </remarks>
public sealed class VramBudgetMonitor
{
    /// <summary>
    /// Bytes a stream process has to hold before the stack counts as running.
    /// </summary>
    /// <remarks>
    /// OBS is present in the counter table from the moment it launches and holds a few tens of megabytes
    /// while it is idle. What the budget is about is the stack that is actually encoding, which arrives
    /// at hundreds of megabytes, so a threshold keeps a launch from being reported as a change and back
    /// again a poll later.
    /// </remarks>
    private const ulong StreamStackPresentBytes = 128UL * 1024 * 1024;

    /// <summary>
    /// Bytes below the table's cut that are worth mentioning. Under this the split is accurate to the
    /// tenth of a gigabyte the line is printed to.
    /// </summary>
    private const ulong MaterialHiddenBytes = 64UL * 1024 * 1024;

    private static readonly TimeSpan AdapterFreshness = TimeSpan.FromSeconds(10);

    private GpuTelemetrySample? _lastAdapter;
    private bool _reported;
    private bool _streamStackPresent;

    /// <summary>Notes the most recent adapter reading, which the next process table is measured against.</summary>
    /// <remarks>
    /// Only from a machine with one GPU, and that is the whole of the guard rather than a caveat on the
    /// output. NVML opens device index 0 and reports that card for the session while the process table is
    /// anchored on whichever adapter the game holds memory on, so with a second NVIDIA device present the
    /// subtraction below is one card's total minus another card's row — a desktop figure that is not
    /// wrong by a little. <see cref="VramAccountingMonitor"/> can afford to publish its comparison with a
    /// caveat because it is a reconciliation nobody acts on directly; this is a budget somebody chooses a
    /// graphics preset against, and a wrong number with a footnote is worse than no number. The
    /// reconciliation line already explains the two-GPU case every half hour.
    /// </remarks>
    public void Observe(GpuTelemetrySample sample)
    {
        if (sample is { IsAvailable: true, UsedVramBytes: not null, TotalVramBytes: > 0, IsSingleAdapterMachine: true })
        {
            _lastAdapter = sample;
        }
    }

    /// <summary>
    /// Returns the budget line when it is worth writing, and null otherwise.
    /// </summary>
    /// <remarks>
    /// Written once at the start, and again whenever the stream stack starts or stops — which is the
    /// only term of the three that changes during an evening, and changes by a gigabyte when it does.
    /// Everything else is a constant the session is spent inside.
    /// </remarks>
    public VramBudgetReport? Observe(GpuProcessMemorySample sample)
    {
        if (!sample.IsAvailable || sample.Processes.Count == 0)
        {
            return null;
        }

        if (_lastAdapter is not { UsedVramBytes: { } usedBytes, TotalVramBytes: { } totalBytes } adapter
            || (sample.Timestamp - adapter.Timestamp).Duration() > AdapterFreshness)
        {
            return null;
        }

        var believable = sample.Processes.Where(process => !sample.IsUnbelievable(process)).ToArray();
        var gameBytes = believable.Where(IsGame).Aggregate(0UL, (total, process) => total + process.DedicatedBytes);
        if (gameBytes == 0)
        {
            // Nothing to budget for yet: the game either has not allocated anything or is not running,
            // and a line about the desktop alone answers no question anybody has.
            return null;
        }

        var streamBytes = believable.Where(IsStreamStack).Aggregate(0UL, (total, process) => total + process.DedicatedBytes);

        // The table is cut to the largest holders, and the stream stack is several processes of which
        // the browser sources are small enough to fall under a low cut. What that costs is the split
        // rather than the budget: the desktop figure is a residual, so a stream row below the cut is
        // counted as desktop and the total the game does not get stays exactly right. Said out loud
        // when there is enough of it to move the split, because the two halves are what the advice is
        // given against — the stream stack can be closed and the desktop mostly cannot.
        var hiddenBytes = HiddenBytes(sample);

        // Everything the game does not hold, taken from the card's own figure so the table's double
        // counting cannot reach it. Floored because the two collectors sample at different instants and
        // a game that grew between them would otherwise produce a negative desktop.
        var otherBytes = usedBytes > gameBytes ? usedBytes - gameBytes : 0;
        var desktopBytes = otherBytes > streamBytes ? otherBytes - streamBytes : 0;

        var streamPresent = streamBytes >= StreamStackPresentBytes;
        if (_reported && streamPresent == _streamStackPresent)
        {
            return null;
        }

        var transition = _reported
            ? streamPresent ? "Streamstacken startade. " : "Streamstacken avslutades. "
            : string.Empty;

        _reported = true;
        _streamStackPresent = streamPresent;

        // What the game may still grow into, which is the number the graphics preset is chosen against.
        var reservedBytes = desktopBytes + streamBytes;
        var headroomBytes = totalBytes > reservedBytes ? totalBytes - reservedBytes : 0;

        var message =
            $"{transition}VRAM-budget: skrivbordet håller {Gigabytes(desktopBytes)} och streamstacken "
            + $"{Gigabytes(streamBytes)}, så spelet har {Gigabytes(headroomBytes)} kvar av kortets "
            + $"{Gigabytes(totalBytes)} och håller nu {Gigabytes(gameBytes)}. Spelets tak sätts av "
            + "texturinställningen; utrymme för ett steg upp tas ur de två första posterna, inte ur kortet.";

        if (hiddenBytes >= MaterialHiddenBytes)
        {
            message +=
                $" {Gigabytes(hiddenBytes)} ligger i processer under topplistans gräns och räknas som "
                + "skrivbord; höj Gpu.ProcessMemoryTopCount om uppdelningen ska stämma på posten.";
        }

        return new VramBudgetReport(message, desktopBytes, streamBytes, gameBytes, headroomBytes, totalBytes);
    }

    /// <summary>
    /// Dedicated bytes held by processes the table did not list, which the residual counts as desktop.
    /// </summary>
    /// <remarks>
    /// The untruncated total already travels with the sample for the reconciliation's sake. Both sides
    /// exclude only the absolutely impossible rows, so the difference is the rows below the cut and
    /// nothing else — in particular a row proved to double count is inside both and cancels.
    /// </remarks>
    private static ulong HiddenBytes(GpuProcessMemorySample sample)
    {
        var listed = sample.Processes
            .Where(process => !process.IsImplausible)
            .Aggregate(0UL, (total, process) => total + process.DedicatedBytes);

        return sample.AccountedDedicatedBytes > listed ? sample.AccountedDedicatedBytes - listed : 0;
    }

    /// <summary>
    /// The game's own rows. FiveM runs as more than one process and the launcher's name carries the
    /// build number, so this matches the two names rather than an exact one.
    /// </summary>
    private static bool IsGame(GpuProcessMemoryUsage process) =>
        process.ProcessName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)
        || process.ProcessName.Contains("GTA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The capture stack: the encoder and the browser sources it hosts, which are separate processes and
    /// separate rows.
    /// </summary>
    private static bool IsStreamStack(GpuProcessMemoryUsage process) =>
        process.ProcessName.StartsWith("obs", StringComparison.OrdinalIgnoreCase);

    private static string Gigabytes(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:F1} GB";
}

/// <summary>One statement of what the card's memory is committed to before the game asks for any.</summary>
public sealed record VramBudgetReport(
    string Message,
    ulong DesktopBytes,
    ulong StreamStackBytes,
    ulong GameBytes,
    ulong GameHeadroomBytes,
    ulong AdapterTotalBytes);
