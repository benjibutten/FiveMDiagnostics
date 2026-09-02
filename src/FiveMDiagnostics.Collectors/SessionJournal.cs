using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

/// <summary>
/// Appends status entries and completed incidents to a JSON Lines file for as long as a session runs.
/// </summary>
/// <remarks>
/// Incidents used to exist only in memory, and reached disk exclusively when the user selected one and
/// pressed export. A six hour stream could therefore auto mark dozens of incidents and leave nothing
/// behind but a PresentMon CSV and whatever ETL traces deep capture wrote, and closing the window threw
/// away the whole history — including the status entries proving the frame telemetry had died in the
/// first minute, which is the one thing that explains why the history was empty to begin with.
/// <para>
/// Only summary level data is written. An incident's own event window is a minute and a half of frame
/// samples, which belongs in an export bundle rather than in a file that keeps growing for six hours;
/// what the journal keeps is enough to reconstruct what happened and when, and to decide which
/// incidents are worth exporting properly.
/// </para>
/// <para>
/// The file lives in the working directory next to the raw captures, and is written unredacted for the
/// same reason those are: it is local evidence, not a bundle meant to be handed to someone else. The
/// redaction rules in <see cref="PrivacyOptions"/> still apply to everything that leaves the machine.
/// </para>
/// </remarks>
public sealed class SessionJournal : IDisposable
{
    /// <summary>
    /// Ceiling on one session's journal. A collector stuck in a failing loop can report on every poll,
    /// and this file is written for the entire length of a stream, so the size has to be bounded by
    /// something other than the user noticing.
    /// </summary>
    public const long DefaultMaxBytes = 8L * 1024 * 1024;

