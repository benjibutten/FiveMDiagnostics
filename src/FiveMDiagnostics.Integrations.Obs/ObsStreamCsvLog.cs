using System.Globalization;

namespace FiveMDiagnostics.Integrations.Obs;

using FiveMDiagnostics.Core;

/// <summary>
/// Appends every OBS poll taken while streaming to a CSV, so the minute the stream started dropping
/// frames can be read off afterwards rather than inferred from one total at the end.
/// </summary>
/// <remarks>
/// OBS's own log gives a single figure per stream, written when it stops. On 7 October that figure was
/// 15.5% and there was no way to say whether it was ten bad minutes or a steady leak from the start —
/// which is the whole question when the complaint is "it gets bad after an hour".
/// </remarks>
public sealed class ObsStreamCsvLog : IDisposable
{
    private const string Header =
        "timestampUtc,isStreaming,isReconnecting,droppedFrames,totalFrames,durationMs,bytes,congestion,"
        + "encodingSkippedFrames,renderSkippedFrames,activeFps,averageRenderMs,ingest";

    private readonly RollingCsvLog _log;

    private ObsStreamCsvLog(RollingCsvLog log)
    {
        _log = log;
    }

    public string Path => _log.Path;

    public string? Failure => _log.Failure;

    public static ObsStreamCsvLog? TryOpen(string workingDirectory, DateTimeOffset startedAtUtc, out string? error)
    {
        var log = RollingCsvLog.TryOpen(
            workingDirectory,
            $"obsstream_{startedAtUtc:yyyyMMdd_HHmmss}.csv",
            Header,
            "Stream-loggen",
            out error);

        return log is null ? null : new ObsStreamCsvLog(log);
    }

    public void Append(ObsTelemetrySample sample, string? ingest)
    {
        _log.AppendRow(
            sample.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            sample.IsStreaming ? "true" : "false",
            sample.IsStreamReconnecting ? "true" : "false",
            sample.StreamDroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            sample.StreamTotalFrames?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            sample.StreamDurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            sample.StreamBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            RollingCsvLog.Format(sample.StreamCongestion, "F3"),
            sample.OutputSkippedFrames?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            sample.RenderSkippedFrames?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            RollingCsvLog.Format(sample.ActiveFps, "F1"),
            RollingCsvLog.Format(sample.AverageFrameRenderTimeMs, "F2"),
            RollingCsvLog.Escape(ingest));
    }

    public void Dispose()
    {
        _log.Dispose();
    }
}
