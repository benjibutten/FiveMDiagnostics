namespace FiveMDiagnostics.Analysis;

using FiveMDiagnostics.Core;

public sealed class FiveMCorrelationEngine : IAnalysisEngine
{
    /// <summary>
    /// Stutter is deviation from the cadence the machine is actually achieving, not absolute frame time.
    /// A fixed 25 ms threshold misses every hitch on a 120 Hz display and fires constantly on a 30 fps
    /// one, so thresholds are derived from the observed median and the display's refresh interval.
    /// </summary>
    private const double SpikeMultiplier = 1.5;
    private const double SevereMultiplier = 2.5;

    /// <summary>VRAM occupancy at which the driver starts evicting resources across PCIe.</summary>
    private const double VramPressurePercent = 90;
    private const double VramCriticalPercent = 95;

    public IncidentAnalysis Analyze(IncidentRecord incident)
    {
        var frameSamples = incident.GetEvents<FrameTelemetrySample>();
        var systemSamples = incident.GetEvents<SystemTelemetrySample>();
        var processSamples = incident.GetEvents<ProcessTelemetrySample>();
        var obsSamples = incident.GetEvents<ObsTelemetrySample>();
        var gpuSamples = incident.GetEvents<GpuTelemetrySample>();
        var networkProbes = incident.GetEvents<NetworkProbeSample>();
        var networkEndpoints = incident.GetEvents<NetworkEndpointSample>();
        var artifacts = incident.GetEvents<ArtifactEvidence>();

        var metrics = BuildFrameMetrics(frameSamples, incident.Environment.DisplayRefreshRateHz);
        var gpu = BuildGpuMetrics(gpuSamples);
        var cores = BuildCoreMetrics(systemSamples);
        var suspectedProcesses = AnalyzeSuspiciousProcesses(systemSamples);
        var hypotheses = new List<HypothesisScore>();

        AddVramHypothesis(hypotheses, metrics, gpu);
        AddObsHypothesis(hypotheses, metrics, obsSamples, gpu);
        AddGpuHypothesis(hypotheses, metrics, obsSamples, systemSamples, gpu);
        AddResourceHypothesis(hypotheses, metrics, processSamples, artifacts, obsSamples, systemSamples, cores);
        AddNetworkHypothesis(hypotheses, metrics, networkProbes, networkEndpoints, artifacts, systemSamples, obsSamples);
        AddDiskHypothesis(hypotheses, metrics, processSamples, systemSamples, artifacts);
        AddExternalProcessHypothesis(hypotheses, suspectedProcesses);
        AddOsLatencyHypothesis(hypotheses, metrics, artifacts, systemSamples, obsSamples);
        AddCorruptionHypothesis(hypotheses, artifacts);

        hypotheses = hypotheses
            .OrderByDescending(item => item.Confidence)
            .ToList();

        if (hypotheses.Count == 0 || hypotheses[0].Confidence < 0.35)
        {
            hypotheses.Insert(0, new HypothesisScore(
                RootCauseCategory.InsufficientEvidence,
                0.2,
                ["Det fanns inte tillräckligt med samstämmig telemetry för en säker klassificering."]));
        }

        var highlights = BuildHighlights(incident, metrics, hypotheses.First(), artifacts, obsSamples, gpu, networkProbes, suspectedProcesses);
        var top = hypotheses[0];
        var summary = BuildSummary(top, metrics, obsSamples, gpu, artifacts, networkProbes, suspectedProcesses);

        return new IncidentAnalysis(
            hypotheses,
            top.Category == RootCauseCategory.InsufficientEvidence,
            summary,
            highlights,
            suspectedProcesses);
    }

