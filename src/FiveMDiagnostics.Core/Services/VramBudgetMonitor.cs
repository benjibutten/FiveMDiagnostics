namespace FiveMDiagnostics.Core;

/// <summary>
/// Turns the three VRAM figures the session already logs into the budget they add up to.
/// </summary>
/// <remarks>
/// <para>
/// This is the one number in six sessions of investigation that predicted an evening instead of
/// describing one. The card's capacity is fixed, the desktop and the stream stack take what they take
/// before the game starts, and FiveM then fills its texture memory to a ceiling set by the graphics
/// preset and stops there. Written out, that is a subtraction: 7.2 GB of game at High plus 1.1 GB of
/// desktop plus 1.0 GB of stream is 9.3 of 10 GB, and the session measured a median of 88.1% with 4.24%
/// of it above 93%. The same subtraction at Medium predicts 8.2 GB, and that session measured 77.6%.
/// Both within a few percentage points, both computable at the start rather than a week later.
/// </para>
/// <para>
/// What makes it worth a line of its own is that it names the lever. The game's ceiling is the graphics
/// preset and costs image quality to move; the other two gigabytes are programs, and the largest single
/// row of them is usually something nobody is using. "The stream stack holds 1.0 GB" is an actionable
/// sentence in a way that "VRAM peaked at 91%" is not.
/// </para>
/// <para>
/// Deliberately measured as the card's own figure minus the game's row rather than as a sum of the
/// other rows. The per-process table double counts — see <see cref="VramAccountingMonitor"/> — and a
/// budget built by adding its rows together would inherit that error whole. The subtraction cannot: it
/// rests on the adapter's total, which is the one figure nothing in the table can inflate.
/// </para>
/// <para>
/// Every figure is reconciled against the card before it is stated, and that is the fix for the worst
/// thing this class has done. On the evening of 30 August it wrote "the game has 8.1 GB left of the
/// card's 10.0" while the card itself stood at 88–92%, because excluding a double-counting row removed
/// it from the budget as well as from the top list: the stream stack was booked at 0.1 GB instead of
/// about 1.3, and the difference silently became headroom. An excluded row now leaves the budget with a
/// hole the card's own figure fills — see <see cref="Observe(GpuProcessMemorySample)"/> — and the free
/// space reported can never exceed total minus what the card says is used, whatever the table claims.
/// </para>
/// </remarks>
public sealed class VramBudgetMonitor
{
    /// <summary>
    /// Bytes a stream process has to hold before the stack counts as running.
    /// </summary>
    /// <remarks>
    /// OBS is present in the counter table from the moment it launches and holds a few tens of megabytes
    /// while it is idle. What the budget is about is the stack that is actually encoding, which arrives
    /// at hundreds of megabytes, so a threshold keeps a launch from being reported as a change and back
    /// again a poll later.
    /// </remarks>
    private const ulong StreamStackPresentBytes = 128UL * 1024 * 1024;

    /// <summary>
    /// Bytes below the table's cut that are worth mentioning. Under this the split is accurate to the
    /// tenth of a gigabyte the line is printed to.
    /// </summary>
    private const ulong MaterialHiddenBytes = 64UL * 1024 * 1024;

    /// <summary>
    /// Consecutive process samples the stream stack has to hold a state before a change is written.
    /// </summary>
    /// <remarks>
    /// The threshold above stops a launch flickering; it does nothing about a row that alternates
    /// between believable and excluded, which is what actually happened. Eighteen "the stream stack
    /// started/stopped" lines were written between 21:48 and 22:04 on an evening when OBS neither
    /// started nor stopped, and each of them restated the budget as though a gigabyte had moved. Three
    /// samples at the five second process cadence is fifteen seconds of agreement — far shorter than
    /// any real transition, and long enough that a single odd sample cannot produce a line.
    /// </remarks>
    private const int StableSamplesForTransition = 3;

    private static readonly TimeSpan AdapterFreshness = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Share of its own streaming budget the game has to reach before the approach line is written.
    /// </summary>
    /// <remarks>
    /// The game fills its budget and does not give it back — measured over five hours on 2 September,
    /// 5.2 GB to 7.0 GB against a streaming budget of 5.6 — so the interesting moment is not when it
    /// arrives but when it is clearly going to. Nine tenths is late enough that a session which never
    /// approaches its budget says nothing, and early enough to be hours before the worst frame.
    /// </remarks>
    private const double BudgetApproachShare = 0.90;

