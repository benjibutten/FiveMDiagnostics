using System.Threading.Channels;

namespace FiveMDiagnostics.Core;

public sealed record CollectorContext(
    ChannelWriter<TelemetryEvent> Writer,
    DiagnosticsSettings Settings,
    IDiagnosticStatusSink StatusSink,
    ITargetProcessResolver ProcessResolver,
    Func<DateTimeOffset> UtcNow);

public interface ITelemetryCollector
{
    string Name { get; }

    Task RunAsync(CollectorContext context, CancellationToken cancellationToken);
}

public interface ITargetProcessResolver
{
    TargetProcessInfo? TryGetTargetProcess();
}

public interface IEnvironmentMetadataProvider
{
    Task<EnvironmentMetadata> CollectAsync(DiagnosticsSettings settings, CancellationToken cancellationToken);
}

public interface IAnalysisEngine
{
    IncidentAnalysis Analyze(IncidentRecord incident);
}

public interface IIncidentExporter
{
    Task<string> ExportAsync(IncidentRecord incident, ExportBundleOptions options, CancellationToken cancellationToken);
}

public interface IArtifactParser
{
    bool CanParse(string path);

    Task<ArtifactParseResult?> ParseAsync(string path, CancellationToken cancellationToken);
}

public interface IDeepCaptureService
{
    /// <summary>
    /// Starts the always-on ring buffer session for a diagnostics session, so a marker has the seconds
    /// before the hitch to save rather than only the seconds after it.
    /// </summary>
    Task<DeepCaptureResult> StartRingBufferAsync(DiagnosticsSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the trace for one marker. With a ring buffer running this writes the accumulated history
    /// plus a short tail and restarts the buffer; without one it falls back to recording forward from
    /// the marker, which cannot explain what led up to it.
    /// </summary>
    Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken);

    /// <summary>Tears the ring buffer session down. Tracing costs the machine performance until it does.</summary>
    Task StopRingBufferAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A deep capture service that can keep recording while the stall it was started for is still going on.
/// </summary>
/// <remarks>
/// <para>
/// Optional, and asked for with a type test rather than folded into <see cref="IDeepCaptureService"/>,
/// because a capture backend that records a fixed window is a perfectly good one — it simply cannot
/// offer this.
/// </para>
/// <para>
/// The tail exists to show the recovery, and a fixed two seconds shows it only when the stall is
/// shorter than two seconds. On 1 September a freeze ran for nine, the capture stopped after four, and
/// the three largest frames of the whole evening fell outside the file that was attached to the
/// incident naming them.
/// </para>
/// </remarks>
public interface IStallAwareDeepCapture
{
    /// <summary>
    /// Returns true while frames are still arriving late, so the tail should keep running. Null leaves
    /// the fixed tail in place.
    /// </summary>
    Func<bool>? StallInProgress { get; set; }
}

/// <summary>
/// Lets a trace analyser ask how full the card was, which is the one thing a trace cannot see.
/// </summary>
/// <remarks>
/// The video memory manager's rate says the driver was moving surfaces; only the adapter says whether
/// it was moving them because the card was full. On 2 September the analyser wrote "so much movement
/// means the card was full and the driver was evacuating surfaces over PCIe" about a trace taken while
/// the card stood at 54%, because it had the first half of that sentence and no way to check the
/// second. Same shape as <see cref="IStallAwareDeepCapture"/>: a probe the session fills in, and a
/// null that leaves the analyser saying only what it measured.
/// </remarks>
public interface IVramAwareTraceAnalysis
{
    /// <summary>
    /// The card's occupancy in percent around the time of the capture, or null when it is not known.
    /// </summary>
    Func<double?>? AdapterVramPercent { get; set; }
}

/// <summary>
/// Lets the session tell the analysis that the present mode has already been accounted for.
/// </summary>
/// <remarks>
/// "Composed: Copy with GPU GDI for 100% of frames — no independent flips, everything went through the
/// compositor" has been written into every incident of eleven sessions, 154 of them in one evening, and
/// it has never once been the answer. It is not a finding: it is what a game running in a borderless
/// window does, every frame, by definition. Once the settings file says the window mode, the sentence
/// belongs in the session header with the measurement that says it costs nothing — 0.50% of 1.25
/// million frames off cadence — and not on every incident as though it were evidence.
/// </remarks>
public interface IWindowModeAwareAnalysis
{
    /// <summary>
    /// Answers, for a given moment, whether the window mode in force then explains a composed present
    /// path — so the per-incident explanation can be dropped for the incidents it covers and kept for
    /// the ones it does not. Null, or false, leaves every incident carrying it, which is right when
    /// nothing has established why the compositor is in the way.
    /// </summary>
    /// <remarks>
    /// A function of time rather than a flag, for the same reason
    /// <see cref="IVramAwareTraceAnalysis.AdapterVramPercent"/> is a function at all. Incidents are
    /// analysed on a worker off a bounded queue, minutes after the window they describe closed, and the
    /// settings file is re-read on a cadence while they wait — so a flag read at analysis time answers
    /// the question with whichever window mode the game happens to be in now. Alt-Enter into exclusive
    /// fullscreen and every incident still queued from the borderless hour loses the present-mode
    /// evidence that was the only thing explaining it; alt-Enter the other way and an hour of incidents
    /// that really were unexplained get told they are accounted for.
    /// </remarks>
    Func<DateTimeOffset, bool>? ComposedPresentExplainedAt { get; set; }
}

public interface IDiagnosticStatusSink
{
    void Report(StatusLevel level, string source, string message);
}