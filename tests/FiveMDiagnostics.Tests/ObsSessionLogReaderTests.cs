namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.Obs;

/// <summary>
/// The OBS log is the fallback for telemetry the WebSocket has not delivered in four sessions, so the
/// cases that matter are "reads the right output" and "says nothing rather than something wrong".
/// </summary>
public sealed class ObsSessionLogReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        foreach (var directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Shaped after a real log: a couple of file recordings early in the evening, then the stream, which
    /// is the output whose totals actually describe the session.
    /// </summary>
    private string WriteLog(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"obs-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
        _tempFiles.Add(path);
        return path;
    }

    private const string RealisticLog = """
        21:03:59.388: CPU Name: AMD Ryzen 7 5700X 8-Core Processor
        21:03:59.389: Physical Cores: 8, Logical Cores: 16
        21:04:01.148: OBS 32.2.2 (64-bit, windows)
        21:04:05.681: [obs-browser]: Version 2.26.9
        21:05:05.428: Output 'adv_file_output': stopping
        21:05:05.429: Output 'adv_file_output': Total frames output: 3600
        21:05:05.429: Output 'adv_file_output': Number of lagged frames due to rendering lag/stalls: 12 (0.3%)
        21:05:05.430: Video stopped, number of skipped frames due to encoding lag: 4/3600 (0.1%)
        21:33:42.376: [rtmp stream: 'rtmp multitrack video'] Connection successful
        02:44:47.453: Output 'rtmp multitrack video': stopping
        02:44:47.453: Output 'rtmp multitrack video': Total frames output: 1119891
        02:44:47.454: Output 'rtmp multitrack video': Number of lagged frames due to rendering lag/stalls: 1263 (0.1%)
        02:44:47.454: Video stopped, number of skipped frames due to encoding lag: 1900/1119903 (0.2%)
        """;

    [Fact]
    public void ReadsTheStreamTotalsFromARealisticLog()
    {
        var summary = ObsSessionLogReader.TryReadFile(WriteLog(RealisticLog));

        Assert.NotNull(summary);
        Assert.Equal(1263, summary!.LaggedRenderFrames);
        Assert.Equal(1119891, summary.TotalOutputFrames);
        Assert.Equal(1900, summary.SkippedEncodingFrames);
        Assert.Equal(1119903, summary.TotalEncodedFrames);

        // The stream, not the file recording that stopped five hours earlier.
        Assert.Equal("rtmp multitrack video", summary.OutputName);
    }

    [Fact]
    public void ComputesTheSharesThatDecideWhetherObsIsAtFault()
    {
        var summary = ObsSessionLogReader.TryReadFile(WriteLog(RealisticLog));

        Assert.NotNull(summary);
        Assert.Equal(0.0011, summary!.RenderLagShare!.Value, 4);
        Assert.Equal(0.0017, summary.EncodingLagShare!.Value, 4);

        // 0.1% and 0.2% is what "OBS is not the problem" looks like, and it is the sentence the report
        // has been missing for four sessions.
        Assert.Contains("rendering lag", summary.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare <c>\d+</c> reads "1,263" as 1 and fails on "1,900/1,119,903" outright, so a localised
    /// build would have reported a render lag of one frame and no encoding figure at all.
    /// </summary>
    [Fact]
    public void GroupSeparatorsDoNotTruncateTheCounts()
    {
        var summary = ObsSessionLogReader.TryReadFile(WriteLog("""
            21:04:01.148: OBS 32.2.2 (64-bit, windows)
            02:44:47.453: Output 'rtmp multitrack video': Total frames output: 1,119,891
            02:44:47.454: Output 'rtmp multitrack video': Number of lagged frames due to rendering lag/stalls: 1,263 (0.1%)
            02:44:47.454: Video stopped, number of skipped frames due to encoding lag: 1,900/1,119,903 (0.2%)
            """));

        Assert.NotNull(summary);
        Assert.Equal(1263, summary!.LaggedRenderFrames);
        Assert.Equal(1119891, summary.TotalOutputFrames);
        Assert.Equal(1900, summary.SkippedEncodingFrames);
        Assert.Equal(1119903, summary.TotalEncodedFrames);
    }

    /// <summary>
    /// The counts are followed by a parenthesised percentage, so a separator set that included a plain
    /// space could reach across it. Zero has to survive too — it is the reading that says OBS was fine.
    /// </summary>
    [Fact]
    public void TheTrailingPercentageIsNotSwallowedAndZeroSurvives()
    {
        var summary = ObsSessionLogReader.TryReadFile(WriteLog("""
            21:04:01.148: OBS 32.2.2 (64-bit, windows)
            02:44:47.453: Output 'rtmp multitrack video': Total frames output: 1119891
            02:44:47.454: Output 'rtmp multitrack video': Number of lagged frames due to rendering lag/stalls: 0 (0.0%)
            02:44:47.454: Video stopped, number of skipped frames due to encoding lag: 0/1119903 (0.0%)
            """));

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.LaggedRenderFrames);
        Assert.Equal(1119891, summary.TotalOutputFrames);
        Assert.Equal(0, summary.SkippedEncodingFrames);
        Assert.Equal(0d, summary.RenderLagShare);
    }

    /// <summary>
    /// The normal shape of a log from a stream that is still running. Reporting the earlier recording's
    /// totals as the session's would be worse than reporting nothing.
    /// </summary>
    [Fact]
    public void ALogWithoutTotalsYieldsNothing()
    {
        var summary = ObsSessionLogReader.TryReadFile(WriteLog("""
            21:04:01.148: OBS 32.2.2 (64-bit, windows)
            21:33:42.376: [rtmp stream: 'rtmp multitrack video'] Connection successful
            """));

        Assert.Null(summary);
    }

    /// <summary>
    /// The upper bound of the session window, which was previously discarded outright.
    /// </summary>
    /// <remarks>
    /// Taking the newest log written any time after the session began would return tomorrow's stream and
    /// report its render and encoding lag as tonight's. The bound carries slack because OBS writes its
    /// totals when an output stops, which lands shortly after the diagnostics session does.
    /// </remarks>
    [Fact]
    public void ALogFromALaterSessionIsNotMistakenForThisOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"obs-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);

        var sessionStart = new DateTimeOffset(2026, 8, 22, 19, 0, 0, TimeSpan.Zero);
        var sessionEnd = new DateTimeOffset(2026, 8, 23, 0, 43, 0, TimeSpan.Zero);

        var mine = Path.Combine(directory, "mine.txt");
        File.WriteAllText(mine, RealisticLog);
        File.SetLastWriteTimeUtc(mine, sessionEnd.UtcDateTime.AddSeconds(84));

        // The next evening's stream, in the same folder and newer.
        var later = Path.Combine(directory, "later.txt");
        File.WriteAllText(later, RealisticLog.Replace("1263", "999999"));
        File.SetLastWriteTimeUtc(later, sessionEnd.UtcDateTime.AddDays(1));

        var summary = ObsSessionLogReader.TryReadLatest(sessionStart, sessionEnd, directory);

        Assert.NotNull(summary);
        Assert.Equal(mine, summary!.LogPath);
        Assert.Equal(1263, summary.LaggedRenderFrames);
    }

    /// <summary>OBS flushes its totals just after the session stops, so that write has to count.</summary>
    [Fact]
    public void ALogWrittenShortlyAfterTheSessionStillCounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"obs-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);

        var sessionStart = new DateTimeOffset(2026, 8, 22, 19, 0, 0, TimeSpan.Zero);
        var sessionEnd = new DateTimeOffset(2026, 8, 23, 0, 43, 0, TimeSpan.Zero);

        var path = Path.Combine(directory, "mine.txt");
        File.WriteAllText(path, RealisticLog);
        File.SetLastWriteTimeUtc(path, sessionEnd.UtcDateTime.AddSeconds(84));

        Assert.NotNull(ObsSessionLogReader.TryReadLatest(sessionStart, sessionEnd, directory));
    }

    [Fact]
    public void TheParserClaimsObsLogsAndLeavesOtherTextFilesAlone()
    {
        var parser = new ObsLogArtifactParser();

        Assert.True(parser.CanParse(WriteLog(RealisticLog)));
        Assert.False(parser.CanParse(WriteLog("2026-08-22 21:00:00 FiveM console: resource started\nnothing to see here\n")));
    }

    [Fact]
    public async Task ParsingProducesEvidenceCarryingTheLagPercentages()
    {
        var parser = new ObsLogArtifactParser();
        var result = await parser.ParseAsync(WriteLog(RealisticLog), CancellationToken.None);

        Assert.NotNull(result);
        var evidence = Assert.Single(result!.Evidence);
        Assert.Contains("obsRenderLagPercent", evidence.Metrics.Keys);
        Assert.Contains("obsEncodingLagPercent", evidence.Metrics.Keys);
        Assert.Equal(0.11, evidence.Metrics["obsRenderLagPercent"], 2);
    }

    [Fact]
    public async Task AStillRunningStreamIsReportedAsSuchRatherThanAsSilence()
    {
        var path = WriteLog("""
            21:04:01.148: OBS 32.2.2 (64-bit, windows)
            21:33:42.376: [rtmp stream: 'rtmp multitrack video'] Connection successful
            """);

        var result = await new ObsLogArtifactParser().ParseAsync(path, CancellationToken.None);

        Assert.NotNull(result);
        var evidence = Assert.Single(result!.Evidence);
        Assert.Contains("stoppas", evidence.Summary, StringComparison.Ordinal);
    }
}
