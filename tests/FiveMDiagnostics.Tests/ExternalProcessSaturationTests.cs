namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// Counting suspects said the same thing about two idle overlays as about a machine whose spare cores
/// had been taken.
/// </summary>
/// <remarks>
/// Built from the 26 August window at 20:38–20:42. The deep capture of ten seconds inside it holds
/// OneDrive at 3.68 of the machine's eight physical cores — more CPU than the game — 1.2 million file
/// operations, and the game's render and main threads sharing a physical core 86% of the time. The
/// incident was ranked a FiveM script spike at 60%, with external interference second at 51%.
/// </remarks>
public sealed class ExternalProcessSaturationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 18, 38, 55, TimeSpan.Zero);

    private readonly FiveMCorrelationEngine _engine = new();

    [Fact]
    public void ABackgroundProcessHeavierThanTheGameOutranksAScriptSpike()
    {
        var analysis = _engine.Analyze(BuildIncident(saturated: true));

        Assert.Equal(RootCauseCategory.ExternalProcessInterference, analysis.Hypotheses[0].Category);
    }

    [Fact]
    public void TheEvidenceNamesTheSaturationRatherThanJustTheProcesses()
    {
        var analysis = _engine.Analyze(BuildIncident(saturated: true));
        var external = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.ExternalProcessInterference);

        Assert.Contains(external.Evidence, item => item.Contains("Maskinen var mättad", StringComparison.Ordinal));
        Assert.Contains(external.Evidence, item => item.Contains("mer CPU än spelet", StringComparison.Ordinal));
        Assert.True(external.Confidence > 0.78, $"the count-only ceiling still stands at {external.Confidence:P0}");
    }

    /// <summary>
    /// The other direction, and the reason the terms are conditional: with cores to spare the scheduler
    /// runs both, so a busy neighbour on an idle machine is not evidence of anything.
    /// </summary>
    [Fact]
    public void TheSameProcessesOnAnIdleMachineDoNotReachTheSameVerdict()
    {
        var analysis = _engine.Analyze(BuildIncident(saturated: false));
        var external = analysis.Hypotheses.First(item => item.Category == RootCauseCategory.ExternalProcessInterference);

        Assert.DoesNotContain(external.Evidence, item => item.Contains("Maskinen var mättad", StringComparison.Ordinal));
        Assert.DoesNotContain(external.Evidence, item => item.Contains("mer CPU än spelet", StringComparison.Ordinal));
        Assert.True(external.Confidence <= 0.78, $"an idle machine reached {external.Confidence:P0}");
    }

    /// <summary>
    /// Each suspect's peak is a maximum over a ninety second window, so adding the peaks together
    /// measures a load the machine need never have carried.
    /// </summary>
    /// <remarks>
    /// The two suspects peak at 16% and 15% in opposite halves of the window and never overlap, against
    /// a game holding 21%. Added together they make 31% and clear the game; at the busiest single instant
    /// the background held 16.3%, and only the second of those is a fact about the machine.
    /// </remarks>
    [Fact]
    public void PeaksAtDifferentTimesAreNotAddedTogether()
    {
        var staggered = _engine.Analyze(BuildIncident(saturated: true, concurrent: false));
        var external = staggered.Hypotheses.First(item => item.Category == RootCauseCategory.ExternalProcessInterference);

        Assert.Contains(external.Evidence, item => item.Contains("Maskinen var mättad", StringComparison.Ordinal));
        Assert.DoesNotContain(external.Evidence, item => item.Contains("mer CPU än spelet", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same two processes at the same peaks, this time actually running together, do clear the game.
    /// Without this the test above would pass for a rule that simply never fires.
    /// </summary>
    [Fact]
    public void TheSamePeaksHeldAtOnceDoClearTheGame()
    {
        var together = _engine.Analyze(BuildIncident(saturated: true, concurrent: true, syncCpu: 16, clientCpu: 15));
        var external = together.Hypotheses.First(item => item.Category == RootCauseCategory.ExternalProcessInterference);

        Assert.Contains(external.Evidence, item => item.Contains("mer CPU än spelet", StringComparison.Ordinal));
    }

    private static IncidentRecord BuildIncident(
        bool saturated,
        bool concurrent = true,
        double syncCpu = 32.7,
        double clientCpu = 11.9)
    {
        var events = new List<TelemetryEvent>();
        var markedAt = Start.AddSeconds(46);

        for (var i = 0; i < 600; i++)
        {
            var slow = i is 460;
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.67),
                slow ? 586.0 : 16.67,
                GpuBusyMs: slow ? 40.0 : 7.1,
                DisplayLatencyMs: 20,
                MsBetweenPresents: slow ? 586.0 : 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: slow ? 585.2 : 8.6,
                CpuWaitMs: slow ? 0.4 : 7.9));
        }

        // Measured in the capture: OneDrive 3.68 cores of 16 logical, the game 3.34. Both figures here
        // are percent of the whole machine, which is what both collectors report.
        // The staggered case uses peaks that only clear the game when wrongly added together: 16 + 15
        // against the game's 21, neither of which exceeds it alone.
        if (!concurrent)
        {
            syncCpu = 16;
            clientCpu = 15;
        }

        var sync = new ProcessActivity("OneDrive.Sync.Service", 4321, saturated ? syncCpu : 13.0, 481L * 1024 * 1024);
        var client = new ProcessActivity("OneDrive", 4322, saturated ? clientCpu : 4.0, 90L * 1024 * 1024);

        var idleSync = sync with { CpuPercent = 0.4 };
        var idleClient = client with { CpuPercent = 0.3 };

        for (var second = 0; second < 60; second++)
        {
            // Concurrent: both busy in every sample. Otherwise each is busy only in its own half of the
            // window, so their peaks are real and their sum never happened.
            var firstHalf = second < 30;
            var samples = concurrent
                ? new[] { sync, client }
                : firstHalf ? [sync, idleClient] : new[] { idleSync, client };

            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(second),
                TotalCpuUsagePercent: saturated ? 86 : 41,
                PerCoreUsagePercent: new Dictionary<string, double>(),
                MemoryCommitPercent: 45,
                AvailableMemoryMb: 15_000,
                TopCpuProcesses: samples,
                TopDiskProcesses: samples,
                DiskAverageLatencyMs: 1.4,
                DiskQueueLength: 0.6,
                HardFaultPagesPerSecond: 3));
        }

        events.Add(new ProcessTelemetrySample(
            markedAt,
            7768,
            "FiveM_b3407_GTAProcess",
            CpuUsagePercent: 21,
            PrivateBytes: 9_000L * 1024 * 1024,
            WorkingSetBytes: 9_000L * 1024 * 1024,
            ThreadCount: 64,
            ReadBytesPerSecond: 400_000,
            WriteBytesPerSecond: 20_000));

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
