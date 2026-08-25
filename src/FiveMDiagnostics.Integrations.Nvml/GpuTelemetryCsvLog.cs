using System.Globalization;

namespace FiveMDiagnostics.Integrations.Nvml;

using FiveMDiagnostics.Core;

/// <summary>
/// Appends every GPU sample to a CSV for the length of a session. The file plumbing — append
/// semantics, the size budget and the recorded failure — lives in <see cref="RollingCsvLog"/>.
/// </summary>
public sealed class GpuTelemetryCsvLog : IDisposable
{
    public const long DefaultMaxBytes = RollingCsvLog.DefaultMaxBytes;

    private const string Header =
        "timestampUtc,isAvailable,adapterName,utilizationPercent,memoryBandwidthPercent,"
        + "usedVramBytes,totalVramBytes,vramUsagePercent,encoderPercent,decoderPercent,temperatureCelsius,throttleReasons";

    private readonly RollingCsvLog _log;

    private GpuTelemetryCsvLog(RollingCsvLog log)
    {
        _log = log;
    }

    /// <summary>Full path of the file being written, for the status entry that tells the user where it is.</summary>
    public string Path => _log.Path;

    /// <summary>Set once the file has been closed, whether by disposal, an IO failure or the size budget.</summary>
    public string? Failure => _log.Failure;

    /// <summary>
    /// Opens the log for a session. Returns null with a reason rather than throwing: a session that
    /// cannot write this file is still a session worth running.
    /// </summary>
    public static GpuTelemetryCsvLog? TryOpen(string workingDirectory, DateTimeOffset startedAtUtc, out string? error, long maxBytes = DefaultMaxBytes)
    {
        var log = RollingCsvLog.TryOpen(
            workingDirectory,
            $"gpu_{startedAtUtc:yyyyMMdd_HHmmss}.csv",
            Header,
            "GPU-loggen",
            out error,
            maxBytes);

        return log is null ? null : new GpuTelemetryCsvLog(log);
    }

    public void Append(GpuTelemetrySample sample)
    {
        _log.AppendRow(
            sample.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            sample.IsAvailable ? "true" : "false",
            RollingCsvLog.Escape(sample.AdapterName),
            RollingCsvLog.Format(sample.UtilizationPercent, "F0"),
            RollingCsvLog.Format(sample.MemoryBandwidthUtilizationPercent, "F0"),
            sample.UsedVramBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            sample.TotalVramBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            RollingCsvLog.Format(sample.VramUsagePercent, "F1"),
            RollingCsvLog.Format(sample.EncoderUtilizationPercent, "F0"),
            RollingCsvLog.Format(sample.DecoderUtilizationPercent, "F0"),
            sample.TemperatureCelsius?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            RollingCsvLog.Escape(string.Join(';', sample.ThrottleReasons)));
    }

    public void Dispose()
    {
        _log.Dispose();
    }
}