    private static FrameMetrics BuildFrameMetrics(IReadOnlyList<FrameTelemetrySample> frameSamples, double? refreshRateHz)
    {
        if (frameSamples.Count == 0)
        {
            return FrameMetrics.Empty;
        }

        var sorted = frameSamples.Select(item => item.FrameTimeMs).OrderBy(value => value).ToArray();
        var median = Percentile(sorted, 0.50);
        var refreshInterval = refreshRateHz is > 0 ? 1000d / refreshRateHz.Value : 1000d / 60;

        // Take whichever is larger: a game locked to 60 fps on a 165 Hz panel is not stuttering, so the
        // achieved cadence is the honest baseline; a game that should hit 120 Hz should not get graded
        // against a median that a bad window has already dragged upwards.
        var baseline = Math.Max(median, refreshInterval);
        var spikeThreshold = Math.Max(baseline * SpikeMultiplier, 10);
        var severeThreshold = Math.Max(baseline * SevereMultiplier, 16);

        var spikes = frameSamples.Where(item => item.FrameTimeMs >= spikeThreshold).ToArray();
        // Attribution needs BOTH sides. PresentMon v1 supplies msGPUActive but no CPU figure, and a
        // missing CPU value is indistinguishable from an idle CPU — which would silently turn every
        // v1 spike into a "GPU-bound" or "present-bound" verdict the data cannot support.
        var breakdownSamples = frameSamples.Where(item => item.CpuBusyMs is not null && item.GpuBusyMs is not null).ToArray();

        var cpuBound = 0;
        var gpuBound = 0;
        var presentBound = 0;

        foreach (var spike in spikes)
        {
            switch (ClassifySpike(spike, baseline))
            {
                case SpikeKind.CpuBound:
                    cpuBound++;
                    break;
                case SpikeKind.GpuBound:
                    gpuBound++;
                    break;
                case SpikeKind.PresentBound:
                    presentBound++;
                    break;
            }
        }

        return new FrameMetrics(
            frameSamples.Count,
            median,
            baseline,
            spikeThreshold,
            severeThreshold,
            frameSamples.Average(item => item.FrameTimeMs),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            spikes.Length,
            frameSamples.Count(item => item.FrameTimeMs >= severeThreshold),
            spikes.Select(item => item.FrameTimeMs).DefaultIfEmpty().Max(),
            breakdownSamples.Length > 0,
            cpuBound,
            gpuBound,
            presentBound,
            frameSamples.Select(item => item.CpuBusyMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Select(item => item.GpuBusyMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Select(item => item.GpuWaitMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Select(item => item.FlipDelayMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Count(item => item.Dropped));
    }

    /// <summary>
    /// Attributes a single slow frame. This is the whole point of the PresentMon v2 breakdown: a spike
    /// where the CPU was busy is a script/resource problem, one where the GPU was busy is contention,
    /// and one where neither was busy is the present or display path stalling.
    /// </summary>
    private static SpikeKind ClassifySpike(FrameTelemetrySample sample, double baseline)
    {
        // Both figures are required: treating a missing value as zero would fabricate an attribution.
        if (sample.CpuBusyMs is not { } cpu || sample.GpuBusyMs is not { } gpu)
        {
            return SpikeKind.Unknown;
        }

        if (cpu > gpu && cpu >= baseline)
        {
            return SpikeKind.CpuBound;
        }

        if (gpu >= cpu && gpu >= baseline)
        {
            return SpikeKind.GpuBound;
        }

        // Frame took long but neither engine was working: the time went into waiting to present.
        return SpikeKind.PresentBound;
    }

    private static GpuMetrics BuildGpuMetrics(IReadOnlyList<GpuTelemetrySample> samples)
    {
        var available = samples.Where(item => item.IsAvailable).ToArray();
        if (available.Length == 0)
        {
            return GpuMetrics.Empty;
        }

        var throttleReasons = available
            .SelectMany(item => item.ThrottleReasons)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GpuMetrics(
            HasData: true,
            available[^1].AdapterName,
            available.Select(item => item.UtilizationPercent ?? 0).DefaultIfEmpty().Max(),
            available.Select(item => item.VramUsagePercent ?? 0).DefaultIfEmpty().Max(),
            available.Select(item => ToGigabytes(item.UsedVramBytes ?? 0)).DefaultIfEmpty().Max(),
            available.Select(item => ToGigabytes(item.TotalVramBytes ?? 0)).DefaultIfEmpty().Max(),
            available.Select(item => item.EncoderUtilizationPercent ?? 0).DefaultIfEmpty().Max(),
            throttleReasons);
    }

    /// <summary>
    /// FiveM's main thread is the practical bottleneck for script work, so one core pinned while the
    /// machine as a whole is idle is much stronger evidence of a resource spike than total CPU load.
    /// </summary>
    private static CoreMetrics BuildCoreMetrics(IReadOnlyList<SystemTelemetrySample> systemSamples)
    {
        var withCores = systemSamples.Where(item => item.PerCoreUsagePercent.Count > 0).ToArray();
        if (withCores.Length == 0)
        {
            return CoreMetrics.Empty;
        }

        var saturated = 0;
        double peakSingleCore = 0;

        foreach (var sample in withCores)
        {
            var maxCore = sample.PerCoreUsagePercent.Values.DefaultIfEmpty().Max();
            peakSingleCore = Math.Max(peakSingleCore, maxCore);

            if (maxCore >= 92 && sample.TotalCpuUsagePercent < 65)
            {
                saturated++;
            }
        }

        return new CoreMetrics(
            withCores.Length,
            saturated,
            Math.Round(peakSingleCore, 1),
            systemSamples.Select(item => item.TotalCpuUsagePercent).DefaultIfEmpty().Max());
    }

    /// <summary>
    /// High VRAM occupancy on its own is not a fault — a game is supposed to use the memory it has, and
    /// NVML reports the whole adapter rather than this process. It only becomes a root cause when the
    /// frame data shows the stalls that eviction produces, so occupancy alone never raises a hypothesis.
    /// </summary>
    private static void AddVramHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, GpuMetrics gpu)
    {
        if (!gpu.HasData || gpu.PeakVramPercent < VramPressurePercent || metrics.SpikeCount == 0)
        {
            return;
        }

        // The signature of eviction is a long frame where neither engine was working. Without the
        // per-frame breakdown that distinction cannot be made, so this stays a weak correlation.
        if (metrics.HasCpuGpuBreakdown && metrics.PresentBoundSpikes == 0)
        {
            return;
        }

        var evidence = new List<string>();
        double confidence;

        if (gpu.PeakVramPercent >= VramCriticalPercent)
        {
            confidence = 0.4;
            evidence.Add($"VRAM låg på {gpu.PeakVramPercent:F0}% ({gpu.PeakVramUsedGb:F1} av {gpu.TotalVramGb:F1} GB). Vid den nivån börjar drivrutinen evakuera resurser över PCIe.");
        }
        else
        {
            confidence = 0.25;
            evidence.Add($"VRAM toppade på {gpu.PeakVramPercent:F0}% ({gpu.PeakVramUsedGb:F1} av {gpu.TotalVramGb:F1} GB), nära gränsen där eviction börjar.");
        }

        if (metrics.HasCpuGpuBreakdown)
        {
            var share = (double)metrics.PresentBoundSpikes / metrics.SpikeCount;
            confidence += share >= 0.5 ? 0.3 : 0.15;
            evidence.Add($"{metrics.PresentBoundSpikes} av {metrics.SpikeCount} frametime-spikes hade varken CPU- eller GPU-arbete igång, vilket är signaturen för att vänta på minnesflytt snarare än på beräkning.");
        }
        else
        {
            evidence.Add($"{metrics.SpikeCount} frametime-spikes sammanföll med det höga VRAM-trycket, men utan PresentMon --v2_metrics går det inte att bekräfta att de var present-bundna.");
        }

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.15;
            evidence.Add($"{metrics.SevereSpikeCount} spikes över {metrics.SevereThresholdMs:F0} ms inträffade under samma fönster.");
        }

        if (gpu.PeakEncoderPercent >= 25)
        {
            confidence += 0.1;
            evidence.Add($"NVENC låg på {gpu.PeakEncoderPercent:F0}%, så encodern konkurrerade om samma GPU-minne.");
        }

        evidence.Add("Obs: VRAM mäts per grafikkort, inte per process, så andra program bidrar till siffran.");
        hypotheses.Add(new HypothesisScore(RootCauseCategory.GpuVramPressure, Math.Min(confidence, 0.95), evidence));
    }

    private static void AddObsHypothesis(List<HypothesisScore> hypotheses, FrameMetrics metrics, IReadOnlyList<ObsTelemetrySample> obsSamples, GpuMetrics gpu)
    {
        if (obsSamples.Count == 0)
        {
            return;
        }

        var connectedSamples = obsSamples.Where(item => item.IsConnected).ToArray();
        if (connectedSamples.Length == 0)
        {
            return;
        }

        var evidence = new List<string>();
        double confidence = 0;

        var skippedRender = Delta(connectedSamples.Select(item => item.RenderSkippedFrames));
        var skippedOutput = Delta(connectedSamples.Select(item => item.OutputSkippedFrames));
        var maxRenderTime = connectedSamples.Select(item => item.AverageFrameRenderTimeMs ?? 0).DefaultIfEmpty().Max();

        if (skippedRender > 0)
        {
            confidence += 0.35;
            evidence.Add($"OBS render skipped frames ökade med {skippedRender} under incidentfönstret.");
        }

        if (skippedOutput > 0)
        {
            confidence += 0.25;
            evidence.Add($"OBS output skipped frames ökade med {skippedOutput} under incidentfönstret.");
        }

        if (maxRenderTime >= 18)
        {
            confidence += 0.2;
            evidence.Add($"OBS average frame render time toppade på {maxRenderTime:F1} ms.");
        }

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.15;
            evidence.Add($"Frametime hade {metrics.SevereSpikeCount} spikes över {metrics.SevereThresholdMs:F0} ms samtidigt som OBS var aktivt.");
        }

        if (gpu.HasData && gpu.PeakEncoderPercent >= 40)
        {
            confidence += 0.1;
            evidence.Add($"NVENC-encodern toppade på {gpu.PeakEncoderPercent:F0}%.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.ObsRenderOutputContention, Math.Min(confidence, 0.97), evidence));
        }
    }

    private static void AddGpuHypothesis(
        List<HypothesisScore> hypotheses,
        FrameMetrics metrics,
        IReadOnlyList<ObsTelemetrySample> obsSamples,
        IReadOnlyList<SystemTelemetrySample> systemSamples,
        GpuMetrics gpu)
    {
        var evidence = new List<string>();
        double confidence = 0;

        // With the v2 breakdown this stops being a guess: only count it as GPU contention when the GPU
        // was actually the busy party during the slow frames.
        if (metrics.HasCpuGpuBreakdown && metrics.GpuBoundSpikes > 0)
        {
            confidence += 0.4;
            evidence.Add($"{metrics.GpuBoundSpikes} av {metrics.SpikeCount} frametime-spikes var GPU-bundna (GPU busy dominerade frametiden).");
        }

        if (metrics.MaxGpuWaitMs >= 5)
        {
            confidence += 0.2;
            evidence.Add($"GPU wait nådde {metrics.MaxGpuWaitMs:F1} ms, vilket pekar på köbildning mot GPU:n.");
        }

        if (gpu.HasData && gpu.PeakUtilizationPercent >= 97)
        {
            confidence += 0.2;
            evidence.Add($"GPU-utilization låg på {gpu.PeakUtilizationPercent:F0}%.");
        }

        if (gpu.ThrottleReasons.Count > 0)
        {
            confidence += 0.2;
            evidence.Add($"GPU:n rapporterade throttling: {string.Join(", ", gpu.ThrottleReasons)}.");
        }

        if (metrics.P99FrameTime >= metrics.SevereThresholdMs)
        {
            confidence += 0.15;
            evidence.Add($"P99 frametime låg på {metrics.P99FrameTime:F1} ms mot en baseline på {metrics.BaselineFrameTime:F1} ms.");
        }

        // Without a breakdown the old frame-time-only reasoning is all that is available, so keep it but
        // do not let it reach the same confidence as a measured attribution.
        if (!metrics.HasCpuGpuBreakdown && metrics.SpikeCount >= 4 && systemSamples.Any(item => item.TotalCpuUsagePercent < 85) && obsSamples.All(item => !item.IsConnected))
        {
            confidence += 0.25;
            evidence.Add($"Det fanns {metrics.SpikeCount} frametime-spikes över {metrics.SpikeThresholdMs:F0} ms utan tydligt CPU- eller OBS-tryck.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.GpuFrametimeContention, Math.Min(confidence, 0.9), evidence));
        }
    }

    private static void AddResourceHypothesis(
        List<HypothesisScore> hypotheses,
        FrameMetrics metrics,
        IReadOnlyList<ProcessTelemetrySample> processSamples,
        IReadOnlyList<ArtifactEvidence> artifacts,
        IReadOnlyList<ObsTelemetrySample> obsSamples,
        IReadOnlyList<SystemTelemetrySample> systemSamples,
        CoreMetrics cores)
    {
        var evidence = new List<string>();
        double confidence = 0;

        var profilerEvidence = artifacts.Where(item => item.Kind is ArtifactKind.ProfilerJson or ArtifactKind.ResmonSnapshot).ToArray();
        if (profilerEvidence.Length > 0)
        {
            confidence += 0.45;
            evidence.AddRange(profilerEvidence.Select(item => item.Summary));
        }

        if (metrics.HasCpuGpuBreakdown && metrics.CpuBoundSpikes > 0)
        {
            confidence += 0.35;
            evidence.Add($"{metrics.CpuBoundSpikes} av {metrics.SpikeCount} frametime-spikes var CPU-bundna (CPU busy dominerade frametiden).");
        }

        if (cores.HasData && cores.SaturatedCoreSamples > 0)
        {
            confidence += 0.2;
            evidence.Add($"En enskild kärna toppade på {cores.PeakSingleCoreUsage:F0}% medan totala CPU-lasten låg lågt i {cores.SaturatedCoreSamples} mätpunkter, vilket är typiskt för FiveM:s main thread.");
        }

        var maxFiveMCpu = processSamples.Select(item => item.CpuUsagePercent).DefaultIfEmpty().Max();
        if (maxFiveMCpu >= 55)
        {
            confidence += 0.2;
            evidence.Add($"FiveM-processen toppade på {maxFiveMCpu:F0}% CPU under incidenten.");
        }

        if (obsSamples.All(item => !item.IsConnected) && systemSamples.Any(item => item.TotalCpuUsagePercent < 80))
        {
            confidence += 0.15;
            evidence.Add("OBS var inte aktivt och systemet i stort såg relativt stabilt ut, vilket talar för FiveM/resource-sida.");
        }

        if (metrics.SpikeCount > 0)
        {
            confidence += 0.1;
            evidence.Add($"Frametime-problemet syns tydligt lokalt med {metrics.SpikeCount} spikes över {metrics.SpikeThresholdMs:F0} ms.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.FiveMResourceSpike, Math.Min(confidence, 0.98), evidence));
        }
    }

    private static void AddNetworkHypothesis(
        List<HypothesisScore> hypotheses,
        FrameMetrics metrics,
        IReadOnlyList<NetworkProbeSample> probes,
        IReadOnlyList<NetworkEndpointSample> endpoints,
        IReadOnlyList<ArtifactEvidence> artifacts,
        IReadOnlyList<SystemTelemetrySample> systemSamples,
        IReadOnlyList<ObsTelemetrySample> obsSamples)
    {
        var evidence = new List<string>();
        double confidence = 0;

        // net_statsFile is the client's own view of the connection and is far stronger evidence than an
        // ICMP probe, which only measures the path and not the game protocol.
        foreach (var netStats in artifacts.Where(item => item.Kind == ArtifactKind.NetStatsCsv))
        {
            var packetLoss = netStats.Metrics.GetValueOrDefault("avgPacketLossPercent", 0);
            var peakPing = netStats.Metrics.GetValueOrDefault("max_avgPingMs", 0);
            var jitter = netStats.Metrics.GetValueOrDefault("avgJitterMs", 0);

            if (packetLoss >= 1)
            {
                confidence += packetLoss >= 3 ? 0.4 : 0.25;
                evidence.Add($"net_statsFile visade {packetLoss:F1}% packet loss.");
            }

            if (jitter >= 30)
            {
                confidence += 0.25;
                evidence.Add($"net_statsFile visade {jitter:F0} ms jitter.");
            }

            if (peakPing >= 120)
            {
                confidence += 0.2;
                evidence.Add($"net_statsFile visade ping upp till {peakPing:F0} ms.");
            }

            if (packetLoss < 1 && jitter < 30 && peakPing < 120)
            {
                evidence.Add($"net_statsFile såg friskt ut (ping {netStats.Metrics.GetValueOrDefault("avgPingMs", 0):F0} ms, jitter {jitter:F0} ms, loss {packetLoss:F1}%), vilket talar emot en nätorsak.");
            }
        }

        var successfulProbes = probes.Where(item => item.Success && item.RoundTripTimeMs is not null).ToArray();
        var failedProbes = probes.Count(item => !item.Success);
        var maxRtt = successfulProbes.Select(item => item.RoundTripTimeMs ?? 0).DefaultIfEmpty().Max();
        var avgRtt = successfulProbes.Select(item => item.RoundTripTimeMs ?? 0).DefaultIfEmpty().Average();

        if (maxRtt >= 120)
        {
            confidence += 0.3;
            evidence.Add($"RTT toppade på {maxRtt:F0} ms.");
        }

        if (successfulProbes.Length >= 3 && maxRtt - avgRtt >= 40)
        {
            confidence += 0.2;
            evidence.Add($"RTT-jitter på minst {maxRtt - avgRtt:F0} ms observerades.");
        }

        if (failedProbes > 0)
        {
            confidence += 0.2;
            evidence.Add($"{failedProbes} probe-förfrågningar misslyckades under incidenten.");
        }

        if (endpoints.Any(item => item.RemoteEndpoints.Count > 0))
        {
            confidence += 0.05;
            evidence.Add("Aktiva remote endpoints fanns under incidenten.");
        }

        // A network incident should look like a healthy local machine that is nonetheless hitching.
        if (metrics.SpikeCount == 0 && systemSamples.Any(item => item.TotalCpuUsagePercent < 75) && obsSamples.All(item => !item.IsConnected))
        {
            confidence += 0.2;
            evidence.Add("Lokal maskin såg stabil ut trots försämrat nätbeteende.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.NetworkJitterOrPacketLoss, Math.Min(confidence, 0.9), evidence));
        }
    }

    private static void AddDiskHypothesis(
        List<HypothesisScore> hypotheses,
        FrameMetrics metrics,
        IReadOnlyList<ProcessTelemetrySample> processSamples,
        IReadOnlyList<SystemTelemetrySample> systemSamples,
        IReadOnlyList<ArtifactEvidence> artifacts)
    {
        var evidence = new List<string>();
        double confidence = 0;

        var maxRead = processSamples.Select(item => item.ReadBytesPerSecond).DefaultIfEmpty().Max();
        var competingIo = systemSamples.SelectMany(item => item.TopDiskProcesses).Where(item => !item.ProcessName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)).ToArray();
        var maxCompetingIo = competingIo.Select(item => item.IoBytesPerSecond).DefaultIfEmpty().Max();
        var streamingHints = artifacts.Where(item => item.Summary.Contains("stream", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (maxRead >= 50 * 1024 * 1024)
        {
            confidence += 0.25;
            evidence.Add($"FiveM läste upp till {ToMegabytes(maxRead):F1} MB/s.");
        }

        if (maxCompetingIo >= 20 * 1024 * 1024)
        {
            confidence += 0.25;
            evidence.Add($"En konkurrerande process låg på {ToMegabytes(maxCompetingIo):F1} MB/s disk-I/O.");
        }

        if (streamingHints.Length > 0)
        {
            confidence += 0.2;
            evidence.AddRange(streamingHints.Select(item => item.Summary));
        }

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.1;
            evidence.Add("Frametime-spikes sammanföll med disk- eller streaming-signaler.");
        }

        if (confidence > 0)
        {
            hypotheses.Add(new HypothesisScore(RootCauseCategory.StreamingOrDiskStall, Math.Min(confidence, 0.88), evidence));
        }
    }

    private static void AddExternalProcessHypothesis(List<HypothesisScore> hypotheses, IReadOnlyList<SuspectedProcessImpact> suspectedProcesses)
    {
        if (suspectedProcesses.Count == 0)
        {
            return;
        }

        var evidence = suspectedProcesses
            .Take(3)
            .Select(item => $"{item.ProcessName} misstänks störa med {item.Reason.ToLowerInvariant()} (peak CPU {item.PeakCpuPercent:F0}%, disk {item.PeakIoMegabytesPerSecond:F1} MB/s).")
            .ToArray();

        hypotheses.Add(new HypothesisScore(
            RootCauseCategory.ExternalProcessInterference,
            Math.Min(0.45 + (suspectedProcesses.Count * 0.06), 0.78),
            evidence));
    }

    private static void AddOsLatencyHypothesis(
        List<HypothesisScore> hypotheses,
        FrameMetrics metrics,
        IReadOnlyList<ArtifactEvidence> artifacts,
        IReadOnlyList<SystemTelemetrySample> systemSamples,
        IReadOnlyList<ObsTelemetrySample> obsSamples)
    {
        var latencyArtifacts = artifacts
            .Where(item => item.Kind == ArtifactKind.EtlTrace
                || item.Summary.Contains("DPC", StringComparison.OrdinalIgnoreCase)
                || item.Summary.Contains("ISR", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (latencyArtifacts.Length == 0)
        {
            return;
        }

        double confidence = 0.3;
        var evidence = latencyArtifacts.Select(item => item.Summary).Distinct().ToList();

        // Measured DPC duration is the actual discriminator; event counts alone say nothing.
        var maxDpcMs = latencyArtifacts.Select(item => item.Metrics.GetValueOrDefault("dpcMaxMs", 0)).DefaultIfEmpty().Max();
        var maxIsrMs = latencyArtifacts.Select(item => item.Metrics.GetValueOrDefault("isrMaxMs", 0)).DefaultIfEmpty().Max();
        var worstLatency = Math.Max(maxDpcMs, maxIsrMs);

        if (worstLatency >= 4)
        {
            confidence += 0.35;
            evidence.Add($"Längsta DPC/ISR höll CPU:n i {worstLatency:F2} ms, vilket blockerar schemaläggaren och drabbar alla trådar samtidigt.");
        }
        else if (worstLatency >= 1)
        {
            confidence += 0.15;
            evidence.Add($"Längsta DPC/ISR låg på {worstLatency:F2} ms.");
        }

        // A stall that hits the present path without CPU or GPU work is what an OS-level freeze looks
        // like from the frame data's point of view.
        if (metrics.PresentBoundSpikes > 0)
        {
            confidence += 0.15;
            evidence.Add($"{metrics.PresentBoundSpikes} spikes saknade både CPU- och GPU-arbete, vilket stämmer med en systemwide stall.");
        }

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.1;
            evidence.Add("Severe frametime-spikes syns samtidigt som ETW-latency-signaler.");
        }

        if (systemSamples.Any(item => item.TotalCpuUsagePercent < 80) && obsSamples.All(item => !item.IsConnected))
        {
            confidence += 0.1;
            evidence.Add("Ingen annan stark lokal contention-stack överröstade ETW-fynden.");
        }

        hypotheses.Add(new HypothesisScore(RootCauseCategory.OsOrDriverLatency, Math.Min(confidence, 0.9), evidence));
    }

    private static void AddCorruptionHypothesis(List<HypothesisScore> hypotheses, IReadOnlyList<ArtifactEvidence> artifacts)
    {
        var corruption = artifacts.Where(item => item.Summary.Contains("cache", StringComparison.OrdinalIgnoreCase) || item.Summary.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || item.Summary.Contains("failed to load", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (corruption.Length == 0)
        {
            return;
        }

        hypotheses.Add(new HypothesisScore(
            RootCauseCategory.PossibleCacheOrResourceCorruption,
            Math.Min(0.45 + (corruption.Length * 0.08), 0.8),
            corruption.Select(item => item.Summary).Distinct().ToArray()));
    }

    private static IReadOnlyList<TimelineHighlight> BuildHighlights(
        IncidentRecord incident,
        FrameMetrics metrics,
        HypothesisScore top,
        IReadOnlyList<ArtifactEvidence> artifacts,
        IReadOnlyList<ObsTelemetrySample> obsSamples,
        GpuMetrics gpu,
        IReadOnlyList<NetworkProbeSample> probes,
        IReadOnlyList<SuspectedProcessImpact> suspectedProcesses)
    {
        var highlights = new List<TimelineHighlight>
        {
            new(incident.Marker.MarkedAt, "Marker", $"{incident.Marker.Label} markerad {incident.Marker.MarkedAt:HH:mm:ss}."),
            new(incident.Marker.MarkedAt, "Frame", $"Baseline {metrics.BaselineFrameTime:F1} ms, P95 {metrics.P95FrameTime:F1} ms, P99 {metrics.P99FrameTime:F1} ms, {metrics.SpikeCount} spikes över {metrics.SpikeThresholdMs:F0} ms."),
        };

        if (metrics.HasCpuGpuBreakdown && metrics.SpikeCount > 0)
        {
            highlights.Add(new(
                incident.Marker.MarkedAt,
                "Frame attribution",
                $"Spikes fördelade som CPU-bundna {metrics.CpuBoundSpikes}, GPU-bundna {metrics.GpuBoundSpikes}, present/display {metrics.PresentBoundSpikes}."));
        }

        if (gpu.HasData)
        {
            highlights.Add(new(
                incident.Marker.MarkedAt,
                "GPU",
                $"{gpu.AdapterName ?? "GPU"}: peak {gpu.PeakUtilizationPercent:F0}% util, VRAM {gpu.PeakVramUsedGb:F1}/{gpu.TotalVramGb:F1} GB ({gpu.PeakVramPercent:F0}%), NVENC {gpu.PeakEncoderPercent:F0}%."));
        }

        var obs = obsSamples.LastOrDefault(item => item.IsConnected);
        if (obs is not null)
        {
            highlights.Add(new(obs.Timestamp, "OBS", $"OBS render time {obs.AverageFrameRenderTimeMs:F1} ms, render skipped {obs.RenderSkippedFrames}, output skipped {obs.OutputSkippedFrames}."));
        }

        var probe = probes.OrderByDescending(item => item.RoundTripTimeMs ?? 0).FirstOrDefault();
        if (probe is not null)
        {
            highlights.Add(new(probe.Timestamp, "Network", probe.Success
                ? $"RTT mot {probe.Host} nådde {probe.RoundTripTimeMs:F0} ms."
                : $"Probe mot {probe.Host} misslyckades: {probe.FailureReason ?? "okänt fel"}."));
        }

        var suspect = suspectedProcesses.FirstOrDefault();
        if (suspect is not null)
        {
            highlights.Add(new(incident.Marker.MarkedAt, "Processes", $"Misstänkt sidoprocess: {suspect.ProcessName} ({suspect.Reason}, peak CPU {suspect.PeakCpuPercent:F0}%, disk {suspect.PeakIoMegabytesPerSecond:F1} MB/s)."));
        }

        highlights.AddRange(artifacts.Take(3).Select(item => new TimelineHighlight(item.Timestamp, item.Kind.ToString(), item.Summary)));
        highlights.Add(new(incident.Marker.MarkedAt, "Classification", $"Högst rankad hypotes: {ToLabel(top.Category)} ({top.Confidence:P0})."));
        return highlights.OrderBy(item => item.Timestamp).ToArray();
    }

    private static string BuildSummary(
        HypothesisScore top,
        FrameMetrics metrics,
        IReadOnlyList<ObsTelemetrySample> obsSamples,
        GpuMetrics gpu,
        IReadOnlyList<ArtifactEvidence> artifacts,
        IReadOnlyList<NetworkProbeSample> probes,
        IReadOnlyList<SuspectedProcessImpact> suspectedProcesses)
    {
        if (top.Category == RootCauseCategory.InsufficientEvidence)
        {
            var missing = new List<string>();
            if (!metrics.HasCpuGpuBreakdown)
            {
                missing.Add("PresentMon med --v2_metrics (ger CPU/GPU-uppdelning per frame)");
            }

            if (!gpu.HasData)
            {
                missing.Add("GPU-telemetri (VRAM och encoder-last)");
            }

            if (artifacts.Count == 0)
            {
                missing.Add("profiler/net_stats eller en deep capture");
            }

            var hint = missing.Count > 0
                ? $" Starkast förbättring just nu: {string.Join(", ", missing)}."
                : string.Empty;

            return $"Insufficient evidence. Kör gärna en ny session i grundläge igen.{hint}";
        }

        var obsActive = obsSamples.Any(item => item.IsConnected) ? "OBS var aktivt." : "OBS var inte aktivt.";
        var attribution = metrics.HasCpuGpuBreakdown && metrics.SpikeCount > 0
            ? $" Av {metrics.SpikeCount} spikes var {metrics.CpuBoundSpikes} CPU-bundna, {metrics.GpuBoundSpikes} GPU-bundna och {metrics.PresentBoundSpikes} present/display-bundna."
            : string.Empty;
        var vramHint = gpu.HasData
            ? $" VRAM toppade på {gpu.PeakVramPercent:F0}% ({gpu.PeakVramUsedGb:F1}/{gpu.TotalVramGb:F1} GB)."
            : string.Empty;
        var artifactHint = artifacts.Count > 0 ? $" {artifacts.Count} importerade artifacts bidrog till bedömningen." : string.Empty;
        var probeHint = probes.Any() ? " Nätprober fanns tillgängliga i incidentfönstret." : string.Empty;
        var suspectHint = suspectedProcesses.FirstOrDefault() is { } suspect
            ? $" Mest avvikande bakgrundsprocess: {suspect.ProcessName} ({suspect.Reason.ToLowerInvariant()})."
            : string.Empty;

        return $"Trolig rotorsak: {ToLabel(top.Category)} ({top.Confidence:P0}). Frametime-fönstret hade baseline {metrics.BaselineFrameTime:F1} ms, P95 {metrics.P95FrameTime:F1} ms och P99 {metrics.P99FrameTime:F1} ms.{attribution}{vramHint} {obsActive}{artifactHint}{probeHint}{suspectHint}";
    }

    private static IReadOnlyList<SuspectedProcessImpact> AnalyzeSuspiciousProcesses(IReadOnlyList<SystemTelemetrySample> systemSamples)
    {
        return systemSamples
            .SelectMany(item => item.TopCpuProcesses.Concat(item.TopDiskProcesses))
            .Where(item => IsRelevantExternalProcess(item.ProcessName))
            .GroupBy(item => (item.ProcessName, item.ProcessId))
            .Select(group =>
            {
                var peakCpu = group.Max(entry => entry.CpuPercent);
                var peakIoMegabytes = group.Max(entry => ToMegabytes(entry.IoBytesPerSecond));

                return new
                {
                    Impact = new SuspectedProcessImpact(
                        group.Key.ProcessName,
                        group.Key.ProcessId,
                        Math.Round(peakCpu, 1),
                        Math.Round(peakIoMegabytes, 1),
                        group.Count(),
                        DescribeProcessReason(group.Key.ProcessName, peakCpu, peakIoMegabytes)),
                    Score = peakCpu + (peakIoMegabytes * 1.5) + (IsKnownOverlayOrHook(group.Key.ProcessName) ? 20 : 0),
                };
            })
            .Where(item => item.Impact.PeakCpuPercent >= 12 || item.Impact.PeakIoMegabytesPerSecond >= 12 || IsKnownOverlayOrHook(item.Impact.ProcessName))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Impact.ObservedSamples)
            .Select(item => item.Impact)
            .Take(5)
            .ToArray();
    }

    private static bool IsRelevantExternalProcess(string processName)
    {
        return !processName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)
            && !processName.Contains("GTA", StringComparison.OrdinalIgnoreCase)
            && !processName.Contains("obs", StringComparison.OrdinalIgnoreCase)
            && !processName.Contains("FiveMDiagnostics", StringComparison.OrdinalIgnoreCase)
            && !processName.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            && !processName.Equals("System", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownOverlayOrHook(string processName)
    {
        return processName.Contains("discord", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("steam", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("overwolf", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("rtss", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("afterburner", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("shadowplay", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("medal", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("razer", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("amdsoftware", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("gamebar", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeProcessReason(string processName, double peakCpu, double peakIoMegabytes)
    {
        if (IsKnownOverlayOrHook(processName) && peakCpu >= 12)
        {
            return "overlay/hook-beteende och hög CPU-belastning";
        }

        if (IsKnownOverlayOrHook(processName))
        {
            return "overlay/hook-beteende";
        }

        if (peakCpu >= 20 && peakIoMegabytes >= 20)
        {
            return "både hög CPU- och diskbelastning";
        }

        if (peakCpu >= 20)
        {
            return "hög CPU-belastning";
        }

        if (peakIoMegabytes >= 20)
        {
            return "hög disk-I/O";
        }

        return "återkommande belastning i bakgrunden";
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Floor(sortedValues.Length * percentile) - 1, 0, sortedValues.Length - 1);
        return sortedValues[index];
    }

    private static long Delta(IEnumerable<long?> samples)
    {
        var values = samples.Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        return values.Length >= 2 ? values[^1] - values[0] : 0;
    }

    private static double ToMegabytes(long bytes)
    {
        return bytes / 1024d / 1024d;
    }

    private static double ToGigabytes(ulong bytes)
    {
        return bytes / 1024d / 1024d / 1024d;
    }

    private static string ToLabel(RootCauseCategory category)
    {
        return category switch
        {
            RootCauseCategory.GpuFrametimeContention => "GPU/frametime contention",
            RootCauseCategory.GpuVramPressure => "GPU VRAM pressure (texture eviction)",
            RootCauseCategory.ObsRenderOutputContention => "OBS/render/output contention",
            RootCauseCategory.FiveMResourceSpike => "FiveM resource/script spike",
            RootCauseCategory.NetworkJitterOrPacketLoss => "Network jitter/packet loss/routing issue",
            RootCauseCategory.StreamingOrDiskStall => "Streaming/disk stall",
            RootCauseCategory.ExternalProcessInterference => "External process interference",
            RootCauseCategory.OsOrDriverLatency => "OS/driver latency",
            RootCauseCategory.PossibleCacheOrResourceCorruption => "Possible cache/resource corruption",
            _ => "Insufficient evidence",
        };
    }

    private enum SpikeKind
    {
        Unknown,
        CpuBound,
        GpuBound,
        PresentBound,
    }

    private sealed record FrameMetrics(
        int SampleCount,
        double MedianFrameTime,
        double BaselineFrameTime,
        double SpikeThresholdMs,
        double SevereThresholdMs,
        double AverageFrameTime,
        double P95FrameTime,
        double P99FrameTime,
        int SpikeCount,
        int SevereSpikeCount,
        double LongestSpikeMs,
        bool HasCpuGpuBreakdown,
        int CpuBoundSpikes,
        int GpuBoundSpikes,
        int PresentBoundSpikes,
        double MaxCpuBusyMs,
        double MaxGpuBusyMs,
        double MaxGpuWaitMs,
        double MaxFlipDelayMs,
        int DroppedCount)
    {
        public static FrameMetrics Empty { get; } = new(0, 0, 16.67, 25, 40, 0, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record GpuMetrics(
        bool HasData,
        string? AdapterName,
        double PeakUtilizationPercent,
        double PeakVramPercent,
        double PeakVramUsedGb,
        double TotalVramGb,
        double PeakEncoderPercent,
        IReadOnlyList<string> ThrottleReasons)
    {
        public static GpuMetrics Empty { get; } = new(false, null, 0, 0, 0, 0, 0, []);
    }

    private sealed record CoreMetrics(
        int TotalSamples,
        int SaturatedCoreSamples,
        double PeakSingleCoreUsage,
        double PeakTotalUsage)
    {
        public static CoreMetrics Empty { get; } = new(0, 0, 0, 0);

        public bool HasData => TotalSamples > 0;
    }
}
