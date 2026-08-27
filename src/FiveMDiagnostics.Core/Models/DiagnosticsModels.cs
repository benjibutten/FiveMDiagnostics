using System.Text.Json.Serialization;

namespace FiveMDiagnostics.Core;

public enum IncidentSeverity
{
    Normal,
    Severe,
}

public enum RootCauseCategory
{
    GpuFrametimeContention,
    GpuVramPressure,
    ObsRenderOutputContention,
    FiveMResourceSpike,
    NetworkJitterOrPacketLoss,
    StreamingOrDiskStall,
    ExternalProcessInterference,
    OsOrDriverLatency,
    PossibleCacheOrResourceCorruption,
    InsufficientEvidence,
    FiveMThreadWait,
}

public enum ArtifactKind
{
    NetStatsCsv,
    ProfilerJson,
    ResmonSnapshot,
    LogFile,
    EtlTrace,
    ManualAttachment,
}

public enum StatusLevel
{
    Info,
    Warning,
    Error,
}

public sealed record ServerProfile
{
    public string Name { get; set; } = string.Empty;
    public string? ProbeHost { get; set; }
    public string? EndpointHint { get; set; }
}

public sealed record PresentMonOptions
{
    /// <summary>
    /// Templates shipped by earlier builds. Stored settings matching one of these are migrated to
    /// <see cref="DefaultArgumentsTemplate"/> on load.
    /// </summary>
    public static readonly IReadOnlyList<string> SupersededArgumentsTemplates =
    [
        // PresentMon 1.x style, before 2.x support landed.
        "-process_id {processId} -output_file \"{outputPath}\"",

        // Passed --v2_metrics, which actually selects a *narrower* column scheme (FrameTime/CPUBusy
        // instead of MsBetweenPresents/MsCPUBusy) than PresentMon's own default.
        "--process_id {processId} --output_file \"{outputPath}\" --v2_metrics " +
        "--no_console_stats --stop_existing_session --terminate_on_proc_exit",

        // The first 2.x default still tailed PresentMon's live file. A transient sharing violation
        // could therefore terminate the collector for the remainder of a long session.
        "--process_id {processId} --output_file \"{outputPath}\" " +
        "--no_console_stats --stop_existing_session --terminate_on_proc_exit",

        // Read stdout, but used PresentMon's default ETW session name. Two PresentMon instances then
        // fight over one session: whichever starts second stops the first, so a stray capture — or a
        // second copy of this app — silently kills a running one.
        "--process_id {processId} --output_stdout " +
        "--no_console_stats --stop_existing_session --terminate_on_proc_exit",
    ];

    /// <summary>
    /// PresentMon 2.x invocation. No metrics flag: PresentMon's default already emits the full v2 column
    /// set, and it is a superset of what <c>--v2_metrics</c> produces.
    /// </summary>
    /// <remarks>
    /// <c>--session_name</c> is what keeps two captures from colliding. PresentMon names its ETW session
    /// <c>PresentMon</c> by default, so a second instance stops the first — observed as a capture that
    /// announced "a trace session named PresentMon is already running and it will be stopped" and then
    /// produced nothing. With a name of our own, <c>--stop_existing_session</c> only ever clears up
    /// after this app's own killed process, which is what it was there for.
    /// </remarks>
    public const string DefaultArgumentsTemplate =
        "--process_id {processId} --output_stdout --session_name {sessionName} " +
        "--no_console_stats --stop_existing_session --terminate_on_proc_exit";

    public string? ExecutablePath { get; set; }
    public string ArgumentsTemplate { get; set; } = DefaultArgumentsTemplate;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

public sealed record GpuOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Per-process VRAM accounting from the Windows <c>GPU Process Memory</c> counters.</summary>
    public bool ProcessMemoryEnabled { get; set; } = true;

    /// <summary>
    /// How often the per-process breakdown is sampled. Far slower than the adapter poll on purpose:
    /// a wildcard counter query walks every process holding a GPU allocation, and what it measures —
    /// who owns the memory — moves on the scale of loading a scene, not of a frame.
    /// </summary>
    public TimeSpan ProcessMemoryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Processes retained per sample, largest first. The tail of the list is dozens of processes holding
    /// a few megabytes of desktop composition each, which is noise in both the log and the report.
    /// </summary>
    /// <remarks>
    /// Raised from ten after the first session that used this table. The question it exists to answer is
    /// "what holds the gigabyte the game does not", and ten places was not enough to answer it: the
    /// non-game total of 1 941 MB was a floor rather than a figure, because every process past the tenth
    /// dropped out of the sample whenever a browser or a chat client opened a window. Nine holders were
    /// resident for the whole session and four more came and went, so the list has to be long enough for
    /// the transient ones to arrive without pushing the resident ones off the end.
    /// </remarks>
    public int ProcessMemoryTopCount { get; set; } = 25;

    /// <summary>
    /// The default this setting shipped with before <see cref="ProcessMemoryTopCount"/> was raised.
    /// Persisted settings files carry it, and a file that still says exactly this was never chosen by
    /// anyone — it is the old default, and it is migrated rather than left to silently truncate.
    /// </summary>
    public const int SupersededProcessMemoryTopCount = 10;

    /// <summary>
    /// Brings a settings file written before the list was lengthened up to the current default.
    /// </summary>
    /// <remarks>
    /// Raising a default in code reaches new installations only, and that gap has cost a session before:
    /// one kept recording with a 256 MB ring buffer for a week after the default became 768, because the
    /// value was already persisted. Only the exact superseded default is rewritten, so a number someone
    /// actually chose — including a deliberate ten — survives being written twice.
    /// </remarks>
    public bool MigrateProcessMemoryTopCount()
    {
        if (ProcessMemoryTopCount != SupersededProcessMemoryTopCount)
        {
            return false;
        }

        ProcessMemoryTopCount = new GpuOptions().ProcessMemoryTopCount;
        return true;
    }

    /// <summary>Clamps hand-edited values that would make the table useless or ruinously long.</summary>
    public void Normalize()
    {
        // One process is a table that can only ever name the game, and the counter query already walks
        // every process regardless of how many are kept, so a large ceiling costs only log size.
        ProcessMemoryTopCount = Math.Clamp(ProcessMemoryTopCount, 1, 200);

        ProcessMemoryInterval = ProcessMemoryInterval < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : ProcessMemoryInterval > TimeSpan.FromMinutes(5)
                ? TimeSpan.FromMinutes(5)
                : ProcessMemoryInterval;
    }
}

