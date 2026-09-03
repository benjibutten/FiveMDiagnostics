using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FiveMDiagnostics.Integrations.Etw;

using FiveMDiagnostics.Core;

/// <summary>
/// Records deep traces through WPR, as a continuously running in-memory ring buffer that a marker
/// saves rather than a recording that starts when the marker arrives.
/// </summary>
/// <remarks>
/// <para>
/// Starting at the marker was the flaw the ring buffer exists to fix: by the time a human notices a
/// hitch and presses the key, or the detector classifies one, the frames that caused it are seconds in
/// the past and the trace begins after the interesting part is over. Everything the capture then holds
/// is the recovery.
/// </para>
/// <para>
/// So the session runs from the moment diagnostics start, writing into a memory ring that keeps only
/// the most recent <see cref="DeepCaptureOptions.RingBufferMegabytes"/>. A marker stops it, which
/// flushes that history to an ETL, and starts a fresh one. The cost is that the buffer is empty again
/// straight after a capture, which is why automatic incidents do not save traces.
/// </para>
/// </remarks>
public sealed class WprDeepCaptureService : IDeepCaptureService, IStallAwareDeepCapture, IDisposable
{
    /// <summary>
    /// WPR records into a single machine-wide session, so nothing here may overlap. Without this gate a
    /// second severe marker would run <c>-cancel</c> — which discards the recording rather than saving
    /// it — and destroy the trace the first marker was still collecting.
    /// </summary>
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    /// <summary>How often the tail asks whether the stall is over. Half a second is a few frames.</summary>
    private const double RecoveryPollMs = 500;

    /// <inheritdoc />
    public Func<bool>? StallInProgress { get; set; }

    /// <summary>
    /// The <c>-start</c> arguments the running ring buffer session was started with, or null when no
    /// session of ours is running. Kept so a capture restarts the buffer exactly as it was, including
    /// the fallback profile stack when the generated profile did not take.
    /// </summary>
    private string? _ringBufferArguments;

    /// <summary>
    /// Remembered so <see cref="StopRingBufferAsync"/> can tear the session down without being handed
    /// settings; a running trace has to stop even when the caller no longer has them.
    /// </summary>
    private string? _wprPathForShutdown;

    /// <summary>
    /// Appended when <c>-start</c> fails. "Already recording" is by far the most common reason, and a
    /// recording this service did not start is now left alone rather than cancelled, so clearing it is a
    /// decision for whoever knows what it belongs to.
    /// </summary>
    private const string AlreadyRecordingHint =
        " Om WPR redan spelar in — ett annat verktyg, eller en tidigare körning som inte städades — avbryts den "
        + "inspelningen inte automatiskt. Kör \"wpr -cancel\" manuellt om den inte behövs.";

