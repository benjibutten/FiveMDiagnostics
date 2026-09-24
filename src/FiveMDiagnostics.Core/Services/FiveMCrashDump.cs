using System.Text;

namespace FiveMDiagnostics.Core;

/// <summary>
/// What a FiveM crash dump says about the crash: where it happened, what it did, and whether Steam's
/// client was inside the game when it did.
/// </summary>
/// <remarks>
/// The finding of 2026-09-12 — <c>citizen-devtools.dll+0x2CCD6</c>, a write to <c>0xDEED</c>, Steam's
/// client loaded — took a hand-written minidump reader the day after, for a crash the app never saw
/// because it was not running. Every figure in it sits in three fixed-layout streams of the file, so no
/// debugger and no symbols are needed.
/// </remarks>
/// <param name="ExceptionCode">The NTSTATUS the process died of, e.g. 0xC0000005.</param>
/// <param name="FaultOffset">Offset into <paramref name="FaultingModule"/>, or the raw address when no module holds it.</param>
/// <param name="AccessKind">For an access violation: 0 read, 1 write, 8 execution. Null for anything else.</param>
public sealed record FiveMCrashDump(
    string FileName,
    DateTimeOffset CrashedAt,
    DateTimeOffset? ProcessStartedAt,
    uint ExceptionCode,
    string? FaultingModule,
    ulong FaultOffset,
    ulong? AccessKind,
    ulong? AccessAddress,
    string? ThreadName,
    bool SteamClientLoaded)
{
    private const uint AccessViolation = 0xC0000005;
    private const double StalledFrameMs = 2000;

    /// <summary>
    /// Silence before the dump that reads as a hang. Wider than it looks: the dump header carries the
    /// second the dump was written, not the fault, and the last frames reach the session through
    /// PresentMon's own flush — together a few seconds on a crash out of normal play.
    /// </summary>
    private static readonly TimeSpan StalledGap = TimeSpan.FromSeconds(5);

    /// <summary>Silence beyond which the frames say nothing about the crash, hung or not.</summary>
    private static readonly TimeSpan NoFramesGap = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LastFramesWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// FiveM's deliberate crash of a game that stopped responding: a write to an address nothing owns,
    /// from the window watchdog. Matched on the module and the address rather than the offset, which
    /// moves with every client build.
    /// </summary>
    public bool IsWatchdog =>
        ExceptionCode == AccessViolation
        && AccessKind == 1
        && AccessAddress == 0xDEED
        && string.Equals(FaultingModule, "citizen-devtools.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An execution fault inside the game executable itself: the form a crash FiveM's dialog called an
    /// early-exit trap took in its dump, the game's main thread running into a page it may not execute.
    /// </summary>
    public bool IsEarlyExitTrap =>
        ExceptionCode == AccessViolation
        && AccessKind == 8
        && FaultingModule?.EndsWith("GTAProcess.exe", StringComparison.OrdinalIgnoreCase) == true;

    /// <param name="now">Local now, so a crash from an earlier day carries its date.</param>
    /// <param name="frames">
    /// Frames the session saw presented, in any order. When they reach up to the crash, the line says
    /// whether the game was still presenting or had already stopped.
    /// </param>
    public string Describe(DateTimeOffset now, IEnumerable<FrameTelemetrySample>? frames = null)
    {
        var crashed = CrashedAt.ToLocalTime();
        var when = crashed.Date == now.ToLocalTime().Date ? $"{crashed:HH:mm:ss}" : $"{crashed:yyyy-MM-dd HH:mm:ss}";
        var age = ProcessStartedAt is { } started && CrashedAt > started
            ? $", {SessionStartAge.Humanise(CrashedAt - started)} efter start"
            : string.Empty;

        var thread = ThreadName is null ? string.Empty : $" (tråden \"{ThreadName}\")";

        var verdict = (IsWatchdog, SteamClientLoaded) switch
        {
            (true, true) => " Det är FiveM:s watchdog och den kända Steam-kapplöpningen; Steam var inladdat.",

            // The case the evening after 2026-09-12 is judged on, so it has to read as a different answer
            // rather than as the same line with one word missing.
            (true, false) => " Det är FiveM:s watchdog, men Steam var inte inladdat — Steam-förklaringen gäller inte den här kraschen.",
            (false, true) => " Steam var inladdat.",
            (false, false) => " Steam var inte inladdat.",
        };

        var trap = IsEarlyExitTrap
            ? " Det är formen av FiveM:s early-exit trap: spelkoden gick själv in i sin avslutningsväg och "
              + "stoppades där. Dumpen säger inte varför, men vad som hände i spelet just då brukar göra det."
            : string.Empty;

        return $"Spelet kraschade {when}{age}: {Location}{thread}, {DescribeException()}.{trap}{verdict}"
            + $"{DescribeLastFrames(frames ?? [])} Dump: {FileName}.";
    }

    /// <summary>Where the fault was: <c>module+0xoffset</c>, or the raw address when no module holds it.</summary>
    public string Location => FaultingModule is null ? $"0x{FaultOffset:X}" : $"{FaultingModule}+0x{FaultOffset:X}";

    /// <summary>
    /// Whether the game was still presenting when it crashed, which tells a crash out of normal play from
    /// a game that hung first. Empty when no frame came in the half minute before the crash.
    /// </summary>
    private string DescribeLastFrames(IEnumerable<FrameTelemetrySample> frames)
    {
        // The dump's clock has whole seconds, so the crash itself can be up to a second after CrashedAt.
        var end = CrashedAt.AddSeconds(1);
        var before = frames.Where(frame => frame.Timestamp <= end).ToArray();
        if (before.Length == 0)
        {
            return string.Empty;
        }

        var last = before.MaxBy(frame => frame.Timestamp)!;
        var lastAt = last.Timestamp.ToLocalTime();
        var silence = CrashedAt - last.Timestamp;
        if (silence > NoFramesGap)
        {
            return string.Empty;
        }

        if (silence > StalledGap)
        {
            return $" Bilden stod still före kraschen: sista frame {lastAt:HH:mm:ss}, {silence.TotalSeconds:F0} s före. "
                + "Spelet hängde innan det dog.";
        }

        var longest = before.Where(frame => frame.Timestamp > CrashedAt - LastFramesWindow).Max(frame => frame.FrameTimeMs);
        if (longest >= StalledFrameMs)
        {
            return $" Bilden stod still före kraschen: en frame på {longest:F0} ms de sista "
                + $"{LastFramesWindow.TotalSeconds:F0} sekunderna. Spelet hängde innan det dog.";
        }

        var rolled = $" Bilden rullade normalt ända till {lastAt:HH:mm:ss,f} (längsta frame de sista "
            + $"{LastFramesWindow.TotalSeconds:F0} s: {longest:F0} ms).";

        // The watchdog only fires on a game it judged hung, so "did not freeze" would contradict the dump;
        // the frames are stated and the reader weighs them against it.
        return IsWatchdog
            ? rolled
            : rolled + " Spelet frös alltså inte före kraschen, och en AppHang i händelseloggen efteråt är "
                + "kraschdialogen, inte orsaken.";
    }

    private string DescribeException()
    {
        if (ExceptionCode != AccessViolation || AccessAddress is not { } address)
        {
            return $"undantag 0x{ExceptionCode:X8}";
        }

        return AccessKind switch
        {
            1 => $"skrivning till 0x{address:X}",
            8 => $"körning på 0x{address:X}",
            _ => $"läsning från 0x{address:X}",
        };
    }
}

/// <summary>
/// Reads the handful of fields <see cref="FiveMCrashDump"/> needs straight out of the minidump format.
/// </summary>
/// <remarks>
/// Layouts are those of <c>minidumpapiset.h</c>. Every count and offset read from the file is checked
/// against the file before it is followed, because a dump FiveM is still writing is exactly as long as
/// it has got so far.
/// </remarks>
public static class MinidumpReader
{
    private const uint Signature = 0x504D444D; // "MDMP"
    private const uint ModuleListStream = 4;
    private const uint ExceptionStream = 6;
    private const uint MiscInfoStream = 15;
    private const uint ThreadNamesStream = 24;
    private const int ModuleSize = 108;
    private const uint MiscProcessTimes = 0x2;

    /// <summary>Generous bounds for a real dump, tight enough that a corrupt count cannot run away.</summary>
    private const uint MaxStreams = 1024;
    private const uint MaxModules = 16_384;
    private const uint MaxStringBytes = 32_768;

    /// <summary>The crash, or null when the file is not a readable minidump with an exception in it.</summary>
    public static FiveMCrashDump? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public static FiveMCrashDump? Read(Stream stream, string fileName)
    {
        using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: true);

        if (stream.Length < 32 || reader.ReadUInt32() != Signature)
        {
            return null;
        }

        reader.ReadUInt32(); // Version
        var streamCount = reader.ReadUInt32();
        var directoryRva = reader.ReadUInt32();
        reader.ReadUInt32(); // CheckSum
        var crashedAt = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32());

        if (streamCount > MaxStreams)
        {
            throw new InvalidDataException("Orimligt antal strömmar.");
        }

        var streams = new Dictionary<uint, uint>();
        Seek(reader, directoryRva, (long)streamCount * 12);
        for (var index = 0; index < streamCount; index++)
        {
            var type = reader.ReadUInt32();
            reader.ReadUInt32(); // DataSize
            var rva = reader.ReadUInt32();
            streams.TryAdd(type, rva);
        }

        if (!streams.TryGetValue(ExceptionStream, out var exceptionRva))
        {
            return null;
        }

        Seek(reader, exceptionRva, 56);
        var threadId = reader.ReadUInt32();
        reader.ReadUInt32(); // alignment
        var code = reader.ReadUInt32();
        reader.ReadUInt32(); // ExceptionFlags
        reader.ReadUInt64(); // nested ExceptionRecord
        var address = reader.ReadUInt64();
        var parameterCount = reader.ReadUInt32();
        reader.ReadUInt32(); // alignment
        ulong? accessKind = parameterCount >= 2 ? reader.ReadUInt64() : null;
        ulong? accessAddress = parameterCount >= 2 ? reader.ReadUInt64() : null;

        var modules = streams.TryGetValue(ModuleListStream, out var moduleRva)
            ? ReadModules(reader, moduleRva)
            : [];

        var faulting = modules.FirstOrDefault(module => address >= module.Base && address - module.Base < module.Size);

        return new FiveMCrashDump(
            fileName,
            crashedAt,
            streams.TryGetValue(MiscInfoStream, out var miscRva) ? ReadProcessStart(reader, miscRva) : null,
            code,
            faulting.Name,
            faulting.Name is null ? address : address - faulting.Base,
            accessKind,
            accessAddress,
            streams.TryGetValue(ThreadNamesStream, out var namesRva) ? ReadThreadName(reader, namesRva, threadId) : null,
            modules.Any(module => string.Equals(module.Name, "steamclient64.dll", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<(ulong Base, uint Size, string? Name)> ReadModules(BinaryReader reader, uint rva)
    {
        Seek(reader, rva, 4);
        var count = reader.ReadUInt32();
        if (count > MaxModules)
        {
            throw new InvalidDataException("Orimligt antal moduler.");
        }

        Seek(reader, rva + 4L, (long)count * ModuleSize);
        var raw = new List<(ulong Base, uint Size, uint NameRva)>((int)count);
        for (var index = 0; index < count; index++)
        {
            var start = reader.BaseStream.Position;
            var baseOfImage = reader.ReadUInt64();
            var size = reader.ReadUInt32();
            reader.ReadUInt32(); // CheckSum
            reader.ReadUInt32(); // TimeDateStamp
            var nameRva = reader.ReadUInt32();
            raw.Add((baseOfImage, size, nameRva));
            reader.BaseStream.Position = start + ModuleSize;
        }

        return raw.Select(module => (module.Base, module.Size, (string?)Path.GetFileName(ReadString(reader, module.NameRva)))).ToList();
    }

    /// <summary>
    /// The process's own start time. Only present when the writer included process times, which FiveM's
    /// dumps do.
    /// </summary>
    private static DateTimeOffset? ReadProcessStart(BinaryReader reader, uint rva)
    {
        Seek(reader, rva, 16);
        reader.ReadUInt32(); // SizeOfInfo
        var flags = reader.ReadUInt32();
        reader.ReadUInt32(); // ProcessId
        var created = reader.ReadUInt32();

        return (flags & MiscProcessTimes) != 0 && created != 0 ? DateTimeOffset.FromUnixTimeSeconds(created) : null;
    }

    private static string? ReadThreadName(BinaryReader reader, uint rva, uint threadId)
    {
        Seek(reader, rva, 4);
        var count = reader.ReadUInt32();
        Seek(reader, rva + 4L, (long)count * 12);

        for (var index = 0; index < count; index++)
        {
            var id = reader.ReadUInt32();
            var nameRva = reader.ReadUInt64();
            if (id == threadId)
            {
                return nameRva > uint.MaxValue ? null : ReadString(reader, (uint)nameRva) is { Length: > 0 } name ? name : null;
            }
        }

        return null;
    }

    /// <summary>A MINIDUMP_STRING: a byte length, then that many bytes of UTF-16.</summary>
    private static string ReadString(BinaryReader reader, uint rva)
    {
        Seek(reader, rva, 4);
        var length = reader.ReadUInt32();
        if (length > MaxStringBytes)
        {
            throw new InvalidDataException("Orimligt lång sträng.");
        }

        Seek(reader, rva + 4L, length);
        return Encoding.Unicode.GetString(reader.ReadBytes((int)length));
    }

    private static void Seek(BinaryReader reader, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > reader.BaseStream.Length)
        {
            throw new InvalidDataException("Dumpen är kortare än den säger.");
        }

        reader.BaseStream.Position = offset;
    }
}
