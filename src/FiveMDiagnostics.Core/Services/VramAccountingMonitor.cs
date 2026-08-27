namespace FiveMDiagnostics.Core;

/// <summary>
/// Compares the per-process VRAM table against the adapter's own figure, and says so in the session log.
/// </summary>
/// <remarks>
/// <para>
/// The two numbers come from different places — the Windows <c>GPU Process Memory</c> counter set and
/// NVML — and neither is expected to match the other exactly: VRAM also holds the display's own
/// framebuffers and allocations belonging to nothing still running, so the process sum normally sits a
/// little under the adapter's. What matters is that the gap stays put.
/// </para>
/// <para>
/// It did not. Across two consecutive sessions on the same machine the sum went from +0.17 GB over the
/// adapter to +1.11 GB over it, which means roughly a gigabyte was being counted twice somewhere in the
/// table. Nothing in the app noticed. It was found by exporting both logs and doing the arithmetic by
/// hand, a session later, and until then the per-process table read as authoritative — the same failure
/// as the 213 GB row, one level quieter, because every individual figure looked reasonable.
/// </para>
/// <para>
/// One line at the start of a session and one every half hour is enough to make it answerable from the
/// journal alone. The check costs two numbers that are already in the pump.
/// </para>
/// </remarks>
public sealed class VramAccountingMonitor
{
    /// <summary>
    /// Gap beyond which the sum is double counting rather than merely failing to see everything.
    /// </summary>
    /// <remarks>
    /// A sum that exceeds the adapter's own figure is wrong by construction: a process cannot hold VRAM
    /// the card does not report as used. Half a gigabyte of slack absorbs the sampling skew between two
    /// collectors on different intervals — the two figures are never read at the same instant — while
    /// still catching the case that prompted this, which was more than twice it.
    /// </remarks>
    public const long ImplausibleOvershootBytes = 512L * 1024 * 1024;

    private readonly TimeSpan _interval;

    private GpuTelemetrySample? _lastAdapter;
    private DateTimeOffset? _lastReportAt;

    public VramAccountingMonitor()
        : this(TimeSpan.FromMinutes(30))
    {
    }

    public VramAccountingMonitor(TimeSpan interval)
    {
        _interval = interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : interval;
    }

    /// <summary>Notes the most recent adapter reading, which the next process sample is compared against.</summary>
    public void Observe(GpuTelemetrySample sample)
    {
        if (sample is { IsAvailable: true, UsedVramBytes: not null })
        {
            _lastAdapter = sample;
        }
    }

    /// <summary>
    /// Compares a process table against the last adapter reading, returning a line to log when one is
    /// due, and null otherwise.
    /// </summary>
    /// <remarks>
    /// The two samples have to be close in time or the comparison measures the gap between two clocks
    /// rather than between two accountings. Ten seconds is generous against a five second process
    /// interval and a sub-second adapter poll, and a stale adapter reading simply produces no line —
    /// silence is correct when the comparison cannot be made.
    /// </remarks>
    public VramAccountingReport? Observe(GpuProcessMemorySample sample)
    {
        if (!sample.IsAvailable || sample.Processes.Count == 0)
        {
            return null;
        }

        if (_lastAdapter is not { UsedVramBytes: { } adapterBytes } adapter)
        {
            return null;
        }

        if ((sample.Timestamp - adapter.Timestamp).Duration() > TimeSpan.FromSeconds(10))
        {
            return null;
        }

        if (_lastReportAt is { } last && sample.Timestamp - last < _interval)
        {
            return null;
        }

        _lastReportAt = sample.Timestamp;

        var processBytes = sample.AccountedDedicatedBytes;
        var differenceBytes = (long)processBytes - (long)adapterBytes;

        // The two figures describe the same card only when there is one card. NVML reads device index 0
        // for the whole session; the process table is anchored on whichever adapter the game holds memory
        // on. On a machine with a second NVIDIA device those can be different GPUs, and their difference
        // would then be a fact about the hardware rather than about the accounting — so the comparison is
        // still reported, and is not allowed to accuse anything.
        var sameAdapter = adapter.IsSingleAdapterMachine;
        var implausible = sameAdapter && differenceBytes > ImplausibleOvershootBytes;

        var message =
            $"VRAM-avstämning: processumman är {Gigabytes(processBytes)} och kortet rapporterar "
            + $"{Gigabytes(adapterBytes)} använt, differens {Signed(differenceBytes)}.";

        if (implausible)
        {
            message +=
                " Summan överstiger kortets egen siffra med mer än en halv gigabyte, vilket inte är möjligt: "
                + "minst en processrad dubbelräknar. Jämför raderna mot föregående session innan tabellen används.";
        }
        else if (!sameAdapter)
        {
            message +=
                " Maskinen har mer än en GPU, eller så rapporterade drivrutinen inget antal. Kortets siffra "
                + "kommer från GPU 0 och processtabellen från spelets adapter, så differensen kan vara två "
                + "olika kort och tolkas inte som ett fel i tabellen.";
        }

        return new VramAccountingReport(message, implausible, processBytes, adapterBytes, differenceBytes);
    }

    private static string Gigabytes(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:F2} GB";

    private static string Signed(long bytes)
    {
        var gigabytes = bytes / 1024d / 1024 / 1024;
        return $"{(bytes >= 0 ? "+" : "-")}{Math.Abs(gigabytes):F2} GB";
    }
}

/// <summary>One reconciliation of the per-process table against the adapter's own figure.</summary>
public sealed record VramAccountingReport(
    string Message,
    bool IsImplausible,
    ulong ProcessSumBytes,
    ulong AdapterUsedBytes,
    long DifferenceBytes);
