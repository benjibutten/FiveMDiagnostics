using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors.Interop;

internal static class WindowsInterop
{
    internal const int AfInet = 2;
    internal const int EnumCurrentSettings = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters ioCounters);

    [DllImport("psapi.dll", SetLastError = true)]
    internal static extern bool GetPerformanceInfo(out PerformanceInformation performanceInformation, int size);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int sizePointer,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetExtendedUdpTable(
        IntPtr udpTable,
        ref int sizePointer,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        UdpTableClass tableClass,
        uint reserved = 0);
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    public uint Size;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonPaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct DevMode
{
    private const int DeviceNameSize = 32;
    private const int FormNameSize = 32;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceNameSize)]
    public string DeviceName;

    public short SpecVersion;
    public short DriverVersion;
    public short Size;
    public short DriverExtra;
    public int Fields;
    public int PositionX;
    public int PositionY;
    public int DisplayOrientation;
    public int DisplayFixedOutput;
    public short Color;
    public short Duplex;
    public short YResolution;
    public short TTOption;
    public short Collate;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = FormNameSize)]
    public string FormName;

    public short LogPixels;
    public int BitsPerPel;
    public int PelsWidth;
    public int PelsHeight;
    public int DisplayFlags;
    public int DisplayFrequency;
    public int IcmMethod;
    public int IcmIntent;
    public int MediaType;
    public int DitherType;
    public int Reserved1;
    public int Reserved2;
    public int PanningWidth;
    public int PanningHeight;
}

internal enum TcpTableClass
{
    BasicListener,
    BasicConnections,
    BasicAll,
    OwnerPidListener,
    OwnerPidConnections,
    OwnerPidAll,
    OwnerModuleListener,
    OwnerModuleConnections,
    OwnerModuleAll,
}

internal enum UdpTableClass
{
    Basic,
    OwnerPid,
    OwnerModule,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MibTcpRowOwnerPid
{
    public uint State;
    public uint LocalAddress;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] LocalPort;

    public uint RemoteAddress;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] RemotePort;

    public uint OwningPid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MibUdpRowOwnerPid
{
    public uint LocalAddress;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] LocalPort;

    public uint OwningPid;
}

internal readonly record struct ProcessMetricSnapshot(
    int ProcessId,
    string ProcessName,
    TimeSpan TotalProcessorTime,
    ulong ReadBytes,
    ulong WriteBytes,
    DateTimeOffset Timestamp,
    long PrivateBytes,
    long WorkingSetBytes,
    int ThreadCount);

internal static class ProcessMetricsReader
{
    public static bool TryRead(Process process, DateTimeOffset timestamp, out ProcessMetricSnapshot snapshot)
    {
        snapshot = default;

        try
        {
            process.Refresh();
            var ioCounters = ReadIoCounters(process);
            snapshot = new ProcessMetricSnapshot(
                process.Id,
                process.ProcessName,
                process.TotalProcessorTime,
                ioCounters.ReadTransferCount,
                ioCounters.WriteTransferCount,
                timestamp,
                process.PrivateMemorySize64,
                process.WorkingSet64,
                process.Threads.Count);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static double ComputeCpuPercent(ProcessMetricSnapshot current, ProcessMetricSnapshot previous)
    {
        var elapsed = (current.Timestamp - previous.Timestamp).TotalMilliseconds;
        if (elapsed <= 0 || current.ProcessId != previous.ProcessId)
        {
            return 0;
        }

        var cpuMs = (current.TotalProcessorTime - previous.TotalProcessorTime).TotalMilliseconds;
        if (cpuMs < 0)
        {
            return 0;
        }

        return Math.Round(cpuMs / elapsed / Environment.ProcessorCount * 100, 1);
    }

    public static long ComputeReadBytesPerSecond(ProcessMetricSnapshot current, ProcessMetricSnapshot previous)
    {
        return ComputeBytesPerSecond(current.ReadBytes, previous.ReadBytes, current.Timestamp - previous.Timestamp);
    }

    public static long ComputeWriteBytesPerSecond(ProcessMetricSnapshot current, ProcessMetricSnapshot previous)
    {
        return ComputeBytesPerSecond(current.WriteBytes, previous.WriteBytes, current.Timestamp - previous.Timestamp);
    }

    private static IoCounters ReadIoCounters(Process process)
    {
        if (!WindowsInterop.GetProcessIoCounters(process.Handle, out var ioCounters))
        {
            return default;
        }

        return ioCounters;
    }

    private static long ComputeBytesPerSecond(ulong current, ulong previous, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || current < previous)
        {
            return 0;
        }

        var delta = current - previous;
        return (long)(delta / Math.Max(elapsed.TotalSeconds, 0.001));
    }
}