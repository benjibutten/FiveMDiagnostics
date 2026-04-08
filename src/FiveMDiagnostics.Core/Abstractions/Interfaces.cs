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
    Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken);
}

public interface IDiagnosticStatusSink
{
    void Report(StatusLevel level, string source, string message);
}