    public async Task<DeepCaptureResult> StartRingBufferAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.DeepCapture.Enabled)
        {
            return new DeepCaptureResult(false, false, "Deep capture är avstängd; ingen bakgrundstrace startades.");
        }

        if (!IsElevated())
        {
            return new DeepCaptureResult(
                Started: false,
                RequiresElevation: true,
                "Deep capture kräver att appen körs som administratör. Utan förhöjda rättigheter finns ingen ringbuffert, "
                + "och en markering kan därför bara spela in framåt från markeringen.");
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ringBufferArguments is not null)
            {
                return new DeepCaptureResult(true, false, "Ringbufferten körde redan.");
            }

            var wprPath = settings.DeepCapture.WprExecutablePath;

            // Clears a session orphaned by an earlier crash so "already recording" does not block the
            // whole session. Safe only because the gate guarantees no capture of ours is in flight, and
            // only for a session that is recognisably ours — see CancelOwnOrphanedSessionAsync.
            await CancelOwnOrphanedSessionAsync(wprPath, CancellationToken.None).ConfigureAwait(false);

            var (arguments, isGenerated, profileNote) = BuildRingBufferArguments(settings);
            var start = await RunWprAsync(wprPath, arguments, cancellationToken).ConfigureAwait(false);

            // The generated profile is the only one whose keywords and buffer size we control, but it is
            // also the only one WPR has never validated before this moment. A rejected profile must not
            // cost the session its deep captures, so the built-in stack takes over and says so.
            if (!start.Success && isGenerated)
            {
                var fallbackArguments = BuildFallbackArguments(settings.DeepCapture);
                var fallback = await RunWprAsync(wprPath, fallbackArguments, cancellationToken).ConfigureAwait(false);
                if (fallback.Success)
                {
                    _ringBufferArguments = fallbackArguments;
                    return new DeepCaptureResult(
                        true,
                        false,
                        $"WPR avvisade den genererade profilen ({start.Message.Trim()}). Ringbufferten körs i stället med "
                        + $"inbyggda profiler ({string.Join(", ", ResolveFallbackProfiles(settings.DeepCapture))}), vilket ger en "
                        + "betydligt större ETL med syscall-events.");
                }

                return new DeepCaptureResult(false, fallback.RequiresElevation, $"Ringbufferten kunde inte startas: {fallback.Message}{AlreadyRecordingHint}");
            }

            if (!start.Success)
            {
                return new DeepCaptureResult(false, start.RequiresElevation, $"Ringbufferten kunde inte startas: {start.Message}{AlreadyRecordingHint}");
            }

            _ringBufferArguments = arguments;
            return new DeepCaptureResult(
                true,
                false,
                $"WPR-ringbuffert igång ({settings.DeepCapture.RingBufferMegabytes} MB{profileNote}). En markering sparar sekunderna före hitchen.");
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async Task StopRingBufferAsync(CancellationToken cancellationToken)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ringBufferArguments is null)
            {
                return;
            }

            // -cancel rather than -stop: the buffer holds only whatever happened since the last capture,
            // and an ETL nobody asked for is not worth the disk. Tracing must stop either way.
            await CancelQuietlyAsync(_wprPathForShutdown ?? "wpr.exe").ConfigureAwait(false);
            _ringBufferArguments = null;
        }
        finally
        {
            _captureGate.Release();
        }
    }

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

    private async Task<DeepCaptureResult> CaptureCoreAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
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

        return _ringBufferArguments is { } ringBufferArguments
            ? await SaveRingBufferAsync(wprPath, ringBufferArguments, capturePath, settings, cancellationToken).ConfigureAwait(false)
            : await CaptureForwardAsync(wprPath, capturePath, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the ring buffer, which writes out the history it holds, then starts a fresh one.
    /// </summary>
    private async Task<DeepCaptureResult> SaveRingBufferAsync(
        string wprPath,
        string ringBufferArguments,
        string capturePath,
        DiagnosticsSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            // The run-up is already in the buffer; this waits only for the recovery, which is why a
            // marker costs seconds rather than the old fifteen.
            var tail = await WaitForRecoveryAsync(settings.DeepCapture, cancellationToken).ConfigureAwait(false);

            var stop = await RunWprAsync(wprPath, $"-stop \"{capturePath}\"", cancellationToken).ConfigureAwait(false);
            _ringBufferArguments = null;

            // A failed -stop can leave the session recording, and -start would then fail with "already
            // recording" and be reported as a restart problem rather than the stop problem it is.
            // Cancelling first makes the state the same either way.
            if (!stop.Success)
            {
                await CancelQuietlyAsync(wprPath).ConfigureAwait(false);
            }

            var restarted = await RestartRingBufferAsync(wprPath, ringBufferArguments).ConfigureAwait(false);
            var restartNote = restarted
                ? " Ringbufferten är igång igen och är tom tills den hunnit fyllas på."
                : " Ringbufferten kunde inte startas om; nästa markering spelar bara in framåt.";

            if (!stop.Success)
            {
                // Same reasoning as in CaptureForwardAsync: a -stop that failed has usually written
                // nothing, and an ETL that was never written must not be reported as a capture.
                var failure = BuildStopFailure(stop, capturePath);
                return failure with { Message = failure.Message + restartNote };
            }

            return new DeepCaptureResult(
                true,
                false,
                $"Deep capture sparad till {capturePath}. Tracen innehåller ringbuffertens historik före markeringen "
                + $"plus {tail.TotalSeconds:F0} s efter." + restartNote,
                capturePath);
        }
        catch (OperationCanceledException)
        {
            await CancelQuietlyAsync(wprPath).ConfigureAwait(false);
            _ringBufferArguments = null;
            throw;
        }
    }

    /// <summary>
    /// Waits out the tail, extending it for as long as frames are still arriving late.
    /// </summary>
    /// <remarks>
    /// Polled rather than event-driven because the only question is "is it over yet", asked a handful of
    /// times against a probe the caller already keeps updated. Without a probe this is the fixed delay
    /// it has always been.
    /// </remarks>
    private Task<TimeSpan> WaitForRecoveryAsync(DeepCaptureOptions options, CancellationToken cancellationToken)
    {
        return WaitForRecoveryAsync(options, StallInProgress, Task.Delay, cancellationToken);
    }

    /// <param name="delay">
    /// How the wait is spent. Injected so the loop can be tested without spending twelve real seconds
    /// per case, and because the thing worth testing is how long it decides to wait, not that
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> works.
    /// </param>
    internal static async Task<TimeSpan> WaitForRecoveryAsync(
        DeepCaptureOptions options,
        Func<bool>? stallInProgress,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        var tail = options.PostMarkerTail;
        if (tail > TimeSpan.Zero)
        {
            await delay(tail, cancellationToken).ConfigureAwait(false);
        }

        if (stallInProgress is not { } stillStalling)
        {
            return tail;
        }

        var maximum = options.MaxPostMarkerTail;
        while (tail < maximum && stillStalling())
        {
            var step = TimeSpan.FromMilliseconds(Math.Min(RecoveryPollMs, (maximum - tail).TotalMilliseconds));
            await delay(step, cancellationToken).ConfigureAwait(false);
            tail += step;
        }

        return tail;
    }

    /// <summary>
    /// Records forward from the marker. Only reached when no ring buffer is running — deep capture was
    /// started mid-session, WPR rejected every profile, or the app was not elevated when the session
    /// began. The resulting trace cannot show what led up to the hitch, and says so.
    /// </summary>
    private static async Task<DeepCaptureResult> CaptureForwardAsync(
        string wprPath,
        string capturePath,
        DiagnosticsSettings settings,
        CancellationToken cancellationToken)
    {
        await CancelOwnOrphanedSessionAsync(wprPath, CancellationToken.None).ConfigureAwait(false);

        // Cancellation can land in any of the three phases below, and a trace left recording keeps
        // costing the machine performance indefinitely. One handler around the whole sequence guarantees
        // the session is torn down no matter where the token fired.
        try
        {
            var start = await RunWprAsync(wprPath, BuildFileModeArguments(settings), cancellationToken).ConfigureAwait(false);
            if (!start.Success)
            {
                return new DeepCaptureResult(false, start.RequiresElevation, start.Message + AlreadyRecordingHint);
            }

            await Task.Delay(settings.DeepCapture.CaptureDuration, cancellationToken).ConfigureAwait(false);

            var stop = await RunWprAsync(wprPath, $"-stop \"{capturePath}\"", cancellationToken).ConfigureAwait(false);
            if (!stop.Success)
            {
                // A failed -stop usually means nothing was written, and it can leave the session still
                // recording. Reporting it as a capture would attach an ETL that may not exist and leave
                // WPR running for the rest of the session, so the trace is torn down and the path is only
                // handed back if a file really did land.
                await CancelQuietlyAsync(wprPath).ConfigureAwait(false);
                return BuildStopFailure(stop, capturePath);
            }

            return new DeepCaptureResult(
                true,
                false,
                $"Deep capture sparad till {capturePath}. Ingen ringbuffert körde, så tracen börjar vid markeringen och "
                + "innehåller inget från före hitchen.",
                capturePath);
        }
        catch (OperationCanceledException)
        {
            await CancelQuietlyAsync(wprPath).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> RestartRingBufferAsync(string wprPath, string arguments)
    {
        var restart = await RunWprAsync(wprPath, arguments, CancellationToken.None).ConfigureAwait(false);
        if (!restart.Success)
        {
            return false;
        }

        _ringBufferArguments = arguments;
        return true;
    }

    /// <summary>
    /// Turns a failed <c>-stop</c> into a result the session manager can act on: a capture only counts
    /// as one when the ETL is on disk.
    /// </summary>
    private static DeepCaptureResult BuildStopFailure(CommandResult stop, string capturePath)
    {
        if (File.Exists(capturePath))
        {
            return new DeepCaptureResult(
                true,
                stop.RequiresElevation,
                $"WPR rapporterade fel när tracen stoppades ({stop.Message}), men {capturePath} skrevs och bifogas. "
                + "Innehållet kan vara ofullständigt.",
                capturePath);
        }

        return new DeepCaptureResult(
            false,
            stop.RequiresElevation,
            $"Deep capture misslyckades: WPR kunde inte stoppa tracen ({stop.Message}). Ingen ETL skrevs.");
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
    /// Cancels a running WPR session only when it is one of ours that nobody stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPR records into a single machine-wide session, so <c>-cancel</c> discards whatever is recording
    /// — including a WPA session, a vendor's support trace or a colleague's capture that has nothing to
    /// do with this app. Running it unconditionally at startup meant destroying someone else's recording
    /// to make room for ours, which is not a trade this app gets to make.
    /// </para>
    /// <para>
    /// Ownership is decided on the generated profile's name appearing in <c>-status profiles</c>, which
    /// is unique to us. Deliberately not on the built-in fallback profiles: <c>GeneralProfile</c> is what
    /// every other WPR user starts too, so an orphan of ours running the fallback is left alone and
    /// reported through <see cref="AlreadyRecordingHint"/> instead. Nothing is parsed beyond that one
    /// name, since the rest of WPR's output is localised.
    /// </para>
    /// </remarks>
    private static async Task CancelOwnOrphanedSessionAsync(string wprPath, CancellationToken cancellationToken)
    {
        var status = await RunWprAsync(wprPath, "-status profiles", cancellationToken).ConfigureAwait(false);
        if (!status.Message.Contains(WprProfileWriter.ProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await CancelQuietlyAsync(wprPath).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <c>-start</c> line for the background ring buffer: no <c>-filemode</c>, so WPR keeps
    /// the trace in the memory ring the profile sizes instead of streaming it to disk.
    /// </summary>
    private (string Arguments, bool IsGenerated, string ProfileNote) BuildRingBufferArguments(DiagnosticsSettings settings)
    {
        _wprPathForShutdown = settings.DeepCapture.WprExecutablePath;

        if (!settings.DeepCapture.UseGeneratedProfile)
        {
            return (BuildFallbackArguments(settings.DeepCapture), false, ", inbyggda profiler");
        }

        var profilePath = WprProfileWriter.TryWrite(settings, out var error);
        if (profilePath is null)
        {
            return (BuildFallbackArguments(settings.DeepCapture), false, $", inbyggda profiler — egen profil kunde inte skrivas: {error}");
        }

        var schedulerNote = !string.IsNullOrWhiteSpace(settings.DeepCapture.CustomProfilePath)
            ? ", anpassad profil – CSwitch-stackar ej verifierade"
            : settings.DeepCapture.CollectContextSwitchStacks
                ? ", CSwitch-stackar aktiva"
                : ", CSwitch-stackar avstängda";
        return ($"-start \"{profilePath}!{WprProfileWriter.ProfileName}\"", true, schedulerNote);
    }

    private static string BuildFileModeArguments(DiagnosticsSettings settings)
    {
        if (settings.DeepCapture.UseGeneratedProfile
            && WprProfileWriter.TryWrite(settings, out _) is { } profilePath)
        {
            return $"-start \"{profilePath}!{WprProfileWriter.ProfileName}\" -filemode";
        }

        return BuildFallbackArguments(settings.DeepCapture) + " -filemode";
    }

    /// <summary>
    /// WPR's built-in profiles, used only when the generated one cannot be written or WPR rejects it.
    /// GeneralProfile brings syscall tracing with it, which is most of the volume in a trace this stack
    /// produces — accepted here because an oversized trace still beats no trace.
    /// </summary>
    private static string BuildFallbackArguments(DeepCaptureOptions options)
    {
        return string.Join(' ', ResolveFallbackProfiles(options).Select(profile => $"-start {profile}"));
    }

    private static IReadOnlyList<string> ResolveFallbackProfiles(DeepCaptureOptions options)
    {
        return options.Profiles.Count > 0 ? options.Profiles.ToArray() : ["GeneralProfile"];
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

public sealed class EtlArtifactParser : IArtifactParser, IVramAwareTraceAnalysis
{
    /// <summary>
    /// A DPC that runs longer than this blocks everything at its IRQL, including the scheduler, and is
    /// the classic cause of a stall that hits every thread in a process at once.
    /// </summary>
    private const double LongDpcThresholdMs = 1.0;

    /// <summary>
    /// Fraction of the trace a stream has to span before its coverage is taken as complete. A little
    /// slack absorbs the ordinary case where a provider's first or last event lands just inside the
    /// trace boundary; anything below this is a stream that died partway through.
    /// </summary>
    private const double CoverageWarningRatio = 0.9;

    /// <inheritdoc />
    public Func<double?>? AdapterVramPercent { get; set; }

    public bool CanParse(string path)
    {
        return Path.GetExtension(path).Equals(".etl", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken)
    {
        // Read before the parse rather than after it: an ETL takes tens of seconds to walk, and the
        // reading that matters is the card's state around the capture, not around the analysis.
        var vramPercent = AdapterVramPercent?.Invoke();

        return Task.Run<ArtifactParseResult?>(() =>
        {
            var dpc = new LatencyAccumulator();
            var isr = new LatencyAccumulator();
            var contextSwitches = new CoverageTracker();
            var stacks = new CoverageTracker();
            var cpu = new CpuSampleAttribution();
            var fileOperations = new FileOperationAttribution();
            var threadWaits = new ThreadWaitAttribution();
            var samples = new CoverageTracker();
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

            // Context switches and stacks are the two streams the analysis leans on hardest, and both
            // have been observed stopping partway through a trace while EventsLost stayed at zero. That
            // is invisible unless someone histograms the events per second by hand, so the parser does
            // it: an ETL whose cswitches end at 23 of 54 seconds explains far more about a failed
            // investigation than any DPC figure it also contains.
            kernel.ThreadCSwitch += data =>
            {
                contextSwitches.Add(data.TimeStamp);
                cpu.OnContextSwitch(data);
                threadWaits.OnContextSwitch(data);
            };
            kernel.StackWalkStack += data => stacks.Add(data.TimeStamp);

            // Image loads, process names and the thread-to-process map all have to be in place before
            // the samples that need them, which they are: WPR rundown emits the already-loaded modules
            // and the existing process and thread tables at the start of the trace.
            kernel.ImageLoad += cpu.OnImageLoad;
            kernel.ImageDCStart += cpu.OnImageLoad;
            kernel.ImageDCStop += cpu.OnImageLoad;
            kernel.ProcessStart += cpu.OnProcess;
            kernel.ProcessDCStart += cpu.OnProcess;
            kernel.ProcessDCStop += cpu.OnProcess;
            kernel.ThreadStart += cpu.OnThread;
            kernel.ThreadDCStart += cpu.OnThread;
            kernel.ThreadDCStop += cpu.OnThread;
            kernel.PerfInfoCollectionStart += cpu.OnSamplingInterval;
            kernel.PerfInfoSetInterval += cpu.OnSamplingInterval;
            kernel.PerfInfoSample += data =>
            {
                samples.Add(data.TimeStamp);
                cpu.OnSample(data);
            };

            source.AllEvents += traceEvent =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                eventCount++;
                firstTimestamp ??= traceEvent.TimeStamp;
                lastTimestamp = traceEvent.TimeStamp;

                // Counted off the raw stream: the FileIO keywords emit a dozen opcodes and this wants
                // every one of them, which is one string comparison against a name that is already
                // being read.
                if (traceEvent.ProviderName is "FileIO" || traceEvent.TaskName is "FileIO")
                {
                    fileOperations.OnEvent(traceEvent);
                }
            };

            source.Process();

            // What the file spans, which for a ring buffer capture is the time since the session was
            // started and says nothing about how much history the file holds. Kept because it is the
            // one figure that names the ring buffer's age, and reported as such.
            var fileSpanSeconds = firstTimestamp is not null && lastTimestamp is not null
                ? (lastTimestamp.Value - firstTimestamp.Value).TotalSeconds
                : 0;

            // What the trace actually covers. A ring buffer wraps its high-rate streams and keeps its
            // rundown and metadata events from the moment the session started, so last-minus-first over
            // every event measured the age of the session — 2 919 s for a 768 MB ring holding twenty
            // seconds of history on 31 August, printed as though the trace covered forty-nine minutes.
            // The streams that are emitted continuously are the only ones whose extent is the history:
            // context switches and CPU samples both run at kilohertz, so where they start is where the
            // retained window starts.
            var coveredStart = Earliest(contextSwitches.FirstTimestamp, samples.FirstTimestamp) ?? firstTimestamp;
            var coveredEnd = Latest(contextSwitches.LastTimestamp, samples.LastTimestamp) ?? lastTimestamp;
            var durationSeconds = coveredStart is { } start && coveredEnd is { } end
                ? Math.Max(0, (end - start).TotalSeconds)
                : 0;

            var cswitchCoverage = contextSwitches.CoverageSeconds(coveredStart);
            var stackCoverage = stacks.CoverageSeconds(coveredStart);

            // Rates come out of the sampled span, which the attribution owns: CoverageSeconds measures
            // from the start of the *trace* on purpose, which is right for coverage and catastrophically
            // wrong as a denominator for a ring buffer that holds seconds of samples inside an ETL
            // spanning hours.
            var attribution = cpu.Summarize();
            var threadWait = threadWaits.Summarize(cpu);

            var metrics = new Dictionary<string, double>
            {
                ["eventCount"] = eventCount,
                ["durationSeconds"] = durationSeconds,
                ["fileSpanSeconds"] = fileSpanSeconds,
                ["eventsLost"] = source.EventsLost,
                ["dpcCount"] = dpc.Count,
                ["dpcMaxMs"] = dpc.MaxMs,
                ["dpcTotalMs"] = dpc.TotalMs,
                ["dpcOverThresholdCount"] = dpc.OverThreshold(LongDpcThresholdMs),
                ["isrCount"] = isr.Count,
                ["isrMaxMs"] = isr.MaxMs,
                ["isrTotalMs"] = isr.TotalMs,
                ["isrOverThresholdCount"] = isr.OverThreshold(LongDpcThresholdMs),
                ["cswitchCount"] = contextSwitches.Count,
                ["cswitchCoverageSeconds"] = cswitchCoverage,
                ["cswitchCoverageRatio"] = Ratio(cswitchCoverage, durationSeconds),
                ["stackCount"] = stacks.Count,
                ["stackCoverageSeconds"] = stackCoverage,
                ["stackCoverageRatio"] = Ratio(stackCoverage, durationSeconds),
                ["cpuSampleCount"] = cpu.SampleCount,

                // Measured from the same covered start the other coverage figures use. Against
                // firstTimestamp it reported the ring buffer's age instead of the sampled window: 2 919
                // seconds of "sample coverage" for a file that held twenty seconds of samples.
                ["cpuSampleCoverageSeconds"] = samples.CoverageSeconds(coveredStart),
                ["cpuSampledSeconds"] = cpu.SampledSeconds,
            };

            if (coveredStart is { } coveredStartAt && coveredEnd is { } coveredEndAt)
            {
                // Written out so the caller — which is the only thing that knows what the capture was
                // taken for — can say whether the marker falls inside the window this file describes.
                metrics["traceCoveredStartUnixMs"] = ToUnixTimeMilliseconds(coveredStartAt);
                metrics["traceCoveredEndUnixMs"] = ToUnixTimeMilliseconds(coveredEndAt);
            }

            if (attribution is not null)
            {
                metrics["cpuTotalCores"] = Math.Round(attribution.TotalCores, 3);
                metrics["cpuSubjectIsGame"] = attribution.SubjectIsGame ? 1 : 0;
                metrics["cpuSubjectProcessCores"] = Math.Round(attribution.SubjectProcessCores, 3);
                metrics["cpuBusiestThreadCores"] = Math.Round(attribution.BusiestThreadCores, 3);
                metrics["cpuBusiestThreadId"] = attribution.BusiestThreadId;

                foreach (var module in attribution.BusiestThreadModules)
                {
                    metrics[$"cpuBusiestThreadCores_{module.Module}"] = Math.Round(module.Cores, 4);
                }

                if (attribution.VideoMemory is { } videoMemory)
                {
                    // The one measurement that tells a full card from a busy one, so the correlation
                    // engine gets it as a number and not only inside the prose summary.
                    metrics["videoMemoryManagerPeakCores"] = Math.Round(videoMemory.PeakCores, 3);
                    metrics["videoMemoryManagerBaselineCores"] = Math.Round(videoMemory.BaselineCores, 3);
                    metrics["videoMemoryManagerPressured"] = videoMemory.IsPressured ? 1 : 0;

                    if (videoMemory.SubjectCoresAtPeak is { } atPeak && videoMemory.SubjectBaselineCores is { } baselineCores)
                    {
                        metrics["videoMemorySubjectCoresAtPeak"] = Math.Round(atPeak, 3);
                        metrics["videoMemorySubjectBaselineCores"] = Math.Round(baselineCores, 3);
                        metrics["videoMemorySubjectWentQuiet"] = videoMemory.SubjectWentQuiet ? 1 : 0;
                    }
                }
            }

            // Operations rather than megabytes, because the traffic that contends for the file system is
            // small in bytes and enormous in count — and the analysis has only ever weighed the bytes.
            var fileSummary = fileOperations.Summarize(cpu.IsGameProcess, cpu.Name);
            if (fileSummary is not null)
            {
                metrics["fileOperations"] = fileSummary.TotalOperations;
                metrics["fileOperationsPerSecond"] = Math.Round(fileSummary.TotalOperations / fileSummary.CoveredSeconds, 1);

                if (fileSummary.BusiestNeighbour is { } neighbour)
                {
                    metrics["fileOperationsNeighbourPerSecond"] = Math.Round(neighbour.OperationsPerSecond, 1);
                    metrics["fileOperationsNeighbourContending"] = fileSummary.HasContendingNeighbour ? 1 : 0;

                    // When it was over the bar, not merely that it was somewhere in this file. The rate
                    // above is an average over the whole retained window, and the analysis was reading
                    // it as evidence about whichever incident the trace happened to overlap — a ring
                    // buffer of tens of seconds against an incident window of ninety overlaps almost
                    // always. Written like the thread-wait intervals, indexed and flat, because a
                    // metrics dictionary of doubles is what an ArtifactEvidence carries.
                    metrics["fileOperationsNeighbourIntervalCount"] = fileSummary.NeighbourContendingIntervals.Count;

                    for (var index = 0; index < fileSummary.NeighbourContendingIntervals.Count; index++)
                    {
                        var interval = fileSummary.NeighbourContendingIntervals[index];
                        metrics[$"fileOperationsNeighbourInterval{index}StartUnixMs"] = ToUnixTimeMilliseconds(interval.Start);
                        metrics[$"fileOperationsNeighbourInterval{index}EndUnixMs"] = ToUnixTimeMilliseconds(interval.End);
                        metrics[$"fileOperationsNeighbourInterval{index}PerSecond"] = Math.Round(interval.PeakOperationsPerSecond, 1);
                    }
                }
            }

            if (threadWait is not null)
            {
                metrics["gameThreadWaitThreadId"] = threadWait.ThreadId;
                metrics["gameThreadLongWaitCount"] = threadWait.LongWaitCount;
                metrics["gameThreadUserRequestWaitCount"] = threadWait.UserRequestWaitCount;
                metrics["gameThreadMaxWaitMs"] = Math.Round(threadWait.MaxWaitMs, 3);
                metrics["gameThreadTotalLongWaitMs"] = Math.Round(threadWait.TotalWaitMs, 3);
                metrics["gameThreadCpuSampleCount"] = threadWait.CpuSampleCount;
                metrics["gameThreadWaitIntervalCount"] = threadWait.Intervals.Count;

                for (var index = 0; index < threadWait.Intervals.Count; index++)
                {
                    var interval = threadWait.Intervals[index];
                    metrics[$"gameThreadWait{index}StartUnixMs"] = interval.StartUnixMs;
                    metrics[$"gameThreadWait{index}EndUnixMs"] = interval.EndUnixMs;
                    metrics[$"gameThreadWait{index}DurationMs"] = Math.Round(interval.DurationMs, 3);
                    metrics[$"gameThreadWait{index}UserRequest"] = interval.IsUserRequest ? 1 : 0;
                }
            }

            var summary = BuildSummary(dpc, isr, durationSeconds, eventCount)
                + BuildSpanSummary(coveredStart, coveredEnd, durationSeconds, fileSpanSeconds)
                + (attribution is not null ? " " + attribution.Describe(vramPercent) : string.Empty)
                + (fileSummary is not null ? " " + fileSummary.Describe() : string.Empty)
                + (threadWait is not null ? " " + threadWait.Describe() : string.Empty)
                + BuildCoverageSummary(contextSwitches, stacks, coveredStart, durationSeconds, source.EventsLost);

            return new ArtifactParseResult(
                new ArtifactAttachment(path, ArtifactKind.EtlTrace, Path.GetFileName(path), DateTimeOffset.UtcNow, Sensitive: true),
                [new ArtifactEvidence(DateTimeOffset.UtcNow, ArtifactKind.EtlTrace, summary, metrics, path)],
                []);
        }, cancellationToken);
    }

    private static DateTime? Earliest(DateTime? left, DateTime? right)
    {
        return left is null ? right : right is null ? left : left < right ? left : right;
    }

    private static DateTime? Latest(DateTime? left, DateTime? right)
    {
        return left is null ? right : right is null ? left : left > right ? left : right;
    }

    /// <summary>
    /// Turns one <c>TraceEvent.TimeStamp</c> into absolute milliseconds.
    /// </summary>
    /// <remarks>
    /// TraceEvent hands out local time, and pairing a local <see cref="DateTime"/> with a zero offset
    /// throws — on every machine whose own zone is not UTC, which is every machine this app has ever
    /// run on. The whole parse went with it: one <see cref="ArgumentException"/> raised while filling
    /// the metrics dictionary, and the trace produced no evidence at all. Letting
    /// <see cref="DateTimeOffset"/> read the kind gets both cases right — local and unspecified take
    /// the machine's offset, an already-UTC value keeps zero.
    /// </remarks>
    private static long ToUnixTimeMilliseconds(DateTime timestamp)
    {
        return new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Says what window the file actually describes, next to what it spans.
    /// </summary>
    /// <remarks>
    /// The two are wildly different for a ring buffer and the difference is the whole question of
    /// whether an attachment is relevant. Reading "over 2 919 seconds" next to a hitch at 20:23:48
    /// suggests the trace has the hitch in it; the file held twenty seconds ending at 20:23:52, and
    /// that is legible only if it is printed.
    /// </remarks>
    private static string BuildSpanSummary(DateTime? coveredStart, DateTime? coveredEnd, double durationSeconds, double fileSpanSeconds)
    {
        if (coveredStart is not { } start || coveredEnd is not { } end)
        {
            return string.Empty;
        }

        var local = $"{start.ToLocalTime():HH:mm:ss}–{end.ToLocalTime():HH:mm:ss}";
        var ring = fileSpanSeconds > durationSeconds * 1.5
            ? $" Filen sträcker sig över {fileSpanSeconds:F0} s, vilket är ringbuffertens ålder och inte dess innehåll."
            : string.Empty;

        return $" Tracen täcker {local} ({durationSeconds:F1} s sammanhängande ström).{ring}";
    }

    private static double Ratio(double covered, double total)
    {
        return total > 0 ? Math.Clamp(covered / total, 0, 1) : 0;
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

    /// <summary>
    /// Describes how much of the trace each key stream actually covers, and says so loudly when a
    /// stream stops early.
    /// </summary>
    /// <remarks>
    /// <c>EventsLost = 0</c> is not evidence that nothing was lost: it counts events the consumer failed
    /// to drain, not a provider that stopped emitting because another session took the keyword. A trace
    /// with full duration and half the context switches looks healthy by every summary statistic there
    /// is, and every conclusion drawn from its second half is worthless.
    /// </remarks>
    private static string BuildCoverageSummary(
        CoverageTracker contextSwitches,
        CoverageTracker stacks,
        DateTime? traceStart,
        double durationSeconds,
        int eventsLost)
    {
        if (durationSeconds <= 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        foreach (var (label, tracker) in new[] { ("Context switches", contextSwitches), ("Stackar", stacks) })
        {
            if (tracker.Count == 0)
            {
                parts.Add($"{label} saknas helt i spåret.");
                continue;
            }

            var coverage = tracker.CoverageSeconds(traceStart);
            if (coverage < durationSeconds * CoverageWarningRatio)
            {
                parts.Add($"{label} slutade vid {coverage:F0} av {durationSeconds:F0} sekunder "
                    + $"({tracker.Count} events, {tracker.SecondsWithEvents} sekunder med data).");
            }
        }

        if (parts.Count == 0)
        {
            return $" Context switches och stackar täcker hela spåret (EventsLost {eventsLost}).";
        }

        var lostNote = eventsLost > 0
            ? $"EventsLost var {eventsLost}."
            : "EventsLost var 0, så det är inte buffertöverskrivning — mer sannolikt tog en annan ETW-session över keywordet.";

        return $" VARNING, ofullständig täckning: {string.Join(" ", parts)} {lostNote} "
            + "Slutsatser om den senare delen av fönstret vilar på data som inte finns.";
    }

    /// <summary>
    /// Tracks when one event stream was actually producing events, rather than only how many it
    /// produced. The count alone cannot distinguish a stream that ran the whole trace from one that
    /// delivered the same number of events in the first third and then went silent.
    /// </summary>
    private sealed class CoverageTracker
    {
        /// <summary>
        /// Distinct whole seconds that carried at least one event. Held as a set rather than a count so
        /// a stream that is merely sparse — a mostly idle machine — is distinguishable from one that
        /// stopped, which needs the last timestamp instead.
        /// </summary>
        private readonly HashSet<long> _secondsWithEvents = [];

        public long Count { get; private set; }

        public DateTime? FirstTimestamp { get; private set; }

        public DateTime? LastTimestamp { get; private set; }

        public int SecondsWithEvents => _secondsWithEvents.Count;

        public void Add(DateTime timestamp)
        {
            Count++;
            FirstTimestamp ??= timestamp;
            LastTimestamp = timestamp;
            _secondsWithEvents.Add(timestamp.Ticks / TimeSpan.TicksPerSecond);
        }

        /// <summary>
        /// Seconds from the start of the trace to this stream's last event. Measured from the trace
        /// start rather than the stream's own first event, so a stream that starts late is not credited
        /// with the time before it appeared.
        /// </summary>
        public double CoverageSeconds(DateTime? traceStart)
        {
            if (LastTimestamp is not { } last || traceStart is not { } start)
            {
                return 0;
            }

            return Math.Max(0, (last - start).TotalSeconds);
        }
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
