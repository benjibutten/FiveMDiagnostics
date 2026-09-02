namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// A deep capture is evidence about the seconds it recorded, and about no others.
/// </summary>
/// <remarks>
/// <para>
/// The video memory manager figure is the strongest single piece of evidence the VRAM hypothesis can
/// carry — it is worth 0.35 confidence on its own, and it is the only one that observes the mechanism
/// instead of a symptom. It was read out of whichever attached trace showed the most paging, across the
/// whole attachment list, with nothing checking that the trace had been recording while the incident
/// happened.
/// </para>
/// <para>
/// An evening produces captures a minute apart, and a session that imports an ETL by hand can attach
/// one taken for something else entirely. Paging the driver did during a level load five minutes
/// earlier then classifies the incident in front of the reader, which is the exact reasoning error the
/// per-frame guards above it exist to prevent.
/// </para>
/// </remarks>
public sealed class TracePressureBelongsToTheIncidentTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 22, 29, 28, TimeSpan.Zero);

    /// <summary>The trace was recording while it happened, so what it measured counts.</summary>
    [Fact]
    public void ATraceThatCoveredTheWindowClassifiesTheIncident()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(coveredFrom: Start));

        var vram = Assert.Single(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.GpuVramPressure);

        Assert.Contains(vram.Evidence, item => item.Contains("videominneshanterare", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same file, the same 0.91 cores, recorded five minutes before the incident opened. It says
    /// what the driver was doing then, and the reader is asking about now.
    /// </summary>
    [Fact]
    public void ATraceFromAnotherWindowDoesNotClassifyThisOne()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(coveredFrom: Start.AddMinutes(-5)));

        Assert.DoesNotContain(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.GpuVramPressure);

        Assert.DoesNotContain("videominneshanterare", analysis.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trace that never said what it covers is kept. The pair is written whenever the file held a
    /// timestamped event to read it from, and an attachment that could not say was still captured for
    /// this marker — silent is not the same as elsewhere.
    /// </summary>
    [Fact]
    public void ATraceThatNamesNoWindowIsStillEvidence()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(coveredFrom: null));

        Assert.Contains(
            analysis.Hypotheses,
            item => item.Category == RootCauseCategory.GpuVramPressure);
    }

    /// <summary>
    /// The evening's own shape: a card at 91%, spikes that PresentMon books as CPU-bound, and one
    /// deep capture whose covered span is the variable under test.
    /// </summary>
    private static IncidentRecord Incident(DateTimeOffset? coveredFrom)
    {
        var markedAt = Start.AddSeconds(30);
        var windowEnd = Start.AddSeconds(90);
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(Frame(Start.AddMilliseconds(i * 16.67), 16.67, cpuBusyMs: 6.9));
        }

        // Four spikes with the processor busy through all of them: not present-bound, which is what
        // makes the driver measurement the only thing that can carry this hypothesis.
        for (var i = 0; i < 4; i++)
        {
            events.Add(Frame(markedAt.AddSeconds(i), 190, cpuBusyMs: 188));
        }

        events.Add(new GpuTelemetrySample(
            markedAt,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 41,
            MemoryBandwidthUtilizationPercent: 15,
            UsedVramBytes: (ulong)(9.1 * 1024 * 1024 * 1024),
            TotalVramBytes: 10UL * 1024 * 1024 * 1024,
            EncoderUtilizationPercent: 12,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: 1));

        var metrics = new Dictionary<string, double>
        {
            ["videoMemoryManagerPeakCores"] = 0.91,
            ["videoMemoryManagerBaselineCores"] = 0.18,
            ["videoMemoryManagerPressured"] = 1,
        };

        if (coveredFrom is { } from)
        {
            metrics["traceCoveredStartUnixMs"] = from.ToUnixTimeMilliseconds();
            metrics["traceCoveredEndUnixMs"] = from.AddSeconds(40).ToUnixTimeMilliseconds();
        }

        events.Add(new ArtifactEvidence(
            markedAt,
            ArtifactKind.EtlTrace,
            "Deep capture: 0,91 kärnor i dxgmms2.sys.",
            metrics,
            "capture.etl"));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Normal, "Auto: 190 ms frame"),
            Start,
            windowEnd,
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
                "Disabled",
                ObsDetectedAtStart: false,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }

    private static FrameTelemetrySample Frame(DateTimeOffset timestamp, double frameTimeMs, double cpuBusyMs)
    {
        return new FrameTelemetrySample(
            timestamp,
            frameTimeMs,
            GpuBusyMs: 4.5,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuBusyMs,
            CpuWaitMs: 0.3);
    }
}