    /// <summary>
    /// How far past the budget the game has to be before the overhead is worth a line of its own.
    /// </summary>
    /// <remarks>
    /// The two collectors sample at different instants and the budget is computed from a config file,
    /// so a few tens of megabytes over is noise. A quarter of a gigabyte is not: it is a frame buffer
    /// and a browser, and it is the term that decides how far the slider actually has to come down.
    /// </remarks>
    private const ulong MaterialOverheadBytes = 256UL * 1024 * 1024;

    private GpuTelemetrySample? _lastAdapter;
    private FiveMClientConfig? _clientConfig;
    private bool _reported;
    private bool _budgetApproachReported;
    private bool _budgetOverheadReported;
    private bool _streamStackPresent;

    /// <summary>
    /// The largest amount the game has been seen holding beyond its streaming budget this session.
    /// </summary>
    /// <remarks>
    /// The whole correction of 3 September in one field. <c>vid_budgetScale</c> governs streaming and
    /// nothing else, so the game's video memory is the budget plus NUI, render targets and the frame
    /// buffers that come with 1440p and 2× MSAA — a measured 1.0 GB median and 2.0 GB peak on the very
    /// evening whose report blamed the whole plateau on the slider. Taken as a running maximum rather
    /// than a median because it is what advice is given against: a recommendation that fits the median
    /// hour and not the worst one sends somebody back to the pause menu a second time.
    /// </remarks>
    private ulong _overheadBytes;

    /// <summary>
    /// The last split that was computed, kept whether or not it was worth a line.
    /// </summary>
    /// <remarks>
    /// The budget line is written twice an evening; the approach warning has to be able to fire on any
    /// sample in between, so the numbers outlive the decision not to print them.
    /// </remarks>
    private ulong _lastGameBytes;
    private ulong _lastReservedBytes;
    private ulong _lastTotalBytes;

    /// <summary>The state the recent samples agree on, which becomes the reported one once it holds.</summary>
    private bool _candidatePresent;
    private int _candidateSamples;

    /// <summary>Notes the most recent adapter reading, which the next process table is measured against.</summary>
    /// <remarks>
    /// Only from a machine with one GPU, and that is the whole of the guard rather than a caveat on the
    /// output. NVML opens device index 0 and reports that card for the session while the process table is
    /// anchored on whichever adapter the game holds memory on, so with a second NVIDIA device present the
    /// subtraction below is one card's total minus another card's row — a desktop figure that is not
    /// wrong by a little. <see cref="VramAccountingMonitor"/> can afford to publish its comparison with a
    /// caveat because it is a reconciliation nobody acts on directly; this is a budget somebody chooses a
    /// graphics preset against, and a wrong number with a footnote is worse than no number. The
    /// reconciliation line already explains the two-GPU case every half hour.
    /// </remarks>
    public void Observe(GpuTelemetrySample sample)
    {
        if (sample is { IsAvailable: true, UsedVramBytes: not null, TotalVramBytes: > 0, IsSingleAdapterMachine: true })
        {
            _lastAdapter = sample;
        }
    }

    /// <summary>
    /// Supplies the texture budget the client was configured with, so the game's half of the split can
    /// be stated against a ceiling instead of only against the card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional. On a machine with no <c>fivem.cfg</c> — or a build of the client that does not write
    /// the convar — the budget line reads exactly as it did before.
    /// </para>
    /// <para>
    /// A new ceiling retires everything that was said against the old one. Both warnings fire once per
    /// session and the overhead is a running maximum, so a slider moved mid-session — which is the
    /// entire reason the settings are re-read at all — used to leave the monitor silent about the budget
    /// it now had, still holding an overhead measured as the distance above a budget that no longer
    /// existed. Lowering the slider by two steps would have <em>raised</em> the recorded overhead by the
    /// difference and then never mentioned it. Reset on the ceiling rather than on
    /// <see cref="FiveMClientConfig.Matches"/>: a file rewritten with the same value changes nothing
    /// here, and re-arming on it would let a client that rewrites its configuration repeat the same
    /// warning all evening.
    /// </para>
    /// </remarks>
    public void SetTextureBudget(FiveMClientConfig? config)
    {
        var before = _clientConfig;
        _clientConfig = config;

        var ceilingUnchanged = (before, config) switch
        {
            (null, null) => true,
            ({ } was, { } now) => was.TextureBudgetBytes == now.TextureBudgetBytes,
            _ => false,
        };

        if (ceilingUnchanged)
        {
            return;
        }

        _budgetApproachReported = false;
        _budgetOverheadReported = false;
        _overheadBytes = 0;
    }

