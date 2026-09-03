using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors.Interop;

/// <summary>One process as the kernel reported it, with the session it belongs to.</summary>
internal readonly record struct ProcessTableRow(ProcessMetricSnapshot Snapshot, int SessionId);

/// <summary>
/// Reads CPU time, I/O totals and session for every process on the machine in a single system call.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the sweep that dominated the app's own cost. The previous version enumerated the
/// process table and then opened each process to read four properties off it, which is one handle and
/// several system calls per process: 168 ms for 265 processes, every two seconds, measured. The trace
/// of 2 September shows the app holding 0.33–0.40 cores through the whole session — fourth heaviest
/// process on the machine, ahead of the compositor and the voice changer — and 95% of that time was in
/// <c>ntoskrnl.exe</c>, which is what a sweep like that looks like from the outside. A diagnostics tool
/// that costs a third of a core to watch a game short of cores is measuring its own weather.
/// </para>
/// <para>
/// The kernel already keeps all of it in one place. <c>NtQuerySystemInformation</c> with
/// <c>SystemProcessInformation</c> returns the entire table — image name, session, kernel and user time,
/// read and write transfer counts — in one call: <b>2.6 ms against 168 ms</b>, measured on the same
/// machine in the same minute.
/// </para>
/// <para>
/// It is also more complete, which was the second problem. Opening a process requires rights the app
/// does not always have, so the old sweep silently dropped every process it could not open — 132 of 265
/// without elevation, including <c>MsMpEng.exe</c> and <c>SearchIndexer.exe</c>, the two most likely
/// background causes of a stutter there are. This call needs no handle and returns them all.
/// </para>
/// <para>
/// Undocumented but not unstable: it is what Task Manager and Process Explorer read, the layout below
/// has been fixed since Windows Vista, and every field this class uses is in the first half of the
/// structure. It is still treated as something that can fail — a null return sends the collector back
/// to the handle-based sweep rather than costing it the process table.
/// </para>
/// </remarks>
internal static class ProcessTableReader
{
    /// <summary><c>SystemProcessInformation</c>.</summary>
    private const int SystemProcessInformationClass = 5;

    /// <summary><c>STATUS_INFO_LENGTH_MISMATCH</c>, the kernel asking for a bigger buffer.</summary>
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    /// <summary>
    /// First buffer tried. A machine with 265 processes needs about 200 KB, so this usually succeeds on
    /// the first call and the growth loop below never runs.
    /// </summary>
    private const int InitialBufferBytes = 512 * 1024;

    /// <summary>
    /// Ceiling on the buffer. Well past any real machine, and a bound worth having before allocating
    /// whatever a system call asks for.
    /// </summary>
    private const int MaxBufferBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The idle process, which is not a process. Its "CPU time" is every idle tick the machine has ever
    /// had and it would own the top of the list for ever.
    /// </summary>
    private const int IdleProcessId = 0;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int systemInformationClass, IntPtr buffer, int length, out int returnLength);

    /// <summary>
    /// Every process on the machine, or null when the call failed and the caller should fall back.
    /// </summary>
    public static IReadOnlyList<ProcessTableRow>? TryRead(DateTimeOffset timestamp)
    {
        var size = InitialBufferBytes;
        var buffer = IntPtr.Zero;

        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(size);
                var status = NtQuerySystemInformation(SystemProcessInformationClass, buffer, size, out var needed);
                if (status == 0)
                {
                    break;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;

                // Anything other than "too small" is a failure this class has no answer for.
                if (status != StatusInfoLengthMismatch)
                {
                    return null;
                }

                // The table grows between the size query and the read, so ask for more than it said.
                size = Math.Max(needed + (64 * 1024), size * 2);
                if (size > MaxBufferBytes)
                {
                    return null;
                }
            }

            return Parse(buffer, size, timestamp);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Walks the linked list of entries, refusing to step outside the buffer.
    /// </summary>
    /// <remarks>
    /// The offsets come from outside managed memory, so they are checked rather than trusted: a
    /// malformed chain would otherwise read whatever follows the allocation. Returns null on the first
    /// sign of one, which sends the caller back to the sweep that cannot be corrupted this way.
    /// </remarks>
    private static IReadOnlyList<ProcessTableRow>? Parse(IntPtr buffer, int size, DateTimeOffset timestamp)
    {
        var entrySize = Marshal.SizeOf<SystemProcessInformation>();
        var rows = new List<ProcessTableRow>(400);
        var offset = 0;

        while (true)
        {
            if (offset < 0 || offset + entrySize > size)
            {
                return null;
            }

            var entry = Marshal.PtrToStructure<SystemProcessInformation>(buffer + offset);
            var processId = (int)entry.UniqueProcessId;

            if (processId != IdleProcessId)
            {
                rows.Add(new ProcessTableRow(
                    new ProcessMetricSnapshot(
                        processId,
                        ImageName(entry),

                        // Kernel and user time, in 100 ns units — the same total
                        // Process.TotalProcessorTime reports, from the same source.
                        new TimeSpan(entry.KernelTime + entry.UserTime),
                        (ulong)Math.Max(entry.ReadTransferCount, 0),
                        (ulong)Math.Max(entry.WriteTransferCount, 0),
                        timestamp,
                        (long)entry.PrivatePageCount,
                        (long)entry.WorkingSetSize,

                        // Threads are counted per process here too, but only the target process reports
                        // one and it reads its own the expensive way.
                        ThreadCount: 0),
                    (int)entry.SessionId));
            }

            if (entry.NextEntryOffset == 0)
            {
                return rows;
            }

            offset += (int)entry.NextEntryOffset;
        }
    }

    /// <summary>
    /// The image name without its extension, which is what <c>Process.ProcessName</c> reports and what
    /// every name comparison downstream is written against.
    /// </summary>
    /// <remarks>
    /// The kernel gives "SearchIndexer.exe" where the managed API gives "SearchIndexer". Names with dots
    /// in them survive: "OneDrive.Sync.Service.exe" loses only the last extension. The system process
    /// has no image name at all and keeps the name the managed API gives it.
    /// </remarks>
    private static string ImageName(SystemProcessInformation entry)
    {
        if (entry.ImageName.Buffer == IntPtr.Zero || entry.ImageName.Length == 0)
        {
            return (int)entry.UniqueProcessId == 4 ? "System" : $"pid {(int)entry.UniqueProcessId}";
        }

        var name = Marshal.PtrToStringUni(entry.ImageName.Buffer, entry.ImageName.Length / 2);
        return string.IsNullOrEmpty(name) ? $"pid {(int)entry.UniqueProcessId}" : Path.GetFileNameWithoutExtension(name);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>
    /// <c>SYSTEM_PROCESS_INFORMATION</c>, in the x64 layout Windows has used since Vista.
    /// </summary>
    /// <remarks>
    /// Declared in full rather than up to the last field of interest, because the entries are a linked
    /// list walked by <c>NextEntryOffset</c> and a short structure would still marshal correctly — but
    /// would silently stop matching if a field before the I/O counters ever moved. Every field is named
    /// so that a mismatch shows up as nonsense in a value somebody reads rather than as a wrong offset
    /// nobody notices.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }
}
