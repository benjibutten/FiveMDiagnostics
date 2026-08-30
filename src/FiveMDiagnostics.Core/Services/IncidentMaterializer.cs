namespace FiveMDiagnostics.Core;

/// <summary>What happened when a threshold-crossing frame was offered to the incidents already open.</summary>
public enum IncidentEscalation
{
    /// <summary>No incident window covered that moment, so the caller still owes the frame an incident.</summary>
    NoOpenIncident,

    /// <summary>A window covered it and already describes something at least as bad.</summary>
    AlreadyWorse,

    /// <summary>The open incident now describes this frame instead.</summary>
    Escalated,
}

public sealed class IncidentMaterializer
{
    private readonly object _sync = new();
    private readonly TimeWindowRingBuffer<TelemetryEvent> _ringBuffer;
    private readonly TimeSpan _preWindow;
    private readonly TimeSpan _postWindow;
    private readonly Dictionary<Guid, PendingIncident> _pending = new();
    private int _pendingCount;

    public IncidentMaterializer(TimeWindowRingBuffer<TelemetryEvent> ringBuffer, TimeSpan preWindow, TimeSpan postWindow)
    {
        _ringBuffer = ringBuffer;
        _preWindow = preWindow;
        _postWindow = postWindow;
    }

    /// <param name="frameTimeMs">
    /// The frame that prompted the marker, when one did. Seeds the bar <see cref="TryEscalate"/>
    /// measures against, so a later frame has to actually be worse than the one this incident is named
    /// after before it may rewrite the label.
    /// </param>
    /// <param name="allowFrameEscalation">
    /// False for an incident that is not described by a frame time at all — a sustained saturation
    /// window, where no single frame is remarkable and the finding is that the frame rate stopped
    /// recovering. That label says strictly more than any frame inside it could, so letting a 40 ms
    /// frame overwrite it loses information. It would happen on the very next suppressed frame
    /// otherwise, because a pacing incident carries no frame time and so starts with a bar of zero that
    /// anything at all clears.
    /// </param>
    public IncidentMarker MarkIncident(
        DateTimeOffset timestamp,
        IncidentSeverity severity,
        string? label = null,
        double frameTimeMs = 0,
        bool allowFrameEscalation = true)
    {
        var marker = new IncidentMarker(
            Guid.NewGuid(),
            timestamp,
            severity,
            label ?? (severity == IncidentSeverity.Severe ? "Severe stutter" : "Stutter"));

        var pending = new PendingIncident(
            marker,
            timestamp - _preWindow,
            timestamp + _postWindow,
            _ringBuffer.Snapshot(timestamp - _preWindow, timestamp))
        {
            WorstFrameTimeMs = frameTimeMs,
            AllowFrameEscalation = allowFrameEscalation,
        };

        lock (_sync)
        {
            _pending[marker.Id] = pending;
            Volatile.Write(ref _pendingCount, _pending.Count);
        }

        return marker;
    }

    /// <summary>
    /// Rewrites an open incident to describe a worse hitch that landed inside its window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Incident windows span ninety seconds and the detector holds a cooldown, so at most one incident
    /// covers any given moment. That is the right rate — a burst is one event — but it meant the window
    /// was named after whichever frame happened to open it, which is systematically the smallest one:
    /// a 41 ms frame opened a window, and the 1 018 ms frame nine seconds later inherited its label. In
    /// the same session a 2 846 ms frame, the worst of the evening, was recorded nowhere at all.
    /// </para>
    /// <para>
    /// So the marker is replaced rather than the incident duplicated. The identity, the window bounds
    /// and every event collected so far are kept; only the severity and label move up to the worst frame
    /// seen. <see cref="IncidentEscalation.AlreadyWorse"/> is what stops a long freeze from escalating
    /// on every frame of itself.
    /// </para>
    /// <para>
    /// The outcome separates "nothing was open" from "the open one is already worse", because the caller
    /// has to react differently. The detector holds a two minute cooldown while an incident window closes
    /// sixty seconds after its marker, so there is a minute in every cycle when a frame is suppressed and
    /// no window exists to absorb it. Collapsing both cases into one null return dropped those frames
    /// entirely, which is the exact loss this path was added to prevent.
    /// </para>
    /// </remarks>
    public IncidentEscalation TryEscalate(
        DateTimeOffset timestamp,
        IncidentSeverity severity,
        string label,
        double frameTimeMs,
        out IncidentMarker? escalated)
    {
        escalated = null;

        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                return IncidentEscalation.NoOpenIncident;
            }

            // Windows cannot overlap in practice, but picking the newest keeps the choice defined if the
            // cooldown is ever configured shorter than the window.
            var open = _pending.Values
                .Where(item => timestamp >= item.WindowStart && timestamp <= item.WindowEnd)
                .OrderByDescending(item => item.Marker.MarkedAt)
                .FirstOrDefault();

            if (open is null)
            {
                return IncidentEscalation.NoOpenIncident;
            }

            if (!open.AllowFrameEscalation || frameTimeMs <= open.WorstFrameTimeMs)
            {
                return IncidentEscalation.AlreadyWorse;
            }

