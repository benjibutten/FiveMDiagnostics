namespace FiveMDiagnostics.Tests;

using FiveMDiagnostics.Core;

/// <summary>
/// The minutes after a game start are reported apart from the minutes of play.
/// </summary>
/// <remarks>
/// The evening of 4 September spent 21.5 of 426 minutes above the band, and 80% of that time fell inside
/// the first forty minutes after the game was restarted at 21:10. Reported as one share it reads as a
/// card that is mildly full all evening; reported split it reads as a card that is full while the game
/// loads and comfortable afterwards, which is a different problem with a different answer. It also makes
/// two evenings comparable, which the single figure cannot: the same machine looks twice as pressured on
/// the night the game was restarted twice.
/// </remarks>
public sealed class VramBandLoadingSplitTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 19, 10, 0, TimeSpan.Zero);

    /// <summary>
    /// Ten pressured minutes right after the start and two more an hour later. The line separates them.
    /// </summary>
    [Fact]
    public void TheBandTimeIsSplitIntoLoadingAndRunning()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));
        monitor.NoteGameStart(Start);

        Play(monitor, Start, minutes: 10, vramPercent: 92);
        Play(monitor, Start.AddMinutes(10), minutes: 30, vramPercent: 70);

        // An hour in: past the loading window, and two minutes back in the band.
        Play(monitor, Start.AddMinutes(60), minutes: 2, vramPercent: 92);
        Play(monitor, Start.AddMinutes(62), minutes: 30, vramPercent: 70);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(12, report.MinutesInBand, 1);
        Assert.Equal(10, report.MinutesInBandLoading, 1);
        Assert.Equal(2, report.MinutesInBandSteady, 1);
        Assert.Equal(1, report.GameStarts);
        Assert.Contains("i inladdningen", report.Message, StringComparison.Ordinal);
        Assert.Contains("minuters drift", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restart opens a second loading window, and the line says how many there were — which is the
    /// figure a reader needs before comparing this evening's loading minutes with another's.
    /// </summary>
    [Fact]
    public void EachRestartOpensItsOwnLoadingWindow()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));
        monitor.NoteGameStart(Start);
        Play(monitor, Start, minutes: 5, vramPercent: 92);
        Play(monitor, Start.AddMinutes(5), minutes: 55, vramPercent: 70);

        monitor.NoteGameStart(Start.AddMinutes(60));
        Play(monitor, Start.AddMinutes(60), minutes: 5, vramPercent: 92);
        Play(monitor, Start.AddMinutes(65), minutes: 30, vramPercent: 70);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(2, report.GameStarts);
        Assert.Equal(10, report.MinutesInBandLoading, 1);
        Assert.Equal(0, report.MinutesInBandSteady, 1);
        Assert.Contains("efter 2 spelstarter", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Polling for the process must not turn the whole evening into a loading window. The same start
    /// told repeatedly is one start.
    /// </summary>
    [Fact]
    public void TheSameStartToldRepeatedlyIsOneStart()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));

        for (var i = 0; i < 50; i++)
        {
            monitor.NoteGameStart(Start.AddSeconds(i));
        }

        Play(monitor, Start, minutes: 5, vramPercent: 92);
        Play(monitor, Start.AddMinutes(5), minutes: 60, vramPercent: 70);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(1, report.GameStarts);
        Assert.Equal(40, report.LoadingMinutes, 1);
    }

    /// <summary>
    /// Without a start the split says nothing rather than guessing. A session that began with the game
    /// already running has no loading window to measure against.
    /// </summary>
    [Fact]
    public void WithNoStartTheLineIsUndivided()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));

        Play(monitor, Start, minutes: 5, vramPercent: 92);
        Play(monitor, Start.AddMinutes(5), minutes: 20, vramPercent: 70);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(0, report.GameStarts);
        Assert.DoesNotContain("i inladdningen", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The band is neither cleared nor blamed on a minute of it, however long the session has run.
    /// </summary>
    /// <remarks>
    /// 02:14 on 9 September: "Det är ett motbevis mot att bandet skulle vara orsaken" on 0.9 minutes in
    /// the band. Thirty measured minutes cleared the session-length gate the sentence was behind, and
    /// the comparison it rests on had a minute of frames on one side.
    /// </remarks>
    [Fact]
    public void AMinuteInTheBandExoneratesNothing()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));

        // Well past the session gate, and a single quiet minute inside the band.
        Play(monitor, Start, minutes: 40, vramPercent: 70);
        PlayQuiet(monitor, Start.AddMinutes(40), minutes: 1, vramPercent: 92);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.True(report.BandCostNothing);
        Assert.Contains("minuter i bandet", report.Message, StringComparison.Ordinal);
        Assert.Contains("ingen slutsats åt något håll", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Bandet kostade ingenting", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Det är ett motbevis", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same evening with real time in the band, where the comparison is worth making.
    /// </summary>
    [Fact]
    public void EnoughTimeInTheBandRestoresTheVerdict()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));

        Play(monitor, Start, minutes: 40, vramPercent: 70);
        PlayQuiet(monitor, Start.AddMinutes(40), minutes: 8, vramPercent: 92);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Contains("Bandet kostade ingenting", report.Message, StringComparison.Ordinal);
        Assert.Contains("Det är ett motbevis", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ten minutes the card is measured after the game closes are not minutes of the evening.
    /// </summary>
    /// <remarks>
    /// The tail exists to show what the card gives back, and the case it was built for — memory still
    /// held at 89 % after the exit — is the one where counting it does the most damage: ten minutes
    /// nobody played, added to the numerator and the denominator of the share the evening is judged on,
    /// and filed as drift rather than loading. An evening that ends with the machine switched off
    /// produces no tail at all, so the figure would also depend on how the evening happened to end.
    /// </remarks>
    [Fact]
    public void TheMinutesAfterTheGameClosesAreNotCounted()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));
        monitor.NoteGameStart(Start);
        Play(monitor, Start, minutes: 30, vramPercent: 70);

        var exited = Start.AddMinutes(30);
        monitor.NoteGameExit(exited);

        // The card still full, and nothing rendering on it. From the poll after the exit: a reading taken
        // in the same instant the game went away is still the evening's.
        Readings(monitor, exited.AddSeconds(5), minutes: 10, vramPercent: 92);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(30, report.MeasuredMinutes, 1);
        Assert.Equal(0, report.MinutesInBand, 1);
        Assert.Equal(70, report.PeakPercent, 1);
    }

    /// <summary>
    /// A restart inside the tail is the same evening, and its minutes count again from the moment the
    /// game is back.
    /// </summary>
    [Fact]
    public void AGameThatComesBackIsMeasuredAgain()
    {
        var monitor = new VramPressureBandMonitor(new HitchThreshold(60));
        monitor.NoteGameStart(Start);
        Play(monitor, Start, minutes: 30, vramPercent: 70);

        var exited = Start.AddMinutes(30);
        monitor.NoteGameExit(exited);
        Readings(monitor, exited.AddSeconds(5), minutes: 5, vramPercent: 92);

        var restarted = exited.AddMinutes(5);
        monitor.NoteGameStart(restarted);
        Play(monitor, restarted, minutes: 20, vramPercent: 92);

        var report = monitor.Summary();

        Assert.NotNull(report);
        Assert.Equal(50, report.MeasuredMinutes, 1);
        Assert.Equal(20, report.MinutesInBand, 1);
    }

    /// <summary>Adapter readings with no game rendering behind them, which is what the tail is.</summary>
    private static void Readings(VramPressureBandMonitor monitor, DateTimeOffset from, int minutes, double vramPercent)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            var minuteStart = from.AddMinutes(minute);

            for (var reading = 0; reading < 12; reading++)
            {
                monitor.Observe(Adapter(minuteStart.AddSeconds(reading * 5), vramPercent));
            }
        }
    }

    /// <summary>
    /// One frame a second at the session's cadence, with a hitch a minute so the threshold settles.
    /// </summary>
    private static void Play(VramPressureBandMonitor monitor, DateTimeOffset from, int minutes, double vramPercent)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            var minuteStart = from.AddMinutes(minute);

            for (var reading = 0; reading < 12; reading++)
            {
                monitor.Observe(Adapter(minuteStart.AddSeconds(reading * 5), vramPercent));
            }

            for (var frame = 0; frame < 60; frame++)
            {
                monitor.Observe(Frame(minuteStart.AddSeconds(frame), frame == 0 ? 90 : 16.7));
            }
        }
    }

    /// <summary>The same minutes without the hitch, so the band's own rate comes out lower.</summary>
    private static void PlayQuiet(VramPressureBandMonitor monitor, DateTimeOffset from, int minutes, double vramPercent)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            var minuteStart = from.AddMinutes(minute);

            for (var reading = 0; reading < 12; reading++)
            {
                monitor.Observe(Adapter(minuteStart.AddSeconds(reading * 5), vramPercent));
            }

            for (var frame = 0; frame < 60; frame++)
            {
                monitor.Observe(Frame(minuteStart.AddSeconds(frame), 16.7));
            }
        }
    }

    private static GpuTelemetrySample Adapter(DateTimeOffset timestamp, double vramPercent)
    {
        const ulong Total = 10UL * 1024 * 1024 * 1024;

        return new GpuTelemetrySample(
            timestamp,
            IsAvailable: true,
            "NVIDIA GeForce RTX 3080",
            UtilizationPercent: 60,
            MemoryBandwidthUtilizationPercent: 20,
            UsedVramBytes: (ulong)(Total * vramPercent / 100),
            TotalVramBytes: Total,
            EncoderUtilizationPercent: 12,
            DecoderUtilizationPercent: 0,
            TemperatureCelsius: 60,
            ThrottleReasons: [],
            AdapterCount: 1);
    }

    private static FrameTelemetrySample Frame(DateTimeOffset timestamp, double frameTimeMs)
    {
        return new FrameTelemetrySample(
            timestamp,
            frameTimeMs,
            GpuBusyMs: 5,
            DisplayLatencyMs: 20,
            MsBetweenPresents: frameTimeMs,
            Dropped: false,
            ProcessName: "FiveM_b3407_GTAProcess.exe",
            CpuBusyMs: frameTimeMs - 6,
            CpuWaitMs: 6);
    }
}
