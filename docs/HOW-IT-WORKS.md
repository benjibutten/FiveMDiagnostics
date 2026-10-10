# How it works

This is the long version of the README: how a session is recorded, how incidents are found, and how the
engine decides what caused them. Setting names refer to `%LocalAppData%\FiveMDiagnostics\settings.json`.

Several rules below exist because an earlier, simpler rule gave a confident wrong answer on a real
session. Where that explains a default, the measurement is included.

## Sessions

A session starts when the FiveM game process appears and ends once it has been gone for ten minutes.
*Start and stop the session with FiveM* (`AutoSession`) turns this off and leaves both ends to the
buttons.

The session follows the game rather than the app for three reasons. A session records a snapshot when it
opens (machine uptime, displays, graphics settings), and one that started at boot would describe the wrong
moment. Deep capture keeps a trace buffer running for as long as the session does. And many players switch
the computer off with the game still running, so a session that only ended through the app would rarely
write its closing summaries.

A game restart within the ten minutes stays in the same session, so an evening with two crashes is one
session. Per-process readings still split at the restart, and the journal says so. The card keeps being
sampled during the ten minutes, because what stays allocated after the game exits is what the game was not
holding. Those readings are kept out of the session's own VRAM figures. Stopping by hand keeps the session
stopped until that game process exits.

## What is recorded

| Source | What it gives |
| --- | --- |
| PresentMon | Every frame: time between presents, CPU busy and wait, GPU busy and wait, present mode, time between display changes |
| NVML (NVIDIA only) | GPU load, memory bandwidth, VRAM used, encoder and decoder load, temperature, throttle reasons |
| Windows performance counters | CPU per core, memory, paging, disk latency and queue length, VRAM per process on any GPU |
| Process table | The game's CPU and memory, and the busiest other processes |
| Network | The game's TCP endpoints and UDP ports, and ping to the server |
| Window focus | Whether the game had focus, and which window took it |
| OBS WebSocket (when streaming) | Render and encoding lag, skipped frames, stream drops |
| WPR (administrator) | Deep capture traces around incidents |
| FiveM's own files | Client log, crash dumps, `fivem.cfg`, the game's `settings.xml` |

Everything goes onto one timeline held in a ring buffer of a few minutes. Files written per session, in
`%LocalAppData%\FiveMDiagnostics\Sessions`:

| File | Contents |
| --- | --- |
| `session_<time>.jsonl` | The journal: every status line, every incident with its analysis, session summaries |
| `presentmon_<pid>_<time>.csv` | Raw PresentMon rows for one game process |
| `gpu_<time>.csv` | One row per GPU sample, every 500 ms |
| `gpuprocs_<time>.csv` | VRAM per process, one row per process per sample, every 5 s |
| `obsstream_<time>.csv` | Stream counters while streaming |
| `deep_<time>_<id>.etl` | Deep capture traces |
| `CitizenFX_log_*.log` | A copy of FiveM's client log, taken when the game exits |

### The journal

The journal is JSON Lines, one object per line with `type`, `timestamp` and `payload`:

| `type` | Payload |
| --- | --- |
| `session-start` | Machine details and the settings that shape the evidence |
| `status` | One status line: level, source, message |
| `incident` | Marker, window, summary, top causes, suspected processes, timeline, event counts per source, attachments |
| `incident-update` | The same, rewritten after new evidence such as a trace was read |
| `session-end` | Number of incidents written |
| `journal-truncated` | Written instead of `session-end` when the 8 MB limit is reached |

Each line is flushed as it is written, because the case the journal exists for is the app being closed or
killed. It is append-only: a reader that keeps the last line per incident id has the current state. It
holds summaries, not the 90 seconds of frames behind each incident; those go into an incident export.
Event counts per source are included because an incident with no frame samples is a broken capture, not a
quiet incident.

## Incidents

An incident is a marker plus the 30 seconds before it and the 60 seconds after. It is analysed once the 60
seconds have passed.

### Marking by hand

`F9` marks the moment and, when the app runs as administrator, saves a deep capture. An optional second key
under *Settings* makes a light mark without a capture. A mark by hand records that a person noticed
something, which no counter can.

### Marking automatically

Waiting for a person to press a key catches a few percent of the stutters, and mostly the ones they happened
to be looking at. The detector therefore marks incidents itself, against a baseline that follows the
session: the rolling median frame time over the last 600 frames, never lower than one display refresh.