    /// <summary>
    /// What the game holds beyond its streaming budget, or zero before it has ever exceeded it.
    /// </summary>
    public ulong OverheadBytes => _overheadBytes;

    /// <summary>
    /// The line to write when the game is closing on the budget it was given, or has passed it, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At most two lines per session, and they answer different questions. The first says the game is
    /// about to reach the budget it was configured with — a prediction, written hours before the worst
    /// frame, which is the whole point of writing it rather than reporting the plateau afterwards. The
    /// second says the game went past that budget anyway, and by how much.
    /// </para>
    /// <para>
    /// The second line exists because of what 2 September got wrong. <c>vid_budgetScale</c> governs
    /// streaming only, and the game's memory was a gigabyte above its budget all evening — so a monitor
    /// that only ever compared the two would have reported "at 100 % of its budget" while the real
    /// figure kept climbing, and the advice that followed would have been given against a ceiling the
    /// game had already left behind.
    /// </para>
    /// <para>
    /// Two claims either way, and they are separate. That the game is at its own budget is a fact about
    /// the configuration; that the card will be in the pressure band before it gets there is a fact
    /// about this machine, and only the second one is worth acting on.
    /// </para>
    /// </remarks>
    public string? DescribeBudgetApproach()
    {
        if (_clientConfig is not { } config || _lastTotalBytes == 0)
        {
            return null;
        }

        var budgetBytes = config.TextureBudgetBytes;
        if (budgetBytes == 0)
        {
            return null;
        }

        var roomForGame = FiveMClientConfig.RoomForGame(
            _lastTotalBytes,
            _lastReservedBytes,
            VramPressureBandMonitor.BandPercent);

        if (!_budgetOverheadReported && _overheadBytes >= MaterialOverheadBytes)
        {
            // Past the budget, with something real measured on top of it. Said once, and it retires the
            // approach line too: the approach has happened.
            _budgetOverheadReported = true;
            _budgetApproachReported = true;

            var over = $"Spelet håller {Gigabytes(_lastGameBytes)}, alltså "
                + $"{Gigabytes(_overheadBytes)} mer än sin streamingbudget på "
                + $"{Gigabytes(budgetBytes)}. Skillnaden är det budgeten inte styr — NUI, render "
                + "targets och bildbuffertar — och den ligger kvar oavsett var reglaget står.";

            return over + Verdict(config, _overheadBytes, roomForGame);
        }

        if (_budgetApproachReported || _lastGameBytes < budgetBytes * BudgetApproachShare)
        {
            return null;
        }

        _budgetApproachReported = true;

        var line = $"Spelet håller {Gigabytes(_lastGameBytes)} av sin streamingbudget på "
            + $"{Gigabytes(budgetBytes)} och kommer inte att sluta växa förrän den är full — och den "
            + "är inte hela dess minne: NUI, render targets och bildbuffertar ligger ovanpå.";

        return line + Verdict(config, _overheadBytes, roomForGame);
    }

    /// <summary>
    /// Whether what the game needs fits under the band, and what to do when it does not.
    /// </summary>
    /// <param name="overheadBytes">
    /// What the game holds outside the budget. Added before the comparison and subtracted before the
    /// recommendation — never left out, which is the omission that made a slider at 55 % look like the
    /// evening's cause.
    /// </param>
    private static string Verdict(FiveMClientConfig config, ulong overheadBytes, ulong roomForGame)
    {
        var needBytes = config.TextureBudgetBytes + overheadBytes;

        if (needBytes <= roomForGame)
        {
            return $" Det ryms: kortet har plats för {Gigabytes(roomForGame)} spel under "
                + $"{VramPressureBandMonitor.BandPercent:F0} %.";
        }

        return $" Kortet har bara plats för {Gigabytes(roomForGame)} under "
            + $"{VramPressureBandMonitor.BandPercent:F0} %, alltså {Gigabytes(needBytes - roomForGame)} "
            + $"för lite. {config.SuggestScale(roomForGame, overheadBytes)}";
    }

