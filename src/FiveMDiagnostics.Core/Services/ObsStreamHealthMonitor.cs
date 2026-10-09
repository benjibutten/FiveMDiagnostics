using System.Globalization;

namespace FiveMDiagnostics.Core;

/// <summary>One line the stream-health monitor wants written to the session journal.</summary>
public sealed record StreamHealthReport(StatusLevel Level, string Message);

/// <summary>
/// Watches the stream output's own counters and says when frames stop reaching the ingest server.
/// </summary>
/// <remarks>
/// <para>
/// Built after two evenings where the stream visibly lagged an hour in and the app said nothing useful
/// about it. On 7 October OBS dropped 30 859 frames (15.5%) to "insufficient bandwidth/connection
/// stalls" in 56 minutes; on 8 October 7 352 (1.7%) in two hours. Both times the game ran clean, the
/// encoder kept up, and the session summary closed on "ingen bildruta uteblev för tittarna", because the
/// only counter it read was the encoder's. Both times a restart fixed it, and both times the restart
/// landed on a different ingest server — which the OBS log recorded and nothing here looked at.
/// </para>
/// <para>
/// What the next evening needs is the clock: when the drops started, how many minutes into the stream,
/// what the upload rate was doing, and which server the stream was talking to. That is what tells a
/// connection that degraded from a machine that did, and it is the difference between "restart the
/// stream" and "restart the computer" as the fix.
/// </para>
/// <para>
/// Measured in one-minute windows. A single poll's delta is too noisy to judge — OBS drops frames in
/// bursts when its send buffer fills — and the counter is cumulative, so a whole-stream percentage
/// hides an hour of clean streaming in front of ten bad minutes.
/// </para>
/// <para>
/// Which stream a reading belongs to, and when it started, is decided from OBS's own counters rather than
/// from what this monitor happened to see. The WebSocket can be down for minutes while OBS carries on —
/// and in that time a stream can stop, start again and outrun the old one's frame count — so "the counter
/// went down" and "the first reading I saw" both get the stream wrong exactly when the socket was the
/// problem.
/// </para>
/// </remarks>
public sealed class ObsStreamHealthMonitor
{
    /// <summary>Length of one judged window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Share of a window's frames dropped before the window counts as bad.
    /// </summary>
    /// <remarks>
    /// One percent is 36 frames a minute at 60 fps — about half a second of stream lost every minute,
    /// which viewers see as a stutter. The evenings this was built from ran at 15.5% and 1.7% averaged
    /// over the whole stream, so either one is far above it in the minutes that mattered; a stray burst
    /// of a dozen frames in an otherwise healthy hour is not.
    /// </remarks>
    public const double BadWindowShare = 0.01;

    /// <summary>Consecutive clean windows before a bad period is declared over.</summary>
    /// <remarks>
    /// Network drops come and go in bursts while a connection is struggling. Calling it over after one
    /// clean minute would write "it stopped" and "it started" alternately for the rest of the evening.
    /// </remarks>
    public const int CleanWindowsToRecover = 3;

    /// <summary>How often an ongoing bad period is restated.</summary>
    /// <remarks>
    /// Often enough that the journal shows whether it is getting worse; rarely enough that a bad hour is
    /// twelve lines rather than sixty.
    /// </remarks>
    public static readonly TimeSpan OngoingReportInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Slack on top of the elapsed time when deciding whether a reading's implied start can still be the
    /// open stream's.
    /// </summary>
    /// <remarks>
    /// Poll jitter and the rounding in obs-websocket's duration. Seconds, not more: the movement a real
    /// stream can make is already allowed for by the elapsed time itself.
    /// </remarks>
    private static readonly TimeSpan SameStreamTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The most reconnecting one stream is assumed to have done between two readings.
    /// </summary>
    private static readonly TimeSpan MaxReconnectDrift = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a stream may have been running at its first reading and still count as seen starting.
    /// </summary>
    private static readonly TimeSpan SeenStartingTolerance = TimeSpan.FromSeconds(10);

    private StreamState? _stream;
    private bool _seenConnected;
    private bool _gapSinceLastReading;
    private long? _lastAttempted;
    private DateTimeOffset? _lastReadingAt;

