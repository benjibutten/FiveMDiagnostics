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

    /// <summary>
    /// How close in time the two readings have to be for the comparison to be about the accounting
    /// rather than about two clocks.
    /// </summary>
    /// <remarks>
    /// Generous against a five second process interval and a sub-second adapter poll. A stale adapter
    /// reading simply produces no comparison, which is the correct outcome when one cannot be made.
    /// </remarks>
    private static readonly TimeSpan AdapterFreshness = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _interval;

    /// <summary>
    /// Processes proved to double count, kept for the session rather than re-decided per sample.
    /// </summary>
    /// <remarks>
    /// The proof is one-sided and does not expire. A row above the adapter's own figure is impossible
    /// once and stays impossible: the counter is adding memory that belongs to somebody else, and it
    /// does not stop doing that because the card filled up enough to hide it. Deciding per sample would
    /// have excluded <c>dwm</c> from the first incident of the session that prompted this and named it
    /// largest holder in the other 153, since its flat 6.1 GB only exceeded the card's own figure while
    /// the game was still filling its texture memory.
    /// </remarks>
    /// <remarks>
    /// Keyed by process id and held against the name it had, because Windows reuses process ids. A row
    /// proved impossible is a statement about a program's counter, not about a number, and letting the
    /// verdict follow a recycled id would quietly exclude an unrelated process from every report for the
    /// rest of the evening. A reused id arriving under a different name drops the entry; the same name
    /// keeps it, which is the case that matters — the compositor restarting is still the compositor.
    /// </remarks>
    private readonly Dictionary<int, string> _doubleCounted = [];

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
    /// Marks the rows in a sample that cannot be believed, and names the ones proved this time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reconciliation below already measures the only thing that can settle this: a process table
    /// whose sum exceeds what the card reports as used is counting something twice. The same comparison
    /// applied one row at a time names <em>which</em> row, because a single process cannot hold more
    /// memory than the whole card is using. That is not a refinement of the absolute 64 GB bound — it is
    /// the case the absolute bound was always missing. <c>dwm</c> at 6.1 GB on a 10 GB card passed the
    /// bound comfortably and was reported as the largest VRAM holder in all 154 incidents of one
    /// session, which hid the process that was actually growing; the arithmetic that exposed it —
    /// FiveM 5.9 GB plus dwm 6.1 GB on a card reporting 7.8 GB used — is exactly this comparison, done
    /// by hand a session later.
    /// </para>
    /// <para>
    /// It is expected of <c>dwm</c> specifically, and understood: in <c>Composed: Copy with GPU GDI</c>
    /// the compositor holds a reference to every frame it composes and the counter does not distinguish
    /// a shared allocation from an owned one. The rule is written against the arithmetic rather than
    /// against the name, because the next compositor-shaped process will not be called dwm.
    /// </para>
    /// <para>
    /// Silent on a machine with more than one GPU, for the same reason the reconciliation is: the two
    /// figures may then describe different cards, and a difference between two cards is a fact about the
    /// hardware rather than an accusation against a process.
    /// </para>
    /// </remarks>
    public GpuProcessMemorySample Annotate(GpuProcessMemorySample sample, out IReadOnlyList<GpuProcessMemoryUsage> newlyProven)
    {
        newlyProven = [];

        if (!sample.IsAvailable || sample.Processes.Count == 0)
        {
            return sample;
        }

        if (_lastAdapter is { UsedVramBytes: { } adapterBytes, IsSingleAdapterMachine: true } adapter
            && (sample.Timestamp - adapter.Timestamp).Duration() <= AdapterFreshness)
        {
            var ceiling = adapterBytes + ImplausibleOvershootBytes;
            List<GpuProcessMemoryUsage>? proven = null;

            foreach (var process in sample.Processes)
            {
                if (_doubleCounted.TryGetValue(process.ProcessId, out var provenName)
                    && !string.Equals(provenName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    // The id has been recycled to a different program, which inherits nothing.
                    _doubleCounted.Remove(process.ProcessId);
                }

                if (process.DedicatedBytes <= ceiling || _doubleCounted.ContainsKey(process.ProcessId))
                {
                    continue;
                }

                _doubleCounted[process.ProcessId] = process.ProcessName;
                (proven ??= []).Add(process);
            }

            if (proven is not null)
            {
                newlyProven = proven;
            }
        }

        return _doubleCounted.Count == 0
            ? sample
            : sample with { DoubleCountedProcessIds = _doubleCounted.Keys.ToArray() };
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

        if ((sample.Timestamp - adapter.Timestamp).Duration() > AdapterFreshness)
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

        // Named every time rather than once, because this line is also what explains why the reported
        // sum stays above the adapter's figure after the row has been excluded from the reports.
        if (_doubleCounted.Count > 0)
        {
            var names = sample.Processes
                .Where(process => _doubleCounted.ContainsKey(process.ProcessId))
                .Select(process => $"{process.ProcessName} ({process.DedicatedGigabytes:F1} GB)")
                .DefaultIfEmpty($"{_doubleCounted.Count} process(er)")
                .Distinct(StringComparer.OrdinalIgnoreCase);

            message +=
                $" Utesluten ur rapporternas topplistor som bevisat dubbelräknande: {string.Join(", ", names)}.";
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