    /// <summary>
    /// Returns the budget line when it is worth writing, and null otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written once at the start, and again whenever the stream stack starts or stops — which is the
    /// only term of the three that changes during an evening, and changes by a gigabyte when it does.
    /// Everything else is a constant the session is spent inside.
    /// </para>
    /// <para>
    /// The split is against the card, never against the table alone. An excluded row is excluded from
    /// the top lists because its number cannot be believed, and the mistake was to let that exclusion
    /// remove the memory as well: the process still holds whatever it holds, the card is still counting
    /// it, and dropping the row silently moved that memory into the game's headroom. So a stream stack
    /// that cannot be measured per process is measured by difference instead — desktop from the rows
    /// that can still be believed, and everything the card says is missing booked onto the stack.
    /// </para>
    /// </remarks>
    public VramBudgetReport? Observe(GpuProcessMemorySample sample)
    {
        if (!sample.IsAvailable || sample.Processes.Count == 0)
        {
            return null;
        }

        if (_lastAdapter is not { UsedVramBytes: { } usedBytes, TotalVramBytes: { } totalBytes } adapter
            || (sample.Timestamp - adapter.Timestamp).Duration() > AdapterFreshness)
        {
            return null;
        }

        var believable = sample.Processes.Where(process => !sample.IsUnbelievable(process)).ToArray();
        var gameBytes = believable.Where(IsGame).Aggregate(0UL, (total, process) => total + process.DedicatedBytes);
        if (gameBytes == 0)
        {
            // Nothing to budget for yet: the game either has not allocated anything or is not running,
            // and a line about the desktop alone answers no question anybody has.
            return null;
        }

        // The table is cut to the largest holders, and the stream stack is several processes of which
        // the browser sources are small enough to fall under a low cut. What that costs is the split
        // rather than the budget: the desktop figure is a residual, so a stream row below the cut is
        // counted as desktop and the total the game does not get stays exactly right. Said out loud
        // when there is enough of it to move the split, because the two halves are what the advice is
        // given against — the stream stack can be closed and the desktop mostly cannot.
        var hiddenBytes = HiddenBytes(sample);

        // Everything the game does not hold, taken from the card's own figure so the table's double
        // counting cannot reach it. Floored because the two collectors sample at different instants and
        // a game that grew between them would otherwise produce a negative desktop.
        var otherBytes = usedBytes > gameBytes ? usedBytes - gameBytes : 0;

        var streamMeasuredBytes = believable.Where(IsStreamStack).Aggregate(0UL, (total, process) => total + process.DedicatedBytes);

        // A stream process whose row has been proved to double count cannot be measured, and must not
        // therefore be measured as zero. This is the case that produced the wrong headroom figure.
        var streamUnmeasurable = sample.UnbelievableProcesses.Any(IsStreamStack);

        ulong streamBytes;
        ulong desktopBytes;
        if (streamUnmeasurable)
        {
            var desktopMeasured = believable
                .Where(process => !IsGame(process) && !IsStreamStack(process))
                .Aggregate(hiddenBytes, (total, process) => total + process.DedicatedBytes);

            desktopBytes = Math.Min(desktopMeasured, otherBytes);
            streamBytes = otherBytes - desktopBytes;
        }
        else
        {
            streamBytes = Math.Min(streamMeasuredBytes, otherBytes);
            desktopBytes = otherBytes - streamBytes;
        }

        // Kept before the early return: the approach warning reads these on samples that produce no
        // line of their own, which is all but two of them.
        _lastGameBytes = gameBytes;
        _lastReservedBytes = desktopBytes + streamBytes;
        _lastTotalBytes = totalBytes;

        // What the slider does not reach, measured rather than assumed. Only observable once the game
        // has actually passed its streaming budget; before that it is zero because nothing has been
        // seen, not because there is none.
        if (_clientConfig?.TextureBudgetBytes is { } budgetBytes && gameBytes > budgetBytes)
        {
            _overheadBytes = Math.Max(_overheadBytes, gameBytes - budgetBytes);
        }

        var transition = TrackStreamStack(streamUnmeasurable ? null : streamMeasuredBytes >= StreamStackPresentBytes);
        if (_reported && transition is null)
        {
            return null;
        }

        _reported = true;

        // What the card itself says is unspent. Nothing derived from the process table is allowed to
        // exceed it: the line below is what a texture setting is chosen against, and on the evening
        // this was written for it said there were eight gigabytes free while the card was at 92%.
        var freeNowBytes = totalBytes > usedBytes ? totalBytes - usedBytes : 0;

        // What the game may still grow into: what it holds now plus what the card has left. Written as
        // a subtraction from the total as well, and the smaller of the two wins, so a table that has
        // lost a row cannot inflate it.
        var reservedBytes = desktopBytes + streamBytes;
        var headroomBytes = Math.Min(
            totalBytes > reservedBytes ? totalBytes - reservedBytes : 0,
            gameBytes + freeNowBytes);

        // The card's physical size is not where the trouble starts. Two sessions measured the same
        // edge: above 88% occupancy the hitch rate multiplies, and on 1 September 76% of every frame
        // over 100 ms fell inside the 17% of the evening spent there. A budget quoted against 10.0 GB
        // therefore offers room the machine cannot use — 8.2 GB for the game on an evening whose
        // measured ceiling was 6.9 — and it is this line that a texture setting gets chosen against.
        var bandBytes = (ulong)(totalBytes * VramPressureBandMonitor.BandPercent / 100);
        var bandHeadroomBytes = bandBytes > reservedBytes ? bandBytes - reservedBytes : 0;

        var message =
            $"{transition}VRAM-budget: skrivbordet håller {Gigabytes(desktopBytes)} och streamstacken "
            + $"{Gigabytes(streamBytes)}. Spelet håller nu {Gigabytes(gameBytes)} och ryms utan tryck upp till "
            + $"{Gigabytes(bandHeadroomBytes)}; kortets fysiska tak ger {Gigabytes(headroomBytes)}, men mätningarna "
            + $"säger att det börjar hacka redan när kortet passerar {VramPressureBandMonitor.BandPercent:F0} %. "
            + $"Kortet rapporterar {Gigabytes(usedBytes)} av {Gigabytes(totalBytes)} använt, alltså "
            + $"{Gigabytes(freeNowBytes)} ledigt just nu. Spelets tak sätts av texturinställningen; utrymme för "
            + "ett steg upp tas ur de två första posterna, inte ur kortet.";

        // The ceiling the game was configured with, said next to the room it has. Six sessions inferred
        // this number from a plateau hours after the fact; it is a line in fivem.cfg and it can be
        // stated on the first process sample.
        if (_clientConfig is { } config)
        {
            message += $" {config.Describe()}";

            if (config.DescribeAgainstCard(totalBytes, reservedBytes, VramPressureBandMonitor.BandPercent, _overheadBytes) is { } verdict)
            {
                message += $" {verdict}";
            }
        }

        if (streamUnmeasurable)
        {
            message +=
                $" Streamstacken kan inte mätas per process (räknaren är trasig och raden är utesluten); "
                + $"enligt kortet saknas {Gigabytes(streamBytes)} efter skrivbordet och de bokförs på "
                + "streamstacken.";
        }

        if (hiddenBytes >= MaterialHiddenBytes)
        {
            message +=
                $" {Gigabytes(hiddenBytes)} ligger i processer under topplistans gräns och räknas som "
                + "skrivbord; höj Gpu.ProcessMemoryTopCount om uppdelningen ska stämma på posten.";
        }

        return new VramBudgetReport(
            message,
            desktopBytes,
            streamBytes,
            gameBytes,
            headroomBytes,
            bandHeadroomBytes,
            totalBytes,
            usedBytes,
            freeNowBytes,
            streamUnmeasurable);
    }

