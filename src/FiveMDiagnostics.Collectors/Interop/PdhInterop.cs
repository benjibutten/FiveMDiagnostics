using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors.Interop;

/// <summary>
/// The slice of PDH needed to read one wildcard counter across every instance in a single call.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.PerformanceCounter"/> is used elsewhere in this project and is the
/// easier API, but it cannot do this: a wildcard has to be expanded into one counter object per
/// instance, and <c>GPU Process Memory</c> has an instance for every process holding a GPU allocation,
/// several per process on a multi-adapter machine. Rebuilding dozens of counter objects every poll —
/// and handling the ones that vanish between enumeration and read — costs more than the query it is
/// meant to perform.
/// <para>
/// <c>PdhAddEnglishCounter</c> rather than <c>PdhAddCounter</c>: counter paths are localised, and this
/// runs on a Swedish Windows. The English form is guaranteed to resolve regardless of display language.
/// </para>
/// </remarks>
internal static class PdhInterop
{
    internal const uint Success = 0;

    /// <summary>Returned when the supplied buffer is too small; the required size comes back with it.</summary>
    internal const uint MoreData = 0x800007D2;

    /// <summary>The counter resolved but no instance reported a value. Normal, not a failure.</summary>
    internal const uint NoData = 0x800007D5;

    /// <summary>Values as 64-bit integers, which is what a byte gauge is.</summary>
    internal const uint FormatLarge = 0x00000400;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>
/// One instance's value in the array <c>PdhGetFormattedCounterArrayW</c> writes.
/// </summary>
/// <remarks>
/// Mirrors <c>PDH_FMT_COUNTERVALUE_ITEM_W</c>. The explicit padding is the union alignment in
/// <c>PDH_FMT_COUNTERVALUE</c>: the value is eight bytes and therefore eight-byte aligned, so a
/// four-byte hole follows <c>CStatus</c> on 64-bit. Reading the struct without it returns the status
/// word shifted into the value, which looks like a plausible byte count rather than an error.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct PdhFormattedCounterValueItem
{
    /// <summary>Pointer to the instance name, e.g. <c>pid_29268_luid_0x00000000_0x0000c42a_phys_0</c>.</summary>
    internal IntPtr Name;

    internal uint CStatus;

    /// <summary>The union's alignment hole. Never read; present so the layout cannot drift.</summary>
    internal uint Padding;

    internal long Value;
}