    /// <summary>Floor for the budget, so a hand-edited or mistaken value still fits a session's incidents.</summary>
    public const long MinimumMaxBytes = 64L * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // JSON Lines puts one record on each line, so indentation would not merely be noise: it would
        // break the format for every reader that splits on newlines.
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Numeric enums make the file depend on declaration order, and this one is meant to still be
        // readable after the code that wrote it has moved on.
        Converters = { new JsonStringEnumConverter(), new UtcDateTimeOffsetConverter() },
    };

    private readonly object _sync = new();
    private readonly long _maxBytes;

    /// <summary>
    /// Bytes held back from the budget for the closing journal-truncated line. Without the reserve that
    /// line is written once the budget is already spent, so the file announcing its own limit is the
    /// one that exceeds it.
    /// </summary>
    private readonly long _truncationReserve;

    private StreamWriter? _writer;
    private long _bytesWritten;
    private int _incidentCount;
    private string? _pendingFailure;

    private SessionJournal(StreamWriter writer, string path, long maxBytes, long existingBytes)
    {
        _writer = writer;
        _maxBytes = maxBytes;
        Path = path;

        // The budget covers the file, not one session's share of it, so a session appending to a file an
        // earlier session already filled starts from that weight rather than from zero.
        _bytesWritten = existingBytes;

        // Serializing a DateTimeOffset trims trailing zeros from the fraction, so the longest truncation
        // line possible is the one whose timestamp needs all seven digits.
        _truncationReserve = MeasureLine(CreateTruncationLine(DateTimeOffset.MaxValue));
    }

    /// <summary>Full path of the file being written, for the status entry that tells the user where it is.</summary>
    public string Path { get; }

    /// <summary>False once the file has been closed, whether by disposal, an IO failure or the size budget.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _writer is not null;
            }
        }
    }

    /// <summary>Incidents actually written to the file, which is what the closing line reports.</summary>
    public int IncidentCount
    {
        get
        {
            lock (_sync)
            {
                return _incidentCount;
            }
        }
    }

    /// <summary>
    /// Opens the journal for a session. Returns null with a reason in <paramref name="error"/> rather
    /// than throwing: a session that cannot write its journal is still a session worth running.
    /// </summary>
    public static SessionJournal? TryOpen(string workingDirectory, DateTimeOffset startedAtUtc, out string? error, long maxBytes = DefaultMaxBytes)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(workingDirectory);
            var path = System.IO.Path.Combine(workingDirectory, $"session_{startedAtUtc:yyyyMMdd_HHmmss}.jsonl");

            // Append rather than Create, so two sessions started inside the same second add to the file
            // instead of the second one erasing the first. FileShare.Read lets the file be tailed or
            // copied while the session is still running, which is when it is most interesting.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var existingBytes = stream.Length;
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                // The failure this file exists for is the app being closed or killed, not a graceful
                // shutdown, so every line has to be on its way to disk by the time it is written.
                AutoFlush = true,
            };

            return new SessionJournal(writer, path, Math.Max(maxBytes, MinimumMaxBytes), existingBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Records what the session is about to run: environment, and the settings that shape the evidence.</summary>
    public void WriteSessionStart(EnvironmentMetadata? environment, DiagnosticsSettings settings, DateTimeOffset startedAt)
    {
        Append("session-start", startedAt, new
        {
            Environment = environment,
            Settings = new
            {
                ServerProfile = settings.ServerProfile.Name,
                settings.RingBufferRetention,
                settings.PreIncidentWindow,
                settings.PostIncidentWindow,
                settings.MaxRetainedIncidents,
                AutoDetect = settings.AutoDetect,
                FramePacing = settings.FramePacing,
                DeepCaptureEnabled = settings.DeepCapture.Enabled,
            },
        });
    }

    /// <summary>Records one status entry, i.e. everything the user can see in the status list.</summary>
    public void WriteStatus(DiagnosticStatusEntry entry)
    {
        Append("status", entry.Timestamp, new
        {
            entry.Level,
            entry.Source,
            entry.Message,
        });
    }

    /// <summary>
    /// Records one classified frame pacing window, whatever its state.
    /// </summary>
    /// <remarks>
    /// Healthy windows are written too, and that is the point: the share of a session that could not
    /// hold its frame rate is only computable if the good minutes are on record next to the bad ones.
    /// At a minute per window a whole evening costs a few hundred short lines, which is a fraction of
    /// what a single incident's payload takes.
    /// </remarks>
    public void WritePacingWindow(FramePacingWindow window)
    {
        Append("pacing", window.End, new
        {
            window.State,
            window.Start,
            window.FrameCount,
            AchievedFps = Math.Round(window.AchievedFps, 2),
            TargetFps = Math.Round(window.TargetFps, 2),
            MedianFrameTimeMs = Math.Round(window.MedianFrameTimeMs, 3),
            MedianCpuWaitMs = window.MedianCpuWaitMs is { } wait ? Math.Round(wait, 3) : (double?)null,
            MedianCpuBusyMs = window.MedianCpuBusyMs is { } cpu ? Math.Round(cpu, 3) : (double?)null,
            MedianGpuBusyMs = window.MedianGpuBusyMs is { } gpu ? Math.Round(gpu, 3) : (double?)null,
            window.SustainedWindows,
        });
    }

    /// <summary>
    /// Records a completed incident at summary level: what was marked, what the analysis concluded, and
    /// which collectors actually contributed telemetry to the window.
    /// </summary>
    /// <remarks>
    /// The event counts matter as much as the analysis. An incident whose window holds no frame samples
    /// at all is not a quiet incident, it is a broken PresentMon capture, and that distinction is
    /// invisible in a summary that only reports what the correlation engine managed to conclude.
    /// </remarks>
    public void WriteIncident(IncidentRecord incident)
    {
        var written = Append("incident", incident.Marker.MarkedAt, CreateIncidentPayload(incident));

        if (!written)
        {
            return;
        }

        lock (_sync)
        {
            _incidentCount++;
        }
    }

    /// <summary>
    /// Records an incident that changed after it was first written, which is what an artifact import
    /// does: it adds evidence to the most recent incident and re-runs the analysis.
    /// </summary>
    /// <remarks>
    /// Written as its own line rather than by rewriting the original one. The file is append only —
    /// rewriting would mean holding it open for random access and losing the guarantee that whatever
    /// reached disk survives a kill — and a reader taking the last line per id still ends up with the
    /// current state, while one reading in order can see what was known when.
    /// </remarks>
    public void WriteIncidentUpdate(IncidentRecord incident)
    {
        Append("incident-update", DateTimeOffset.UtcNow, CreateIncidentPayload(incident));
    }

    private static object CreateIncidentPayload(IncidentRecord incident)
    {
        return new
        {
            incident.Id,
            incident.Marker.MarkedAt,
            incident.Marker.Severity,
            incident.Marker.Label,
            incident.WindowStart,
            incident.WindowEnd,
            Summary = incident.Analysis?.Summary,
            InsufficientEvidence = incident.Analysis?.InsufficientEvidence,
            Hypotheses = incident.Analysis?.Hypotheses
                .Take(3)
                .Select(item => new { item.Category, item.Confidence })
                .ToArray(),
            SuspectedProcesses = incident.Analysis?.SuspectedProcesses
                .Select(item => new { item.ProcessName, item.PeakCpuPercent, item.PeakIoMegabytesPerSecond, item.Reason })
                .ToArray(),
            Timeline = incident.Analysis?.TimelineHighlights
                .Select(item => new { item.Timestamp, item.Category, item.Summary })
                .ToArray(),
            EventCounts = incident.Events
                .GroupBy(item => item.Source)
                .ToDictionary(group => group.Key, group => group.Count()),
            Attachments = incident.Attachments.Select(item => item.DisplayName).ToArray(),
        };
    }

    /// <summary>Closes the session off with the totals, so a truncated file is recognisable as truncated.</summary>
    public void WriteSessionEnd(DateTimeOffset endedAt)
    {
        Append("session-end", endedAt, new
        {
            IncidentCount = IncidentCount,
        });
    }

    /// <summary>
    /// Hands over the first failure the journal has hit, once. The caller reports it through the normal
    /// status path; taking it clears it, so a broken disk produces one warning rather than one per line.
    /// </summary>
    public bool TryTakeFailure(out string message)
    {
        lock (_sync)
        {
            if (_pendingFailure is null)
            {
                message = string.Empty;
                return false;
            }

            message = _pendingFailure;
            _pendingFailure = null;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Writes one line. Returns false when it was dropped, whatever the reason.</summary>
    private bool Append(string type, DateTimeOffset timestamp, object payload)
    {
        lock (_sync)
        {
            if (_writer is null)
            {
                return false;
            }

            string line;
            try
            {
                line = JsonSerializer.Serialize(new JournalLine(type, timestamp, payload), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // One unserializable record is not a reason to stop journaling the rest of the session.
                RecordFailureLocked($"En journalrad kunde inte serialiseras: {ex.Message}");
                return false;
            }

            var lineBytes = MeasureLine(line);
            if (_bytesWritten + lineBytes + _truncationReserve > _maxBytes)
            {
                CloseWithBudgetReachedLocked();
                return false;
            }

            try
            {
                _writer.WriteLine(line);
                _bytesWritten += lineBytes;
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                RecordFailureLocked($"Sessionsloggen kunde inte skrivas och stängdes: {ex.Message}");
                _writer?.Dispose();
                _writer = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Stops at the budget with a final line saying so, because a journal that simply ends is
    /// indistinguishable from a session that crashed — the exact thing the reader is trying to rule out.
    /// </summary>
    private void CloseWithBudgetReachedLocked()
    {
        var line = CreateTruncationLine(DateTimeOffset.UtcNow);
        var lineBytes = MeasureLine(line);

        // The reserve normally guarantees this fits. It still cannot when the session inherited a file
        // another one had already filled, and a marker that pushes the file past the very limit it is
        // announcing would defeat its own purpose — the failure message says the same thing anyway.
        if (_bytesWritten + lineBytes <= _maxBytes)
        {
            try
            {
                _writer!.WriteLine(line);
                _bytesWritten += lineBytes;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // Nothing left to do: the file is being closed either way.
            }
        }

        RecordFailureLocked($"Sessionsloggen nådde sin storleksgräns på {FormatBudget(_maxBytes)} och stängdes.");
        _writer?.Dispose();
        _writer = null;
    }

    private string CreateTruncationLine(DateTimeOffset timestamp)
    {
        return JsonSerializer.Serialize(new JournalLine("journal-truncated", timestamp, new { LimitBytes = _maxBytes }), JsonOptions);
    }

    private static long MeasureLine(string line)
    {
        return Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(System.Environment.NewLine);
    }

    /// <summary>The budget can be set below a megabyte, and a warning reading "0 MB" explains nothing.</summary>
    private static string FormatBudget(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024):0.#} MB"
            : $"{bytes / 1024d:0.#} kB";
    }

    private void RecordFailureLocked(string message)
    {
        // First failure wins: it is the one that explains the others.
        _pendingFailure ??= message;
    }

    private sealed record JournalLine(string Type, DateTimeOffset Timestamp, object Payload);

    /// <summary>Writes every timestamp in the journal as UTC, whatever offset it arrived with.</summary>
    /// <remarks>
    /// The file used to carry both. Status entries were stamped by the collectors in local time and
    /// incidents and pacing windows in UTC, so a reader scrolling one evening's journal saw 01:14 and
    /// 23:14 alternating on adjacent lines for the same two minutes, and every comparison had to begin
    /// by working out which line was in which zone. Both forms were valid ISO-8601 and neither was
    /// wrong; the mixture was. Normalising on the way out is one place rather than a rule every
    /// collector has to remember, and it covers payload timestamps as well as the envelope.
    /// </remarks>
    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetDateTimeOffset();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            // UtcDateTime rather than ToUniversalTime, so the line ends in Z instead of +00:00. Both are
            // UTC; only one of them says so at a glance, and being read at a glance is the whole point.
            writer.WriteStringValue(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
    }
}
