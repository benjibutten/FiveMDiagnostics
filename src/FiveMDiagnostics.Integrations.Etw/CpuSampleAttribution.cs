using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// Attributes sampled CPU time to a process, a thread and the module the instruction pointer landed in.
/// </summary>
/// <remarks>
/// <para>
/// "The spike was CPU-bound" was as far as the analysis could get for three sessions, and it is not far
/// enough to act on: it is the same sentence whether the game is computing, an anti-cheat layer is
/// enumerating windows, or a script engine is running server Lua on the render thread. The distinction
/// between those decides whether the answer is a graphics setting, a support ticket or nothing at all,
/// and it is one image load table away from data the trace already contains.
/// </para>
/// <para>
/// Attribution stops at the module. Function names would need symbol servers and minutes per trace,
/// and module granularity has so far been enough — "0.15 cores in citizen-scripting-lua.dll, up from
/// 0.02" located a cause that no amount of frame time analysis had.
/// </para>
/// <para>
/// Rates are reported in <em>cores</em>: one sample per millisecond of wall clock is one processor held
/// busy. That compares directly against a frame budget, and unlike a percentage it does not change
/// meaning when the trace is a ring buffer whose retained window is seconds long.
/// </para>
/// </remarks>
internal sealed class CpuSampleAttribution
{
    /// <summary>
    /// Sampling interval in 100 ns units when the trace never said. 1 kHz is the WPR default, and the
    /// alternative to assuming it is refusing to report a rate at all.
    /// </summary>
    private const int DefaultIntervalIn100Ns = 10_000;

    /// <summary>
    /// Buffered samples beyond which attribution stops collecting. Each entry is sixteen bytes, so this
    /// caps the parser at roughly 64 MB — far more than any ring buffer capture produces, and a bound
    /// worth having before a file-mode capture of unknown length is ever parsed.
    /// </summary>
    private const int MaxBufferedSamples = 4_000_000;

    /// <summary>Kernel module that pages video memory in and out of the card.</summary>
    /// <remarks>
    /// The whole point of <see cref="VideoMemoryPressure"/>. Windows moves allocations over PCIe on a
    /// System worker thread inside this driver, so the cost of a full card lands here and nowhere else:
    /// not in the game's threads, which go quiet, and not on the GPU, which idles.
    /// </remarks>
    private const string VideoMemoryManagerModule = "dxgmms2.sys";

    /// <summary>Bucket the per-second video memory rate is measured in.</summary>
    /// <remarks>
    /// A freeze lasts seconds, and one second is short enough to separate it from the trace's quiet
    /// remainder while still holding a thousand samples per busy processor.
    /// </remarks>
    private const double VideoMemoryBucketSeconds = 1.0;

    /// <summary>Complete buckets below which per-second rates are not reported at all.</summary>
    private const int MinimumVideoMemoryBuckets = 3;

    private readonly Dictionary<int, List<ImageRange>> _imagesByProcess = [];
    private readonly List<ImageRange> _kernelImages = [];
    private readonly Dictionary<int, string> _processNames = [];
    private readonly Dictionary<int, int> _processByThread = [];

    /// <summary>
    /// Thread id and instruction pointer per sample, resolved to a process and a module only once the
    /// whole trace has been read.
    /// </summary>
    /// <remarks>
    /// Resolving as the samples arrive does not work. Sampled profile events carry a thread id but
    /// usually report <c>ProcessID</c> as -1, and the rundown that maps threads to processes is emitted
    /// when the session <em>stops</em> — after every sample in the file. Eager resolution therefore put
    /// the entire trace under "pid -1", which owns no image table, so every frame came back as an
    /// unknown module. The failure was silent: the summary still read like an answer.
    /// </remarks>
    private readonly List<(int ThreadId, ulong InstructionPointer, int OffsetMs)> _samples = [];

    private int _intervalIn100Ns = DefaultIntervalIn100Ns;
    private DateTime? _firstSample;
    private DateTime? _lastSample;
    private bool _bufferFull;

    public long SampleCount { get; private set; }

    /// <summary>True when the trace held more samples than attribution was willing to buffer.</summary>
    public bool IsTruncated => _bufferFull;