public sealed record ObsOptions
{
    public string Endpoint { get; set; } = "ws://127.0.0.1:4455";
    public string Password { get; set; } = string.Empty;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed record DeepCaptureOptions
{
    public const int CurrentCaptureProfileRevision = 2;

    /// <summary>
    /// Version of the generated capture profile defaults. Zero means settings written before profile
    /// migrations were introduced; it is deliberately not initialised here so JSON that lacks the
    /// property can be distinguished from newly created defaults.
    /// </summary>
    public int CaptureProfileRevision { get; set; }

    public static DeepCaptureOptions CreateDefault() => new()
    {
        CaptureProfileRevision = CurrentCaptureProfileRevision,
    };

    public bool Enabled { get; set; } = true;
    public string WprExecutablePath { get; set; } = "wpr.exe";

    /// <summary>
    /// Legacy single-profile setting. Read so a customised value from an older install is migrated into
    /// <see cref="Profiles"/> instead of being silently dropped; cleared once migrated.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Profile { get; set; }

    /// <summary>
    /// Fallback used only when the generated profile cannot be written or WPR refuses to start it.
    /// </summary>
    /// <remarks>
    /// GeneralProfile is what these entries exist to avoid. It enables syscall enter/exit tracing, which
    /// measured 88 of 132 million events and roughly 5 GB of a 6.9 GB trace — events that could not even
    /// be attributed to a thread. The generated profile in <see cref="UseGeneratedProfile"/> asks for the
    /// keywords the analysis actually reads and nothing else; this list only runs when that fails, so a
    /// deep capture still produces something rather than nothing.
    /// </remarks>
    public IList<string> Profiles { get; set; } = ["GeneralProfile", "CPU", "GPU", "DiskIO", "Minifilter", "ResidentSet"];

    /// <summary>
    /// Records through a generated .wprp rather than the built-in profiles above. Turn off only to
    /// compare against the old behaviour; the generated profile is what keeps the ETL under a gigabyte
    /// and is the only one that can size the ring buffer.
    /// </summary>
    public bool UseGeneratedProfile { get; set; } = true;

    /// <summary>
    /// A hand-written .wprp to use instead of the generated one. The profile it contains must be named
    /// <c>FiveMStall</c> and must define both a Memory and a File variant, or the ring buffer cannot
    /// start and the fallback profiles are used.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomProfilePath { get; set; }

    /// <summary>
    /// Size of the in-memory ring buffer the background session records into, and therefore how much
    /// history a marker can save — the part of the timeline where the cause of a stall actually is.
    /// </summary>
    /// <remarks>
    /// Now measured rather than guessed. With scheduler stacks enabled, a 256 MB capture retained about
    /// seven seconds, roughly 36.6 MB per second. The 768 MB default therefore buys about 21 seconds;
    /// automatic capture normally stops it at the hitch, while still leaving enough room for a manual
    /// reaction. It is non-paged pool held for the whole session, which is why it is not larger still.
    /// </remarks>
    public int RingBufferMegabytes { get; set; } = 768;

    /// <summary>Bytes per second of ring buffer the generated profile was measured to produce.</summary>
    /// <remarks>
    /// From a 256 MB capture with scheduler stacks that retained about 7 s. Used only to tell the user how many
    /// seconds of history their buffer size buys, which is the number that actually matters and is
    /// otherwise invisible until the ETL is opened.
    /// </remarks>
    public const double MeasuredRingBufferBytesPerSecond = 36.6 * 1024 * 1024;

    /// <summary>Rough seconds of history the configured buffer holds, at the measured fill rate.</summary>
    public double EstimatedRingBufferSeconds =>
        RingBufferMegabytes * 1024d * 1024d / MeasuredRingBufferBytesPerSecond;

    /// <summary>
    /// Walks a stack on every file open, system-wide, so a capture can say which component is doing the
    /// file work rather than leaving it to be inferred from where a thread's CPU samples landed.
    /// </summary>
    /// <remarks>
    /// Off by default because the cost is real and the payoff is occasional. Measured across two
    /// captures from the same machine, file opens run at 830–923 per second system-wide against 9 300 –
    /// 11 800 CPU samples per second, so this adds roughly 9% more stack walks — and it spends ring
    /// buffer, which is the resource that decides how many seconds of run-up a marker can save. Nothing
    /// in the app reads these stacks yet; they are for reading the ETL by hand when the question is
    /// specifically "which module is opening this file".
    /// </remarks>
    public bool CollectFileStacks { get; set; }

    /// <summary>
    /// Saves the kernel stacks attached to context switches and ready-thread events. These turn a long
    /// <c>Wait/UserRequest</c> interval from "the game was asleep" into "this call chain put it to
    /// sleep", which is the missing evidence in the August 24 traces.
    /// </summary>
    /// <remarks>
    /// This costs ring-buffer history, so the same profile revision also upgrades the old 256 MB/5 s
    /// defaults to 768 MB/2 s. Automatic incident capture stops the buffer at the hitch; the player
    /// does not have to press a key or run a separate CSwitch tool.
    /// </remarks>
    public bool CollectContextSwitchStacks { get; set; } = true;

    /// <summary>
    /// How long a marker waits before stopping the ring buffer, so the recovery after the hitch is in
    /// the trace as well as the run-up. The pre-incident history costs no wait at all: it is already in
    /// the buffer by the time the marker arrives.
    /// </summary>
    /// <remarks>
    /// The tail is not free, and that is why it is short. The session keeps recording throughout it, so
    /// every second of tail displaces a second of run-up from the far end of the ring. A capture with a
    /// five second tail was measured holding 5.9 s before the marker and 7.3 s after it — the tail plus
    /// the couple of seconds <c>wpr -stop</c> takes to drain — meaning more of the buffer described the
    /// recovery than the cause. Two seconds still shows whether the frame rate came back.
    /// </remarks>
    public TimeSpan PostMarkerTail { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Duration of a one-shot capture, used only when no ring buffer session is running — an unelevated
    /// start, a profile WPR rejected, or a marker that arrived before the session came up. Such a
    /// capture holds nothing from before the marker.
    /// </summary>
    public TimeSpan CaptureDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Severe manual markers retain their historical automatic capture behaviour. Enabling this also
    /// captures normal manual markers.
    /// </summary>
    public bool CaptureNormalManualIncidents { get; set; }

    /// <summary>
    /// Lets the detector save a trace on its own, without waiting for someone to press the marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Automatic captures used to be refused outright, on the reasoning that WPR is affordable once on
    /// demand and ruinous every couple of minutes. The reasoning was right and the rule was still wrong:
    /// a five and a half hour session raised eighteen severe incidents and produced no trace at all,
    /// because the only marker anyone pressed was one, and it missed its own stall. The two worst frames
    /// of that session — 2 846 ms and 1 683 ms — went unrecorded entirely.
    /// </para>
    /// <para>
    /// What was actually needed was a budget rather than a prohibition. The gates below are deliberately
    /// tight: only frames far beyond an ordinary spike qualify, and only a handful per session. The same
    /// session contained sixteen frames over 300 ms, so six captures would have covered the events that
    /// mattered at the cost of six flushes across an evening.
    /// </para>
    /// </remarks>
    public bool CaptureAutoIncidents { get; set; } = true;

    /// <summary>
    /// Frame time, in milliseconds, an automatically detected hitch has to reach before it may spend a
    /// capture. Absolute rather than a multiple of baseline, for the same reason
    /// <see cref="FramePacingOptions"/> is: the multiplier moves with the damage, and a threshold meant
    /// to catch the rare catastrophic frame must not drift upwards during a bad patch.
    /// </summary>
    /// <remarks>
    /// Was 300 ms, which was right for the sessions it was calibrated against and went blind the moment
    /// they improved. The session of 26 August ran seven hours and contained exactly one frame over
    /// 300 ms — the game's own restart, not a hitch — so two captures were taken in the first ninety
    /// minutes and none at all in the last three and a half hours. What remained to investigate that
    /// evening lived at 100–170 ms: 67 frames, of which the three largest after the opening hour exist
    /// in no trace. A threshold meant to catch the rare catastrophic frame has to be set against the
    /// frames the machine actually produces, and 120 ms is two missed frames at 60 Hz.
    /// </remarks>
    public double AutoCaptureFrameTimeMs { get; set; } = 120;

    /// <summary>
    /// Captures the detector may spend in one session. Each one writes a multi hundred megabyte ETL and
    /// leaves the ring buffer empty until it refills, so this is the setting that keeps a bad evening
    /// from filling the disk.
    /// </summary>
    public int MaxAutoCapturesPerSession { get; set; } = 8;

    /// <summary>
    /// Ceiling on automatic captures inside <see cref="CaptureBudgetWindow"/>, rather than for a whole
    /// session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same failure <see cref="AutoDetectOptions.MaxIncidentsPerWindow"/> was introduced to fix, one
    /// layer down. A session-wide ceiling is spent by whichever hour is worst, and the worst hour is
    /// routinely the opening one — a cache rebuilt after a settings change, a sync backlog, a level
    /// still streaming in. Replayed against the frames of 26 August, a flat 120 ms threshold against a
    /// session ceiling alone spends all six captures before 22:16 and four of them inside the warm-up
    /// hour, then leaves the remaining five hours bare. One per hour against the same frames spends six
    /// captures spread across the whole session and picks up the largest late-session frame, which is
    /// the evidence the evening was actually short of.
    /// </para>
    /// <para>
    /// A frame past <see cref="AutoCaptureOverrideFrameTimeMs"/> ignores this window, for the reason it
    /// ignores the cooldown: the session ceiling is meant to be the binding constraint on a genuinely
    /// catastrophic frame, and spacing meant to ration ordinary ones should not turn one away.
    /// </para>
    /// </remarks>
    public int MaxAutoCapturesPerWindow { get; set; } = 1;

    /// <summary>The window <see cref="MaxAutoCapturesPerWindow"/> is counted over.</summary>
    public TimeSpan CaptureBudgetWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Minimum spacing between automatic captures. Hitches arrive in bursts — 41% of the ones measured
    /// came within five seconds of another — and a burst is one event worth one trace, not twenty. The
    /// spacing also gives the ring buffer time to refill, which a capture inside the cooldown would find
    /// nearly empty anyway.
    /// </summary>
    public TimeSpan AutoCaptureCooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Frame time at which a hitch may break the cooldown instead of being refused by it, provided the
    /// session still has budget left.
    /// </summary>
    /// <remarks>
    /// The cooldown is sized for bursts, and it is right about bursts. It was wrong about the two frames
    /// that mattered most in the session of 25 August: a 518 ms frame refused with seven minutes of
    /// cooldown left and a 779 ms frame refused with ten, both while the session still had a capture in
    /// the budget it never spent. They are the third and fourth largest frames of that evening and
    /// neither exists in any trace. The ceiling is the constraint this feature was sized around — six
    /// captures across an evening — so a frame far beyond the ordinary threshold should be spending that
    /// ceiling rather than being turned away by spacing meant to collapse a burst.
    /// </remarks>
    public double AutoCaptureOverrideFrameTimeMs { get; set; } = 500;

    /// <summary>
    /// Spacing an overriding frame still has to clear. The cooldown may be broken; this may not.
    /// </summary>
    /// <remarks>
    /// A capture is not free the instant it is triggered. Measured across the five captures of 25
    /// August, the trigger to "capture saved" latency was 28–32 seconds, and the ring buffer then needs
    /// about <see cref="EstimatedRingBufferSeconds"/> — some 21 s at the 768 MB default — to refill. A
    /// capture started before that lands on a buffer holding a few seconds of history, which is the
    /// failure this whole gate exists to avoid. Sixty seconds clears both at the default buffer size,
    /// and it is what correctly refuses the 1 327 ms frame six seconds after a capture: that frame was
    /// already inside the trace the previous trigger produced.
    /// <para>
    /// A larger buffer refills more slowly, so this is raised to match it rather than trusted as a
    /// constant — <see cref="Normalize"/> floors it at the buffer's own refill time plus the tail and
    /// the drain. Sixty is the default, not the minimum.
    /// </para>
    /// </remarks>
    public TimeSpan AutoCaptureOverrideCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Applies one-time changes to settings saved by builds before capture-profile revisions.</summary>
    public bool MigrateCaptureProfile()
    {
        if (CaptureProfileRevision >= CurrentCaptureProfileRevision)
        {
            return false;
        }

        // These were the exact persisted defaults in the affected build. Preserve genuinely custom
        // buffer/tail combinations, while ensuring installations on those defaults get enough room for
        // the newly useful scheduler stacks.
        if (RingBufferMegabytes == 256 && PostMarkerTail == TimeSpan.FromSeconds(5))
        {
            RingBufferMegabytes = 768;
            PostMarkerTail = TimeSpan.FromSeconds(2);
        }

        CollectContextSwitchStacks = true;

        // Revision 2 lowers the automatic capture threshold and makes the budget time-windowed. Both
        // were persisted defaults rather than choices, so an installation still carrying them gets the
        // new ones; a hand-picked threshold or ceiling is left exactly as it is. Without this the
        // change reaches only fresh installs, and the machine under investigation is not one.
        if (AutoCaptureFrameTimeMs == 300)
        {
            AutoCaptureFrameTimeMs = 120;
        }

        if (MaxAutoCapturesPerSession == 6)
        {
            MaxAutoCapturesPerSession = 8;
        }

        CaptureProfileRevision = CurrentCaptureProfileRevision;
        return true;
    }

    /// <summary>Clamps hand-edited values that would otherwise make the ring buffer useless or ruinous.</summary>
    public void Normalize()
    {
        // Below ~64 MB the buffer wraps inside a second at the rates a stall produces, so the "history"
        // it holds would end after the marker anyway. The upper bound is non-paged pool the machine
        // gives up for the whole session.
        RingBufferMegabytes = Math.Clamp(RingBufferMegabytes, 64, 2048);

        PostMarkerTail = PostMarkerTail < TimeSpan.Zero
            ? TimeSpan.Zero
            : PostMarkerTail > TimeSpan.FromSeconds(60)
                ? TimeSpan.FromSeconds(60)
                : PostMarkerTail;

        // A threshold at or below an ordinary spike would let a routine 40 ms frame spend a capture, and
        // the budget would be gone in the first minute. 100 ms is already three missed frames at 60 Hz.
        AutoCaptureFrameTimeMs = double.IsNaN(AutoCaptureFrameTimeMs) || AutoCaptureFrameTimeMs < 100
            ? 120
            : Math.Min(AutoCaptureFrameTimeMs, 60_000);

        MaxAutoCapturesPerSession = Math.Clamp(MaxAutoCapturesPerSession, 0, 100);

        // Never more per window than the session allows in total, which would make the window budget
        // dead code rather than the tighter of the two gates.
        MaxAutoCapturesPerWindow = Math.Clamp(MaxAutoCapturesPerWindow, 1, Math.Max(MaxAutoCapturesPerSession, 1));

        // A window shorter than the cooldown cannot hold more than one capture anyway, and one longer
        // than a day is the session ceiling wearing a different name.
        CaptureBudgetWindow = CaptureBudgetWindow < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : CaptureBudgetWindow > TimeSpan.FromDays(1)
                ? TimeSpan.FromDays(1)
                : CaptureBudgetWindow;

        // Shorter than the post-marker tail plus the drain and the next capture starts against a ring
        // that holds almost nothing.
        AutoCaptureCooldown = AutoCaptureCooldown < TimeSpan.FromSeconds(30)
            ? TimeSpan.FromSeconds(30)
            : AutoCaptureCooldown > TimeSpan.FromHours(6)
                ? TimeSpan.FromHours(6)
                : AutoCaptureCooldown;

        // Clamped no lower than the ordinary threshold, where it means "off" rather than "always" —
        // see OverridesCooldownFor, which is where equality is given that meaning.
        AutoCaptureOverrideFrameTimeMs = double.IsNaN(AutoCaptureOverrideFrameTimeMs)
            ? Math.Max(500, AutoCaptureFrameTimeMs)
            : Math.Clamp(AutoCaptureOverrideFrameTimeMs, AutoCaptureFrameTimeMs, 60_000);

        // Never longer than the cooldown it overrides — that would make it dead code rather than a
        // shorter path — and never shorter than a capture takes to finish and the ring to refill.
        AutoCaptureOverrideCooldown = AutoCaptureOverrideCooldown < MinimumOverrideCooldown
            ? MinimumOverrideCooldown
            : AutoCaptureOverrideCooldown > AutoCaptureCooldown
                ? AutoCaptureCooldown
                : AutoCaptureOverrideCooldown;
    }

    /// <summary>
    /// Shortest override spacing this configuration can support, derived from the buffer rather than
    /// fixed.
    /// </summary>
    /// <remarks>
    /// A larger ring buffer takes proportionally longer to refill, so a constant would be right at one
    /// setting and wrong at the rest: at the 2 048 MB ceiling the refill alone is about 56 s, and a
    /// 60 s override would approve a capture while the previous one was still draining. The terms are
    /// the tail the previous capture recorded, the time its buffer needs to fill again, and thirty
    /// seconds for <c>wpr -stop</c> to write the file — measured at 28–32 s across five captures.
    /// </remarks>
    private TimeSpan MinimumOverrideCooldown => PostMarkerTail
        + TimeSpan.FromSeconds(EstimatedRingBufferSeconds)
        + TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a frame is catastrophic enough to spend budget the ordinary cooldown would have withheld.
    /// </summary>
    /// <remarks>
    /// An override threshold equal to the ordinary one disables the override rather than firing it for
    /// every capture-worthy frame. Equality is what <see cref="Normalize"/> produces from any value set
    /// below the ordinary threshold, and the alternative reading of it is the worst of both: every frame
    /// that clears 300 ms would bypass the ten minute spacing, which is precisely the burst behaviour
    /// the spacing exists to collapse.
    /// </remarks>
    public bool OverridesCooldownFor(double frameTimeMs)
    {
        return AutoCaptureOverrideFrameTimeMs > AutoCaptureFrameTimeMs
            && frameTimeMs >= AutoCaptureOverrideFrameTimeMs;
    }
}

/// <summary>
/// Thresholds for marking incidents without user input. Multipliers are relative to the cadence the
/// machine is actually achieving, for the same reason the correlation engine works that way: a fixed
/// millisecond threshold is either deaf on a 120 Hz display or deafening on a 60 Hz one.
/// </summary>
public sealed record AutoDetectOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Frame time, as a multiple of baseline, that marks a normal incident. 2x at 60 fps is one lost frame.</summary>
    public double SpikeMultiplier { get; set; } = 2.0;

    /// <summary>Frame time, as a multiple of baseline, that marks a severe incident.</summary>
    public double SevereMultiplier { get; set; } = 4.0;

    /// <summary>Consecutive undisplayed frames that count as a visible freeze on their own.</summary>
    public int DroppedFrameRun { get; set; } = 3;

    /// <summary>
    /// Minimum spacing between auto-marked incidents. Incident windows span 90 seconds, so a shorter
    /// cooldown would produce incidents that mostly re-describe each other's telemetry.
    /// </summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Ceiling on auto-marked incidents inside <see cref="IncidentBudgetWindow"/>, rather than for a
    /// whole session.
    /// </summary>
    /// <remarks>
    /// A session-wide ceiling spends itself early and then goes quiet: a four hour stream exhausted 40
    /// incidents after an hour and three quarters, so the detector was disarmed for the entire second
    /// half — including whatever changed to make it worse. A rolling window keeps the rate bounded
    /// without letting a bad opening hour buy silence for the rest of the evening.
    /// </remarks>
    public int MaxIncidentsPerWindow { get; set; } = 20;

    /// <summary>The window <see cref="MaxIncidentsPerWindow"/> is counted over.</summary>
    public TimeSpan IncidentBudgetWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Legacy session-wide ceiling. Read so a customised value from an older settings file becomes the
    /// window budget instead of being silently dropped; cleared once migrated.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxIncidentsPerSession { get; set; }

    /// <summary>Frames observed before any threshold is allowed to fire.</summary>
    public int MinimumSamples { get; set; } = 120;

    /// <summary>Frames the rolling median is computed over. 600 is roughly ten seconds at 60 fps.</summary>
    public int BaselineWindowFrames { get; set; } = 600;

    /// <summary>
    /// Upper bound on the rolling window. The detector allocates two arrays of this size up front, so an
    /// edited settings file must not be able to ask for hundreds of megabytes. 20 000 frames is about
    /// five minutes at 60 fps, far beyond what a baseline meant to track slow drift needs.
    /// </summary>
    public const int MaxBaselineWindowFrames = 20_000;

    /// <summary>
    /// Clamps hand-edited values into the range the detector was designed for and reports whether
    /// anything had to change.
    /// </summary>
    /// <remarks>
    /// These options are read from a JSON file the user can edit, and the degenerate values are not
    /// merely useless: a spike multiplier of 0, a dropped-frame run of 0 or a zero cooldown makes almost
    /// every frame a trigger, and each trigger snapshots a 90 second window, writes a status entry and
    /// refreshes the UI. Clamping at the point settings are read keeps that storm impossible instead of
    /// relying on every consumer to be defensive.
    /// </remarks>
    public bool Normalize()
    {
        var original = this with { };

        SpikeMultiplier = ClampMultiplier(SpikeMultiplier, fallback: 2.0);
        SevereMultiplier = Math.Max(ClampMultiplier(SevereMultiplier, fallback: 4.0), SpikeMultiplier);

        // One undisplayed frame is a dropped frame, not a freeze; a run is at least two.
        DroppedFrameRun = Math.Clamp(DroppedFrameRun, 2, 600);

        // A settings file written before the budget became time-windowed carries its ceiling here. The
        // old number was chosen as a whole-session allowance, so it is the closest thing to an intent
        // this code has; taking it clears it so the migration happens exactly once.
        if (MaxIncidentsPerSession is { } legacyCeiling)
        {
            MaxIncidentsPerWindow = legacyCeiling;
            MaxIncidentsPerSession = null;
        }

        MaxIncidentsPerWindow = Math.Clamp(MaxIncidentsPerWindow, 1, 500);
        BaselineWindowFrames = Math.Clamp(BaselineWindowFrames, 60, MaxBaselineWindowFrames);

        // A window shorter than the cooldown cannot hold more than one incident anyway, and one longer
        // than a day is a session-wide ceiling wearing a different name.
        IncidentBudgetWindow = IncidentBudgetWindow < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : IncidentBudgetWindow > TimeSpan.FromDays(1)
                ? TimeSpan.FromDays(1)
                : IncidentBudgetWindow;

        // The observed sample count saturates at the window size, so a minimum above it would keep the
        // detector permanently disarmed rather than merely conservative.
        MinimumSamples = Math.Clamp(MinimumSamples, 30, BaselineWindowFrames);

        // Incident windows span 90 seconds, so anything below a few seconds only produces incidents
        // that re-describe each other's telemetry.
        Cooldown = Cooldown < TimeSpan.FromSeconds(5)
            ? TimeSpan.FromSeconds(5)
            : Cooldown > TimeSpan.FromHours(1)
                ? TimeSpan.FromHours(1)
                : Cooldown;

        return this != original;
    }

    private static double ClampMultiplier(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        // Below 1.2 the "spike" is inside normal frame-to-frame variance at any refresh rate.
        return Math.Clamp(value, 1.2, 100);
    }
}

/// <summary>
/// Thresholds for <see cref="FramePacingMonitor"/>, which classifies the session window by window into
/// "the cadence held" and "the cadence did not".
/// </summary>
/// <remarks>
/// These are absolute on purpose. <see cref="AutoDetectOptions"/> measures against a rolling baseline
/// and therefore cannot see a slow degradation — the baseline moves with it. Everything here is
/// measured either against zero (how much idle time the pipeline had left) or against the best cadence
/// the machine has been shown to sustain in this same session, neither of which drifts with the damage.
/// </remarks>
public sealed record FramePacingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How much of the session each classified window covers. A minute is long enough that a single
    /// hitch cannot colour it and short enough to locate a bad patch to the minute.
    /// </summary>
    public TimeSpan WindowLength { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Frames a window needs before it is classified at all. Below this the window is a loading screen,
    /// an alt-tab or a capture gap, and its median says nothing about the machine.
    /// </summary>
    public int MinimumFrames { get; set; } = 600;

    /// <summary>
    /// Median <c>MsCPUWait</c>, in milliseconds, below which the pipeline is out of headroom. A frame
    /// rate cap that is being met shows up as several milliseconds of wait per frame; a measured 0.14 ms
    /// means nothing is being waited for and the CPU is what limits the frame rate.
    /// </summary>
    public double SaturatedCpuWaitMs { get; set; } = 1.0;

    /// <summary>Median <c>MsCPUWait</c> below which the margin is thin enough to be worth reporting.</summary>
    public double MarginalCpuWaitMs { get; set; } = 4.0;

    /// <summary>
    /// How far the window's median frame time has to sit above the session's best cadence before the
    /// vanished headroom counts as saturation rather than as a game that is simply uncapped.
    /// </summary>
    public double MarginalCadenceRatio { get; set; } = 1.08;

    /// <summary>
    /// Cadence ratio that means saturation on its own, used when no CPU/GPU breakdown is available and
    /// frame time is the only signal there is.
    /// </summary>
    public double SaturatedCadenceRatio { get; set; } = 1.25;

    /// <summary>
    /// Saturated windows between reminders once a bad patch is under way. The transition into
    /// saturation always raises an incident; without a reminder a half hour of 45 fps would be
    /// represented by a single marker at its start.
    /// </summary>
    public int SustainedReminderWindows { get; set; } = 15;

    /// <summary>Clamps hand-edited values into the range the monitor was designed for.</summary>
    public void Normalize()
    {
        WindowLength = WindowLength < TimeSpan.FromSeconds(10)
            ? TimeSpan.FromSeconds(10)
            : WindowLength > TimeSpan.FromMinutes(15)
                ? TimeSpan.FromMinutes(15)
                : WindowLength;

        MinimumFrames = Math.Clamp(MinimumFrames, 30, 100_000);
        SaturatedCpuWaitMs = Math.Clamp(SaturatedCpuWaitMs, 0.05, 8);
        MarginalCpuWaitMs = Math.Max(Math.Clamp(MarginalCpuWaitMs, 0.1, 16), SaturatedCpuWaitMs);
        MarginalCadenceRatio = Math.Clamp(MarginalCadenceRatio, 1.01, 3);
        SaturatedCadenceRatio = Math.Max(Math.Clamp(SaturatedCadenceRatio, 1.02, 5), MarginalCadenceRatio);
        SustainedReminderWindows = Math.Clamp(SustainedReminderWindows, 1, 1000);
    }
}

public sealed record PrivacyOptions
{
    public bool IncludeSensitiveFieldsInExport { get; set; }
    public bool IncludeAttachedArtifactsInExport { get; set; }
}

public sealed record DiagnosticsSettings
{
    public string WorkingDirectory { get; set; } = string.Empty;
    public string ExportDirectory { get; set; } = string.Empty;
    public string ArtifactDirectory { get; set; } = string.Empty;
    public TimeSpan RingBufferRetention { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan PreIncidentWindow { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PostIncidentWindow { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Incidents kept in memory across the whole application lifetime. Each one retains its full 90
    /// second event window — thousands of frame samples — so an unbounded history grows for as long as
    /// the app stays open, and the auto detector can add up to
    /// <see cref="AutoDetectOptions.MaxIncidentsPerWindow"/> per hour on top of every manual marker.
    /// The oldest are dropped once the cap is reached; exported bundles are unaffected.
    /// </summary>
    public int MaxRetainedIncidents { get; set; } = 50;

    public TimeSpan ProcessPollingInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan SystemPollingInterval { get; set; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan NetworkPollingInterval { get; set; } = TimeSpan.FromSeconds(2);
    public ServerProfile ServerProfile { get; set; } = new();
    public PresentMonOptions PresentMon { get; set; } = new();
    public GpuOptions Gpu { get; set; } = new();
    public ObsOptions Obs { get; set; } = new();
    public DeepCaptureOptions DeepCapture { get; set; } = DeepCaptureOptions.CreateDefault();
    public AutoDetectOptions AutoDetect { get; set; } = new();
    public FramePacingOptions FramePacing { get; set; } = new();
    public PrivacyOptions Privacy { get; set; } = new();
    public string Language { get; set; } = "en";

    public static DiagnosticsSettings CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveMDiagnostics");
        return new DiagnosticsSettings
        {
            WorkingDirectory = Path.Combine(root, "Sessions"),
            ExportDirectory = Path.Combine(root, "Exports"),
            ArtifactDirectory = Path.Combine(root, "Artifacts"),
        };
    }
}

public sealed record EnvironmentMetadata(
    string WindowsVersion,
    string CpuModel,
    ulong TotalMemoryBytes,
    string GpuName,
    string? GpuDriverVersion,
    double? DisplayRefreshRateHz,
    string? HagsState,
    bool ObsDetectedAtStart,
    string ServerProfileName,
    DateTimeOffset SessionStartedAt,
    DateTimeOffset? SessionEndedAt);

/// <summary>
/// A process the diagnostics session is following.
/// </summary>
/// <param name="StartedAt">
/// When the process started, or null when the start time could not be read. Together with the name it
/// is what tells a reused process id from the process this record was resolved from — see
/// <see cref="ProcessIdentity.StillMatches"/>.
/// </param>
public sealed record TargetProcessInfo(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset DetectedAt,
    DateTimeOffset? StartedAt = null);

public sealed record ProcessActivity(string ProcessName, int ProcessId, double CpuPercent, long IoBytesPerSecond);

public sealed record SuspectedProcessImpact(
    string ProcessName,
    int? ProcessId,
    double PeakCpuPercent,
    double PeakIoMegabytesPerSecond,
    int ObservedSamples,
    string Reason);

public sealed record RemoteEndpointInfo(string Protocol, string RemoteAddress, int RemotePort, string? EndpointHint = null);

public sealed record ArtifactAttachment(
    string FilePath,
    ArtifactKind Kind,
    string DisplayName,
    DateTimeOffset ImportedAt,
    bool Sensitive);

public sealed record ArtifactParseResult(
    ArtifactAttachment Attachment,
    IReadOnlyList<ArtifactEvidence> Evidence,
    IReadOnlyList<string> Notes);

public sealed record DeepCaptureResult(bool Started, bool RequiresElevation, string Message, string? CapturePath = null);

public sealed record DiagnosticStatusEntry(DateTimeOffset Timestamp, StatusLevel Level, string Source, string Message);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FrameTelemetrySample), "frame")]
[JsonDerivedType(typeof(GpuTelemetrySample), "gpu")]
[JsonDerivedType(typeof(GpuProcessMemorySample), "gpu-process-memory")]
[JsonDerivedType(typeof(SystemTelemetrySample), "system")]
[JsonDerivedType(typeof(ProcessTelemetrySample), "process")]
[JsonDerivedType(typeof(ObsTelemetrySample), "obs")]
[JsonDerivedType(typeof(NetworkEndpointSample), "endpoint")]
[JsonDerivedType(typeof(NetworkProbeSample), "probe")]
[JsonDerivedType(typeof(ArtifactEvidence), "artifact")]
[JsonDerivedType(typeof(CaptureHealthTelemetrySample), "capture-health")]
public abstract record TelemetryEvent(DateTimeOffset Timestamp, string Source);

/// <summary>
/// One presented frame. The CPU/GPU breakdown comes from PresentMon 2.x <c>--v2_metrics</c> and is what
/// separates a CPU-bound spike (script/resource work) from a GPU-bound one (encode contention) from a
/// present/composition stall. The fields stay nullable because PresentMon 1.x CSVs do not carry them.
/// </summary>
public sealed record FrameTelemetrySample(
    DateTimeOffset Timestamp,
    double FrameTimeMs,
    double? GpuBusyMs,
    double? DisplayLatencyMs,
    double? MsBetweenPresents,
    bool Dropped,
    string ProcessName,
    double? SwapChainLatencyMs = null,
    double? CpuBusyMs = null,
    double? CpuWaitMs = null,
    double? GpuWaitMs = null,
    double? GpuLatencyMs = null,
    double? FlipDelayMs = null,
    double? InputLatencyMs = null,
    string? PresentMode = null,
    double? MsBetweenDisplayChange = null) : TelemetryEvent(Timestamp, "Frame")
{
    /// <summary>
    /// True when the frame did not reach the screen through an independent hardware flip. Every
    /// "Composed:" mode routes the frame through DWM instead, which adds a compositor hop the frame
    /// time never shows — a capture where every frame sat in <c>Composed: Copy with GPU GDI</c> is a
    /// different machine from one running <c>Hardware: Independent Flip</c>, and nothing else in the
    /// telemetry distinguishes them.
    /// </summary>
    public bool IsComposedPresent => PresentMode is { } mode
        && mode.StartsWith("Composed", StringComparison.OrdinalIgnoreCase);
}

public sealed record GpuTelemetrySample(
    DateTimeOffset Timestamp,
    bool IsAvailable,
    string? AdapterName,
    double? UtilizationPercent,
    double? MemoryBandwidthUtilizationPercent,
    ulong? UsedVramBytes,
    ulong? TotalVramBytes,
    double? EncoderUtilizationPercent,
    double? DecoderUtilizationPercent,
    int? TemperatureCelsius,
    IReadOnlyList<string> ThrottleReasons,
    int? AdapterCount = null) : TelemetryEvent(Timestamp, "GPU")
{
    public double? VramUsagePercent => TotalVramBytes is > 0 && UsedVramBytes is not null
        ? (double)UsedVramBytes.Value / TotalVramBytes.Value * 100
        : null;

    /// <summary>
    /// Whether this reading is certain to describe the same adapter the per-process table does.
    /// </summary>
    /// <remarks>
    /// NVML opens device index 0 and reports that card for the whole session, while the per-process
    /// counter table is anchored on whichever adapter the game holds memory on. With one NVIDIA device
    /// present those are the same card and the two figures are comparable. With more than one they need
    /// not be, and comparing them would produce a discrepancy that says nothing about either. Null means
    /// the count was not recorded — samples from before it was carried, and fakes — and is treated the
    /// same as unconfirmed rather than assumed safe.
    /// </remarks>
    public bool IsSingleAdapterMachine => AdapterCount == 1;
}

/// <summary>
/// One process's share of the adapter's memory, as Windows accounts it.
/// </summary>
/// <param name="DedicatedBytes">
/// Memory resident in VRAM. This is the number that has to fit in the card, and the one Task Manager
/// shows as "Dedicated GPU memory".
/// </param>
/// <param name="SharedBytes">
/// Memory the process has in system RAM that the GPU can reach over PCIe. It grows when allocations no
/// longer fit in VRAM, so a process whose shared usage climbs while dedicated stalls is being evicted.
/// </param>
/// <param name="InstanceCount">
/// Counter instances the figure was summed from, kept so a reading that goes wrong can be diagnosed
/// from the log rather than by reading the aggregator's source a week later. Defaulted because most
/// callers constructing one of these by hand are tests and reports that never saw a counter.
/// </param>
public sealed record GpuProcessMemoryUsage(
    int ProcessId,
    string ProcessName,
    ulong DedicatedBytes,
    ulong SharedBytes,
    int InstanceCount = 0,
    IReadOnlyList<GpuProcessMemoryInstance>? Instances = null)
{
    /// <summary>
    /// Above this, a per-process dedicated figure is a counter fault rather than a measurement.
    /// </summary>
    /// <remarks>
    /// No consumer adapter has this much local memory, so nothing on a machine this app runs on can
    /// legitimately reach it. The bound exists because a runaway instance sum already happened once and
    /// went out in 145 reports as the largest VRAM holder; naming a reading as impossible is cheap and
    /// the alternative is a plausible-looking table that is wrong about the one thing it is for.
    /// </remarks>
    public const ulong ImplausibleDedicatedBytes = 64UL * 1024 * 1024 * 1024;

    /// <summary>Whether the reading is large enough to be impossible on any adapter this runs on.</summary>
    public bool IsImplausible => DedicatedBytes > ImplausibleDedicatedBytes;

    public double DedicatedGigabytes => DedicatedBytes / 1024d / 1024 / 1024;

    public double SharedGigabytes => SharedBytes / 1024d / 1024 / 1024;
}

/// <summary>
/// One raw counter instance behind a process's row, kept only for readings the aggregate says are
/// impossible.
/// </summary>
/// <remarks>
/// The instance count was added to prove that an impossible total came from summing many instances, and
/// on its first outing it disproved it: obs64 reported 209 GB on a 10 GB card across a whole session
/// with an instance count of one. A single number cannot say more than that, so the next question —
/// which adapter that instance belongs to, and whether the figure is dedicated or shared memory being
/// double counted — needs the instance itself. Kept for impossible rows only: the healthy case is
/// twenty-odd instances every five seconds, and none of them are worth the disk.
/// </remarks>
public sealed record GpuProcessMemoryInstance(string InstanceName, string? Adapter, ulong DedicatedBytes, ulong SharedBytes);

/// <summary>
/// Which processes hold the adapter's memory, so "VRAM is at 95 %" can be answered with "and here is
/// what is holding it".
/// </summary>
/// <remarks>
/// NVML reports occupancy for the whole card, which was enough to find that stalls cluster above a
/// VRAM threshold but not to act on it: the fix differs entirely depending on whether the game, the
/// capture software or a browser owns the last gigabyte. The numbers come from the Windows
/// <c>GPU Process Memory</c> counter set — the same source as Task Manager's per-process column — so
/// they are vendor neutral and cover processes NVML never reports in WDDM mode.
/// </remarks>
public sealed record GpuProcessMemorySample(
    DateTimeOffset Timestamp,
    bool IsAvailable,
    IReadOnlyList<GpuProcessMemoryUsage> Processes,
    string? UnavailableReason = null,
    ulong? AllProcessesDedicatedBytes = null) : TelemetryEvent(Timestamp, "GpuProcessMemory")
{
    /// <summary>
    /// Dedicated bytes across every process on the adapter, including those below the top-N cut.
    /// </summary>
    /// <remarks>
    /// <see cref="Processes"/> holds the largest holders only, so <see cref="TotalDedicatedBytes"/>
    /// answers "how much do the biggest hold" rather than "how much is accounted for". Reconciling the
    /// table against the adapter's own figure needs the second question, because double counting in a
    /// process that never reaches the list would be invisible to the first. Falls back to the top-N sum
    /// for samples recorded before the untruncated figure was carried.
    /// </remarks>
    public ulong AccountedDedicatedBytes => AllProcessesDedicatedBytes ?? TotalDedicatedBytes;

    /// <summary>
    /// Dedicated bytes over every process that reported any.
    /// </summary>
    /// <remarks>
    /// Deliberately not expected to match the adapter total NVML reports. The counter set covers
    /// processes, and VRAM also holds the display's own framebuffers plus allocations belonging to
    /// nothing that is still running; the gap between this sum and NVML's figure is itself readable.
    /// <para>
    /// Impossible readings are excluded for the same reason <see cref="Top"/> excludes them, and it
    /// matters more here than it looks: the correlation engine picks an incident's peak sample by this
    /// total. One runaway figure climbing all evening would make the newest sample in every window the
    /// "fullest" one, quietly turning the peak into an arbitrary pick — and the export writes this
    /// number out as the figure to compare against the adapter's own.
    /// </para>
    /// </remarks>
    public ulong TotalDedicatedBytes => Processes
        .Where(process => !process.IsImplausible)
        .Aggregate(0UL, (total, process) => total + process.DedicatedBytes);

    /// <summary>Processes whose reading is impossible for the adapter, kept in the log and out of reports.</summary>
    public IEnumerable<GpuProcessMemoryUsage> ImplausibleProcesses => Processes.Where(process => process.IsImplausible);

    /// <summary>
    /// The largest holders, for a report that has room to name a few.
    /// </summary>
    /// <remarks>
    /// Impossible readings are skipped rather than dropped from <see cref="Processes"/>. A report that
    /// says "largest in VRAM: obs64 with 213.9 GB" is worse than one that names the real second place,
    /// but a log that quietly discards the row makes the fault itself unfindable — and the fault is what
    /// tells us the aggregation is wrong.
    /// </remarks>
    public IEnumerable<GpuProcessMemoryUsage> Top(int count)
    {
        return Processes
            .Where(process => !process.IsImplausible)
            .OrderByDescending(process => process.DedicatedBytes)
            .Take(count);
    }
}

public sealed record SystemTelemetrySample(
    DateTimeOffset Timestamp,
    double TotalCpuUsagePercent,
    IReadOnlyDictionary<string, double> PerCoreUsagePercent,
    double MemoryCommitPercent,
    ulong AvailableMemoryMb,
    IReadOnlyList<ProcessActivity> TopCpuProcesses,
    IReadOnlyList<ProcessActivity> TopDiskProcesses,
    double? DiskAverageLatencyMs = null,
    double? DiskQueueLength = null,
    double? HardFaultPagesPerSecond = null) : TelemetryEvent(Timestamp, "System");

public sealed record ProcessTelemetrySample(
    DateTimeOffset Timestamp,
    int ProcessId,
    string ProcessName,
    double CpuUsagePercent,
    long PrivateBytes,
    long WorkingSetBytes,
    int ThreadCount,
    long ReadBytesPerSecond,
    long WriteBytesPerSecond) : TelemetryEvent(Timestamp, "Process");

public sealed record ObsTelemetrySample(
    DateTimeOffset Timestamp,
    bool IsConnected,
    double? ActiveFps,
    double? AverageFrameRenderTimeMs,
    long? RenderSkippedFrames,
    long? OutputSkippedFrames,
    double? CpuUsagePercent,
    double? MemoryUsageMb,
    bool IsStreaming,
    bool IsRecording,
    bool IsProcessRunning = false) : TelemetryEvent(Timestamp, "OBS");

/// <summary>
/// Low-rate PresentMon health snapshot. Keeping this at roughly one sample per second exposes gaps and
/// restarts without duplicating per-frame telemetry or adding another polling loop.
/// </summary>
public sealed record CaptureHealthTelemetrySample(
    DateTimeOffset Timestamp,
    long FrameCount,
    DateTimeOffset? FirstFrameAt,
    DateTimeOffset? LastFrameAt,
    double LargestFrameGapSeconds,
    double ContinuousFrameSpanSeconds,
    int RestartCount,
    bool CaptureProcessRunning,
    int FrameGapCount = 0) : TelemetryEvent(Timestamp, "CaptureHealth");

public sealed record NetworkEndpointSample(
    DateTimeOffset Timestamp,
    int ProcessId,
    IReadOnlyList<RemoteEndpointInfo> RemoteEndpoints,
    IReadOnlyList<int> UdpLocalPorts) : TelemetryEvent(Timestamp, "Network");

public sealed record NetworkProbeSample(
    DateTimeOffset Timestamp,
    string Host,
    double? RoundTripTimeMs,
    bool Success,
    string? FailureReason = null) : TelemetryEvent(Timestamp, "Probe");

public sealed record ArtifactEvidence(
    DateTimeOffset Timestamp,
    ArtifactKind Kind,
    string Summary,
    IReadOnlyDictionary<string, double> Metrics,
    string? SourceFile = null) : TelemetryEvent(Timestamp, "Artifact");

public sealed record IncidentMarker(Guid Id, DateTimeOffset MarkedAt, IncidentSeverity Severity, string Label);

public sealed record TimelineHighlight(DateTimeOffset Timestamp, string Category, string Summary);

public sealed record HypothesisScore(RootCauseCategory Category, double Confidence, IReadOnlyList<string> Evidence);

public sealed record IncidentAnalysis(
    IReadOnlyList<HypothesisScore> Hypotheses,
    bool InsufficientEvidence,
    string Summary,
    IReadOnlyList<TimelineHighlight> TimelineHighlights,
    IReadOnlyList<SuspectedProcessImpact> SuspectedProcesses);

public sealed record IncidentRecord(
    Guid Id,
    IncidentMarker Marker,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    EnvironmentMetadata Environment,
    IReadOnlyList<TelemetryEvent> Events,
    IncidentAnalysis? Analysis,
    IReadOnlyList<ArtifactAttachment> Attachments)
{
    public IReadOnlyList<TEvent> GetEvents<TEvent>() where TEvent : TelemetryEvent
    {
        return Events.OfType<TEvent>().OrderBy(item => item.Timestamp).ToArray();
    }
}

public sealed record ExportBundleOptions(string OutputDirectory, bool IncludeSensitiveFields, bool IncludeAttachedArtifacts);
