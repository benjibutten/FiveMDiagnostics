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
    /// Buffered samples beyond which attribution stops collecting. Each entry is twelve bytes, so this
    /// caps the parser at roughly 48 MB — far more than any ring buffer capture produces, and a bound
    /// worth having before a file-mode capture of unknown length is ever parsed.
    /// </summary>
    private const int MaxBufferedSamples = 4_000_000;

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
    private readonly List<(int ThreadId, ulong InstructionPointer)> _samples = [];

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

        _samples.Add((data.ThreadID, data.InstructionPointer));
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

        // Keyed by process as well as thread. Windows reuses thread ids once a thread exits, so a key
        // of thread id alone can merge two unrelated threads' work into one row — unlikely inside a
        // window of seconds, and wrong in a way nothing downstream could detect.
        foreach (var (threadId, instructionPointer) in _samples)
        {
            var processId = _processByThread.GetValueOrDefault(threadId, -1);
            samplesByProcess[processId] = samplesByProcess.GetValueOrDefault(processId) + 1;

            var threadKey = (processId, threadId);
            samplesByThread[threadKey] = samplesByThread.GetValueOrDefault(threadKey) + 1;

            var moduleKey = (processId, threadId, Resolve(processId, instructionPointer));
            samplesByThreadModule[moduleKey] = samplesByThreadModule.GetValueOrDefault(moduleKey) + 1;
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
            ToCores(subject.Value),
            busiestThread.Key.ThreadId,
            ToCores(busiestThread.Value),
            modules);
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

    private string Name(int processId)
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

/// <summary>Where the sampled CPU time in a trace went.</summary>
internal sealed record CpuAttributionSummary(
    double TotalCores,
    string SubjectProcess,
    double SubjectProcessCores,
    int BusiestThreadId,
    double BusiestThreadCores,
    IReadOnlyList<ModuleShare> BusiestThreadModules)
{
    /// <summary>
    /// The sentence the investigation actually needed, in the form it was written by hand three times.
    /// </summary>
    public string Describe()
    {
        var modules = BusiestThreadModules.Count > 0
            ? " – " + string.Join(", ", BusiestThreadModules.Select(item => $"{item.Share:P0} {item.Module}"))
            : string.Empty;

        return $"CPU-sampling: {TotalCores:F2} kärnor upptagna totalt. {SubjectProcess} höll {SubjectProcessCores:F2} kärnor, "
            + $"och dess hetaste tråd (tid {BusiestThreadId}) {BusiestThreadCores:F2} kärnor{modules}.";
    }
}
