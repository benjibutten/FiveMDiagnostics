# FiveM Diagnostics

FiveM Diagnostics is a Windows-only WPF desktop app for collecting local evidence around intermittent FiveM stutter incidents and ranking likely root causes.

The collection and analysis pipeline is designed to be generic and work with any FiveM server.

## What v1 does

- WPF desktop app with tray mode
- MVVM-based UI with tray context menu controls
- Background collectors that only sample while a FiveM/GTA process is active
- Ring buffer retention of at least 90 seconds in memory
- Incident materialization with 30 seconds before and 60 seconds after a marker
- Automatic incident marking when a frame crosses a relative stutter threshold, so an unattended session still produces evidence
- PresentMon 2.x integration with per-frame CPU/GPU attribution, plus safe fallback and automatic discovery
- GPU telemetry via NVML: utilization, VRAM occupancy, NVENC load and throttle reasons
- OBS websocket polling with safe fallback when OBS is absent
- System, process and basic network telemetry on a unified timeline
- Artifact import for `net_statsFile` CSV, profiler JSON, resmon/log evidence and ETL files
- Correlation engine that ranks likely causes with evidence and can say `insufficient evidence`
- ZIP export with JSON summary, CSV metrics, cleartext report and optional attachments
- Fake data generator for offline validation of the rule engine

## Root-cause categories

The analysis engine ranks these categories:

1. GPU/frametime contention
2. GPU VRAM pressure (texture eviction)
3. OBS/render/output contention
4. FiveM resource/script spike
5. Network jitter/packet loss/routing issue
6. Streaming/disk stall
7. External process interference
8. OS/driver latency
9. Possible cache/resource corruption

### External interference is ranked by how much of the machine it took

Counting suspected processes said the same thing about two idle overlays as about a window where
OneDrive held 3.68 of eight physical cores — more CPU than the game — with 87% of every file operation on
the machine and the game's render thread sharing a physical core 86% of the time. That incident was
ranked a FiveM script spike at 60%, with external interference second at 51%, because the category's score
was a function of *how many* neighbours were noticed and never of how large they were.

Three measured terms now weigh in, and all three are conditional on the state that makes interference cost
frames at all. The machine has to be saturated — with cores to spare the scheduler simply runs both, so a
busy neighbour on an idle machine is evidence of nothing. The suspects' CPU **at one instant** is compared
against the game's peak, both as a percentage of the whole machine from the same counter, so "the
background took more than the game" is a statement the engine can now make. Background disk throughput past
200 MB/s adds a smaller term, because volume is not latency but a sync queue moving that much is also
thousands of file system operations a second contended for in the kernel.

The concurrency matters and the first version of this got it wrong. Each suspect's peak is a maximum over
a ninety second window, so adding the peaks together measures a load the machine need never have carried —
a sync service busy at the start and a browser busy at the end would sum to a figure that beats the game
without the two ever having run together. The comparison is a claim about a moment, so it is measured in
one: the busiest single sample, deduplicated across the sample's CPU and disk lists. The game is still
represented by its *peak*, which is the reading most favourable to it and makes clearing the bar the
conservative version of the claim.

### How a slow frame is attributed

With PresentMon `--v2_metrics` every spike is classified using the per-frame CPU/GPU breakdown rather
than frame time alone:

| Signature | Interpretation |
| --- | --- |
| `MsCPUBusy` dominates | FiveM script/resource work or CPU contention |
| `MsGPUBusy` dominates | GPU contention, e.g. NVENC encoding against the game |
| Neither is busy | Present/display path stalled — VRAM eviction, DPC/ISR latency or composition |

**`MsCPUBusy` is not a measurement of execution, and a trace overrules it.** PresentMon derives the
figure from the gap between presents, so a main thread blocked on a lock reads exactly like one running
script: a 586 ms frame reported 585.2 ms of CPU busy for a thread the ETL shows off the processor for
568.9 of them. Where a deep capture covers the frame and shows an active game thread waiting, the
CPU-bound attribution is **not counted as evidence of script work at all** — not counted and then capped,
which was the earlier shape of this rule and still ranked the hypothesis first in 190 of one session's
198 incidents. The same reading is likewise barred from *ruling out* a storage stall, since a thread
blocked on a disk read produces it too.

The contradiction is **proportionate, not boolean**. An incident window is ninety seconds and can hold
more than one cause, so how much of the window's CPU-bound spike time the waits actually cover is measured
rather than whether one of them touched a slow frame. A 120 ms wait in a window that lost 1 750 ms to
CPU-bound spikes explains 7% of it and discards nothing; the same rule discards the attribution when the
waits cover most of that time. Treating any overlap as decisive would hide whatever else was in the window.

**Without a trace or a profiler, the attribution is a lead rather than a verdict.** Nothing else in the
ordinary telemetry measures execution: the per-core counters sample once a second and a pinned core is
FiveM's main thread on any evening at all, and the process CPU figure is an average over the same second.
So a `FiveMResourceSpike` ranking whose only positive evidence is the frame breakdown is capped at 34%,
below the 35% bar that promotes a hypothesis past "insufficient evidence" — the same treatment the storage
verdict resting on throughput already gets, and for the same reason. One session ranked 151 of its 154
incidents as script spikes at 80% confidence, every one of them on a figure that reads identically for a
thread executing and a thread asleep, and the single freeze that had a trace shows the thread asleep. The
cap lifts as soon as something measures execution: an imported profiler snapshot, or the deep capture the
app now reads back into the incident itself.

PresentMon's `PresentMode` and `MsBetweenDisplayChange` are read alongside these and reported in the
summary, the timeline and the exported metrics. The mode is the only column that says *how* a frame
reached the screen — a window where every frame sat in `Composed: Copy with GPU GDI` is a machine
compositing through DWM rather than getting an independent flip, which costs latency on every frame and
is invisible in frame time. Display-change cadence separates "frames were slow" from "frames were on
time and the screen did not update", and the two need different fixes.

### A verdict never outranks the measurement behind it

Disk counters (`\PhysicalDisk\Avg. Disk sec/Transfer`, `Current Disk Queue Length`, `\Memory\Pages
Input/sec`) can fail to open, and used to fail in complete silence — the analysis then fell back to raw
process throughput and still reached 88% confidence in a storage stall, for an incident whose ETL held
five disk operations and three hard faults. Two rules now apply:

- The system collector **reports at session start** which counters opened and, for each that did not, the
  reason. It reports again if a counter opened but produced no value across the first twenty reads. Both
  go to the session journal, so whether the counters worked is answerable from the log alone.
- A hypothesis resting on a fallback rather than on the measurement it substitutes for is **capped below
  the 35% bar** that promotes a hypothesis past "insufficient evidence". Throughput cannot distinguish a
  busy disk from a slow one, so it produces a lead worth checking, not an answer that ends the
  investigation.

The same rule governs `MsCPUBusy`, which is derived from the gap between presents and so reads
identically for a thread executing script and a thread asleep on a lock. When an attached trace shows
the game's thread off the processor for the frame, that reading stops counting as evidence of script
work entirely rather than being counted and then capped.

