using System.Diagnostics;
using System.Globalization;

namespace FiveMDiagnostics.Integrations.PresentMon;

using FiveMDiagnostics.Core;

public sealed class PresentMonTelemetryCollector : ITelemetryCollector, IDisposable
{
    private readonly object _sync = new();
    private bool _reportedMissingExecutable;
    private int? _currentProcessId;
    private string? _currentOutputPath;
    private Process? _presentMonProcess;
    private long _lastFilePosition;
    private Dictionary<string, int>? _headerIndex;
    private DateTimeOffset _captureStartTimeUtc;

    public string Name => "PresentMon";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var executablePath = context.Settings.PresentMon.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                if (!_reportedMissingExecutable)
                {
                    _reportedMissingExecutable = true;
                    context.StatusSink.Report(StatusLevel.Warning, Name, "PresentMon hittades inte. Frame telemetry är begränsad tills executable path konfigureras.");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var target = context.ProcessResolver.TryGetTargetProcess();
            if (target is null)
            {
                StopCapture();
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

        StopCapture();
    }

    public void Dispose()
    {
        StopCapture();
    }

    private void EnsureCaptureStarted(CollectorContext context, string executablePath, int processId)
    {
        lock (_sync)
        {
            if (_currentProcessId == processId && _presentMonProcess is { HasExited: false })
            {
                return;
            }

            StopCapture();
            _currentProcessId = processId;
            _currentOutputPath = Path.Combine(context.Settings.WorkingDirectory, $"presentmon_{processId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.csv");
            _captureStartTimeUtc = DateTimeOffset.UtcNow;

            var arguments = context.Settings.PresentMon.ArgumentsTemplate
                .Replace("{processId}", processId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{outputPath}", _currentOutputPath, StringComparison.OrdinalIgnoreCase);

            var startInfo = new ProcessStartInfo(executablePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            _presentMonProcess = Process.Start(startInfo);
            _lastFilePosition = 0;
            _headerIndex = null;

            if (_presentMonProcess is null)
            {
                context.StatusSink.Report(StatusLevel.Warning, Name, "PresentMon kunde inte startas.");
                return;
            }

            context.StatusSink.Report(StatusLevel.Info, Name, $"PresentMon capture startad för PID {processId}.");
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

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            yield break;
        }

        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (startPosition > stream.Length)
        {
            startPosition = 0;
            headerIndex = null;
        }

        stream.Seek(startPosition, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (headerIndex is null)
            {
                headerIndex = ParseHeader(line);
                continue;
            }

            var cells = line.Split(',');
            var sample = ParseSample(cells, headerIndex, processName, utcNow());
            if (sample is not null)
            {
                yield return sample;
            }
        }

        lock (_sync)
        {
            _lastFilePosition = stream.Position;
            _headerIndex = headerIndex;
        }
    }

    private FrameTelemetrySample? ParseSample(string[] cells, IReadOnlyDictionary<string, int> headerIndex, string processName, DateTimeOffset fallbackTimestamp)
    {
        var frameTime = ReadDouble(cells, headerIndex, "msBetweenPresents", "msBetweenDisplayChange");
        if (frameTime is null)
        {
            return null;
        }

        var timeInSeconds = ReadDouble(cells, headerIndex, "timeInSeconds", "timeindisplay");
        var timestamp = timeInSeconds is not null
            ? _captureStartTimeUtc.AddSeconds(timeInSeconds.Value)
            : fallbackTimestamp;

        return new FrameTelemetrySample(
            timestamp,
            frameTime.Value,
            ReadDouble(cells, headerIndex, "msGPUActive", "msUntilRenderComplete"),
            ReadDouble(cells, headerIndex, "msUntilDisplayed", "msUntilDisplayChange"),
            ReadDouble(cells, headerIndex, "msBetweenPresents"),
            ReadBool(cells, headerIndex, "Dropped"),
            ReadString(cells, headerIndex, "ProcessName") ?? processName,
            ReadDouble(cells, headerIndex, "msInPresentApi"));
    }

    private static Dictionary<string, int> ParseHeader(string headerLine)
    {
        return headerLine
            .Split(',')
            .Select((value, index) => (Header: value.Trim(), Index: index))
            .ToDictionary(item => item.Header, item => item.Index, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadString(string[] cells, IReadOnlyDictionary<string, int> headerIndex, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (headerIndex.TryGetValue(columnName, out var index) && index < cells.Length)
            {
                return cells[index];
            }
        }

        return null;
    }

    private static double? ReadDouble(string[] cells, IReadOnlyDictionary<string, int> headerIndex, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (!headerIndex.TryGetValue(columnName, out var index) || index >= cells.Length)
            {
                continue;
            }

            if (double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool ReadBool(string[] cells, IReadOnlyDictionary<string, int> headerIndex, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (!headerIndex.TryGetValue(columnName, out var index) || index >= cells.Length)
            {
                continue;
            }

            var cell = cells[index];
            if (bool.TryParse(cell, out var parsed))
            {
                return parsed;
            }

            if (int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                return numeric != 0;
            }
        }

        return false;
    }

    private void StopCapture()
    {
        lock (_sync)
        {
            if (_presentMonProcess is { HasExited: false })
            {
                try
                {
                    _presentMonProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore shutdown failures.
                }
            }

            _presentMonProcess?.Dispose();
            _presentMonProcess = null;
            _currentProcessId = null;
            _currentOutputPath = null;
            _lastFilePosition = 0;
            _headerIndex = null;
        }
    }
}