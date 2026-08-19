using System.Text.Json;

namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Analysis;
using FiveMDiagnostics.Collectors;
using FiveMDiagnostics.Core;
using FiveMDiagnostics.Fakes;

/// <summary>
/// A six hour session used to leave nothing on disk unless the user selected an incident and exported
/// it by hand, so every incident it auto marked — and every status entry explaining why it marked so
/// few — died with the window.
/// </summary>
public sealed class SessionJournalTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(
        Path.GetTempPath(),
        "FiveMDiagnosticsTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void Journal_WritesOneJsonObjectPerLine()
    {
        var startedAt = new DateTimeOffset(2026, 8, 18, 19, 31, 16, TimeSpan.Zero);
        var settings = CreateSettings();

        using (var journal = OpenJournal(startedAt))
        {
            Assert.NotNull(journal);
            journal!.WriteSessionStart(CreateEnvironment(), settings, startedAt);
            journal.WriteStatus(new DiagnosticStatusEntry(startedAt, StatusLevel.Warning, "PresentMon", "PresentMon avslutades av sig självt."));
            journal.WriteSessionEnd(startedAt.AddHours(6));
        }

        var lines = ReadJournalLines(startedAt);

        Assert.Equal(["session-start", "status", "session-end"], lines.Select(line => line.GetProperty("type").GetString()));
        Assert.Equal("PresentMon avslutades av sig självt.", lines[1].GetProperty("payload").GetProperty("message").GetString());
        Assert.Equal("Warning", lines[1].GetProperty("payload").GetProperty("level").GetString());
    }

    /// <summary>
    /// The event counts are the part that distinguishes a quiet incident from a dead PresentMon
    /// capture, which is the failure the journal was written to make visible after the fact.
    /// </summary>
    [Fact]
    public void Incident_IsWrittenWithItsAnalysisAndEventCounts()
    {
        var startedAt = new DateTimeOffset(2026, 8, 18, 19, 31, 16, TimeSpan.Zero);
        var scenario = FakeScenarioGenerator.Create(FakeScenarioKind.ObsGpuContention).ToIncidentRecord();
        var incident = scenario with { Analysis = new FiveMCorrelationEngine().Analyze(scenario) };

        using (var journal = OpenJournal(startedAt))
        {
            journal!.WriteIncident(incident);
            Assert.Equal(1, journal.IncidentCount);
        }

        var payload = Assert.Single(ReadJournalLines(startedAt)).GetProperty("payload");

        Assert.Equal(incident.Marker.Label, payload.GetProperty("label").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("summary").GetString()));
        Assert.NotEmpty(payload.GetProperty("hypotheses").EnumerateArray());

        var eventCounts = payload.GetProperty("eventCounts");
        Assert.Contains(eventCounts.EnumerateObject(), item => item.Value.GetInt32() > 0);

        // The whole 90 second window must stay out: it is thousands of samples per incident, and this
        // file is appended to for as long as a stream runs.
        Assert.False(payload.TryGetProperty("events", out _));
    }

    [Fact]
    public void Journal_StopsAtItsSizeBudgetAndSaysSo()
    {
        var startedAt = new DateTimeOffset(2026, 8, 18, 19, 31, 16, TimeSpan.Zero);
        var entry = new DiagnosticStatusEntry(startedAt, StatusLevel.Info, "Collector", new string('x', 512));

        using (var journal = SessionJournal.TryOpen(_workingDirectory, startedAt, out _, maxBytes: SessionJournal.MinimumMaxBytes))
        {
            Assert.NotNull(journal);

            for (var index = 0; index < 10_000; index++)
            {
                journal!.WriteStatus(entry);
            }

            Assert.False(journal!.IsOpen);
            Assert.True(journal.TryTakeFailure(out var failure));
            Assert.Contains("storleksgräns", failure);
        }

        var lines = ReadJournalLines(startedAt);

        Assert.Equal("journal-truncated", lines[^1].GetProperty("type").GetString());
        Assert.True(new FileInfo(JournalPath(startedAt)).Length <= SessionJournal.MinimumMaxBytes);
    }

    /// <summary>
    /// The truncation line is part of the file, so it has to be part of the budget. Filling the file to
    /// exactly one line short of the limit leaves room for that last line and nothing else: writing both
    /// puts the file over the very limit it is announcing.
    /// </summary>
    [Fact]
    public void Journal_KeepsRoomForItsOwnTruncationLine()
    {
        var startedAt = new DateTimeOffset(2026, 8, 18, 19, 31, 16, TimeSpan.Zero);
        var entry = new DiagnosticStatusEntry(startedAt, StatusLevel.Info, "Collector", "en helt vanlig rad");

        // Measured rather than assumed: the line's length depends on how the payload is serialized.
        var probeStartedAt = startedAt.AddSeconds(1);
        using (var probe = OpenJournal(probeStartedAt))
        {
            probe!.WriteStatus(entry);
        }

        var lineLength = new FileInfo(JournalPath(probeStartedAt)).Length;
        WriteFiller(JournalPath(startedAt), SessionJournal.MinimumMaxBytes - lineLength);

        using (var journal = SessionJournal.TryOpen(_workingDirectory, startedAt, out _, maxBytes: SessionJournal.MinimumMaxBytes))
        {
            journal!.WriteStatus(entry);
            Assert.False(journal.IsOpen);
        }

        var lines = ReadJournalLines(startedAt);

        Assert.Equal("journal-truncated", lines[^1].GetProperty("type").GetString());
        Assert.True(
            new FileInfo(JournalPath(startedAt)).Length <= SessionJournal.MinimumMaxBytes,
            $"Journalen växte till {new FileInfo(JournalPath(startedAt)).Length} byte mot gränsen {SessionJournal.MinimumMaxBytes}.");
    }

    /// <summary>
    /// The budget covers the file, and the file can already exist: two sessions inside the same second
    /// share one. A session that starts counting from zero grows it to twice the limit.
    /// </summary>
    [Fact]
    public void Journal_CountsTheBytesTheFileAlreadyHeld()
    {
        var startedAt = new DateTimeOffset(2026, 8, 18, 19, 31, 16, TimeSpan.Zero);
        WriteFiller(JournalPath(startedAt), SessionJournal.MinimumMaxBytes - 32);
        var lengthBeforeSession = new FileInfo(JournalPath(startedAt)).Length;

        using (var journal = SessionJournal.TryOpen(_workingDirectory, startedAt, out _, maxBytes: SessionJournal.MinimumMaxBytes))
        {
            journal!.WriteStatus(new DiagnosticStatusEntry(startedAt, StatusLevel.Info, "Collector", "för sent"));

            Assert.False(journal.IsOpen);
            Assert.True(journal.TryTakeFailure(out var failure));
            Assert.Contains("storleksgräns", failure);

            // The budget is 64 kB here, and a warning that reads "0 MB" tells the user nothing.
            Assert.DoesNotContain("0 MB", failure);
        }

        // Not even the truncation line fits in the 32 bytes left, and announcing the limit by exceeding
        // it would be worse than leaving it unannounced.
        Assert.Equal(lengthBeforeSession, new FileInfo(JournalPath(startedAt)).Length);
    }

    /// <summary>Two sessions inside the same second must not erase each other's evidence.</summary>
    [Fact]
    public void SecondSessionInTheSameSecond_AppendsInsteadOfOverwriting()
    {
        var startedAt = new DateTimeOffset(2026, 8, 18, 19, 31, 16, TimeSpan.Zero);

        using (var first = OpenJournal(startedAt))
        {
            first!.WriteStatus(new DiagnosticStatusEntry(startedAt, StatusLevel.Info, "First", "ett"));
        }

        using (var second = OpenJournal(startedAt))
        {
            second!.WriteStatus(new DiagnosticStatusEntry(startedAt, StatusLevel.Info, "Second", "två"));
        }

        var lines = ReadJournalLines(startedAt);

        Assert.Equal(["First", "Second"], lines.Select(line => line.GetProperty("payload").GetProperty("source").GetString()));
    }

    [Fact]
    public void UnwritableWorkingDirectory_ReportsInsteadOfThrowing()
    {
        var filePath = Path.Combine(_workingDirectory, "not-a-directory");
        Directory.CreateDirectory(_workingDirectory);
        File.WriteAllText(filePath, "occupied");

        var journal = SessionJournal.TryOpen(filePath, DateTimeOffset.UtcNow, out var error);

        Assert.Null(journal);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// The end to end case: an incident completed by a running session has to be on disk without anyone
    /// selecting it or pressing export.
    /// </summary>
    [Fact]
    public async Task SessionManager_WritesStatusAndIncidentsWithoutAnExport()
    {
        var settings = CreateSettings();

        // The materializer finalizes an incident once its post window has passed, and the finalize loop
        // ticks once a second, so a zero length window keeps the test to roughly one tick.
        settings.PreIncidentWindow = TimeSpan.FromSeconds(1);
        settings.PostIncidentWindow = TimeSpan.Zero;

        var completed = new TaskCompletionSource<IncidentRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var manager = CreateManager(settings))
        {
            manager.IncidentCompleted += (_, incident) => completed.TrySetResult(incident);

            await manager.StartSessionAsync();
            manager.Report(StatusLevel.Info, "Test", "kollektorn startade");
            Assert.NotNull(manager.MarkIncident(IncidentSeverity.Severe));

            var incident = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(IncidentSeverity.Severe, incident.Marker.Severity);

            await manager.StopSessionAsync();
        }

        var lines = ReadJournalLines();
        var types = lines.Select(line => line.GetProperty("type").GetString()!).ToArray();

        Assert.Equal("session-start", types[0]);
        Assert.Equal("session-end", types[^1]);
        Assert.Contains("incident", types);
        Assert.Contains(lines, line => line.GetProperty("payload").TryGetProperty("message", out var message)
            && message.GetString() == "kollektorn startade");
        Assert.Equal(1, lines[^1].GetProperty("payload").GetProperty("incidentCount").GetInt32());
    }

    /// <summary>
    /// Importing an artifact re-runs the analysis of the most recent incident, and that conclusion —
    /// the one drawn with the evidence the user went and fetched — is usually the one worth keeping.
    /// </summary>
    [Fact]
    public async Task ImportedArtifact_WritesTheUpdatedIncidentToTheJournal()
    {
        var settings = CreateSettings();
        settings.PreIncidentWindow = TimeSpan.FromSeconds(1);
        settings.PostIncidentWindow = TimeSpan.Zero;

        var completed = new TaskCompletionSource<IncidentRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var artifactPath = Path.Combine(_workingDirectory, "net_stats.log");

        await using (var manager = CreateManager(settings))
        {
            manager.IncidentCompleted += (_, incident) => completed.TrySetResult(incident);

            await manager.StartSessionAsync();
            Assert.NotNull(manager.MarkIncident(IncidentSeverity.Severe));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // No parser handles this extension, so it is attached as manual supporting evidence — the
            // same path any unrecognised artifact takes.
            await manager.ImportArtifactsAsync([artifactPath]);
            await manager.StopSessionAsync();
        }

        var updates = ReadJournalLines()
            .Where(line => line.GetProperty("type").GetString() == "incident-update")
            .ToArray();

        var payload = Assert.Single(updates).GetProperty("payload");
        Assert.Contains(
            payload.GetProperty("attachments").EnumerateArray(),
            item => item.GetString() == Path.GetFileName(artifactPath));

        // The update is an extra line, not a second incident: the closing total counts incidents.
        Assert.Equal(1, ReadJournalLines()[^1].GetProperty("payload").GetProperty("incidentCount").GetInt32());
    }

    /// <summary>
    /// Deep capture runs detached from the marking call and reports its outcome when it finishes, which
    /// can be long after the user pressed stop. Closing the journal without waiting for it drops the one
    /// status entry that says whether the ETL trace the incident refers to actually exists.
    /// </summary>
    [Fact]
    public async Task DeepCaptureFinishingAfterStop_StillReachesTheJournal()
    {
        var settings = CreateSettings();
        settings.DeepCapture.Enabled = true;

        var deepCapture = new GatedDeepCaptureService("Deep capture sparad.");

        await using (var manager = CreateManager(settings, deepCapture))
        {
            await manager.StartSessionAsync();
            Assert.NotNull(manager.MarkIncident(IncidentSeverity.Severe));
            await deepCapture.Started.WaitAsync(TimeSpan.FromSeconds(30));

            // Released only once the stop is already under way, which is exactly the ordering that used
            // to lose the entry.
            var stopping = manager.StopSessionAsync();
            _ = Task.Delay(TimeSpan.FromMilliseconds(150)).ContinueWith(_ => deepCapture.Release());
            await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var lines = ReadJournalLines();
        var messages = lines
            .Select(line => line.GetProperty("payload").TryGetProperty("message", out var message) ? message.GetString() : null)
            .ToArray();

        Assert.Contains("Deep capture sparad.", messages);
        Assert.Equal("session-end", lines[^1].GetProperty("type").GetString());
    }

    /// <summary>Fills a journal file with one padded line of exactly <paramref name="totalBytes"/> bytes.</summary>
    private void WriteFiller(string path, long totalBytes)
    {
        Directory.CreateDirectory(_workingDirectory);

        const string prefix = "{\"type\":\"filler\",\"payload\":\"";
        const string suffix = "\"}";
        var padding = (int)(totalBytes - System.Environment.NewLine.Length - prefix.Length - suffix.Length);

        // All ASCII, so one character is one byte and the file lands on the requested size exactly.
        File.WriteAllText(path, prefix + new string('x', padding) + suffix + System.Environment.NewLine);
    }

    private static EnvironmentMetadata CreateEnvironment()
        => new("Windows 11", "CPU", 16UL * 1024 * 1024 * 1024, "GPU", null, 60, null, false, "Profile", DateTimeOffset.UtcNow, null);

    private SessionJournal? OpenJournal(DateTimeOffset startedAt)
        => SessionJournal.TryOpen(_workingDirectory, startedAt, out _);

    private DiagnosticsSettings CreateSettings()
    {
        var settings = DiagnosticsSettings.CreateDefault();
        settings.WorkingDirectory = _workingDirectory;
        settings.ExportDirectory = Path.Combine(_workingDirectory, "Exports");
        settings.DeepCapture.Enabled = false;
        return settings;
    }

    private DiagnosticsSessionManager CreateManager(DiagnosticsSettings settings, IDeepCaptureService? deepCaptureService = null)
    {
        return new DiagnosticsSessionManager(
            settings,
            new StubEnvironmentMetadataProvider(),
            new FiveMCorrelationEngine(),
            new StubIncidentExporter(),
            deepCaptureService ?? new StubDeepCaptureService(),
            collectors: [],
            artifactParsers: [],
            new StubProcessResolver());
    }

    private string JournalPath(DateTimeOffset startedAt)
        => Path.Combine(_workingDirectory, $"session_{startedAt:yyyyMMdd_HHmmss}.jsonl");

    private IReadOnlyList<JsonElement> ReadJournalLines(DateTimeOffset? startedAt = null)
    {
        var path = startedAt is { } timestamp
            ? JournalPath(timestamp)
            : Directory.GetFiles(_workingDirectory, "session_*.jsonl").Single();

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
    }

    private sealed class StubEnvironmentMetadataProvider : IEnvironmentMetadataProvider
    {
        public Task<EnvironmentMetadata> CollectAsync(DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(CreateEnvironment() with { ServerProfileName = settings.ServerProfile.Name });
    }

    private sealed class StubIncidentExporter : IIncidentExporter
    {
        public Task<string> ExportAsync(IncidentRecord incident, ExportBundleOptions options, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
    }

    private sealed class StubDeepCaptureService : IDeepCaptureService
    {
        public Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new DeepCaptureResult(false, false, "stub"));
    }

    /// <summary>A deep capture the test decides when to finish, so the race with stop is not a race.</summary>
    private sealed class GatedDeepCaptureService(string message) : IDeepCaptureService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<DeepCaptureResult> CaptureAsync(IncidentMarker marker, DiagnosticsSettings settings, CancellationToken cancellationToken)
        {
            _started.TrySetResult();

            // Deliberately not observing the token: a WPR trace that is already recording still has to be
            // stopped and written out, so the work outlives the session it belongs to.
            await _release.Task.ConfigureAwait(false);
            return new DeepCaptureResult(true, false, message);
        }
    }

    private sealed class StubProcessResolver : ITargetProcessResolver
    {
        public TargetProcessInfo? TryGetTargetProcess() => null;
    }
}