**And the frame's own `MsCPUWait` bounds the trace in return.** A trace can only show that *some* thread
waited; the frame says whether the frame waited. On 30 August the engine ranked an incident
`FiveMThreadWait` at 98% confidence from a wait on a worker thread that was waiting for the main thread,
while all 35 of that evening's frames over 100 ms reported between 0.1 and 0.4 ms of `MsCPUWait`. So a
window whose large frames carry the column and show under a millisecond of wait **cannot rank a
thread-wait verdict highest**, whatever the attachment shows for other threads. The consequence is worth
stating plainly: on this machine `MsCPUWait` reads ~0.1 ms even for frames whose trace shows the game's
own thread off the processor for the whole frame — verified at 0.0737 ms for the 261.6 ms frame of
30 August — so the rule also demotes that case. Neither verdict then tops the ranking: the script spike
is still contradicted by the trace, the wait is bounded by the frame, and the incident is reported as
unresolved rather than as a confident answer built on an instrument that cannot tell the two apart.

Correlating the two instruments needs care, because **they do not share a clock**. PresentMon reports
frame times relative to its own trace and the collector recovers wall clock as
`min(readUtc - relativeMs)`, an estimate that can only ever be late: `readUtc` is taken after PresentMon
buffered the row, wrote it and the poll loop read it, so the anchor keeps the smallest pipeline latency
of the session as a standing bias. Measured on six incidents of the 29 August session that carried both
a marker and a trace naming the wait behind it, the marker ran **1.17 to 1.32 seconds late** every time.
An earlier 50 ms tolerance therefore rejected all six, and `FiveMThreadWait` was never once ranked in
the field across nine sessions while the reports kept concluding a script spike from `MsCPUBusy`.

Matching is now on **duration first and clock second**: a wait explains a frame when it accounts for
50–150% of what that frame lost, and falls within three seconds of it. The duration test is much the
stronger of the two — every genuine match in that session lands within a few percent (245.8 ms of wait
against 245 ms of lost frame, 250.0 against 249, 197.2 against 199, 120.9 against 122, 117.7 against
119) — and it correctly rejects the one traced hitch that was not a wait, a 252 ms frame where
`adhesive.dll` held 3.58 cores and the wait covers only 71.4 ms of it.

### Automatic incident marking

Relying on the user to notice a hitch and hit a hotkey samples the problem at a few percent, and biases
that sample towards whatever they happened to be looking at. A six hour session in the field produced an
estimated thousand hitches against a single manual marker.

The detector therefore marks incidents on its own, using the same relative baseline as the analysis
engine — the rolling median frame time over the last ~10 seconds, floored at the display refresh
interval:

| Rule | Default | Severity |
| --- | --- | --- |
| Frame time ≥ `SpikeMultiplier` × baseline | 2.0× (33 ms at 60 fps) | Normal |
| Frame time ≥ `SevereMultiplier` × baseline | 4.0× (67 ms at 60 fps) | Severe |
| `DroppedFrameRun` consecutive undisplayed frames | 3 | Normal |

Guard rails, all configurable under `AutoDetect` in `settings.json`:

- **Cooldown** (2 minutes) — incident windows span 90 seconds, so anything shorter produces incidents
  that mostly re-describe each other's telemetry.
- **MaxIncidentsPerWindow** (20) over **IncidentBudgetWindow** (1 hour) — each incident retains its full
  event window in memory. The budget is time-windowed rather than session-wide: a session-wide ceiling of
  40 was exhausted after 1 h 42 min of a 3 h 57 min stream, leaving the detector disarmed for the whole
  second half. A settings file that still carries the old `MaxIncidentsPerSession` has its value migrated
  into `MaxIncidentsPerWindow` on load.
- **MinimumSamples** (120) — nothing fires before the baseline settles, so a level load is not a stutter.

Every value is clamped when settings are read and written. A hand-edited file with `SpikeMultiplier: 0`,
`DroppedFrameRun: 0` or `Cooldown: 0` would otherwise trigger on nearly every frame, each trigger
snapshotting a 90 second window; `BaselineWindowFrames` is capped because the detector allocates arrays
of that size up front.

Warnings are also surfaced in the app's header rather than only in the status list, which scrolls. The
banner shows the latest line from each collector whose most recent word was a warning, so a collector that
cannot measure stays visible until it says otherwise and clears itself when it recovers.

`MaxIncidentsPerWindow` bounds the rate, so the top-level **MaxRetainedIncidents** (50) bounds the
history across all sessions. Beyond that the oldest incidents are dropped from memory and from the list —
exported bundles are unaffected.

Auto-marked incidents **may trigger deep capture, but only within a budget**. Saving the ring buffer
writes a multi-hundred-megabyte ETL and empties the buffer, so this used to be refused outright — until a
five and a half hour session raised eighteen severe incidents and produced no trace at all, leaving every
one of its hitch clusters unexplained. Three gates now decide, and all three have to pass: the frame has
to reach **DeepCapture.AutoCaptureFrameTimeMs** (120 ms), captures are spaced by
**DeepCapture.AutoCaptureCooldown** (10 min) so a burst spends one rather than twenty, and
**DeepCapture.MaxAutoCapturesPerSession** (12) is a hard ceiling. Set `DeepCapture.CaptureAutoIncidents`
to `false` for the old behaviour.

The threshold was 300 ms, which was right for the sessions it was calibrated against and went blind the
moment they improved. A seven hour session contained exactly one frame over 300 ms — the game's own
restart, not a hitch — so two captures were taken in the first ninety minutes and none at all in the last
three and a half hours, while the 67 frames that were the entire remaining problem sat at 100–170 ms.

Lowering the threshold alone would have made the opposite mistake, and the fix is the same one
`MaxIncidentsPerWindow` applies a level up. Against those real frames, a flat 120 ms threshold with only a
session ceiling spends all its captures before the ninety minute mark — 43 of the 67 fell inside fourteen
minutes of the opening hour, a cache rebuilt after a settings change with a sync backlog on top — and
records nothing for the remaining five hours. **DeepCapture.MaxAutoCapturesPerWindow** (3) over
**DeepCapture.CaptureBudgetWindow** (1 h) rations the ordinary frames instead, which against the same
session spreads six captures across the whole evening and picks up its largest late frame. A frame past
the override threshold below ignores the window budget, answering to the session ceiling and the ring
buffer only — and does not spend the window's slot either, since charging it would let one catastrophic
frame buy silence for the rest of the hour, which is the failure the window exists to prevent arriving by
the one path that is exempt from it.

One per hour was the next thing to become the binding constraint. The session after the threshold was
lowered refused twenty-four hitches, every one of them with "1 capture(s) per 60 min are already taken",
and among them the evening's second largest frame at 484 ms — the only large frame of that night with
neither an explanation nor a trace. Three an hour cannot produce more than three against the ten minute
cooldown, so the cooldown still decides the volume and the window budget decides only that a quiet hour
does not lose its slot to a loud one.