    /// <summary>The ingest server the current stream was found to be talking to, if known.</summary>
    public string? CurrentIngest => _stream?.Ingest;

    /// <summary>When the current stream's connection (its start, or its latest reconnect) was made.</summary>
    /// <remarks>
    /// Used by the collector to look up the ingest server in OBS's log near that moment, so it is the
    /// real start — taken from OBS's own duration — even for a stream that was already running when the
    /// session began, whose connection line lies well before the first reading.
    /// </remarks>
    public DateTimeOffset? ConnectionStartedAt => _stream?.ConnectionStartedAt;

    /// <summary>Takes one poll and returns whatever is worth saying about it.</summary>
    public IReadOnlyList<StreamHealthReport> Observe(ObsTelemetrySample sample)
    {
        var reports = new List<StreamHealthReport>();

        if (!sample.IsConnected)
        {
            if (!sample.IsProcessRunning && _stream is not null)
            {
                // OBS itself is gone, so whatever it was streaming is over. Closed at the last reading
                // that saw it, not now: the game can run for hours after OBS, and the summary of a
                // 20:00–21:00 stream must not say 23:00.
                reports.AddRange(Close(StreamEnding.ObsClosed));
            }

            // Only the socket is down: the stream may well still be running, and the next connected
            // reading decides — by its own duration — whether it is the same one.
            _gapSinceLastReading = true;
            return reports;
        }

        var streaming = sample.IsStreaming;
        var impliedStart = ImpliedStart(sample);

        if (_stream is not null && (!streaming || IsDifferentStream(_stream, sample, impliedStart)))
        {
            reports.AddRange(Close(StreamEnding.Stopped));
        }

        if (streaming && _stream is null)
        {
            var startedAt = impliedStart ?? sample.Timestamp;

            // Without a duration to go by, a stream with frames already sent at the session's first
            // reading, or after the socket was down, started at some point this cannot see.
            var unseenStart = impliedStart is { } start
                ? sample.Timestamp - start > SeenStartingTolerance
                : sample.StreamTotalFrames is > 0 && (!_seenConnected || _gapSinceLastReading);

            _stream = new StreamState(sample, startedAt, unseenStart, startKnown: impliedStart is not null);
            reports.Add(new StreamHealthReport(StatusLevel.Info, DescribeStart(_stream)));
        }

        _seenConnected = true;
        _gapSinceLastReading = false;
        _lastAttempted = Attempted(sample);
        _lastReadingAt = sample.Timestamp;

        if (_stream is null)
        {
            return reports;
        }

        _stream.LastImpliedStart = impliedStart;

        reports.AddRange(ObserveReconnect(_stream, sample));
        reports.AddRange(ObserveWindow(_stream, sample));
        return reports;
    }

