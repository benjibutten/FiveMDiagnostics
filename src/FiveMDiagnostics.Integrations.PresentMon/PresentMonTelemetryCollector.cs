using System.Diagnostics;
using System.Globalization;

namespace FiveMDiagnostics.Integrations.PresentMon;

using FiveMDiagnostics.Core;

public sealed class PresentMonTelemetryCollector : ITelemetryCollector, IDisposable
{
    private readonly object _sync = new();
    private string? _resolvedExecutablePath;
    private bool _reportedAutoDetectedExecutable;
    private bool _reportedMissingExecutable;
    private int? _currentProcessId;
    private string? _currentOutputPath;
    private Process? _presentMonProcess;
    private long _lastFilePosition;
    private Dictionary<string, int>? _headerIndex;
    private DateTimeOffset? _traceStartEstimateUtc;

    public string Name => "PresentMon";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var discovery = PresentMonLocator.Discover(context.Settings.PresentMon.ExecutablePath);
                var executablePath = discovery.ExecutablePath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    if (!_reportedMissingExecutable)
                    {
                        _reportedMissingExecutable = true;
                        context.StatusSink.Report(StatusLevel.Warning, Name, "PresentMon hittades inte. Frame telemetry är begränsad tills executable path konfigureras.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(_resolvedExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    StopCapture(context);
                    _resolvedExecutablePath = executablePath;
                }

                _reportedMissingExecutable = false;
                if (discovery.Kind == PresentMonDiscoveryKind.AutoDetected && !_reportedAutoDetectedExecutable)
                {
                    _reportedAutoDetectedExecutable = true;
                    context.StatusSink.Report(StatusLevel.Info, Name, $"PresentMon hittades automatiskt på {executablePath}.");
                }

                var target = context.ProcessResolver.TryGetTargetProcess();
                if (target is null)
                {
                    StopCapture(context);
                    await Task.Delay(context.Settings.PresentMon.PollingInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                EnsureCaptureStarted(context, executablePath, target.ProcessId);
                foreach (var sample in ReadNewSamples(target.ProcessName, context.UtcNow))
                {
                    await context.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(context.Settings.PresentMon.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            StopCapture(context);
        }
    }

    public void Dispose()
    {
        StopCapture(null);
    }

    private void EnsureCaptureStarted(CollectorContext context, string executablePath, int processId)
    {
        lock (_sync)
        {
            if (_currentProcessId == processId && _presentMonProcess is { HasExited: false })
            {
                return;
            }

            StopCaptureLocked(context);
            _currentProcessId = processId;
            _currentOutputPath = Path.Combine(context.Settings.WorkingDirectory, $"presentmon_{processId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.csv");
            _traceStartEstimateUtc = null;

            var arguments = context.Settings.PresentMon.ArgumentsTemplate
                .Replace("{processId}", processId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{outputPath}", _currentOutputPath, StringComparison.OrdinalIgnoreCase);

            var startInfo = new ProcessStartInfo(executablePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            _presentMonProcess = Process.Start(startInfo);
            _lastFilePosition = 0;
            _headerIndex = null;

            if (_presentMonProcess is null)
            {
                context.StatusSink.Report(StatusLevel.Warning, Name, "PresentMon kunde inte startas.");
                return;
            }

            // PresentMon writes an elevation warning to stderr immediately. Nothing drained these pipes
            // before, so once the 4 KB buffer filled PresentMon blocked forever.
            DrainDiagnosticStream(_presentMonProcess, context);

            context.StatusSink.Report(StatusLevel.Info, Name, $"PresentMon capture startad för PID {processId}.");
        }
    }

    private void DrainDiagnosticStream(Process process, CollectorContext context)
    {
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            var level = args.Data.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? StatusLevel.Warning
                : StatusLevel.Info;
            context.StatusSink.Report(level, Name, $"PresentMon: {args.Data.Trim()}");
        };

        process.OutputDataReceived += (_, _) => { };

        try
        {
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch (InvalidOperationException)
        {
            // Process exited before redirection could start.
        }
    }

    private IEnumerable<FrameTelemetrySample> ReadNewSamples(string processName, Func<DateTimeOffset> utcNow)
    {
        string? outputPath;
        long startPosition;
        Dictionary<string, int>? headerIndex;

        lock (_sync)
        {
            outputPath = _currentOutputPath;
            startPosition = _lastFilePosition;
            headerIndex = _headerIndex is null
                ? null
                : new Dictionary<string, int>(_headerIndex, StringComparer.OrdinalIgnoreCase);
        }

        // PresentMon creates the file lazily, only once it has frames to write.
        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            yield break;
        }

        var readUtc = utcNow();
        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (startPosition > stream.Length)
        {
            startPosition = 0;
            headerIndex = null;
        }

        stream.Seek(startPosition, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        var rows = new List<(string[] Cells, double? RelativeMs)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (headerIndex is null)
            {
                headerIndex = PresentMonCsvParser.ParseHeader(line);
                continue;
            }

            var cells = line.Split(',');
            rows.Add((cells, PresentMonCsvParser.ReadRelativeMs(cells, headerIndex)));
        }

        var position = stream.Position;
        DateTimeOffset traceStart;

        lock (_sync)
        {
            _lastFilePosition = position;
            _headerIndex = headerIndex;
            AnchorTraceStartLocked(rows, readUtc);
            traceStart = _traceStartEstimateUtc ?? readUtc;
        }

        if (headerIndex is null)
        {
            yield break;
        }

        foreach (var (cells, relativeMs) in rows)
        {
            var timestamp = relativeMs is { } offset ? traceStart.AddMilliseconds(offset) : readUtc;
            var sample = PresentMonCsvParser.ParseRow(cells, headerIndex, processName, timestamp);
            if (sample is not null)
            {
                yield return sample;
            }
        }
    }

    /// <summary>
    /// PresentMon reports frame times relative to the start of its trace, not as wall-clock. The read
    /// always happens after the frame, so <c>readUtc - relativeMs</c> is an upper bound on the trace
    /// start and the tightest bound seen so far is the best estimate. Keeping the minimum lets the
    /// anchor converge as batches arrive instead of collapsing every frame onto the read time.
    /// </summary>
    private void AnchorTraceStartLocked(List<(string[] Cells, double? RelativeMs)> rows, DateTimeOffset readUtc)
    {
        foreach (var (_, relativeMs) in rows)
        {
            if (relativeMs is not { } value)
            {
                continue;
            }

            var candidate = readUtc.AddMilliseconds(-value);
            if (_traceStartEstimateUtc is null || candidate < _traceStartEstimateUtc)
            {
                _traceStartEstimateUtc = candidate;
            }
        }
    }

    private void StopCapture(CollectorContext? context)
    {
        lock (_sync)
        {
            StopCaptureLocked(context);
        }
    }

    private void StopCaptureLocked(CollectorContext? context)
    {
        if (_presentMonProcess is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    // Killing PresentMon orphans its ETW session, which then blocks the next capture.
                    // CloseMainWindow does not apply to a console app, so the session name is reclaimed
                    // by --stop_existing_session on the next start; give it a chance to flush regardless.
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                context?.StatusSink.Report(StatusLevel.Info, Name, $"PresentMon avslutades inte rent: {ex.Message}");
            }

            process.Dispose();
        }

        _presentMonProcess = null;
        _currentProcessId = null;
        _currentOutputPath = null;
        _lastFilePosition = 0;
        _headerIndex = null;
        _traceStartEstimateUtc = null;
    }
}
