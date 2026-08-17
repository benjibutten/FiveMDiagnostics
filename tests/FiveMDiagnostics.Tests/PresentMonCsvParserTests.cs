namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Integrations.PresentMon;

/// <summary>
/// Every header and data row below was captured verbatim from PresentMon 2.4.1 on Windows 11. The three
/// modes emit genuinely different column names, and a parser written against one produces zero rows
/// against another, so all three are pinned here.
/// </summary>
public sealed class PresentMonCsvParserTests
{
    /// <summary>PresentMon's default output — what the app requests, and the richest column set.</summary>
    private const string DefaultHeader =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,"
        + "PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,"
        + "MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,"
        + "MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,"
        + "MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    /// <summary>The --v2_metrics scheme: same data, no Ms prefix, fewer columns than the default.</summary>
    private const string V2MetricsHeader =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,"
        + "PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,"
        + "DisplayLatency,DisplayedTime,AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency,"
        + "ClickToPhotonLatency";

    private const string V1MetricsHeader =
        "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,"
        + "msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode,msUntilRenderComplete,"
        + "msUntilDisplayed,msBetweenDisplayChange,msFlipDelay,msUntilRenderStart,msGPUActive,msSinceInput";

    private const string V2MetricsRow =
        "powershell.exe,23808,0x21B3B8D7B98,D3D9,-1,0,0,Composed: Copy with GPU GDI,"
        + "4.5693,4.5473,4.3471,0.2002,0.0611,4.4668,0.1352,4.3316,11.9879,4.1037,NA,4.5693,NA,NA,NA";

    private const string DefaultRow =
        "FiveM_b3407_GTAProcess.exe,6048,0x2EEABC088D8,D3D9,-1,0,0,Composed: Copy with GPU GDI,"
        + "4270.1141,NA,18.4,18.5,0.32,0.27,13.5961,4251.7,18.4,15.2,1.1,7.9,6.5,6.3,0.4,NA,4251.7,0.2,62.4511,NA";

    [Fact]
    public void ParseRow_MapsDefaultScheme()
    {
        var header = PresentMonCsvParser.ParseHeader(DefaultHeader);
        var sample = PresentMonCsvParser.ParseRow(DefaultRow.Split(','), header, "fallback", DateTimeOffset.UnixEpoch);

        Assert.NotNull(sample);
        Assert.Equal(18.4, sample.FrameTimeMs, 3);
        Assert.Equal(15.2, sample.CpuBusyMs);
        Assert.Equal(1.1, sample.CpuWaitMs);
        Assert.Equal(6.3, sample.GpuBusyMs);
        Assert.Equal(0.4, sample.GpuWaitMs);
        Assert.Equal(7.9, sample.GpuLatencyMs);
        Assert.Equal(13.5961, sample.DisplayLatencyMs);
        Assert.Equal(0.2, sample.FlipDelayMs);
        Assert.Equal("FiveM_b3407_GTAProcess.exe", sample.ProcessName);
        Assert.False(sample.Dropped);
    }

    /// <summary>
    /// Regression: this scheme names the frame duration <c>FrameTime</c>, not <c>MsBetweenPresents</c>.
    /// A parser looking only for the latter returned null for every single row.
    /// </summary>
    [Fact]
    public void ParseRow_MapsV2MetricsScheme()
    {
        var header = PresentMonCsvParser.ParseHeader(V2MetricsHeader);
        var sample = PresentMonCsvParser.ParseRow(V2MetricsRow.Split(','), header, "fallback", DateTimeOffset.UnixEpoch);

        Assert.NotNull(sample);
        Assert.Equal(4.5473, sample.FrameTimeMs, 4);
        Assert.Equal(4.3471, sample.CpuBusyMs);
        Assert.Equal(0.2002, sample.CpuWaitMs);
        Assert.Equal(0.1352, sample.GpuBusyMs);
        Assert.Equal(4.3316, sample.GpuWaitMs);
        Assert.Equal(0.0611, sample.GpuLatencyMs);
        Assert.Equal(11.9879, sample.DisplayLatencyMs);
        Assert.Equal("powershell.exe", sample.ProcessName);
    }

    [Fact]
    public void ParseRow_MapsV1MetricsScheme()
    {
        var header = PresentMonCsvParser.ParseHeader(V1MetricsHeader);
        var row = "app.exe,1,0x0,DXGI,1,0,1,4.27,0.3,18.4,0,Hardware: Independent Flip,7.0,13.6,18.5,0.2,1.0,6.3,25.0";

        var sample = PresentMonCsvParser.ParseRow(row.Split(','), header, "fallback", DateTimeOffset.UnixEpoch);

        Assert.NotNull(sample);
        Assert.Equal(18.4, sample.FrameTimeMs, 3);
        Assert.Equal(6.3, sample.GpuBusyMs);
        Assert.Equal(13.6, sample.DisplayLatencyMs);
        Assert.True(sample.Dropped);

        // v1 has no CPU/GPU split, so attribution is simply unavailable rather than wrong.
        Assert.Null(sample.CpuBusyMs);
        Assert.Null(sample.GpuWaitMs);
    }

    /// <summary>
    /// Every scheme reports frame time relative to trace start in milliseconds, just under a different
    /// column name. Getting this wrong collapses a whole polling batch onto one timestamp.
    /// </summary>
    [Theory]
    [InlineData(nameof(DefaultHeader))]
    [InlineData(nameof(V2MetricsHeader))]
    [InlineData(nameof(V1MetricsHeader))]
    public void ReadRelativeMs_ReturnsMillisecondsForEveryScheme(string schemeName)
    {
        var (header, row, expected) = schemeName switch
        {
            nameof(DefaultHeader) => (DefaultHeader, DefaultRow, 4270.1141),
            nameof(V2MetricsHeader) => (V2MetricsHeader, V2MetricsRow, 4.5693),
            _ => (V1MetricsHeader, "app.exe,1,0x0,DXGI,1,0,0,4.2701141,0.3,18.4,0,Composed,7.0,13.6,18.5,0.2,1.0,6.3,25.0", 4270.1141),
        };

        var parsed = PresentMonCsvParser.ReadRelativeMs(row.Split(','), PresentMonCsvParser.ParseHeader(header));

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Value, 3);
    }

    [Fact]
    public void ParseRow_TreatsUndisplayedFrameAsDropped()
    {
        var header = PresentMonCsvParser.ParseHeader(V2MetricsHeader);
        var cells = V2MetricsRow.Split(',');
        cells[16] = "NA"; // DisplayLatency

        var sample = PresentMonCsvParser.ParseRow(cells, header, "fallback", DateTimeOffset.UnixEpoch);

        Assert.NotNull(sample);
        Assert.True(sample.Dropped);
        Assert.Null(sample.DisplayLatencyMs);
    }

    [Fact]
    public void ParseRow_ReturnsNull_WhenNoFrameTimeColumnExists()
    {
        var header = PresentMonCsvParser.ParseHeader("Application,ProcessID,TimeInMs");

        Assert.Null(PresentMonCsvParser.ParseRow("app.exe,1,10.0".Split(','), header, "fallback", DateTimeOffset.UnixEpoch));
    }
}