| Rule | Default | Severity |
| --- | --- | --- |
| Frame time at least `SpikeMultiplier` × baseline | 2.0 (33 ms at 60 fps) | Normal |
| Frame time at least `SevereMultiplier` × baseline | 4.0 (67 ms at 60 fps) | Severe |
| `DroppedFrameRun` frames in a row that never reached the screen | 3 | Normal |
| 20 or more spikes inside one rolling minute | fixed | Normal |

The last rule catches a stall on a timer. A 35 to 85 ms frame every third of a second never crosses a
frame-time threshold and still averages 57 to 59 fps.

Limits under `AutoDetect`:

- `Cooldown` (2 minutes) between incidents. Windows are 90 seconds long, so anything shorter produces
  incidents that describe each other's data.
- `MaxIncidentsPerWindow` (20) per `IncidentBudgetWindow` (1 hour). The budget is per hour rather than per
  session because a session-wide limit runs out early in a long evening and leaves the rest unwatched.
- `MinimumSamples` (120): nothing fires until the baseline has settled, so a loading screen is not a stutter.

Every value is clamped when settings are read and saved; a hand-edited zero would otherwise mark nearly
every frame. `MaxRetainedIncidents` (50) caps how many incidents stay in memory across sessions.

A frame that crosses a threshold while an incident is already open raises that incident instead of opening
a new one. The incident is renamed after the worst frame in its window and its severity can only go up.
Otherwise every incident would be named after the frame that opened it, which is usually the smallest.

### Relative and absolute thresholds

Spike thresholds are relative: a stutter is a frame that is late compared with what the machine is doing.
A 120 Hz display running at 120 fps flags from about 12.5 ms, a 60 Hz one from about 25 ms.

A relative threshold cannot see a slow decline, because the baseline drifts with it. `FramePacingMonitor`
covers that case. It classifies each minute against two things that do not drift: how much idle time the
pipeline had left (`MsCPUWait`; a frame cap that is being met leaves several milliseconds of wait per
frame), and the best cadence the session has held so far. Every minute is written to the journal, good ones
included, so the share of an evening that could not hold its frame rate can be computed. An incident is
raised when a bad stretch starts and then at intervals while it lasts. In one measured 6.5-hour session the
machine could not hold 60 fps for 104 minutes, and the spike detector raised almost nothing for it.

### Frames on time, screen late

A frame presented on time is not necessarily shown on time. `DisplayCadenceMonitor` rounds every
`MsBetweenDisplayChange` to a whole number of refreshes and reports the share that missed the cadence the
session holds. Counting refreshes rather than milliseconds keeps the figure comparable between a 60 Hz and
a 144 Hz display.

The most common cause found so far is two monitors at different refresh rates. Windows then composes the
game's frames and resamples them, and about one frame in nine reaches the screen a refresh early or late
while the frame times look perfect. In the measured case, setting both displays to the same rate took the
figure from about 11% to 0.4% and halved the stutter rate. `RefreshRateMismatch` checks the displays at
session start and says so when their rates differ by more than 10%.

## Deep capture

Deep capture uses WPR, the Windows Performance Recorder, and needs administrator rights.

It records continuously into a ring buffer in memory from the start of the session. A marker stops the
recording, which writes the buffer to an ETL file, and starts a new one. Starting the trace at the marker
would miss the cause: by the time someone presses a key, the frames that caused the stutter are seconds in
the past.

- `RingBufferMegabytes` (768) sets how much history a marker can save. A 256 MB test held about seven
  seconds, so the default holds about 21.
- `PostMarkerTail` (2 s) is how long the recording continues after the marker, so the recovery is in the
  trace as well.
- The app writes its own WPR profile each session: context switches with stacks, ready-thread events,
  CPU samples, DPC/ISR, disk and file I/O, hard faults and the working set. WPR's built-in
  `GeneralProfile` also traces every system call, which in one test made up about 5 GB of a 6.9 GB trace and
  could not be tied to a thread. `DeepCapture.CustomProfilePath` points at a profile of your own.
- Only one capture runs at a time. A marker raised while one is being written is recorded without a trace.

### When automatic incidents get a capture

Writing a capture costs several hundred megabytes and empties the buffer, so automatic incidents get one only
within a budget. All of these have to allow it:

- The frame reaches `AutoCaptureFrameTimeMs` (120 ms).
- `AutoCaptureCooldown` (10 minutes) has passed since the last capture.
- Fewer than `MaxAutoCapturesPerWindow` (3) captures were taken in the last `CaptureBudgetWindow` (1 hour).
- Fewer than `MaxAutoCapturesPerSession` (6) captures were taken in the session.

