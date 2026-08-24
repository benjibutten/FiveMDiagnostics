namespace FiveMDiagnostics.Tools.EtlAnalyzer;

using Microsoft.Windows.EventTracing.Cpu;

/// <summary>
/// The slice of a trace that actually carries CPU samples, and the helpers every report needs to turn
/// sample counts into something comparable.
/// </summary>
/// <remarks>
/// A ring buffer trace spans hours but only retains the last few seconds of sampling, so the file's own
/// duration is useless as a denominator — one capture would report "0.7 % CPU" for a thread that was
/// pinned. Every rate in this tool is therefore computed against the sampled span, and expressed in
/// <em>cores</em>: at the 1 kHz default sampling rate one sample per millisecond is one core held busy,
/// which compares directly against a frame budget (0.89 cores at 22 ms/frame is 19.6 ms of CPU per
/// frame) and across captures of different lengths.
/// </remarks>
internal sealed class TraceWindow
{
    private TraceWindow(IReadOnlyList<ICpuSample> samples, double durationSeconds, double samplesPerCoreSecond)
    {
        Samples = samples;
        DurationSeconds = durationSeconds;
        SamplesPerCoreSecond = samplesPerCoreSecond;
    }

    public IReadOnlyList<ICpuSample> Samples { get; }

    public double DurationSeconds { get; }

    /// <summary>
    /// Samples one fully busy processor produces per second, i.e. the reciprocal of the sampling
    /// interval.
    /// </summary>
    /// <remarks>
    /// Read from the samples rather than assumed to be 1 kHz. WPR's default is 1 kHz, but a profile
    /// recorded at a different rate would make every figure in this tool wrong by exactly that factor,
    /// silently and plausibly. Each sample carries the interval it represents as its weight, so the
    /// median weight is the rate the trace was actually recorded at.
    /// </remarks>
    public double SamplesPerCoreSecond { get; }

    public bool IsEmpty => Samples.Count == 0 || DurationSeconds <= 0;

    public decimal StartRelativeMilliseconds => Samples.Count > 0
        ? Samples[0].Timestamp.RelativeTimestamp.TotalMilliseconds
        : 0;

    public decimal EndRelativeMilliseconds => Samples.Count > 0
        ? Samples[^1].Timestamp.RelativeTimestamp.TotalMilliseconds
        : 0;

    public bool Contains(decimal relativeMilliseconds)
    {
        return !IsEmpty
            && relativeMilliseconds >= StartRelativeMilliseconds
            && relativeMilliseconds <= EndRelativeMilliseconds;
    }

    public static TraceWindow From(IReadOnlyList<ICpuSample> samples)
    {
        if (samples.Count == 0)
        {
            return new TraceWindow(samples, 0, 1000);
        }

        var span = (double)(samples[^1].Timestamp.RelativeTimestamp.TotalSeconds
            - samples[0].Timestamp.RelativeTimestamp.TotalSeconds);

        // Median rather than the first sample's weight: the first and last samples of a wrapped ring
        // buffer can carry a partial interval.
        var weights = samples
            .Select(sample => (double)sample.Weight.TotalMilliseconds)
            .Where(weight => weight > 0)
            .Order()
            .ToArray();
        var intervalMs = weights.Length > 0 ? weights[weights.Length / 2] : 1d;

        return new TraceWindow(samples, span, 1000d / intervalMs);
    }

    /// <summary>
    /// Restricts the retained sample window to offsets measured from its first CPU sample.
    /// </summary>
    public TraceWindow Slice(int? fromMilliseconds, int? toMilliseconds)
    {
        if (IsEmpty || (fromMilliseconds is null && toMilliseconds is null))
        {
            return this;
        }

        var origin = Samples[0].Timestamp.RelativeTimestamp.TotalMilliseconds;
        var from = fromMilliseconds ?? 0;
        var to = toMilliseconds is { } end ? (decimal)end : decimal.MaxValue;
        var sliced = Samples
            .Where(sample =>
            {
                var offset = sample.Timestamp.RelativeTimestamp.TotalMilliseconds - origin;
                return offset >= from && offset < to;
            })
            .ToArray();

        return From(sliced);
    }

    /// <summary>Sample count expressed as the number of CPU cores that many samples represent.</summary>
    public double Cores(int sampleCount)
    {
        return DurationSeconds > 0 ? sampleCount / DurationSeconds / SamplesPerCoreSecond : 0;
    }

    public string Header(string filePath)
    {
        if (IsEmpty)
        {
            return $"{Path.GetFileName(filePath)}: no CPU samples in trace";
        }

        var first = Samples[0].Timestamp.DateTimeOffset;
        var last = Samples[^1].Timestamp.DateTimeOffset;
        return $"{Path.GetFileName(filePath)}  {first:HH:mm:ss.fff}–{last:HH:mm:ss.fff}  "
            + $"{DurationSeconds:F2}s sampled  {Samples.Count} samples at {SamplesPerCoreSecond:F0} Hz  "
            + $"{Cores(Samples.Count):F2} cores busy";
    }

    public static string ProcessName(ICpuSample sample) => sample.Process?.ImageName ?? "?";

    public static string ModuleName(ICpuSample sample) => sample.Image?.FileName ?? "?";

    public static int ThreadId(ICpuSample sample) => sample.Thread?.Id ?? -1;
}
