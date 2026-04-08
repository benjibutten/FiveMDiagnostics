using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Diagnostics.Tracing;

namespace FiveMDiagnostics.Integrations.Etw;

using FiveMDiagnostics.Core;

public sealed class WprDeepCaptureService : IDeepCaptureService
{
    public async Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
    {
        var wprPath = settings.DeepCapture.WprExecutablePath;
        var capturePath = Path.Combine(settings.WorkingDirectory, $"deep_{marker.MarkedAt:yyyyMMdd_HHmmss}_{marker.Id:N}.etl");

        var start = await RunWprAsync(wprPath, $"-start {settings.DeepCapture.Profile} -filemode", cancellationToken).ConfigureAwait(false);
        if (!start.Success)
        {
            return new DeepCaptureResult(false, start.RequiresElevation, start.Message);
        }

        try
        {
            await Task.Delay(settings.DeepCapture.CaptureDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RunWprAsync(wprPath, "-cancel", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var stop = await RunWprAsync(wprPath, $"-stop \"{capturePath}\"", cancellationToken).ConfigureAwait(false);
        if (!stop.Success)
        {
            return new DeepCaptureResult(true, stop.RequiresElevation, stop.Message, capturePath);
        }

        return new DeepCaptureResult(true, false, $"Deep capture sparad till {capturePath}.", capturePath);
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
        catch (Win32Exception ex)
        {
            return new CommandResult(false, false, $"WPR saknas eller kunde inte startas: {ex.Message}");
        }
        catch (Exception ex)
        {
            return BuildFailure(ex.Message);
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
    public bool CanParse(string path)
    {
        return Path.GetExtension(path).Equals(".etl", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run<ArtifactParseResult?>(() =>
        {
            long eventCount = 0;
            long dpcCount = 0;
            long isrCount = 0;
            DateTime? firstTimestamp = null;
            DateTime? lastTimestamp = null;

            using var source = new ETWTraceEventSource(path);
            source.AllEvents += traceEvent =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                eventCount++;
                firstTimestamp ??= traceEvent.TimeStamp;
                lastTimestamp = traceEvent.TimeStamp;

                var name = string.Join(' ', new[] { traceEvent.ProviderName, traceEvent.EventName, traceEvent.OpcodeName, traceEvent.TaskName });
                if (name.Contains("DPC", StringComparison.OrdinalIgnoreCase))
                {
                    dpcCount++;
                }

                if (name.Contains("ISR", StringComparison.OrdinalIgnoreCase))
                {
                    isrCount++;
                }
            };

            source.Process();

            var durationSeconds = firstTimestamp is not null && lastTimestamp is not null
                ? (lastTimestamp.Value - firstTimestamp.Value).TotalSeconds
                : 0;

            var metrics = new Dictionary<string, double>
            {
                ["eventCount"] = eventCount,
                ["dpcEvents"] = dpcCount,
                ["isrEvents"] = isrCount,
                ["durationSeconds"] = durationSeconds,
            };

            var summary = dpcCount + isrCount > 0
                ? $"ETL-trace innehöll {dpcCount} DPC- och {isrCount} ISR-relaterade events över {durationSeconds:F1} sekunder."
                : $"ETL-trace analyserad. {eventCount} events över {durationSeconds:F1} sekunder registrerades.";

            return new ArtifactParseResult(
                new ArtifactAttachment(path, ArtifactKind.EtlTrace, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
                [new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.EtlTrace, summary, metrics, path)],
                []);
        }, cancellationToken);
    }
}