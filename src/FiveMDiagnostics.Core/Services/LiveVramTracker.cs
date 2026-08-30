namespace FiveMDiagnostics.Core;

/// <summary>
/// Keeps a live picture of which processes hold the adapter's memory, and notices when one of them
/// takes a large amount of it at once.
/// </summary>
/// <remarks>
/// <para>
/// Two things are wanted from the same data and neither was available during a session. The first is
/// simply the table: <see cref="VramBudgetMonitor"/> says the desktop holds 1.3 GB, and the next
/// question is always which program that is. It was answered by reading a CSV the following day, and by
/// then the answer had stopped being actionable.
/// </para>
/// <para>
/// The second is the step change. On 29 August <c>Voicemod</c> sat at 669 MB, flat, for three hours and
/// 47 minutes across 2 712 samples — and then went to 1 403 MB in twenty seconds and never gave it back.
/// It is a hard requirement on that machine, so the useful thing is not to close it but to know that
/// bringing its window up costs three quarters of a gigabyte, which is more than a step of texture
/// quality. Nothing in the app said so; it was found by diffing a column.
/// </para>
/// <para>
/// The alarm is on rate, not on size, because the game's own row grows too and must not trip it. FiveM
/// fills its texture pool to a ceiling over the first hours of a session — 3 985 MB to 7 200 MB over
/// twenty minutes at High, the fastest ramp measured, which is 2.7 MB/s. Voicemod's step was 36.7 MB/s,
/// thirteen times faster. A threshold on bytes alone would have to be set above the pool fill and would
/// then miss the step; a threshold on how fast the bytes arrive separates them with an order of
/// magnitude to spare and needs no exception list.
/// </para>
/// </remarks>
public sealed class LiveVramTracker
{
    /// <summary>How much a process has to take, inside <see cref="GrowthWindow"/>, to be worth saying.</summary>
    /// <remarks>
    /// Set from both sides of the measurement it has to separate. Voicemod's step delivered 734 MB inside
    /// twenty seconds; the game's fastest observed pool fill delivers about 160 MB in a minute. Anywhere
    /// between those works, and this sits nearer the quiet end so a slower step still registers.
    /// </remarks>
    private const ulong MaterialGrowthBytes = 384UL * 1024 * 1024;

    /// <summary>The window the growth is measured over.</summary>
    private static readonly TimeSpan GrowthWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a climb is allowed to continue before it is reported as it stands.
    /// </summary>
    /// <remarks>
    /// The report waits for the climb to stop so it can name the whole step rather than the moment the
    /// threshold was crossed. This bounds that wait, so a process that keeps taking memory is still
    /// reported — late and understated is better than never, and a genuine runaway is the case where the
    /// line matters most.
    /// </remarks>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Rows below this are left out of the live view. Two dozen processes hold a few megabytes each and
    /// listing them turns the one table that answers "what is holding the card" into a process list.
    /// </summary>
    private const ulong ListedBytes = 24UL * 1024 * 1024;

    /// <summary>
    /// How long a process id may go unseen before its history is dropped.
    /// </summary>
    /// <remarks>
    /// The history is keyed on the process id, and Windows reuses those within an evening. A new
    /// process landing on a dead one's id would inherit its baseline, its peak and — worst of the three
    /// — the flag saying its step change has already been reported, so the one line this class exists
    /// to write would never be written for it. The name is checked first and catches the common case;
    /// this catches the rest, including the same program restarting onto its own old id.
    /// <para>
    /// Long, because <see cref="GpuProcessMemorySample.Processes"/> carries the largest holders only and
    /// a live process can drop off that list for a while. Re-baselining one that did costs a row's
    /// "since session start" figure; letting a dead one's history stand costs a missed alert.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(5);

    private readonly Dictionary<int, ProcessHistory> _history = [];

