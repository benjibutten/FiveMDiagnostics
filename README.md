# FiveM Diagnostics

A Windows app that records what your computer is doing while you play FiveM, so that when the game
stutters you can find out why. It runs in the tray, starts measuring when FiveM starts, marks bad frames
on its own, and gives you one zip file to send to whoever is helping you.

It works with any FiveM server. Everything stays on your machine unless you share it yourself.

## What it can tell you

Each stutter becomes an incident: 30 seconds before it and 60 seconds after, with frame times, CPU, memory,
GPU and VRAM use, disk and network readings, and the other programs that were busy. The app ranks the
likely causes for every incident and shows the evidence behind each one. When the evidence is too thin
it says so instead of guessing.

The causes it can tell apart include the graphics card running out of memory, a FiveM script holding up
the game, another program taking the CPU or the disk, network loss, the game waiting on a lock, the
Windows paging file, and the game window losing focus. If you stream with OBS, it also measures OBS and
the stream.

## Install

1. Download `FiveMDiagnostics-<version>-win-x64.zip` from the
   [latest release](https://github.com/benjibutten/FiveMDiagnostics/releases/latest).
2. Extract it to a folder of its own and run `FiveMDiagnostics.exe`. Keep the folder: the app updates
   itself in place.

Windows SmartScreen may warn about a new or unsigned build. Each release lists the zip's SHA-256 hash
if you want to check the download before choosing *Run anyway*.

PresentMon, which measures frame times, is included in the zip. Nothing else needs installing.

## Using it

The first time it starts, the app asks whether you stream with OBS. Answer no unless you do; it only
changes what is measured and shown, and you can change it under *Settings*.

### Before you play

On the *Overview* tab you can tick programs to close and start FiveM from the app.
That is optional. Starting FiveM as usual works just as well, because the session starts when the game
does and ends ten minutes after it closes.

### While you play

Press `F9` when it stutters. The app also marks bad frames by itself, but your mark
records that you noticed something, which the numbers cannot. When the app runs as administrator, each
mark also saves a deep capture: a short Windows trace of the seconds before the stutter, showing what
every thread was doing. The key can be changed under *Settings*.

### Afterwards

Open *Share*, pick the evening and press *Create zip*. Explorer opens with the file
selected, ready to drag into Discord or attach to a support ticket. IP addresses and your Windows user
name are removed from the files unless you tick *Include sensitive details*.

The *Incidents* tab shows every incident with its ranked causes and timeline if you want to read them
yourself.

## Administrator rights

The app works without administrator rights. Deep capture needs them, because WPR, the Windows tool that
records the trace, does. The window says when it is running without them and offers
*Restart as administrator*.

## Streaming

With *I stream with OBS* ticked, the app reads OBS through its WebSocket server: render and encoding lag,
skipped frames, and frames the stream drops on the way to the ingest server. Turn the WebSocket server on
in OBS under *Tools → WebSocket Server Settings*. With the box unticked, OBS is never contacted and
nothing about streaming appears in the reports.

## Privacy

The app uploads nothing. Sessions, settings and exports live in `%LocalAppData%\FiveMDiagnostics`, and
session files older than seven days are deleted when the app starts (the number of days is a
setting). [PRIVACY.md](PRIVACY.md) lists what
is collected and what the share zip leaves out.

## Limitations

- The analysis text and the session journal are written in Swedish. The interface is in English and
  Swedish.
- GPU load, temperature and encoder use come from NVIDIA's driver. On AMD and Intel cards those readings
  are missing; VRAM per process works on any card.
- FiveM sends gameplay over UDP, and Windows does not say which server a UDP socket talks to. The app
  takes the server from FiveM's TCP connection to the same host, and many servers do not answer ping, so
  network evidence is weaker than the rest. A `net_statsFile` export imported on the *Incidents* tab helps.
- The app measures your computer, not the server. Queues, desync and scripts running on the server are
  outside what it can see.

## Building from source

Requires Windows 10 or 11 and the .NET 10 SDK.

```powershell
dotnet build FiveMDiagnostics.slnx
dotnet test FiveMDiagnostics.slnx
dotnet run --project src/FiveMDiagnostics.App.Wpf/FiveMDiagnostics.App.Wpf.csproj
```

A local build looks for PresentMon on `PATH` and in the usual install folders, or at the path set under
*Settings → Advanced paths and tools*.

| Project | Contents |
| --- | --- |
| `FiveMDiagnostics.App.Wpf` | Window, tray, setup guide, settings |
| `FiveMDiagnostics.Core` | Models, settings, ring buffer, the monitors that write session summaries |
| `FiveMDiagnostics.Collectors` | Session manager and the system, process, network and focus collectors |
| `FiveMDiagnostics.Analysis` | The engine that ranks causes, and the artifact parsers |
| `FiveMDiagnostics.Export` | Incident bundles and session zips |
| `FiveMDiagnostics.Integrations.*` | PresentMon, NVML, OBS and ETW/WPR |
| `FiveMDiagnostics.Tools.EtlAnalyzer` | Command-line reader for deep capture traces |
| `FiveMDiagnostics.Fakes` | Simulated incidents for testing the engine |

[docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) explains how incidents are detected and how the engine
weighs evidence. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) covers the code structure.
