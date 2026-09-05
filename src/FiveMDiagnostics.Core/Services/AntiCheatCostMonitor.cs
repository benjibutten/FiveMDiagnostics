namespace FiveMDiagnostics.Core;

/// <summary>
/// Sums what FiveM's anti-cheat cost across a whole session, so it is answered once instead of
/// rediscovered every time somebody reads a trace.
/// </summary>
/// <remarks>
/// <para>
/// The per-trace line already names it — "dess hetaste tråd (tid 9048) 0,83 kärnor – 54 % adhesive.dll
/// (FiveM:s anti-cheat)" — and that sentence has now been read as a new finding in three separate
/// reviews. It is not new and it is not a cause: measured across twelve traces of one evening the module
/// held between 0.37 and 0.85 cores with a median of 0.47, which is a fixed fee for running FiveM,
/// present in the quiet traces and the bad ones alike.
/// </para>
/// <para>
/// Stating the range and the median in the session summary is what retires the question. A figure that
/// varies by a factor of two across an evening and never correlates with a hitch has been measured
/// enough; a figure that suddenly reaches two cores has not, and only a session-level line makes the
/// difference visible without opening twelve traces by hand.
/// </para>
/// </remarks>
public sealed class AntiCheatCostMonitor
{
    /// <summary>FiveM's anti-tamper module, as the trace's image table names it.</summary>
    public const string Module = "adhesive.dll";

    /// <summary>
    /// How the per-trace CPU figure arrives: the busiest game thread's cores, keyed by module.
    /// </summary>
    private const string BusiestThreadModulePrefix = "cpuBusiestThreadCores_";

    private readonly object _sync = new();
    private readonly List<double> _cores = [];
    private readonly List<double> _fileOperationsPerSecond = [];

    private int _traces;

    /// <summary>
    /// Folds one trace's metrics in. Traces with no anti-cheat figure are counted but contribute nothing,
    /// which is itself worth knowing — it means the module was not on the busiest thread that time.
    /// </summary>
    public void Observe(IReadOnlyDictionary<string, double> metrics)
    {
        // The module can be measured two ways and both are the same number seen from different ends: as
        // a share of the busiest thread, and as the cores held by whichever thread makes the file
        // operations. Either is enough; taking the larger avoids reporting a range that is really an
        // artefact of which thread happened to be busiest in a given trace.
        var fromThread = metrics.GetValueOrDefault($"{BusiestThreadModulePrefix}{Module}");
        var fromScanner = metrics.GetValueOrDefault("antiCheatThreadCores");
        var cores = Math.Max(fromThread, fromScanner);

        lock (_sync)
        {
            _traces++;

            if (cores > 0)
            {
                _cores.Add(cores);
            }

            if (metrics.TryGetValue("antiCheatFileOperationsPerSecond", out var operations) && operations > 0)
            {
                _fileOperationsPerSecond.Add(operations);
            }
        }
    }

    /// <summary>The session's figure, or null when no trace ever showed the module.</summary>
    public AntiCheatCostReport? Summary()
    {
        lock (_sync)
        {
            if (_cores.Count == 0)
            {
                return null;
            }

            var sorted = _cores.Order().ToArray();

            return new AntiCheatCostReport(
                _traces,
                sorted.Length,
                sorted[0],
                sorted[^1],
                sorted[sorted.Length / 2],
                _fileOperationsPerSecond.Count == 0 ? null : _fileOperationsPerSecond.Order().ToArray()[_fileOperationsPerSecond.Count / 2]);
        }
    }
}

/// <summary>What FiveM's anti-cheat held across a session's traces.</summary>
/// <param name="Traces">Traces observed, including any that did not show the module at all.</param>
/// <param name="TracesWithModule">Traces the module was measured in.</param>
/// <param name="MedianFileOperationsPerSecond">
/// How often the scan thread touched the file system, or null when no trace measured it. Carried because
/// it is the other half of the same fixed fee — the anti-cheat reads other programs' executables in a
/// loop, and those operations have been counted as the game streaming resources.
/// </param>
public sealed record AntiCheatCostReport(
    int Traces,
    int TracesWithModule,
    double MinCores,
    double MaxCores,
    double MedianCores,
    double? MedianFileOperationsPerSecond)
{
    /// <summary>
    /// Cores above which the fee stops being a fee and becomes something to look at.
    /// </summary>
    /// <remarks>
    /// Twice the worst of the twelve traces this was calibrated against. Below it the module is doing
    /// what it does every evening; above it something has changed, and the point of stating the range is
    /// that the change would be visible.
    /// </remarks>
    public const double UnusualCores = 1.7;

    /// <summary>True when the module held more than any measured session has shown it holding.</summary>
    public bool IsUnusual => MaxCores >= UnusualCores;

    public string Message
    {
        get
        {
            var operations = MedianFileOperationsPerSecond is { } rate
                ? $" Dess tråd gjorde omkring {rate:N0} filoperationer i sekunden — den läser andra "
                    + "programs exe-filer i en loop, och de operationerna ska inte läsas som att spelet "
                    + "strömmar resurser."
                : string.Empty;

            var verdict = IsUnusual
                ? " Det är högre än någon tidigare mätt session och är värt att titta på."
                : " Det är en fast avgift för att köra FiveM och ingen orsak till hitcharna — den ligger "
                    + "lika högt i de lugna spåren som i de dåliga. Posten är därmed avförd.";

            return $"FiveM:s anti-cheat ({AntiCheatCostMonitor.Module}) höll {MinCores:F2}–{MaxCores:F2} "
                + $"kärnor i {TracesWithModule} av {Traces} traces, median {MedianCores:F2}.{operations}{verdict}";
        }
    }
}
