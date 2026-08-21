using System.Globalization;

namespace FiveMDiagnostics.Integrations.PresentMon;

using FiveMDiagnostics.Core;

/// <summary>
/// Maps a PresentMon CSV row onto <see cref="FrameTelemetrySample"/>.
/// </summary>
/// <remarks>
/// PresentMon 2.4.1 emits three different column schemes and the differences are not cosmetic — a
/// parser written against one produces zero rows against another. Verified empirically against a live
/// capture:
/// <list type="bullet">
/// <item><description>
/// <b>default</b> (no metrics flag): <c>TimeInMs</c>, <c>MsBetweenPresents</c>, <c>MsCPUBusy</c>,
/// <c>MsGPUBusy</c>, <c>MsUntilDisplayed</c>. This is the richest set and what the app requests.
/// </description></item>
/// <item><description>
/// <b>--v2_metrics</b>: <c>CPUStartTime</c>, <c>FrameTime</c>, <c>CPUBusy</c>, <c>GPUBusy</c>,
/// <c>DisplayLatency</c> — same data, no <c>Ms</c> prefix, and fewer columns than the default.
/// </description></item>
/// <item><description>
/// <b>--v1_metrics</b>: <c>TimeInSeconds</c>, <c>msBetweenPresents</c>, <c>msGPUActive</c>,
/// <c>Dropped</c>, and no CPU/GPU split at all.
/// </description></item>
/// </list>
/// Lookups are case-insensitive, so v1's lowercase <c>ms</c> prefix collides with the default scheme's
/// <c>Ms</c> prefix and needs no separate entry.
/// </remarks>
public static class PresentMonCsvParser
{
    private static readonly string[] FrameTimeColumns = ["FrameTime", "MsBetweenPresents", "msBetweenDisplayChange"];
    private static readonly string[] CpuBusyColumns = ["CPUBusy", "MsCPUBusy"];
    private static readonly string[] CpuWaitColumns = ["CPUWait", "MsCPUWait"];
    private static readonly string[] GpuBusyColumns = ["GPUBusy", "MsGPUBusy", "msGPUActive", "GPUTime", "MsGPUTime"];
    private static readonly string[] GpuWaitColumns = ["GPUWait", "MsGPUWait"];
    private static readonly string[] GpuLatencyColumns = ["GPULatency", "MsGPULatency"];
    private static readonly string[] PresentApiColumns = ["MsInPresentAPI"];
    private static readonly string[] FlipDelayColumns = ["MsFlipDelay"];
    private static readonly string[] InputLatencyColumns =
        ["AllInputToPhotonLatency", "MsAllInputToPhotonLatency", "ClickToPhotonLatency", "MsClickToPhotonLatency"];

    /// <summary>
    /// How the frame reached the screen: <c>Hardware: Independent Flip</c>, <c>Composed: Flip</c>,
    /// <c>Composed: Copy with GPU GDI</c> and so on.
    /// </summary>
    /// <remarks>
    /// Read because it is the one column that describes the present path itself rather than its timing,
    /// and no amount of frame time analysis substitutes for it: a capture where all 848 272 frames sat
    /// in <c>Composed: Copy with GPU GDI</c> is a machine compositing every frame through DWM, which is
    /// obvious in three seconds of looking at the raw CSV and was invisible to this app.
    /// </remarks>
    private static readonly string[] PresentModeColumns = ["PresentMode"];

    /// <summary>
    /// Time between frames actually changing on screen, as opposed to between presents.
    /// </summary>
    /// <remarks>
    /// The two diverge exactly when the interesting failures happen. Presents arriving on a steady
    /// cadence while display changes stutter means the frames are being produced on time and held
    /// somewhere after the present call — a compositor or flip queue problem that
    /// <c>MsBetweenPresents</c> alone reports as a perfectly healthy frame rate.
    /// </remarks>
    private static readonly string[] DisplayChangeColumns = ["MsBetweenDisplayChange"];

    /// <summary>
    /// Time of the frame relative to the start of the PresentMon trace. Whichever of these columns is
    /// present, the unit is milliseconds — <c>CPUStartTime</c> included, confirmed against a four second
    /// capture whose final row read 3996.99.
    /// </summary>
    private static readonly string[] RelativeMillisecondColumns = ["TimeInMs", "CPUStartTime", "CPUStartTimeInMs"];

