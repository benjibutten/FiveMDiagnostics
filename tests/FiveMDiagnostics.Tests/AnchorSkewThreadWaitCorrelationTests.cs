namespace FiveMDiagnostics.Tests;

using System.Globalization;
using System.Text.RegularExpressions;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The two instruments that time a hitch do not share a clock, and the engine used to assume they did.
/// </summary>
/// <remarks>
/// PresentMon reports frame times relative to its own trace. The collector recovers wall clock as
/// <c>min(readUtc - relativeMs)</c> over every row it has seen, and that estimate can only ever be late:
/// <c>readUtc</c> is taken after PresentMon buffered the row, wrote it, and the poll loop picked it up,
/// so the anchor keeps the smallest pipeline latency of the session as a standing bias. ETL timestamps
/// carry no such offset.
/// <para>
/// The size of the disagreement is measured. On the 29 August session six incidents carried both a
/// marker — an anchored frame timestamp — and a trace naming the wait behind it:
/// </para>
/// <code>
///   trace                  marker (app)     ETL wait start     skew
///   deep_20260829_224317   00:43:17.021     00:43:15.704     +1 318 ms
///   deep_20260829_225759   00:57:59.976     00:57:58.700     +1 277 ms
///   deep_20260829_235222   01:52:22.702     01:52:21.528     +1 175 ms
///   deep_20260830_003329   02:33:29.606     02:33:28.427     +1 179 ms
/// </code>
/// <para>
/// The gate these tests replace allowed 50 ms of tolerance, so it rejected every one. Across nine
/// recorded sessions <see cref="RootCauseCategory.FiveMThreadWait"/> was never once ranked in the
/// field, while the reports kept concluding <see cref="RootCauseCategory.FiveMResourceSpike"/> from
/// <c>MsCPUBusy</c> for frames whose own attached trace showed the main thread asleep. The 29 August
/// note recorded that defect for the fifth session running.
/// </para>
/// </remarks>
public sealed class AnchorSkewThreadWaitCorrelationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 22, 42, 47, TimeSpan.Zero);

    /// <summary>The measured skew of the worst of the six, rounded up.</summary>
    private const double MeasuredAnchorSkewMs = 1_320;

    private readonly FiveMCorrelationEngine _engine = new();

    /// <summary>
    /// The incident this is built from: 00:43:15, a 262 ms frame whose trace shows the main thread
    /// (tid 10032) off the processor for 245.8 ms of it, released by tid 12864. Nothing else was
    /// running — 3.14 cores of 16, no external process above 0.3, 77% VRAM, 60 °C. The report that
    /// evening ranked it a FiveM resource/script spike at 80%.
    /// </summary>
    [Fact]
    public void AWaitIsStillFoundWhenTheAnchorIsOverASecondLate()
    {
        var analysis = _engine.Analyze(BuildAugust29Incident(anchorSkewMs: MeasuredAnchorSkewMs));

        Assert.Equal(RootCauseCategory.FiveMThreadWait, analysis.Hypotheses[0].Category);
    }

    /// <summary>
    /// And the verdict it was getting instead is now contradicted rather than merely outranked, because
    /// the reading it rested on cannot tell a thread executing from a thread blocked.
    /// </summary>
    [Fact]
    public void TheScriptSpikeVerdictIsContradictedOnceTheWaitIsFound()
    {
        var analysis = _engine.Analyze(BuildAugust29Incident(anchorSkewMs: MeasuredAnchorSkewMs));
        var resource = analysis.Hypotheses.FirstOrDefault(item => item.Category == RootCauseCategory.FiveMResourceSpike);

        Assert.NotNull(resource);
        Assert.True(
            resource!.Confidence <= 0.3,
            $"a script spike reached {resource.Confidence:P0} for a frame the thread slept through");
    }

    /// <summary>
    /// Sanity in the other direction: with the clocks aligned the answer must not change. A fix that
    /// only works when the anchor is wrong is not a fix.
    /// </summary>
    [Fact]
    public void TheSameWaitIsFoundWhenTheClocksAgree()
    {
        var analysis = _engine.Analyze(BuildAugust29Incident(anchorSkewMs: 0));

        Assert.Equal(RootCauseCategory.FiveMThreadWait, analysis.Hypotheses[0].Category);
    }

    /// <summary>
    /// Widening the gate must not turn the whole retained ring buffer into candidate evidence. The
    /// buffer holds up to an hour, and a wait from ten seconds away is a different hitch.
    /// </summary>
    [Fact]
    public void AWaitFromElsewhereInTheRingBufferIsStillRejected()
    {
        var analysis = _engine.Analyze(BuildAugust29Incident(anchorSkewMs: 10_000));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.FiveMThreadWait);
    }

    /// <summary>
    /// The duration match is what carries the correlation now, and it has to reject the one traced
    /// hitch of that evening which genuinely was not a wait.
    /// </summary>
    /// <remarks>
    /// 01:41:36, a 252 ms frame. The main thread waited 71.4 ms of it, released by tid 14984 sitting in
    /// <c>adhesive.dll</c> — FiveM's anti-tamper layer, which held 3.58 cores across four threads for
    /// the duration. The wait is real but accounts for under a third of what the frame lost, so the
    /// cause is the CPU those threads burned, not the sleep.
    /// </remarks>
    [Fact]
    public void AWaitThatExplainsOnlyPartOfTheFrameDoesNotBecomeItsCause()
    {
        var analysis = _engine.Analyze(BuildAugust29Incident(
            anchorSkewMs: MeasuredAnchorSkewMs,
            frameTimeMs: 252.2,
            waitMs: 71.4));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.FiveMThreadWait);
    }

    /// <summary>
    /// A frame's lost time may be credited to waiting once, however many waits landed on it.
    /// </summary>
    /// <remarks>
    /// The waits in a trace come from one thread and so cannot overlap each other, but the skew
    /// tolerance is three seconds wide and two of them therefore land on the same slow frame routinely.
    /// Each was credited with the whole of what that frame lost, so two 130 ms waits claimed 260 ms of a
    /// 245 ms hole — and the figure is compared against the window's CPU-bound spike time to decide
    /// whether the trace contradicts the attribution, which an overcount can flip on its own.
    /// </remarks>
    [Fact]
    public void TwoWaitsLandingOnTheSameFrameAreNotBothCreditedWithIt()
    {
        var analysis = _engine.Analyze(BuildAugust29Incident(
            anchorSkewMs: MeasuredAnchorSkewMs,
            waitMs: 130,
            secondWaitMs: 130));

        var attributedMs = AttributedWaitMs(analysis);

        Assert.NotNull(attributedMs);
        Assert.True(
            attributedMs <= 245,
            $"waiting was credited with {attributedMs:F0} ms of a frame that lost 245");
    }

    /// <summary>
    /// The milliseconds the engine says waiting accounts for, read back out of the evidence line that
    /// reports them. Null when no hypothesis named a wait.
    /// </summary>
    private static double? AttributedWaitMs(IncidentAnalysis analysis)
    {
        foreach (var line in analysis.Hypotheses.SelectMany(item => item.Evidence))
        {
            var match = Regex.Match(line, @"av CPU:n i (\d+(?:[.,]\d+)?) av");
            if (match.Success)
            {
                return double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the 00:43 incident, with the frame stream offset from the trace by <paramref name="anchorSkewMs"/>
    /// exactly as the live anchor offsets it.
    /// </summary>
    /// <param name="secondWaitMs">
    /// A second wait by the same thread, 1.5 seconds ahead of the first — disjoint from it, as waits by
    /// one thread must be, and inside the skew tolerance of the same frame.
    /// </param>
    private static IncidentRecord BuildAugust29Incident(
        double anchorSkewMs,
        double frameTimeMs = 261.6,
        double waitMs = 245.8,
        double? secondWaitMs = null)
    {
        var events = new List<TelemetryEvent>();

        // The wait, on the ETL's own clock.
        var waitStart = Start.AddSeconds(28.5);

        // The same moment as PresentMon reported it, which is where the frame stream puts it.
        var frameAt = waitStart.AddMilliseconds(anchorSkewMs);
        var streamStart = Start.AddMilliseconds(anchorSkewMs);

        // 16.67 ms median, as every session of this investigation has measured.
        for (var index = 0; index < 600; index++)
        {
            events.Add(new FrameTelemetrySample(
                streamStart.AddMilliseconds(index * 16.67),
                16.67,
                GpuBusyMs: 7.7,
                DisplayLatencyMs: 20,
                MsBetweenPresents: 16.67,
                Dropped: false,
                ProcessName: "FiveM_b3407_GTAProcess.exe",
                CpuBusyMs: 7.4,
                CpuWaitMs: 9.3));
        }

        // PresentMon's numbers for the frame itself: 249.3 ms of "CPU busy" for a thread the trace shows
        // off the processor for 245.8 of them.
        events.Add(new FrameTelemetrySample(
            frameAt,
            frameTimeMs,
            GpuBusyMs: 12.6,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: frameTimeMs - 12.3,
            CpuWaitMs: 0.1));

        var waitMetrics = new Dictionary<string, double>
            {
                ["traceDurationSeconds"] = 38.65,
                ["dpcMaxMs"] = 0.23,
                ["isrMaxMs"] = 0.13,
                ["cpuSampleCount"] = 187_183,
                ["cpuSubjectIsGame"] = 1,
                ["cpuSubjectProcessCores"] = 2.79,
                ["gameThreadWaitThreadId"] = 10_032,
                ["gameThreadLongWaitCount"] = 1,
                ["gameThreadUserRequestWaitCount"] = 1,
                ["gameThreadMaxWaitMs"] = waitMs,
                ["gameThreadWaitIntervalCount"] = 1,
                ["gameThreadWait0StartUnixMs"] = waitStart.ToUnixTimeMilliseconds(),
                ["gameThreadWait0EndUnixMs"] = waitStart.AddMilliseconds(waitMs).ToUnixTimeMilliseconds(),
                ["gameThreadWait0DurationMs"] = waitMs,
                ["gameThreadWait0UserRequest"] = 1,
            };

        if (secondWaitMs is { } secondMs)
        {
            var secondStart = waitStart.AddMilliseconds(-1_500);
            waitMetrics["gameThreadWaitIntervalCount"] = 2;
            waitMetrics["gameThreadLongWaitCount"] = 2;
            waitMetrics["gameThreadMaxWaitMs"] = Math.Max(waitMs, secondMs);
            waitMetrics["gameThreadWait1StartUnixMs"] = secondStart.ToUnixTimeMilliseconds();
            waitMetrics["gameThreadWait1EndUnixMs"] = secondStart.AddMilliseconds(secondMs).ToUnixTimeMilliseconds();
            waitMetrics["gameThreadWait1DurationMs"] = secondMs;
            waitMetrics["gameThreadWait1UserRequest"] = 1;
        }

        events.Add(new ArtifactEvidence(
            waitStart,
            ArtifactKind.EtlTrace,
            "Schemaläggning: aktiv GTA-tråd tid 10032 låg sammanhängande av CPU:n upp till 245,8 ms.",
            waitMetrics));

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), frameAt, IncidentSeverity.Severe, $"Auto: {frameTimeMs:F0} ms frame"),
            Start,
            Start.AddSeconds(90),
            new EnvironmentMetadata(
                "Microsoft Windows 10.0.26200",
                "AMD Ryzen 7 5700X 8-Core Processor",
                34_278_539_264,
                "NVIDIA GeForce RTX 3080",
                "32.0.16.1088",
                59,
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
