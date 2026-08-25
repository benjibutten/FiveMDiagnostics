using System.Globalization;
using System.Text;

namespace FiveMDiagnostics.Core;

/// <summary>
/// An append-only CSV that lasts a session, with a size budget and a reason when it stops.
/// </summary>
/// <remarks>
/// Telemetry used to survive only inside incident windows, so anything the analysis did not fold into
/// a timeline string was gone — recovering how VRAM behaved across an evening meant taking 42 separate
/// timeline strings apart by hand. A flat file next to the PresentMon CSV costs a few megabytes for a
/// whole stream and turns the same question into opening it.
/// <para>
/// Failures are recorded rather than thrown. A session that cannot write its log is still a session
/// worth running, and a full disk should produce one warning rather than one per sample.
/// </para>
/// <para>
/// Written unredacted, like the session journal: it is local evidence, not a bundle meant to be handed
/// to someone else.
/// </para>
/// </remarks>
public sealed class RollingCsvLog : IDisposable
{
    /// <summary>
    /// Ceiling on one session's file. At a couple of rows a second this covers well over a day — but
    /// polling intervals are settings, and a file that grows for a whole stream needs a bound that is
    /// not "the user notices".
    /// </summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly long _maxBytes;
    private readonly string _description;

    private StreamWriter? _writer;
    private long _bytesWritten;

    private RollingCsvLog(StreamWriter writer, string path, string description, long maxBytes, long existingBytes)
    {
        _writer = writer;
        _maxBytes = maxBytes;
        _description = description;
        Path = path;
        _bytesWritten = existingBytes;
    }

    /// <summary>Full path of the file being written, for the status entry that tells the user where it is.</summary>
    public string Path { get; }

    /// <summary>Set once the file has been closed, whether by disposal, an IO failure or the size budget.</summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// Opens a log for a session. Returns null with a reason rather than throwing.
    /// </summary>
    /// <param name="description">Names the log in failure messages, e.g. "GPU-loggen".</param>
    public static RollingCsvLog? TryOpen(
        string workingDirectory,
        string fileName,
        string header,
        string description,
        out string? error,
        long maxBytes = DefaultMaxBytes)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(workingDirectory);
            var path = System.IO.Path.Combine(workingDirectory, fileName);

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

            var log = new RollingCsvLog(writer, path, description, Math.Max(maxBytes, 64 * 1024), existingBytes);
            if (existingBytes == 0)
            {
                log.WriteLine(header);
            }

            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Writes one row, joining the fields with commas. Fields must already be escaped.</summary>
    public void AppendRow(params string[] fields)
    {
        WriteLine(string.Join(',', fields));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static string Format(double? value, string format)
    {
        return value?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
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
                Failure ??= $"{_description} nådde sin storleksgräns på {_maxBytes / (1024 * 1024)} MB och stängdes.";
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
                Failure ??= $"{_description} kunde inte skrivas och stängdes: {ex.Message}";
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
