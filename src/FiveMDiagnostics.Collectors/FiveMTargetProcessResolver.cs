using System.Runtime.InteropServices;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Core;

public sealed class FiveMTargetProcessResolver : ITargetProcessResolver
{
    private static readonly nint InvalidHandleValue = -1;
    private const uint SnapshotProcessFlag = 0x00000002;
    private static readonly string[] CandidateTokens = ["FiveM", "GTAProcess"];
    private readonly object _sync = new();
    private readonly TimeSpan _activeCacheDuration = TimeSpan.FromMilliseconds(750);
    private readonly TimeSpan _idleCacheDuration = TimeSpan.FromSeconds(3);
    private DateTimeOffset _lastRefreshUtc;
    private TargetProcessInfo? _cached;

    public TargetProcessInfo? TryGetTargetProcess()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var cacheDuration = _cached is null ? _idleCacheDuration : _activeCacheDuration;
            if (now - _lastRefreshUtc <= cacheDuration)
            {
                return _cached;
            }

            _cached = Scan(now);
            _lastRefreshUtc = now;
            return _cached;
        }
    }

    private static TargetProcessInfo? Scan(DateTimeOffset now)
    {
        TargetProcessInfo? bestMatch = null;
        var bestScore = int.MinValue;
        var snapshotHandle = CreateToolhelp32Snapshot(SnapshotProcessFlag, 0);
        if (snapshotHandle == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = ProcessEntry32.Create();
            if (!Process32First(snapshotHandle, ref entry))
            {
                return null;
            }

            do
            {
                if (entry.ProcessId == Environment.ProcessId)
                {
                    continue;
                }

                var processName = Path.GetFileNameWithoutExtension(entry.ExecutableFile);
                if (string.IsNullOrWhiteSpace(processName) || !CandidateTokens.Any(token => processName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var score = Score(processName);
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestMatch = new TargetProcessInfo((int)entry.ProcessId, processName, null, now);
            }
            while (Process32Next(snapshotHandle, ref entry));

            return bestMatch;
        }
        finally
        {
            _ = CloseHandle(snapshotHandle);
        }
    }

    private static int Score(string processName)
    {
        var score = 0;
        if (processName.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (processName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshotHandle, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshotHandle, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        private const int MaxPath = 260;

        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string ExecutableFile;

        public static ProcessEntry32 Create()
        {
            return new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                ExecutableFile = string.Empty,
            };
        }
    }
}