    /// <summary>
    /// Records which ingest server the current stream connected to, and says so once per server.
    /// </summary>
    public StreamHealthReport? NoteIngest(string ingest)
    {
        if (_stream is null || string.Equals(_stream.Ingest, ingest, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var previous = _stream.Ingest;
        _stream.Ingest = ingest;

        return new StreamHealthReport(
            StatusLevel.Info,
            previous is null
                ? $"Streamen skickar till {ingest} (enligt OBS-loggen). Servern står med i alla rader om tappade "
                    + "bildrutor, så en kväll med problem kan jämföras med en utan."
                : $"Streamen bytte server från {previous} till {ingest} efter återanslutningen.");
    }

    /// <summary>Writes the summary for a stream still open when the session ends.</summary>
    public IReadOnlyList<StreamHealthReport> Finish()
    {
        return _stream is null ? [] : Close(StreamEnding.SessionEnded);
    }

    /// <summary>Frames the output tried to send: the ones it sent and the ones it dropped.</summary>
    private static long? Attempted(ObsTelemetrySample sample)
    {
        return sample.StreamTotalFrames is { } total ? total + (sample.StreamDroppedFrames ?? 0) : null;
    }

    /// <summary>
    /// When the stream started, as implied by how many frames it has tried to send.
    /// </summary>
    /// <remarks>
    /// obs-websocket's <c>outputDuration</c> is the sent-frame count times the frame interval, so it falls
    /// behind the clock by every dropped frame — a 15% drop rate would move a start read from it later
    /// by nine seconds a minute. The interval is recovered from the duration and the sent count, and the
    /// dropped frames are put back. What is left is only the time spent reconnecting, which sends nothing
    /// and drops nothing.
    /// </remarks>
    private static DateTimeOffset? ImpliedStart(ObsTelemetrySample sample)
    {
        if (sample.StreamDurationMs is not { } duration || sample.StreamTotalFrames is not { } total || total <= 0
            || Attempted(sample) is not { } attempted)
        {
            return null;
        }

        var frameMs = (double)duration / total;
        return sample.Timestamp - TimeSpan.FromMilliseconds(attempted * frameMs);
    }

    /// <summary>
    /// Whether a connected, streaming reading belongs to a stream other than the open one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the same stream the implied start never moves earlier, and moves later only by time spent
    /// reconnecting — never by more than has passed, and in practice never by minutes, because a stream
    /// that cannot get through for that long has usually been given up on by OBS and stopped. A stream
    /// started anew moves it later by at least as long as the old one had been running.
    /// </para>
    /// <para>
    /// So a shift beyond <see cref="MaxReconnectDrift"/> is a new stream even across a long WebSocket
    /// outage, where comparing frame counts fails as soon as the new stream has outrun the old one. The
    /// attempted-frame count going down still catches a stop and restart inside the first couple of
    /// minutes. What is given up is a single reconnect longer than the drift allowance: it is read as a
    /// new stream, which splits one summary into two rather than merging two streams' counters into one.
    /// </para>
    /// </remarks>
    private bool IsDifferentStream(StreamState stream, ObsTelemetrySample sample, DateTimeOffset? impliedStart)
    {
        if (Attempted(sample) is { } attempted && _lastAttempted is { } previousAttempted && attempted < previousAttempted)
        {
            return true;
        }

        if (impliedStart is not { } implied || stream.LastImpliedStart is not { } previous || _lastReadingAt is not { } lastAt)
        {
            return false;
        }

        var shift = implied - previous;
        var elapsed = sample.Timestamp - lastAt;
        var allowance = (elapsed < MaxReconnectDrift ? elapsed : MaxReconnectDrift) + SameStreamTolerance;
        return shift > allowance || shift < -SameStreamTolerance;
    }

    private IEnumerable<StreamHealthReport> ObserveReconnect(StreamState stream, ObsTelemetrySample sample)
    {
        if (sample.IsStreamReconnecting && stream.ReconnectingSince is null)
        {
            stream.ReconnectingSince = sample.Timestamp;
            stream.Reconnects++;
            yield return new StreamHealthReport(
                StatusLevel.Warning,
                $"OBS tappade anslutningen till servern{Server(stream)} kl. {Clock(sample.Timestamp)} "
                + $"({MinutesIn(stream, sample.Timestamp)}) och försöker återansluta.");
        }
        else if (!sample.IsStreamReconnecting && stream.ReconnectingSince is { } since)
        {
            stream.ReconnectingSince = null;
            stream.ConnectionStartedAt = sample.Timestamp;
            yield return new StreamHealthReport(
                StatusLevel.Info,
                $"OBS återanslöt efter {(sample.Timestamp - since).TotalSeconds:F0} s.");
        }
    }

    private IEnumerable<StreamHealthReport> ObserveWindow(StreamState stream, ObsTelemetrySample sample)
    {
        stream.Last = sample;
        stream.WindowMaxCongestion = Math.Max(stream.WindowMaxCongestion, sample.StreamCongestion ?? 0);

        if (sample.Timestamp - stream.WindowStart.Timestamp < Window)
        {
            yield break;
        }

        var window = Measure(stream.WindowStart, sample, stream.WindowMaxCongestion);
        stream.WindowStart = sample;
        stream.WindowMaxCongestion = 0;

        if (window is null)
        {
            yield break;
        }

        if (IsBad(window))
        {
            stream.BadWindows++;
            stream.FirstBadAt ??= window.From;
            stream.CleanInRow = 0;

            if (stream.TroubleSince is null)
            {
                stream.TroubleSince = window.From;
                stream.TroubleDropped = window.Dropped;
                stream.TroubleTotal = window.Total;
                stream.LastTroubleReport = window.To;
                yield return new StreamHealthReport(StatusLevel.Warning, DescribeOnset(stream, window));
                yield break;
            }

            stream.TroubleDropped += window.Dropped;
            stream.TroubleTotal += window.Total;
        }
        else if (stream.TroubleSince is not null)
        {
            stream.TroubleDropped += window.Dropped;
            stream.TroubleTotal += window.Total;

            if (++stream.CleanInRow >= CleanWindowsToRecover)
            {
                var report = DescribeRecovery(stream, window.To);
                stream.TroubleSince = null;
                stream.CleanInRow = 0;
                yield return new StreamHealthReport(StatusLevel.Info, report);
                yield break;
            }
        }

        if (stream.TroubleSince is not null && window.To - stream.LastTroubleReport >= OngoingReportInterval)
        {
            stream.LastTroubleReport = window.To;
            yield return new StreamHealthReport(StatusLevel.Warning, DescribeOngoing(stream, window));
        }
    }

    private static bool IsBad(WindowMeasurement window) => window.Total > 0 && window.Share >= BadWindowShare;

    private IReadOnlyList<StreamHealthReport> Close(StreamEnding ending)
    {
        var stream = _stream!;
        _stream = null;

        if (stream.Last.StreamTotalFrames is null)
        {
            // Connected and streaming, but OBS never reported the output's counters — an obs-websocket
            // too old to carry them. Nothing measured means nothing to summarise.
            return [];
        }

        // The minute that was still filling when the stream ended is judged too. Without it a stream
        // shorter than one window was never judged at all, and the last bad seconds of a longer one
        // were left out of the count that decides the summary's level.
        if (Measure(stream.WindowStart, stream.Last, stream.WindowMaxCongestion) is { } tail && IsBad(tail))
        {
            stream.BadWindows++;
            stream.FirstBadAt ??= tail.From;
        }

        var totals = Measure(stream.First, stream.Last, 0);
        if (totals is null)
        {
            return [];
        }

        // Both ends from readings, so the times and the length in the line agree with each other however
        // long after the last reading the stream was found to be over.
        var end = Clock(stream.Last.Timestamp) + ending switch
        {
            StreamEnding.ObsClosed => " (OBS stängdes)",
            StreamEnding.SessionEnded => " (sista mätningen; sessionen slutade innan streamen sågs stoppa)",
            _ => string.Empty,
        };

        var start = stream.UnseenStart
            ? stream.StartKnown
                ? $"{Clock(stream.StartedAt)} (mätt från {Clock(stream.First.Timestamp)})"
                : $"mätt från {Clock(stream.First.Timestamp)}"
            : Clock(stream.StartedAt);

        var measured = stream.Last.Timestamp - stream.First.Timestamp;
        var head = $"Streamen {start}–{end}, {measured.TotalMinutes:F0} min mätt{Server(stream)}: ";

        if (totals.Dropped == 0)
        {
            var rate = totals.Mbps is { } mbps ? $", snitt {mbps:F1} Mbit/s" : string.Empty;
            return
            [
                new StreamHealthReport(
                    StatusLevel.Info,
                    head + $"inga bildrutor tappades mot nätverket ({totals.Total:N0} skickade{rate})."
                        + DescribeReconnects(stream)),
            ];
        }

        var windows = Math.Max(1, (int)Math.Ceiling(measured / Window));
        string spread;
        if (stream.BadWindows > 0 && stream.FirstBadAt is { } firstBad)
        {
            spread = $" Tappet låg i {stream.BadWindows} av {windows} minuter, första kl. {Clock(firstBad)} "
                + $"({MinutesIn(stream, firstBad)}).";
        }
        else if (windows > 1)
        {
            // Only a stream long enough to have several windows can have its drops spread across them.
            spread = $" Ingen enskild minut nådde {BadWindowShare.ToString("P0", CultureInfo.CurrentCulture)}, "
                + "så tappet var utspritt snarare än en period.";
        }
        else
        {
            spread = string.Empty;
        }

        var bad = stream.BadWindows > 0 || totals.Share >= BadWindowShare;
        return
        [
            new StreamHealthReport(
                bad ? StatusLevel.Warning : StatusLevel.Info,
                head + $"{totals.Dropped:N0} av {totals.Total:N0} bildrutor tappades mot nätverket ({totals.Share:P1})."
                    + spread
                    + DescribeEncoder(totals)
                    + DescribeReconnects(stream)),
        ];
    }

    private static string DescribeStart(StreamState stream)
    {
        if (!stream.UnseenStart)
        {
            return $"Streamen startade kl. {Clock(stream.StartedAt)}. Tappade bildrutor mot nätverket mäts per minut.";
        }

        var when = stream.StartKnown
            ? $"Streamen startade kl. {Clock(stream.StartedAt)} enligt OBS och pågick redan när mätningen började"
            : "Streamen pågick redan när mätningen började";

        return $"{when} ({stream.First.StreamTotalFrames:N0} bildrutor skickade). Tappade bildrutor mot nätverket "
            + "mäts per minut från och med nu; det som hände innan räknas inte.";
    }

    private static string DescribeOnset(StreamState stream, WindowMeasurement window)
    {
        return $"Streamen tappar bildrutor mot nätverket: {window.Dropped:N0} av {window.Total:N0} ({window.Share:P1}) "
            + $"mellan {Clock(window.From)} och {Clock(window.To)}, {MinutesIn(stream, window.From)}{Server(stream)}. "
            + DescribeRate(window)
            + DescribeEncoder(window)
            + " Testa att stoppa och starta bara streamen: försvinner tappet är det anslutningen eller servern, "
            + "finns det kvar ligger det i datorn eller det lokala nätet.";
    }

    private static string DescribeOngoing(StreamState stream, WindowMeasurement window)
    {
        var share = stream.TroubleTotal > 0 ? (double)stream.TroubleDropped / stream.TroubleTotal : 0;
        return $"Tappet mot nätverket pågår sedan {Clock(stream.TroubleSince!.Value)}: {stream.TroubleDropped:N0} av "
            + $"{stream.TroubleTotal:N0} bildrutor ({share:P1}) hittills{Server(stream)}. Senaste minuten "
            + $"{window.Share:P1}. " + DescribeRate(window).TrimEnd();
    }

    private static string DescribeRecovery(StreamState stream, DateTimeOffset at)
    {
        var since = stream.TroubleSince!.Value;
        var share = stream.TroubleTotal > 0 ? (double)stream.TroubleDropped / stream.TroubleTotal : 0;
        return $"Tappet mot nätverket har upphört ({CleanWindowsToRecover} rena minuter i rad till kl. {Clock(at)}). "
            + $"Perioden {Clock(since)}–{Clock(at)} tappade {stream.TroubleDropped:N0} av {stream.TroubleTotal:N0} "
            + $"bildrutor ({share:P1}).";
    }

    private static string DescribeRate(WindowMeasurement window)
    {
        if (window.Mbps is not { } mbps)
        {
            return string.Empty;
        }

        var congestion = window.MaxCongestion > 0
            ? $", OBS congestion upp till {window.MaxCongestion.ToString("F2", CultureInfo.InvariantCulture)}"
            : string.Empty;

        return $"Uppladdningen låg på {mbps:F1} Mbit/s{congestion}.";
    }

    /// <summary>
    /// Says whether the encoder kept up in the same span, which is what places the fault outside the PC.
    /// </summary>
    private static string DescribeEncoder(WindowMeasurement window)
    {
        if (window.EncoderSkipped is not { } skipped)
        {
            return string.Empty;
        }

        return skipped * 10 < window.Dropped
            ? $" Kodningen hann med ({skipped:N0} överhoppade), så det är sändningen ut som inte kom fram — inte datorn."
            : $" Kodningen hoppade också över {skipped:N0} bildrutor, så datorn hann inte heller med fullt ut.";
    }

    private static string DescribeReconnects(StreamState stream)
    {
        return stream.Reconnects switch
        {
            0 => string.Empty,
            1 => " OBS återanslöt en gång under streamen.",
            var count => $" OBS återanslöt {count} gånger under streamen.",
        };
    }

    private static WindowMeasurement? Measure(ObsTelemetrySample from, ObsTelemetrySample to, double maxCongestion)
    {
        if (from.StreamDroppedFrames is not { } droppedFrom
            || to.StreamDroppedFrames is not { } droppedTo
            || from.StreamTotalFrames is not { } totalFrom
            || to.StreamTotalFrames is not { } totalTo)
        {
            return null;
        }

        var dropped = Math.Max(0, droppedTo - droppedFrom);

        // Attempted, not sent: OBS's total excludes the frames it dropped, and its own log prints the
        // percentage against sent plus dropped. Against sent alone 7 October would read 18.3%, not 15.5%.
        var total = Math.Max(0, totalTo - totalFrom) + dropped;
        var share = total > 0 ? (double)dropped / total : 0;

        var seconds = (to.Timestamp - from.Timestamp).TotalSeconds;
        double? mbps = seconds > 0 && from.StreamBytes is { } bytesFrom && to.StreamBytes is { } bytesTo && bytesTo >= bytesFrom
            ? (bytesTo - bytesFrom) * 8 / seconds / 1_000_000
            : null;

        long? encoderSkipped = from.OutputSkippedFrames is { } skippedFrom && to.OutputSkippedFrames is { } skippedTo
            ? Math.Max(0, skippedTo - skippedFrom)
            : null;

        return new WindowMeasurement(from.Timestamp, to.Timestamp, dropped, total, share, mbps, maxCongestion, encoderSkipped);
    }

    private static string Server(StreamState stream)
    {
        return stream.Ingest is { } ingest ? $", server {ingest}" : string.Empty;
    }

    private static string MinutesIn(StreamState stream, DateTimeOffset at)
    {
        var minutes = (at - stream.StartedAt).TotalMinutes;
        return stream.UnseenStart && !stream.StartKnown
            ? $"{minutes:F0} min efter att mätningen började"
            : $"{minutes:F0} min in i streamen";
    }

    private static string Clock(DateTimeOffset at)
    {
        return at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private enum StreamEnding
    {
        Stopped,
        ObsClosed,
        SessionEnded,
    }

    private sealed record WindowMeasurement(
        DateTimeOffset From,
        DateTimeOffset To,
        long Dropped,
        long Total,
        double Share,
        double? Mbps,
        double MaxCongestion,
        long? EncoderSkipped);

    private sealed class StreamState(ObsTelemetrySample first, DateTimeOffset startedAt, bool unseenStart, bool startKnown)
    {
        public ObsTelemetrySample First { get; } = first;

        /// <summary>When the stream really started: from OBS's duration when known, else the first reading.</summary>
        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>The stream was already running at its first reading, so its counters before that are not ours.</summary>
        public bool UnseenStart { get; } = unseenStart;

        /// <summary>Whether <see cref="StartedAt"/> came from OBS rather than from the first reading.</summary>
        public bool StartKnown { get; } = startKnown;

        public DateTimeOffset? LastImpliedStart { get; set; }

        public ObsTelemetrySample Last { get; set; } = first;

        public ObsTelemetrySample WindowStart { get; set; } = first;

        public double WindowMaxCongestion { get; set; }

        public DateTimeOffset ConnectionStartedAt { get; set; } = startedAt;

        public string? Ingest { get; set; }

        public int BadWindows { get; set; }

        public DateTimeOffset? FirstBadAt { get; set; }

        public DateTimeOffset? TroubleSince { get; set; }

        public long TroubleDropped { get; set; }

        public long TroubleTotal { get; set; }

        public DateTimeOffset LastTroubleReport { get; set; }

        public int CleanInRow { get; set; }

        public DateTimeOffset? ReconnectingSince { get; set; }

        public int Reconnects { get; set; }
    }
}