    /// <summary>
    /// Follows the stream stack's state, returning the sentence that opens the line when it has really
    /// changed and null when there is nothing to report.
    /// </summary>
    /// <param name="present">
    /// What this sample observed, or null when the stack's rows cannot be believed and the sample
    /// therefore observed nothing. A broken row is not evidence that the stack stopped, which is exactly
    /// the mistake that produced eighteen transitions in sixteen minutes.
    /// </param>
    private string? TrackStreamStack(bool? present)
    {
        if (present is not { } observed)
        {
            // Hold. The state stands until something that can be measured disagrees with it.
            _candidateSamples = 0;
            return _reported ? null : string.Empty;
        }

        if (!_reported)
        {
            // The first line states the budget rather than a change, so it is not held back: there is
            // no previous state for it to flap against.
            _streamStackPresent = observed;
            _candidatePresent = observed;
            _candidateSamples = 1;
            return string.Empty;
        }

        if (observed == _streamStackPresent)
        {
            _candidatePresent = observed;
            _candidateSamples = 0;
            return null;
        }

        if (observed != _candidatePresent)
        {
            _candidatePresent = observed;
            _candidateSamples = 1;
            return null;
        }

        if (++_candidateSamples < StableSamplesForTransition)
        {
            return null;
        }

        _streamStackPresent = observed;
        _candidateSamples = 0;
        return observed ? "Streamstacken startade. " : "Streamstacken avslutades. ";
    }

