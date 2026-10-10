namespace FiveMDiagnostics.Tests;

using System.Threading.Channels;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Fakes;
using FiveMDiagnostics.Integrations.Obs;

/// <summary>
/// A player who has said they do not stream gets nothing about OBS: it is not polled, the VRAM budget
/// has no stream stack, and an incident's analysis does not mention a program they never asked about.
/// </summary>
public sealed class NotStreamingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 19, 0, 0, TimeSpan.Zero);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    [Fact]
    public async Task TheObsCollectorWritesNothingAndSaysNothing()
    {
        var settings = DiagnosticsSettings.CreateDefault();
        settings.Streams = false;
        var channel = Channel.CreateUnbounded<TelemetryEvent>();
        var sink = new RecordingStatusSink();
        var context = new CollectorContext(channel.Writer, settings, sink, new StubProcessResolver(), () => Start);

        // Returns on its own: a collector that polled would only stop on the token.
        await new ObsTelemetryCollector().RunAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(channel.Reader.TryRead(out _));
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void ASettingsFileWithoutTheAnswerKeepsMeasuringTheStream()
    {
        Assert.True(new DiagnosticsSettings().MeasuresStream);
        Assert.False(new DiagnosticsSettings { Streams = false }.MeasuresStream);
    }

    [Fact]
    public void TheBudgetLineCountsObsAsDesktopAndNeverNamesAStreamStack()
    {
        var monitor = new VramBudgetMonitor(measuresStream: false);
        monitor.Observe(Adapter(Start, usedGigabytes: 6.0));

        var report = monitor.Observe(Sample(Start, gameGigabytes: 3.5));

        Assert.NotNull(report);
        Assert.DoesNotContain("streamstack", report!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skrivbordet och övriga program håller 2,5 GB", report.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, report.StreamStackBytes);
    }

    [Fact]
    public void AStreamerStillGetsTheStreamStackSplitOut()
    {
        var monitor = new VramBudgetMonitor();
        monitor.Observe(Adapter(Start, usedGigabytes: 6.0));

        var report = monitor.Observe(Sample(Start, gameGigabytes: 3.5));

        Assert.NotNull(report);
        Assert.Contains("streamstacken 0,9 GB", report!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncidentWithoutObsSamplesDoesNotMentionObs()
    {
        var incident = FakeScenarioGenerator.Create(FakeScenarioKind.FiveMResourceSpike).ToIncidentRecord();
        incident = incident with { Events = incident.Events.Where(item => item is not ObsTelemetrySample).ToArray() };

        var analysis = new FiveMCorrelationEngine().Analyze(incident);

        Assert.DoesNotContain("OBS", analysis.Summary, StringComparison.Ordinal);
        Assert.All(analysis.Hypotheses.SelectMany(item => item.Evidence), text => Assert.DoesNotContain("OBS", text, StringComparison.Ordinal));
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double usedGigabytes) =>
        new(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 40,
            MemoryBandwidthUtilizationPercent: 21,
            UsedVramBytes: (ulong)(usedGigabytes * Gigabyte),
            TotalVramBytes: 10UL * Gigabyte,
            EncoderUtilizationPercent: 0,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 59,
            ThrottleReasons: [],
            AdapterCount: 1);

    private static GpuProcessMemorySample Sample(DateTimeOffset timestamp, double gameGigabytes) =>
        new(
            timestamp,
            IsAvailable: true,
            [
                new GpuProcessMemoryUsage(31076, "FiveM_b3407_GTAProcess", (ulong)(gameGigabytes * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(7548, "obs64", (ulong)(0.9 * Gigabyte), 0, 1),
                new GpuProcessMemoryUsage(2244, "dwm", (ulong)(1.33 * Gigabyte), 0, 1),
            ]);

    private sealed class RecordingStatusSink : IDiagnosticStatusSink
    {
        public List<DiagnosticStatusEntry> Entries { get; } = [];

        public void Report(StatusLevel level, string source, string message)
            => Entries.Add(new DiagnosticStatusEntry(DateTimeOffset.UtcNow, level, source, message));
    }

    private sealed class StubProcessResolver : ITargetProcessResolver
    {
        public TargetProcessInfo? TryGetTargetProcess()
            => new(1234, "FiveM_b3407_GTAProcess", null, DateTimeOffset.UtcNow);
    }
}
