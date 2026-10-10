using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FiveMDiagnostics.Export;

/// <summary>One recorded session, identified by its journal.</summary>
/// <param name="StartedAt">When the session started, in UTC, as the journal's name records it.</param>
/// <param name="LastWrittenAt">
/// When the journal was last written, in UTC. For a session that is still running this is its latest
/// line; a journal that has reached its size limit stops moving before the session ends.
/// </param>
/// <param name="NextStartedAt">When the following session started, or null for the newest one.</param>
public sealed record RecordedSession(
    string JournalPath,
    DateTimeOffset StartedAt,
    DateTimeOffset LastWrittenAt,
    DateTimeOffset? NextStartedAt);

/// <summary>Where a session package was written and how large it is.</summary>
/// <param name="Unreadable">Files that belonged to the session but were locked by another program.</param>
public sealed record SessionPackage(string ZipPath, long Bytes, int Files, int TracesLeftOut, IReadOnlyList<string> Unreadable);

/// <summary>
/// Packages everything one session left in the working directory into a single zip that can be handed
/// to someone else.
/// </summary>
/// <remarks>
/// The working directory is flat and shared by every session. A session opens its journal before any
/// other file, so a file belongs to the session whose journal was the last one opened before the file
/// was created. Only the session's own outputs are taken, by name: the directory also holds state files
/// that carry over between sessions and say nothing on their own.
/// </remarks>
public static class SessionShareExporter
{
    private const string JournalPrefix = "session_";

    /// <summary>Prefixes of the files a session writes, besides its journal.</summary>
    private static readonly string[] SessionFilePrefixes =
        ["presentmon_", "gpu_", "gpuprocs_", "obsstream_", "app-crash_", "CitizenFX_log_"];

    private const string TracePrefix = "deep_";

    private const string Redacted = "[redacted]";