The hourly budget stops an early burst from using up every capture. In one session, 43 of 67 frames over
120 ms fell within fourteen minutes of the first hour.

A frame over `AutoCaptureOverrideFrameTimeMs` (250 ms) skips the cooldown and the hourly budget, and does not
use up the hour's slot. It still waits `AutoCaptureOverrideCooldown` (60 s) so the buffer has refilled. That
wait is raised automatically for a larger buffer. If the buffer has not refilled, an override frame is
captured against the part that has, as long as it is larger than the frame the previous capture was taken
for. It always waits for the previous capture to finish writing.

Both thresholds adapt to the session. `AdaptiveThresholdFramesPerHour` (20) and
`AdaptiveOverrideFramesPerHour` (3) set how many frames an hour may exceed each threshold. When more do,
the threshold moves to the level that rate actually reaches in this session. The ordinary threshold only
ever rises. The override can also fall, but never to within 25% of the ordinary threshold. Set a rate to
`0` to keep the fixed value.

A sustained bad stretch from the frame pacing monitor can also use a capture, since no single frame in it
stands out.

### Reading the trace back

A capture taken for an automatic incident is parsed and added to that incident's evidence, and the
incident is analysed again. Without this the trace sits next to an incident that never saw it. Set
`AnalyzeAutomaticCaptures` to `false` to import traces by hand instead.

The parser reports:

- the time span the trace actually covers, taken from the continuous streams. The file's own span measures
  how long the ring buffer has existed, not what it holds.
- when each stream was producing. Context switches have been seen stopping halfway through a trace while
  every other statistic looked healthy.
- DPC and ISR durations rather than counts. Thousands of short DPCs are normal; one 8 ms DPC stalls every
  thread.
- long off-CPU periods of the game's threads, with their wait reasons. This is what tells a thread that is
  blocked apart from one running script.

`FiveMDiagnostics.Tools.EtlAnalyzer` goes further by hand:

```powershell
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> cpu
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> thread --tid 24096
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> io
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> wait --min-ms 100
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> cpu --from-ms 20000 --to-ms 23000
```

`wait` answers the question a CPU report cannot: when the game's thread sleeps through a long frame, which
thread woke it and what it was blocked in. It follows the chain of wakers until it reaches a thread that
was running the whole time, which is the one doing the work everything else waits for. A DPC, a cycle, an
unknown waker or a depth of eight ends the chain. A step inferred from which thread was on the processor,
rather than from a recorded wake event, is marked as inferred. Rates are given in cores: 0.89 cores is
19.6 ms of CPU inside a 22 ms frame.

## How the engine ranks causes

Every incident gets a score per cause, each with the evidence that produced it:

1. GPU load
2. VRAM pressure
3. OBS rendering or encoding (only when OBS is measured)
4. A FiveM resource or script
5. Network jitter, loss or routing
6. Asset streaming or a disk stall
7. Another program
8. Windows or driver latency
9. Possible cache or resource corruption
10. The game's thread blocked waiting
11. The game not in focus
12. Paging to the paging file
13. The driver moving memory out of VRAM

A cause needs at least 35% to be reported. Below that the incident reads as insufficient evidence, with what
was missing.

### Which side of the pipeline was slow

PresentMon splits each frame into CPU and GPU parts:

| Signature | Reading |
| --- | --- |
| `MsCPUBusy` dominates | Script or resource work, or CPU contention |
| `MsGPUBusy` dominates | GPU contention, for example the encoder competing with the game |
| Neither is busy | The present or display path stalled: VRAM eviction, DPC/ISR latency or composition |

`MsCPUBusy` is not a measurement of execution. PresentMon derives it from the gap between presents, so a
main thread blocked on a lock reads exactly like one running script. In one trace, a 586 ms frame showed
585 ms of CPU busy for a thread that was off the processor for 569 ms of it. Three rules follow:

- When a trace covers the frame and shows the game's thread waiting, the CPU-side reading does not count
  as evidence of script work at all. How much is discarded is proportional: a 120 ms wait in a window that
  lost 1 750 ms to CPU-side spikes explains 7% of it.
- Without a trace or a profiler, a script verdict that rests only on this reading is capped at 34%, just
  under the reporting bar. It is a lead, not an answer.
- The frame's own `MsCPUWait` limits a thread-wait verdict in return. A trace shows that some thread waited;
  the frame shows whether the frame waited. If the large frames in a window show under a millisecond of
  wait, a thread-wait verdict cannot rank first.

