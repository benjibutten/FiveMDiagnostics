namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Core;

/// <summary>
/// The evening of 6 September, whose only remaining mechanism was the driver evacuating VRAM.
/// </summary>
/// <remarks>
/// 00:14:01 local: the card at 89.2 % steps up from 87.4 in a second, utilization falls to 2 % and
/// memory bandwidth to zero in the same reading, the game's main thread waits on its own render thread
/// which is spinning in <c>d3d11.dll</c> and <c>nvwgf2umx.dll</c>, and the frame takes 425 ms. Three
/// instruments, the same event, and none of them talks to the others — the same shape as the paging
/// stall of the night before. Reading it took an afternoon with a GPU CSV open by hand.
/// </remarks>
public sealed class GpuResidencyStallTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 22, 13, 40, TimeSpan.Zero);

    /// <summary>The frame, 21 seconds into the window.</summary>
    private static readonly DateTimeOffset FrameAt = Start.AddSeconds(21);

    private const double StallMs = 425;

    [Theory]
    [InlineData(90.9, 0.5, true)]
    [InlineData(50, 0.5, false)]
    [InlineData(90.9, 20, false)]
    public void AResidencyDropRequiresPressureAndAdjacentSamples(double before, double gapSeconds, bool expected)
    {
        var incident = Incident(withTrace: false);
        incident = incident with
        {
            Events = incident.Events.Where(item => item is not GpuTelemetrySample)
                .Concat(new TelemetryEvent[]
                {
                    Adapter(FrameAt.AddSeconds(-gapSeconds), 44, 14, before),
                    Adapter(FrameAt, 9, 14, before - 3),
                }).ToArray(),
        };

        var analysis = new FiveMCorrelationEngine().Analyze(incident);
        Assert.Equal(expected, analysis.Hypotheses.Any(item => item.Category == RootCauseCategory.GpuResidencyStall));
    }

    [Theory]
    [InlineData(-120, -60, InsufficientEvidenceReason.NoTrace)]
    [InlineData(120, 180, InsufficientEvidenceReason.NoTrace)]
    [InlineData(0, 30, InsufficientEvidenceReason.TooFewSpikes)]
    public void EvidenceGapUsesTraceCoverage(int startSeconds, int endSeconds, InsufficientEvidenceReason expected)
    {
        var incident = Incident(withTrace: false);
        incident = incident with
        {
            Events = incident.Events.OfType<FrameTelemetrySample>().Cast<TelemetryEvent>()
                .Append(new ArtifactEvidence(FrameAt, ArtifactKind.EtlTrace, "Trace", new Dictionary<string, double>
                {
                    ["traceCoveredStartUnixMs"] = Start.AddSeconds(startSeconds).ToUnixTimeMilliseconds(),
                    ["traceCoveredEndUnixMs"] = Start.AddSeconds(endSeconds).ToUnixTimeMilliseconds(),
                })).ToArray(),
        };

        Assert.Equal(expected, new FiveMCorrelationEngine().Analyze(incident).EvidenceGap);
    }

    [Fact]
    public void TheThreeSignalsTogetherAreTheVerdict()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        Assert.Equal(RootCauseCategory.GpuResidencyStall, analysis.Hypotheses[0].Category);
        Assert.True(analysis.Hypotheses[0].Confidence >= 0.9);

        var evidence = analysis.Hypotheses[0].Evidence;
        Assert.Contains(evidence, item => item.Contains("minnesbandbredd 0 %", StringComparison.Ordinal));
        Assert.Contains(evidence, item => item.Contains("89,2 %", StringComparison.Ordinal));
        Assert.Contains(evidence, item => item.Contains("d3d11.dll", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reading that makes the difference. The same window with the card still moving memory is a
    /// card under load, and this rule has nothing to say about it.
    /// </summary>
    [Fact]
    public void ACardWithBandwidthIsBusyRatherThanStopped()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(stalledBandwidthPercent: 14));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.GpuResidencyStall);
    }

    /// <summary>
    /// Without the bandwidth column there is no way to tell a stopped card from an idle one, and the
    /// rule says nothing rather than guessing from utilization alone.
    /// </summary>
    [Fact]
    public void WithoutTheBandwidthColumnTheRuleStaysSilent()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(stalledBandwidthPercent: null));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.GpuResidencyStall);
    }

    /// <summary>
    /// A card that never did any work in the window was idle, not stalled — a loading screen or a
    /// minimised game reads identically at the frame itself.
    /// </summary>
    [Fact]
    public void AnIdleCardIsNotAStalledOne()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(busyUtilizationPercent: 3));

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.GpuResidencyStall);
    }

    /// <summary>
    /// The 281 ms frame of 01:00:39, which the capture budget had no room left for: two signals instead
    /// of three still names the card, and says so with less confidence.
    /// </summary>
    [Fact]
    public void WithoutATraceTheTwoAdapterSignalsStillNameTheCard()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(withTrace: false));

        var residency = analysis.Hypotheses.Single(item => item.Category == RootCauseCategory.GpuResidencyStall);

        Assert.Equal(0.7, residency.Confidence, 2);
        Assert.DoesNotContain(residency.Evidence, item => item.Contains("d3d11.dll", StringComparison.Ordinal));
    }

    /// <summary>
    /// The card was at 89 %, which is below the engine's own VRAM threshold of 90 % — so the verdict
    /// this replaces would not have been reached at all.
    /// </summary>
    [Fact]
    public void TheOccupancyRuleAloneWouldHaveMissedIt()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident());

        Assert.DoesNotContain(analysis.Hypotheses, item => item.Category == RootCauseCategory.GpuVramPressure);
    }


    /// <summary>
    /// The 2 973 ms freeze of 10 September, where this verdict and its own trace text contradicted each
    /// other inside one incident.
    /// </summary>
    /// <remarks>
    /// The card stopped — utilization 3–6 %, memory bandwidth 0–1 %, for three seconds — and
    /// <c>dxgmms2.sys</c> went to 1.01 cores. But the card sat at 86.0 % in the second it happened, and
    /// the trace text said so in the same incident: "below the 88 % where eviction begins … the driver
    /// moved memory for some other reason, and this was not memory pressure". The verdict was
    /// nevertheless GPU residency at 75 %. Making room in VRAM is a claim about a full card, so below
    /// the band the observation is kept and the verdict is not.
    /// </remarks>
    [Fact]
    public void BelowTheBandTheStallIsObservedButNotTheVerdict()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(vramShiftPercentPoints: 4));

        var residency = analysis.Hypotheses.Single(item => item.Category == RootCauseCategory.GpuResidencyStall);

        Assert.True(residency.Confidence < 0.35, $"expected it under the classification floor, got {residency.Confidence:F2}");
        Assert.NotEqual(RootCauseCategory.GpuResidencyStall, analysis.Hypotheses[0].Category);

        // The evidence still describes the stopped card, and now says why it is not memory pressure —
        // in the same words the trace text uses, so the incident cannot contradict itself again.
        Assert.Contains(residency.Evidence, item => item.Contains("minnesbandbredd 0 %", StringComparison.Ordinal));
        Assert.Contains(residency.Evidence, item => item.Contains("inte minnestryck", StringComparison.Ordinal));
        Assert.Contains(residency.Evidence, item => item.Contains("får inte bli dom", StringComparison.Ordinal));
    }

    /// <summary>
    /// A card nobody measured is not a card measured below the band.
    /// </summary>
    /// <remarks>
    /// The run-up's peak was zero-filled when no VRAM reading fell inside it, so an incident with the
    /// memory column missing — NVML not answering, a driver restart, the counter simply absent — read as
    /// a card measured at 0.0 %. That is below the band, so the verdict wrote "kortet låg bara på 0,0 %
    /// … det här var inte minnestryck" and held itself under the classification floor, dismissing memory
    /// pressure on a number nobody took. Unmeasured has to say unmeasured.
    /// </remarks>
    [Fact]
    public void AMissingVramColumnIsNotACardMeasuredBelowTheBand()
    {
        var analysis = new FiveMCorrelationEngine().Analyze(Incident(vramMeasured: false));

        var residency = analysis.Hypotheses.Single(item => item.Category == RootCauseCategory.GpuResidencyStall);

        // The stopped card and the driver module are still worth what they are worth; only the VRAM
        // bonus is missing, and the below-the-band floor must not apply.
        Assert.True(residency.Confidence >= 0.7, $"expected the stopped card to still count, got {residency.Confidence:F2}");

        Assert.Contains(residency.Evidence, item => item.Contains("Ingen VRAM-mätning", StringComparison.Ordinal));
        Assert.DoesNotContain(residency.Evidence, item => item.Contains("0,0 %", StringComparison.Ordinal));
        Assert.DoesNotContain(residency.Evidence, item => item.Contains("inte minnestryck", StringComparison.Ordinal));
    }

    private static IncidentRecord Incident(
        double? stalledBandwidthPercent = 0,
        double busyUtilizationPercent = 44,
        bool withTrace = true,
        double vramShiftPercentPoints = 0,
        bool vramMeasured = true)
    {
        var events = new List<TelemetryEvent>();

        for (var i = 0; i < 600; i++)
        {
            events.Add(Frame(Start.AddMilliseconds(i * 16.7), 16.7));
        }

        // PresentMon reports the whole of it as CPU busy with almost no GPU work, which is what a frame
        // spent waiting for the driver looks like from the present side.
        events.Add(Frame(FrameAt, StallMs, cpuBusyMs: StallMs - 12, gpuBusyMs: 8));

        // The run-up: the card working normally, then a step into the band, then a second in which it
        // computes nothing at all.
        for (var half = 0; half < 60; half++)
        {
            var at = Start.AddSeconds(half * 0.5);
            var seconds = half * 0.5;

            var vramPercent = seconds switch
            {
                < 18 => 87.4,
                < 20 => 88.8,
                < 23 => 89.2,
                _ => 89.8,
            };

            var stopped = seconds is >= 20.5 and <= 23.5;
            events.Add(Adapter(
                at,
                utilizationPercent: stopped ? 2 : busyUtilizationPercent,
                bandwidthPercent: stopped ? stalledBandwidthPercent : 14,
                vramPercent: vramMeasured ? vramPercent - vramShiftPercentPoints : null));
        }

        if (withTrace)
        {
            events.Add(Trace());
        }

        return new IncidentRecord(
            Guid.NewGuid(),
            new IncidentMarker(Guid.NewGuid(), FrameAt, IncidentSeverity.Severe, "Auto: 425 ms frame"),
            Start,
            Start.AddSeconds(90),
            PagingStallAgainstTheSeptember5SessionTests.Environment(),
            events,
            Analysis: null,
            Attachments: []);
    }

    /// <summary>
    /// The 00:13 capture: the main thread off the processor for 379.8 ms, released by the render thread,
    /// which was on the processor the whole time inside the graphics driver.
    /// </summary>
    private static ArtifactEvidence Trace()
    {
        return new ArtifactEvidence(
            FrameAt,
            ArtifactKind.EtlTrace,
            "Väntan: huvudtråden låg av processorn 379,8 ms, släppt av rendertråden i samma process.",
            new Dictionary<string, double>
            {
                ["durationSeconds"] = 28,
                ["dpcMaxMs"] = 0.48,
                ["isrMaxMs"] = 0.30,
                ["cpuSampleCount"] = 191_402,
                ["cpuSubjectIsGame"] = 1,
                ["cpuSubjectProcessCores"] = 3.9,

                ["gameThreadWaitThreadId"] = 32_936,
                ["gameThreadLongWaitCount"] = 1,
                ["gameThreadUserRequestWaitCount"] = 1,
                ["gameThreadMaxWaitMs"] = 379.8,
                ["gameThreadWaitIntervalCount"] = 1,
                ["gameThreadWaitChainLength"] = 2,
                ["gameThreadWait0StartUnixMs"] = FrameAt.ToUnixTimeMilliseconds(),
                ["gameThreadWait0EndUnixMs"] = FrameAt.AddMilliseconds(379.8).ToUnixTimeMilliseconds(),
                ["gameThreadWait0DurationMs"] = 379.8,
                ["gameThreadWait0UserRequest"] = 1,

                ["gameThreadBlockedByThreadId"] = 27_740,
                ["gameThreadBlockerCores_GTAProcess.exe"] = 0.71,
                ["gameThreadBlockerCores_d3d11.dll"] = 0.12,
                ["gameThreadBlockerCores_nvwgf2umx.dll"] = 0.05,
            });
    }

    /// <param name="vramPercent">Null for a reading the memory column is missing from.</param>
    private static GpuTelemetrySample Adapter(
        DateTimeOffset at,
        double utilizationPercent,
        double? bandwidthPercent,
        double? vramPercent)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            at,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            utilizationPercent,
            bandwidthPercent,
            vramPercent is { } percent ? (ulong)(Total * percent / 100) : null,
            Total,
            EncoderUtilizationPercent: utilizationPercent < 5 ? 0 : 34,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 58,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    private static FrameTelemetrySample Frame(
        DateTimeOffset at,
        double frameTimeMs,
        double? cpuBusyMs = null,
        double? gpuBusyMs = null)
    {
        return new FrameTelemetrySample(
            at,
            frameTimeMs,
            GpuBusyMs: gpuBusyMs ?? 4.9,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: cpuBusyMs ?? 8.0,
            CpuWaitMs: 0.1);
    }
}
