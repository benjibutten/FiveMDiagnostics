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
    private static byte[] Dump(bool steamLoaded)
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
        writer.Write((uint)Crashed.ToUnixTimeSeconds());
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
        writer.Write(DevtoolsBase + 0x2CCD6);
        writer.Write(2u);
        writer.Write(0u);
        writer.Write(1UL);
        writer.Write(0xDEEDUL);
        writer.Write(new byte[(15 - 2) * 8]);
        writer.Write(0u);
        writer.Write(0u);

        var strings = new MemoryStream();
        var nameRvas = new List<uint>();
        foreach (var text in modules.Append("Window Watchdog"))
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
