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

- **Spike thresholds are derived, not fixed.** They come from `max(median frame time, display refresh
  interval)`, because stutter is deviation from the achieved cadence. A fixed threshold either misses
  every hitch on a high-refresh display or fires constantly on a low-refresh one.
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
  watching. With no focus telemetry at all every frame counts, which is what the app did before.

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