            open.WorstFrameTimeMs = frameTimeMs;

            // Severity only ever rises. An incident opened as Severe stays Severe even if the frame that
            // escalates its label happens to classify as Normal against a baseline that has since moved.
            escalated = open.Marker with
            {
                Severity = severity > open.Marker.Severity ? severity : open.Marker.Severity,
                Label = LabelWithFrameTime(label, open.Marker.MarkedAt, timestamp),
            };

            open.Marker = escalated;
            return IncidentEscalation.Escalated;
        }
    }

    /// <summary>
    /// How far the escalating frame may be from the marker before the label has to say when it happened.
    /// </summary>
    private static readonly TimeSpan LabelTimeWorthStating = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Appends the frame's own time to the label when it is far enough from the marker to mislead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker time and the window bounds have to stay where they were — moving them would slide the
    /// window out from under the events already collected in it — so an escalated label can name a frame
    /// most of a minute away from the timestamp printed beside it. That is tolerable on its own. What is
    /// not is that a deep capture is triggered by the frame that <em>opened</em> the window, so the
    /// attached trace can cover a different second entirely from the one the heading names.
    /// </para>
    /// <para>
    /// The 29 August session is the case. Its largest frame, 1 049 ms, escalated an incident that had
    /// opened 41 seconds earlier; the report went out headed "Auto: 1049 ms frame" beside a marker time
    /// of 22:50:35 and a trace covering 22:50:11 to 22:50:39 — while the frame it names happened at
    /// 22:51:16 and had no trace at all, its own capture having been refused by the budget. Reading that
    /// trace against that heading costs an hour and yields a conclusion about the wrong second.
    /// </para>
    /// </remarks>
    private static string LabelWithFrameTime(string label, DateTimeOffset markedAt, DateTimeOffset frameAt)
    {
        return (frameAt - markedAt).Duration() < LabelTimeWorthStating
            ? label
            : $"{label} kl. {frameAt.ToLocalTime():HH:mm:ss}";
    }

    public IReadOnlyList<IncidentRecord> OnTelemetry(TelemetryEvent telemetryEvent, EnvironmentMetadata environment, IReadOnlyList<ArtifactAttachment> attachments)
    {
        // Called once per telemetry event, which at PresentMon frame rates is hundreds of times a
        // second. With nothing pending there is no work to do, so skip the lock and the allocation.
        if (Volatile.Read(ref _pendingCount) == 0)
        {
            return [];
        }

        lock (_sync)
        {
            foreach (var incident in _pending.Values)
            {
                if (telemetryEvent.Timestamp >= incident.WindowStart && telemetryEvent.Timestamp <= incident.WindowEnd)
                {
                    incident.Events.Add(telemetryEvent);
                }
            }

            return FinalizeDueLocked(telemetryEvent.Timestamp, environment, attachments);
        }
    }

    public IReadOnlyList<IncidentRecord> FinalizeDue(DateTimeOffset now, EnvironmentMetadata environment, IReadOnlyList<ArtifactAttachment> attachments)
    {
        lock (_sync)
        {
            return FinalizeDueLocked(now, environment, attachments);
        }
    }

    private IReadOnlyList<IncidentRecord> FinalizeDueLocked(DateTimeOffset now, EnvironmentMetadata environment, IReadOnlyList<ArtifactAttachment> attachments)
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var completed = new List<IncidentRecord>();
        var due = _pending.Values.Where(item => item.WindowEnd <= now).ToArray();

        foreach (var pending in due)
        {
            _pending.Remove(pending.Marker.Id);
            Volatile.Write(ref _pendingCount, _pending.Count);
            var relatedAttachments = attachments
                .Where(item => item.ImportedAt >= pending.WindowStart && item.ImportedAt <= pending.WindowEnd)
                .OrderBy(item => item.ImportedAt)
                .ToArray();

            completed.Add(new IncidentRecord(
                pending.Marker.Id,
                pending.Marker,
                pending.WindowStart,
                pending.WindowEnd,
                environment,
                pending.Events.OrderBy(item => item.Timestamp).ToArray(),
                Analysis: null,
                Attachments: relatedAttachments));
        }

        return completed;
    }

    private sealed class PendingIncident
    {
        public PendingIncident(IncidentMarker marker, DateTimeOffset windowStart, DateTimeOffset windowEnd, IReadOnlyList<TelemetryEvent> initialEvents)
        {
            Marker = marker;
            WindowStart = windowStart;
            WindowEnd = windowEnd;
            Events = new List<TelemetryEvent>(initialEvents);
        }

        public IncidentMarker Marker { get; set; }

        /// <summary>
        /// Worst frame this incident has been told about, so escalation is monotonic and a single long
        /// freeze rewrites the marker once rather than on every frame it spans.
        /// </summary>
        public double WorstFrameTimeMs { get; set; }

        /// <summary>Whether a frame time may rename this incident at all. See MarkIncident.</summary>
        public bool AllowFrameEscalation { get; set; } = true;

        public DateTimeOffset WindowStart { get; }

        public DateTimeOffset WindowEnd { get; }

        public List<TelemetryEvent> Events { get; }
    }
}