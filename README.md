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

`MaxIncidentsPerWindow` bounds the rate, so the top-level **MaxRetainedIncidents** (50) bounds the
history across all sessions. Beyond that the oldest incidents are dropped from memory and from the list —
exported bundles are unaffected.

Auto-marked incidents **may trigger deep capture, but only within a budget**. Saving the ring buffer
writes a multi-hundred-megabyte ETL and empties the buffer, so this used to be refused outright — until a
five and a half hour session raised eighteen severe incidents and produced no trace at all, leaving every
one of its hitch clusters unexplained. Three gates now decide, and all three have to pass: the frame has
to reach **DeepCapture.AutoCaptureFrameTimeMs** (120 ms), captures are spaced by
**DeepCapture.AutoCaptureCooldown** (10 min) so a burst spends one rather than twenty, and
**DeepCapture.MaxAutoCapturesPerSession** (8) is a hard ceiling. Set `DeepCapture.CaptureAutoIncidents`
to `false` for the old behaviour.

The threshold was 300 ms, which was right for the sessions it was calibrated against and went blind the
moment they improved. A seven hour session contained exactly one frame over 300 ms — the game's own
restart, not a hitch — so two captures were taken in the first ninety minutes and none at all in the last
three and a half hours, while the 67 frames that were the entire remaining problem sat at 100–170 ms.

Lowering the threshold alone would have made the opposite mistake, and the fix is the same one
`MaxIncidentsPerWindow` applies a level up. Against those real frames, a flat 120 ms threshold with only a
session ceiling spends all its captures before the ninety minute mark — 43 of the 67 fell inside fourteen
minutes of the opening hour, a cache rebuilt after a settings change with a sync backlog on top — and
records nothing for the remaining five hours. **DeepCapture.MaxAutoCapturesPerWindow** (1) over
**DeepCapture.CaptureBudgetWindow** (1 h) rations the ordinary frames instead, which against the same
session spreads six captures across the whole evening and picks up its largest late frame. A frame past
the override threshold below ignores the window budget, answering to the session ceiling and the ring
buffer only — and does not spend the window's slot either, since charging it would let one catastrophic
frame buy silence for the rest of the hour, which is the failure the window exists to prevent arriving by
the one path that is exempt from it.

The spacing gate has one way past it. A frame beyond **DeepCapture.AutoCaptureOverrideFrameTimeMs**
(500 ms) spends budget the cooldown would have withheld, because the ceiling was always meant to be the
binding constraint and a session that turned away its third and fourth largest frames while holding an
unspent capture had those the wrong way round. What the override cannot skip is the ring buffer
refilling: **DeepCapture.AutoCaptureOverrideCooldown** (60 s) covers the 28-32 s a capture takes to
finish writing plus the ~21 s the buffer needs to refill, so an override never records a nearly empty
ring. It is raised automatically for a larger `RingBufferMegabytes`, which refills proportionally more
slowly — at the 2 048 MB ceiling the refill alone is about 56 s. Setting the override threshold to the
ordinary one turns the override off.

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
wall clock by tracking the tightest observed bound across batches, so frames land at their real position
on the timeline instead of collapsing onto the moment they were read.


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

Turn it off with `Gpu.ProcessMemoryEnabled`; `Gpu.ProcessMemoryInterval` and `Gpu.ProcessMemoryTopCount`
(25) set the cadence and how many processes each sample keeps. The list is long because the question is
what holds the memory the game does not: nine processes held GPU memory for a whole measured session and
four more came and went, and at the original ten places the non-game total was a floor rather than a
figure.

## Capture depth

Every session runs the standard capture. The deeper WPR capture is switched on automatically by
`Mark Severe`; a settings checkbox can also opt normal *manual* markers into deep capture. Automatic
incidents can start WPR within the budget described above, which keeps a bad session to a handful of
traces rather than one every couple of minutes.

### Standard capture

- No admin required
- One-click start with the default settings
- Collects system/process/network telemetry
- Polls OBS if available
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
