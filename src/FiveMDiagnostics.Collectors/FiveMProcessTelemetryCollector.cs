using System.Diagnostics;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

public sealed class FiveMProcessTelemetryCollector : ITelemetryCollector
{
    private ProcessMetricSnapshot? _previousSnapshot;

    /// <summary>
    /// The process id the last read failure was reported for. A resolver that is still handing out an
    /// id Windows has already reaped produces one failure per polling interval — twice a second — and
    /// a session that restarted the game logged sixteen identical warnings, all describing one event.
    /// Reporting per id turns that back into one line per occurrence.
    /// </summary>
    private int? _reportedFailurePid;

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
                    if (ProcessMetricsReader.TryRead(process, timestamp, out var snapshot, includeThreadCount: true))
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
                        _reportedFailurePid = null;

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
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // The process ended between the resolver handing out the id and this read. Not a
                    // fault of its own — the next poll re-resolves — so it is worth exactly one line.
                    _previousSnapshot = null;
                    ReportOnce(context, target.ProcessId, $"FiveM-processen (PID {target.ProcessId}) kördes inte längre när metrics skulle läsas.");
                }
                catch (Exception ex)
                {
                    _previousSnapshot = null;
                    ReportOnce(context, target.ProcessId, $"Kunde inte läsa FiveM-processens metrics: {ex.Message}");
                }
            }
            else
            {
                _previousSnapshot = null;
                _reportedFailurePid = null;
            }

            await Task.Delay(context.Settings.ProcessPollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReportOnce(CollectorContext context, int processId, string message)
    {
        if (_reportedFailurePid == processId)
        {
            return;
        }

        _reportedFailurePid = processId;
        context.StatusSink.Report(StatusLevel.Warning, Name, message);
    }
}
