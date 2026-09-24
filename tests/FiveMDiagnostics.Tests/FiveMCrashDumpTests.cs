namespace FiveMDiagnostics.Tests;

using System.Text;

using FiveMDiagnostics.Core;

/// <summary>
/// The crash of 2026-09-12 22:10:31, which the app did not see because it was not running and which took
/// a hand-written minidump reader the day after.
/// </summary>
public sealed class FiveMCrashDumpTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 9, 12, 19, 50, 16, TimeSpan.Zero);
    private static readonly DateTimeOffset Crashed = new(2026, 9, 12, 20, 10, 31, TimeSpan.Zero);
    private const ulong DevtoolsBase = 0x7FF8_1000_0000;

    /// <summary>Where the builder puts the game executable, the first module, below citizen-devtools.</summary>
    private const ulong ExeBase = DevtoolsBase - 0x1000_0000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiveMDiagnosticsTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TheWatchdogCrashWithSteamLoadedReadsAsTheNoteWroteIt()
    {
        var dump = MinidumpReader.Read(new MemoryStream(Dump(steamLoaded: true)), "3ab0bc5b.dmp");

        Assert.NotNull(dump);
        Assert.True(dump!.IsWatchdog);

        var line = dump.Describe(Crashed.AddMinutes(50));
        Assert.Contains("citizen-devtools.dll+0x2CCD6", line, StringComparison.Ordinal);
        Assert.Contains("20 min efter start", line, StringComparison.Ordinal);
        Assert.Contains("skrivning till 0xDEED", line, StringComparison.Ordinal);
        Assert.Contains("\"Window Watchdog\"", line, StringComparison.Ordinal);
        Assert.Contains("den kända Steam-kapplöpningen; Steam var inladdat", line, StringComparison.Ordinal);
    }

    /// <summary>The question the next evening is judged on, so the answer has to read differently.</summary>
    [Fact]
    public void TheSameCrashWithoutSteamSaysTheSteamExplanationDoesNotApply()
    {
        var dump = MinidumpReader.Read(new MemoryStream(Dump(steamLoaded: false)), "next.dmp");

        Assert.Contains("Steam-förklaringen gäller inte", dump!.Describe(Crashed), StringComparison.Ordinal);
    }

    /// <summary>
    /// The game's own exit path running into FiveM's trap, out of normal play: the line names the trap
    /// and says the game did not hang first, so a Windows hang report written afterwards is not read as
    /// the cause.
    /// </summary>
    [Fact]
    public void AnEarlyExitTrapOutOfNormalPlaySaysTheGameDidNotHangFirst()
    {
        var crashed = new DateTimeOffset(2026, 9, 23, 20, 19, 21, TimeSpan.Zero);
        var dump = MinidumpReader.Read(new MemoryStream(EarlyExitDump(crashed)), "520a42ae.dmp");

        Assert.True(dump!.IsEarlyExitTrap);
        Assert.False(dump.IsWatchdog);

        var line = dump.Describe(crashed, FramesUntil(crashed.AddMilliseconds(40)));
        Assert.Contains("FiveM_b3407_GTAProcess.exe+0x101C", line, StringComparison.Ordinal);
        Assert.Contains("\"MainThrd\"", line, StringComparison.Ordinal);
        Assert.Contains("early-exit trap", line, StringComparison.Ordinal);
        Assert.Contains("frös alltså inte före kraschen", line, StringComparison.Ordinal);
        Assert.Contains("AppHang", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatchdogCrashIsNotAnEarlyExitTrap()
    {
        Assert.False(MinidumpReader.Read(new MemoryStream(Dump(steamLoaded: false)), "w.dmp")!.IsEarlyExitTrap);
    }

    /// <summary>A game that stopped presenting well before it died hung first, whatever the dump says.</summary>
    [Fact]
    public void FramesThatStoppedBeforeTheCrashSayTheGameHung()
    {
        var dump = MinidumpReader.Read(new MemoryStream(Dump(steamLoaded: false)), "w.dmp")!;

        var line = dump.Describe(Crashed, FramesUntil(Crashed.AddSeconds(-20)));

        Assert.Contains("Bilden stod still före kraschen", line, StringComparison.Ordinal);
        Assert.DoesNotContain("frös alltså inte", line, StringComparison.Ordinal);
    }

    /// <summary>A two-second frame just before the crash is a hang even when frames came right up to it.</summary>
    [Fact]
    public void ALongFrameJustBeforeTheCrashSaysTheGameHung()
    {
        var dump = MinidumpReader.Read(new MemoryStream(EarlyExitDump(Crashed)), "e.dmp")!;
        var frames = FramesUntil(Crashed.AddMilliseconds(-2400))
            .Append(new FrameTelemetrySample(Crashed, 2400, 5, null, 2400, false, "FiveM_b3407_GTAProcess.exe"));

        var line = dump.Describe(Crashed, frames);

        Assert.Contains("en frame på 2400 ms", line, StringComparison.Ordinal);
        Assert.DoesNotContain("frös alltså inte", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dump is written a moment after the fault and the last frames reach the session late, so a few
    /// seconds' silence before a dump is still a crash out of normal play.
    /// </summary>
    [Fact]
    public void AFewSecondsOfSilenceBeforeTheDumpIsNotAHang()
    {
        var dump = MinidumpReader.Read(new MemoryStream(EarlyExitDump(Crashed)), "e.dmp")!;

        var line = dump.Describe(Crashed, FramesUntil(Crashed.AddSeconds(-4)));

        Assert.Contains("frös alltså inte", line, StringComparison.Ordinal);
    }

    /// <summary>Frames from long before the crash say nothing about whether it hung.</summary>
    [Fact]
    public void FramesFromLongBeforeTheCrashLeaveTheFrameSentenceOut()
    {
        var dump = MinidumpReader.Read(new MemoryStream(EarlyExitDump(Crashed)), "e.dmp")!;

        var line = dump.Describe(Crashed, FramesUntil(Crashed.AddMinutes(-2)));

        Assert.DoesNotContain("Bilden", line, StringComparison.Ordinal);
    }

    /// <summary>The watchdog fires on a game it judged hung, so the line does not say it did not freeze.</summary>
    [Fact]
    public void AWatchdogCrashDoesNotClaimTheGameDidNotFreeze()
    {
        var dump = MinidumpReader.Read(new MemoryStream(Dump(steamLoaded: false)), "w.dmp")!;

        var line = dump.Describe(Crashed, FramesUntil(Crashed));

        Assert.Contains("Bilden rullade normalt", line, StringComparison.Ordinal);
        Assert.DoesNotContain("frös alltså inte", line, StringComparison.Ordinal);
    }

    /// <summary>With no frames from the crashed game, the line says nothing about them.</summary>
    [Fact]
    public void NoFramesBeforeTheCrashLeaveTheFrameSentenceOut()
    {
        var dump = MinidumpReader.Read(new MemoryStream(Dump(steamLoaded: false)), "w.dmp")!;

        var line = dump.Describe(Crashed, FramesUntil(Crashed.AddMinutes(5))
            .Where(frame => frame.Timestamp > Crashed.AddMinutes(1)).ToArray());

        Assert.DoesNotContain("Bilden", line, StringComparison.Ordinal);
    }

    /// <summary>FiveM wrote two dumps a second apart for the crash of 2026-09-23; that is one crash.</summary>
    [Fact]
    public void TwoDumpsASecondApartAreOneCrash()
    {
        var crashed = new DateTimeOffset(2026, 9, 23, 20, 19, 21, TimeSpan.Zero);
        var first = MinidumpReader.Read(new MemoryStream(EarlyExitDump(crashed)), "520a42ae.dmp")!;
        var second = MinidumpReader.Read(new MemoryStream(EarlyExitDump(crashed.AddSeconds(1))), "383f5b6b.dmp")!;
        var later = MinidumpReader.Read(new MemoryStream(EarlyExitDump(crashed.AddHours(2))), "later.dmp")!;

        // Out of order: file write times, which the dumps are listed by, need not agree with their clocks.
        var lines = FiveMCrashDumpLog.Describe([later, second, first], crashed, []);

        Assert.Equal(2, lines.Count);
        Assert.Contains("Dump: 520a42ae.dmp", lines[0], StringComparison.Ordinal);
        Assert.Contains("En dump till från samma krasch, 1 s senare", lines[0], StringComparison.Ordinal);
        Assert.Contains("383f5b6b.dmp", lines[0], StringComparison.Ordinal);
        Assert.Contains("Dump: later.dmp", lines[1], StringComparison.Ordinal);
    }

    /// <summary>A minute of steady 60 FPS frames ending at <paramref name="last"/>.</summary>
    private static FrameTelemetrySample[] FramesUntil(DateTimeOffset last) =>
        Enumerable.Range(0, 3600)
            .Select(index => new FrameTelemetrySample(
                last.AddMilliseconds(-16.667 * index), 16.667, 5, null, 16.667, false, "FiveM_b3407_GTAProcess.exe"))
            .ToArray();

    /// <summary>A dump FiveM is still writing is shorter than its own directory says.</summary>
    [Fact]
    public void AHalfWrittenDumpIsNotRead()
    {
        var path = WriteDump("partial.dmp", Dump(steamLoaded: true)[..300]);

        Assert.Null(MinidumpReader.TryRead(path));
    }

    [Fact]
    public void EachDumpIsReportedOnceAndAHalfWrittenOneWhenItIsComplete()
    {
        var crashes = Path.Combine(_directory, "crashes");
        var working = Path.Combine(_directory, "Sessions");
        var now = DateTimeOffset.UtcNow;

        var complete = Dump(steamLoaded: true);
        WriteDump(Path.Combine("crashes", "first.dmp"), complete);
        WriteDump(Path.Combine("crashes", "second.dmp"), complete[..300]);

        Assert.Equal(["first.dmp"], FiveMCrashDumpLog.TakeNew(crashes, working, now).Select(dump => dump.FileName));

        // Finished writing between two checks.
        WriteDump(Path.Combine("crashes", "second.dmp"), complete);
        Assert.Equal(["second.dmp"], FiveMCrashDumpLog.TakeNew(crashes, working, now).Select(dump => dump.FileName));

        Assert.Empty(FiveMCrashDumpLog.TakeNew(crashes, working, now));
    }

    /// <summary>The first check on a machine reports the last week, not everything FiveM ever kept.</summary>
    [Fact]
    public void TheFirstCheckLeavesOldDumpsOut()
    {
        var crashes = Path.Combine(_directory, "crashes");
        var working = Path.Combine(_directory, "Sessions");
        var now = DateTimeOffset.UtcNow;

        var old = WriteDump(Path.Combine("crashes", "august.dmp"), Dump(steamLoaded: true));
        File.SetLastWriteTimeUtc(old, now.AddDays(-30).UtcDateTime);
        WriteDump(Path.Combine("crashes", "yesterday.dmp"), Dump(steamLoaded: true));

        Assert.Equal(["yesterday.dmp"], FiveMCrashDumpLog.TakeNew(crashes, working, now).Select(dump => dump.FileName));
        Assert.Empty(FiveMCrashDumpLog.TakeNew(crashes, working, now));
    }

    private string WriteDump(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// The four streams the reader uses, laid out as <c>minidumpapiset.h</c> does: exception, module list,
    /// misc info and thread names, with the strings after them.
    /// </summary>
    private static byte[] Dump(bool steamLoaded) =>
        Dump(steamLoaded, Crashed, DevtoolsBase + 0x2CCD6, accessKind: 1, accessAddress: 0xDEED, "Window Watchdog");

    /// <summary>
    /// The crash of 2026-09-23 22:19:21: an execution fault at <c>FiveM_b3407_GTAProcess.exe+0x101C</c>
    /// on the main thread, the address and the faulting instruction the same.
    /// </summary>
    private static byte[] EarlyExitDump(DateTimeOffset crashedAt) =>
        Dump(steamLoaded: false, crashedAt, ExeBase + 0x101C, accessKind: 8, accessAddress: ExeBase + 0x101C, "MainThrd");

    private static byte[] Dump(bool steamLoaded, DateTimeOffset crashedAt, ulong address, ulong accessKind, ulong accessAddress, string threadName)
    {
        const uint ThreadId = 4184;
        string[] modules = steamLoaded
            ? [@"C:\FiveM\FiveM_b3407_GTAProcess.exe", @"C:\FiveM\citizen-devtools.dll", @"C:\Steam\steamclient64.dll"]
            : [@"C:\FiveM\FiveM_b3407_GTAProcess.exe", @"C:\FiveM\citizen-devtools.dll"];

        const int Header = 32;
        const int Directory = 4 * 12;
        const int Exception = 168;
        var moduleList = 4 + (modules.Length * 108);
        const int Misc = 24;
        const int Names = 4 + 12;

        var exceptionRva = Header + Directory;
        var moduleRva = exceptionRva + Exception;
        var miscRva = moduleRva + moduleList;
        var namesRva = miscRva + Misc;
        var stringsRva = namesRva + Names;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(0x504D444Du);
        writer.Write(0xA793u);
        writer.Write(4u);
        writer.Write((uint)Header);
        writer.Write(0u);
        writer.Write((uint)crashedAt.ToUnixTimeSeconds());
        writer.Write(0UL);

        foreach (var (type, size, rva) in new[] { (6u, Exception, exceptionRva), (4u, moduleList, moduleRva), (15u, Misc, miscRva), (24u, Names, namesRva) })
        {
            writer.Write(type);
            writer.Write((uint)size);
            writer.Write((uint)rva);
        }

        writer.Write(ThreadId);
        writer.Write(0u);
        writer.Write(0xC0000005u);
        writer.Write(0u);
        writer.Write(0UL);
        writer.Write(address);
        writer.Write(2u);
        writer.Write(0u);
        writer.Write(accessKind);
        writer.Write(accessAddress);
        writer.Write(new byte[(15 - 2) * 8]);
        writer.Write(0u);
        writer.Write(0u);

        var strings = new MemoryStream();
        var nameRvas = new List<uint>();
        foreach (var text in modules.Append(threadName))
        {
            nameRvas.Add((uint)(stringsRva + strings.Length));
            var bytes = Encoding.Unicode.GetBytes(text);
            strings.Write(BitConverter.GetBytes((uint)bytes.Length));
            strings.Write(bytes);
            strings.Write(new byte[2]);
        }

        writer.Write((uint)modules.Length);
        for (var index = 0; index < modules.Length; index++)
        {
            writer.Write(DevtoolsBase + ((ulong)index - 1) * 0x1000_0000);
            writer.Write(0x0100_0000u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(nameRvas[index]);
            writer.Write(new byte[108 - 24]);
        }

        writer.Write((uint)Misc);
        writer.Write(0x3u);
        writer.Write(16436u);
        writer.Write((uint)Started.ToUnixTimeSeconds());
        writer.Write(0u);
        writer.Write(0u);

        writer.Write(1u);
        writer.Write(ThreadId);
        writer.Write((ulong)nameRvas[^1]);

        writer.Write(strings.ToArray());
        writer.Flush();
        return stream.ToArray();
    }
}
