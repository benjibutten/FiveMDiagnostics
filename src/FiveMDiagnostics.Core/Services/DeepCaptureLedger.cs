namespace FiveMDiagnostics.Core;

/// <summary>
/// Reconciles the captures written to disk against the captures whose evidence reached an incident.
/// </summary>
/// <remarks>
/// <para>
/// On 8 September a 903 MB trace covering the evening's second worst frame was written, spent a slot of
/// the capture budget, and never reached the incident it was taken for. That incident was ranked
/// "External process interference" and counted among the ones that "had no trace", while the file sat on
/// disk holding the wait chain that explained it. Nothing in the session log said a capture had gone
/// missing; the gap was found by listing the directory a day later.
/// </para>
/// <para>
/// The check is a difference of two lists and cannot repair anything — the point is that the difference
/// is stated. A session where every capture landed says so in one line, which is what makes the session
/// where one did not readable at a glance rather than after a directory listing.
/// </para>
/// </remarks>
public sealed class DeepCaptureLedger
{
    /// <summary>
    /// Guards both sets. Captures are written on the task that took them and evidence is attached on the
    /// telemetry pump, so the two meet.
    /// </summary>
    private readonly object _sync = new();

    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reachedAnIncident = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Notes that a capture finished writing and is on disk.</summary>
    public void RecordWritten(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_sync)
        {
            _written.Add(path);
        }
    }

    /// <summary>Notes that a capture's evidence was attached to an incident.</summary>
    /// <remarks>
    /// Called for every trace that lands on an incident, including one imported by hand. A path that was
    /// never written by this session is simply not in the other set and cannot produce a difference.
    /// </remarks>
    public void RecordReachedAnIncident(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_sync)
        {
            _reachedAnIncident.Add(path);
        }
    }

    /// <summary>
    /// The reconciliation, or null when this session took no captures.
    /// </summary>
    public DeepCaptureLedgerReport? Summary()
    {
        string[] written;
        string[] orphaned;
        lock (_sync)
        {
            if (_written.Count == 0)
            {
                return null;
            }

            written = [.. _written];
            orphaned = [.. _written.Where(path => !_reachedAnIncident.Contains(path))];
        }

        return new DeepCaptureLedgerReport(written.Length, orphaned.Select(Path.GetFileName).OfType<string>().ToArray());
    }
}

/// <param name="OrphanedCaptures">
/// File names of the captures that were written but whose evidence never reached an incident, so the
/// difference can be checked against the directory without opening anything.
/// </param>
public sealed record DeepCaptureLedgerReport(int CapturesWritten, IReadOnlyList<string> OrphanedCaptures)
{
    public bool HasOrphans => OrphanedCaptures.Count > 0;

    public string Message =>
        HasOrphans
            ? $"Avstämning av deep captures: {CapturesWritten} skrevs till disk, "
                + $"{CapturesWritten - OrphanedCaptures.Count} kopplades till en incident. "
                + $"{OrphanedCaptures.Count} gjorde det inte: {string.Join(", ", OrphanedCaptures)}. "
                + "Filen finns kvar och innehåller ett fönster ingen dom har läst — importera den för hand, "
                + "och räkna inte incidenten den togs för som en utan spår."
            : $"Avstämning av deep captures: {CapturesWritten} skrevs till disk och lika många kopplades "
                + "till en incident. Ingen trace ligger oanalyserad.";
}