On some machines `MsCPUWait` reads about 0.1 ms even for frames whose trace shows the game's thread asleep.
Then neither verdict ranks first and the incident is reported as unresolved, which is the honest answer
from two instruments that cannot tell the cases apart.

### A fallback never reaches a verdict

The disk counters can fail to open. The system collector says at session start which counters opened, and
again if one opened but produced nothing. A verdict that rests on a fallback, such as process disk
throughput in place of disk latency, is capped below the reporting bar. Throughput cannot tell a busy disk
from a slow one.

### Matching a trace to a frame

PresentMon and the trace use different clocks. The collector anchors PresentMon's relative times to wall
time as `min(readUtc - relativeMs)`, which can only be late: the read happens after PresentMon has buffered
and written the row. Measured against traces, the anchor ran 1.2 to 1.3 seconds late.

So a wait is matched to a frame by duration first and time second. It has to account for 50 to 150% of what
the frame lost and fall within three seconds of it. Genuine matches land within a few percent: 245.8 ms of
wait against a 245 ms frame, 197.2 against 199.

### Another program

A count of busy background processes says the same about two idle overlays as about a sync service using
more CPU than the game. The score is measured instead, and only counts when the machine was saturated,
since with cores to spare both simply run. It compares the background's CPU at a single instant with the
game's peak, both as a share of the whole machine. Peaks are not added up across the window, because a
program busy at the start and another busy at the end never loaded the machine together. Background disk
traffic above 200 MB/s adds a smaller term.

## VRAM

NVML gives the card's total use. Who holds it comes from the Windows `GPU Process Memory` counters, read
through PDH every five seconds, the same source as Task Manager. NVML cannot help here: its per-process
query is not supported for graphics processes on consumer drivers.

Figures are summed per process on one adapter, the one the game uses. A single-GPU desktop still lists two
adapters, and summing across them once reported 213 GB on a 10 GB card.

### Rows that cannot be right

No process can hold more than the card reports as used, so a row that does is counting shared memory twice.
Such a row is left out of the reports for the rest of the session, named in a warning and kept in the log.
The compositor (`dwm`) does this when it composes the game's frames, but the rule is written against the
arithmetic rather than the name.

The table is also compared with the card's own figure at session start and every thirty minutes. The sum
normally sits a little under the card's figure. A sum above it means double counting, and more than half
a gigabyte over is logged as a warning. That check only accuses on a single-GPU machine, where both
figures are known to describe the same card.

### The budget line

The session states what the card is committed to before the game asks for anything: what the desktop and
other programs hold (and the stream stack, when streaming), what the game holds, how much room is left, and
what the texture budget in `fivem.cfg` asks for. The desktop figure is the card's total minus the game's
row, so a double-counting row cannot distort it.

The room is quoted against 88% of the card rather than all of it, because stutters multiply above that
level. In two sessions compared minute by minute, the stutter rate inside that band was several times the
rate outside it. `VramPressureBandMonitor` measures this for every session by pairing each frame with the
VRAM reading nearest to it.

The texture advice in the line is held back while the game is still loading, since advice computed from a
half-full card points far too low. The readings are still written. On a machine with more than one GPU no budget is given, because the subtraction
would mix two cards.

When streaming, OBS's processes are split out as the stream stack. A change in the stack is only reported
after three samples in a row agree, because a row flipping between believable and excluded once produced
eighteen false start/stop lines in sixteen minutes.

### Live VRAM

*Advanced → What holds VRAM* shows every process's VRAM now, what it has taken since the session started,
and its peak. The app also reports when a process takes a large amount at once. That alarm is on the rate
of growth rather than on size, because the game's own memory grows steadily as it loads; in one session a
voice changer sat at 669 MB for nearly four hours and then took another 734 MB in twenty seconds.

## Session summaries

At the end of a session, and every fifteen minutes while it runs, the journal gets summary lines:

- how long the card spent above 88% and how much more often it stuttered there
- the `MsCPUWait` distribution of the largest frames, which separates a blocked thread from a pipeline
  working flat out
- which cause the engine ranked first, counted over all incidents
- what the deep captures themselves cost: the minute after writing a capture can hold more stutters than
  the rest of the evening, and two sessions with different numbers of captures are only comparable with
  that figure
- the game's graphics settings from `settings.xml`, read at start and every five minutes, with any change.
  When several copies exist (Documents redirected to OneDrive leaves one behind), the newest is read and the
  line says when it was written.

## Streaming with OBS

