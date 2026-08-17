using System.Runtime.InteropServices;
using System.Text;

namespace FiveMDiagnostics.Integrations.Nvml;

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

[Flags]
internal enum NvmlThrottleReason : ulong
{
    None = 0,
    GpuIdle = 1UL << 0,
    ApplicationsClocksSetting = 1UL << 1,
    SwPowerCap = 1UL << 2,
    HwSlowdown = 1UL << 3,
    SyncBoost = 1UL << 4,
    SwThermalSlowdown = 1UL << 5,
    HwThermalSlowdown = 1UL << 6,
    HwPowerBrakeSlowdown = 1UL << 7,
    DisplayClockSetting = 1UL << 8,
}

/// <summary>
/// Minimal NVML binding. nvml.dll ships with the NVIDIA display driver and lives in System32, so the
/// import resolves without any extra install — but every entry point still has to degrade quietly on
/// machines with no NVIDIA GPU.
/// </summary>
internal static class NvmlInterop
{
    private const string Library = "nvml.dll";

    internal const int Success = 0;
    internal const int TemperatureSensorGpu = 0;

    [DllImport(Library, EntryPoint = "nvmlInit_v2")]
    internal static extern int Init();

    [DllImport(Library, EntryPoint = "nvmlShutdown")]
    internal static extern int Shutdown();

    [DllImport(Library, EntryPoint = "nvmlDeviceGetCount_v2")]
    internal static extern int GetDeviceCount(out uint deviceCount);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    internal static extern int GetDeviceHandleByIndex(uint index, out IntPtr device);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    internal static extern int GetDeviceName(IntPtr device, StringBuilder name, uint length);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    internal static extern int GetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    internal static extern int GetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetEncoderUtilization")]
    internal static extern int GetEncoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetDecoderUtilization")]
    internal static extern int GetDecoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperature")]
    internal static extern int GetTemperature(IntPtr device, int sensorType, out uint temperature);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
    internal static extern int GetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);

    internal static IReadOnlyList<string> DescribeThrottleReasons(ulong raw)
    {
        var reasons = (NvmlThrottleReason)raw;
        if (reasons is NvmlThrottleReason.None or NvmlThrottleReason.GpuIdle)
        {
            return [];
        }

        var described = new List<string>();
        foreach (var candidate in Enum.GetValues<NvmlThrottleReason>())
        {
            if (candidate is NvmlThrottleReason.None or NvmlThrottleReason.GpuIdle)
            {
                continue;
            }

            if (reasons.HasFlag(candidate))
            {
                described.Add(candidate.ToString());
            }
        }

        return described;
    }
}
