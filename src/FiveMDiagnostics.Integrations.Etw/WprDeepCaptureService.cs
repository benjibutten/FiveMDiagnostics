using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FiveMDiagnostics.Integrations.Etw;

using FiveMDiagnostics.Core;

public sealed class WprDeepCaptureService : IDeepCaptureService, IDisposable
{
    /// <summary>
    /// WPR records into a single machine-wide session, so captures cannot overlap. Without this gate a
    /// second severe marker would run <c>-cancel</c> — which discards the recording rather than saving
    /// it — and destroy the trace the first marker was still collecting.
    /// </summary>
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    public async Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
    {
        if (!await _captureGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new DeepCaptureResult(
                Started: false,
                RequiresElevation: false,
                "En deep capture pågår redan. Den här markeringen registrerades, men ingen andra WPR-trace startades.");
        }

        try
        {
            return await CaptureCoreAsync(marker, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public void Dispose()
    {
        _captureGate.Dispose();
    }

    private static async Task<DeepCaptureResult> CaptureCoreAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
    {
        // wpr.exe cannot self-elevate, and failing halfway through leaves a running trace session
        // behind. Checking up front turns a confusing mid-capture failure into a clear message.
        if (!IsElevated())
        {
            return new DeepCaptureResult(
                Started: false,
                RequiresElevation: true,
                "Deep capture kräver att appen körs som administratör. Starta om appen förhöjt och markera incidenten igen.");
        }

        var wprPath = settings.DeepCapture.WprExecutablePath;
        var capturePath = Path.Combine(settings.WorkingDirectory, $"deep_{marker.MarkedAt:yyyyMMdd_HHmmss}_{marker.Id:N}.etl");

        // Clears a session orphaned by an earlier crash so "already recording" does not block every
        // subsequent severe marker. Safe only because the capture gate guarantees no capture of ours is
        // in flight — otherwise this would discard a trace still being collected.
        await RunWprAsync(wprPath, "-cancel", CancellationToken.None).ConfigureAwait(false);

        // Cancellation can land in any of the three phases below, and a trace left recording keeps
        // costing the machine performance indefinitely. One handler around the whole sequence guarantees
        // the session is torn down no matter where the token fired.
        try
        {
            var start = await RunWprAsync(wprPath, BuildStartArguments(settings.DeepCapture), cancellationToken).ConfigureAwait(false);
            if (!start.Success)
            {
                return new DeepCaptureResult(false, start.RequiresElevation, start.Message);
            }

            await Task.Delay(settings.DeepCapture.CaptureDuration, cancellationToken).ConfigureAwait(false);

            var stop = await RunWprAsync(wprPath, $"-stop \"{capturePath}\"", cancellationToken).ConfigureAwait(false);
            if (!stop.Success)
            {
                return new DeepCaptureResult(true, stop.RequiresElevation, stop.Message, capturePath);
            }

            return new DeepCaptureResult(true, false, $"Deep capture sparad till {capturePath}.", capturePath);
        }
        catch (OperationCanceledException)
        {
            await CancelQuietlyAsync(wprPath).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CancelQuietlyAsync(string wprPath)
    {
        try
        {
            await RunWprAsync(wprPath, "-cancel", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the caller is already unwinding.
        }
    }

    /// <summary>
    /// GeneralProfile is first-level triage only. Explaining a multi-second stall needs GPU work, disk
    /// and filter-driver activity, and resident-set behaviour on the same timeline.
    /// </summary>
    private static string BuildStartArguments(DeepCaptureOptions options)
    {
        var profiles = options.Profiles.Count > 0 ? options.Profiles : ["GeneralProfile"];
        return string.Join(' ', profiles.Select(profile => $"-start {profile}")) + " -filemode";
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<CommandResult> RunWprAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new CommandResult(false, false, "WPR kunde inte startas.");
            }

            try
            {
                var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var output = (await stdOutTask.ConfigureAwait(false)) + Environment.NewLine + (await stdErrTask.ConfigureAwait(false));

                if (process.ExitCode == 0)
                {
                    return new CommandResult(true, false, string.IsNullOrWhiteSpace(output) ? "WPR lyckades." : output.Trim());
                }

                return BuildFailure(output);
            }
            catch (OperationCanceledException)
            {
                // Leaving wpr.exe running would hold the trace session open. Kill it, then let the
                // cancellation propagate so the caller can issue -cancel.
                TryKill(process);
                throw;
            }
        }
        catch (Win32Exception ex)
        {
            return new CommandResult(false, false, $"WPR saknas eller kunde inte startas: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildFailure(ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to kill.
        }
    }

    private static CommandResult BuildFailure(string output)
    {
        var requiresElevation = output.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            || output.Contains("elevation", StringComparison.OrdinalIgnoreCase)
            || output.Contains("access is denied", StringComparison.OrdinalIgnoreCase);

        return new CommandResult(false, requiresElevation, string.IsNullOrWhiteSpace(output)
            ? "WPR misslyckades."
            : output.Trim());
    }

    private sealed record CommandResult(bool Success, bool RequiresElevation, string Message);
}

public sealed class EtlArtifactParser : IArtifactParser
{
    /// <summary>
    /// A DPC that runs longer than this blocks everything at its IRQL, including the scheduler, and is
    /// the classic cause of a stall that hits every thread in a process at once.
    /// </summary>
    private const double LongDpcThresholdMs = 1.0;

    public bool CanParse(string path)
    {
        return Path.GetExtension(path).Equals(".etl", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run<ArtifactParseResult?>(() =>
        {
            var dpc = new LatencyAccumulator();
            var isr = new LatencyAccumulator();
            long eventCount = 0;
            DateTime? firstTimestamp = null;
            DateTime? lastTimestamp = null;

            using var source = new ETWTraceEventSource(path);
            var kernel = new KernelTraceEventParser(source);

            // Counting events whose name merely contains "DPC" says nothing: 10k short DPCs are normal,
            // one 8 ms DPC is not. What matters is how long each one held the CPU.
            kernel.PerfInfoDPC += data => dpc.Add(data.ElapsedTimeMSec);
            kernel.PerfInfoThreadedDPC += data => dpc.Add(data.ElapsedTimeMSec);
            kernel.PerfInfoISR += data => isr.Add(data.ElapsedTimeMSec);

            source.AllEvents += traceEvent =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                eventCount++;
                firstTimestamp ??= traceEvent.TimeStamp;
                lastTimestamp = traceEvent.TimeStamp;
            };

            source.Process();

            var durationSeconds = firstTimestamp is not null && lastTimestamp is not null
                ? (lastTimestamp.Value - firstTimestamp.Value).TotalSeconds
                : 0;

            var metrics = new Dictionary<string, double>
            {
                ["eventCount"] = eventCount,
                ["durationSeconds"] = durationSeconds,
                ["dpcCount"] = dpc.Count,
                ["dpcMaxMs"] = dpc.MaxMs,
                ["dpcTotalMs"] = dpc.TotalMs,
                ["dpcOverThresholdCount"] = dpc.OverThreshold(LongDpcThresholdMs),
                ["isrCount"] = isr.Count,
                ["isrMaxMs"] = isr.MaxMs,
                ["isrTotalMs"] = isr.TotalMs,
                ["isrOverThresholdCount"] = isr.OverThreshold(LongDpcThresholdMs),
            };

            return new ArtifactParseResult(
                new ArtifactAttachment(path, ArtifactKind.EtlTrace, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
                [new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.EtlTrace, BuildSummary(dpc, isr, durationSeconds, eventCount), metrics, path)],
                []);
        }, cancellationToken);
    }

    private static string BuildSummary(LatencyAccumulator dpc, LatencyAccumulator isr, double durationSeconds, long eventCount)
    {
        if (dpc.Count == 0 && isr.Count == 0)
        {
            return $"ETL-trace analyserad. {eventCount} events över {durationSeconds:F1} sekunder, men inga DPC/ISR-events fanns i spåret.";
        }

        var longDpcs = dpc.OverThreshold(LongDpcThresholdMs);
        var longIsrs = isr.OverThreshold(LongDpcThresholdMs);
        var verdict = dpc.MaxMs >= 4 || isr.MaxMs >= 4
            ? " Det är högt nog för att blockera hela systemet och matchar DPC/ISR-latens som rotorsak."
            : string.Empty;

        return $"ETL-trace: längsta DPC {dpc.MaxMs:F2} ms ({longDpcs} över {LongDpcThresholdMs:F0} ms av {dpc.Count}), "
            + $"längsta ISR {isr.MaxMs:F2} ms ({longIsrs} över {LongDpcThresholdMs:F0} ms av {isr.Count}) "
            + $"över {durationSeconds:F1} sekunder.{verdict}";
    }

    private sealed class LatencyAccumulator
    {
        private readonly List<double> _overThreshold = [];

        public long Count { get; private set; }

        public double MaxMs { get; private set; }

        public double TotalMs { get; private set; }

        public void Add(double elapsedMs)
        {
            if (double.IsNaN(elapsedMs) || elapsedMs < 0)
            {
                return;
            }

            Count++;
            TotalMs += elapsedMs;
            if (elapsedMs > MaxMs)
            {
                MaxMs = elapsedMs;
            }

            if (elapsedMs >= LongDpcThresholdMs)
            {
                _overThreshold.Add(elapsedMs);
            }
        }

        public int OverThreshold(double thresholdMs)
        {
            return _overThreshold.Count(value => value >= thresholdMs);
        }
    }
}
