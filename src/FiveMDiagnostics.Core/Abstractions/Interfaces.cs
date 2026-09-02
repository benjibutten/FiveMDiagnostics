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

public interface IDiagnosticStatusSink
{
    void Report(StatusLevel level, string source, string message);
}