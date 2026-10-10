namespace FiveMDiagnostics.Tests;

using System.IO.Compression;

using FiveMDiagnostics.Export;

/// <summary>
/// The zip a player hands over: one session's own files, from a working directory that every session
/// shares, without the traces unless asked and without addresses unless allowed.
/// </summary>
public sealed class SessionShareExporterTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = Started.AddHours(3);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));
    private string Sessions => Path.Combine(_root, "Sessions");
    private string Exports => Path.Combine(_root, "Exports");

    public SessionShareExporterTests()
    {
        Directory.CreateDirectory(Sessions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SessionsAreFoundByTheirJournalNewestFirstEachBoundedByTheNext()
    {
        Write("session_20261009_180000.jsonl", "{}", Started, Ended);
        Write("session_20261008_180000.jsonl", "{}", Started.AddDays(-1), Ended.AddDays(-1));

        var sessions = SessionShareExporter.FindSessions(Sessions);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(Started, sessions[0].StartedAt);
        Assert.Equal(Ended, sessions[0].LastWrittenAt);
        Assert.Null(sessions[0].NextStartedAt);
        Assert.Equal(Started, sessions[1].NextStartedAt);
    }

    [Fact]
    public async Task OnlyThisSessionsOwnFilesGoInAndTracesStayOut()
    {
        Write("session_20261009_180000.jsonl", "{}", Started, Ended);
        Write("presentmon_1234_20261009_180010.csv", "x", Started.AddSeconds(10), Ended);
        Write("CitizenFX_log_2026-10-09T180000.log", "x", Ended.AddMinutes(1), Ended);
        Write("deep_20261009_190000_abc.etl", "x", Started.AddHours(1), Started.AddHours(1));
        Write("last-session-processes.txt", "x", Ended, Ended);
        Write("presentmon_9999_20261008_180010.csv", "x", Started.AddDays(-1), Ended.AddDays(-1));

        var package = await Export(includeTraces: false);

        Assert.Equal(
            ["CitizenFX_log_2026-10-09T180000.log", "presentmon_1234_20261009_180010.csv", "session_20261009_180000.jsonl"],
            EntryNames(package.ZipPath));
        Assert.Equal(1, package.TracesLeftOut);
    }

    /// <summary>
    /// A relaunch soon after the previous session ended: each file goes to the session that was open when
    /// it was created, and to no other.
    /// </summary>
    [Fact]
    public async Task SessionsMinutesApartDoNotShareFiles()
    {
        var second = Started.AddHours(1).AddMinutes(3);
        Write("session_20261009_180000.jsonl", "{}", Started, Started.AddHours(1));
        Write("gpu_20261009_180001.csv", "x", Started.AddSeconds(1), Started.AddHours(1));
        Write("session_20261009_190300.jsonl", "{}", second, second.AddHours(1));
        Write("gpu_20261009_190301.csv", "x", second.AddSeconds(1), second.AddHours(1));

        var sessions = SessionShareExporter.FindSessions(Sessions);
        var newer = await SessionShareExporter.ExportAsync(sessions[0], Exports, false, true, [], CancellationToken.None);
        var older = await SessionShareExporter.ExportAsync(sessions[1], Exports, false, true, [], CancellationToken.None);

        Assert.Equal(["gpu_20261009_190301.csv", "session_20261009_190300.jsonl"], EntryNames(newer.ZipPath));
        Assert.Equal(["gpu_20261009_180001.csv", "session_20261009_180000.jsonl"], EntryNames(older.ZipPath));
    }

    /// <summary>
    /// A running session's journal can report a write time hours behind; a file the session created
    /// after that still belongs to it.
    /// </summary>
    [Fact]
    public async Task TheNewestSessionTakesFilesCreatedAfterItsJournalWasLastWritten()
    {
        Write("session_20261009_180000.jsonl", "{}", Started, Started);
        Write("presentmon_4321_20261009_200000.csv", "x", Started.AddHours(2), Started.AddHours(2));

        var package = await Export(includeTraces: false);

        Assert.Contains("presentmon_4321_20261009_200000.csv", EntryNames(package.ZipPath));
    }

    [Fact]
    public async Task TracesGoInWhenAskedFor()
    {
        Write("session_20261009_180000.jsonl", "{}", Started, Ended);
        Write("deep_20261009_190000_abc.etl", "x", Started.AddHours(1), Started.AddHours(1));

        var package = await Export(includeTraces: true);

        Assert.Contains("deep_20261009_190000_abc.etl", EntryNames(package.ZipPath));
        Assert.Equal(0, package.TracesLeftOut);
    }

    [Fact]
    public async Task AddressesHostsAndTheAccountNameAreRedacted()
    {
        var user = Environment.UserName;
        Write(
            "session_20261009_180000.jsonl",
            $"{{\"message\":\"Ansluten till 185.12.34.56:30120 och 2001:db8:85a3::8a2e:370:7334 via play.example.se, logg i C:\\\\Users\\\\{user}\\\\AppData och C:/Users/{user}/x\"}}",
            Started,
            Ended);

        var package = await Export(includeTraces: false, extra: ["play.example.se:30120"]);

        var text = ReadEntry(package.ZipPath, "session_20261009_180000.jsonl");
        Assert.DoesNotContain("185.12.34.56", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8", text, StringComparison.Ordinal);
        Assert.DoesNotContain("play.example.se", text, StringComparison.Ordinal);
        Assert.DoesNotContain(user, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Users\\\\[redacted]\\\\AppData", text, StringComparison.Ordinal);
        Assert.Contains("C:/Users/[redacted]/x", text, StringComparison.Ordinal);
    }

    /// <summary>The account segment ends at the next separator, so the rest of a CSV row survives.</summary>
    [Fact]
    public async Task RedactionLeavesTheRestOfALineAndClockTimesAlone()
    {
        Write("session_20261009_180000.jsonl", $"1,C:\\Users\\{Environment.UserName},next,20:01:23.338", Started, Ended);

        var package = await Export(includeTraces: false);

        Assert.Equal("1,C:\\Users\\[redacted],next,20:01:23.338", ReadEntry(package.ZipPath, "session_20261009_180000.jsonl"));
    }

    [Fact]
    public async Task NothingIsRedactedWhenSensitiveDetailsAreAllowed()
    {
        Write("session_20261009_180000.jsonl", "185.12.34.56", Started, Ended);

        var session = SessionShareExporter.FindSessions(Sessions).Single();
        var package = await SessionShareExporter.ExportAsync(session, Exports, includeTraces: false, redact: false, [], CancellationToken.None);

        Assert.Equal("185.12.34.56", ReadEntry(package.ZipPath, "session_20261009_180000.jsonl"));
    }

    /// <summary>An earlier package may already have been sent, so exporting again writes a new file.</summary>
    [Fact]
    public async Task ExportingTwiceKeepsTheFirstPackage()
    {
        Write("session_20261009_180000.jsonl", "{}", Started, Ended);

        var first = await Export(includeTraces: false);
        var second = await Export(includeTraces: false);

        Assert.NotEqual(first.ZipPath, second.ZipPath);
        Assert.True(File.Exists(first.ZipPath));
    }

    private Task<SessionPackage> Export(bool includeTraces, IReadOnlyCollection<string>? extra = null)
    {
        var session = SessionShareExporter.FindSessions(Sessions).First();
        return SessionShareExporter.ExportAsync(session, Exports, includeTraces, redact: true, extra ?? [], CancellationToken.None);
    }

    private void Write(string name, string content, DateTimeOffset created, DateTimeOffset written)
    {
        var path = Path.Combine(Sessions, name);
        File.WriteAllText(path, content);
        File.SetCreationTimeUtc(path, created.UtcDateTime);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
    }

    private static string[] EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private static string ReadEntry(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }
}