The spacing gate has one way past it. A frame beyond **DeepCapture.AutoCaptureOverrideFrameTimeMs**
(250 ms) spends budget the cooldown would have withheld, because the ceiling was always meant to be the
binding constraint and a session that turned away its third and fourth largest frames while holding an
unspent capture had those the wrong way round. What the override cannot skip is the ring buffer
refilling: **DeepCapture.AutoCaptureOverrideCooldown** (60 s) covers the 28-32 s a capture takes to
finish writing plus the ~21 s the buffer needs to refill, so an override never records a nearly empty
ring. It is raised automatically for a larger `RingBufferMegabytes`, which refills proportionally more
slowly — at the 2 048 MB ceiling the refill alone is about 56 s. Setting the override threshold to the
ordinary one turns the override off.

**And the refill itself has one way past it, for an extreme frame.** Four sessions running lost their
largest frame to that wait — most recently 356 ms at 20:23:50, refused with "43 s left before the ring
buffer has refilled", in the opening ten minutes of a session where the buffer never catches up at all. A
frame at or above the configured `AutoCaptureOverrideFrameTimeMs` is traced against a half-filled buffer
instead, because several seconds of run-up beats none and the frame is not coming back. It still waits for
the previous capture to finish writing its own file — the tail plus the ~30 s `wpr -stop` takes — and it
must be **larger than the frame the previous capture was taken for**, so an unbroken patch of 900 ms
frames spends one capture per refill rather than one per write.

**Both thresholds follow the session's own frames upwards.** A constant is right for the sessions it was
calibrated against and wrong for the next ones, which has now happened twice: 300 ms became 120 after one
evening improved past it, and 500 ms became 250 after the next one did, each discovered by counting frames
by hand a session later. **DeepCapture.AdaptiveThresholdFramesPerHour** (20) and
**DeepCapture.AdaptiveOverrideFramesPerHour** (3) say how many frames an hour may exceed each threshold
before it moves to where the session's own material is: at three an hour after two hours, the sixth
largest frame of the session is the level three an hour have reached. It does nothing until the session
has produced at least three frames above the level, since the largest single frame of a session is one
event rather than an estimate of anything. Set either rate to `0` to pin the constant.

The ordinary threshold only ever *raises*; the **override also follows the session's frames downwards**,
which four notes running asked for. The constant sits at 250 ms, an evening's largest frames come in at
150–240, and the exception then turns away the very frames it exists for while the budget still holds
unspent captures — a 235 ms frame, the evening's second largest, refused by ten minutes of cooldown. The
rate is what bounds the lowering: at three an hour the level is where three frames an hour actually
reach, so it can never admit more than the budget was sized for, and it may not fall within 25% of the
ordinary threshold, where the exception would swallow the rule.

**An automatic capture's ETL is read back into the incident that triggered it.** Without this the traces
exist and the analysis never sees them: the rule that a trace overrules `MsCPUBusy` was implemented for
three sessions and fired in none of them, because the file was attached to the incident and never handed
to a parser. One session wrote five ETLs and not one of its 154 incidents carried a line of ETL evidence,
while 151 were ranked as script spikes — including the freeze whose own trace shows the main thread off
the processor for 178.0 of its 178 ms. The parse costs a burst of CPU on one core while the session is
still running; set `DeepCapture.AnalyzeAutomaticCaptures` to `false` to go back to importing traces by
hand.

A sustained saturation window can spend a capture too, even with no remarkable frame in it — that is the
condition a frame time threshold structurally cannot see, and it was 104 of 391 minutes in one session.
Manual `Severe` markers still trigger a capture outside the budget.

A frame that crosses a threshold while an incident is already open **escalates that incident** rather than
being discarded: the marker is renamed after the worst frame the window actually contains, and its severity
can only rise. Without this the window was named after whichever frame happened to open it, which is
systematically the smallest one. A sustained saturation incident is exempt, since its label already says
more than any single frame inside it could.

Manual marking is unchanged and still worth using: it records that a human *perceived* something, which
the telemetry alone cannot establish.

### Spike thresholds are relative, not fixed

Stutter is deviation from the cadence the machine is achieving, so thresholds derive from the observed
median frame time and the display refresh interval (whichever is larger), not a hardcoded 25 ms. A
120 Hz display running at 120 fps flags spikes from roughly 12.5 ms; a 60 Hz one from roughly 25 ms.

### Frames are timed twice: when the game presents them, and when the screen shows them

A frame time of 16.67 ms means the game produced the frame on time. It does not mean anyone saw it on
time, and for eight sessions of one investigation those two answers disagreed while only the first was
computed.

`DisplayCadenceMonitor` rounds every `MsBetweenDisplayChange` to a whole number of display refreshes
and reports the share that did not land on the cadence the session actually holds. Counting refreshes
rather than milliseconds is what makes the figure comparable across displays: a 60 fps game changes the
screen every two refreshes on a 120 Hz panel and every one on a 60 Hz panel, and a frame that slips is
one refresh out in both cases — but 8.3 ms in the first and 16.7 ms in the second.

Measured across the investigation, with the presents shown alongside for the two sessions that have
both:

| session | presented on cadence | reached the screen on cadence |
|---|---:|---:|
| 21 Aug | — | 87.2% |
| 22 Aug | — | 79.0% |
| 23 Aug | — | 85.6% |
| 24 Aug | — | 88.9% |
| 25 Aug | — | 89.4% |
| 26 Aug | — | 86.3% |
| 27 Aug | — | 89.5% |
| 28 Aug | 97.2% | 88.7% |
| 30 Aug | 98.3% | **99.6%** |

On 28 August 5.8% of frames reached the screen a refresh **early** and 5.5% a refresh **late** — an
oscillation around the right moment, about one frame in nine, all evening, invisible in frame time
because the frames themselves were on time. The cause was the game's blt swapchain being composed by
DWM while two monitors ran at different refresh rates, which forces the compositor to resample. Setting
both displays to the same rate took the figure to 0.41% and halved the hitch rate at every threshold.

So `RefreshRateMismatch` also checks the attached displays at session start and says so when their
rates differ by more than 10%, which is the one finding of that investigation that is fixed before
playing rather than analysed afterwards. `EnvironmentMetadata.Displays` records the whole set, because
the previous field — the primary display's rate alone — could not express the problem.

### Frame pacing is measured separately, and absolutely

A relative threshold cannot see a slow degradation, because its baseline moves with the damage. In one
6.5 hour session the machine spent 104 of 391 minutes unable to hold 60 fps — median frame rates of
37–52 in blocks half an hour long — and the spike detector raised almost nothing for it, since a
baseline that had drifted to 20 ms puts the 2x bar at 40 ms.

