using System.Globalization;
using System.Text.RegularExpressions;

namespace FiveMDiagnostics.Integrations.Obs;

/// <summary>
/// What OBS wrote about itself, read from its own log file after the fact.
/// </summary>
public sealed record ObsSessionLogSummary(
    string LogPath,
    long? LaggedRenderFrames,
    long? TotalOutputFrames,
    long? SkippedEncodingFrames,
    long? TotalEncodedFrames,
    string? OutputName)
{
    public double? RenderLagShare => TotalOutputFrames is > 0 && LaggedRenderFrames is { } lagged
        ? (double)lagged / TotalOutputFrames.Value
        : null;

    public double? EncodingLagShare => TotalEncodedFrames is > 0 && SkippedEncodingFrames is { } skipped
        ? (double)skipped / TotalEncodedFrames.Value
        : null;

    public string Describe()
    {
        var parts = new List<string>();

        if (RenderLagShare is { } render)
        {
            parts.Add($"{LaggedRenderFrames:N0} av {TotalOutputFrames:N0} frames tappades av rendering lag ({render:P1})");
        }

        if (EncodingLagShare is { } encoding)
        {
            parts.Add($"{SkippedEncodingFrames:N0} av {TotalEncodedFrames:N0} frames hoppades över av encoding lag ({encoding:P1})");
        }

        return parts.Count == 0
            ? $"OBS-loggen {Path.GetFileName(LogPath)} innehöll inga avslutningssiffror; sändningen kan ha pågått när sessionen stoppades."
            : $"OBS ({OutputName ?? "utdata"}) enligt egen logg: {string.Join(", ", parts)}.";
    }
}

/// <summary>
/// Reads the render and encoding lag totals out of OBS's own log file.
/// </summary>
/// <remarks>
/// <para>
/// This is a fallback, and it exists because the primary path kept not happening. Four sessions running,
/// the OBS WebSocket was never connected — 116 of 117 incidents recorded "OBS-processen körde men
/// WebSocket var inte ansluten" — so every per-incident render and encoding lag figure was missing.
/// Meanwhile OBS had been writing the session totals to a text file the whole time, and two lines of it
/// answered the question four sessions of setup instructions had not: 0.1% rendering lag, 0.2% encoding
/// lag, over four hours and fifty-one minutes.
/// </para>
/// <para>
/// It is genuinely worse data than the WebSocket supplies. It is one number for a whole session rather
/// than a series that can be lined up against an incident, and it only appears once the output stops, so
/// a session stopped mid-stream reads nothing. It is also enough to rule OBS in or out, which is what
/// the missing telemetry was wanted for.
/// </para>
/// </remarks>
public static class ObsSessionLogReader
{
    /// <summary>
    /// One count, allowing the group separators a localised build may emit.
    /// </summary>
    /// <remarks>
    /// OBS formats these with plain <c>%d</c> in the logs seen so far, so the separators are defensive
    /// rather than observed. They are cheap to allow and expensive to get wrong: a bare <c>\d+</c>
    /// against "1,263" matches the leading "1" and silently reports a render lag of one frame instead of
    /// 1 263, and against "1,900/1,119,903" it fails to match the second group at all and drops the
    /// encoding figure entirely. Separators are stripped again in <see cref="ParseCount"/>.
    /// <para>
    /// A plain ASCII space is deliberately not accepted even though some locales group with one: these
    /// counts are followed by a parenthesised percentage on the same line, and allowing a space would let
    /// the pattern reach across it. The non-breaking and narrow no-break spaces a text formatter actually
    /// emits are safe, because nothing else on the line uses them.
    /// </para>
    /// </remarks>
    private const string CountPattern = @"\d[\d.,\u00A0\u202F']*\d|\d";

