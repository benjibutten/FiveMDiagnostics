namespace FiveMDiagnostics.Core;

/// <summary>
/// Decides whether an automatically detected hitch may spend one of the session's deep captures.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the previous rule — automatic incidents never capture — cost a whole session.
/// Eighteen severe incidents were raised over five and a half hours and not one produced a trace, so
/// the five hitch clusters that were the entire remaining problem could not be looked inside. The only
/// ETL of the evening came from the single marker a human pressed, and the stall it was meant to catch
/// had ended 0.4 seconds before the retained window began.
/// </para>
/// <para>
/// The opposite extreme is worse. Capturing every automatic incident would have written 117 ETLs that
/// night, each a few hundred megabytes, each leaving the ring buffer empty for the seconds afterwards —
/// which is precisely the window the next hitch in a burst would land in. So the gates are three, and
/// all three have to pass: the frame has to be far beyond an ordinary spike, captures have to be spaced
/// out so a burst spends one rather than twenty, and the session has a hard ceiling.
/// </para>
/// <para>
/// The spacing gate has one way past it, added after it refused the wrong events. A frame beyond
/// <see cref="DeepCaptureOptions.AutoCaptureOverrideFrameTimeMs"/> spends budget the ordinary cooldown
/// would have withheld — the ceiling was always meant to be the binding constraint, and a session that
/// turned away its third and fourth largest frames while holding an unspent capture had the two the
/// wrong way round. What the override cannot skip is the ring buffer refilling, so it answers to a
/// shorter spacing of its own rather than to none.
/// </para>
/// <para>
/// Sized against real data. That session held 965 frames over 33 ms, 120 over 100 ms and 16 over
/// 300 ms. At the default threshold the ceiling of six is the binding constraint rather than the
/// threshold, which is the intended shape: the rare frames all qualify, and the budget decides how many
/// of them are worth the disk.
/// </para>
/// <para>
/// Reservations all come from the telemetry pump, which is single-reader, so the counters need no
/// synchronisation. <see cref="Remaining"/> is also read from the UI thread, where a stale value is
/// harmless — it is a status line, not a decision.
/// </para>
/// </remarks>
public sealed class AutoDeepCaptureBudget
{
    private readonly DeepCaptureOptions _options;
    private int _spent;
    private DateTimeOffset? _lastCaptureAt;

    public AutoDeepCaptureBudget(DeepCaptureOptions options)
    {
        _options = options;
    }

    /// <summary>Captures the detector has spent so far this session.</summary>
    public int Spent => _spent;

    /// <summary>Captures still available, for the UI to show rather than leaving the user to guess.</summary>
    public int Remaining => Math.Max(0, _options.MaxAutoCapturesPerSession - _spent);

    /// <summary>
    /// Reserves a capture for a single catastrophic frame, returning false when any gate refuses.
    /// </summary>
    /// <remarks>
    /// Reserving is the act of spending: the caller starts the capture immediately afterwards, and a
    /// capture that then fails is still charged, because the ring buffer was disturbed either way.
    /// </remarks>
    public bool TryReserve(DateTimeOffset timestamp, double frameTimeMs, out string? refusal)
    {
        if (frameTimeMs < _options.AutoCaptureFrameTimeMs)
        {
            // Not a refusal worth reporting: the overwhelming majority of incidents land here, and
            // saying so every time would bury the two cases the user does need to know about.
            refusal = null;
            return false;
        }

        return TryReserveCore(
            timestamp,
            $"en {frameTimeMs:F0} ms hitch",
            mayOverrideCooldown: _options.OverridesCooldownFor(frameTimeMs),
            out refusal);
    }

    /// <summary>
    /// Reserves a capture for a frame rate that has stopped recovering, rather than for one bad frame.
    /// </summary>
    /// <remarks>
    /// The frame time threshold cannot see this case at all, and it is the case that mattered most. A
    /// session spent 104 of 391 minutes below 50 fps in unbroken half-hour blocks, and because no single
    /// frame in them was remarkable, nothing in the app ever asked for a trace of one. Sustained
    /// saturation is raised rarely by <see cref="FramePacingMonitor"/> — once on entering a bad patch and
    /// then on a reminder cadence — so letting it reach the budget adds very few captures and covers the
    /// only condition a per-frame rule structurally cannot.
    /// </remarks>
    public bool TryReserveForSustainedSaturation(DateTimeOffset timestamp, out string? refusal)
    {
        // No frame to be far beyond the threshold, so no override: saturation is by definition a stretch
        // of unremarkable frames, and one stretch is one trace however long it lasts.
        return TryReserveCore(
            timestamp,
            "en period där bildfrekvensen inte återhämtade sig",
            mayOverrideCooldown: false,
            out refusal);
    }

    /// <param name="mayOverrideCooldown">
    /// Whether the frame is catastrophic enough to spend budget the ordinary cooldown would have
    /// withheld. The shorter <see cref="DeepCaptureOptions.AutoCaptureOverrideCooldown"/> still applies:
    /// it is what keeps the override from capturing a ring buffer that has not refilled yet.
    /// </param>
    private bool TryReserveCore(DateTimeOffset timestamp, string description, bool mayOverrideCooldown, out string? refusal)
    {
        if (!_options.Enabled || !_options.CaptureAutoIncidents)
        {
            refusal = null;
            return false;
        }

        if (_spent >= _options.MaxAutoCapturesPerSession)
        {
            refusal = $"Deep capture hoppades över för {description}: sessionens budget på "
                + $"{_options.MaxAutoCapturesPerSession} automatiska captures är förbrukad. Höj "
                + $"DeepCapture.MaxAutoCapturesPerSession om fler behövs.";
            return false;
        }

        if (_lastCaptureAt is { } last)
        {
            var elapsed = timestamp - last;
            var cooldown = mayOverrideCooldown ? _options.AutoCaptureOverrideCooldown : _options.AutoCaptureCooldown;

            if (elapsed < cooldown)
            {
                var remaining = cooldown - elapsed;
                refusal = mayOverrideCooldown
                    ? $"Deep capture hoppades över för {description}: {remaining.TotalSeconds:F0} s kvar innan "
                        + "ringbufferten hunnit fyllas på efter föregående capture. Frametimen räckte för att "
                        + "bryta cooldownen, men inte för att kringgå påfyllningen."
                    : $"Deep capture hoppades över för {description}: {remaining.TotalMinutes:F0} min "
                        + "kvar av cooldown efter föregående capture, och ringbufferten har ännu inte fyllts på.";
                return false;
            }
        }

        _spent++;
        _lastCaptureAt = timestamp;
        refusal = null;
        return true;
    }
}
