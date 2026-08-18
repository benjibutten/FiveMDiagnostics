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

### How a slow frame is attributed

With PresentMon `--v2_metrics` every spike is classified using the per-frame CPU/GPU breakdown rather
than frame time alone:

| Signature | Interpretation |
| --- | --- |
| `MsCPUBusy` dominates | FiveM script/resource work or CPU contention |
| `MsGPUBusy` dominates | GPU contention, e.g. NVENC encoding against the game |
| Neither is busy | Present/display path stalled — VRAM eviction, DPC/ISR latency or composition |

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
- **MaxIncidentsPerSession** (40) — each incident retains its full event window in memory.
- **MinimumSamples** (120) — nothing fires before the baseline settles, so a level load is not a stutter.

Every value is clamped when settings are read and written. A hand-edited file with `SpikeMultiplier: 0`,
`DroppedFrameRun: 0` or `Cooldown: 0` would otherwise trigger on nearly every frame, each trigger
snapshotting a 90 second window; `BaselineWindowFrames` is capped because the detector allocates arrays
of that size up front.

`MaxIncidentsPerSession` bounds one session, so the top-level **MaxRetainedIncidents** (50) bounds the
history across all of them. Beyond that the oldest incidents are dropped from memory and from the list —
exported bundles are unaffected.

Auto-marked incidents **never trigger deep capture**. WPR writes a multi-hundred-megabyte ETL and costs
about fifteen seconds of tracing, which is affordable once on demand and ruinous every two minutes for
six hours. Manual `Severe` markers still trigger it.

Manual marking is unchanged and still worth using: it records that a human *perceived* something, which
the telemetry alone cannot establish.

### Spike thresholds are relative, not fixed

Stutter is deviation from the cadence the machine is achieving, so thresholds derive from the observed
median frame time and the display refresh interval (whichever is larger), not a hardcoded 25 ms. A
120 Hz display running at 120 fps flags spikes from roughly 12.5 ms; a 60 Hz one from roughly 25 ms.

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
--process_id {processId} --output_file "{outputPath}" --no_console_stats --stop_existing_session --terminate_on_proc_exit
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

Settings written by an older build are migrated to the template above automatically on load.

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

From the third restart onwards the status entry escalates to `Error`, because at that point the session's
frame data should be treated as incomplete.

### GPU telemetry notes

GPU sampling uses `nvml.dll`, which ships with the NVIDIA display driver and needs no separate install.
On a non-NVIDIA machine the collector reports once that NVML is unavailable and then emits samples marked
unavailable, leaving every other collector untouched.

## Capture depth

There is no mode selector in the UI. Every session runs the standard capture; the deeper WPR capture is
switched on automatically by `Mark Severe` and by nothing else.

### Standard capture

- No admin required
- One-click start with the default settings
- Collects system/process/network telemetry
- Polls OBS if available
- Uses PresentMon only if it is configured or found automatically
- Intended to stay low overhead

### Deep capture

- Triggered automatically on `Mark Severe`
- **Requires the app to run as administrator.** `wpr.exe` cannot self-elevate, so the app checks up front
  and reports that clearly rather than failing part-way through and leaving a trace session running.
- Starts a short WPR trace only when needed, stacking the profiles that matter for stutter:
  `GeneralProfile`, `CPU`, `GPU`, `DiskIO`, `Minifilter`, `ResidentSet` (configurable via
  `DeepCapture.Profiles`)
- Attempts to save an ETL file in the session working directory
- ETL analysis reports DPC/ISR **durations**, not event counts: ten thousand short DPCs are normal, a
  single 8 ms one blocks the scheduler and stalls every thread at once
- Only one capture runs at a time. WPR records into a single machine-wide session, so a severe marker
  raised while a capture is in flight is recorded as an incident but does not start a second trace

All setup fields are optional. The app can start a session with the default paths and no network hints configured.

## Tray controls

- right-click the tray icon to start or stop a session
- right-click the tray icon to mark normal or severe stutter while a session is active
- right-click the tray icon to export the latest incident or reopen the main window

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
- GPU samples cover the whole adapter, not per-process VRAM attribution, so the VRAM pressure hypothesis
  requires corroborating present-bound frame spikes before it will fire at all
- When no probe host is configured, the server address is inferred from FiveM's TCP connection: accepted
  immediately on port 30120, otherwise only after the endpoint has persisted across several polls. Set
  `ServerProfile.ProbeHost` explicitly if the inference picks the wrong host.
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