    private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);

    /// <summary>
    /// Full eight-group addresses, and compressed ones with a leading hex group before <c>::</c>.
    /// Deliberately narrow: clock times and C++ scope names in the client log also contain colons.
    /// </summary>
    private static readonly Regex Ipv6 = new(
        @"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b|\b(?:[0-9a-fA-F]{1,4}:){1,6}:(?:[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{1,4})*)?\b",
        RegexOptions.Compiled);

    /// <summary>Sessions with a journal in <paramref name="workingDirectory"/>, newest first.</summary>
    public static IReadOnlyList<RecordedSession> FindSessions(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return [];
        }

        var journals = new DirectoryInfo(workingDirectory)
            .EnumerateFiles($"{JournalPrefix}*.jsonl")
            .Select(file => (File: file, Started: TryParseStart(file.Name)))
            .Where(item => item.Started is not null)
            .OrderBy(item => item.Started)
            .ToArray();

        var sessions = new List<RecordedSession>(journals.Length);
        for (var index = 0; index < journals.Length; index++)
        {
            var (file, started) = journals[index];
            sessions.Add(new RecordedSession(
                file.FullName,
                started!.Value,
                LastWriteOf(file),
                index + 1 < journals.Length ? journals[index + 1].Started : null));
        }

        sessions.Reverse();
        return sessions;
    }

    /// <summary>
    /// Writes the session's files into a new zip in <paramref name="exportDirectory"/>. Nothing is left
    /// behind when it fails.
    /// </summary>
    /// <param name="includeTraces">Whether the deep-capture traces go in too. They are close to a gigabyte each.</param>
    /// <param name="redact">
    /// Replaces IP addresses, <paramref name="extraSensitive"/> and the Windows account name where it
    /// appears as a path or account segment, in every text file. Binary traces cannot be redacted and
    /// are only included when asked for.
    /// </param>
    /// <param name="extraSensitive">Further strings to replace, such as a server host name or <c>host:port</c>.</param>
    public static async Task<SessionPackage> ExportAsync(
        RecordedSession session,
        string exportDirectory,
        bool includeTraces,
        bool redact,
        IReadOnlyCollection<string> extraSensitive,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(session.JournalPath)!;
        var from = session.StartedAt.UtcDateTime;
        var to = session.NextStartedAt?.UtcDateTime ?? DateTime.MaxValue;

        var candidates = new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(file => file.CreationTimeUtc >= from && file.CreationTimeUtc < to)
            .ToArray();

        var files = candidates
            .Where(file => SessionFilePrefixes.Any(prefix => file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Prepend(new FileInfo(session.JournalPath))
            .ToList();

        var traces = candidates
            .Where(file => file.Name.StartsWith(TracePrefix, StringComparison.OrdinalIgnoreCase)
                && file.Extension.Equals(".etl", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (includeTraces)
        {
            files.AddRange(traces);
        }

        Directory.CreateDirectory(exportDirectory);
        var zipPath = UniqueZipPath(exportDirectory, session.StartedAt.ToLocalTime());
        var redaction = redact ? Redaction.Create(extraSensitive) : null;
        var unreadable = new List<string>();

        try
        {
            await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The running session is still writing its own files, and the game may hold its log.
                FileStream source;
                try
                {
                    source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException)
                {
                    unreadable.Add(file.Name);
                    continue;
                }

                await using var _ = source;
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                await using var target = entry.Open();

                if (redaction is not null && IsText(file))
                {
                    // Line by line: a frame log from a long evening is hundreds of megabytes.
                    using var reader = new StreamReader(source, Encoding.UTF8);
                    await using var writer = new StreamWriter(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                    {
                        await writer.WriteLineAsync(redaction.Apply(line)).ConfigureAwait(false);
                    }
                }
                else
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }

        return new SessionPackage(
            zipPath,
            new FileInfo(zipPath).Length,
            files.Count - unreadable.Count,
            includeTraces ? 0 : traces.Length,
            unreadable);
    }

    /// <summary>
    /// The journal's real last write. Windows does not update the directory entry while a handle is
    /// open, so the enumerated value of a running session's journal is the moment it was opened.
    /// </summary>
    private static DateTimeOffset LastWriteOf(FileInfo file)
    {
        try
        {
            file.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
    }

    /// <summary>A name that does not replace an earlier package, which may already have been sent.</summary>
    private static string UniqueZipPath(string exportDirectory, DateTimeOffset localStart)
    {
        var stem = $"FiveMDiagnostics_{localStart:yyyy-MM-dd_HHmmss}";
        var path = Path.Combine(exportDirectory, stem + ".zip");
        for (var copy = 2; File.Exists(path); copy++)
        {
            path = Path.Combine(exportDirectory, $"{stem}_{copy}.zip");
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static DateTimeOffset? TryParseStart(string fileName)
    {
        var stamp = Path.GetFileNameWithoutExtension(fileName)[JournalPrefix.Length..];
        return DateTimeOffset.TryParseExact(
            stamp,
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var started)
            ? started
            : null;
    }

    private static bool IsText(FileInfo file) =>
        file.Extension.ToLowerInvariant() is ".jsonl" or ".csv" or ".txt" or ".log";

    private sealed class Redaction
    {
        private readonly Regex? _account;
        private readonly string[] _values;

        private Redaction(Regex? account, string[] values)
        {
            _account = account;
            _values = values;
        }

        public static Redaction Create(IReadOnlyCollection<string> extra)
        {
            // A host given as host:port also appears on its own.
            var values = extra
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => value.Count(c => c == ':') == 1 ? [value, value[..value.IndexOf(':')]] : new[] { value })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Length) // longest first, so a shorter overlap cannot split it
                .ToArray();

            return new Redaction(AccountPattern(), values);
        }

        public string Apply(string text)
        {
            text = Ipv4.Replace(text, Redacted);
            text = Ipv6.Replace(text, Redacted);
            if (_account is not null)
            {
                text = _account.Replace(text, Redacted);
            }

            foreach (var value in _values)
            {
                text = text.Replace(value, Redacted, StringComparison.OrdinalIgnoreCase);
            }

            return text;
        }

        /// <summary>
        /// The account and profile-folder names, matched only as a segment after a slash or backslash —
        /// <c>Users\name</c>, <c>C:/Users/name</c>, <c>PC-NAME\name</c> — and ending at a separator. The
        /// bare name can be a common word that appears elsewhere in a log.
        /// </summary>
        private static Regex? AccountPattern()
        {
            var names = new[]
                {
                    Environment.UserName,
                    Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => name.Length)
                .Select(Regex.Escape)
                .ToArray();

            return names.Length == 0
                ? null
                : new Regex(
                    $@"(?<=[\\/])(?:{string.Join('|', names)})(?=$|[\\/""\s,;:'])",
                    RegexOptions.IgnoreCase);
        }
    }
}
