using System.Threading.Channels;

namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;
using FiveMDiagnostics.Integrations.Obs;

/// <summary>
/// The measurement that was missing on the one evening it was the question.
/// </summary>
/// <remarks>
/// A session ran 5 h 47 min with OBS running and its WebSocket never answering, on the night the stream
/// was failing to start. Render lag and skipped frames are the only view this app has of the stream's
/// own health and they were absent from all 154 incidents, each of which recorded the state faithfully
/// as "process körs, WebSocket frånkopplad" followed by four empty fields. Nobody reads that as a
/// fault. The fix is two clicks in OBS, and it is only worth anything while the session is still
/// running.
/// </remarks>
public sealed class ObsConnectionWarningTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 19, 39, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(86_400, 300)]
    public void ConnectionWarningDelayIsKeptActionable(int configuredSeconds, int expectedSeconds)
    {
        var options = new ObsOptions { ConnectionWarningDelay = TimeSpan.FromSeconds(configuredSeconds) };

        Assert.True(options.Normalize());
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.ConnectionWarningDelay);
    }

    [Fact]
    public void ObsRunningWithoutItsSocketIsReportedAsAWarning()
    {
        var sink = new RecordingStatusSink();
        var collector = new ObsTelemetryCollector();
        var now = Start;

        collector.ReportConnectionHealth(Context(sink, () => now), Disconnected(now));

        // Nothing yet: a normal connect takes a few polls, and reporting one as a failure would make the
        // warning meaningless.
        Assert.Empty(sink.Entries);

        now = Start.AddSeconds(31);
        collector.ReportConnectionHealth(Context(sink, () => now), Disconnected(now));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(StatusLevel.Warning, entry.Level);
        Assert.Contains("WebSocket Server Settings", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once. A warning repeated every second becomes the same background noise as the field it replaces.
    /// </summary>
    [Fact]
    public void TheWarningIsNotRepeated()
    {
        var sink = new RecordingStatusSink();
        var collector = new ObsTelemetryCollector();

        for (var second = 0; second < 300; second++)
        {
            var now = Start.AddSeconds(second);
            collector.ReportConnectionHealth(Context(sink, () => now), Disconnected(now));
        }

        Assert.Single(sink.Entries);
    }

    /// <summary>
    /// And it clears itself, which is what lets the banner in the app be driven by the latest line from
    /// each collector rather than by a vocabulary every collector would have to learn.
    /// </summary>
    [Fact]
    public void ConnectingAfterwardsSaysSo()
    {
        var sink = new RecordingStatusSink();
        var collector = new ObsTelemetryCollector();
        var warned = Start.AddSeconds(31);

        collector.ReportConnectionHealth(Context(sink, () => Start), Disconnected(Start));
        collector.ReportConnectionHealth(Context(sink, () => warned), Disconnected(warned));
        collector.ReportConnectionHealth(Context(sink, () => warned.AddSeconds(5)), Connected(warned.AddSeconds(5)));

        Assert.Equal(2, sink.Entries.Count);
        Assert.Equal(StatusLevel.Info, sink.Entries[^1].Level);
        Assert.Contains("ansluten", sink.Entries[^1].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closing OBS and opening it again arms the warning for the second attempt.
    /// </summary>
    /// <remarks>
    /// Restarting OBS mid-session is precisely when somebody has just changed a setting in it, so the
    /// second silent evening is the one worth reporting rather than the one to swallow. Without arming
    /// it again the first warning of a session is the only one it can ever produce.
    /// </remarks>
    [Fact]
    public void RestartingObsArmsTheWarningAgain()
    {
        var sink = new RecordingStatusSink();
        var collector = new ObsTelemetryCollector();

        collector.ReportConnectionHealth(Context(sink, () => Start), Disconnected(Start));
        collector.ReportConnectionHealth(Context(sink, () => Start.AddSeconds(31)), Disconnected(Start.AddSeconds(31)));
        Assert.Single(sink.Entries);

        // OBS is closed, and later started again with the socket still not answering.
        collector.ReportConnectionHealth(Context(sink, () => Start.AddMinutes(5)), NotRunning(Start.AddMinutes(5)));

        var relaunch = Start.AddMinutes(10);
        collector.ReportConnectionHealth(Context(sink, () => relaunch), Disconnected(relaunch));
        collector.ReportConnectionHealth(Context(sink, () => relaunch.AddSeconds(31)), Disconnected(relaunch.AddSeconds(31)));

        Assert.Equal(2, sink.Entries.Count);
        Assert.All(sink.Entries, entry => Assert.Equal(StatusLevel.Warning, entry.Level));
    }

    /// <summary>
    /// OBS not running is not a fault. The reports say so plainly, and there is nothing for anyone to
    /// fix.
    /// </summary>
    [Fact]
    public void ObsNotRunningIsNeverAWarning()
    {
        var sink = new RecordingStatusSink();
        var collector = new ObsTelemetryCollector();

        for (var second = 0; second < 300; second++)
        {
            var now = Start.AddSeconds(second);
            collector.ReportConnectionHealth(Context(sink, () => now), NotRunning(now));
        }

        Assert.Empty(sink.Entries);
    }

    private static ObsTelemetrySample Disconnected(DateTimeOffset timestamp) => new(
        timestamp, IsConnected: false, null, null, null, null, null, null, false, false, IsProcessRunning: true);

    private static ObsTelemetrySample NotRunning(DateTimeOffset timestamp) => new(
        timestamp, IsConnected: false, null, null, null, null, null, null, false, false, IsProcessRunning: false);

    private static ObsTelemetrySample Connected(DateTimeOffset timestamp) => new(
        timestamp, IsConnected: true, 60, 6.2, 0, 0, 11, 940, true, false, IsProcessRunning: true);

    private static CollectorContext Context(IDiagnosticStatusSink sink, Func<DateTimeOffset> utcNow)
    {
        return new CollectorContext(
            Channel.CreateUnbounded<TelemetryEvent>().Writer,
            DiagnosticsSettings.CreateDefault(),
            sink,
            new StubProcessResolver(),
            utcNow);
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
