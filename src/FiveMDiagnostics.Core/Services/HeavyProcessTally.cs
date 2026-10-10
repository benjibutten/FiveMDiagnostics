using System.Globalization;

namespace FiveMDiagnostics.Core;

/// <summary>
/// The programs that weighed most on the machine beside the game during one session: the most VRAM each
/// held at once and its average share of the CPU. Saved at session end, so the next session can suggest
/// them for closing before FiveM starts.
/// </summary>
public sealed class HeavyProcessTally
{
    private const string FileName = "last-session-heavy-processes.txt";

    private readonly Dictionary<string, double> _peakVramMegabytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _cpuPercentSum = new(StringComparer.OrdinalIgnoreCase);
    private int _cpuSamples;

    /// <summary>Folds in one per-process VRAM sample, summing the processes that share a name.</summary>
    public void Observe(GpuProcessMemorySample sample)
    {
        if (!sample.IsAvailable)
        {
            return;
        }

        var perName = sample.Processes
            .Where(process => !sample.IsUnbelievable(process) && BackgroundProgram.IsCandidate(process.ProcessName))
            .GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in perName)
        {
            var megabytes = group.Sum(process => (double)process.DedicatedBytes) / 1024 / 1024;
            _peakVramMegabytes[group.Key] = Math.Max(_peakVramMegabytes.GetValueOrDefault(group.Key), megabytes);
        }
    }

    /// <summary>
    /// Folds in one system sample's busiest processes. A program missing from a sample's list counts as
    /// idle for that sample, so the average is over the whole session.
    /// </summary>
    public void Observe(SystemTelemetrySample sample)
    {
        _cpuSamples++;

        var perName = sample.TopCpuProcesses
            .Where(process => !process.IsSystemService && BackgroundProgram.IsCandidate(process.ProcessName))
            .GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in perName)
        {
            _cpuPercentSum[group.Key] = _cpuPercentSum.GetValueOrDefault(group.Key) + group.Sum(process => process.CpuPercent);
        }
    }

    /// <summary>Every program seen, heaviest first.</summary>
    public IReadOnlyList<HeavyProcess> Results() =>
        _peakVramMegabytes.Keys
            .Union(_cpuPercentSum.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(name => new HeavyProcess(
                name,
                _peakVramMegabytes.GetValueOrDefault(name),
                _cpuSamples == 0 ? 0 : _cpuPercentSum.GetValueOrDefault(name) / _cpuSamples))
            .OrderByDescending(process => process.PeakVramMegabytes)
            .ThenByDescending(process => process.AverageCpuPercent)
            .ToArray();

    /// <summary>
    /// Overwrites the file with this session's results. Best effort: losing it costs one session's
    /// suggestions, and it runs during shutdown.
    /// </summary>
    public static void Save(string workingDirectory, IReadOnlyList<HeavyProcess> processes)
    {
        try
        {
            Directory.CreateDirectory(workingDirectory);
            File.WriteAllLines(
                Path.Combine(workingDirectory, FileName),
                processes.Select(process => string.Join(
                    '\t',
                    process.ProcessName,
                    process.PeakVramMegabytes.ToString("F0", CultureInfo.InvariantCulture),
                    process.AverageCpuPercent.ToString("F2", CultureInfo.InvariantCulture))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The previous session's results, or empty when there are none or the file cannot be read.</summary>
    public static IReadOnlyList<HeavyProcess> TryLoad(string workingDirectory)
    {
        try
        {
            var path = Path.Combine(workingDirectory, FileName);
            if (!File.Exists(path))
            {
                return [];
            }

            return File.ReadAllLines(path)
                .Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 3)
                .Select(parts => new HeavyProcess(
                    parts[0],
                    double.TryParse(parts[1], CultureInfo.InvariantCulture, out var vram) ? vram : 0,
                    double.TryParse(parts[2], CultureInfo.InvariantCulture, out var cpu) ? cpu : 0))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <param name="AverageCpuPercent">Average share of the whole machine's CPU over the session.</param>
public sealed record HeavyProcess(string ProcessName, double PeakVramMegabytes, double AverageCpuPercent)
{
    /// <summary>A noticeable share of the 8 to 12 GB cards FiveM is usually played on.</summary>
    public const double VramWorthClosingMegabytes = 300;

    /// <summary>A program that kept working through the whole session rather than one that woke up once.</summary>
    public const double CpuWorthClosingPercent = 2;

    /// <summary>Whether the program held enough of either to be worth suggesting.</summary>
    public bool IsWorthClosing => PeakVramMegabytes >= VramWorthClosingMegabytes || AverageCpuPercent >= CpuWorthClosingPercent;
}
