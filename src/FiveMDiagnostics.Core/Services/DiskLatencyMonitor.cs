namespace FiveMDiagnostics.Core;

/// <summary>
/// Keeps, per volume, how often it was the session's slowest disk and how slowly it answered.
/// </summary>
/// <remarks>
/// <para>
/// Every incident already carries a line naming the slowest disk in its window, and that line is
/// correct, complete and unreadable in bulk. On 10 September <c>D:</c> answered in 815.9 ms with an
/// empty queue, once, in the last incident of the evening — and the machine stopped writing data
/// sixty-four seconds later. The line was written exactly as designed. It sat among fifty-two
/// identically shaped lines saying <c>F:</c> answered in 13 ms, and three separate reviews of three
/// separate evenings walked past it: the same volume had produced 686.6 ms on 8 September and 223.6 ms
/// on 9 September, each in its own incident, each unremarked.
/// </para>
/// <para>
/// So the summary is per volume rather than per reading. A volume that is slowest in every reading at
/// 13 ms is a slow disk doing its job; a volume that is slowest in one reading out of fifty-three, at
/// fifty times any other volume's worst, is something else — and the difference is invisible until the
/// two are written on the same line.
/// </para>
/// <para>
/// The tally counts polling samples, not incidents: <see cref="Observe"/> is called once per system
/// poll, which is every 750 ms, so an evening produces thousands of readings and a volume's count says
/// how much of the session it was the slowest disk — not how many times something happened to it. The
/// count was called "windows" and read as one incident apiece, which made "slowest in 4 812 windows"
/// look like an alarm when it is a quiet disk answering in 13 ms for an hour.
/// </para>
/// <para>
/// The warning it raises is deliberately careful about what it claims. An idle mechanical disk parks its
/// heads and spins down, and the first access afterwards costs several hundred milliseconds of spin-up
/// with an empty queue and no process reading — which is precisely the shape of the <c>D:</c> readings,
/// on a disk the game had been moved off. Saying "this disk is failing" would have been the wrong call
/// on the evening this was written for. Saying "this volume answered far slower than it usually does,
/// after lying unused, and that may be spin-up" is the true statement, and it is still the one that gets
/// the reader to look.
/// </para>
/// </remarks>
public sealed class DiskLatencyMonitor
{
    /// <summary>
    /// How many times slower than its own median a volume's worst reading must be to be called out.
    /// </summary>
    /// <remarks>
    /// The evening that prompted this had a ratio of roughly sixty: 815.9 ms against a median of 13.8.
    /// A factor of ten is far below that and still far above anything a working disk produces across a
    /// session — <c>F:</c>'s own worst was 15.8 ms against a median of 13.8, a ratio of 1.1.
    /// </remarks>
    private const double OutlierRatio = 10;

    /// <summary>
    /// The floor under the outlier rule, so a fast volume's ordinary jitter cannot trip it.
    /// </summary>
    /// <remarks>
    /// <c>C:</c> answers in 0.04 ms and occasionally in 6; that is a ratio of 150 and means nothing at
    /// all. The rule is about a disk that stopped answering, and below a tenth of a second nothing did.
    /// </remarks>
    private const double OutlierFloorMs = 100;

    /// <summary>Readings below this are not worth keeping as a sample's worst.</summary>
    private const double IgnoreBelowMs = 0.01;

    private readonly object _sync = new();
    private readonly Dictionary<string, List<double>> _byVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folds in one polling sample's slowest volume and what it answered in.</summary>
    public void Observe(string? volume, double? latencyMs)
    {
        if (string.IsNullOrWhiteSpace(volume) || latencyMs is not { } latency || latency < IgnoreBelowMs)
        {
            return;
        }

        lock (_sync)
        {
            if (!_byVolume.TryGetValue(volume, out var readings))
            {
                readings = [];
                _byVolume[volume] = readings;
            }

            readings.Add(latency);
        }
    }

    /// <summary>The per-volume tally, or null when nothing measured a disk yet.</summary>
    public DiskLatencyReport? Summary()
    {
        lock (_sync)
        {
            if (_byVolume.Count == 0)
            {
                return null;
            }

            var volumes = _byVolume
                .Select(entry =>
                {
                    var sorted = entry.Value.OrderBy(value => value).ToArray();
                    return new DiskVolumeLatency(
                        entry.Key,
                        sorted.Length,
                        sorted[sorted.Length / 2],
                        sorted[^1]);
                })
                .OrderByDescending(volume => volume.WorstMs)
                .ToArray();

            return new DiskLatencyReport(volumes, volumes.Where(IsOutlier).ToArray());
        }
    }

    /// <summary>
    /// Whether a volume answered far slower than it usually does, slowly enough to matter.
    /// </summary>
    /// <remarks>
    /// A volume read only once has no history to be measured against, so its worst is compared with the
    /// floor alone — which is the case that matters, since a disk that woke up once is exactly that.
    /// </remarks>
    private static bool IsOutlier(DiskVolumeLatency volume) =>
        volume.WorstMs >= OutlierFloorMs
        && (volume.Readings == 1 || volume.WorstMs >= volume.MedianMs * OutlierRatio);
}

/// <param name="Readings">
/// How many polling samples this volume was the slowest disk in. Samples, not incidents: the system
/// poll runs every 750 ms, so this is roughly how much of the session the volume spent as the slowest
/// disk.
/// </param>
public sealed record DiskVolumeLatency(string Volume, int Readings, double MedianMs, double WorstMs);

/// <param name="Outliers">Volumes whose worst reading stands far outside their own behaviour.</param>
public sealed record DiskLatencyReport(
    IReadOnlyList<DiskVolumeLatency> Volumes,
    IReadOnlyList<DiskVolumeLatency> Outliers)
{
    public bool HasOutlier => Outliers.Count > 0;

    public string Message
    {
        get
        {
            var tally = string.Join(
                "; ",
                Volumes.Select(volume =>
                    $"{volume.Volume} var långsammast i {volume.Readings} mätpunkter "
                    + $"(median {volume.MedianMs:F1} ms, värsta {volume.WorstMs:F1})"));

            var warning = HasOutlier
                ? " " + string.Join(
                    " ",
                    Outliers.Select(volume =>
                        $"{volume.Volume} svarade på {volume.WorstMs:F0} ms, vilket är långt utanför vad "
                        + "den annars gör. En volym som legat oanvänd kan kosta så mycket på första "
                        + "åtkomsten medan en mekanisk disk varvar upp — det är den vanligaste "
                        + "förklaringen och den är ofarlig. Men det är också vad en enhet som håller på "
                        + "att tappa kontakten ser ut som, så en blick i Windows händelselogg kring den "
                        + "tidpunkten avgör vilket."))
                : string.Empty;

            return $"Disklatens per volym: {tally}.{warning}";
        }
    }
}
