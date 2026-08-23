using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;
using FiveMDiagnostics.Export;

/// <summary>
/// The export bundle has to read the same on every machine that writes it.
/// </summary>
/// <remarks>
/// This is not hypothetical tidiness. The app runs on a Swedish install, where the default number
/// format writes 16.6 as "16,6" — inside a comma separated file. Every <c>metrics.csv</c> the app had
/// produced carried a decimal comma in the value column, so the column count varied per row and any
/// parser reading it either failed or silently misread the numbers. The report was affected too: a
/// "largest incident gap 90.00 s" line came out as "90,00".
/// <para>
/// Run under an explicit Swedish culture rather than the ambient one, so the guarantee holds on an
/// invariant-culture CI machine as well — which is exactly where a regression here would hide.
/// </para>
/// </remarks>
public sealed class ExportCultureTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 22, 21, 2, 46, TimeSpan.Zero);

    [Fact]
    public async Task TheBundleIsCultureInvariantEvenOnASwedishInstall()
    {
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;

        try
        {
            var swedish = new CultureInfo("sv-SE");
            CultureInfo.CurrentCulture = swedish;
            CultureInfo.CurrentUICulture = swedish;

            // Sanity check that this culture really does use a decimal comma, so the test cannot pass
            // because the culture failed to apply.
            Assert.Equal("16,60", 16.6.ToString("F2", CultureInfo.CurrentCulture));

            var (metrics, report) = await ExportAsync();

            Assert.Contains("16.60", metrics, StringComparison.Ordinal);
            Assert.DoesNotContain("16,60", metrics, StringComparison.Ordinal);
            Assert.Contains("largest incident gap 90.00 s", report, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    /// <summary>
    /// The specific corruption: a decimal comma in a comma separated file changes how many columns a
    /// row has, which is worse than a wrong number because it is not obviously wrong.
    /// </summary>
    [Fact]
    public async Task EveryMetricsRowHasFourColumns()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            var (metrics, _) = await ExportAsync();

            var rows = metrics.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.All(rows, row => Assert.Equal(4, row.Split(',').Length));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Every telemetry type, not just frames.
    /// </summary>
    /// <remarks>
    /// The first pass at this rewrote only the ToString calls that carried a format string, which left
    /// GPU temperature, VRAM byte counts, process counters, artifact metrics and the embedded per-process
    /// summaries formatting in whatever locale the machine happened to use. Asserting on one sample type
    /// is what let that survive, so this one exports a bundle carrying all of them and looks for the
    /// signature of the bug anywhere in the file: a decimal comma between two digits.
    /// </remarks>
    [Fact]
    public async Task NoTelemetryTypeWritesADecimalCommaIntoTheBundle()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            var (metrics, _) = await ExportAsync(FullTelemetry());

            var offender = Regex.Match(metrics, @"\d,\d");
            Assert.False(
                offender.Success,
                $"metrics.csv contains a decimal comma near: {Excerpt(metrics, offender.Index)}");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static string Excerpt(string text, int index)
    {
        var start = Math.Max(0, index - 60);
        return text.Substring(start, Math.Min(140, text.Length - start));
    }

    /// <summary>One sample of every kind the exporter flattens, all carrying fractional values.</summary>
    private static TelemetryEvent[] FullTelemetry()
    {
        var busy = new ProcessActivity("chrome", 99, 12.5, 1_234_567);

        return
        [
            new FrameTelemetrySample(BaseTime, 16.6, 8.25, 5.5, 16.6, false, "FiveM", CpuBusyMs: 7.25, CpuWaitMs: 9.35),
            new GpuTelemetrySample(
                BaseTime,
                IsAvailable: true,
                AdapterName: "RTX 3080",
                UtilizationPercent: 57.5,
                MemoryBandwidthUtilizationPercent: 22.5,
                UsedVramBytes: 9_123_456_789UL,
                TotalVramBytes: 10_737_418_240UL,
                EncoderUtilizationPercent: 36.5,
                DecoderUtilizationPercent: 0.5,
                TemperatureCelsius: 83,
                ThrottleReasons: ["SwThermalSlowdown"]),
            new SystemTelemetrySample(
                BaseTime,
                TotalCpuUsagePercent: 62.5,
                PerCoreUsagePercent: new Dictionary<string, double> { ["0"] = 99.5 },
                MemoryCommitPercent: 45.5,
                AvailableMemoryMb: 15_000,
                TopCpuProcesses: [busy],
                TopDiskProcesses: [busy],
                DiskAverageLatencyMs: 1.25,
                DiskQueueLength: 0.35,
                HardFaultPagesPerSecond: 2.5),
            new ProcessTelemetrySample(BaseTime, 1234, "FiveM", 44.5, 14_900_000_000, 6_800_000_000, 81, 67_700_000, 8_500_000),
            new ObsTelemetrySample(BaseTime, true, 59.5, 2.25, 1263, 1900, 12.5, 512.5, true, false, true),
            new NetworkProbeSample(BaseTime, "135.125.160.15", 14, true),
            new ArtifactEvidence(
                BaseTime,
                ArtifactKind.LogFile,
                "OBS enligt egen logg",
                new Dictionary<string, double> { ["obsRenderLagPercent"] = 0.11 },
                "obs.txt"),
        ];
    }

    private static async Task<(string Metrics, string Report)> ExportAsync(TelemetryEvent[]? events = null)
    {
        events ??=
        [
            new FrameTelemetrySample(BaseTime.AddSeconds(-30), 16.6, 8, 5, 16.6, false, "FiveM", CpuBusyMs: 7.25),
            new FrameTelemetrySample(BaseTime.AddSeconds(60), 16.6, 8, 5, 16.6, false, "FiveM", CpuBusyMs: 7.25),
        ];

        var incident = new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), BaseTime, IncidentSeverity.Normal, "Stutter"),
            BaseTime.AddSeconds(-30),
            BaseTime.AddSeconds(60),
            new EnvironmentMetadata(
                "Windows 11",
                "AMD Ryzen 7 5700X",
                32UL * 1024 * 1024 * 1024,
                "RTX 3080",
                "555.12",
                120,
                "Enabled",
                true,
                "Example Server",
                BaseTime.AddSeconds(-30),
                BaseTime.AddSeconds(60)),
            events,
            Analysis: null,
            Attachments: []);

        var outputDirectory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
        var zipPath = await new IncidentBundleExporter().ExportAsync(
            incident,
            new ExportBundleOptions(outputDirectory, IncludeSensitiveFields: false, IncludeAttachedArtifacts: false),
            CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        using var metricsReader = new StreamReader(zip.GetEntry("metrics.csv")!.Open());
        using var reportReader = new StreamReader(zip.GetEntry("incident-report.txt")!.Open());

        return (await metricsReader.ReadToEndAsync(), await reportReader.ReadToEndAsync());
    }
}
