using System.Globalization;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

/// <summary>
/// Appends the per-process VRAM breakdown to a CSV for the length of a session, one row per process
/// per sample.
/// </summary>
/// <remarks>
/// Long format rather than a column per process, because the set of processes holding GPU memory
/// changes during a session — a capture program starts, a browser tab closes — and a wide file would
/// have to fix the columns at the moment the header is written.
/// </remarks>
public sealed class GpuProcessMemoryCsvLog : IDisposable
{
    private const string Header = "timestampUtc,processId,processName,dedicatedBytes,sharedBytes";

    private readonly RollingCsvLog _log;

    private GpuProcessMemoryCsvLog(RollingCsvLog log)
    {
        _log = log;
    }

    public string Path => _log.Path;

    public string? Failure => _log.Failure;

    public static GpuProcessMemoryCsvLog? TryOpen(string workingDirectory, DateTimeOffset startedAtUtc, out string? error)
    {
        var log = RollingCsvLog.TryOpen(
            workingDirectory,
            $"gpuprocs_{startedAtUtc:yyyyMMdd_HHmmss}.csv",
            Header,
            "VRAM-per-process-loggen",
            out error);

        return log is null ? null : new GpuProcessMemoryCsvLog(log);
    }

    public void Append(GpuProcessMemorySample sample)
    {
        var timestamp = sample.Timestamp.ToString("O", CultureInfo.InvariantCulture);
        foreach (var process in sample.Processes)
        {
            _log.AppendRow(
                timestamp,
                process.ProcessId.ToString(CultureInfo.InvariantCulture),
                RollingCsvLog.Escape(process.ProcessName),
                process.DedicatedBytes.ToString(CultureInfo.InvariantCulture),
                process.SharedBytes.ToString(CultureInfo.InvariantCulture));
        }
    }

    public void Dispose()
    {
        _log.Dispose();
    }
}
