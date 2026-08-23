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
}

public sealed record ObsOptions
{
    public string Endpoint { get; set; } = "ws://127.0.0.1:4455";
    public string Password { get; set; } = string.Empty;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed record DeepCaptureOptions
{
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
    /// Now measured rather than guessed. A 256 MB capture from a live session retained 13.3 seconds of
    /// samples once context switch stacks stopped being walked, which is about 19 MB per second. That is
    /// not enough: a human needs six to seven seconds to feel a hitch and reach the marker, and the
    /// capture that prompted this change reached back 5.9 seconds and missed its own stall by 0.4. The
    /// default buys roughly forty seconds at the measured rate, so the run-up survives the reaction
    /// time. It is non-paged pool held for the whole session, which is why it is not larger still.
    /// </remarks>
    public int RingBufferMegabytes { get; set; } = 768;

    /// <summary>Bytes per second of ring buffer the generated profile was measured to produce.</summary>
    /// <remarks>
    /// From a 256 MB capture that retained 13.26 s of CPU samples. Used only to tell the user how many
    /// seconds of history their buffer size buys, which is the number that actually matters and is
    /// otherwise invisible until the ETL is opened.
    /// </remarks>
    public const double MeasuredRingBufferBytesPerSecond = 19.3 * 1024 * 1024;

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
    public double AutoCaptureFrameTimeMs { get; set; } = 300;

    /// <summary>
    /// Captures the detector may spend in one session. Each one writes a multi hundred megabyte ETL and
    /// leaves the ring buffer empty until it refills, so this is the setting that keeps a bad evening
    /// from filling the disk.
    /// </summary>
    public int MaxAutoCapturesPerSession { get; set; } = 6;

    /// <summary>
    /// Minimum spacing between automatic captures. Hitches arrive in bursts — 41% of the ones measured
    /// came within five seconds of another — and a burst is one event worth one trace, not twenty. The
    /// spacing also gives the ring buffer time to refill, which a capture inside the cooldown would find
    /// nearly empty anyway.
    /// </summary>
    public TimeSpan AutoCaptureCooldown { get; set; } = TimeSpan.FromMinutes(10);

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
            ? 300
            : Math.Min(AutoCaptureFrameTimeMs, 60_000);

        MaxAutoCapturesPerSession = Math.Clamp(MaxAutoCapturesPerSession, 0, 100);

        // Shorter than the post-marker tail plus the drain and the next capture starts against a ring
        // that holds almost nothing.
        AutoCaptureCooldown = AutoCaptureCooldown < TimeSpan.FromSeconds(30)
            ? TimeSpan.FromSeconds(30)
            : AutoCaptureCooldown > TimeSpan.FromHours(6)
                ? TimeSpan.FromHours(6)
                : AutoCaptureCooldown;
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
    public DeepCaptureOptions DeepCapture { get; set; } = new();
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
    IReadOnlyList<string> ThrottleReasons) : TelemetryEvent(Timestamp, "GPU")
{
    public double? VramUsagePercent => TotalVramBytes is > 0 && UsedVramBytes is not null
        ? (double)UsedVramBytes.Value / TotalVramBytes.Value * 100
        : null;
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
