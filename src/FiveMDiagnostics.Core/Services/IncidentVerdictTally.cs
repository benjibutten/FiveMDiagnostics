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
    private readonly Dictionary<Guid, InsufficientEvidenceReason> _evidenceGaps = [];
    private readonly Dictionary<Guid, IReadOnlyList<string>> _suspects = [];

    /// <summary>Records, or replaces, the top-ranked category for one incident.</summary>
    /// <param name="evidenceGap">
    /// Why the category is <see cref="RootCauseCategory.InsufficientEvidence"/>, when it is. Ignored
    /// otherwise, and cleared on a re-analysis that no longer classifies the incident that way.
    /// </param>
    /// <param name="suspects">
    /// The processes this incident named as suspected neighbours. Kept so the session can say when one
    /// name is on nearly every incident, which is either the session's finding or a broken rule and is
    /// invisible either way from inside a single incident.
    /// </param>
    public void Record(
        Guid markerId,
        RootCauseCategory? category,
        InsufficientEvidenceReason? evidenceGap = null,
        IReadOnlyList<string>? suspects = null)
    {
        if (category is not { } verdict)
        {
            return;
        }

        lock (_sync)
        {
            _verdicts[markerId] = verdict;
            _suspects[markerId] = suspects ?? [];

            if (verdict == RootCauseCategory.InsufficientEvidence && evidenceGap is { } gap)
            {
                _evidenceGaps[markerId] = gap;
            }
            else
            {
                _evidenceGaps.Remove(markerId);
            }
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

            var byGap = _evidenceGaps.Values
                .GroupBy(reason => reason)
                .Select(group => new EvidenceGapCount(group.Key, group.Count()))
                .ToArray();

            // Counted over the incidents that named anything at all, so a session where most incidents
            // have no neighbour to name cannot make a rare one look universal.
            var withSuspects = _suspects.Values.Where(names => names.Count > 0).ToArray();
            var ubiquitous = withSuspects
                .SelectMany(names => names.Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SuspectFrequency(group.Key, group.Count(), withSuspects.Length))
                .OrderByDescending(item => item.Incidents)
                .FirstOrDefault(item => item.IsUbiquitous);

            return new IncidentVerdictReport(_verdicts.Count, byCategory, byGap, ubiquitous);
        }
    }
}

/// <summary>How often one category came out on top.</summary>
public sealed record IncidentVerdictCount(RootCauseCategory Category, int Count);

/// <summary>How often one reason stood behind an <see cref="RootCauseCategory.InsufficientEvidence"/> verdict.</summary>
public sealed record EvidenceGapCount(InsufficientEvidenceReason Reason, int Count);

/// <summary>What the engine concluded across a whole session.</summary>
/// <summary>
/// One process named as a suspect on nearly every incident that named anything.
/// </summary>
/// <remarks>
/// On 9 September <c>FiveM_ChromeBrowser</c> was a suspected neighbour in 129 of 149 incidents, and in
/// all 98 after midnight. Both readings were in the log, one incident at a time, where a name in a list
/// of suspects looks like ordinary background noise. A name on every single incident is not noise: it is
/// either the evening's finding or a rule that names whoever happens to be busiest, and both are worth
/// the reader's attention before any individual verdict is.
/// </remarks>
public sealed record SuspectFrequency(string ProcessName, int Incidents, int IncidentsWithSuspects)
{
    /// <summary>
    /// Share of incidents a name has to reach before it is reported as universal rather than frequent.
    /// </summary>
    /// <remarks>
    /// Nine tenths, and it has been passed once: the sessions before this ran at 46% and 51% for the
    /// same process, which is a busy neighbour and reads as one.
    /// </remarks>
    private const double UbiquitousShare = 0.9;

    /// <summary>Incidents below which a share says nothing. A name on two of two is not a pattern.</summary>
    private const int MinimumIncidents = 20;

    public double Share => IncidentsWithSuspects == 0 ? 0 : (double)Incidents / IncidentsWithSuspects;

    public bool IsUbiquitous => IncidentsWithSuspects >= MinimumIncidents && Share >= UbiquitousShare;

    public string Message =>
        $"{ProcessName} pekas ut som misstänkt sidoprocess i {Incidents} av {IncidentsWithSuspects} "
        + $"incidenter ({Share:P0}). En process som namnges i nästan varenda incident är antingen "
        + "sessionens fynd eller en regel som namnger den som råkar vara mest upptagen — läs väntkedjorna "
        + "i spåren innan den tas för en orsak, för de säger vem som faktiskt höll tråden.";
}

