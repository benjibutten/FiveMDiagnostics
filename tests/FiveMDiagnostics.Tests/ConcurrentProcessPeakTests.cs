namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// A program's peak is what its processes cost the machine at one moment, not the sum of moments.
/// </summary>
/// <remarks>
/// Folding several processes of one name into one row is right — the NUI layer's renderers cost the
/// machine together and the reader acts on the program. Taking each instance's own peak and adding
/// those was not: two helpers that spiked twelve seconds apart were reported as one program holding
/// both at once. The figure was larger than anything the machine ever saw, and it decided the ranking.
/// </remarks>
public sealed class ConcurrentProcessPeakTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 20, 4, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset MarkedAt = Start.AddSeconds(46);

    [Fact]
    public void PeaksFromDifferentMomentsAreNotAddedTogether()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(concurrent: false));

        var suspect = Assert.Single(analysis.SuspectedProcesses, item => item.ProcessName == "explorer");

        // 30 and 25 in two different samples: the machine's worst moment was 30.
        Assert.Equal(30, suspect.PeakCpuPercent, 1);
        Assert.DoesNotContain("processer med samma namn", suspect.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the rule, which is the reason the summing exists at all.
    /// </summary>
    [Fact]
    public void PeaksFromTheSameMomentStillAddUp()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(concurrent: true));

        var suspect = Assert.Single(analysis.SuspectedProcesses, item => item.ProcessName == "explorer");

        Assert.Equal(55, suspect.PeakCpuPercent, 1);
        Assert.Contains("2 processer med samma namn", suspect.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two instances of one program, either in the same sample or twelve seconds apart.
    /// </summary>
    private static IncidentRecord Incident(bool concurrent)
    {
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            var slow = i is 460;
            events.Add(new FrameTelemetrySample(
                Start.AddMilliseconds(i * 16.9),
                slow ? 281.0 : 16.9,
                GpuBusyMs: slow ? 16.1 : 5.0,
                DisplayLatencyMs: 20,
                MsBetweenPresents: slow ? 281.0 : 16.9,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: slow ? 251.2 : 7.8,
                CpuWaitMs: slow ? 4.76 : 8.8));
        }

        var first = new ProcessActivity("explorer", 7420, 30, 0);
        var second = new ProcessActivity("explorer", 7480, 25, 0);
        var perCore = Enumerable.Range(0, 16).ToDictionary(index => index.ToString(), _ => 38d);

        for (var at = 0; at < 90; at += 2)
        {
            IReadOnlyList<ProcessActivity> topCpu = (concurrent, at) switch
            {
                (true, 40) => [first, second],
                (false, 40) => [first],
                (false, 52) => [second],
                _ => [],
            };

            events.Add(new SystemTelemetrySample(
                Start.AddSeconds(at),
                TotalCpuUsagePercent: 38,
                PerCoreUsagePercent: perCore,
                MemoryCommitPercent: 58,
                AvailableMemoryMb: 12_000,
                TopCpuProcesses: topCpu,
                TopDiskProcesses: []));
        }

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), MarkedAt, IncidentSeverity.Severe, "Auto: 281 ms frame"),
            Start,
            Start.AddSeconds(90),
            PagingStallAgainstTheSeptember5SessionTests.Environment(),
            events,
            Analysis: null,
            Attachments: []);
    }
}