    /// <summary>
    /// Folds a sample into the live view and returns it, along with any step change worth reporting.
    /// </summary>
    public LiveVramSnapshot Observe(GpuProcessMemorySample sample)
    {
        if (!sample.IsAvailable)
        {
            return new LiveVramSnapshot(sample.Timestamp, [], []);
        }

        var alerts = new List<LiveVramGrowth>();
        var rows = new List<LiveVramRow>();

        foreach (var process in sample.Processes)
        {
            // Unbelievable rows stay visible and stay labelled. Hiding them makes the fault they
            // represent unfindable, and this table is where somebody would notice obs64 claiming 39.9 GB
            // of a 10 GB card in the first place.
            var trusted = !sample.IsUnbelievable(process);

            // A history that belongs to a different program on the same id belongs to a process that has
            // exited, and starting the new one from the old one's numbers is worse than starting it from
            // nothing.
            if (_history.TryGetValue(process.ProcessId, out var history)
                && !string.Equals(history.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                history = null;
            }

            if (history is null)
            {
                history = new ProcessHistory(process.ProcessName, process.DedicatedBytes);
                _history[process.ProcessId] = history;
            }

            history.LastSeenAt = sample.Timestamp;

            if (trusted && history.Observe(sample.Timestamp, process.DedicatedBytes, out var takenBytes))
            {
                alerts.Add(new LiveVramGrowth(
                    process.ProcessName,
                    process.ProcessId,
                    takenBytes,
                    history.BaselineBytes,
                    process.DedicatedBytes));
            }

            if (process.DedicatedBytes >= ListedBytes || !trusted)
            {
                rows.Add(new LiveVramRow(
                    process.ProcessName,
                    process.ProcessId,
                    process.DedicatedBytes,
                    history.BaselineBytes,
                    history.PeakBytes,
                    trusted));
            }
        }

        Forget(sample.Timestamp);

        // Untrusted rows sort with the rest on their own reported size, which puts them at the top where
        // their label is impossible to miss. That is the right place for a number that is wrong.
        rows.Sort((left, right) => right.DedicatedBytes.CompareTo(left.DedicatedBytes));

        return new LiveVramSnapshot(sample.Timestamp, rows, alerts);
    }

    /// <summary>Drops the history of processes that have not been seen for <see cref="ForgetAfter"/>.</summary>
    /// <remarks>
    /// Over a dozen entries, once a sample. It bounds the dictionary over a long evening as much as it
    /// keeps a dead process's numbers from being handed to whatever lands on its id next.
    /// </remarks>
    private void Forget(DateTimeOffset now)
    {
        List<int>? expired = null;
        foreach (var (processId, history) in _history)
        {
            if (now - history.LastSeenAt > ForgetAfter)
            {
                (expired ??= []).Add(processId);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var processId in expired)
        {
            _history.Remove(processId);
        }
    }

    /// <summary>What one process has held, since the session started and over the recent past.</summary>
    private sealed class ProcessHistory(string processName, ulong baselineBytes)
    {
        private readonly Queue<(DateTimeOffset At, ulong Bytes)> _recent = new();
        private bool _reported;
        private ulong _previousBytes = baselineBytes;
        private ulong _pendingFloorBytes;
        private DateTimeOffset _pendingSince;
        private bool _pending;

        /// <summary>The program this history belongs to, which is what tells a reused id from the same process.</summary>
        public string ProcessName { get; } = processName;

        /// <summary>When the process last appeared in a sample.</summary>
        public DateTimeOffset LastSeenAt { get; set; }

        public ulong BaselineBytes { get; } = baselineBytes;

        public ulong PeakBytes { get; private set; } = baselineBytes;

        /// <summary>
        /// Records a reading and reports whether it completes a step worth naming.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reported when the climb stops, not when it crosses the threshold. Voicemod's step arrived over
        /// five samples — 669, 784, 934, 1 099, 1 258, 1 403 — and firing at the crossing would have
        /// announced 430 MB for something that took 734 by the time it settled. The point of the line is
        /// to be compared against what a step of texture quality costs, so it has to carry the whole
        /// figure.
        /// </para>
        /// <para>
        /// Once per process per session. A step that has happened stays in the numbers, and repeating it
        /// every five seconds for the rest of the evening would bury the next one.
        /// </para>
        /// </remarks>
        public bool Observe(DateTimeOffset at, ulong bytes, out ulong takenBytes)
        {
            takenBytes = 0;
            PeakBytes = Math.Max(PeakBytes, bytes);

            var previousBytes = _previousBytes;
            _previousBytes = bytes;

            _recent.Enqueue((at, bytes));
            while (_recent.Count > 1 && at - _recent.Peek().At > GrowthWindow)
            {
                _recent.Dequeue();
            }

            if (_reported)
            {
                return false;
            }

            if (!_pending)
            {
                var floorBytes = _recent.Min(entry => entry.Bytes);
                if (bytes <= floorBytes || bytes - floorBytes < MaterialGrowthBytes)
                {
                    return false;
                }

                _pending = true;
                _pendingFloorBytes = floorBytes;
                _pendingSince = at;
                return false;
            }

            // Still climbing, so the step is not over and its size is not yet known. The settle window
            // bounds the wait: a process that keeps taking memory indefinitely is worth saying so about
            // rather than waiting on forever.
            if (bytes > previousBytes && at - _pendingSince < SettleWindow)
            {
                return false;
            }

            takenBytes = PeakBytes > _pendingFloorBytes ? PeakBytes - _pendingFloorBytes : 0;
            _pending = false;
            _reported = true;
            return takenBytes > 0;
        }
    }
}

/// <summary>One process's line in the live VRAM view.</summary>
/// <param name="BaselineBytes">What it held when the session first saw it, so growth reads at a glance.</param>
/// <param name="IsTrusted">False for a row this session has proved impossible or double counted.</param>
public sealed record LiveVramRow(
    string ProcessName,
    int ProcessId,
    ulong DedicatedBytes,
    ulong BaselineBytes,
    ulong PeakBytes,
    bool IsTrusted)
{
    public double DedicatedMegabytes => DedicatedBytes / 1024d / 1024;

    /// <summary>Bytes taken since the session started; negative growth reads as zero.</summary>
    public double GrowthMegabytes => DedicatedBytes > BaselineBytes
        ? (DedicatedBytes - BaselineBytes) / 1024d / 1024
        : 0;
}

/// <summary>A process that took a large amount of the card at once.</summary>
public sealed record LiveVramGrowth(
    string ProcessName,
    int ProcessId,
    ulong TakenBytes,
    ulong BaselineBytes,
    ulong CurrentBytes)
{
    public string Message =>
        $"{ProcessName} tog {TakenBytes / 1024d / 1024:F0} MB VRAM på under en minut och håller nu "
        + $"{CurrentBytes / 1024d / 1024:F0} MB, mot {BaselineBytes / 1024d / 1024:F0} MB vid sessionens start. "
        + "Ett sådant steg ges sällan tillbaka; jämför det mot vad ett steg upp i texturkvalitet kostar.";
}

/// <summary>The live view as of one sample.</summary>
public sealed record LiveVramSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<LiveVramRow> Rows,
    IReadOnlyList<LiveVramGrowth> Growth);
