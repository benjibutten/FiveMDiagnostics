namespace FiveMDiagnostics.Core;

public sealed class IncidentMaterializer
{
    private readonly object _sync = new();
    private readonly TimeWindowRingBuffer<TelemetryEvent> _ringBuffer;
    private readonly TimeSpan _preWindow;
    private readonly TimeSpan _postWindow;
    private readonly Dictionary<Guid, PendingIncident> _pending = new();

    public IncidentMaterializer(TimeWindowRingBuffer<TelemetryEvent> ringBuffer, TimeSpan preWindow, TimeSpan postWindow)
    {
        _ringBuffer = ringBuffer;
        _preWindow = preWindow;
        _postWindow = postWindow;
    }

    public IncidentMarker MarkIncident(DateTimeOffset timestamp, IncidentSeverity severity, string? label = null)
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
            _ringBuffer.Snapshot(timestamp - _preWindow, timestamp));

        lock (_sync)
        {
            _pending[marker.Id] = pending;
        }

        return marker;
    }

    public IReadOnlyList<IncidentRecord> OnTelemetry(TelemetryEvent telemetryEvent, EnvironmentMetadata environment, IReadOnlyList<ArtifactAttachment> attachments)
    {
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
        var completed = new List<IncidentRecord>();
        var due = _pending.Values.Where(item => item.WindowEnd <= now).ToArray();

        foreach (var pending in due)
        {
            _pending.Remove(pending.Marker.Id);
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

        public IncidentMarker Marker { get; }

        public DateTimeOffset WindowStart { get; }

        public DateTimeOffset WindowEnd { get; }

        public List<TelemetryEvent> Events { get; }
    }
}