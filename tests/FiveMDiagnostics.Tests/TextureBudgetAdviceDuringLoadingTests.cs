namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The advice written four minutes into loading on 20 September, and why it must not be.
/// </summary>
/// <remarks>
/// At 22:14 the session wrote "spelet behöver 5,4 GB … Det är 1,6 GB för mycket … Extended Texture
/// Budget 3, ungefär 15 % på reglaget". The game held 0.1 GB at that moment — it had not been given a
/// byte of its budget yet — and the desktop's row stood at 4.4 GB, a figure the same session had
/// rejected as double counting three minutes earlier. Following it would have taken the slider from
/// half to a seventh, and the evening's actual answer, written at 00:17 once the game had filled up,
/// was 8.
/// </remarks>
public sealed class TextureBudgetAdviceDuringLoadingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 20, 10, 44, TimeSpan.Zero);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    /// <summary>
    /// During loading the readings are written and the recommendation is not, and the line says which.
    /// </summary>
    [Fact]
    public void TheRecommendationIsHeldBackWhileTheGameIsStillLoading()
    {
        var monitor = new VramBudgetMonitor();
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start, BudgetScale: 10));

        monitor.Observe(Adapter(Start, usedGigabytes: 5.1));
        var report = monitor.Observe(Sample(Start, gameGigabytes: 0.1));

        Assert.NotNull(report);

        // The card's own readings still stand: nothing about them depends on the game being loaded.
        Assert.Contains("VRAM-budget:", report!.Message, StringComparison.Ordinal);
        Assert.Contains("Extended Texture Budget står på 10", report.Message, StringComparison.Ordinal);

        // The advice does not.
        Assert.Contains("hålls inne", report.Message, StringComparison.Ordinal);
        Assert.Contains("har alltså inte fyllt den ännu", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("för mycket, och spelet kommer att ta dem", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Extended Texture Budget 3", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once the game has filled its budget the advice arrives on its own, without waiting for the stream
    /// stack to change state.
    /// </summary>
    /// <remarks>
    /// The budget line is written about twice an evening. Holding the first one and leaving it at that
    /// would trade a wrong recommendation for none at all on any evening whose stream stack is already
    /// running when the game starts.
    /// </remarks>
    [Fact]
    public void OnceTheGameHasFilledItsBudgetTheAdviceIsWritten()
    {
        var monitor = new VramBudgetMonitor();
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start, BudgetScale: 10));

        monitor.Observe(Adapter(Start, usedGigabytes: 5.1));
        var held = monitor.Observe(Sample(Start, gameGigabytes: 0.1));
        Assert.Contains("hålls inne", held!.Message, StringComparison.Ordinal);

        // Two hours later: the game holds 7.3 GB against a 5.4 GB streaming budget, which is the
        // evening's own reading at 00:17.
        var later = Start.AddHours(2);
        monitor.Observe(Adapter(later, usedGigabytes: 8.9));
        var report = monitor.Observe(Sample(later, gameGigabytes: 7.3));

        Assert.NotNull(report);
        Assert.DoesNotContain("hålls inne", report!.Message, StringComparison.Ordinal);
        Assert.Contains("Texturbudgeten är större än vad kortet klarar", report.Message, StringComparison.Ordinal);

        // The overhead the game holds on top of the budget is measured by now, so the value suggested is
        // a step, not a collapse.
        Assert.DoesNotContain("Extended Texture Budget 3,", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Moving the slider is worth a fresh budget line, because the ceiling is what the line is about.
    /// </summary>
    /// <remarks>
    /// The line is written about twice an evening and a changed ceiling used not to be one of the
    /// occasions, so an evening where the slider was dragged mid-session carried a budget line about
    /// the value it no longer ran on. The hold added above made that worse: a first line written during
    /// loading would have been the only one, and it says nothing about what to set.
    /// </remarks>
    [Fact]
    public void ChangingTheBudgetWritesAFreshLineAboutTheNewCeiling()
    {
        var monitor = new VramBudgetMonitor();
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start, BudgetScale: 10));

        monitor.Observe(Adapter(Start, usedGigabytes: 8.9));
        var first = monitor.Observe(Sample(Start, gameGigabytes: 7.3));
        Assert.Contains("Extended Texture Budget står på 10", first!.Message, StringComparison.Ordinal);

        // Nothing changed, so nothing is repeated.
        monitor.Observe(Adapter(Start.AddMinutes(1), usedGigabytes: 8.9));
        Assert.Null(monitor.Observe(Sample(Start.AddMinutes(1), gameGigabytes: 7.3)));

        // The slider is dragged up in the pause menu.
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start.AddMinutes(30), BudgetScale: 16));

        var later = Start.AddMinutes(31);
        monitor.Observe(Adapter(later, usedGigabytes: 8.9));
        var report = monitor.Observe(Sample(later, gameGigabytes: 7.3));

        Assert.NotNull(report);
        Assert.Contains("Extended Texture Budget står på 16", report!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Forcing a fresh budget line must not hand a single sample the authority the hysteresis denies it.
    /// </summary>
    /// <remarks>
    /// The budget line's "said once" flag was also what told the stream stack tracker whether it had
    /// ever observed a state — and its first observation is adopted without waiting for three agreeing
    /// samples, correctly, because there is nothing to flap against yet. Clearing the flag on a changed
    /// ceiling re-armed that branch, so one odd process sample could silently reset the state and the
    /// next three ordinary ones would announce a transition that never happened. Eighteen such lines in
    /// sixteen minutes is why the hysteresis exists.
    /// </remarks>
    [Fact]
    public void AChangedCeilingDoesNotReArmTheStreamStackHysteresis()
    {
        var monitor = new VramBudgetMonitor();
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start, BudgetScale: 10));

        // The stack is running and the state is established.
        monitor.Observe(Adapter(Start, usedGigabytes: 8.9));
        Assert.NotNull(monitor.Observe(Sample(Start, gameGigabytes: 7.3)));

        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start.AddMinutes(30), BudgetScale: 16));

        // One sample whose obs64 row is unreadable, the way an excluded row looks.
        var at = Start.AddMinutes(31);
        monitor.Observe(Adapter(at, usedGigabytes: 8.9));
        monitor.Observe(Sample(at, gameGigabytes: 7.3, streamGigabytes: 0));

        // The stack never went anywhere, so nothing may announce that it came back.
        for (var i = 2; i <= 6; i++)
        {
            var later = Start.AddMinutes(30 + i);
            monitor.Observe(Adapter(later, usedGigabytes: 8.9));
            var report = monitor.Observe(Sample(later, gameGigabytes: 7.3));

            if (report is not null)
            {
                Assert.DoesNotContain("Streamstacken startade", report.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Streamstacken avslutades", report.Message, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// A configuration file that could not be read is not a changed ceiling.
    /// </summary>
    /// <remarks>
    /// The reader returns null for an <see cref="IOException"/> as readily as for a missing file, and
    /// the client rewrites <c>fivem.cfg</c> while the game runs. Treating that as a change threw away
    /// the measured overhead and put the recommendation back behind its hold — over a file that had not
    /// changed at all.
    /// </remarks>
    [Fact]
    public void AnUnreadableConfigIsNotAChangedCeiling()
    {
        var monitor = new VramBudgetMonitor();
        monitor.SetTextureBudget(new FiveMClientConfig("fivem.cfg", Start, BudgetScale: 10));

        monitor.Observe(Adapter(Start, usedGigabytes: 8.9));
        monitor.Observe(Sample(Start, gameGigabytes: 7.3));
        Assert.True(monitor.OverheadBytes > 0);

        // One poll where the file could not be read.
        monitor.SetTextureBudget(null);

        Assert.True(monitor.OverheadBytes > 0);
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double usedGigabytes) =>
        new(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 40,
            MemoryBandwidthUtilizationPercent: 15,
            UsedVramBytes: (ulong)(usedGigabytes * Gigabyte),
            TotalVramBytes: 10UL * Gigabyte,
            EncoderUtilizationPercent: 37,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: 1);

    private static GpuProcessMemorySample Sample(
        DateTimeOffset timestamp,
        double gameGigabytes,
        double streamGigabytes = 0.6) =>
        new(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(13280, "FiveM_b3407_GTAProcess", (ulong)(gameGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(2704, "obs64", (ulong)(streamGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(992, "dwm", (ulong)(1.37 * Gigabyte), 0, 1),
            ]);
}
