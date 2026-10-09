namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;
using FiveMDiagnostics.Integrations.Obs;

/// <summary>
/// The evenings of 7 and 8 October: the stream lagged an hour in, the game and the encoder were fine,
/// and the frames were lost on the way to the ingest server. The app read only the encoder's counter
/// and closed the session saying nothing had failed to reach the viewers.
/// </summary>
public sealed class ObsStreamHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 19, 16, 54, TimeSpan.Zero);

    private const double FrameMs = 1000.0 / 60;

    /// <summary>
    /// A stream at 60 fps and about 9 Mbit/s, counted the way obs-websocket counts it: the total is
    /// the frames sent, the dropped ones are not in it, and the duration is the sent count's worth of
    /// frame intervals.
    /// </summary>
    private static IEnumerable<ObsTelemetrySample> Stream(
        int seconds,
        Func<int, bool> dropping,
        double dropShare = 0.2,
        DateTimeOffset? startedAt = null,
        int firstSecond = 0)
    {
        var at = startedAt ?? Start;
        long dropped = 0;
        long sent = 0;
        long bytes = 0;

        for (var second = 0; second <= seconds; second++)
        {
            if (second >= firstSecond)
            {
                yield return Streaming(at.AddSeconds(second), dropped, sent, bytes);
            }

            var droppedNow = dropping(second) ? (long)(60 * dropShare) : 0;
            dropped += droppedNow;
            sent += 60 - droppedNow;
            bytes += (60 - droppedNow) * (9_000_000 / 8 / 60);
        }
    }

    private static ObsTelemetrySample Streaming(DateTimeOffset at, long dropped, long sent, long bytes, long encoderSkipped = 1, bool reconnecting = false) => new(
        at,
        IsConnected: true,
        ActiveFps: 60,
        AverageFrameRenderTimeMs: 1.2,
        RenderSkippedFrames: 3,
        OutputSkippedFrames: encoderSkipped,
        CpuUsagePercent: 4,
        MemoryUsageMb: 900,
        IsStreaming: true,
        IsRecording: false,
        IsProcessRunning: true,
        StreamDroppedFrames: dropped,
        StreamTotalFrames: sent,
        StreamBytes: bytes,
        StreamCongestion: dropped > 0 ? 0.6 : 0,
        IsStreamReconnecting: reconnecting,
        StreamDurationMs: (long)(sent * FrameMs));

    private static ObsTelemetrySample Idle(DateTimeOffset at) => new(
        at, true, 60, 1.2, 3, 1, 4, 900, IsStreaming: false, IsRecording: false, IsProcessRunning: true,
        StreamDroppedFrames: 0, StreamTotalFrames: 0, StreamBytes: 0, StreamCongestion: 0, StreamDurationMs: 0);

    private static ObsTelemetrySample SocketDown(DateTimeOffset at, bool obsRunning) => new(
        at, false, null, null, null, null, null, null, false, false, IsProcessRunning: obsRunning);

    private static List<StreamHealthReport> Run(ObsStreamHealthMonitor monitor, IEnumerable<ObsTelemetrySample> samples)
    {
        var reports = new List<StreamHealthReport>();
        foreach (var sample in samples)
        {
            reports.AddRange(monitor.Observe(sample));
        }

        return reports;
    }

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The first bad minute is said at once, with the clock and how far into the stream it was.</summary>
    [Fact]
    public void DropsAnHourInAreWarnedAboutWhenTheyStart()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);

        // Clean for 55 minutes, then a fifth of every second's frames lost.
        var reports = Run(monitor, Stream(60 * 60, second => second >= 55 * 60));

        Assert.Contains(reports, report => report.Message.StartsWith($"Streamen startade kl. {Clock(Start)}", StringComparison.Ordinal));

        var onset = Assert.Single(reports, report => report.Message.StartsWith("Streamen tappar bildrutor", StringComparison.Ordinal));
        Assert.Equal(StatusLevel.Warning, onset.Level);
        Assert.Contains("55 min in i streamen", onset.Message, StringComparison.Ordinal);

        // Against attempted frames, as OBS prints it: 12 of 60, not 12 of the 48 sent.
        Assert.Contains("20,0", onset.Message, StringComparison.Ordinal);
        Assert.Contains("Kodningen hann med", onset.Message, StringComparison.Ordinal);
        Assert.Contains("stoppa och starta bara streamen", onset.Message, StringComparison.Ordinal);
    }

    /// <summary>A healthy hour says nothing between the start and the summary.</summary>
    [Fact]
    public void ACleanStreamIsQuietAndSaysSoAtTheEnd()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);

        var reports = Run(monitor, Stream(60 * 60, _ => false));
        Assert.Single(reports);

        reports.AddRange(monitor.Observe(Idle(Start.AddMinutes(61))));
        var summary = reports.Last();
        Assert.Equal(StatusLevel.Info, summary.Level);
        Assert.Contains("inga bildrutor tappades mot nätverket", summary.Message, StringComparison.Ordinal);
        Assert.Contains("9,0 Mbit/s", summary.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bad period ends after three clean minutes, and a long one is restated every five minutes
    /// rather than every minute.
    /// </summary>
    [Fact]
    public void ABadPeriodIsRestatedSparinglyAndClosed()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);

        var reports = Run(monitor, Stream(40 * 60, second => second is >= 10 * 60 and < 22 * 60));

        Assert.Single(reports, report => report.Message.StartsWith("Streamen tappar bildrutor", StringComparison.Ordinal));
        var ongoing = reports.Count(report => report.Message.StartsWith("Tappet mot nätverket pågår", StringComparison.Ordinal));
        Assert.InRange(ongoing, 1, 3);

        var recovered = Assert.Single(reports, report => report.Message.StartsWith("Tappet mot nätverket har upphört", StringComparison.Ordinal));
        Assert.Equal(StatusLevel.Info, recovered.Level);
    }

    /// <summary>
    /// The summary is written even when the session ends with the stream still running, and its end is
    /// the last reading — so the times and the length in the line agree.
    /// </summary>
    [Fact]
    public void ASessionEndingMidStreamStillGetsTheSummary()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);
        Run(monitor, Stream(20 * 60, second => second >= 5 * 60));

        var summary = Assert.Single(monitor.Finish());
        Assert.Equal(StatusLevel.Warning, summary.Level);
        Assert.Contains("tappades mot nätverket", summary.Message, StringComparison.Ordinal);
        Assert.Contains($"–{Clock(Start.AddMinutes(20))} (sista mätningen", summary.Message, StringComparison.Ordinal);
        Assert.Contains("20 min mätt", summary.Message, StringComparison.Ordinal);
        Assert.Contains("första kl.", summary.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// OBS quitting ends its stream. Before, the stream stayed open while the game ran on, and the summary
    /// at the end of the evening gave the session's end as the stream's.
    /// </summary>
    [Fact]
    public void ObsClosingEndsTheStreamAtItsLastReading()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);
        Run(monitor, Stream(60 * 60, _ => false));

        var reports = Run(monitor, [SocketDown(Start.AddMinutes(61), obsRunning: false)]);
        var summary = Assert.Single(reports);
        Assert.Contains($"–{Clock(Start.AddMinutes(60))} (OBS stängdes)", summary.Message, StringComparison.Ordinal);
        Assert.Contains("60 min mätt", summary.Message, StringComparison.Ordinal);

        // Hours later the session ends and there is nothing left open to summarise.
        Assert.Empty(monitor.Finish());
    }

    /// <summary>A socket that drops while OBS keeps running does not end the stream.</summary>
    [Fact]
    public void AWebSocketOutageAloneDoesNotEndTheStream()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);

        var samples = Stream(20 * 60, _ => false).ToList();
        var reports = Run(monitor, samples.Take(5 * 60));
        reports.AddRange(Run(monitor, [SocketDown(Start.AddMinutes(6), obsRunning: true)]));
        reports.AddRange(Run(monitor, samples.Skip(10 * 60)));

        Assert.Single(reports);
        Assert.Contains("20 min mätt", Assert.Single(monitor.Finish()).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream stopped and started again while the socket was down, and by the reconnect already past the
    /// old one's frame count, is a new stream — not one stream whose deltas span two outputs.
    /// </summary>
    [Fact]
    public void ARestartDuringAnOutageIsANewStreamEvenWhenItsCountIsHigher()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);
        Run(monitor, Stream(2 * 60, _ => false));
        Run(monitor, [SocketDown(Start.AddMinutes(2).AddSeconds(1), obsRunning: true)]);

        // Restarted at +3 min; the socket is back at +12 min, nine minutes into the new stream.
        var restartedAt = Start.AddMinutes(3);
        var reports = Run(monitor, Stream(9 * 60 + 30, _ => false, startedAt: restartedAt, firstSecond: 9 * 60));

        Assert.Contains(reports, report => report.Message.StartsWith("Streamen ", StringComparison.Ordinal)
            && report.Message.Contains("2 min mätt", StringComparison.Ordinal));
        Assert.Contains(reports, report => report.Message.StartsWith($"Streamen startade kl. {Clock(restartedAt)} enligt OBS", StringComparison.Ordinal));
    }

    /// <summary>
    /// A stream that started while the socket was down is dated by OBS's own count, not by the reconnect.
    /// </summary>
    [Fact]
    public void AStreamStartedDuringAnOutageGetsItsRealStartTime()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddMinutes(-30))]);
        Run(monitor, [SocketDown(Start.AddMinutes(-29), obsRunning: true)]);

        // Started at Start; the socket returns 25 minutes later.
        var reports = Run(monitor, Stream(26 * 60, _ => false, firstSecond: 25 * 60));

        var start = Assert.Single(reports);
        Assert.StartsWith($"Streamen startade kl. {Clock(Start)} enligt OBS och pågick redan", start.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream shorter than one window is still judged. Before, 40% lost in 50 seconds read as Info and
    /// "utspritt".
    /// </summary>
    [Fact]
    public void AShortBadStreamIsAWarningAndNotCalledSpread()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);
        Run(monitor, Stream(50, _ => true, dropShare: 0.4));

        var summary = Assert.Single(monitor.Observe(Idle(Start.AddSeconds(51))));
        Assert.Equal(StatusLevel.Warning, summary.Level);
        Assert.DoesNotContain("utspritt", summary.Message, StringComparison.Ordinal);
        Assert.Contains("1 av 1 minuter", summary.Message, StringComparison.Ordinal);
    }

    /// <summary>When the encoder is the one falling behind, the warning must not blame the network path alone.</summary>
    [Fact]
    public void EncoderLagIsNotPassedOffAsTheConnection()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);

        var samples = Stream(5 * 60, _ => true)
            .Select((sample, index) => sample with { OutputSkippedFrames = index * 12L });
        var reports = Run(monitor, samples);

        var onset = Assert.Single(reports, report => report.Message.StartsWith("Streamen tappar bildrutor", StringComparison.Ordinal));
        Assert.Contains("datorn hann inte heller med", onset.Message, StringComparison.Ordinal);
    }

    /// <summary>The server the stream went to is carried in every line once it is known.</summary>
    [Fact]
    public void TheIngestServerIsNamedOnceAndCarriedAlong()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);

        var samples = Stream(5 * 60, second => second > 0).ToList();
        Run(monitor, samples.Take(1));

        Assert.NotNull(monitor.NoteIngest("35.55.42.6 (ingest.global-contribute.live-video.net)"));
        Assert.Null(monitor.NoteIngest("35.55.42.6 (ingest.global-contribute.live-video.net)"));

        var reports = Run(monitor, samples.Skip(1));
        var onset = Assert.Single(reports, report => report.Message.StartsWith("Streamen tappar bildrutor", StringComparison.Ordinal));
        Assert.Contains("server 35.55.42.6", onset.Message, StringComparison.Ordinal);
    }

    /// <summary>A stream already running when the session opened is looked up from its real start.</summary>
    [Fact]
    public void AStreamAlreadyRunningIsDatedFromItsRealStart()
    {
        var monitor = new ObsStreamHealthMonitor();

        // An hour in when the session opens, a few hundred frames dropped before that; none since.
        var reports = Run(monitor, Stream(70 * 60, second => second < 20, dropShare: 1.0 / 3, firstSecond: 60 * 60));

        Assert.Contains("pågick redan", Assert.Single(reports).Message, StringComparison.Ordinal);
        Assert.Equal(Start, monitor.ConnectionStartedAt!.Value, TimeSpan.FromSeconds(2));

        var summary = Assert.Single(monitor.Finish());
        Assert.Contains("inga bildrutor tappades", summary.Message, StringComparison.Ordinal);
    }

    /// <summary>A reconnect is said both ways and does not split the stream.</summary>
    [Fact]
    public void AReconnectIsReported()
    {
        var monitor = new ObsStreamHealthMonitor();
        Run(monitor, [Idle(Start.AddSeconds(-5))]);

        var reports = Run(monitor,
        [
            Streaming(Start, 0, 0, 0),
            Streaming(Start.AddSeconds(30), 0, 1800, 33_750_000, reconnecting: true),
            Streaming(Start.AddSeconds(42), 0, 1800, 33_750_000),
        ]);

        Assert.Contains(reports, report => report.Level == StatusLevel.Warning && report.Message.Contains("försöker återansluta", StringComparison.Ordinal));
        Assert.Contains(reports, report => report.Message.StartsWith("OBS återanslöt efter 12 s", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, report => report.Message.Contains("min mätt", StringComparison.Ordinal));
    }

    /// <summary>The figures from the real log of 7 October are read, including OBS's own denominator.</summary>
    [Fact]
    public void TheLogsNetworkDropLineIsRead()
    {
        var summary = ReadLog("""
            22:12:13.292: Output 'rtmp multitrack video': stopping
            22:12:13.292: Output 'rtmp multitrack video': Total frames output: 168233 (199092 attempted)
            22:12:13.292: Output 'rtmp multitrack video': Total drawn frames: 199256 (199257 attempted)
            22:12:13.292: Output 'rtmp multitrack video': Number of lagged frames due to rendering lag/stalls: 1 (0.0%)
            22:12:13.292: Output 'rtmp multitrack video': Number of dropped frames due to insufficient bandwidth/connection stalls: 30859 (15.5%)
            22:12:13.292: Video stopped, number of skipped frames due to encoding lag: 1/199153 (0.0%)
            """);

        Assert.NotNull(summary);
        Assert.Equal(30859, summary.NetworkDroppedFrames);
        Assert.Equal(199092, summary.StreamAttemptedFrames);
        Assert.Equal(0.155, summary.NetworkDropShare!.Value, precision: 3);
        Assert.Contains("tappades mot nätverket", summary.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A recording stopped after a bad stream keeps the stream's drops and denominator. Before, its block
    /// cleared them — losing exactly the evening this reads the log for.
    /// </summary>
    [Fact]
    public void ARecordingStoppedAfterTheStreamKeepsTheStreamsDrops()
    {
        var summary = ReadLog("""
            22:12:13.292: Output 'rtmp multitrack video': Total frames output: 168233 (199092 attempted)
            22:12:13.292: Output 'rtmp multitrack video': Number of lagged frames due to rendering lag/stalls: 1 (0.0%)
            22:12:13.292: Output 'rtmp multitrack video': Number of dropped frames due to insufficient bandwidth/connection stalls: 30859 (15.5%)
            22:30:00.000: Output 'adv_file_output': Total frames output: 647
            22:30:00.000: Output 'adv_file_output': Number of lagged frames due to rendering lag/stalls: 0 (0.0%)
            """);

        Assert.NotNull(summary);
        Assert.Equal(30859, summary.NetworkDroppedFrames);
        Assert.Equal(199092, summary.StreamAttemptedFrames);
    }

    /// <summary>A later clean stream does replace an earlier bad one.</summary>
    [Fact]
    public void ALaterCleanStreamClearsTheEarlierDrops()
    {
        var summary = ReadLog("""
            22:12:13.292: Output 'rtmp multitrack video': Total frames output: 168233 (199092 attempted)
            22:12:13.292: Output 'rtmp multitrack video': Number of lagged frames due to rendering lag/stalls: 1 (0.0%)
            22:12:13.292: Output 'rtmp multitrack video': Number of dropped frames due to insufficient bandwidth/connection stalls: 30859 (15.5%)
            01:39:07.929: Output 'rtmp multitrack video': Total frames output: 730546
            01:39:07.929: Output 'rtmp multitrack video': Number of lagged frames due to rendering lag/stalls: 7 (0.0%)
            """);

        Assert.NotNull(summary);
        Assert.Null(summary.NetworkDroppedFrames);
    }

    /// <summary>The connection line gives the server; one from before this stream does not count.</summary>
    [Fact]
    public void TheIngestServerIsFoundInTheLog()
    {
        var connectedAt = new DateTimeOffset(2026, 10, 7, 21, 16, 52, TimeSpan.Zero).ToLocalTime();
        var lines = new[]
        {
            ConnectionLine(connectedAt.AddHours(-1), "35.55.41.49"),
            ConnectionLine(connectedAt.AddSeconds(2), "35.55.42.6"),
        };

        Assert.Equal(
            "35.55.42.6 (ingest.global-contribute.live-video.net)",
            ObsSessionLogReader.FindStreamConnection(lines, connectedAt, connectedAt.AddSeconds(10)));

        Assert.Null(ObsSessionLogReader.FindStreamConnection(lines.Take(1), connectedAt, connectedAt.AddSeconds(10)));
    }

    /// <summary>
    /// A connection logged before the clocks went back is placed at the offset it was logged under.
    /// </summary>
    [Fact]
    public void ALineFromBeforeTheClockChangeKeepsItsOwnOffset()
    {
        var stockholm = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        // Clocks go back at 03:00 CEST on 25 October 2026. Stream connected 01:30 CEST (23:30 UTC); read
        // at 04:00 CET (03:00 UTC).
        var connected = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
        string[] lines = ["01:30:00.000: [rtmp stream: 'adv_stream'] Connection to rtmp://arn03.contribute.live-video.net/app (1.2.3.4) successful"];

        // Within a minute of the real connection it is found; a whole hour wrong, it would be dropped.
        Assert.NotNull(ObsSessionLogReader.FindStreamConnection(lines, connected.AddSeconds(30), now, stockholm));
        Assert.Null(ObsSessionLogReader.FindStreamConnection(lines, connected.AddMinutes(30), now, stockholm));
    }

    private static string ConnectionLine(DateTimeOffset at, string ip) =>
        $"{at:HH:mm:ss}.047: [rtmp stream: 'rtmp multitrack video'] Connection to rtmp://ingest.global-contribute.live-video.net/app ({ip}) successful";

    private static ObsSessionLogSummary? ReadLog(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"obs-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
        try
        {
            return ObsSessionLogReader.TryReadFile(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
