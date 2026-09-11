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

    private readonly Dictionary<string, DateTimeOffset> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reachedAnIncident = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Notes that a capture finished writing and is on disk.</summary>
    /// <param name="writtenAt">
    /// When it landed. Kept so the reconciliation can be run before the session ends: a capture written
    /// seconds ago has not been parsed yet and would be reported as orphaned by every interim line until
    /// it was.
    /// </param>
    public void RecordWritten(string path, DateTimeOffset writtenAt)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_sync)
        {
            _written.TryAdd(path, writtenAt);
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
    /// The reconciliation over every capture, for the end of the session.
    /// </summary>
    public DeepCaptureLedgerReport? Summary() => Summary(DateTimeOffset.MaxValue, TimeSpan.Zero);

    /// <summary>
    /// The reconciliation over the captures that have had time to be parsed, or null when there are none.
    /// </summary>
    /// <remarks>
    /// The interim form exists because of how the evening of 10 September ended: the machine went down
    /// mid-write, the session was never stopped, and the reconciliation — which only ran at the end —
    /// was never written at all. A line that is only produced by an orderly shutdown is missing from
    /// exactly the sessions worth reconstructing. Parsing takes five to seven seconds in practice, so
    /// anything outside the settling period has been given its chance.
    /// </remarks>
    /// <param name="now">The moment the summary is being written.</param>
    /// <param name="settlingPeriod">How recently written a capture may be and still not count as missing.</param>
    public DeepCaptureLedgerReport? Summary(DateTimeOffset now, TimeSpan settlingPeriod)
    {
        string[] orphaned;
        int settled;
        int pending;
        lock (_sync)
        {
            if (_written.Count == 0)
            {
                return null;
            }

            var ripe = _written.Where(entry => now - entry.Value >= settlingPeriod).Select(entry => entry.Key).ToArray();
            settled = ripe.Length;
            pending = _written.Count - settled;
            orphaned = [.. ripe.Where(path => !_reachedAnIncident.Contains(path))];
        }

        return settled == 0
            ? null
            : new DeepCaptureLedgerReport(
                settled,
                orphaned.Select(Path.GetFileName).OfType<string>().ToArray(),
                pending);
    }
}

/// <param name="OrphanedCaptures">
/// File names of the captures that were written but whose evidence never reached an incident, so the
/// difference can be checked against the directory without opening anything.
/// </param>
/// <param name="CapturesPending">
/// Captures written too recently to have been parsed yet, and therefore left out of the count above.
/// Always zero at the end of a session.
/// </param>
public sealed record DeepCaptureLedgerReport(
    int CapturesWritten,
    IReadOnlyList<string> OrphanedCaptures,
    int CapturesPending = 0)
{
    public bool HasOrphans => OrphanedCaptures.Count > 0;

    public string Message =>
        (HasOrphans
            ? $"Avstämning av deep captures: {CapturesWritten} skrevs till disk, "
                + $"{CapturesWritten - OrphanedCaptures.Count} kopplades till en incident. "
                + $"{OrphanedCaptures.Count} gjorde det inte: {string.Join(", ", OrphanedCaptures)}. "
                + "Filen finns kvar och innehåller ett fönster ingen dom har läst — importera den för hand, "
                + "och räkna inte incidenten den togs för som en utan spår."
            : $"Avstämning av deep captures: {CapturesWritten} skrevs till disk och lika många kopplades "
                + "till en incident. Ingen trace ligger oanalyserad.")
        + Pending;

    private string Pending => CapturesPending switch
    {
        0 => string.Empty,
        1 => " En capture till skrevs nyss och har ännu inte hunnit analyseras; den räknas i nästa avstämning.",
        _ => $" Ytterligare {CapturesPending} captures skrevs nyss och har ännu inte hunnit analyseras; de "
            + "räknas i nästa avstämning.",
    };
}
