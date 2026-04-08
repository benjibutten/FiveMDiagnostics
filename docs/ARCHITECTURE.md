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
- environment metadata provider

The collectors write into a bounded channel to keep backpressure explicit.

## `FiveMDiagnostics.Analysis`

- rule-based correlation engine
- parsers for common FiveM-side artifacts

The analysis intentionally prefers evidence correlation over averages. It also emits `insufficient evidence` if the signals are weak.

## `FiveMDiagnostics.Export`

- incident bundle creation
- redaction-aware JSON/CSV/report generation

## `FiveMDiagnostics.Integrations.PresentMon`

- launches a configured PresentMon executable
- tails CSV output incrementally
- converts rows into frame telemetry samples
- degrades safely if the dependency is missing

## `FiveMDiagnostics.Integrations.Obs`

- raw `obs-websocket` client over `ClientWebSocket`
- polls stats and output state on a short interval
- keeps OBS optional

## `FiveMDiagnostics.Integrations.Etw`

- short WPR deep capture for severe incidents
- ETL artifact parsing via `TraceEvent`

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
- `SystemTelemetrySample`
- `ProcessTelemetrySample`
- `ObsTelemetrySample`
- `NetworkEndpointSample`
- `NetworkProbeSample`
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
