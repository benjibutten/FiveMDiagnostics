namespace FiveMDiagnostics.Core;

/// <summary>
/// Counts what the correlation engine actually concluded across a session, so its ranking is readable
/// without opening the journal.
/// </summary>
/// <remarks>
/// <para>
/// The engine ranked <see cref="RootCauseCategory.GpuVramPressure"/> highest in 26 of the 119 incidents
/// of 30 August, which is the first time its ranking has led to the right answer on its own — the card
/// was the evening's problem, and the engine had said so before anyone looked. Nobody saw it, because
/// a verdict lives inside one incident and the only way to see the distribution was to count lines in
/// the jsonl afterwards.
/// </para>
/// <para>
/// Keyed by marker rather than counted on publication. An incident is re-analysed whenever evidence is
/// attached to it — an imported artifact, or the ETL an automatic capture wrote — and its verdict
/// usually changes when that happens, which is the entire point of re-analysing. Counting each
/// publication would score the same incident twice and weight it by how much evidence it collected.
/// </para>
/// </remarks>
public sealed class IncidentVerdictTally
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, RootCauseCategory> _verdicts = [];

    /// <summary>Records, or replaces, the top-ranked category for one incident.</summary>
    public void Record(Guid markerId, RootCauseCategory? category)
    {
        if (category is not { } verdict)
        {
            return;
        }

        lock (_sync)
        {
            _verdicts[markerId] = verdict;
        }
    }

    /// <summary>The distribution, or null when the session classified nothing.</summary>
    public IncidentVerdictReport? Summary()
    {
        lock (_sync)
        {
            if (_verdicts.Count == 0)
            {
                return null;
            }

            var byCategory = _verdicts.Values
                .GroupBy(category => category)
                .Select(group => new IncidentVerdictCount(group.Key, group.Count()))
                .OrderByDescending(item => item.Count)
                .ToArray();

            return new IncidentVerdictReport(_verdicts.Count, byCategory);
        }
    }
}

/// <summary>How often one category came out on top.</summary>
public sealed record IncidentVerdictCount(RootCauseCategory Category, int Count);

/// <summary>What the engine concluded across a whole session.</summary>
public sealed record IncidentVerdictReport(int Incidents, IReadOnlyList<IncidentVerdictCount> ByCategory)
{
    /// <summary>Incidents where the card's memory was the top-ranked explanation.</summary>
    public int VramPressureIncidents => ByCategory
        .Where(item => item.Category == RootCauseCategory.GpuVramPressure)
        .Sum(item => item.Count);

    /// <summary>
    /// The line that lifts the VRAM verdict out of the jsonl, or null when the engine never reached it.
    /// </summary>
    /// <remarks>
    /// Its own line rather than a share inside the distribution below, because it is the one verdict in
    /// this list that names something the user can act on before the next session: the card is full, and
    /// the texture setting is what fills it.
    /// </remarks>
    public string? VramPressureMessage => VramPressureIncidents == 0
        ? null
        : $"GPU VRAM-tryck: motorn rankade kortets minne högst i {VramPressureIncidents} av {Incidents} "
            + $"incidenter ({(double)VramPressureIncidents / Incidents:P0}). Det är sessionens tydligaste "
            + "enskilda dom och den pekar på texturinställningen, inte på en process som växer.";

    /// <summary>The whole ranking, largest first, for the reader who wants the rest of it.</summary>
    public string Message
    {
        get
        {
            var parts = ByCategory.Select(item => $"{item.Category} {item.Count}");
            return $"Motorns rangordning över {Incidents} incidenter: {string.Join(", ", parts)}.";
        }
    }
}
