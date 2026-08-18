using System.Diagnostics;
using System.Globalization;

namespace FiveMDiagnostics.Integrations.PresentMon;

using FiveMDiagnostics.Core;

public sealed class PresentMonTelemetryCollector : ITelemetryCollector, IDisposable
{
    private readonly object _sync = new();
    private readonly PresentMonCaptureHealth _health = new();
    private string? _resolvedExecutablePath;
    private bool _reportedAutoDetectedExecutable;
    private bool _reportedMissingExecutable;
    private int? _currentProcessId;
    private string? _currentOutputPath;
    private Process? _presentMonProcess;
    private long _lastFilePosition;
    private Dictionary<string, int>? _headerIndex;
    private DateTimeOffset? _traceStartEstimateUtc;
    private long _samplesThisCapture;
    private long _positionAtLastHealthCheck;
    private bool _reportedSuspension;
    private string? _pendingRestartReason;

    public string Name => "PresentMon";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            // Restarts counted during an earlier session say nothing about this one, so a new session
            // always starts on a clean ladder.
            _health.Reset();
            _reportedSuspension = false;
        }

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

                var produced = 0;
                foreach (var sample in ReadNewSamples(target.ProcessName, context.UtcNow))
                {
                    produced++;
                    await context.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                CheckCaptureHealth(context, produced);

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

            if (_health.TargetProcessId != processId)
            {
                _health.OnTargetChanged(processId);
                _reportedSuspension = false;
            }

            // A capture that ends while the game is still running is the failure that produced a
            // 0.77 second CSV for a six hour session, and it used to restart in silence.
            var exitReason = _currentProcessId == processId && _presentMonProcess is { HasExited: true } exited
                ? $"PresentMon avslutades av sig självt (exit code {TryGetExitCode(exited)}) efter {_samplesThisCapture} frames."
                : null;

            StopCaptureLocked(context);

            // Every start after the first one for this target is a restart, whether the process died on
            // its own or the health check killed a mute capture. Both go through the same ladder, so a
            // PresentMon that exits immediately cannot be respawned once per polling interval forever.
            if (_health.HasStartedCapture)
            {
                var now = context.UtcNow();
                if (!_health.TryBeginRestart(now))
                {
                    ReportGaveUpLocked(context, exitReason);
                    return;
                }

                ReportRestartLocked(context, exitReason ?? _pendingRestartReason ?? "PresentMon capture behövde startas om.");
            }

            _pendingRestartReason = null;
            _currentProcessId = processId;
            _currentOutputPath = Path.Combine(context.Settings.WorkingDirectory, $"presentmon_{processId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.csv");
            _traceStartEstimateUtc = null;
            _samplesThisCapture = 0;
            _positionAtLastHealthCheck = 0;

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

            // Counts as an attempt even when the start fails outright, so a start that keeps failing is
            // spaced out by the same ladder rather than retried once per polling interval.
            _health.OnCaptureStarted(context.UtcNow());

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

    /// <summary>
    /// Watches for a capture that is alive but mute. PresentMon can lose its ETW session, or be killed
    /// by another tool that grabs the same session name, without its process exiting — from the outside
    /// that looks identical to a healthy capture whose file simply stops growing.
    /// </summary>
    /// <remarks>
    /// Silence alone is ambiguous: an alt-tab, a minimised window or a loading screen present nothing
    /// either. The capture is therefore only killed when <see cref="PresentMonCaptureHealth"/> both
    /// considers it silent — a window that doubles after every restart — and still allows a restart, so
    /// a paused game costs at most a handful of increasingly spaced retries instead of one every
    /// fifteen seconds for as long as the pause lasts.
    /// </remarks>
    private void CheckCaptureHealth(CollectorContext context, int samplesProduced)
    {
        lock (_sync)
        {
            if (_presentMonProcess is null)
            {
                return;
            }

            // A file that grew without yielding samples still proves the capture is alive, which keeps
            // a header-only or unparsable batch from being read as a dead ETW session.
            var fileAdvanced = _lastFilePosition != _positionAtLastHealthCheck;
            _positionAtLastHealthCheck = _lastFilePosition;
            var now = context.UtcNow();

            if (samplesProduced > 0 || fileAdvanced)
            {
                _samplesThisCapture += samplesProduced;
                if (_health.OnProgress(now))
                {
                    context.StatusSink.Report(
                        StatusLevel.Info,
                        Name,
                        $"PresentMon har levererat frames stabilt i {PresentMonCaptureHealth.StableRunBeforeReset.TotalMinutes:F0} min — omstartsräknaren nollställd.");
                }

                return;
            }

            if (!_health.IsSilent(now))
            {
                return;
            }

            if (!_health.CanRestart(now))
            {
                ReportGaveUpLocked(context, reason: null);
                return;
            }

            var silence = now - _health.LastProgressUtc;
            _pendingRestartReason =
                $"PresentMon har inte levererat några frames på {silence.TotalSeconds:F0} s trots att FiveM körs (totalt {_samplesThisCapture} frames denna capture).";

            // Dropping the process forces the next EnsureCaptureStarted to spawn a fresh capture, which
            // is also where the restart is counted against the ladder.
            StopCaptureLocked(context);
        }
    }

    private void ReportRestartLocked(CollectorContext context, string reason)
    {
        var restartCount = _health.RestartCount;
        var level = restartCount >= PresentMonCaptureHealth.RestartsBeforeEscalation ? StatusLevel.Error : StatusLevel.Warning;
        var suffix = restartCount >= PresentMonCaptureHealth.RestartsBeforeEscalation
            ? $" Detta är omstart {restartCount} — frame-datat för sessionen är sannolikt ofullständigt."
            : " Startar om capturen.";

        context.StatusSink.Report(level, Name, reason + suffix);
    }

    /// <summary>
    /// Says once — not once per polling interval — that automatic restarts have stopped. Restarting
    /// costs an ETW session teardown and setup each time, so a target that has failed repeatedly is
    /// better left alone until FiveM or the session restarts.
    /// </summary>
    private void ReportGaveUpLocked(CollectorContext context, string? reason)
    {
        if (_reportedSuspension || !_health.IsSuspended)
        {
            return;
        }

        _reportedSuspension = true;
        var prefix = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason + " ";
        context.StatusSink.Report(
            StatusLevel.Error,
            Name,
            $"{prefix}PresentMon startades om {_health.RestartCount} gånger utan att leverera frames. Automatiska omstarter pausas tills FiveM startas om eller en ny session startas — frame telemetry saknas till dess.");
    }

    private static string TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "okänd";
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