    /// <summary>
    /// Time until the frame reached the screen. Doubles as the dropped-frame signal: PresentMon 2.x has
    /// no <c>Dropped</c> column, and writes "NA" here for a frame that was never displayed.
    /// </summary>
    private static readonly string[] DisplayLatencyColumns = ["DisplayLatency", "MsUntilDisplayed", "msUntilDisplayChange"];

    public static Dictionary<string, int> ParseHeader(string headerLine)
    {
        return headerLine
            .Split(',')
            .Select((value, index) => (Header: value.Trim(), Index: index))
            .ToDictionary(item => item.Header, item => item.Index, StringComparer.OrdinalIgnoreCase);
    }

    public static double? ReadRelativeMs(string[] cells, IReadOnlyDictionary<string, int> headerIndex)
    {
        var milliseconds = ReadDouble(cells, headerIndex, RelativeMillisecondColumns);
        if (milliseconds is not null)
        {
            return milliseconds;
        }

        return ReadDouble(cells, headerIndex, "TimeInSeconds") * 1000;
    }

    public static FrameTelemetrySample? ParseRow(
        string[] cells,
        IReadOnlyDictionary<string, int> headerIndex,
        string fallbackProcessName,
        DateTimeOffset timestamp)
    {
        var frameTime = ReadDouble(cells, headerIndex, FrameTimeColumns);
        if (frameTime is null)
        {
            return null;
        }

        return new FrameTelemetrySample(
            timestamp,
            frameTime.Value,
            ReadDouble(cells, headerIndex, GpuBusyColumns),
            ReadDouble(cells, headerIndex, DisplayLatencyColumns),
            frameTime,
            ReadBool(cells, headerIndex, "Dropped") || IsUndisplayed(cells, headerIndex),
            ReadString(cells, headerIndex, "Application", "ProcessName") ?? fallbackProcessName,
            ReadDouble(cells, headerIndex, PresentApiColumns),
            ReadDouble(cells, headerIndex, CpuBusyColumns),
            ReadDouble(cells, headerIndex, CpuWaitColumns),
            ReadDouble(cells, headerIndex, GpuWaitColumns),
            ReadDouble(cells, headerIndex, GpuLatencyColumns),
            ReadDouble(cells, headerIndex, FlipDelayColumns),
            ReadDouble(cells, headerIndex, InputLatencyColumns),
            ReadPresentMode(cells, headerIndex),
            ReadDouble(cells, headerIndex, DisplayChangeColumns));
    }

    /// <summary>
    /// Reads the present mode, treating the blank and "NA" cells PresentMon writes for a frame it could
    /// not classify as absent rather than as a mode named "NA".
    /// </summary>
    private static string? ReadPresentMode(string[] cells, IReadOnlyDictionary<string, int> headerIndex)
    {
        var value = ReadString(cells, headerIndex, PresentModeColumns)?.Trim();
        return string.IsNullOrEmpty(value) || string.Equals(value, "NA", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private static bool IsUndisplayed(string[] cells, IReadOnlyDictionary<string, int> headerIndex)
    {
        foreach (var columnName in DisplayLatencyColumns)
        {
            if (!headerIndex.TryGetValue(columnName, out var index) || index >= cells.Length)
            {
                continue;
            }

            return string.Equals(cells[index].Trim(), "NA", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? ReadString(string[] cells, IReadOnlyDictionary<string, int> headerIndex, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (headerIndex.TryGetValue(columnName, out var index) && index < cells.Length)
            {
                return cells[index];
            }
        }

        return null;
    }

    private static double? ReadDouble(string[] cells, IReadOnlyDictionary<string, int> headerIndex, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (!headerIndex.TryGetValue(columnName, out var index) || index >= cells.Length)
            {
                continue;
            }

            // "NA" marks a metric that does not apply to this frame; fall through to the next candidate
            // column rather than treating the whole row as unparseable.
            if (double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool ReadBool(string[] cells, IReadOnlyDictionary<string, int> headerIndex, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (!headerIndex.TryGetValue(columnName, out var index) || index >= cells.Length)
            {
                continue;
            }

            var cell = cells[index];
            if (bool.TryParse(cell, out var parsed))
            {
                return parsed;
            }

            if (int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                return numeric != 0;
            }
        }

        return false;
    }
}
