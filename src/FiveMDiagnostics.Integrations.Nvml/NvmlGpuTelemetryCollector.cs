using System.Text;

namespace FiveMDiagnostics.Integrations.Nvml;

using FiveMDiagnostics.Core;

/// <summary>
/// Samples GPU utilization, VRAM occupancy, encoder load and throttle state. VRAM occupancy is the
/// signal that separates "the GPU is busy" from "the driver is evicting textures over PCIe", which is
/// the failure mode that produces whole-system stalls rather than merely slow frames.
/// </summary>
public sealed class NvmlGpuTelemetryCollector : ITelemetryCollector, IDisposable
{
    /// <summary>
    /// Tracked separately from <see cref="_deviceReady"/>: NVML requires every successful init to be
    /// matched by a shutdown, and init can succeed even when the device lookup that follows fails.
    /// </summary>
    private bool _nvmlInitialized;

    private bool _deviceReady;
    private bool _unavailable;
    private bool _reportedUnavailable;
    private IntPtr _device;
    private string? _adapterName;

    public string Name => "GpuTelemetry";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        if (!context.Settings.Gpu.Enabled)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (context.ProcessResolver.TryGetTargetProcess() is not null)
                {
                    await context.Writer.WriteAsync(Sample(context), cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(context.Settings.Gpu.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ShutdownNvml();
        }
    }

    public void Dispose()
    {
        ShutdownNvml();
    }

    private GpuTelemetrySample Sample(CollectorContext context)
    {
        var timestamp = context.UtcNow();

        if (!EnsureInitialized(context))
        {
            return Unavailable(timestamp);
        }

        try
        {
            ulong? usedVram = null;
            ulong? totalVram = null;
            if (NvmlInterop.GetMemoryInfo(_device, out var memory) == NvmlInterop.Success)
            {
                usedVram = memory.Used;
                totalVram = memory.Total;
            }

            double? gpuUtilization = null;
            double? memoryUtilization = null;
            if (NvmlInterop.GetUtilizationRates(_device, out var utilization) == NvmlInterop.Success)
            {
                gpuUtilization = utilization.Gpu;
                memoryUtilization = utilization.Memory;
            }

            double? encoderUtilization = NvmlInterop.GetEncoderUtilization(_device, out var encoder, out _) == NvmlInterop.Success
                ? encoder
                : null;

            double? decoderUtilization = NvmlInterop.GetDecoderUtilization(_device, out var decoder, out _) == NvmlInterop.Success
                ? decoder
                : null;

            int? temperature = NvmlInterop.GetTemperature(_device, NvmlInterop.TemperatureSensorGpu, out var celsius) == NvmlInterop.Success
                ? (int)celsius
                : null;

            var throttleReasons = NvmlInterop.GetCurrentClocksThrottleReasons(_device, out var rawReasons) == NvmlInterop.Success
                ? NvmlInterop.DescribeThrottleReasons(rawReasons)
                : [];

            return new GpuTelemetrySample(
                timestamp,
                IsAvailable: true,
                _adapterName,
                gpuUtilization,
                memoryUtilization,
                usedVram,
                totalVram,
                encoderUtilization,
                decoderUtilization,
                temperature,
                throttleReasons);
        }
        catch (DllNotFoundException)
        {
            MarkUnavailable(context, "nvml.dll kunde inte laddas.");
            return Unavailable(timestamp);
        }
        catch (EntryPointNotFoundException ex)
        {
            MarkUnavailable(context, $"NVML-funktion saknas i drivrutinen: {ex.Message}");
            return Unavailable(timestamp);
        }
    }

    private bool EnsureInitialized(CollectorContext context)
    {
        if (_deviceReady)
        {
            return true;
        }

        if (_unavailable)
        {
            return false;
        }

        try
        {
            if (NvmlInterop.Init() != NvmlInterop.Success)
            {
                MarkUnavailable(context, "NVML kunde inte initieras. GPU-telemetri är inaktiv (ingen NVIDIA-GPU?).");
                return false;
            }

            _nvmlInitialized = true;

            if (NvmlInterop.GetDeviceCount(out var count) != NvmlInterop.Success || count == 0)
            {
                MarkUnavailable(context, "NVML hittade ingen GPU.");
                return false;
            }

            if (NvmlInterop.GetDeviceHandleByIndex(0, out _device) != NvmlInterop.Success)
            {
                MarkUnavailable(context, "NVML kunde inte öppna GPU 0.");
                return false;
            }

            var nameBuffer = new StringBuilder(96);
            _adapterName = NvmlInterop.GetDeviceName(_device, nameBuffer, (uint)nameBuffer.Capacity) == NvmlInterop.Success
                ? nameBuffer.ToString()
                : null;

            _deviceReady = true;
            context.StatusSink.Report(StatusLevel.Info, Name, $"GPU-telemetri aktiv för {_adapterName ?? "GPU 0"}.");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            MarkUnavailable(context, $"NVML är inte tillgängligt: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Marks the collector as permanently unavailable and releases NVML if it had already been
    /// initialized, so a failure after a successful init does not leak the library handle.
    /// </summary>
    private void MarkUnavailable(CollectorContext context, string message)
    {
        _unavailable = true;
        _deviceReady = false;
        ShutdownNvml();

        if (_reportedUnavailable)
        {
            return;
        }

        _reportedUnavailable = true;
        context.StatusSink.Report(StatusLevel.Warning, Name, message);
    }

    private static GpuTelemetrySample Unavailable(DateTimeOffset timestamp)
    {
        return new GpuTelemetrySample(timestamp, false, null, null, null, null, null, null, null, null, []);
    }

    private void ShutdownNvml()
    {
        if (!_nvmlInitialized)
        {
            return;
        }

        _nvmlInitialized = false;
        _deviceReady = false;
        _device = IntPtr.Zero;

        try
        {
            NvmlInterop.Shutdown();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing useful to do while tearing down.
        }
    }
}