    /// <summary>
    /// Dedicated bytes held by processes the table did not list, which the residual counts as desktop.
    /// </summary>
    /// <remarks>
    /// The untruncated total already travels with the sample for the reconciliation's sake. Both sides
    /// exclude only the absolutely impossible rows, so the difference is the rows below the cut and
    /// nothing else — in particular a row proved to double count is inside both and cancels.
    /// </remarks>
    private static ulong HiddenBytes(GpuProcessMemorySample sample)
    {
        var listed = sample.Processes
            .Where(process => !process.IsImplausible)
            .Aggregate(0UL, (total, process) => total + process.DedicatedBytes);

        return sample.AccountedDedicatedBytes > listed ? sample.AccountedDedicatedBytes - listed : 0;
    }

    /// <summary>
    /// The game's own rows. FiveM runs as more than one process and the launcher's name carries the
    /// build number, so this matches the two names rather than an exact one.
    /// </summary>
    private static bool IsGame(GpuProcessMemoryUsage process) =>
        process.ProcessName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)
        || process.ProcessName.Contains("GTA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The capture stack: the encoder and the browser sources it hosts, which are separate processes and
    /// separate rows.
    /// </summary>
    private static bool IsStreamStack(GpuProcessMemoryUsage process) =>
        process.ProcessName.StartsWith("obs", StringComparison.OrdinalIgnoreCase);

    private static string Gigabytes(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:F1} GB";
}

/// <summary>One statement of what the card's memory is committed to before the game asks for any.</summary>
/// <param name="AdapterUsedBytes">What the card itself reports as used, which every figure above answers to.</param>
/// <param name="FreeBytes">
/// Total minus the card's own used figure. The budget may never report more room than this, however the
/// per-process table splits it.
/// </param>
/// <param name="GameHeadroomBytes">
/// What the game could hold before the card is physically full. Kept because it is the number the
/// hardware imposes, but it is not the number to choose a texture setting against.
/// </param>
/// <param name="GameBandHeadroomBytes">
/// What the game can hold before the card enters the measured pressure band. Two sessions put that edge
/// at <see cref="VramPressureBandMonitor.BandPercent"/>, and above it the hitch rate multiplies — so
/// this, and not the physical ceiling, is the budget a setting is chosen against.
/// </param>
/// <param name="StreamStackDerived">
/// True when the stream stack was measured by difference because its own rows could not be believed.
/// </param>
public sealed record VramBudgetReport(
    string Message,
    ulong DesktopBytes,
    ulong StreamStackBytes,
    ulong GameBytes,
    ulong GameHeadroomBytes,
    ulong GameBandHeadroomBytes,
    ulong AdapterTotalBytes,
    ulong AdapterUsedBytes,
    ulong FreeBytes,
    bool StreamStackDerived);