Only when *I stream with OBS* is ticked. Otherwise the OBS collector returns at once, no OBS lines are
written, and OBS counts as an ordinary program in the VRAM budget.

When it is ticked, the app polls OBS's WebSocket once a second while the game runs. If OBS is running but
the WebSocket does not answer within `Obs.ConnectionWarningDelay` (30 s), the window says so; it is a two
click fix in OBS and only worth anything while the session is running.

While the stream is live, the stream output's own counters are read as well: dropped frames, total frames,
bytes sent, congestion and reconnects. These are frames lost on the way to the ingest server, which is a
different counter from the encoder falling behind. The journal gets a line when more than 1% of frames are
dropped in a minute, a reminder every five minutes while it lasts, a line when three clean minutes end it,
and a summary per stream. Every poll is also written to `obsstream_<time>.csv`.

## PresentMon

The collector runs:

```text
--process_id {processId} --output_stdout --session_name {sessionName} --no_console_stats --stop_existing_session --terminate_on_proc_exit
```

There is no metrics flag on purpose. PresentMon 2.4.1 has three column schemes, and the default one is a
superset of `--v2_metrics`:

| Invocation | Time column | Frame time | CPU/GPU split |
| --- | --- | --- | --- |
| default | `TimeInMs` | `MsBetweenPresents` | `MsCPUBusy`, `MsGPUBusy`, `MsGPUWait` |
| `--v2_metrics` | `CPUStartTime` | `FrameTime` | `CPUBusy`, `GPUBusy`, `GPUWait` |
| `--v1_metrics` | `TimeInSeconds` | `msBetweenPresents` | none |

The parser reads all three, so a hand-edited template still produces rows. `--session_name` gives the
capture its own ETW session, so a second PresentMon on the machine cannot stop it, and
`--stop_existing_session` clears up after a capture that was killed. Output is read from stdout into a
bounded buffer and copied to the session's CSV; PresentMon's own output file is never opened.

PresentMon does not need administrator rights for a game running on the same account. Without them it
cannot see processes started on other accounts. The collector stops the capture itself when the game
exits rather than relying on `--terminate_on_proc_exit`.

The release bundles PresentMon 2.4.1 next to the app, pinned by hash, and the app prefers that copy over
any other on the machine. A configured path wins over both.

### Capture health

The collector restarts PresentMon when its process exits while the game is still running, or when it has
produced no frames for 15 seconds. Silence is ambiguous, though: an alt-tab or a loading screen presents
nothing either. So the tolerated silence doubles after every restart (15 s, 30 s, 60 s, up to 4 minutes),
and after five fruitless restarts automatic restarts stop until the game or the session is restarted. Two
minutes of healthy capture reset the count. A health line records frame count, largest gap and restarts,
and each incident export says whether its 30 and 60 seconds were covered.

## Network

The app records the game's TCP endpoints and UDP ports, and pings the server. Windows does not say which
peer a UDP socket talks to, and FiveM plays over UDP, so the server is taken from the game's TCP connection
to the same host, preferring port 30120. Set `ServerProfile.ProbeHost` if it picks the wrong host.

Many game servers do not answer ping. After 30 failures in a row the app stops pinging that host for the
session and says so once, rather than in every incident.

For better network evidence, import a `net_statsFile` CSV on the *Incidents* tab. Its ping, jitter and loss
outweigh the ping probe, and a healthy file counts as evidence against a network cause. An incident that
has already finished is analysed again when a file is imported.

## Exports

*Export selected* on the *Incidents* tab writes one incident as a zip: `summary.json`, `metrics.csv` with
the 90 seconds of samples, `incident-report.txt`, and optionally the attached files. The server address
appears in the analysis text as well as in the data, so both are redacted unless sensitive details are
included.

*Share* packages a whole session; [PRIVACY.md](../PRIVACY.md) describes what goes in and what is redacted.
A file belongs to the session whose journal was the last one opened before the file was created, so two
sessions minutes apart do not share files.

## Keeping the app light

The app must not cause the stutters it measures:

- Collectors that depend on the game do nothing until it runs.
- The process sweep does not enumerate threads; doing that for every process dominated the app's own
  allocations. Only the game reports a thread count.
- Analysis runs on its own queue instead of on the telemetry pump. Sorting and scanning a 90-second window
  on the pump stalled every collector in the middle of the stutter being recorded. The queue is drained
  before a session is reported as stopped.
- Hot paths that run hundreds of times a second at PresentMon's frame rate return early when there is
  nothing to do.