public sealed record IncidentVerdictReport(
    int Incidents,
    IReadOnlyList<IncidentVerdictCount> ByCategory,
    IReadOnlyList<EvidenceGapCount> ByEvidenceGap,
    SuspectFrequency? UbiquitousSuspect = null)
{
    /// <summary>Incidents where the card's memory was the top-ranked explanation.</summary>
    /// <remarks>
    /// Both verdicts about the card's memory, because they are the same finding at two strengths and
    /// they call for the same thing: take memory off the card. Counting only the occupancy verdict left
    /// this line silent on exactly the sessions where the sharper one won — a
    /// <see cref="RootCauseCategory.GpuResidencyStall"/> outranks
    /// <see cref="RootCauseCategory.GpuVramPressure"/> in every incident it fires on, so an evening
    /// whose mechanism was measured three times over would report less VRAM pressure than one where it
    /// was only correlated.
    /// </remarks>
    public int VramPressureIncidents => ByCategory
        .Where(item => item.Category is RootCauseCategory.GpuVramPressure or RootCauseCategory.GpuResidencyStall)
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

    /// <summary>Incidents the engine ruled out because the game was behind another window.</summary>
    public int NotInFocusIncidents => ByCategory
        .Where(item => item.Category == RootCauseCategory.GameNotInFocus)
        .Sum(item => item.Count);

    /// <summary>Incidents that happened while somebody was actually looking at the game.</summary>
    public int IncidentsInPlay => Incidents - NotInFocusIncidents;

    /// <summary>
    /// The line that says how much of the evening's incident list was an alt-tab, or null when none of it
    /// was.
    /// </summary>
    /// <remarks>
    /// Its own line for the same reason the VRAM one has its own: it changes how every other figure in
    /// the summary is read. A count of two hundred incidents of which sixty happened with the game in the
    /// background is not a count of two hundred, and previous sessions had no way to say so — they
    /// reported the whole list as stutter and left the comparison between evenings resting on how often
    /// the player happened to tab out.
    /// </remarks>
    public string? NotInFocusMessage => NotInFocusIncidents == 0
        ? null
        : $"Ur fokus: {NotInFocusIncidents} av {Incidents} incidenter "
            + $"({(double)NotInFocusIncidents / Incidents:P0}) inföll medan spelet låg bakom ett annat "
            + $"fönster — alt-tab eller Windows-tangenten. De räknas inte som spellagg. Kvar i spelet: "
            + $"{IncidentsInPlay} incidenter.";

    /// <summary>Incidents the engine could not classify at all.</summary>
    public int InsufficientEvidenceIncidents => ByCategory
        .Where(item => item.Category == RootCauseCategory.InsufficientEvidence)
        .Sum(item => item.Count);

    /// <summary>
    /// The line that splits an "insufficient evidence" count by why, or null when the session had none.
    /// </summary>
    /// <remarks>
    /// On 8 September this verdict was 38% of a session's incidents and appeared in the ranking below as
    /// one entry among many, with no way to tell a session that is short on deep captures from one whose
    /// windows were simply too quiet to classify. The two call for different responses — more budget, or
    /// none at all — so the count is worth nothing until it is split.
    /// </remarks>
    public string? InsufficientEvidenceMessage
    {
        get
        {
            if (InsufficientEvidenceIncidents == 0)
            {
                return null;
            }

            var byReason = ByEvidenceGap.ToDictionary(item => item.Reason, item => item.Count);
            var parts = new List<string>();

            if (byReason.GetValueOrDefault(InsufficientEvidenceReason.NoFrameData) is > 0 and var noFrames)
            {
                parts.Add($"{noFrames} hade ingen framedata alls");
            }

            if (byReason.GetValueOrDefault(InsufficientEvidenceReason.NoTrace) is > 0 and var noTrace)
            {
                parts.Add($"{noTrace} hade ingen trace");
            }

            if (byReason.GetValueOrDefault(InsufficientEvidenceReason.TooFewSpikes) is > 0 and var tooFewSpikes)
            {
                parts.Add($"{tooFewSpikes} hade för få spikes i fönstret");
            }

            if (byReason.GetValueOrDefault(InsufficientEvidenceReason.Inconclusive) is > 0 and var inconclusive)
            {
                parts.Add($"{inconclusive} hade både trace och spikes men gick ändå inte att klassificera");
            }

            var breakdown = parts.Count > 0 ? $": {string.Join(", ", parts)}" : string.Empty;
            return $"Insufficient evidence: {InsufficientEvidenceIncidents} av {Incidents} incidenter "
                + $"({(double)InsufficientEvidenceIncidents / Incidents:P0}) saknade underlag för en dom{breakdown}.";
        }
    }

    /// <summary>The whole ranking, largest first, for the reader who wants the rest of it.</summary>
    public string Message
    {
        get
        {
            var parts = ByCategory.Select(item => $"{item.Category} {item.Count}");
            var inPlay = NotInFocusIncidents > 0 ? $" Varav {IncidentsInPlay} med spelet i förgrunden." : string.Empty;
            return $"Motorns rangordning över {Incidents} incidenter: {string.Join(", ", parts)}.{inPlay}";
        }
    }
}
