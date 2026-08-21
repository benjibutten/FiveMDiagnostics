using System.Globalization;
using System.Text;

namespace FiveMDiagnostics.Integrations.Nvml;

using FiveMDiagnostics.Core;

/// <summary>
/// Appends every GPU sample to a CSV for the length of a session.
/// </summary>
/// <remarks>
/// GPU telemetry used to survive only inside incident windows, so anything the analysis did not fold
/// into a timeline string was gone — recovering how VRAM behaved across an evening meant taking 42
/// separate timeline strings apart by hand. A flat file next to the PresentMon CSV costs a few
/// megabytes for a whole stream and makes the same question a matter of opening it.
/// <para>
/// Written unredacted for the same reason the session journal is: it is local evidence, not a bundle
/// meant to be handed to someone else.
/// </para>
/// </remarks>
public sealed class GpuTelemetryCsvLog : IDisposable
{
    /// <summary>
    /// Ceiling on one session's file. At the default 500 ms cadence a row is about 120 bytes, so this
    /// covers well over a day — but the polling interval is a setting, and a file that grows for a whole
    /// stream needs a bound that is not "the user notices".
    /// </summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private const string Header =
        "timestampUtc,isAvailable,adapterName,utilizationPercent,memoryBandwidthPercent,"
        + "usedVramBytes,totalVramBytes,vramUsagePercent,encoderPercent,decoderPercent,temperatureCelsius,throttleReasons";

    private readonly object _sync = new();
    private readonly long _maxBytes;

    private StreamWriter? _writer;
    private long _bytesWritten;

    private GpuTelemetryCsvLog(StreamWriter writer, string path, long maxBytes, long existingBytes)
    {
        _writer = writer;
        _maxBytes = maxBytes;
        Path = path;
        _bytesWritten = existingBytes;
    }

    /// <summary>Full path of the file being written, for the status entry that tells the user where it is.</summary>
    public string Path { get; }

    /// <summary>Set once the file has been closed, whether by disposal, an IO failure or the size budget.</summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// Opens the log for a session. Returns null with a reason rather than throwing: a session that
    /// cannot write this file is still a session worth running.
    /// </summary>
    public static GpuTelemetryCsvLog? TryOpen(string workingDirectory, DateTimeOffset startedAtUtc, out string? error, long maxBytes = DefaultMaxBytes)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(workingDirectory);
            var path = System.IO.Path.Combine(workingDirectory, $"gpu_{startedAtUtc:yyyyMMdd_HHmmss}.csv");

            // Append rather than Create, so two sessions started inside the same second add to the file
            // instead of the second erasing the first. FileShare.Read lets it be opened while running,
            // which is when watching VRAM climb is most useful.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var existingBytes = stream.Length;
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                // The failure this file exists for is the app being closed or killed mid-stream, so a
                // row has to be on its way to disk by the time it is written. At a couple of rows a
                // second the cost is nothing.
                AutoFlush = true,
            };

            var log = new GpuTelemetryCsvLog(writer, path, Math.Max(maxBytes, 64 * 1024), existingBytes);
            if (existingBytes == 0)
            {
                log.WriteLine(Header);
            }

            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    public void Append(GpuTelemetrySample sample)
    {
        WriteLine(string.Join(',', [
            sample.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            sample.IsAvailable ? "true" : "false",
            Escape(sample.AdapterName),
            Format(sample.UtilizationPercent, "F0"),
            Format(sample.MemoryBandwidthUtilizationPercent, "F0"),
            sample.UsedVramBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            sample.TotalVramBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Format(sample.VramUsagePercent, "F1"),
            Format(sample.EncoderUtilizationPercent, "F0"),
            Format(sample.DecoderUtilizationPercent, "F0"),
            sample.TemperatureCelsius?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Escape(string.Join(';', sample.ThrottleReasons)),
        ]));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void WriteLine(string line)
    {
        lock (_sync)
        {
            if (_writer is null)
            {
                return;
            }

            var lineBytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (_bytesWritten + lineBytes > _maxBytes)
            {
                Failure ??= $"GPU-loggen nådde sin storleksgräns på {_maxBytes / (1024 * 1024)} MB och stängdes.";
                _writer.Dispose();
                _writer = null;
                return;
            }

            try
            {
                _writer.WriteLine(line);
                _bytesWritten += lineBytes;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                Failure ??= $"GPU-loggen kunde inte skrivas och stängdes: {ex.Message}";
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private static string Format(double? value, string format)
    {
        return value?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
