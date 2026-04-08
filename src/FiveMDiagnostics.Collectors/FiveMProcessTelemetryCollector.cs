using System.Diagnostics;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

public sealed class FiveMProcessTelemetryCollector : ITelemetryCollector
{
    private ProcessMetricSnapshot? _previousSnapshot;

    public string Name => "FiveMProcessTelemetry";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var target = context.ProcessResolver.TryGetTargetProcess();
            if (target is not null)
            {
                try
                {
                    using var process = Process.GetProcessById(target.ProcessId);
                    var timestamp = context.UtcNow();
                    if (ProcessMetricsReader.TryRead(process, timestamp, out var snapshot))
                    {
                        var cpu = _previousSnapshot is { } previous && previous.ProcessId == snapshot.ProcessId
                            ? ProcessMetricsReader.ComputeCpuPercent(snapshot, previous)
                            : 0;

                        var readBytesPerSecond = _previousSnapshot is { } previousRead && previousRead.ProcessId == snapshot.ProcessId
                            ? ProcessMetricsReader.ComputeReadBytesPerSecond(snapshot, previousRead)
                            : 0;

                        var writeBytesPerSecond = _previousSnapshot is { } previousWrite && previousWrite.ProcessId == snapshot.ProcessId
                            ? ProcessMetricsReader.ComputeWriteBytesPerSecond(snapshot, previousWrite)
                            : 0;

                        _previousSnapshot = snapshot;

                        await context.Writer.WriteAsync(
                            new ProcessTelemetrySample(
                                timestamp,
                                snapshot.ProcessId,
                                snapshot.ProcessName,
                                cpu,
                                snapshot.PrivateBytes,
                                snapshot.WorkingSetBytes,
                                snapshot.ThreadCount,
                                readBytesPerSecond,
                                writeBytesPerSecond),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    context.StatusSink.Report(StatusLevel.Warning, Name, $"Kunde inte läsa FiveM-processens metrics: {ex.Message}");
                }
            }
            else
            {
                _previousSnapshot = null;
            }

            await Task.Delay(context.Settings.ProcessPollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}