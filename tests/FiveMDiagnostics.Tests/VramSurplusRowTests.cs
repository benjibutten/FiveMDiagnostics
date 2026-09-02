namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The compositor row that stayed in every report for two hours because it never exceeded the card.
/// </summary>
/// <remarks>
/// <para>
/// The per-row ceiling only fires once one process claims more than the whole card is using, and on
/// 1 September <c>dwm</c> never did while the game was running: a flat 3.5 GB on a 10 GB card that sat
/// between 82 and 94% full. It was named second largest VRAM holder in all 48 incidents of the evening,
/// and was finally caught at 01:14:38 — after the game exited and the card's own figure fell underneath
/// it. Two hours late, and only because the session happened to still be open.
/// </para>
/// <para>
/// What settles it earlier is the shape of the residual. A process table is expected to land slightly
/// <em>under</em> the adapter, since VRAM also holds framebuffers and allocations owned by nothing still
/// running. The evening's first sample summed to 11.95 GB against 8.16 GB reported: removing the
/// compositor leaves 8.41, a quarter gigabyte over and the expected shape; removing the game leaves
/// 5.66, two and a half gigabytes short, which is not.
/// </para>
/// </remarks>
public sealed class VramSurplusRowTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 21, 11, 28, TimeSpan.Zero);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    /// <summary>
    /// The evening as recorded, three samples in: the row whose removal reconciles the table is named,
    /// and the game — whose removal would undershoot by gigabytes — is not.
    /// </summary>
    [Fact]
    public void TheRowThatExplainsTheSurplusIsProvedWithoutExceedingTheCard()
    {
        var monitor = new VramAccountingMonitor();

        var proven = RunSamples(monitor, count: 3);

        var row = Assert.Single(proven);
        Assert.Equal("dwm", row.Process.ProcessName);
        Assert.Equal(DoubleCountProof.ExplainsSurplus, row.Proof);

        // And the point of proving it: the compositor is out of the top list, so what the reader sees is
        // the game and the process that can actually be closed.
        var annotated = monitor.Annotate(Sample(Start.AddSeconds(20)), out _);
        Assert.Equal(
            ["FiveM_b3407_GTAProcess", "Voicemod"],
            annotated.Top(2).Select(item => item.ProcessName));
    }

    /// <summary>
    /// Proved once. The surplus does not go away when the row explaining it is named — the collector
    /// keeps reporting the same table — so the arithmetic still fits on every sample that follows.
    /// </summary>
    /// <remarks>
    /// The verdict is held in the monitor and the caller is told what is <em>new</em>, because what the
    /// caller does with it is write a warning. Re-proving a row already proved writes that warning once
    /// per process sample, which is once every five seconds for the rest of the evening.
    /// </remarks>
    [Fact]
    public void TheRowIsProvedOnceAndNotOncePerSample()
    {
        var monitor = new VramAccountingMonitor();

        Assert.Single(RunSamples(monitor, count: 3));

        for (var index = 3; index < 10; index++)
        {
            var at = Start.AddSeconds(index * 5);
            monitor.Observe(Adapter(at, usedGigabytes: 8.16));
            monitor.Annotate(Sample(at), out var proven);

            Assert.Empty(proven);
        }

        // Still excluded, though. Silence here means "nothing new", not "never mind".
        var annotated = monitor.Annotate(Sample(Start.AddSeconds(60)), out _);
        Assert.DoesNotContain("dwm", annotated.Top(2).Select(item => item.ProcessName));
    }

    /// <summary>
    /// A process id recycled mid-proof hands nothing to its successor.
    /// </summary>
    /// <remarks>
    /// Two samples in, the id has a streak and one more agreeing sample would convict it. Windows
    /// reuses process ids freely, and an id that comes back as a different program is a different
    /// program: it starts at nothing, exactly as it would have if the first two samples had never been
    /// taken. The same rule <see cref="DoubleCountProof.ExceedsAdapter"/> has always applied to the
    /// finished verdict, applied to the evidence that produces it.
    /// </remarks>
    [Fact]
    public void ARecycledProcessIdDoesNotInheritTheStreak()
    {
        var monitor = new VramAccountingMonitor();

        for (var index = 0; index < 2; index++)
        {
            var at = Start.AddSeconds(index * 5);
            monitor.Observe(Adapter(at, usedGigabytes: 8.16));
            monitor.Annotate(Sample(at), out var proven);
            Assert.Empty(proven);
        }

        // 1904 is now something else with the same footprint. It has been seen once.
        var recycled = Start.AddSeconds(10);
        monitor.Observe(Adapter(recycled, usedGigabytes: 8.16));
        monitor.Annotate(Sample(recycled, compositorName: "ShellExperienceHost"), out var third);
        Assert.Empty(third);

        // And is convicted on its own third sample, not on its predecessor's.
        for (var index = 3; index < 5; index++)
        {
            var at = Start.AddSeconds(index * 5);
            monitor.Observe(Adapter(at, usedGigabytes: 8.16));
            monitor.Annotate(Sample(at, compositorName: "ShellExperienceHost"), out third);
        }

        var row = Assert.Single(third);
        Assert.Equal("ShellExperienceHost", row.Process.ProcessName);
    }

    /// <summary>
    /// One agreeing sample is not a proof. The two collectors never read at the same instant, and a game
    /// that allocated a gigabyte between them would otherwise convict whichever row happened to fit.
    /// </summary>
    [Fact]
    public void OneSampleIsNotEnough()
    {
        var monitor = new VramAccountingMonitor();

        Assert.Empty(RunSamples(monitor, count: 1));
        Assert.Empty(RunSamples(monitor, count: 1, from: 1));
    }

    /// <summary>
    /// A table that adds up is left alone, however large the compositor's own row happens to be.
    /// </summary>
    [Fact]
    public void ATableThatReconcilesProvesNothing()
    {
        var monitor = new VramAccountingMonitor();

        for (var index = 0; index < 5; index++)
        {
            var at = Start.AddSeconds(index * 5);
            monitor.Observe(Adapter(at, usedGigabytes: 8.60));
            monitor.Annotate(Sample(at, dwmGigabytes: 0.40), out var proven);
            Assert.Empty(proven);
        }
    }

    /// <summary>
    /// Two rows that would each fit means the arithmetic cannot say which, and guessing would exclude a
    /// process that really is holding what it reports.
    /// </summary>
    [Fact]
    public void AnAmbiguousSurplusProvesNothing()
    {
        var monitor = new VramAccountingMonitor();

        for (var index = 0; index < 5; index++)
        {
            var at = Start.AddSeconds(index * 5);

            // Two rows of 3.5 GB on a card reporting 8.16: removing either leaves the same 8.41.
            monitor.Observe(Adapter(at, usedGigabytes: 8.16));
            monitor.Annotate(
                new GpuProcessMemorySample(
                    at,
                    IsAvailable: true,
                    [
                        new GpuProcessMemoryUsage(1904, "dwm", (ulong)(3.51 * Gigabyte), 0, 1),
                        new GpuProcessMemoryUsage(9012, "obs64", (ulong)(3.51 * Gigabyte), 0, 1),
                        new GpuProcessMemoryUsage(23688, "FiveM_b3407_GTAProcess", (ulong)(4.93 * Gigabyte), 0, 1),
                    ]),
                out var proven);

            Assert.Empty(proven);
        }
    }

    private static IReadOnlyList<DoubleCountedRow> RunSamples(VramAccountingMonitor monitor, int count, int from = 0)
    {
        IReadOnlyList<DoubleCountedRow> proven = [];
        for (var index = from; index < from + count; index++)
        {
            var at = Start.AddSeconds(index * 5);
            monitor.Observe(Adapter(at, usedGigabytes: 8.16));
            monitor.Annotate(Sample(at), out proven);
        }

        return proven;
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double usedGigabytes)
    {
        return new GpuTelemetrySample(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 38,
            MemoryBandwidthUtilizationPercent: 15,
            UsedVramBytes: (ulong)(usedGigabytes * Gigabyte),
            TotalVramBytes: 10UL * Gigabyte,
            EncoderUtilizationPercent: 34,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    /// <summary>
    /// The evening's own table, to the hundredth of a gigabyte.
    /// </summary>
    /// <remarks>
    /// The top four rows are what a report shows; the accounted total is what the collector measured
    /// across all twenty-eight processes, and it is the figure the reconciliation is done against. On
    /// 1 September at 23:11:28 that read 11.95 GB against the card's 8.16.
    /// </remarks>
    private static GpuProcessMemorySample Sample(
        DateTimeOffset timestamp,
        double dwmGigabytes = 3.51,
        string compositorName = "dwm")
    {
        var accounted = (5.51 + dwmGigabytes) * Gigabyte;

        return new GpuProcessMemorySample(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(23688, "FiveM_b3407_GTAProcess", (ulong)(6.29 * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(1904, compositorName, (ulong)(dwmGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(7712, "Voicemod", (ulong)(1.03 * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(2244, "explorer", (ulong)(0.11 * Gigabyte), 0, 1),
            ],
            AllProcessesDedicatedBytes: (ulong)(accounted + (2.93 * Gigabyte)));
    }
}
