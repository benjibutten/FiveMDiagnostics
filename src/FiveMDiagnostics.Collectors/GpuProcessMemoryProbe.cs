using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;

/// <summary>
/// One reading of per-process GPU memory. Behind an interface so the collector's own lifetime — which
/// spans sessions and has to survive a failed one — can be tested without the counter registry.
/// </summary>
internal interface IGpuProcessMemoryProbe : IDisposable
{
    bool TryRead(
        out IReadOnlyList<KeyValuePair<string, long>> dedicated,
        out IReadOnlyList<KeyValuePair<string, long>> shared,
        out string? error);
}

/// <summary>
/// Reads the Windows <c>GPU Process Memory</c> counters for every process at once.
/// </summary>
/// <remarks>
/// This is the source Task Manager uses for its per-process GPU memory column, which matters because
/// the obvious alternative does not work here: NVML's per-process memory queries return "not
/// supported" for graphics processes on a WDDM display driver, which is every consumer Windows
/// machine. The counters are also vendor neutral and include processes NVML never enumerates — the
/// compositor and the browser among them, and those are exactly the ones being accounted for.
/// </remarks>
public sealed class GpuProcessMemoryProbe : IGpuProcessMemoryProbe
{
    private const string DedicatedCounterPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const string SharedCounterPath = @"\GPU Process Memory(*)\Shared Usage";

    /// <summary>
    /// Ceiling on one counter array. Each item is 24 bytes plus its instance name, so this covers tens
    /// of thousands of instances — far past anything real, and a bound worth having before allocating
    /// whatever a driver reports as the required size.
    /// </summary>
    private const uint MaxBufferBytes = 8 * 1024 * 1024;

    private IntPtr _query;
    private IntPtr _dedicatedCounter;
    private IntPtr _sharedCounter;
    private bool _disposed;

    private GpuProcessMemoryProbe(IntPtr query, IntPtr dedicatedCounter, IntPtr sharedCounter)
    {
        _query = query;
        _dedicatedCounter = dedicatedCounter;
        _sharedCounter = sharedCounter;
    }

    /// <summary>
    /// Opens the query once for the session. Returns null with a reason rather than throwing: a machine
    /// whose counter set is missing or corrupted should lose this one table, not its telemetry.
    /// </summary>
    public static GpuProcessMemoryProbe? TryOpen(out string? error)
    {
        error = null;
        var query = IntPtr.Zero;

        try
        {
            var status = PdhInterop.PdhOpenQueryW(null, IntPtr.Zero, out query);
            if (status != PdhInterop.Success)
            {
                error = $"PdhOpenQuery misslyckades (0x{status:X8}).";
                return null;
            }

            status = PdhInterop.PdhAddEnglishCounterW(query, DedicatedCounterPath, IntPtr.Zero, out var dedicated);
            if (status != PdhInterop.Success)
            {
                error = $"Räknaren \"{DedicatedCounterPath}\" kunde inte läggas till (0x{status:X8}). "
                    + "Kör \"lodctr /R\" om prestandaräknarna är trasiga.";
                PdhInterop.PdhCloseQuery(query);
                return null;
            }

            // Shared usage is worth having but not worth failing over: dedicated is the number that has
            // to fit in the card, and shared is the corroborating detail.
            var sharedCounter = PdhInterop.PdhAddEnglishCounterW(query, SharedCounterPath, IntPtr.Zero, out var shared) == PdhInterop.Success
                ? shared
                : IntPtr.Zero;

            return new GpuProcessMemoryProbe(query, dedicated, sharedCounter);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"PDH är inte tillgängligt: {ex.Message}";
            if (query != IntPtr.Zero)
            {
                PdhInterop.PdhCloseQuery(query);
            }

            return null;
        }
    }

    /// <summary>
    /// Collects one reading of both counters, keyed by instance name.
    /// </summary>
    /// <returns>False when the query produced nothing, which is the normal state before a GPU is in use.</returns>
    public bool TryRead(
        out IReadOnlyList<KeyValuePair<string, long>> dedicated,
        out IReadOnlyList<KeyValuePair<string, long>> shared,
        out string? error)
    {
        dedicated = [];
        shared = [];
        error = null;

        if (_disposed)
        {
            error = "Räknaren är stängd.";
            return false;
        }

        var status = PdhInterop.PdhCollectQueryData(_query);
        if (status != PdhInterop.Success)
        {
            error = status == PdhInterop.NoData ? null : $"PdhCollectQueryData misslyckades (0x{status:X8}).";
            return false;
        }

        if (!TryReadCounter(_dedicatedCounter, out dedicated, out error))
        {
            return false;
        }

        if (_sharedCounter != IntPtr.Zero)
        {
            // A failure here is not worth reporting: the caller already has the number it needs.
            TryReadCounter(_sharedCounter, out shared, out _);
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_query != IntPtr.Zero)
        {
            PdhInterop.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        _dedicatedCounter = IntPtr.Zero;
        _sharedCounter = IntPtr.Zero;
    }

    private static bool TryReadCounter(IntPtr counter, out IReadOnlyList<KeyValuePair<string, long>> values, out string? error)
    {
        values = [];
        error = null;

        // The documented two-call pattern: ask with a zero-length buffer to learn the size, then read.
        // The instance set changes between the calls whenever a process exits, so a size that comes back
        // stale simply produces MoreData again on the second call and this returns empty for one poll.
        uint bufferSize = 0;
        var status = PdhInterop.PdhGetFormattedCounterArrayW(counter, PdhInterop.FormatLarge, ref bufferSize, out _, IntPtr.Zero);
        if (status == PdhInterop.NoData || bufferSize == 0)
        {
            return true;
        }

        if (status != PdhInterop.MoreData)
        {
            error = $"PdhGetFormattedCounterArray misslyckades (0x{status:X8}).";
            return false;
        }

        if (bufferSize > MaxBufferBytes)
        {
            error = $"Räknaren begärde {bufferSize / 1024} kB, vilket är mer än gränsen på {MaxBufferBytes / 1024} kB.";
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhInterop.PdhGetFormattedCounterArrayW(counter, PdhInterop.FormatLarge, ref bufferSize, out var itemCount, buffer);
            if (status != PdhInterop.Success)
            {
                error = status == PdhInterop.MoreData
                    ? null
                    : $"PdhGetFormattedCounterArray misslyckades (0x{status:X8}).";
                return status == PdhInterop.MoreData;
            }

            var itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
            var readings = new List<KeyValuePair<string, long>>((int)itemCount);
            for (var index = 0; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(buffer + (index * itemSize));
                if (item.CStatus != PdhInterop.Success || item.Name == IntPtr.Zero)
                {
                    continue;
                }

                if (Marshal.PtrToStringUni(item.Name) is { Length: > 0 } name)
                {
                    readings.Add(new KeyValuePair<string, long>(name, item.Value));
                }
            }

            values = readings;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
