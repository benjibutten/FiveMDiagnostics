# FiveM Diagnostics

FiveM Diagnostics is a Windows-only WPF desktop app for collecting local evidence around intermittent FiveM stutter incidents and ranking likely root causes.

The default server profile is `The Path`, but the collection and analysis pipeline is designed to stay generic for other FiveM servers.

## What v1 does

- WPF desktop app with tray mode
- MVVM-based UI with global hotkeys
- Background collectors that only sample while a FiveM/GTA process is active
- Ring buffer retention of at least 90 seconds in memory
- Incident materialization with 30 seconds before and 60 seconds after a marker
- PresentMon integration with safe fallback when the executable is missing
- OBS websocket polling with safe fallback when OBS is absent
- System, process and basic network telemetry on a unified timeline
- Artifact import for `net_statsFile` CSV, profiler JSON, resmon/log evidence and ETL files
- Correlation engine that ranks likely causes with evidence and can say `insufficient evidence`
- ZIP export with JSON summary, CSV metrics, cleartext report and optional attachments
- Fake data generator for offline validation of the rule engine

## Root-cause categories

The analysis engine ranks these categories:

1. GPU/frametime contention
2. OBS/render/output contention
3. FiveM resource/script spike
4. Network jitter/packet loss/routing issue
5. Streaming/disk stall
6. External process interference
7. OS/driver latency
8. Possible cache/resource corruption

## Solution layout

- `src/FiveMDiagnostics.App.Wpf`: desktop UI, tray mode, hotkeys, app composition
- `src/FiveMDiagnostics.Core`: domain models, settings, interfaces, ring buffer, incident materializer
- `src/FiveMDiagnostics.Collectors`: session orchestration and local collectors
- `src/FiveMDiagnostics.Analysis`: correlation engine and artifact parsers
- `src/FiveMDiagnostics.Export`: incident bundle export
- `src/FiveMDiagnostics.Integrations.PresentMon`: PresentMon-backed frame telemetry collector
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

- server profile name
- probe host/IP for lightweight RTT probing
- endpoint hint
- PresentMon executable path
- working/export/artifact directories
- export redaction toggles

### PresentMon notes

The collector is designed to be resilient if PresentMon is not installed or not configured.

Default argument template:

```text
-process_id {processId} -output_file "{outputPath}"
```

If you use a newer PresentMon build with a different CLI shape, update the argument template in `settings.json` or in the UI.

## Capture modes

### Basic mode

- No admin required
- Collects system/process/network telemetry
- Polls OBS if available
- Uses PresentMon only if configured
- Intended to stay low overhead

### Deep mode

- Triggered automatically on `Mark Severe`
- Starts a short WPR trace only when needed
- Attempts to save an ETL file in the session working directory
- If WPR requires elevation, the app reports that cleanly instead of crashing

## Hotkeys

- `Ctrl+Alt+F9`: mark stutter
- `Ctrl+Alt+F10`: mark severe stutter
- `Ctrl+Alt+F11`: export latest incident

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

Sensitive fields are redacted by default.

## Notes on network evidence

In basic mode the app captures:

- TCP remote endpoints for the active FiveM/GTA PID
- UDP local ports for the active PID
- optional RTT probes to a configured host/IP

This is enough to separate many local frametime incidents from probable network incidents, but it is not a full packet capture.

## Offline validation

The app includes fake scenarios for:

- OBS/GPU contention
- FiveM resource spike
- network issue

The tests assert that the rule engine distinguishes the OBS/GPU and FiveM resource scenarios.

## Limitations in v1

- PresentMon CLI variants differ between releases, so the executable path and arguments may need adjustment
- UDP remote endpoint ownership is not fully reconstructed in basic mode without heavier tracing
- ETL analysis is intentionally shallow in v1 and focuses on quick indicators such as DPC/ISR-like evidence
- FiveM artifact parsers are designed to accept common exports, but some community-specific file layouts will need richer parsing in v2

## Documentation

- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `PRIVACY.md`
