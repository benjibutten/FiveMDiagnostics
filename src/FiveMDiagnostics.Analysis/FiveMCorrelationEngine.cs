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

    /// <summary>Ceiling for a storage verdict backed by the disk counters that were supposed to measure it.</summary>
    private const double MeasuredConfidenceCeiling = 0.88;

    /// <summary>
    /// Ceiling for a verdict resting on a fallback rather than on the measurement it substitutes for.
    /// </summary>
    /// <remarks>
    /// Deliberately below the 0.35 bar that promotes a hypothesis past "insufficient evidence": a
    /// conclusion drawn without the instrument that could refute it should be a lead worth checking, not
    /// a top-ranked answer that ends the investigation.
    /// </remarks>
    private const double FallbackConfidenceCeiling = 0.34;

    /// <summary>
    /// Share of frames in one present mode before it counts as how the machine presents, rather than as
    /// a mode a few frames happened to use during a transition.
    /// </summary>
    private const double DominantPresentModeShare = 0.9;

    /// <summary>
    /// Share of a window's spikes that has to be CPU-bound before the frame attribution is treated as
    /// having answered the question, whatever else the window contains.
    /// </summary>
    private const double DecisiveAttributionShare = 0.8;

    /// <summary>
    /// Ceiling for a hypothesis the per-frame attribution contradicts. Below the 0.35 bar that promotes
    /// a hypothesis past "insufficient evidence", so such a verdict can be listed as a lead but cannot
    /// be the answer.
    /// </summary>
    private const double ContradictedConfidenceCeiling = 0.3;

    /// <summary>
    /// Ceiling for a script verdict whose only positive evidence is the per-frame CPU/GPU breakdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MsCPUBusy</c> is derived from the gap between presents, so it reports a thread blocked on a
    /// lock exactly as it reports a thread running script. That is not a weakness the figure has in
    /// unusual windows — it is what the figure is, and it means the breakdown can say the time did not
    /// go to the GPU or to the present path without being able to say that the game's own code ran.
    /// Nothing else in the ordinary telemetry closes that gap: the per-core counters sample once a
    /// second and a pinned core is FiveM's main thread on any evening at all, and the process CPU figure
    /// is an average over the same second. Only a trace or a profiler measures execution.
    /// </para>
    /// <para>
    /// The session of 27 August is the whole argument. All 67 of its frames over 100 ms reported
    /// <c>MsCPUBusy</c> at 70% or more of frame time and 151 of its 154 incidents were ranked as script
    /// spikes on the strength of it, at 80% confidence — while the one freeze that had a trace shows the
    /// main thread off the processor for 178.0 of its 178 ms. Set below the 0.35 bar deliberately, and
    /// for the same reason the storage verdict resting on throughput is: a conclusion drawn without the
    /// instrument that could refute it is a lead worth checking, not the answer that ends the
    /// investigation.
    /// </para>
    /// </remarks>
    private const double UncorroboratedAttributionCeiling = 0.34;

    public IncidentAnalysis Analyze(IncidentRecord incident)
    {
        var frameSamples = incident.GetEvents<FrameTelemetrySample>();
        var systemSamples = incident.GetEvents<SystemTelemetrySample>();
        var processSamples = incident.GetEvents<ProcessTelemetrySample>();
        var obsSamples = incident.GetEvents<ObsTelemetrySample>();
        var gpuSamples = incident.GetEvents<GpuTelemetrySample>();
        var gpuProcessMemory = PeakGpuProcessMemory(incident.GetEvents<GpuProcessMemorySample>());
        var networkProbes = incident.GetEvents<NetworkProbeSample>();
        var networkEndpoints = incident.GetEvents<NetworkEndpointSample>();
        var artifacts = incident.GetEvents<ArtifactEvidence>();

        // Process names, OBS presence, disk throughput and similar context cannot prove a frametime
        // incident on their own. In particular, two idle Discord helper processes used to become a
        // 57% external-process conclusion when PresentMon had produced no rows at all.
        if (frameSamples.Count == 0)
        {
            var insufficient = new HypothesisScore(
                RootCauseCategory.InsufficientEvidence,
                1,
                ["Ingen framedata samlades in i incidentfönstret; ingen process kan tillskrivas stuttern utan observerade frames."]);
            return new IncidentAnalysis(
                [insufficient],
                true,
                "Insufficient evidence. PresentMon levererade inga frames i incidentfönstret.",
                [new TimelineHighlight(incident.Marker.MarkedAt, "Capture health", "0 frames i incidentfönstret; rotorsak klassificerades inte.")],
                []);
        }

        var metrics = BuildFrameMetrics(frameSamples, incident.Environment.DisplayRefreshRateHz);
        var gpu = BuildGpuMetrics(gpuSamples);
        var cores = BuildCoreMetrics(systemSamples);
        var suspectedProcesses = AnalyzeSuspiciousProcesses(systemSamples);
        var hypotheses = new List<HypothesisScore>();
        var correlatedThreadWait = FindCorrelatedThreadWait(artifacts, frameSamples, metrics);

        AddThreadWaitHypothesis(hypotheses, correlatedThreadWait);
        AddVramHypothesis(hypotheses, metrics, gpu, correlatedThreadWait, gpuProcessMemory);
        AddObsHypothesis(hypotheses, metrics, obsSamples, gpu);
        AddGpuHypothesis(hypotheses, metrics, obsSamples, systemSamples, gpu, correlatedThreadWait);
        AddResourceHypothesis(hypotheses, metrics, processSamples, artifacts, obsSamples, systemSamples, cores, correlatedThreadWait);
        AddNetworkHypothesis(hypotheses, metrics, networkProbes, networkEndpoints, artifacts, systemSamples, obsSamples);
        AddDiskHypothesis(hypotheses, metrics, processSamples, systemSamples, artifacts, correlatedThreadWait);
        AddExternalProcessHypothesis(hypotheses, suspectedProcesses, processSamples, systemSamples);
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

        var highlights = BuildHighlights(incident, metrics, hypotheses.First(), artifacts, obsSamples, gpu, networkProbes, suspectedProcesses, gpuProcessMemory);
        var top = hypotheses[0];
        var summary = BuildSummary(top, metrics, obsSamples, gpu, artifacts, networkProbes, suspectedProcesses, gpuProcessMemory);

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

        // Time as well as count. Counting spikes gives a 1 258 ms frame and a 34 ms frame one vote each,
        // which is how a window whose entire lost time was one CPU-bound stall came out at "6 of 12
        // CPU-bound" and let a storage verdict through. What the player lost is milliseconds, so the
        // share that decides an attribution has to be measured in them.
        //
        // The denominator is every spike, not merely the ones an attribution could be made for.
        // Dividing by the classified time instead would let a window with one CPU-bound frame and
        // eleven unattributable ones report 100% CPU-bound, which is a claim about 1/12 of the
        // evidence. Unknown time counts against the CPU share, so thin attribution coverage
        // produces a low share and the cap below simply does not fire — which is the conservative
        // direction, and the same way the count-based share behaved before this.
        var totalSpikeMs = 0d;
        var cpuBoundMs = 0d;

        foreach (var spike in spikes)
        {
            var kind = ClassifySpike(spike, baseline);
            totalSpikeMs += spike.FrameTimeMs;

            switch (kind)
            {
                case SpikeKind.CpuBound:
                    cpuBound++;
                    cpuBoundMs += spike.FrameTimeMs;
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
            cpuBoundMs,
            totalSpikeMs,
            frameSamples.Select(item => item.CpuBusyMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Select(item => item.GpuBusyMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Select(item => item.GpuWaitMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Select(item => item.FlipDelayMs ?? 0).DefaultIfEmpty().Max(),
            frameSamples.Count(item => item.Dropped),
            BuildPresentModeMetrics(frameSamples),
            BuildDisplayChangeMetrics(frameSamples, baseline));
    }

    /// <summary>
    /// Summarises how the frames in this window reached the screen.
    /// </summary>
    /// <remarks>
    /// The mode is a property of the machine's configuration far more than of the moment, so what
    /// matters is the mode nearly every frame used, not a distribution. A window that is entirely
    /// <c>Composed: Copy with GPU GDI</c> says the frames never got an independent flip at all — which
    /// costs latency on every single frame and is invisible in frame time, since a compositor that adds
    /// a consistent hop still produces a perfectly even cadence.
    /// </remarks>
    private static PresentModeMetrics BuildPresentModeMetrics(IReadOnlyList<FrameTelemetrySample> frameSamples)
    {
        var classified = frameSamples.Where(item => item.PresentMode is not null).ToArray();
        if (classified.Length == 0)
        {
            return PresentModeMetrics.Empty;
        }

        var dominant = classified
            .GroupBy(item => item.PresentMode!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .First();

        return new PresentModeMetrics(
            HasData: true,
            dominant.Key,
            (double)dominant.Count() / classified.Length,
            (double)classified.Count(item => item.IsComposedPresent) / classified.Length,
            classified.Length);
    }

    /// <summary>
    /// Compares the cadence of presents against the cadence of the screen actually changing.
    /// </summary>
    /// <remarks>
    /// A window where presents are even and display changes are not is the signature of frames being
    /// produced on time and then held after the present call. Reporting only frame time hides that case
    /// completely: the game looks like it is running perfectly and the player sees stutter.
    /// </remarks>
    private static DisplayChangeMetrics BuildDisplayChangeMetrics(IReadOnlyList<FrameTelemetrySample> frameSamples, double baseline)
    {
        var values = frameSamples
            .Select(item => item.MsBetweenDisplayChange)
            .Where(item => item is > 0)
            .Select(item => item!.Value)
            .OrderBy(value => value)
            .ToArray();

        if (values.Length == 0)
        {
            return DisplayChangeMetrics.Empty;
        }

        var threshold = Math.Max(baseline * SpikeMultiplier, 10);
        return new DisplayChangeMetrics(
            HasData: true,
            values.Length,
            Percentile(values, 0.50),
            Percentile(values, 0.99),
            values[^1],
            values.Count(value => value >= threshold));
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
    /// The window's fullest moment, which is the one worth reporting.
    /// </summary>
    /// <remarks>
    /// The per-process breakdown is sampled every few seconds while an incident window spans minutes,
    /// so an arbitrary sample can land after the game released the memory it was holding. Picking the
    /// sample with the largest total keeps the table describing the pressure the incident is about.
    /// </remarks>
    private static GpuProcessMemorySample? PeakGpuProcessMemory(IReadOnlyList<GpuProcessMemorySample> samples)
    {
        return samples
            .Where(sample => sample.IsAvailable && sample.Processes.Count > 0)
            .OrderByDescending(sample => sample.TotalDedicatedBytes)
            .FirstOrDefault();
    }

    /// <summary>
    /// The largest holders of VRAM as one sentence, or null when the breakdown was not collected.
    /// </summary>
    /// <remarks>
    /// Three entries, and only those holding at least a tenth of a gigabyte. The tail is a dozen
    /// processes with a few megabytes of desktop composition each, and listing them turns the one line
    /// that answers "what do I close" into something nobody reads.
    /// </remarks>
    private static string? DescribeGpuProcessMemory(GpuProcessMemorySample? sample)
    {
        if (sample is null)
        {
            return null;
        }

        var owners = sample
            .Top(3)
            .Where(process => process.DedicatedGigabytes >= 0.1)
            .Select(process => $"{process.ProcessName} {process.DedicatedGigabytes:F1} GB")
            .ToArray();

        return owners.Length > 0 ? string.Join(", ", owners) + "." : null;
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
    private static void AddVramHypothesis(
        List<HypothesisScore> hypotheses,
        FrameMetrics metrics,
        GpuMetrics gpu,
        CorrelatedThreadWait? correlatedThreadWait,
        GpuProcessMemorySample? gpuProcessMemory)
    {
        if (!gpu.HasData || gpu.PeakVramPercent < VramPressurePercent || metrics.SpikeCount == 0)
        {
            return;
        }

        // A measured off-CPU wait is direct evidence for where the missing time went. High occupancy
        // cannot override it: VRAM is adapter-wide and full VRAM is normal until an eviction is seen.
        if (correlatedThreadWait is not null)
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

        evidence.Add(DescribeGpuProcessMemory(gpuProcessMemory) is { } owners
            ? $"Fördelningen vid trycket: {owners}"
            : "Obs: VRAM mäts per grafikkort, inte per process, så andra program bidrar till siffran.");
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
        GpuMetrics gpu,
        CorrelatedThreadWait? correlatedThreadWait)
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
            if (correlatedThreadWait is not null)
            {
                confidence = Math.Min(confidence, ContradictedConfidenceCeiling);
                evidence.Add("ETL-spåret visar samtidigt att en aktiv GTA-tråd låg av CPU:n i en lång väntan; GPU-belastning förklarar därför inte den fångade pausen.");
            }

            hypotheses.Add(new HypothesisScore(RootCauseCategory.GpuFrametimeContention, Math.Min(confidence, 0.9), evidence));
        }
    }

    private static void AddThreadWaitHypothesis(
        List<HypothesisScore> hypotheses,
        CorrelatedThreadWait? wait)
    {
        if (wait is null)
        {
            return;
        }

        var evidence = new List<string>
        {
            $"ETL-schemaläggningen visar att aktiv GTA-tråd tid {wait.ThreadId:F0} var i Waiting-tillstånd i {wait.DurationMs:F1} ms under den långsamma framen.",
            "Den tiden var väntetid, inte schemalagd CPU-exekvering; PresentMon MsCPUBusy får därför inte tolkas som att tråden arbetade hela den långsamma framen.",
        };

        var confidence = 0.9;
        if (wait.IsUserRequest)
        {
            confidence = 0.98;
            evidence.Add("Den överlappande väntan klassades som Wait/UserRequest, vilket pekar på synkronisering, en signal eller I/O-svar inne i spelprocessen snarare än CPU- eller GPU-mättnad.");
        }

        hypotheses.Add(new HypothesisScore(RootCauseCategory.FiveMThreadWait, confidence, evidence));
    }

    private static void AddResourceHypothesis(
        List<HypothesisScore> hypotheses,
        FrameMetrics metrics,
        IReadOnlyList<ProcessTelemetrySample> processSamples,
        IReadOnlyList<ArtifactEvidence> artifacts,
        IReadOnlyList<ObsTelemetrySample> obsSamples,
        IReadOnlyList<SystemTelemetrySample> systemSamples,
        CoreMetrics cores,
        CorrelatedThreadWait? correlatedThreadWait)
    {
        var evidence = new List<string>();
        double confidence = 0;

        // Only a parsed profiler resource with measured time is positive resource evidence. A generic
        // profiler import and a Resmon text export are useful attachments, but neither proves that a
        // FiveM resource executed during this window.
        var profilerEvidence = artifacts
            .Where(item => item.Kind == ArtifactKind.ProfilerJson
                && item.Metrics.GetValueOrDefault("topResourceMs") > 0)
            .ToArray();
        if (profilerEvidence.Length > 0)
        {
            confidence += 0.45;
            evidence.AddRange(profilerEvidence.Select(item => item.Summary));
        }

        // The two instruments that measure execution rather than infer it. A profiler names the resource
        // that ran; a trace holds the samples that say which thread was on a processor and which was
        // waiting. Everything else in this method is a second-resolution average or the frame breakdown
        // itself, and neither can tell a running thread from a blocked one inside a single frame.
        //
        // Being an ETL is not the same as having measured anything. A trace can carry DPC latency and
        // nothing else — sampled profiles refused by another ETW session, a stream that died in the
        // first seconds, a profile that never asked for them — and a file with no CPU samples of the
        // game says exactly as much about whether its threads ran as no file at all. So the lift needs
        // the samples themselves and an attribution of them to the game's own process, which are the
        // two figures the parser writes when it has them.
        var measuredExecution = profilerEvidence.Length > 0
            || artifacts.Any(MeasuredGameExecution);

        if (metrics.HasCpuGpuBreakdown && metrics.CpuBoundSpikes > 0)
        {
            // Not counted at all when the trace shows the thread was asleep. PresentMon derives MsCPUBusy
            // from the gap between presents, so a main thread blocked on a lock reads exactly like one
            // executing script — a 586 ms frame reported 585 ms of CPU busy for a thread that was off the
            // processor for 569 of them. Adding confidence here and capping it afterwards was the earlier
            // shape of this rule and it still ranked the hypothesis first; the reading is not weak
            // evidence of script work, it is not evidence of it.
            if (correlatedThreadWait?.Contradicts(metrics.CpuBoundSpikeMs) != true)
            {
                confidence += 0.35;
                evidence.Add($"{metrics.CpuBoundSpikes} av {metrics.SpikeCount} frametime-spikes var CPU-bundna (CPU busy dominerade frametiden).");
            }
            else
            {
                evidence.Add(
                    $"BORTSETT: {metrics.CpuBoundSpikes} av {metrics.SpikeCount} spikes ser CPU-bundna ut, men ETL-spåret visar "
                    + $"att aktiv GTA-tråd tid {correlatedThreadWait.ThreadId:F0} låg av CPU:n i "
                    + $"{correlatedThreadWait.OverlappingSlowFrameMs:F0} av spikarnas {metrics.CpuBoundSpikeMs:F0} CPU-bundna ms. "
                    + "PresentMon räknar blockerad tid som CPU busy, så attributionen kan inte "
                    + "skilja skriptarbete från väntan här och räknas därför inte som stöd.");
            }
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
            var ceiling = 0.98;

            // The same contradiction GpuFrametimeContention already answers to. A thread asleep for most
            // of the frame is not running a script, and the remaining signals here — the game's process
            // CPU peak, a quiet system, a count of spikes — are all consistent with a process that is
            // blocked rather than working. They are worth a lead, not a verdict.
            if (correlatedThreadWait?.Contradicts(metrics.CpuBoundSpikeMs) == true)
            {
                ceiling = ContradictedConfidenceCeiling;
                evidence.Add(
                    $"NEDVIKTAD: ETL-spåret visar en aktiv GTA-tråd av CPU:n i "
                    + $"{correlatedThreadWait.OverlappingSlowFrameMs:F0} av fönstrets {metrics.CpuBoundSpikeMs:F0} "
                    + $"CPU-bundna spike-ms, så konfidensen är takad till {ContradictedConfidenceCeiling:P0}. "
                    + "En blockerad tråd utför inget skriptarbete, och resterande indicier skiljer inte de två fallen åt.");
            }
            else if (!measuredExecution)
            {
                // Not a contradiction — nothing here says the thread was asleep. It says nothing said it
                // was awake either, and a verdict about the game executing script needs a measurement of
                // the game executing.
                ceiling = Math.Min(ceiling, UncorroboratedAttributionCeiling);

                // The frame breakdown is named only when the window actually has one. Without it the
                // hypothesis rests on signals that are weaker still, and the cap is the same.
                var attributionNote = metrics.HasCpuGpuBreakdown && metrics.CpuBoundSpikes > 0
                    ? "PresentMon härleder MsCPUBusy ur mellanrummet mellan presents och rapporterar en blockerad "
                        + "tråd likadant som en som kör skript, och "
                    : string.Empty;

                evidence.Add(
                    $"OBEKRÄFTAD: ingen mätning av faktisk exekvering täcker fönstret, så konfidensen är takad till "
                    + $"{UncorroboratedAttributionCeiling:P0}. {attributionNote}per-kärna-räknarna och processens "
                    + "CPU-andel samplas en gång i sekunden och kan inte skilja de två fallen åt i en enskild frame. "
                    + "En deep capture eller en profiler över fönstret avgör frågan.");
            }

            hypotheses.Add(new HypothesisScore(RootCauseCategory.FiveMResourceSpike, Math.Min(confidence, ceiling), evidence));
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

        // A host that answered nothing, ever, is not a host that started dropping packets during this
        // incident. Game servers routinely refuse ICMP outright, and the probe host is inferred from
        // the connection rather than configured, so the common case for a total failure is that the
        // probe was aimed at something that was never going to reply. Treating that as evidence of
        // packet loss produced a network hypothesis in 26 incidents of a session whose every spike was
        // CPU bound, so the probes are allowed to say nothing rather than to say the wrong thing.
        var probesNeverAnswered = probes.Count > 0 && successfulProbes.Length == 0;

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

        if (failedProbes > 0 && !probesNeverAnswered)
        {
            confidence += 0.2;
            evidence.Add($"{failedProbes} probe-förfrågningar misslyckades under incidenten, medan andra svarade.");
        }

        if (endpoints.Any(item => item.RemoteEndpoints.Count > 0))
        {
            confidence += 0.05;
            evidence.Add("Aktiva remote endpoints fanns under incidenten.");
        }

        // A network incident should look like a healthy local machine that is nonetheless hitching.
        if (confidence > 0 && metrics.SpikeCount == 0 && systemSamples.Any(item => item.TotalCpuUsagePercent < 75) && obsSamples.All(item => !item.IsConnected))
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
        IReadOnlyList<ArtifactEvidence> artifacts,
        CorrelatedThreadWait? correlatedThreadWait)
    {
        var evidence = new List<string>();
        double confidence = 0;

        var maxRead = processSamples.Select(item => item.ReadBytesPerSecond).DefaultIfEmpty().Max();
        var competingIo = systemSamples.SelectMany(item => item.TopDiskProcesses).Where(item => !item.ProcessName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)).ToArray();
        var maxCompetingIo = competingIo.Select(item => item.IoBytesPerSecond).DefaultIfEmpty().Max();
        var maxLatency = systemSamples.Select(item => item.DiskAverageLatencyMs ?? 0).DefaultIfEmpty().Max();
        var maxQueue = systemSamples.Select(item => item.DiskQueueLength ?? 0).DefaultIfEmpty().Max();
        var maxHardFaultPages = systemSamples.Select(item => item.HardFaultPagesPerSecond ?? 0).DefaultIfEmpty().Max();

        // Availability is per counter, and only latency and queue length count towards it. Those two are
        // what separate a disk that is working hard from a disk that is slow; hard faults measure paging
        // and say nothing about whether the disk kept up. Treating "any one of the three was present" as
        // measured disk data meant a lone hard fault counter — or a queue counter that only ever read
        // zero — lifted the ceiling on evidence that was still just throughput.
        var hasLatencyData = systemSamples.Any(item => item.DiskAverageLatencyMs is not null);
        var hasQueueData = systemSamples.Any(item => item.DiskQueueLength is not null);
        var hasHardFaultData = systemSamples.Any(item => item.HardFaultPagesPerSecond is not null);
        var hasDiskCounterData = hasLatencyData || hasQueueData;
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

        if (maxLatency >= 20)
        {
            confidence += 0.3;
            evidence.Add($"Disklatensen toppade på {maxLatency:F1} ms.");
        }

        if (maxQueue >= 2)
        {
            confidence += 0.2;
            evidence.Add($"Diskkön toppade på {maxQueue:F1} samtidiga operationer.");
        }

        if (maxHardFaultPages >= 100)
        {
            confidence += 0.15;
            evidence.Add($"Paging från disk toppade på {maxHardFaultPages:F0} sidor/s.");
        }

        if (metrics.SevereSpikeCount > 0)
        {
            confidence += 0.1;
            evidence.Add("Frametime-spikes sammanföll med disk- eller streaming-signaler.");
        }

        var hasStrongIoFallback = !hasDiskCounterData
            && (maxRead >= 50 * 1024 * 1024 || maxCompetingIo >= 20 * 1024 * 1024);
        if (hasStrongIoFallback)
        {
            var missingCounters = new List<string>(3);
            if (!hasLatencyData)
            {
                missingCounters.Add("disklatens");
            }

            if (!hasQueueData)
            {
                missingCounters.Add("diskkö");
            }

            if (!hasHardFaultData)
            {
                missingCounters.Add("hard fault");
            }

            // Named rather than blanket: the warning used to claim all three were missing, which stopped
            // being true once each counter was tracked separately.
            var missingNames = missingCounters.Count == 1
                ? missingCounters[0]
                : string.Join(", ", missingCounters.SkipLast(1)) + " och " + missingCounters[^1];

            evidence.Add(
                $"VARNING: {missingNames}-counters saknades i fönstret. Bedömningen bygger bara på "
                + "genomströmning, och genomströmning kan inte skilja en disk som jobbar mycket från en disk som är långsam. "
                + $"Konfidensen är därför takad till {FallbackConfidenceCeiling:P0}.");
        }

        // The counters that measure a slow disk, as opposed to a busy one. Only these can outweigh the
        // per-frame attribution below, because only these observe latency rather than volume.
        var hasMeasuredStallSignal = maxLatency >= 20 || maxQueue >= 2 || maxHardFaultPages >= 100;

        var hasStorageStallSignal = hasMeasuredStallSignal
            || streamingHints.Length > 0
            || hasStrongIoFallback;
        if (confidence > 0 && hasStorageStallSignal)
        {
            // Without counters the evidence is throughput plus the fact that frames were slow, and that
            // combination reached 88% for an incident whose ETL contained five disk operations and three
            // hard faults. The measurements that would have ruled a disk stall out were the missing ones,
            // so the ceiling has to reflect their absence rather than the tally of what remained.
            var ceiling = hasDiskCounterData ? MeasuredConfidenceCeiling : FallbackConfidenceCeiling;

            // Per-frame attribution outranks every storage signal there is, the measured ones included.
            // A stalled disk stalls a frame by making the CPU wait; time the CPU spent executing is time
            // it was not blocked on storage, so a window whose lost time is overwhelmingly CPU-busy has
            // already ruled storage out no matter what the disk counters read. Concurrent is not causal:
            // the counters are a window maximum over ninety seconds, and a latency peak somewhere in
            // that window says nothing about the frame that actually hitched.
            //
            // This deliberately applies even when hasMeasuredStallSignal is set. Restricting it to
            // windows without measured signals was the earlier version of this rule, and it left the
            // exact case it was written for untouched — the incident that scored 88% had both a latency
            // reading and a 1 258 ms frame whose CPU was busy for 1 242 ms of it.
            // The contradiction rests on MsCPUBusy meaning execution, and it does not when the trace
            // shows the thread asleep. A thread blocked on a storage read is off the processor for the
            // whole frame and PresentMon reports every millisecond of it as CPU busy, so applying the
            // rule here would use a disk stall's own signature as proof that it was not one.
            if (metrics.HasCpuGpuBreakdown && metrics.SpikeCount > 0
                && correlatedThreadWait?.Contradicts(metrics.CpuBoundSpikeMs) != true)
            {
                var cpuBoundShare = (double)metrics.CpuBoundSpikes / metrics.SpikeCount;

                // Weighted by time first, and by count only as a fallback when nothing could be
                // attributed. Counting treats a 1 258 ms stall and a 34 ms wobble as one vote each,
                // which is how a window that lost 1.4 seconds — 95% of it inside a single CPU-bound
                // frame — scored 50% by count and let the storage verdict stand. Milliseconds are what
                // the player lost, so milliseconds are what the attribution is measured in.
                var decisiveShare = metrics.CpuBoundTimeShare ?? cpuBoundShare;
                if (decisiveShare >= DecisiveAttributionShare)
                {
                    ceiling = Math.Min(ceiling, ContradictedConfidenceCeiling);

                    var counterNote = hasMeasuredStallSignal
                        ? "Disksignalerna i fönstret är samtidiga, men kan inte förklara en frame som CPU:n tillbringade med att räkna"
                        : "Ingen disklatens, diskkö eller paging översteg heller tröskeln";

                    evidence.Add(
                        $"NEDVIKTAD: {decisiveShare:P0} av spike-tiden ({metrics.CpuBoundSpikeMs:F0} av {metrics.TotalSpikeMs:F0} ms, "
                        + $"{metrics.CpuBoundSpikes} av {metrics.SpikeCount} spikes) var CPU-bunden. {counterNote}, "
                        + $"så konfidensen är takad till {ContradictedConfidenceCeiling:P0}.");
                }
            }

            hypotheses.Add(new HypothesisScore(RootCauseCategory.StreamingOrDiskStall, Math.Min(confidence, ceiling), evidence));
        }
    }

    /// <summary>
    /// Ranks background processes by how much of the machine they took, not merely by how many of them
    /// were noticed.
    /// </summary>
    /// <remarks>
    /// The count alone put a ceiling of 78% on this category and reached it the same way for a pair of
    /// idle overlays as for the window that prompted this: OneDrive holding 3.68 of eight physical cores
    /// — more CPU than the game itself — with 87% of every file operation on the machine and the game's
    /// render thread sharing a physical core 86% of the time. That incident was ranked a FiveM script
    /// spike at 60% with this category second at 51%.
    /// <para>
    /// Both figures are percent of the whole machine, from the same counter, so the game and its
    /// neighbours are directly comparable. A background process that outweighs the game is the
    /// observation that separates interference from an evening's ordinary background noise, and it is
    /// the one the count could not make.
    /// </para>
    /// </remarks>
    private static void AddExternalProcessHypothesis(
        List<HypothesisScore> hypotheses,
        IReadOnlyList<SuspectedProcessImpact> suspectedProcesses,
        IReadOnlyList<ProcessTelemetrySample> processSamples,
        IReadOnlyList<SystemTelemetrySample> systemSamples)
    {
        if (suspectedProcesses.Count == 0)
        {
            return;
        }

        var evidence = suspectedProcesses
            .Take(3)
            .Select(item => $"{item.ProcessName} misstänks störa med {item.Reason.ToLowerInvariant()} (peak CPU {item.PeakCpuPercent:F0}%, disk {item.PeakIoMegabytesPerSecond:F1} MB/s).")
            .ToList();

        var confidence = 0.45 + (suspectedProcesses.Count * 0.06);

        var machinePeakCpu = systemSamples.Select(item => item.TotalCpuUsagePercent).DefaultIfEmpty().Max();

        // The game's peak rather than its concurrent figure, deliberately. It is the reading most
        // favourable to the game, so clearing it is the conservative version of the claim below.
        var gamePeakCpu = processSamples.Select(item => item.CpuUsagePercent).DefaultIfEmpty().Max();

        var suspectPeakCpu = ConcurrentSuspectCpu(suspectedProcesses, systemSamples);
        var suspectPeakIo = suspectedProcesses.Select(item => item.PeakIoMegabytesPerSecond).DefaultIfEmpty().Max();

        // A saturated machine is the precondition for interference to cost frames at all. With cores to
        // spare the scheduler simply runs both, which is why a busy neighbour on an idle machine is not
        // evidence of anything.
        if (machinePeakCpu >= 85)
        {
            confidence += 0.15;
            evidence.Add($"Maskinen var mättad: total CPU toppade på {machinePeakCpu:F0}%, så spelets trådar konkurrerade om kärnor snarare än att få egna.");
        }

        if (gamePeakCpu > 0 && suspectPeakCpu >= gamePeakCpu)
        {
            confidence += 0.2;
            evidence.Add($"Bakgrundsprocesserna tog samtidigt mer CPU än spelet: {suspectPeakCpu:F0}% i samma mätpunkt mot FiveM:s {gamePeakCpu:F0}% som mest (båda som andel av hela maskinen).");
        }

        // Volume on its own is not latency, so this is a smaller term than the two above. It is here
        // because a sync queue moving hundreds of megabytes a second is also thousands of file system
        // operations a second, and that traffic is contended for in the kernel by everything else.
        if (suspectPeakIo >= 200)
        {
            confidence += 0.1;
            evidence.Add($"Disktrafiken från bakgrunden toppade på {suspectPeakIo:F0} MB/s.");
        }

        hypotheses.Add(new HypothesisScore(
            RootCauseCategory.ExternalProcessInterference,
            Math.Min(confidence, 0.9),
            evidence));
    }

    /// <summary>
    /// The most CPU the suspected processes held <em>at one instant</em>, as a percentage of the machine.
    /// </summary>
    /// <remarks>
    /// Summing each suspect's own peak was the first version and it measures nothing real: the peaks are
    /// maxima over a ninety second window and need never have happened together, so a sync service busy
    /// at the start and a browser busy at the end would add up to a load the machine never carried. The
    /// claim the sum is used for — that the background outweighed the game — is about a moment, so it has
    /// to be measured in one.
    /// <para>
    /// Deduplicated by process within a sample, because a process busy on both CPU and disk appears in
    /// both of the sample's lists and would otherwise be counted twice.
    /// </para>
    /// </remarks>
    private static double ConcurrentSuspectCpu(
        IReadOnlyList<SuspectedProcessImpact> suspectedProcesses,
        IReadOnlyList<SystemTelemetrySample> systemSamples)
    {
        var suspects = suspectedProcesses
            .Select(item => (item.ProcessName, item.ProcessId))
            .ToHashSet();

        var highest = 0d;
        foreach (var sample in systemSamples)
        {
            var counted = new HashSet<(string, int)>();
            var total = 0d;

            foreach (var process in sample.TopCpuProcesses.Concat(sample.TopDiskProcesses))
            {
                var key = (process.ProcessName, process.ProcessId);
                if (suspects.Contains(key) && counted.Add(key))
                {
                    total += process.CpuPercent;
                }
            }

            if (total > highest)
            {
                highest = total;
            }
        }

        return highest;
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
        IReadOnlyList<SuspectedProcessImpact> suspectedProcesses,
        GpuProcessMemorySample? gpuProcessMemory)
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

        if (metrics.PresentMode.HasData)
        {
            var mode = metrics.PresentMode;
            var note = mode.IsComposed
                ? " Frames går genom DWM i stället för en egen flip, vilket lägger på en kompositorstuds på varje frame."
                : string.Empty;
            highlights.Add(new(
                incident.Marker.MarkedAt,
                "Present mode",
                $"{mode.DominantShare:P0} av {mode.ClassifiedFrames} frames presenterades som \"{mode.DominantMode}\".{note}"));
        }

        // Only worth a line when the two cadences disagree: presents on time with display changes
        // stuttering is a different fault from frames simply taking too long to produce.
        if (metrics.DisplayChange.HasData && metrics.DisplayChange.SpikeCount > metrics.SpikeCount)
        {
            highlights.Add(new(
                incident.Marker.MarkedAt,
                "Display change",
                $"{metrics.DisplayChange.SpikeCount} hopp i MsBetweenDisplayChange mot {metrics.SpikeCount} i frametime "
                + $"(median {metrics.DisplayChange.MedianMs:F1} ms, P99 {metrics.DisplayChange.P99Ms:F1} ms, max {metrics.DisplayChange.MaxMs:F1} ms). "
                + "Frames presenterades jämnare än de nådde skärmen."));
        }

        if (gpu.HasData)
        {
            highlights.Add(new(
                incident.Marker.MarkedAt,
                "GPU",
                $"{gpu.AdapterName ?? "GPU"}: peak {gpu.PeakUtilizationPercent:F0}% util, VRAM {gpu.PeakVramUsedGb:F1}/{gpu.TotalVramGb:F1} GB ({gpu.PeakVramPercent:F0}%), NVENC {gpu.PeakEncoderPercent:F0}%."));
        }

        if (DescribeGpuProcessMemory(gpuProcessMemory) is { } vramOwners)
        {
            highlights.Add(new(gpuProcessMemory!.Timestamp, "VRAM per process", vramOwners));
        }

        var obs = obsSamples.LastOrDefault();
        if (obs is not null)
        {
            var state = obs.IsStreaming
                ? "process körs, WebSocket ansluten, streamar"
                : obs.IsConnected
                    ? "process körs, WebSocket ansluten, streamar inte"
                    : obs.IsProcessRunning
                        ? "process körs, WebSocket frånkopplad"
                        : "process körs inte";
            highlights.Add(new(obs.Timestamp, "OBS", $"OBS-status: {state}. Render time {obs.AverageFrameRenderTimeMs:F1} ms, render skipped {obs.RenderSkippedFrames}, output skipped {obs.OutputSkippedFrames}."));
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
        IReadOnlyList<SuspectedProcessImpact> suspectedProcesses,
        GpuProcessMemorySample? gpuProcessMemory)
    {
        // Both are observations rather than conclusions, so they belong in the summary whether or not
        // the engine managed to classify anything. An unclassified window is precisely where "every
        // frame was composed" or "every probe failed" is the most useful thing anyone can be told.
        var presentModeHint = metrics.PresentMode switch
        {
            { IsComposed: true } composed =>
                $" Present mode var \"{composed.DominantMode}\" för {composed.DominantShare:P0} av frames — inga oberoende flips, "
                + "allt gick via kompositorn.",
            { IsUniform: true } uniform => $" Present mode var \"{uniform.DominantMode}\" genom hela fönstret.",
            { HasData: true } mixed => $" Present mode växlade; vanligast var \"{mixed.DominantMode}\" ({mixed.DominantShare:P0}).",
            _ => string.Empty,
        };
        var probeHint = BuildProbeHint(probes);

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

            return $"Insufficient evidence. Kör gärna en ny session i grundläge igen.{hint}{presentModeHint}{probeHint}";
        }

        var obsActive = obsSamples.Any(item => item.IsStreaming)
            ? "OBS-processen körde, WebSocket var ansluten och streamen var aktiv."
            : obsSamples.Any(item => item.IsConnected)
                ? "OBS-processen körde och WebSocket var ansluten, men streamen var inte aktiv."
                : obsSamples.Any(item => item.IsProcessRunning)
                    ? "OBS-processen körde men WebSocket var inte ansluten."
                    : "OBS-processen körde inte.";
        var attribution = metrics.HasCpuGpuBreakdown && metrics.SpikeCount > 0
            ? $" Av {metrics.SpikeCount} spikes var {metrics.CpuBoundSpikes} CPU-bundna, {metrics.GpuBoundSpikes} GPU-bundna och {metrics.PresentBoundSpikes} present/display-bundna."
            : string.Empty;
        // The adapter figure says how full the card was; the top holder says whose memory it was. Four
        // sessions of reports carried the first without the second, and the reader could only guess.
        var vramOwnerHint = gpuProcessMemory?.Top(1).FirstOrDefault() is { } largest
            ? $" Störst i VRAM: {largest.ProcessName} med {largest.DedicatedGigabytes:F1} GB."
            : string.Empty;
        var vramHint = gpu.HasData
            ? $" VRAM toppade på {gpu.PeakVramPercent:F0}% ({gpu.PeakVramUsedGb:F1}/{gpu.TotalVramGb:F1} GB).{vramOwnerHint}"
            : string.Empty;
        var artifactHint = artifacts.Count > 0 ? $" {artifacts.Count} importerade artifacts bidrog till bedömningen." : string.Empty;
        var suspectHint = suspectedProcesses.FirstOrDefault() is { } suspect
            ? $" Mest avvikande bakgrundsprocess: {suspect.ProcessName} ({suspect.Reason.ToLowerInvariant()})."
            : string.Empty;

        return $"Trolig rotorsak: {ToLabel(top.Category)} ({top.Confidence:P0}). Frametime-fönstret hade baseline {metrics.BaselineFrameTime:F1} ms, P95 {metrics.P95FrameTime:F1} ms och P99 {metrics.P99FrameTime:F1} ms.{attribution}{presentModeHint}{vramHint} {obsActive}{artifactHint}{probeHint}{suspectHint}";
    }

    /// <summary>
    /// Whether a trace actually sampled the game running, rather than merely covering the window.
    /// </summary>
    /// <remarks>
    /// <c>cpuSampleCount</c> is what the parser counts of the sampled-profile stream; <c>
    /// cpuSubjectIsGame</c> records that the selected process really was FiveM/GTA rather than the
    /// parser's hottest-process fallback. Positive game cores are the weakest claim that is still worth
    /// something: the trace looked at processors while the window was open and saw the game on them. A trace missing either is kept as
    /// evidence for everything else it holds — DPC and ISR durations, disk and hard faults, the wait
    /// chain — and simply does not lift the ceiling on a claim about script work.
    /// </remarks>
    private static bool MeasuredGameExecution(ArtifactEvidence artifact)
    {
        return artifact.Kind == ArtifactKind.EtlTrace
            && artifact.Metrics.GetValueOrDefault("cpuSampleCount") > 0
            && artifact.Metrics.GetValueOrDefault("cpuSubjectIsGame") > 0
            && artifact.Metrics.GetValueOrDefault("cpuSubjectProcessCores") > 0;
    }

    /// <summary>
    /// Says what the network probes actually did, rather than that they existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Nätprober fanns tillgängliga" was true of a window in which every single probe failed, which is
    /// close to the opposite of what a reader takes from it — probes that all fail are either a blocked
    /// ICMP path or a network that was down, and both are findings rather than background.
    /// </para>
    /// <para>
    /// The advice that used to follow — point <c>ServerProfile.ProbeHost</c> at a host that answers, or
    /// turn the probes off — is the collector's line now and not this one. It is a fact about the
    /// session and it belongs in the session log once, which is where the collector writes it when it
    /// gives up on a host. Under every incident it spent three sentences of each report explaining that
    /// a measurement was missing, which is as useful the hundredth time as it was the first.
    /// </para>
    /// </remarks>
    private static string BuildProbeHint(IReadOnlyList<NetworkProbeSample> probes)
    {
        if (probes.Count == 0)
        {
            return string.Empty;
        }

        var failed = probes.Count(item => !item.Success);
        if (failed == 0)
        {
            var worstRtt = probes.Select(item => item.RoundTripTimeMs ?? 0).DefaultIfEmpty().Max();
            return $" Alla {probes.Count} nätprober svarade, högsta RTT {worstRtt:F0} ms.";
        }

        if (failed == probes.Count)
        {
            var reason = probes
                .Select(item => item.FailureReason)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? "okänt fel";

            return $" Samtliga {probes.Count} nätprober misslyckades ({reason}); ingen RTT-mätning för fönstret.";
        }

        var succeededRtt = probes.Where(item => item.Success).Select(item => item.RoundTripTimeMs ?? 0).DefaultIfEmpty().Max();
        return $" {failed} av {probes.Count} nätprober misslyckades; de som svarade toppade på {succeededRtt:F0} ms.";
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
            .Where(item => item.Impact.PeakCpuPercent >= 12 || item.Impact.PeakIoMegabytesPerSecond >= 12)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Impact.ObservedSamples)
            .Select(item => item.Impact)
            .Take(5)
            .ToArray();
    }

    private static bool IsRelevantExternalProcess(string processName)
    {
        var baseName = Path.GetFileNameWithoutExtension(processName);
        return !processName.Contains("FiveM", StringComparison.OrdinalIgnoreCase)
            && !processName.Contains("GTA", StringComparison.OrdinalIgnoreCase)
            && !processName.Contains("obs", StringComparison.OrdinalIgnoreCase)
            && !processName.Contains("FiveMDiagnostics", StringComparison.OrdinalIgnoreCase)
            && !baseName.Equals("wpr", StringComparison.OrdinalIgnoreCase)
            && !baseName.Equals("wprui", StringComparison.OrdinalIgnoreCase)
            && !baseName.Equals("PresentMon", StringComparison.OrdinalIgnoreCase)
            && !processName.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            && !processName.Equals("System", StringComparison.OrdinalIgnoreCase);
    }

    private static CorrelatedThreadWait? FindCorrelatedThreadWait(
        IReadOnlyList<ArtifactEvidence> artifacts,
        IReadOnlyList<FrameTelemetrySample> frameSamples,
        FrameMetrics metrics)
    {
        var slowFrames = frameSamples
            .Where(frame => frame.FrameTimeMs >= Math.Max(100, metrics.SpikeThresholdMs))
            .ToArray();
        if (slowFrames.Length == 0)
        {
            return null;
        }

        var matches = new List<CorrelatedThreadWait>();
        foreach (var trace in artifacts.Where(item => item.Kind == ArtifactKind.EtlTrace))
        {
            var count = (int)trace.Metrics.GetValueOrDefault("gameThreadWaitIntervalCount");
            var threadId = trace.Metrics.GetValueOrDefault("gameThreadWaitThreadId");
            for (var index = 0; index < count; index++)
            {
                var start = trace.Metrics.GetValueOrDefault($"gameThreadWait{index}StartUnixMs");
                var end = trace.Metrics.GetValueOrDefault($"gameThreadWait{index}EndUnixMs");
                var duration = trace.Metrics.GetValueOrDefault($"gameThreadWait{index}DurationMs");
                if (start <= 0 || end <= start || duration < 100)
                {
                    continue;
                }

                // PresentMon timestamps the beginning of the frame-side work. Fifty milliseconds of
                // clock/anchor tolerance is enough for batching without allowing an unrelated wait
                // several seconds earlier in the retained ETL to become the incident's explanation.
                //
                // How much of the slow frames the wait actually covers is kept, not just whether it
                // touched one. A window is ninety seconds and can hold several unrelated causes; a wait
                // that overlaps one frame by a millisecond is not a reason to discount the attribution
                // for all of them, and treating it as one hid whatever else was in the window.
                var overlapMs = 0d;
                foreach (var frame in slowFrames)
                {
                    var frameStart = frame.Timestamp.ToUnixTimeMilliseconds() - 50;
                    var frameEnd = frame.Timestamp.ToUnixTimeMilliseconds() + frame.FrameTimeMs + 50;
                    var overlap = Math.Min(end, frameEnd) - Math.Max(start, frameStart);
                    if (overlap > 0)
                    {
                        overlapMs += overlap;
                    }
                }

                if (overlapMs > 0)
                {
                    matches.Add(new CorrelatedThreadWait(
                        threadId,
                        duration,
                        trace.Metrics.GetValueOrDefault($"gameThreadWait{index}UserRequest") >= 0.5,

                        // Frames can overlap each other after the tolerance is applied, so the summed
                        // overlap is clamped to the wait itself: a thread cannot be off the processor
                        // for longer than it was off the processor.
                        Math.Min(overlapMs, duration)));
                }
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        // The longest wait names the incident, but every matched wait counts towards how much of the
        // window's lost time is accounted for by waiting. Two 300 ms waits explain a 600 ms hole; the
        // longest of them alone explains half of it.
        return matches
            .OrderByDescending(wait => wait.DurationMs)
            .First() with { OverlappingSlowFrameMs = matches.Sum(wait => wait.OverlappingSlowFrameMs) };
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
            RootCauseCategory.FiveMThreadWait => "FiveM-tråd blockerad i Waiting",
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
        double CpuBoundSpikeMs,
        double TotalSpikeMs,
        double MaxCpuBusyMs,
        double MaxGpuBusyMs,
        double MaxGpuWaitMs,
        double MaxFlipDelayMs,
        int DroppedCount,
        PresentModeMetrics PresentMode,
        DisplayChangeMetrics DisplayChange)
    {
        public static FrameMetrics Empty { get; } =
            new(0, 0, 16.67, 25, 40, 0, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PresentModeMetrics.Empty, DisplayChangeMetrics.Empty);

        /// <summary>
        /// Share of the window's whole lost time that the CPU spent computing rather than waiting.
        /// Spikes that could not be attributed are in the denominator, so an incomplete attribution
        /// lowers this rather than flattering it. Null when there were no spikes at all.
        /// </summary>
        public double? CpuBoundTimeShare => TotalSpikeMs > 0 ? CpuBoundSpikeMs / TotalSpikeMs : null;
    }

    /// <summary>
    /// A wait from the trace that lands on the window's slow frames.
    /// </summary>
    /// <param name="OverlappingSlowFrameMs">
    /// How much of the slow frames this wait actually covers, summed across every matched wait. This is
    /// what decides whether the wait contradicts the window's CPU attribution or merely coincides with
    /// part of it — a ninety second window can hold more than one cause, and a wait that overlaps one
    /// frame by a millisecond is not a reason to discount the attribution for all of them.
    /// </param>
    private sealed record CorrelatedThreadWait(
        double ThreadId,
        double DurationMs,
        bool IsUserRequest,
        double OverlappingSlowFrameMs)
    {
        /// <summary>
        /// Whether waiting accounts for a decisive share of the CPU-bound time it would be used against.
        /// </summary>
        /// <remarks>
        /// Zero CPU-bound time means there is nothing to contradict, and the answer is false rather than
        /// vacuously true: the contradiction exists only to stop MsCPUBusy being read as execution, so
        /// with no such reading in play it has no work to do.
        /// </remarks>
        public bool Contradicts(double cpuBoundSpikeMs)
        {
            return cpuBoundSpikeMs > 0
                && OverlappingSlowFrameMs >= cpuBoundSpikeMs * DecisiveAttributionShare;
        }
    }

    private sealed record PresentModeMetrics(
        bool HasData,
        string? DominantMode,
        double DominantShare,
        double ComposedShare,
        int ClassifiedFrames)
    {
        public static PresentModeMetrics Empty { get; } = new(false, null, 0, 0, 0);

        /// <summary>True when effectively every frame took the same path to the screen.</summary>
        public bool IsUniform => HasData && DominantShare >= DominantPresentModeShare;

        /// <summary>True when that path went through the compositor rather than an independent flip.</summary>
        public bool IsComposed => ComposedShare >= DominantPresentModeShare;
    }

    private sealed record DisplayChangeMetrics(
        bool HasData,
        int SampleCount,
        double MedianMs,
        double P99Ms,
        double MaxMs,
        int SpikeCount)
    {
        public static DisplayChangeMetrics Empty { get; } = new(false, 0, 0, 0, 0, 0);
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