`FramePacingMonitor` classifies the session a minute at a time against two things that do not drift:
how much idle time the frame pipeline had left (`MsCPUWait` — a frame rate cap that is being met is
several milliseconds of wait per frame, and a collapse towards zero means the CPU has become the
limit), and the best cadence this same session has been shown to sustain, which only ever ratchets
down. Every window is written to the session journal, healthy ones included, because the share of an
evening that could not hold its frame rate is only computable with the good minutes on record next to
the bad. Incidents are raised at the transition into a bad patch and then on a reminder cadence.

Replayed over that session it classifies 91 of 390 minutes as saturated, finds a 27 minute unbroken
run and a worst minute of 37.3 fps, and raises 14 incidents rather than 180.

## Solution layout

- `src/FiveMDiagnostics.App.Wpf`: desktop UI, tray mode and app composition
- `src/FiveMDiagnostics.Core`: domain models, settings, interfaces, ring buffer, incident materializer
- `src/FiveMDiagnostics.Collectors`: session orchestration and local collectors
- `src/FiveMDiagnostics.Analysis`: correlation engine and artifact parsers
- `src/FiveMDiagnostics.Export`: incident bundle export
- `src/FiveMDiagnostics.Integrations.PresentMon`: PresentMon-backed frame telemetry collector
- `src/FiveMDiagnostics.Integrations.Nvml`: NVML-backed GPU/VRAM telemetry collector
- `src/FiveMDiagnostics.Integrations.Obs`: raw `obs-websocket` polling
- `src/FiveMDiagnostics.Integrations.Etw`: WPR deep capture and ETL parsing
- `src/FiveMDiagnostics.Fakes`: simulated incident scenarios
- `src/FiveMDiagnostics.Tools.EtlAnalyzer`: offline command line reader for deep capture ETLs
- `tests/FiveMDiagnostics.Tests`: acceptance-oriented tests

## Requirements

- Windows 10/11
- .NET SDK 10.0.104 or later
- Optional: PresentMon executable for frame telemetry
- Optional: OBS Studio 28+ with `obs-websocket` enabled
- Optional: `wpr.exe` available in `PATH` for deep capture mode

## Build and run

```powershell
dotnet build FiveMDiagnostics.slnx
dotnet run --project src/FiveMDiagnostics.App.Wpf/FiveMDiagnostics.App.Wpf.csproj
```

Run tests:

```powershell
dotnet test FiveMDiagnostics.slnx
```

Read a deep capture ETL:

```powershell
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> cpu
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> thread --tid 24096
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> io
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> wait --min-ms 100
dotnet run --project src/FiveMDiagnostics.Tools.EtlAnalyzer -- <trace.etl> cpu --from-ms 20000 --to-ms 23000
```

The app's own ETL parser answers whether a trace is usable and whether a driver held the CPU. The tool
answers what comes next: which thread inside the game was the bottleneck, what code it was running,
whether it was sharing a physical core, and whether the file system traffic ever reached the disk.
`wait` answers the question a CPU report cannot: when the game thread is asleep for most of a long
frame, which thread released it, and which module it was blocked in. It needs a capture recorded with
`CSwitch` and `ReadyThread` stacks, and it reads the trace with TraceEvent because TraceProcessing
rejects the context switch stream in these ring buffer captures.

`wait` follows the **release chain past the first link**, because the first link is rarely the answer.
Across three sessions the thread that released the game's main thread was a near-idle synchronisation
thread inside `gta-core-five.dll` — 0.03 cores, no work of its own — which was itself waiting on the
render thread for the same interval:

```text
    release chain
      tid 25872  (FiveM_b3407_GTAProcess.exe)  waited    568.9 ms
      tid 20648  (FiveM_b3407_GTAProcess.exe)  waited    568.4 ms  ← blocked in FiveM_b3407_GTAProcess.exe
      tid 13924  (FiveM_b3407_GTAProcess.exe)  did not wait — on the processor for all of it, this is where the chain ends
```

The walk stops at the first thread with no wait of its own covering the interval, which is the thread the
report exists to name: it is on the processor while everything behind it is not. Reaching it used to be
three manual invocations of this command, every session since 25 August. A link has to be off the
processor for most of the wait it explains, so a thread that merely parked nearby does not join the chain,
and a cycle, an unnameable waker or a depth of eight ends the walk.

A **DPC** ends it too. The classic kernel `ReadyThread` event names no thread, and the thread the context
switch stream shows on that processor is merely the one the interrupt suspended — so the chain says the
wake came from a DPC on that CPU and stops, rather than stepping onto an uninvolved thread and continuing
from there. Where a link was taken from the processor rather than from a recorded `ReadyThread` stack it
is marked `(inferred from the processor)`, because that step holds for an ordinary user mode wake and is
still an inference.
`--from-ms` and `--to-ms` zoom any CPU or thread report into offsets from the first retained CPU sample,
which keeps a multi-second wait from being averaged away by the rest of the ring buffer.
Rates are reported in *cores*, so a thread at 0.89 cores is spending 19.6 ms of CPU inside a 22 ms
frame — which is the number that explains a frame rate.

## Configuration

Settings are stored locally at:

```text
%LocalAppData%\FiveMDiagnostics\settings.json
```

The UI lets you edit:

- optional server name used in exports and incident labels
- optional ping host/IP for lightweight RTT probing
- optional endpoint label for detected connections
- language
- advanced PresentMon/path settings when needed
- export redaction toggles

### PresentMon notes

The collector is designed to be resilient if PresentMon is not installed or not configured.

If no path is configured, the app first tries to find `PresentMon.exe` through `PATH` and common install
locations, including versioned filenames such as `PresentMon-2.4.1-x64.exe`.

Default argument template:

```text
--process_id {processId} --output_stdout --no_console_stats --stop_existing_session --terminate_on_proc_exit
```

Note the absence of a metrics flag. PresentMon 2.4.1 emits **three different column schemes**, verified
against live captures:

| Invocation | Time column | Frame time | CPU/GPU split |
| --- | --- | --- | --- |
| default (no flag) | `TimeInMs` | `MsBetweenPresents` | `MsCPUBusy`, `MsGPUBusy`, `MsGPUWait` |
| `--v2_metrics` | `CPUStartTime` | `FrameTime` | `CPUBusy`, `GPUBusy`, `GPUWait` |
| `--v1_metrics` | `TimeInSeconds` | `msBetweenPresents` | none |

The default is a **superset** of `--v2_metrics`, so passing that flag gains nothing and only narrows the
output. All three schemes report the relative time in milliseconds (`TimeInSeconds` excepted), and the
parser handles all three — a hand-edited template will not silently produce zero rows.

`--stop_existing_session` matters because a PresentMon that was killed leaves its ETW session behind, and
the next capture would otherwise refuse to start. `--terminate_on_proc_exit` is a no-op unless the app
runs elevated; the collector stops the capture itself when the target process disappears.

Settings written by an older build are migrated to the template above automatically on load. The app
drains stdout continuously and writes its own raw CSV copy in the session directory. It never needs to
open PresentMon's live output file, so a transient file-sharing lock cannot terminate frame collection.
Each PresentMon process owns a separate bounded stdout buffer (8,192 rows); delayed callbacks from a
retired process are rejected, and overflow is reported and recovered through the restart backoff instead
of allowing memory to grow without limit.