    /// <summary>
    /// Wall clock the retained samples actually span.
    /// </summary>
    /// <remarks>
    /// Deliberately not the trace duration, and owned here rather than passed in. A ring buffer ETL is
    /// hours long on disk while retaining seconds of sampling — the first attempt divided by the trace
    /// length and reported 0.02 cores for a machine running at 9.3, which is wrong by three orders of
    /// magnitude and looks plausible enough to be believed.
    /// </remarks>
    public double SampledSeconds => _firstSample is { } first && _lastSample is { } last
        ? Math.Max((last - first).TotalSeconds, 0)
        : 0;

    public DateTime? FirstSampleTimestamp => _firstSample;

    public DateTime? LastSampleTimestamp => _lastSample;

    /// <summary>Samples make a sleeping background thread distinguishable from an active game loop.</summary>
    public int SampleCountForThread(int threadId)
    {
        return _samples.Count(sample => sample.ThreadId == threadId);
    }

    public bool IsGameThread(int threadId)
    {
        return _processByThread.TryGetValue(threadId, out var processId) && IsGameProcess(Name(processId));
    }

    public bool IsGameProcess(int processId) => IsGameProcess(Name(processId));

    public int ProcessIdForThread(int threadId) => _processByThread.GetValueOrDefault(threadId, -1);

    /// <summary>
    /// Main/render game threads spend a material share in the GTA executable. Requiring that share
    /// excludes dormant helpers and, crucially, a reused thread id whose later owner was the game.
    /// </summary>
    public double GameExecutableSampleShareForThread(int threadId, int processId)
    {
        var samples = _samples.Where(sample => sample.ThreadId == threadId).ToArray();
        if (samples.Length == 0)
        {
            return 0;
        }

        var gameSamples = samples.Count(sample =>
            Resolve(processId, sample.InstructionPointer).Contains("GTAProcess", StringComparison.OrdinalIgnoreCase));
        return (double)gameSamples / samples.Length;
    }

    /// <summary>Records an image load. Kernel images are kept in one shared range list.</summary>
    public void OnImageLoad(ImageLoadTraceData data)
    {
        var range = new ImageRange(data.ImageBase, data.ImageBase + (ulong)data.ImageSize, ImageName(data.FileName));

        // Drivers are loaded into the System process but execute on whichever thread entered the
        // kernel, so a per-process table would fail to resolve every kernel frame in the trace.
        if (data.ProcessID is 0 or 4 || range.Start >= 0xFFFF_8000_0000_0000)
        {
            _kernelImages.Add(range);
            return;
        }

        if (!_imagesByProcess.TryGetValue(data.ProcessID, out var images))
        {
            images = [];
            _imagesByProcess[data.ProcessID] = images;
        }

        images.Add(range);
    }

    /// <summary>
    /// Records which process a thread belongs to, from thread start and rundown events.
    /// </summary>
    public void OnThread(ThreadTraceData data)
    {
        if (data.ThreadID >= 0 && data.ProcessID >= 0)
        {
            _processByThread[data.ThreadID] = data.ProcessID;
        }
    }

    /// <summary>
    /// Records the same mapping from context switches.
    /// </summary>
    /// <remarks>
    /// Belt and braces for a thread that neither started during the trace nor survived to the stop
    /// rundown, which is otherwise invisible to the thread events entirely. Context switches see every
    /// thread that ran, which by definition includes every thread that was sampled.
    /// </remarks>
    public void OnContextSwitch(CSwitchTraceData data)
    {
        if (data.NewThreadID >= 0 && data.NewProcessID >= 0)
        {
            _processByThread[data.NewThreadID] = data.NewProcessID;
        }

        if (data.OldThreadID >= 0 && data.OldProcessID >= 0)
        {
            _processByThread[data.OldThreadID] = data.OldProcessID;
        }
    }

    public void OnProcess(ProcessTraceData data)
    {
        if (data.ProcessID >= 0 && !string.IsNullOrEmpty(data.ImageFileName))
        {
            _processNames[data.ProcessID] = data.ImageFileName;
        }
    }

