# Architecture

## Goals

The app is built to answer one practical question quickly: when a user marks a stutter, what evidence best explains the incident without relying on average FPS alone.

## High-level flow

1. `DiagnosticsSessionManager` starts a session.
2. Collectors publish `TelemetryEvent` items into a bounded channel.
3. The session manager pumps those events into a time-window ring buffer.
4. When the user marks a stutter, `IncidentMaterializer` snapshots 30 seconds of history and keeps appending 60 seconds of future samples.
5. Once the window closes, the analysis engine scores hypotheses.
6. The UI shows the ranked result, evidence and a simplified timeline.
7. Export packages the incident into a local ZIP.

## Projects

## `FiveMDiagnostics.Core`

- Shared models for telemetry, incidents, analysis and export
- Collector/export/analyzer interfaces
- `TimeWindowRingBuffer<T>`
- `IncidentMaterializer`

This project stays framework-agnostic.

## `FiveMDiagnostics.Collectors`

- `DiagnosticsSessionManager`
- FiveM/GTA process resolution
- system telemetry collector
- per-process FiveM telemetry collector
- network collector
- window focus collector
- environment metadata provider

The collectors write into a bounded channel to keep backpressure explicit.

## `FiveMDiagnostics.Analysis`

- rule-based correlation engine
- parsers for common FiveM-side artifacts

The analysis intentionally prefers evidence correlation over averages. It also emits `insufficient evidence` if the signals are weak, naming which missing input would help most.

Three decisions shape the scoring:

- **Spike thresholds are derived, not fixed.** They are multiples of the session's baseline rather than
  millisecond constants — see [Hitch threshold and incident thresholds](#hitch-threshold-and-incident-thresholds).
- **Slow frames are attributed, not guessed.** The PresentMon v2 CPU/GPU breakdown decides whether a
  spike was CPU-bound, GPU-bound or present-bound. Without that breakdown the engine falls back to
  frame-time-only reasoning but caps its confidence lower, so a measured attribution always outranks
  an inferred one.
- **Frames nobody saw are not stutter.** Alt-tab and the Windows key produce frame times that read
  exactly like a freeze, and the game is behind another window while they happen. `WindowFocusSample`
  makes that measurable, so those frames are held out of every running measurement — the spike
  detector's baseline, the pacing classification, the VRAM band comparison, the capture budget — and
  counted separately in `GameFocusMonitor` instead of quietly folded in. An incident marked during one
  is ruled `GameNotInFocus` rather than ranked against nine hypotheses about a game nobody was
  watching. With no focus telemetry at all every frame counts, which is what the app did before. The
  verdict is classified at the frame the incident is *named after* rather than at its marker — an
  escalated incident's marker can sit most of a minute before that frame — and it never silences the
  verdict behind it, because excluding a window from the statistics is not the same as explaining it.
- **A paging stall is proved twice.** `MemoryPagingStall` fires when the trace shows hard faults in
  the game process and a read out of the paging file whose service time matches the game thread's
  off-CPU interval. Two streams that know nothing about each other reporting the same millisecond
  figure is the strongest evidence the engine can assemble, and it outranks every inferred verdict.

## `FiveMDiagnostics.Export`

- incident bundle creation
- redaction-aware JSON/CSV/report generation

## `FiveMDiagnostics.Integrations.PresentMon`

- launches a configured PresentMon executable
- tails CSV output incrementally
- converts rows into frame telemetry samples
- degrades safely if the dependency is missing

## `FiveMDiagnostics.Integrations.Nvml`

- thin `nvml.dll` binding for GPU utilization, VRAM occupancy, NVENC load and throttle reasons
- VRAM occupancy is what distinguishes "the GPU is busy" from "the driver is evicting textures over
  PCIe"; the latter stalls the whole system rather than merely slowing frames
- degrades to unavailable samples on non-NVIDIA hardware

## `FiveMDiagnostics.Integrations.Obs`

- raw `obs-websocket` client over `ClientWebSocket`
- polls stats and output state on a short interval
- keeps OBS optional

## `FiveMDiagnostics.Integrations.Etw`

- short WPR deep capture for severe incidents
- ETL artifact parsing via `TraceEvent`
- per-process CPU out of the trace, because the process counters read about once a second and a shell
  burst lasts two to four tenths of one
- the release chain behind the game thread's longest wait, which separates "the game blocked on
  itself" from "something outside took the processor"
- a second read of the same ETL for the stacks the first read could not know it wanted: the
  ReadyThread stacks that name each waker as a fact instead of a processor inference, and the sample
  stacks inside the wait that say what the thread at the end of the chain was doing there, as module
  chains. Costs a few seconds per capture; see `StackSecondPass`
- disk service time per **volume**, not per process. A machine's disks are not alike, and averaging
  them hides the one that is broken: a system drive answering thousands of operations at 0.1 ms and a
  second drive answering tens at 20–450 ms read as one unremarkable disk when summed together

## `FiveMDiagnostics.Fakes`

- deterministic demo scenarios for offline validation

## `FiveMDiagnostics.App.Wpf`

- WPF shell
- viewmodel and commands
- tray icon management
- global hotkey registration
- settings persistence

## Data model strategy

All telemetry streams share a common base type:

- `TelemetryEvent`

Important concrete event types:

- `FrameTelemetrySample`
- `GpuTelemetrySample`
- `SystemTelemetrySample`
- `ProcessTelemetrySample`
- `ObsTelemetrySample`
- `NetworkEndpointSample`
- `NetworkProbeSample`
- `WindowFocusSample`
- `ArtifactEvidence`

This lets the app preserve one merged timeline while still keeping type-specific analysis.

## Hitch threshold and incident thresholds

Everything that grades a frame grades it against one number:

    baseline = max(median frame time, display refresh interval)

`HitchThreshold.BaselineFrom` is that line, and it is the only place it is written. Take whichever is
larger: a game locked to 60 fps on a 165 Hz panel is not stuttering, so the achieved cadence is the
honest figure; a game that should reach 120 Hz must not be graded against a median a bad window has
already dragged upwards.

What differs is the multiplier, because the questions differ:

| Bar | Multiplier | Who reads it | What it answers |
|---|---|---|---|
| Hitch | 2× | `GameFocusMonitor`, `CaptureCostMonitor`, `VramPressureBandMonitor`, `HalfHourBreakdownMonitor` | Did the player feel this frame? |
| Incident, spike | 2× **and** ≥ 100 ms (`AutoIncidentDetector`), 1.5× (`FiveMCorrelationEngine`) | the detector live, the engine afterwards | Is this worth opening a window and running hypotheses over? |
| Incident, severe | 4× / 2.5×, the detector's also behind the 100 ms floor | the same two | Is this worth a deep capture? |
| Capture-worthy | adaptive, floored at 120 ms | `AutoDeepCaptureBudget` | Is this large enough that a ~900 MB trace of it will show anything? |

The hitch bar is fixed once per session, after a 600-frame warm-up, by `HitchThreshold`, and every
monitor that counts hitches reads that one instance. It used to be computed in four places — two
refreshes alone in the focus line, two copies of one cadence median in capture cost and the VRAM band,
and whatever the focus line happened to hold in the half-hour table. At 59.94 Hz they all land near
33.3 ms, so the divergence never showed inside a single summary; it showed between evenings, where the
same figure has been written up as both 1.4× and 3.4× because two lines counted against two bars. The
notes compare evenings on the decimal, so the session writes the bar it settled on into the journal as
soon as it knows it.

The incident thresholds are deliberately *not* the hitch bar, and unlike it they are not fixed for the
session: the detector's median rolls over the last 600 frames, so it follows a machine that degrades
during an evening, and the engine's is taken over the frames inside the window it is analysing. Hitch
frequency is a measurement of the evening; an incident is a decision to spend a window, fourteen
hypotheses and possibly a deep capture. Changing one must not move the other.

The detector's absolute floor (`AutoDetectOptions.IncidentFloorMs`, 100 ms) is there because a
multiplier measures the wrong thing at the bottom of its range. Twice a 16.7 ms baseline is 33 ms, and
11 September produced 123 auto incidents at a median of 45 ms — 74 under 50 ms, one of them a 36 ms
frame ruled `ExternalProcessInterference` against `SearchIndexer` at confidence 0.51. With the floor
that evening keeps 11 of the 123. The frames below it are still hitches and still counted as such by
all four monitors, and the session writes how many at the end so the drop has a stated cause. One
consequence worth knowing when reading a journal: at a 16.7 ms baseline every frame past the floor is
already past four times the baseline, so a 60 fps evening produces only Severe incidents, and the
Normal tier only reappears on an evening whose baseline is above 25 ms. The out-of-focus path in
`DiagnosticsSessionManager` keeps its own fixed 500 ms limit and never sees the floor; so does
`AutoDeepCaptureBudget`, whose 120 ms is a different question again.

## Incident lifecycle

The ring buffer stores at least 3 minutes of samples in v1. Marking an incident does not stop collection. Instead, the materializer:

- snapshots the pre-window immediately
- keeps buffering live samples for the post-window
- finalizes when the closing timestamp is reached

This keeps normal overhead low and avoids high-cost capture until the user marks a severe event.

## Failure model

External integrations are expected to fail sometimes. v1 uses best-effort integration with safe fallback:

- no PresentMon: app still runs, but frame evidence weakens
- no OBS: OBS collector emits disconnected samples
- no WPR or no elevation: deep capture reports the limitation instead of crashing
- unsupported artifact format: artifact is still attachable as manual evidence

## Why rules first

v1 uses a rule-based engine because:

- it is easier to audit
- it produces explainable evidence lists
- it matches the early product goal of trustworthy diagnostics

The scoring model is deliberately conservative. It should prefer `insufficient evidence` over a confident but weak claim.