Timestamps in the CSV are relative to the start of the PresentMon trace. The collector anchors them to
wall clock by tracking the tightest observed bound across batches, so frames land close to their real
position on the timeline instead of collapsing onto the moment they were read.

The anchor is **biased late, and cannot not be**. It is `min(readUtc - relativeMs)`, and `readUtc` is
taken after PresentMon buffered the row, wrote it and the poll loop read it — so the estimate carries
the smallest pipeline latency of the whole session as a standing offset. Measured against ETL
timestamps, which have no such offset, on six incidents of the 29 August session: **1.17 to 1.32 seconds
late**. That is invisible while frames are only compared against each other, and it silently broke every
correlation against a trace until the analysis stopped relying on the two clocks agreeing (see
[A verdict never outranks the measurement behind it](#a-verdict-never-outranks-the-measurement-behind-it)).
Anything correlating a frame against another instrument has to allow for it.


#### Capture health

A capture that dies while the game is still running used to restart in silence, and a six hour session
was found to have produced a 0.77 second CSV. The collector now watches for both failure modes and
reports them to the status log:

- PresentMon's process exiting on its own while FiveM is still running.
- PresentMon still running but producing no frames for 15 seconds — it can lose its ETW session, or have
  it taken by another tool using the same session name, without its process exiting.

Either case restarts the capture, but silence is ambiguous: an alt-tab, a minimised window or a loading
screen presents nothing either. Restarting on a flat 15 second timer therefore meant a paused game got a
kill-and-respawn every 15 seconds, which costs more ETW churn than the frames it recovers. So:

- The tolerated silence **doubles after every restart** (15 s, 30 s, 60 s … capped at 4 minutes), and the
  same ladder spaces the restarts themselves out — a PresentMon that exits instantly can no longer be
  respawned once per polling interval.
- A CSV that grows without yielding parsable samples counts as alive, so an unusable batch is not read as
  a dead ETW session.
- After **five** fruitless restarts, automatic restarts are suspended and reported as `Error`. They
  resume when FiveM is restarted (a new process id) or a new session is started. A mute capture that is
  left running recovers on its own as soon as frames return.
- A capture that then runs healthily for two minutes clears the restart counter, so the ladder describes
  the current problem rather than the whole session.
- A low-rate health snapshot records frame count, observed time range, largest gap, continuous pre-buffer
  span and total restarts. Incident exports state whether both the 30-second pre-window and 60-second
  post-window were covered.

From the third restart onwards the status entry escalates to `Error`, because at that point the session's
frame data should be treated as incomplete.

### GPU telemetry notes

GPU sampling uses `nvml.dll`, which ships with the NVIDIA display driver and needs no separate install.
On a non-NVIDIA machine the collector reports once that NVML is unavailable and then emits samples marked
unavailable, leaving every other collector untouched.

### Per-process VRAM

NVML reports occupancy for the whole adapter, which is enough to find that stalls cluster above a VRAM
threshold and not enough to act on it: the fix differs entirely depending on whether the game, the
capture software or a browser owns the last gigabyte. A second collector therefore samples the Windows
`GPU Process Memory` counters — the same source as Task Manager's per-process column — every five
seconds, and the incident report names the top holders instead of carrying the old caveat that VRAM
cannot be attributed.

Read through PDH rather than `System.Diagnostics.PerformanceCounter`, because a wildcard instance set
has to be expanded into one counter object per process otherwise, and via `PdhAddEnglishCounter` so the
paths resolve on a non-English Windows. NVML is not an option here at all: its per-process memory
queries return "not supported" for graphics processes on a WDDM driver, which is every consumer machine.

Figures are summed per process across the counter instances of **one adapter** — the one the game
itself holds memory on. More than one adapter is the normal case rather than the laptop case: a
single-GPU desktop enumerates two distinct adapter LUIDs, and summing across them produced a reading of
213 GB on a 10 GB card that stood as "largest VRAM holder" in 145 incident reports before it was caught.
A per-process figure that is still impossible for any adapter is kept in the log, named in a warning, and
left out of the reports' top list. That warning now lists the counter instances behind the row — each
one's adapter LUID, dedicated bytes and shared bytes — rather than only how many there were. The count
was added to prove the total came from summing instances and on its first outing disproved it: obs64
reported 209 GB for a whole session with an instance count of **one**, so the runaway sum was never the
explanation and a single number could not say what was.

The table is also reconciled against the adapter's own figure, once at session start and every thirty
minutes after. The two come from different sources and are not expected to match exactly — VRAM holds
framebuffers belonging to no running process, so the sum normally sits a little under NVML's figure — but
a sum *above* what the card reports as used is impossible and means a row is double counting. It happened:
across two consecutive sessions on one machine the gap went from +0.17 GB to +1.11 GB, every individual
row still looked reasonable, and it was found by exporting both logs and doing the arithmetic by hand a
session later. Beyond half a gigabyte of overshoot the line is logged as a warning.

Two things the comparison has to be careful about. It sums **every** process on the adapter rather than the
top-N the table reports, because double counting in a process below the cut would otherwise be invisible to
a check that only ever looks at the biggest. And it only ever *accuses* on a single-GPU machine: NVML reads
device index 0 for the whole session while the process table is anchored on whichever adapter the game
holds memory on, so with a second NVIDIA device present those need not be the same card and their
difference would be a fact about the hardware rather than about the accounting. The line is still written
in that case, with the caveat, and never escalates to a warning.

**The same comparison names the row, one row at a time.** A single process cannot hold more memory than
the card reports as used, so a row that does is double counting by definition — and that catches the case
the absolute bound was always missing. `dwm` reported a flat 6.1 GB on a 10 GB card, cleared the 64 GB
bound comfortably, and stood as "largest in VRAM" in all 154 incidents of a session while hiding the
process that was actually growing; the arithmetic that exposed it was FiveM at 5.9 GB plus dwm at 6.1 GB
on a card reporting 7.8 GB used. It is expected of the compositor specifically and understood — in
`Composed: Copy with GPU GDI` DWM holds a reference to every frame it composes and the counter does not
distinguish a shared allocation from an owned one — but the rule is written against the arithmetic rather
than the name, because the next compositor-shaped process will not be called dwm. The proof does not
expire: a row that exceeded the card's figure once is excluded from the reports for the rest of the
session, named in a warning when it is first proved and again in every reconciliation line, and kept in
the log. Deciding it per sample instead would have excluded that row from the first incident of the
evening and named it largest holder in the other 153, since it only exceeded the card's own figure while
the game was still filling its texture memory.

**The three figures are also stated as a budget.** Once the session has both readings and the game has
allocated something, one line says what the card's memory is committed to before the game asks for any —
"skrivbordet håller 1.1 GB och streamstacken 1.0 GB, så spelet har 7.9 GB kvar av kortets 10.0 GB" — and
it is written again whenever the stream stack starts or stops, which is the only term of the three that
moves during an evening. This is the one figure in six sessions of investigation that predicted a session
rather than describing one: the game fills its texture memory to a ceiling set by the graphics preset and
stops there, so 7.2 GB of game at High plus 2.1 GB of everything else is 9.3 of 10 GB, and that session
measured a VRAM median of 88.1% with 4.24% of it above 93%. The same subtraction at Medium predicts 82%
and the session measured 77.6%. It also names the lever: the game's ceiling costs image quality to move,
while the other two gigabytes are programs. The desktop figure is taken as the card's own number minus
the game's row rather than as a sum of the other rows, so the double counting above cannot reach it.

**An excluded row does not leave the budget with it.** Excluding a proved double-counter from the top
lists is right; letting the exclusion remove the memory as well is not, because the process still holds
whatever it holds and the card is still counting it. That is how one evening produced "the game has 8.1 GB
left of the card's 10.0" while the card itself stood at 88–92%: obs64 had been excluded, so the stream
stack was booked at 0.1 GB instead of about 1.3 and the difference silently became headroom. A stream
stack that cannot be measured per process is now measured **by difference** — the desktop from the rows
that can still be believed, and everything the card says is missing booked onto the stack, stated as
such: "the stream stack cannot be measured per process; the card is missing 1.3 GB and they are booked
there". The line also states what the card reports as used and free, and the room it offers the game can
never exceed total minus used, whatever the table claims.

**The stream stack's start and stop need hysteresis.** Eighteen "the stream stack started/stopped" lines
were written between 21:48 and 22:04 on an evening when OBS neither started nor stopped: the row was
alternating between believable and excluded, and each flip restated the budget as though a gigabyte had
moved. A change is now written only after three consecutive samples agree — fifteen seconds at the
process cadence — and while the stack's rows cannot be believed at all, the state is held rather than
decided from a row already known to be broken.

Two things it refuses to guess at. On a machine with **more than one GPU** no budget is stated at all:
the subtraction would be one card's total minus another card's row, and unlike the reconciliation — which
publishes with a caveat because nobody acts on it directly — this is the number a graphics preset gets
chosen against, where a wrong figure with a footnote is worse than no figure. And because the process
table is cut to `Gpu.ProcessMemoryTopCount`, bytes held by processes below the cut land in the desktop
residual; the total the game does not get stays exactly right either way, but the **split** between
desktop and stream can be understated, so the line says how much is down there when there is enough of it
to matter.

**The table is also live in the window, and it notices a step change.** The status line in the header
names the two largest holders, which answers "is the card full" but not "what do I close" — the second
question was only ever answerable by reading the process CSV the following day. `LiveVramTracker` keeps
what each process held when the session started alongside what it holds now, so growth reads at a glance,
and it says so when a process takes a large amount of the card at once. The alarm is on **rate**, not on
size, because the game's own row grows too: FiveM fills its texture pool at about 2.7 MB/s at its fastest
measured, while `Voicemod` sat flat at 669 MB for three hours and 47 minutes and then took 734 MB in
twenty seconds — 36.7 MB/s, thirteen times faster — and never gave it back. A threshold on bytes would
have to be set above the pool fill and would then miss the step. The report waits for the climb to stop
before naming it, so the line carries the whole 734 MB rather than the 430 it had reached when the
threshold was crossed. Rows the session has proved impossible stay in the view and stay labelled, since
this table is where a process claiming 39.9 GB of a 10 GB card gets noticed in the first place.

Turn it off with `Gpu.ProcessMemoryEnabled`; `Gpu.ProcessMemoryInterval` and `Gpu.ProcessMemoryTopCount`
(25) set the cadence and how many processes each sample keeps. The list is long because the question is
what holds the memory the game does not: nine processes held GPU memory for a whole measured session and
four more came and went, and at the original ten places the non-game total was a floor rather than a
figure.

### The app reports what it costs the session it is measuring

A deep capture ends by flushing roughly 900 MB of ring buffer to disk while the game is running, and the
app never said what that cost. Counted by hand on the 29 August session, the minute after each of its ten
flushes held hitches at four times the rate of the rest of the evening — 222 against 80 per hour at the
33 ms threshold, 96 against 22 at 50 ms — while the large ones were untouched at 6.0 against 6.4 per
hour. That is roughly 27 of the evening's 412 hitches being the instrument rather than the machine.

`CaptureCostMonitor` now measures it and writes it into the session summary. Part of the excess is not
the flush at all — a capture happens because a hitch happened, and hitches cluster — so the line states
what it measured and not what caused it. The point is that two evenings with different numbers of
captures are not comparable without it.

### The session summary states the card's band, the wait distribution and the engine's own ranking

Three numbers the 31 August review had to work out by hand out of the CSV, each of which the app already
had every input for, and each now a line in the session summary.

**The VRAM band, with its measured cost.** Every previous session warned only about processes whose VRAM
grew, which is silent on the evening where the memory was taken before the game started. `VramPressureBandMonitor`
buckets the session into minutes, counts the minutes whose adapter readings were mostly above 88% and
above 91%, and compares the hitch rate inside the band against outside it — "the card was above 88% in 33
of 278 minutes; in those minutes the hitch rate was 4.1× higher than in the rest". The 88% band is no
longer a guess from 26 August: it is these minutes compared with each other, and the gradient behind it
was 82 against 794 hitches ≥33 ms per hour. The hitch threshold follows the cadence the session actually
holds, as `CaptureCostMonitor`'s does.

**The `MsCPUWait` distribution of the largest frames.** "None of the 35 frames over 100 ms waited" was the
sharpest single observation of that review and is one subtraction away from data every session already
collects. It separates a blocked thread from a pipeline working flat out on one line, and it is the same
measurement that bounds the thread-wait verdict above.

**What the engine concluded, across the session.** The ranking lives inside one incident, so the only way
to see the distribution was to count lines in the jsonl. `IncidentVerdictTally` keeps the top-ranked
category per marker — per marker, so an incident re-analysed when its trace arrives is counted once
rather than twice — and writes the distribution at session end, with `GpuVramPressure` on a line of its
own: it was ranked highest in 26 of 119 incidents on 31 August and was right about the evening before
anybody looked, which is the first time the ranking has led to the answer on its own.

### The session records the game's own graphics settings

Every comparison in the investigation rested on a remembered setting, and the window mode took four
sessions to establish from an in-game menu that showed "Fullscreen" and sometimes "Fullscreen
(Borderless)" for the same configuration. It is one integer in the file the game writes.
`GameGraphicsSettingsReader` reads `Rockstar Games/GTA V/settings.xml` at session start and records the
values that cost VRAM or decide presentation, spelling out `Windowed` (0 exclusive, 1 windowed with a
border, 2 borderless). It is best effort and silent when the file is not found, since a wrong setting in
the record is worse than none. It is also **not complete**: FiveM's `Extended Texture Budget` is its own
setting and is not in that file, which matters because it was one of the two knobs moved on 27 August.

**The newest copy wins, and the line says when it was written.** Taking the first candidate path that
existed was wrong on exactly the machine this was written for: Documents is redirected into OneDrive, a
copy is routinely left behind on the non-redirected path, and the session then recorded settings nobody
had used for months with nothing in the line to show it. Every candidate that exists is now enumerated,
the one with the latest `LastWriteTimeUtc` is read, the older ones are named, and the timestamp is printed
— "senast skriven 2026-08-24 21:11, alltså före den här sessionen" makes the remaining failure
self-evident instead of silent.

**And it is re-read while the session runs**, every five minutes as well as at the end, reporting what
moved: `TextureQuality 2 → 1`, or a file that stopped being readable. At session end alone it would be
written on one evening in ten, because this machine is switched off rather than stopped through the app.
A change mid-session splits the telemetry into two configurations, which anyone comparing the evening
against another one has to know before doing the arithmetic.

## Capture depth

Every session runs the standard capture. The deeper WPR capture is switched on automatically by
`Mark Severe`; a settings checkbox can also opt normal *manual* markers into deep capture. Automatic
incidents can start WPR within the budget described above, which keeps a bad session to a handful of
traces rather than one every couple of minutes.

### Standard capture

- No admin required
- One-click start with the default settings
- Collects system/process/network telemetry
- Polls OBS if available, and **says so in the window when OBS is running and its WebSocket is not
  answering**. That state was already recorded — one session carried "process körs, WebSocket
  frånkopplad" and four empty fields in 130 incident timelines — and being recorded is not being noticed:
  the same session ran 5 h 47 min without a single OBS measurement, on the evening the stream was failing
  to start, and it was found the next day by reading incident reports. Render lag and skipped frames are
  the only view this app has of the stream's own health, the fix is two clicks in OBS, and it is only
  worth anything while the session is still running. Reported once after
  **Obs.ConnectionWarningDelay** (30 s), with the recovery reported once as well.
- Uses PresentMon only if it is configured or found automatically
- Intended to stay low overhead

### Deep capture

- **Runs as a continuous ring buffer, not a recording that starts at the marker.** WPR records into an
  in-memory ring from the moment the session starts; a marker stops it, which flushes the accumulated
  history to an ETL, and starts a fresh one. Starting at the marker meant the trace began after the
  interesting part was already over — by the time a human presses the key or the detector classifies a
  hitch, the frames that caused it are seconds in the past.
- **DeepCapture.RingBufferMegabytes** (768) decides how much run-up a marker can save. With scheduler
  stacks enabled, a 256 MB test retained about seven seconds (roughly 36.6 MB/s), so the default buys
  about 21 seconds. Automatic capture normally stops it at the hitch, and that size still outlasts a
  manual reaction. **DeepCapture.PostMarkerTail** (2 s) is how long the marker waits
  before stopping, so the recovery is in the trace too; it is short because the session keeps recording
  throughout it, and every second of tail displaces a second of run-up from the far end of the ring.
- Triggered automatically on `Mark Severe`; optionally by a normal manual marker when explicitly enabled
- **Requires the app to run as administrator.** `wpr.exe` cannot self-elevate, so the app checks up front
  and reports that clearly rather than failing part-way through and leaving a trace session running.
  Without elevation there is no ring buffer, and a marker falls back to recording forward from itself —
  which the resulting status entry says explicitly.
- **Records through a generated `.wprp`, not `GeneralProfile`.** The built-in profile enables syscall
  enter/exit tracing: 88 of 132 million events and about 5 GB of a 6.9 GB trace, none of it attributable
  to a thread. The generated profile asks for context switches, ready threads, sampled profiles, DPC/ISR,
  disk and file I/O, hard faults and resident set. CSwitch and ReadyThread stacks are on by default so a
  long `Wait/UserRequest` interval retains the call chain that initiated it; file-open stacks remain off.
  It is rewritten to the working directory each session so the buffer size follows the setting; point
  `DeepCapture.CustomProfilePath` at your own file to override it. If WPR rejects the generated profile
  the built-in `DeepCapture.Profiles` stack takes over and the status entry says so.
- ETL analysis reports DPC/ISR **durations**, not event counts: ten thousand short DPCs are normal, a
  single 8 ms one blocks the scheduler and stalls every thread at once
- ETL analysis reconstructs long off-CPU intervals for active GTA threads and reports their wait state,
  reason and duration. This prevents PresentMon `MsCPUBusy` from being mistaken for CPU execution when
  the thread actually spent most of the frame blocked in `Wait/UserRequest`.
- ETL analysis reports **the window the file actually covers**, not the span of its events. A ring
  buffer keeps its rundown and metadata events from the moment the session started, so last-minus-first
  measured the age of the ring rather than its contents — up to 2 919 s for a 768 MB buffer holding
  twenty seconds of history, printed as though the trace covered forty-nine minutes. The span is now
  taken from the streams that are actually continuous (context switches and CPU samples), written out
  next to the marker's own time — "the trace covers 20:23:31–20:23:52; the marker was at 20:23:48, inside
  it" — so whether an attachment is relevant is one line rather than an hour with the ETL open. The
  file's own span is kept as `fileSpanSeconds` and named as the ring buffer's age.
- ETL analysis also reports **per-stream coverage**. Context switches and stacks have been observed
  stopping at 23 of 54 seconds while `EventsLost` stayed at 0 — that counts events the consumer failed to
  drain, not a provider that went silent because another ETW session took the keyword. A trace with full
  duration and half the context switches looks healthy by every other statistic, so the parser measures
  when each stream was actually producing and warns when one ends early.
- Only one capture runs at a time. WPR records into a single machine-wide session, so a severe marker
  raised while a capture is in flight is recorded as an incident but does not start a second trace

For the next session there is no separate “collect CSwitch stack” procedure. Install/run the updated
build **as administrator**, start diagnostics before FiveM, and play normally. Keep the lowered distance
scaling/population density and paused OneDrive unchanged so the session is comparable. Use **Mark Normal**
whenever lag is perceived: that human annotation is valuable even if the frame stays below the automatic
threshold. Use **Mark Severe** for an obvious freeze or major hitch. Hitches above the automatic 120 ms
threshold save the relevant ETL by themselves, but automatic detection does not replace perceived-lag
markers. At session start the status log should say
`CSwitch-stackar aktiva`. Older saved 256 MB/5 s defaults are migrated once to 768 MB/2 s automatically.

All setup fields are optional. The app can start a session with the default paths and no network hints configured.

## Tray controls

- right-click the tray icon to start or stop a session
- right-click the tray icon to mark normal or severe stutter while a session is active
- right-click the tray icon to export the latest incident or reopen the main window

## Session journal

Every session appends a JSON Lines file to the working directory as it runs:

```text
%LocalAppData%\FiveMDiagnostics\Sessions\session_<yyyyMMdd_HHmmss>.jsonl
```

Incidents otherwise exist only in memory and only reach disk through an explicit export of a selected
incident, and the status list is capped at 200 entries and dies with the process. A six-hour stream can
therefore auto-mark dozens of incidents and leave behind nothing but a PresentMon CSV and whatever ETL
traces deep capture wrote — including, in the failure that motivated this, the status entries proving
the frame telemetry had died in the first minute, which is the very thing that explains the empty
history.

One JSON object per line, each with `type`, `timestamp` and `payload`:

| `type` | Payload |
| --- | --- |
| `session-start` | environment metadata and the settings that shape the evidence |
| `status` | one status entry: level, source, message |
| `incident` | marker, window, analysis summary, top hypotheses, suspected processes, timeline, per-source event counts, attachment names |
| `incident-update` | the same payload, re-written after an imported artifact added evidence and the analysis was re-run |
| `session-end` | incidents written |
| `journal-truncated` | written instead of `session-end` when the size budget is reached |

Notes:

- **Summary level only.** An incident's own event window is 90 seconds of frame samples; that belongs in
  an export bundle, not in a file appended to for six hours.
- **Per-source event counts are the point.** An incident window holding no frame samples is not a quiet
  incident, it is a broken PresentMon capture, and a summary reporting only what the correlation engine
  concluded hides that distinction.
- **Flushed per line**, because the failure it exists for is the app being closed or killed.
- **Append only.** An incident that changes later gets an `incident-update` line rather than a rewrite,
  so whatever reached disk survives a kill; a reader taking the last line per incident id ends up with
  the current state.
- **Bounded at 8 MB** for the file, including anything a session started in the same second already
  wrote to it. On reaching the limit a `journal-truncated` line is written and the file is closed — a
  journal that simply stops is indistinguishable from a crash.
- **Written unredacted**, like the raw captures beside it. It is local evidence, not a bundle meant to
  be handed to someone else; the redaction rules still apply to everything exported.

## Continuous GPU log

Alongside the journal, every session writes each GPU sample to a flat CSV:

```text
%LocalAppData%\FiveMDiagnostics\Sessions\gpu_<yyyyMMdd_HHmmss>.csv
```

Columns are timestamp, availability, adapter, GPU and memory-bandwidth utilisation, used/total VRAM and
the derived percentage, encoder and decoder load, temperature, and throttle reasons.

GPU telemetry otherwise survives only inside incident windows, and whatever the analysis did not fold
into a timeline string was gone: reconstructing how VRAM behaved across an evening meant taking 42
separate timeline strings apart by hand. At the default 500 ms cadence a whole stream costs a few
megabytes. Flushed per row for the same reason the journal is, bounded at 64 MB, and written unredacted.

The per-process breakdown is written next to it, one row per process per sample:

```text
%LocalAppData%\FiveMDiagnostics\Sessions\gpuprocs_<yyyyMMdd_HHmmss>.csv
```

Columns are timestamp, process id, process name, dedicated bytes, shared bytes and the number of
counter instances the figure was summed from. Long format rather than a column per process, because the
set of processes holding GPU memory changes during a session and a wide file would have to fix its
columns when the header is written.

## Export bundle

By default exports are written under:

```text
%LocalAppData%\FiveMDiagnostics\Exports
```

Each ZIP contains:

- `summary.json`
- `metrics.csv`
- `incident-report.txt`
- optional `artifacts/` directory when attachment export is enabled

Sensitive fields are redacted by default. Redaction covers the generated analysis text as well as the
structured fields — the server address appears verbatim in summaries, timeline entries and hypothesis
evidence, so redacting only the event fields would still ship it in prose.

## Notes on network evidence

Without a deep capture the app captures:

- TCP remote endpoints for the active FiveM/GTA PID
- UDP local ports for the active PID
- RTT probes to a configured host/IP, or — when none is configured — to the server address derived from
  FiveM's own TCP connection (preferring port 30120)

Windows exposes no per-socket remote peer for UDP, and FiveM carries gameplay over UDP, so the server is
inferred from the TCP connection to the same host rather than observed directly. This is enough to
separate many local frametime incidents from probable network incidents, but it is not a full packet
capture.

A host that answers no probe at all is given up on for the session after thirty consecutive failures —
most game servers do not answer ICMP, and continuing produces nothing but an explanation in every report
that no RTT was measured. The explanation belongs in the session log, once, where the collector writes it
when it gives up and names the host. An incident whose window happens to contain those first failures now
says so in one sentence rather than three: the advice to point `ServerProfile.ProbeHost` at a host that
answers, or turn the probes off, is a fact about the session and not about the incident, and under 154 of
them it was as useful the hundredth time as the first.

For stronger network evidence, have the server-side or client-side `net_statsFile` CSV to hand and import
it with **Import artifacts**. Its ping, jitter and packet loss feed directly into the network hypothesis
and outweigh the ICMP probe, which only measures the path rather than the game protocol. A healthy
net_stats is recorded as evidence *against* a network cause.

Artifacts can be imported after an incident has already finished. The incident is re-analysed in place
and its ranking updates, so there is no need to import before marking.

## Offline validation

The app includes fake scenarios for:

- OBS/GPU contention
- FiveM resource spike
- network issue

The tests assert that the rule engine distinguishes the OBS/GPU and FiveM resource scenarios.

## Limitations in v1

- PresentMon CLI variants differ between releases, so the executable path and arguments may need adjustment
- UDP remote endpoint ownership is not fully reconstructed without heavier tracing
- GPU telemetry is NVIDIA-only; AMD and Intel GPUs report as unavailable
- The VRAM pressure hypothesis still requires corroborating present-bound frame spikes before it will
  fire at all, even though per-process attribution is now available
- When no probe host is configured, the server address is inferred from FiveM's TCP connection: accepted
  immediately on port 30120, otherwise only after the endpoint has persisted across several polls. Set
  `ServerProfile.ProbeHost` explicitly if the inference picks the wrong host.
- Probing is given up for the session after 30 consecutive failures against one host, with one status
  line saying so. Most game servers do not answer ICMP at all, and continuing produced nothing but a
  paragraph in every incident report explaining that there is no RTT measurement. A success resets the
  count, and changing servers gets a fresh one.
- FiveM artifact parsers are designed to accept common exports, but some community-specific file layouts will need richer parsing in v2

## Keeping the app's own overhead low

The tool has to stay cheap enough that it does not become the thing it is measuring:

- The system-wide process sweep does not enumerate threads. `Process.Threads` materializes an object per
  thread, and doing that for every process on the machine dominated allocation cost; only the target
  process reports a thread count.
- The telemetry pump caches the attachment snapshot instead of copying it per event, and the incident
  materializer returns early when nothing is pending — both run hundreds of times a second at PresentMon
  frame rates.
- Collectors that depend on a running game idle until a FiveM/GTA process is present.
- Correlation analysis runs on its own bounded worker queue rather than on the telemetry pump. Analysing
  an incident sorts its frame data and makes several passes over a 90 second window, and doing that on
  the pump stalled ingestion for every collector — a CPU and GC spike in the middle of the very stutter
  being recorded. The queue is drained before a session is reported as stopped.

## Documentation

- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `PRIVACY.md`
