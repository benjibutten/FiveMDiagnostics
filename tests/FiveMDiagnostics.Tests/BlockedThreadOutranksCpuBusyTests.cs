namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// PresentMon's <c>MsCPUBusy</c> cannot tell a thread executing from a thread blocked, and the engine
/// used to read it as though it could.
/// </summary>
/// <remarks>
/// Built from the largest frame of the 26 August session: 586 ms at 20:39:41, reported by PresentMon as
/// 585.2 ms of CPU busy, for a main thread the ETL shows off the processor for 568.9 ms of it. The
/// figure is derived from the gap between presents, so a thread asleep on a lock produces exactly the
/// reading a thread running script does. That evening 190 of 198 incidents were ranked
/// <see cref="RootCauseCategory.FiveMResourceSpike"/> on the strength of it.
/// </remarks>
public sealed class BlockedThreadOutranksCpuBusyTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 18, 39, 11, TimeSpan.Zero);

    private readonly FiveMCorrelationEngine _engine = new();

    /// <summary>
    /// The reading must not be counted as support at all, rather than counted and then capped. Capping
    /// alone still left the hypothesis ranked first in the reports this was written from.
    /// </summary>
    [Fact]
    public void ABlockedThreadStopsCpuBusyFromSupportingAScriptSpike()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: true));
        var resource = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.NotNull(resource);
        Assert.DoesNotContain(resource!.Evidence, item => item.Contains("var CPU-bundna", StringComparison.Ordinal));
        Assert.Contains(resource.Evidence, item => item.Contains("BORTSETT", StringComparison.Ordinal));
    }

    [Fact]
    public void ABlockedThreadCapsTheScriptSpikeVerdict()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: true));
        var resource = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.NotNull(resource);
        Assert.True(
            resource!.Confidence <= 0.3,
            $"a script spike reached {resource.Confidence:P0} for a frame the thread slept through");
        Assert.Contains(resource.Evidence, item => item.Contains("NEDVIKTAD", StringComparison.Ordinal));
    }

    /// <summary>
    /// The thread wait hypothesis is the one the evidence actually supports, so it has to come out on
    /// top — the ranking is what the incident report prints.
    /// </summary>
    [Fact]
    public void TheThreadWaitHypothesisOutranksTheScriptSpike()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: true));

        Assert.Equal(RootCauseCategory.FiveMThreadWait, analysis.Hypotheses[0].Category);
    }

    /// <summary>
    /// The other direction, and the reason this is conditional on the trace: without one there is no
    /// evidence of blocking, so the attribution still counts and is still reported as it stands.
    /// </summary>
    [Fact]
    public void WithoutATraceTheCpuAttributionStillCounts()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: false));
        var resource = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.NotNull(resource);
        Assert.Contains(resource!.Evidence, item => item.Contains("var CPU-bundna", StringComparison.Ordinal));
        Assert.DoesNotContain(resource.Evidence, item => item.Contains("NEDVIKTAD", StringComparison.Ordinal));
        Assert.True(resource.Confidence > 0.3, $"an uncontradicted script verdict was discarded ({resource.Confidence:P0})");
    }

    /// <summary>
    /// It still cannot be the answer. Nothing here says the thread was blocked, and nothing says it was
    /// running either: <c>MsCPUBusy</c> reads identically for both, and every other signal in the window
    /// is a once-a-second average that cannot resolve a single frame.
    /// </summary>
    /// <remarks>
    /// The session of 27 August is the argument. All 67 of its frames over 100 ms reported
    /// <c>MsCPUBusy</c> at 70% or more of frame time and 151 of its 154 incidents were ranked as script
    /// spikes at 80% confidence — while the one freeze that had a trace shows the main thread off the
    /// processor for 178.0 of its 178 ms. Below the 0.35 bar is where the same codebase already puts a
    /// storage verdict resting on throughput instead of on the disk counters.
    /// </remarks>
    [Fact]
    public void WithoutATraceTheCpuAttributionCannotReachAVerdict()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: false));
        var resource = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.True(resource.Confidence <= 0.34, $"an unmeasured script verdict reached {resource.Confidence:P0}");
        Assert.Contains(resource.Evidence, item => item.Contains("OBEKRÄFTAD", StringComparison.Ordinal));
        Assert.Equal(RootCauseCategory.InsufficientEvidence, analysis.Hypotheses[0].Category);
        Assert.True(analysis.InsufficientEvidence);
    }

    /// <summary>
    /// Having a trace is not the same as having measured anything.
    /// </summary>
    /// <remarks>
    /// A capture can come back carrying DPC and ISR durations and no sampled profiles at all — another
    /// ETW session holding the keyword, a stream that died in the first seconds, a profile that never
    /// asked for them. Such a file says exactly as much about whether the game's threads ran as no file
    /// does, so it must not lift the ceiling on a claim that they ran script. It is still evidence for
    /// everything else it holds.
    /// </remarks>
    [Fact]
    public void ATraceThatSampledNoCpuDoesNotLiftTheCap()
    {
        var analysis = _engine.Analyze(BuildIncidentWithCoverageOnlyTrace());
        var resource = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.True(resource.Confidence <= 0.34, $"an ETL with no CPU samples lifted the cap to {resource.Confidence:P0}");
        Assert.Contains(resource.Evidence, item => item.Contains("OBEKRÄFTAD", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the trace that did sample the game running lifts it, which is the whole point of taking one.
    /// </summary>
    [Fact]
    public void ATraceThatSampledTheGameLiftsTheCap()
    {
        var analysis = _engine.Analyze(BuildIncidentWithCpuSampledTrace());
        var resource = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.True(resource.Confidence > 0.34, $"a trace that sampled the game left the cap on ({resource.Confidence:P0})");
        Assert.DoesNotContain(resource.Evidence, item => item.Contains("OBEKRÄFTAD", StringComparison.Ordinal));
    }

    [Fact]
    public void ATraceWhoseFallbackSubjectIsNotTheGameDoesNotLiftTheCap()
    {
        var analysis = _engine.Analyze(WithTraceMetrics(new Dictionary<string, double>
        {
            ["cpuSampleCount"] = 100_000,
            ["cpuSubjectIsGame"] = 0,
            ["cpuSubjectProcessCores"] = 3.2,
        }));
        var resource = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.True(resource.Confidence <= 0.34);
        Assert.Contains(resource.Evidence, item => item.Contains("OBEKRÄFTAD", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericResmonEvidenceDoesNotLiftTheCap()
    {
        var incident = BuildIncident(withTrace: false);
        var analysis = _engine.Analyze(incident with
        {
            Events = incident.Events.Concat([new ArtifactEvidence(
                incident.Marker.MarkedAt,
                ArtifactKind.ResmonSnapshot,
                "resmon/export antyder resource-spike: inventory 11.4ms",
                new Dictionary<string, double>())]).ToArray(),
        });
        var resource = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.True(resource.Confidence <= 0.34);
        Assert.DoesNotContain(resource.Evidence, item => item.Contains("resmon/export", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A profiler measures execution, so a window that has one is not guessing and keeps its verdict.
    /// The cap is about the instrument being absent, not about the conclusion being unwelcome.
    /// </summary>
    [Fact]
    public void AProfilerLiftsTheCap()
    {
        var incident = BuildIncident(withTrace: false);
        var withProfiler = incident with
        {
            Events = incident.Events
                .Concat([new ArtifactEvidence(
                    incident.Marker.MarkedAt,
                    ArtifactKind.ProfilerJson,
                    "Profiler JSON pekade ut resource 'inventory' med 240.0 ms.",
                    new Dictionary<string, double> { ["topResourceMs"] = 240 })])
                .ToArray(),
        };

        var analysis = _engine.Analyze(withProfiler);

        Assert.Equal(RootCauseCategory.FiveMResourceSpike, analysis.Hypotheses[0].Category);
        Assert.True(analysis.Hypotheses[0].Confidence > 0.34);
    }

    /// <summary>
    /// A storage verdict is contradicted by CPU-bound time, and that contradiction rests on the same
    /// reading. A thread blocked on a disk read is off the processor for the whole frame and PresentMon
    /// reports every millisecond of it as CPU busy, so the rule would use a disk stall's own signature
    /// as proof that it was not one.
    /// </summary>
    [Fact]
    public void ABlockedThreadAlsoStopsCpuBusyFromRulingOutStorage()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: true));
        var disk = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.StreamingOrDiskStall);

        if (disk is not null)
        {
            Assert.DoesNotContain(disk.Evidence, item => item.Contains("av spike-tiden", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// An incident window is ninety seconds and can hold more than one cause. A wait that covers a small
    /// fraction of the window's CPU-bound time contradicts that fraction, not all of it.
    /// </summary>
    /// <remarks>
    /// Here a 120 ms wait lands on one slow frame in a window that lost 1 750 ms to CPU-bound spikes.
    /// Treating any overlap as decisive discarded the attribution for all of them, which hides whatever
    /// else was in the window — the failure this gate is proportionate to avoid.
    /// </remarks>
    [Fact]
    public void AWaitCoveringLittleOfTheWindowDoesNotDiscardTheWholeAttribution()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: true, waitMs: 120, extraCpuBoundSpikes: true));
        var resource = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.NotNull(resource);
        Assert.Contains(resource!.Evidence, item => item.Contains("var CPU-bundna", StringComparison.Ordinal));
        Assert.DoesNotContain(resource.Evidence, item => item.Contains("NEDVIKTAD", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same window with a wait that covers nearly all of its CPU-bound time is still contradicted.
    /// Without this the test above would pass for a gate that never fires.
    /// </summary>
    [Fact]
    public void AWaitCoveringMostOfTheWindowStillDiscardsIt()
    {
        var analysis = _engine.Analyze(BuildIncident(withTrace: true, waitMs: 1_700, extraCpuBoundSpikes: true));
        var resource = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.NotNull(resource);
        Assert.Contains(resource!.Evidence, item => item.Contains("BORTSETT", StringComparison.Ordinal));
    }

    /// <summary>An incident whose trace holds latency and coverage figures and no CPU samples.</summary>
    private static IncidentRecord BuildIncidentWithCoverageOnlyTrace()
    {
        return WithTraceMetrics(new Dictionary<string, double>
        {
            ["traceDurationSeconds"] = 39.4,
            ["dpcMaxMs"] = 0.42,
            ["isrMaxMs"] = 0.11,
            ["cswitchCount"] = 412_004,
            ["cpuSampleCount"] = 0,
        });
    }

    /// <summary>The same trace with the sampled-profile stream present and attributed to the game.</summary>
    private static IncidentRecord BuildIncidentWithCpuSampledTrace()
    {
        return WithTraceMetrics(new Dictionary<string, double>
        {
            ["traceDurationSeconds"] = 39.4,
            ["dpcMaxMs"] = 0.42,
            ["cpuSampleCount"] = 372_918,
            ["cpuSubjectIsGame"] = 1,
            ["cpuSampledSeconds"] = 38.9,
            ["cpuTotalCores"] = 2.5,
            ["cpuSubjectProcessCores"] = 1.78,
            ["cpuBusiestThreadCores"] = 0.47,
        });
    }

    private static IncidentRecord WithTraceMetrics(IReadOnlyDictionary<string, double> metrics)
    {
        var incident = BuildIncident(withTrace: false);

        return incident with
        {
            Events = incident.Events
                .Concat([new ArtifactEvidence(
                    incident.Marker.MarkedAt,
                    ArtifactKind.EtlTrace,
                    "ETL-trace analyserad.",
                    metrics)])
                .ToArray(),
        };
    }

    private static IncidentRecord BuildIncident(
        bool withTrace,
        double waitMs = 568.9,
        bool extraCpuBoundSpikes = false)
    {
        var events = new List<TelemetryEvent>();
        var markedAt = Start.AddSeconds(30);

        for (var i = 0; i < 600; i++)
        {
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.67),
                16.67,
                GpuBusyMs: 6.1,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 6.4,
                CpuWaitMs: 9.9));
        }

        // The frame itself, with the numbers PresentMon reported for it.
        events.Add(new FrameTelemetrySample(
            markedAt,
            586.0,
            GpuBusyMs: 40.0,
            DisplayLatencyMs: 20,
            MsBetweenPresents: 586.0,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: 585.2,
            CpuWaitMs: 0.4));

        // Four further CPU-bound spikes in the two seconds after the marker, making roughly 1 750 ms of
        // CPU-bound spike time in the window. A short wait covers almost none of it; a 1 700 ms one
        // covers nearly all of it. Which of those is true is what the gate has to distinguish.
        if (extraCpuBoundSpikes)
        {
            foreach (var offset in new[] { 600, 900, 1_200, 1_500 })
            {
                events.Add(new FrameTelemetrySample(
                    markedAt.AddMilliseconds(offset),
                    290.0,
                    GpuBusyMs: 9.0,
                    DisplayLatencyMs: 20,
                    MsBetweenPresents: 290.0,
                    Dropped: false,
                    ProcessName: "FiveM_b3407_GTAProcess.exe",
                    CpuBusyMs: 288.0,
                    CpuWaitMs: 0.3));
            }
        }

        for (var second = 0; second < 60; second++)
        {
            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: 70,
                PerCoreUsagePercent: new Dictionary<string, double>(),
                MemoryCommitPercent: 45,
                AvailableMemoryMb: 15_000,
                TopCpuProcesses: [],
                TopDiskProcesses: [],
                DiskAverageLatencyMs: 1.1,
                DiskQueueLength: 0.3,
                HardFaultPagesPerSecond: 1));
        }

        events.Add(new ProcessTelemetrySample(
            markedAt,
            7768,
            "FiveM_b3407_GTAProcess",
            CpuUsagePercent: 61,
            PrivateBytes: 9_000L * 1024 * 1024,
            WorkingSetBytes: 9_000L * 1024 * 1024,
            ThreadCount: 64,
            ReadBytesPerSecond: 400_000,
            WriteBytesPerSecond: 20_000));

        if (withTrace)
        {
            // What the ETL says about the same frame: the main thread was off the processor for 568.9 of
            // the 586 ms, released by a synchronisation thread behind it.
            events.Add(new ArtifactEvidence(
                markedAt,
                ArtifactKind.EtlTrace,
                "Schemaläggning: aktiv GTA-tråd låg av CPU:n.",
                new Dictionary<string, double>
                {
                    ["gameThreadWaitThreadId"] = 25872,
                    ["gameThreadLongWaitCount"] = 5,
                    ["gameThreadUserRequestWaitCount"] = 5,
                    ["gameThreadMaxWaitMs"] = waitMs,
                    ["gameThreadWaitIntervalCount"] = 1,
                    ["gameThreadWait0StartUnixMs"] = markedAt.ToUnixTimeMilliseconds(),
                    ["gameThreadWait0EndUnixMs"] = markedAt.AddMilliseconds(waitMs).ToUnixTimeMilliseconds(),
                    ["gameThreadWait0DurationMs"] = waitMs,
                    ["gameThreadWait0UserRequest"] = 1,
                }));
        }

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), markedAt, IncidentSeverity.Severe, "Auto: 586 ms frame"),
            Start,
            Start.AddSeconds(90),
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                120,
                "Disabled",
                ObsDetectedAtStart: true,
                ServerProfileName: string.Empty,
                SessionStartedAt: Start,
                SessionEndedAt: null),
            events,
            Analysis: null,
            Attachments: []);
    }
}