    public void OnSamplingInterval(SampledProfileIntervalTraceData data)
    {
        if (data.NewInterval > 0)
        {
            _intervalIn100Ns = data.NewInterval;
        }
    }

    public void OnSample(SampledProfileTraceData data)
    {
        SampleCount++;
        _firstSample ??= data.TimeStamp;
        _lastSample = data.TimeStamp;

        if (data.ProcessID >= 0 && data.ThreadID >= 0)
        {
            _processByThread.TryAdd(data.ThreadID, data.ProcessID);
        }

        if (_samples.Count >= MaxBufferedSamples)
        {
            _bufferFull = true;
            return;
        }

        // Offset rather than a timestamp: four bytes instead of eight, and every question asked of it
        // is "where in the retained window", never "at what wall clock".
        var offsetMs = _firstSample is { } first ? (int)Math.Clamp((data.TimeStamp - first).TotalMilliseconds, 0, int.MaxValue) : 0;
        _samples.Add((data.ThreadID, data.InstructionPointer, offsetMs));
    }

    /// <summary>
    /// Summarises the busiest thread of the process under investigation, plus the machine-wide load.
    /// </summary>
    /// <returns>Null when the trace retained no samples, which is the normal state of a wrapped ring buffer.</returns>
    public CpuAttributionSummary? Summarize()
    {
        var seconds = SampledSeconds;
        if (_samples.Count == 0 || seconds <= 0)
        {
            return null;
        }

        var samplesByProcess = new Dictionary<int, long>();
        var samplesByThread = new Dictionary<(int ProcessId, int ThreadId), long>();
        var samplesByThreadModule = new Dictionary<(int ProcessId, int ThreadId, string Module), long>();

        // Per second of the retained window, so a six-second freeze inside a thirty-second trace can be
        // told apart from the trace's own baseline. Both series are filled in the one pass the samples
        // are already resolved in; a second pass over four million entries is not free.
        var bucketCount = (int)Math.Ceiling(seconds / VideoMemoryBucketSeconds);
        var videoMemoryByBucket = new long[Math.Max(bucketCount, 1)];
        var samplesByProcessBucket = new Dictionary<int, long[]>();

        // Keyed by process as well as thread. Windows reuses thread ids once a thread exits, so a key
        // of thread id alone can merge two unrelated threads' work into one row — unlikely inside a
        // window of seconds, and wrong in a way nothing downstream could detect.
        foreach (var (threadId, instructionPointer, offsetMs) in _samples)
        {
            var processId = _processByThread.GetValueOrDefault(threadId, -1);
            samplesByProcess[processId] = samplesByProcess.GetValueOrDefault(processId) + 1;

            var threadKey = (processId, threadId);
            samplesByThread[threadKey] = samplesByThread.GetValueOrDefault(threadKey) + 1;

            var module = Resolve(processId, instructionPointer);
            var moduleKey = (processId, threadId, module);
            samplesByThreadModule[moduleKey] = samplesByThreadModule.GetValueOrDefault(moduleKey) + 1;

            var bucket = Math.Clamp((int)(offsetMs / (VideoMemoryBucketSeconds * 1000)), 0, videoMemoryByBucket.Length - 1);
            if (module.Equals(VideoMemoryManagerModule, StringComparison.OrdinalIgnoreCase))
            {
                videoMemoryByBucket[bucket]++;
            }

            if (!samplesByProcessBucket.TryGetValue(processId, out var processBuckets))
            {
                processBuckets = new long[videoMemoryByBucket.Length];
                samplesByProcessBucket[processId] = processBuckets;
            }

            processBuckets[bucket]++;
        }

        // Samples per second of one busy processor, from the interval the trace reported rather than
        // from an assumed 1 kHz: a profile recorded at a different rate would otherwise be misread by
        // exactly that factor.
        var samplesPerCoreSecond = 10_000_000d / Math.Max(_intervalIn100Ns, 1);
        double ToCores(long samples) => samples / seconds / samplesPerCoreSecond;

        var subject = SelectSubject(samplesByProcess);
        var busiestThread = samplesByThread
            .Where(entry => entry.Key.ProcessId == subject.Key)
            .OrderByDescending(entry => entry.Value)
            .First();

        var modules = samplesByThreadModule
            .Where(entry => entry.Key.ProcessId == busiestThread.Key.ProcessId
                && entry.Key.ThreadId == busiestThread.Key.ThreadId)
            .OrderByDescending(entry => entry.Value)
            .Take(4)
            .Select(entry => new ModuleShare(entry.Key.Module, (double)entry.Value / busiestThread.Value, ToCores(entry.Value)))
            .ToArray();

        return new CpuAttributionSummary(
            ToCores(_samples.Count),
            Name(subject.Key),
            IsGameProcess(Name(subject.Key)),
            ToCores(subject.Value),
            busiestThread.Key.ThreadId,
            ToCores(busiestThread.Value),
            modules,
            SummarizeVideoMemory(
                videoMemoryByBucket,
                samplesByProcessBucket.GetValueOrDefault(subject.Key),
                Name(subject.Key),
                seconds,
                samplesPerCoreSecond));
    }

