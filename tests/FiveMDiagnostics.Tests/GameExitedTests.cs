namespace FiveMDiagnostics.Tests;

using System.Text.Json;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// What the end of an evening looked like in the app, and what it should look like.
/// </summary>
/// <remarks>
/// On 1 September the game exited at 01:14:14 with the session still running. Within twenty-five
/// seconds the app raised three warnings — PresentMon about a PID that was no longer the same process,
/// the process telemetry about metrics it could not read, and PresentMon again about a second PID it
/// had just picked up. The banner headed "needs attention" then sat on all three indefinitely, because
/// the only thing that clears a collector's warning is a later ordinary line from the same collector,
/// and a collector with nothing left to look at never writes one.
/// </remarks>
public sealed class GameExitedTests
{
    /// <summary>
    /// The second PID was the launcher. It shares the name, presents nothing, reads as a downloader,
    /// and outlives the game — so it is not a target and picking it up produces a warning about a
    /// process nobody was measuring.
    /// </summary>
    [Theory]
    [InlineData("FiveM_b3407_GTAProcess", true)]
    [InlineData("GTA5", true)]
    [InlineData("FiveM", false)]
    [InlineData("FiveM_ROSLauncher", false)]
    [InlineData("FiveMDiagnostics", false)]
    public void OnlyTheProcessThatPresentsFramesIsATarget(string processName, bool expected)
    {
        Assert.Equal(expected, FiveMTargetProcessResolver.IsRenderingProcess(processName));
    }

    /// <summary>
    /// Every timestamp in the journal is UTC, whatever offset the collector stamped it with.
    /// </summary>
    /// <remarks>
    /// The file used to carry both: status entries in local time and incidents and pacing windows in
    /// UTC, so one evening's journal alternated 01:14 and 23:14 on adjacent lines for the same two
    /// minutes. Both were valid ISO-8601 and neither was wrong; the mixture was, and every comparison
    /// began by working out which line was in which zone.
    /// </remarks>
    [Fact]
    public void EveryJournalTimestampIsWrittenInUtc()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"journal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var startedAt = new DateTimeOffset(2026, 9, 1, 21, 11, 27, TimeSpan.Zero);
            string path;

            using (var journal = SessionJournal.TryOpen(directory, startedAt, out var error))
            {
                Assert.Null(error);
                Assert.NotNull(journal);
                path = journal!.Path;

                // The offset the status collectors actually stamp with on the machine under
                // investigation, which is two hours ahead of UTC in September.
                journal.WriteStatus(new DiagnosticStatusEntry(
                    new DateTimeOffset(2026, 9, 2, 1, 14, 34, TimeSpan.FromHours(2)),
                    StatusLevel.Info,
                    "PresentMon",
                    "Spelet avslutades."));
            }

            var line = File.ReadAllLines(path).Single(item => item.Contains("\"status\"", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(line);
            var timestamp = document.RootElement.GetProperty("timestamp").GetString();

            Assert.NotNull(timestamp);
            Assert.EndsWith("Z", timestamp, StringComparison.Ordinal);
            Assert.StartsWith("2026-09-01T23:14:34", timestamp, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An unclassified incident still reports what it measured.
    /// </summary>
    /// <remarks>
    /// The branch used to return before the VRAM, spike and OBS sentences were built, so fifteen of the
    /// forty-eight incidents of 1 September said nothing whatsoever about the card — while it sat
    /// between 85 and 92% in every one of them, which is the band the evening turned out to be about.
    /// What a thin window justifies withholding is the verdict, not the readings.
    /// </remarks>
    [Fact]
    public void AnUnclassifiedIncidentStillReportsWhatItMeasured()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(BuildThinIncident());

        Assert.True(analysis.InsufficientEvidence);
        Assert.StartsWith("Insufficient evidence.", analysis.Summary, StringComparison.Ordinal);

        // The measurements the old branch dropped on the floor.
        Assert.Contains("VRAM toppade på 91%", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("Störst i VRAM: FiveM_b3407_GTAProcess", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("baseline", analysis.Summary, StringComparison.Ordinal);

        // And how close it came, which is what separates "nothing here" from "one point short".
        Assert.Contains("under tröskeln 35 %", analysis.Summary, StringComparison.Ordinal);
    }

    private static IncidentRecord BuildThinIncident()
    {
        var start = new DateTimeOffset(2026, 9, 1, 22, 29, 28, TimeSpan.Zero);
        var markedAt = start.AddSeconds(30);
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 1_200; i++)
        {
            events.Add(new FrameTelemetrySample(
                start.AddMilliseconds(i * 16.67),
                16.67,
                GpuBusyMs: 4.5,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 6.9,
                CpuWaitMs: 9.7));
        }

        // One 38 ms frame: enough to open an incident, not enough for any hypothesis to reach the bar.
        events.Add(new FrameTelemetrySample(
            markedAt,
            38.0,
            GpuBusyMs: 6.0,
            DisplayLatencyMs: 20,
            MsBetweenPresents: 38.0,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: 36.0,
            CpuWaitMs: 0.4));

        events.Add(new GpuTelemetrySample(
            markedAt,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 41,
            MemoryBandwidthUtilizationPercent: 15,
            UsedVramBytes: (ulong)(9.1 * 1024 * 1024 * 1024),
            TotalVramBytes: 10UL * 1024 * 1024 * 1024,
            EncoderUtilizationPercent: 36,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: 1));

        events.Add(new GpuProcessMemorySample(
            markedAt,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(23688, "FiveM_b3407_GTAProcess", (ulong)(6.8 * 1024 * 1024 * 1024), 0, 1),
                new GpuProcessMemoryUsage(7712, "Voicemod", (ulong)(1.03 * 1024 * 1024 * 1024), 0, 1),
            ]));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Normal, "Auto: 38 ms frame"),
            start,
            start.AddSeconds(90),
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
                "Disabled",
                ObsDetectedAtStart: true,
                ServerProfileName: string.Empty,
                SessionStartedAt: start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
