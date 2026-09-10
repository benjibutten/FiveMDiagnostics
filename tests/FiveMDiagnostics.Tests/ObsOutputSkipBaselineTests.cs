using System.Threading.Channels;

namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;
using FiveMDiagnostics.Integrations.Obs;

/// <summary>
/// The counter OBS keeps from the start of its output, read as if it belonged to this session.
/// </summary>
/// <remarks>
/// <c>outputSkippedFrames</c> counts from the moment the stream started, which is routinely an hour
/// before the diagnostics session is opened. Reported as an absolute peak, a handful of frames dropped
/// early in the stream made every session after it close on a Warning saying those frames never reached
/// the viewers — a true sentence about a period the session did not measure. The render counter beside
/// it was already read as a start and an end; this is the same treatment.
/// </remarks>
public sealed class ObsOutputSkipBaselineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 19, 12, 0, TimeSpan.Zero);

    /// <summary>Frames dropped before the session opened belong to the stream, not to the evening.</summary>
    [Fact]
    public void ASkipFromBeforeTheSessionIsNotThisSessionsWarning()
    {
        var sink = new RecordingStatusSink();
        var collector = new ObsTelemetryCollector();

        for (var second = 0; second < 60; second++)
        {
            collector.NoteSkippedFrames(Sample(Start.AddSeconds(second), renderSkipped: 15, outputSkipped: 42));
        }

        collector.ReportSkippedFrameTotals(Context(sink));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(StatusLevel.Info, entry.Level);
        Assert.Contains("rörde sig inte", entry.Message, StringComparison.Ordinal);

        // Still printed, because "42, none of them tonight" is the answer and a blank is not.
        Assert.Contains("42", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>A counter that moves during the session is what the warning is for.</summary>
    [Fact]
    public void FramesDroppedDuringTheSessionAreStillAWarning()
    {
        var sink = new RecordingStatusSink();
        var collector = new ObsTelemetryCollector();

        collector.NoteSkippedFrames(Sample(Start, renderSkipped: 15, outputSkipped: 42));
        collector.NoteSkippedFrames(Sample(Start.AddMinutes(30), renderSkipped: 96, outputSkipped: 55));

        collector.ReportSkippedFrameTotals(Context(sink));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(StatusLevel.Warning, entry.Level);
        Assert.Contains("steg med 13", entry.Message, StringComparison.Ordinal);
        Assert.Contains("gick från 15 till 96", entry.Message, StringComparison.Ordinal);
    }

    private static ObsTelemetrySample Sample(DateTimeOffset timestamp, long renderSkipped, long outputSkipped) => new(
        timestamp,
        IsConnected: true,
        ActiveFps: 60,
        AverageFrameRenderTimeMs: 6.2,
        RenderSkippedFrames: renderSkipped,
        OutputSkippedFrames: outputSkipped,
        CpuUsagePercent: 11,
        MemoryUsageMb: 940,
        IsStreaming: true,
        IsRecording: false,
        IsProcessRunning: true);

    private static CollectorContext Context(IDiagnosticStatusSink sink)
    {
        return new CollectorContext(
            Channel.CreateUnbounded<TelemetryEvent>().Writer,
            DiagnosticsSettings.CreateDefault(),
            sink,
            new StubProcessResolver(),
            () => Start);
    }

    private sealed class RecordingStatusSink : IDiagnosticStatusSink
    {
        public List<DiagnosticStatusEntry> Entries { get; } = [];

        public void Report(StatusLevel level, string source, string message)
            => Entries.Add(new DiagnosticStatusEntry(DateTimeOffset.UtcNow, level, source, message));
    }

    private sealed class StubProcessResolver : ITargetProcessResolver
    {
        public TargetProcessInfo? TryGetTargetProcess()
            => new(1234, "FiveM_b3407_GTAProcess", null, DateTimeOffset.UtcNow);
    }
}