    /// <summary>Lines are timestamped <c>HH:mm:ss.fff:</c> and carry the output name in quotes.</summary>
    private static readonly Regex LaggedPattern = new(
        @"Output '(?<output>[^']*)':\s*Number of lagged frames due to rendering lag/stalls:\s*(?<lagged>" + CountPattern + ")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SkippedPattern = new(
        @"number of skipped frames due to encoding lag:\s*(?<skipped>" + CountPattern + @")\s*/\s*(?<total>" + CountPattern + ")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TotalFramesPattern = new(
        @"Output '(?<output>[^']*)':\s*Total frames output:\s*(?<total>" + CountPattern + ")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the newest OBS log that was being written during the session window, or null when OBS logs
    /// nowhere this can find or none of them overlap the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched on write time rather than on the timestamp in the filename: OBS names its logs in local
    /// time and the session is tracked in UTC, and a log still open when the session ended is the one
    /// wanted even though its name says it started hours earlier.
    /// </para>
    /// <para>
    /// Both ends of the window are enforced. Taking the newest log written any time after the session
    /// began would happily return a log from a stream that started the next evening, and report its
    /// figures as this session's. The upper bound carries deliberate slack, because the write that
    /// matters usually lands just after the diagnostics session stops — OBS flushes its totals when an
    /// output stops, which in the session this was built from was 84 seconds later.
    /// </para>
    /// </remarks>
    public static ObsSessionLogSummary? TryReadLatest(DateTimeOffset sessionStart, DateTimeOffset sessionEnd, string? logDirectory = null)
    {
        var directory = logDirectory ?? DefaultLogDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var earliest = sessionStart.UtcDateTime;
        var latest = sessionEnd.UtcDateTime + PostSessionWriteGrace;

        FileInfo? newest = null;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.txt"))
            {
                // A log OBS finished writing before the session began cannot describe it, and one written
                // well after it belongs to a later run.
                if (file.LastWriteTimeUtc < earliest || file.LastWriteTimeUtc > latest)
                {
                    continue;
                }

                if (newest is null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                {
                    newest = file;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return newest is null ? null : TryReadFile(newest.FullName);
    }

    /// <summary>
    /// How long after the session a log may still be written and count as this session's.
    /// </summary>
    /// <remarks>
    /// Generous on purpose: the user stops the diagnostics session, then stops the stream, and OBS writes
    /// its totals at that second. Measured at 84 seconds in the session this was built from. Half an hour
    /// covers a slow wrap-up without reaching the next evening.
    /// </remarks>
    private static readonly TimeSpan PostSessionWriteGrace = TimeSpan.FromMinutes(30);

    /// <summary>Parses one OBS log. Public so a log can be pointed at directly, and for tests.</summary>
    public static ObsSessionLogSummary? TryReadFile(string path)
    {
        string[] lines;
        try
        {
            // OBS keeps the current log open for writing, so it has to be read share-all rather than
            // through the convenience overloads.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        long? lagged = null;
        long? totalOutput = null;
        long? skipped = null;
        long? totalEncoded = null;
        string? outputName = null;

        // Last match wins. A session that streamed and also recorded writes these lines once per output,
        // and the stream is the one that stops last and matters most.
        foreach (var line in lines)
        {
            if (LaggedPattern.Match(line) is { Success: true } laggedMatch)
            {
                lagged = ParseCount(laggedMatch.Groups["lagged"].Value);
                outputName = laggedMatch.Groups["output"].Value;
            }

            if (SkippedPattern.Match(line) is { Success: true } skippedMatch)
            {
                skipped = ParseCount(skippedMatch.Groups["skipped"].Value);
                totalEncoded = ParseCount(skippedMatch.Groups["total"].Value);
            }

            if (TotalFramesPattern.Match(line) is { Success: true } totalMatch)
            {
                totalOutput = ParseCount(totalMatch.Groups["total"].Value);
                outputName ??= totalMatch.Groups["output"].Value;
            }
        }

        if (lagged is null && skipped is null)
        {
            return null;
        }

        return new ObsSessionLogSummary(path, lagged, totalOutput, skipped, totalEncoded, outputName);
    }

    /// <summary>
    /// Reads a count, dropping any group separators the pattern allowed through.
    /// </summary>
    /// <remarks>
    /// Every non-digit is stripped rather than a specific separator being honoured, because the log does
    /// not say which locale wrote it and "1,263" is 1 263 under every convention that could have
    /// produced it — no OBS build writes a fractional frame count.
    /// </remarks>
    private static long? ParseCount(string value)
    {
        Span<char> digits = stackalloc char[value.Length];
        var length = 0;

        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                digits[length++] = character;
            }
        }

        return length > 0
            && long.TryParse(digits[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static string? DefaultLogDirectory()
    {
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "obs-studio", "logs");
    }
}
