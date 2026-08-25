namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;

/// <summary>
/// The per-process VRAM table exists to answer "which program is holding the last gigabyte", so the
/// cases that matter are the ones that would answer it with the wrong program: an instance name parsed
/// loosely enough to pick up the wrong id, and a process whose allocations are spread over several
/// counter instances.
/// </summary>
public sealed class GpuProcessMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pid_29268_luid_0x00000000_0x0000c42a_phys_0", 29268)]
    [InlineData("pid_4_luid_0x00000000_0x000004d1_phys_0", 4)]

    // The counter set has grown fields before. An unknown shape must drop out rather than be guessed at.
    [InlineData("pid_29268", 29268)]
    [InlineData("luid_0x00000000_0x0000c42a_phys_0", null)]
    [InlineData("pid__luid_0x00000000", null)]
    [InlineData("pid_notanumber_luid_0x0", null)]
    [InlineData("pid_0_luid_0x00000000", null)]
    [InlineData("", null)]
    public void InstanceNamesResolveToTheProcessThatOwnsThem(string instance, int? expected)
    {
        Assert.Equal(expected, GpuProcessMemoryAggregator.ParseProcessId(instance));
    }

    /// <summary>
    /// One process has an instance per adapter and per memory segment. Reporting the first one found
    /// would show a laptop's integrated share and omit the discrete card's — the same number, wrong by
    /// an order of magnitude, with nothing in the output to say so.
    /// </summary>
    [Fact]
    public void MemoryIsSummedOverEveryInstanceBelongingToTheSameProcess()
    {
        var usage = GpuProcessMemoryAggregator.Aggregate(
            [
                Reading("pid_100_luid_0x00000000_0x0000c42a_phys_0", 4_000_000_000),
                Reading("pid_100_luid_0x00000000_0x0000c42a_phys_1", 2_000_000_000),
                Reading("pid_200_luid_0x00000000_0x0000c42a_phys_0", 500_000_000),
            ],
            [Reading("pid_100_luid_0x00000000_0x0000c42a_phys_0", 1_000_000_000)],
            new Dictionary<int, string> { [100] = "FiveM_b3407_GTAProcess", [200] = "obs64" },
            topCount: 10);

        var game = Assert.Single(usage, item => item.ProcessId == 100);
        Assert.Equal(6_000_000_000UL, game.DedicatedBytes);
        Assert.Equal(1_000_000_000UL, game.SharedBytes);
        Assert.Equal("FiveM_b3407_GTAProcess", game.ProcessName);
    }

    [Fact]
    public void ProcessesAreRankedByDedicatedMemoryAndTrimmedToTheTop()
    {
        var usage = GpuProcessMemoryAggregator.Aggregate(
            [
                Reading("pid_1_luid_0x0_0x1_phys_0", 100),
                Reading("pid_2_luid_0x0_0x1_phys_0", 900),
                Reading("pid_3_luid_0x0_0x1_phys_0", 500),
            ],
            [],
            new Dictionary<int, string>(),
            topCount: 2);

        Assert.Equal([2, 3], usage.Select(item => item.ProcessId));
    }

    /// <summary>
    /// A process the name lookup missed still has to appear. It exited between the counter read and the
    /// process enumeration, and dropping it would remove memory from the table that is really allocated.
    /// </summary>
    [Fact]
    public void ProcessesWithoutAResolvedNameAreStillReported()
    {
        var usage = GpuProcessMemoryAggregator.Aggregate(
            [Reading("pid_4242_luid_0x0_0x1_phys_0", 700)],
            [],
            new Dictionary<int, string>(),
            topCount: 5);

        Assert.Equal("pid 4242", Assert.Single(usage).ProcessName);
    }

    /// <summary>
    /// A negative reading is PDH's way of reporting an error status for one instance. Adding it would
    /// subtract from a process's real total.
    /// </summary>
    [Fact]
    public void FailedInstanceReadingsDoNotReduceAProcessTotal()
    {
        var usage = GpuProcessMemoryAggregator.Aggregate(
            [
                Reading("pid_100_luid_0x0_0x1_phys_0", 8_000_000),
                Reading("pid_100_luid_0x0_0x1_phys_1", -1),
            ],
            [],
            new Dictionary<int, string>(),
            topCount: 5);

        Assert.Equal(8_000_000UL, Assert.Single(usage).DedicatedBytes);
    }

    /// <summary>
    /// The sample's own total is what a report compares against NVML's adapter figure, so it has to be
    /// the sum of what is listed rather than of what was measured before trimming.
    /// </summary>
    [Fact]
    public void SampleTotalsAndTopListComeFromTheProcessesItCarries()
    {
        var sample = new GpuProcessMemorySample(
            Now,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(100, "FiveM", 6_000_000_000, 0),
                new GpuProcessMemoryUsage(200, "obs64", 500_000_000, 0),
                new GpuProcessMemoryUsage(300, "chrome", 400_000_000, 0),
            ]);

        Assert.Equal(6_900_000_000UL, sample.TotalDedicatedBytes);
        Assert.Equal(["FiveM", "obs64"], sample.Top(2).Select(item => item.ProcessName));
        Assert.Equal(5.59, Math.Round(sample.Processes[0].DedicatedGigabytes, 2));
    }

    /// <summary>
    /// The counters are read through PDH rather than the managed counter API, which means the struct
    /// layout and the two-call buffer protocol are ours to get right. This proves the whole path on the
    /// machine it runs on: any Windows box has processes holding GPU memory for the desktop alone.
    /// </summary>
    [Fact]
    public void TheWindowsCountersCanActuallyBeRead()
    {
        using var probe = GpuProcessMemoryProbe.TryOpen(out var openError);
        if (probe is null)
        {
            // A machine with a broken or rebuilt counter registry is a real configuration, and the
            // collector degrades to a warning there rather than failing. So does this test.
            Assert.False(string.IsNullOrWhiteSpace(openError));
            return;
        }

        Assert.True(probe.TryRead(out var dedicated, out _, out var readError), readError);
        Assert.Null(readError);

        var names = System.Diagnostics.Process.GetProcesses().ToDictionary(item => item.Id, item => item.ProcessName);
        var usage = GpuProcessMemoryAggregator.Aggregate(dedicated, [], names, topCount: 5);
        Assert.NotEmpty(usage);

        // The failure this guards against is a struct layout error, which reads the status word as part
        // of the value and produces byte counts in the exabytes rather than an exception.
        Assert.All(usage, item => Assert.InRange(item.DedicatedBytes, 1UL, 64UL * 1024 * 1024 * 1024));

        // And that the ids in the instance names are real ones. A parser that read the wrong digits
        // would still produce a plausible table, just one where nothing could be named.
        Assert.Contains(usage, item => !item.ProcessName.StartsWith("pid ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The manager holds one collector instance and runs it again for every session, so a session that
    /// could not open the counters must not disable the next one.
    /// </summary>
    /// <remarks>
    /// The bug this pins down was silent in exactly the way that matters: an "unavailable" flag set on a
    /// first failed session was never cleared, so every later session returned immediately — after
    /// opening a counter query it then never closed.
    /// </remarks>
    [Fact]
    public async Task AFailedSessionDoesNotDisableTheNextOne()
    {
        var probe = new FakeProbe();
        var attempt = 0;
        var collector = new GpuProcessMemoryCollector(() =>
            ++attempt == 1 ? (null, "counters are broken") : (probe, null));

        var failed = await RunOneSessionAsync(collector);
        Assert.Empty(failed);

        var recovered = await RunOneSessionAsync(collector);
        var sample = Assert.IsType<GpuProcessMemorySample>(Assert.Single(recovered));
        Assert.True(sample.IsAvailable);
        Assert.Equal(880_000_000UL, Assert.Single(sample.Processes).DedicatedBytes);
    }

    /// <summary>A session that ends releases the counter query it opened, however it ends.</summary>
    [Fact]
    public async Task TheCounterQueryIsClosedWhenTheSessionEnds()
    {
        var probe = new FakeProbe();
        var collector = new GpuProcessMemoryCollector(() => (probe, null));

        await RunOneSessionAsync(collector);

        Assert.True(probe.IsDisposed);
    }

    /// <summary>
    /// Runs the collector until it has produced one sample, then cancels — the way a session ends.
    /// </summary>
    private static async Task<IReadOnlyList<TelemetryEvent>> RunOneSessionAsync(GpuProcessMemoryCollector collector)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<TelemetryEvent>();
        using var cancellation = new CancellationTokenSource();
        var directory = Path.Combine(Path.GetTempPath(), "FiveMDiagnostics.Tests", Guid.NewGuid().ToString("N"));

        var settings = DiagnosticsSettings.CreateDefault();
        settings.WorkingDirectory = directory;
        settings.Gpu.ProcessMemoryInterval = TimeSpan.FromMilliseconds(1);

        var context = new CollectorContext(
            channel.Writer,
            settings,
            new SilentStatusSink(),
            new AlwaysRunningProcess(),
            () => DateTimeOffset.UnixEpoch);

        // Cancelled from the writer side rather than on a timer, so the test neither races nor sleeps.
        var run = collector.RunAsync(context, cancellation.Token);
        var events = new List<TelemetryEvent>();
        var reader = channel.Reader;

        var firstSample = Task.Run(async () =>
        {
            if (await reader.WaitToReadAsync(cancellation.Token).ConfigureAwait(false) && reader.TryRead(out var telemetry))
            {
                events.Add(telemetry);
            }
        });

        // A collector that returns without producing anything is the failed-session case, and it
        // finishes on its own.
        await Task.WhenAny(run, firstSample).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // How a session always ends.
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log file is the collector's, not the test's business.
        }

        return events;
    }

    private sealed class FakeProbe : IGpuProcessMemoryProbe
    {
        public bool IsDisposed { get; private set; }

        public bool TryRead(
            out IReadOnlyList<KeyValuePair<string, long>> dedicated,
            out IReadOnlyList<KeyValuePair<string, long>> shared,
            out string? error)
        {
            dedicated = [new KeyValuePair<string, long>("pid_1992_luid_0x0_0x1_phys_0", 880_000_000)];
            shared = [];
            error = null;
            return true;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class SilentStatusSink : IDiagnosticStatusSink
    {
        public void Report(StatusLevel level, string source, string message)
        {
        }
    }

    private sealed class AlwaysRunningProcess : ITargetProcessResolver
    {
        public TargetProcessInfo? TryGetTargetProcess()
        {
            return new TargetProcessInfo(1, "FiveM_b3407_GTAProcess", null, DateTimeOffset.UnixEpoch);
        }
    }

    private static KeyValuePair<string, long> Reading(string instance, long value)
    {
        return new KeyValuePair<string, long>(instance, value);
    }
}