    /// <summary>
    /// Measures how hard Windows was moving video memory, per second, and what the subject process was
    /// doing while it happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one measurement that separates a full card from a busy one, and the reason it exists is the
    /// 1 September session. A nine-second freeze produced frames of 790, 677 and 381 ms with
    /// <c>MsCPUBusy</c> covering almost the whole of each, which every summary the app writes calls
    /// CPU-bound. The trace says the opposite: the game fell from 3.93 to 3.22 cores and its render
    /// thread from 0.64 to 0.34, while <c>dxgmms2.sys</c> on a System worker went from 0.18 to 0.91.
    /// Nobody was computing. The card was full and the driver was evacuating it.
    /// </para>
    /// <para>
    /// Reported as a peak against a baseline rather than as one average, because the average over a
    /// thirty-second trace containing a six-second freeze is neither number and answers no question.
    /// The baseline is the median second, which a freeze cannot move.
    /// </para>
    /// </remarks>
    private static VideoMemoryPressure? SummarizeVideoMemory(
        long[] videoMemoryByBucket,
        long[]? subjectByBucket,
        string subjectProcess,
        double seconds,
        double samplesPerCoreSecond)
    {
        var total = videoMemoryByBucket.Sum();
        var overallCores = total / seconds / samplesPerCoreSecond;

        // Only whole buckets. The last one is a fraction of a second and dividing its samples by a full
        // second understates it, which on a trace that ends inside the freeze understates exactly the
        // number the measurement exists for.
        var completeBuckets = (int)Math.Floor(seconds / VideoMemoryBucketSeconds);
        if (completeBuckets < MinimumVideoMemoryBuckets)
        {
            return new VideoMemoryPressure(overallCores, overallCores, subjectProcess, null, null);
        }

        double Cores(long samples) => samples / VideoMemoryBucketSeconds / samplesPerCoreSecond;

        var perBucket = new double[completeBuckets];
        for (var index = 0; index < completeBuckets; index++)
        {
            perBucket[index] = Cores(videoMemoryByBucket[index]);
        }

        var peakBucket = 0;
        for (var index = 1; index < completeBuckets; index++)
        {
            if (perBucket[index] > perBucket[peakBucket])
            {
                peakBucket = index;
            }
        }

        var baseline = Median(perBucket);

        double? subjectAtPeak = null;
        double? subjectBaseline = null;
        if (subjectByBucket is not null)
        {
            var subjectPerBucket = new double[completeBuckets];
            for (var index = 0; index < completeBuckets; index++)
            {
                subjectPerBucket[index] = Cores(subjectByBucket[index]);
            }

            subjectAtPeak = subjectPerBucket[peakBucket];
            subjectBaseline = Median(subjectPerBucket);
        }

        return new VideoMemoryPressure(baseline, perBucket[peakBucket], subjectProcess, subjectAtPeak, subjectBaseline);
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>
    /// Picks the process the summary is about: the game if it is in the trace, otherwise whatever used
    /// the most CPU.
    /// </summary>
    /// <remarks>
    /// The hottest process is usually the game, but "usually" is not good enough for the one line a
    /// reader takes away. A capture made while a browser or an encoder was busier would silently
    /// describe that instead, and the reader has no way to tell from the sentence that it happened.
    /// </remarks>
    private KeyValuePair<int, long> SelectSubject(Dictionary<int, long> samplesByProcess)
    {
        var game = samplesByProcess
            .Where(entry => IsGameProcess(Name(entry.Key)))
            .OrderByDescending(entry => entry.Value)
            .ToArray();

        return game.Length > 0
            ? game[0]
            : samplesByProcess.OrderByDescending(entry => entry.Value).First();
    }

    private static bool IsGameProcess(string name)
    {
        return name.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase)
            || name.Contains("FiveM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The process's image name, or a pid when the trace never named it.
    /// </summary>
    /// <remarks>
    /// Public because the file system attribution needs the same table. The kernel's FileIO events carry
    /// a process id and no name, and a report that says "pid 5672 made 125 000 operations a second" is
    /// one lookup short of being usable — that pid is Windows Search, and nobody reading the line can
    /// know it.
    /// </remarks>
    public string Name(int processId)
    {
        return _processNames.TryGetValue(processId, out var name) ? name : $"pid {processId}";
    }

    private string Resolve(int processId, ulong instructionPointer)
    {
        if (_imagesByProcess.TryGetValue(processId, out var images) && Find(images, instructionPointer) is { } module)
        {
            return module;
        }

        return Find(_kernelImages, instructionPointer) ?? "okänd modul";
    }

    private static string? Find(List<ImageRange> images, ulong address)
    {
        // Linear, because the list is a few hundred entries and this runs once per sample on a
        // background task. A sorted array with a binary search is the obvious next step if a longer
        // capture ever makes it matter.
        for (var index = images.Count - 1; index >= 0; index--)
        {
            var image = images[index];
            if (address >= image.Start && address < image.End)
            {
                return image.Name;
            }
        }

        return null;
    }

    private static string ImageName(string? fileName)
    {
        return string.IsNullOrEmpty(fileName) ? "okänd modul" : Path.GetFileName(fileName);
    }

    private readonly record struct ImageRange(ulong Start, ulong End, string Name);
}

/// <summary>Share of one thread's sampled time spent in a module.</summary>
internal sealed record ModuleShare(string Module, double Share, double Cores);

/// <summary>
/// How hard Windows was moving video memory during the trace, and what that cost the subject process.
/// </summary>
/// <param name="BaselineCores">Median second in <c>dxgmms2.sys</c> across the retained window.</param>
/// <param name="PeakCores">Busiest second in <c>dxgmms2.sys</c>.</param>
/// <param name="SubjectProcess">Process the two subject figures describe.</param>
/// <param name="SubjectCoresAtPeak">What that process held during the busiest video-memory second.</param>
/// <param name="SubjectBaselineCores">What it held in the median second.</param>
internal sealed record VideoMemoryPressure(
    double BaselineCores,
    double PeakCores,
    string SubjectProcess,
    double? SubjectCoresAtPeak,
    double? SubjectBaselineCores)
{
    /// <summary>
    /// Peak rate at which the driver is unmistakably evacuating the card rather than doing housekeeping.
    /// </summary>
    /// <remarks>
    /// Calibrated against the four captures of 1 September, where the module tracked the card's fill
    /// monotonically: 0.05 cores at 84% VRAM, 0.15 at 86%, 0.18 at 88%, 0.22 at 86% with a cluster,
    /// 0.41 during a hitch at 91% and 0.91 during the nine-second freeze at 92%. The 0.40 bar sits
    /// between the highest quiet reading and the lowest reading taken while frames were being lost.
    /// </remarks>
    public const double PressuredCores = 0.40;

    public bool IsPressured => PeakCores >= PressuredCores;

    /// <summary>True when the subject process went quieter while the driver was busiest.</summary>
    /// <remarks>
    /// The signature that separates waiting from computing, and the sentence that would have saved two
    /// sessions of calling these frames CPU-bound.
    /// </remarks>
    public bool SubjectWentQuiet =>
        SubjectCoresAtPeak is { } atPeak
        && SubjectBaselineCores is { } baseline
        && baseline > 0
        && atPeak < baseline * 0.9;

    /// <summary>
    /// The band above which the card's own occupancy corroborates an eviction reading.
    /// </summary>
    /// <remarks>
    /// The same 88% three sessions of frame data put the edge at. Below it the driver's work is
    /// something else — a resolution change, a mode switch, a level load handing over surfaces — and
    /// saying "the card was full" about it is simply false.
    /// </remarks>
    private const double CorroboratingVramPercent = 88;

    /// <param name="adapterVramPercent">
    /// How full the card was, when the session knows. The trace does not contain it, and the conclusion
    /// depends on it: 0.42 cores at 92% occupancy is eviction, and the same 0.42 cores at 54% is not.
    /// </param>
    public string Describe(double? adapterVramPercent = null)
    {
        if (!IsPressured)
        {
            return $"Videominne: {ModuleGlossary.Annotate("dxgmms2.sys")} höll som mest {PeakCores:F2} kärnor "
                + "— ingen mätbar flyttning av videominne i spåret.";
        }

        var quiet = SubjectWentQuiet
            ? $" Samtidigt föll {SubjectProcess} till {SubjectCoresAtPeak!.Value:F2} kärnor, mot {SubjectBaselineCores!.Value:F2} "
                + "i spårets övriga sekunder — tiden gick åt till att vänta, inte till att räkna."
            : string.Empty;

        var measurement = $"Videominne: {ModuleGlossary.Annotate("dxgmms2.sys")} höll {PeakCores:F2} kärnor som mest "
            + $"under en sekund, mot {BaselineCores:F2} i spårets lugna sekunder.";

        var meaning = adapterVramPercent switch
        {
            { } percent when percent >= CorroboratingVramPercent =>
                $" Kortet låg samtidigt på {percent:F0} %, så flyttningen är eviction: drivrutinen "
                + "gjorde plats genom att skyffla ytor över PCIe.",

            { } percent =>
                $" Men kortet låg bara på {percent:F0} %, alltså under {CorroboratingVramPercent:F0} % "
                + "där eviction börjar. Drivrutinen flyttade minne av något annat skäl — en inladdning "
                + "eller ett lägesbyte — och det här var inte minnestryck.",

            _ => " Så mycket flyttning brukar betyda att kortet är fullt och att drivrutinen evakuerar "
                + "ytor över PCIe, men spåret innehåller ingen avläsning av kortets fyllnadsgrad — "
                + "läs den mot GPU-raden i incidenten innan slutsatsen dras.",
        };

        return measurement + meaning + quiet;
    }
}

/// <summary>Where the sampled CPU time in a trace went.</summary>
internal sealed record CpuAttributionSummary(
    double TotalCores,
    string SubjectProcess,
    bool SubjectIsGame,
    double SubjectProcessCores,
    int BusiestThreadId,
    double BusiestThreadCores,
    IReadOnlyList<ModuleShare> BusiestThreadModules,
    VideoMemoryPressure? VideoMemory)
{
    /// <summary>
    /// The sentence the investigation actually needed, in the form it was written by hand three times.
    /// </summary>
    /// <param name="adapterVramPercent">
    /// The card's occupancy, passed through to the video memory sentence, which cannot be concluded
    /// without it. Null leaves that sentence saying only what the trace measured.
    /// </param>
    public string Describe(double? adapterVramPercent = null)
    {
        var modules = BusiestThreadModules.Count > 0
            ? " – " + string.Join(", ", BusiestThreadModules.Select(item => $"{item.Share:P0} {ModuleGlossary.Annotate(item.Module)}"))
            : string.Empty;

        var videoMemory = VideoMemory is { } pressure ? " " + pressure.Describe(adapterVramPercent) : string.Empty;

        return $"CPU-sampling: {TotalCores:F2} kärnor upptagna totalt. {SubjectProcess} höll {SubjectProcessCores:F2} kärnor, "
            + $"och dess hetaste tråd (tid {BusiestThreadId}) {BusiestThreadCores:F2} kärnor{modules}.{videoMemory}";
    }
}
