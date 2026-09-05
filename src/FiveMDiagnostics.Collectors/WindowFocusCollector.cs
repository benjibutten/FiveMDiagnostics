using System.Diagnostics;

namespace FiveMDiagnostics.Collectors;

using FiveMDiagnostics.Collectors.Interop;
using FiveMDiagnostics.Core;

/// <summary>
/// Watches which window owns the foreground, so the frames lost behind an alt-tab can be told from the
/// frames lost in play.
/// </summary>
/// <remarks>
/// <para>
/// This is the cheapest collector in the app and the only one that can answer the question. Every other
/// signal — CPU, VRAM, file operations, present mode — reads exactly the same whether the player is
/// driving through the city or reading Discord with the game behind it, and the second case has been
/// counted in every hitch rate this app has ever reported.
/// </para>
/// <para>
/// Polled at <see cref="PollInterval"/> rather than hooked. <c>SetWinEventHook</c> would give the switch
/// to the millisecond, but it needs a message pump on the collector's thread and delivers on the
/// hooking thread's queue, which is the sort of machinery that goes wrong quietly. A hundred
/// milliseconds of a <c>GetForegroundWindow</c> call is under a microsecond of work and bounds the error
/// on both edges of a switch to one poll — which is the resolution the exclusion is stated at.
/// </para>
/// <para>
/// Emits on change and then on a heartbeat. The change is what the session reasons about, but an
/// incident window is built from a ring buffer that drops old events, so a quiet hour of play would
/// otherwise leave an incident with no focus sample in it at all — and "no sample" must not be read as
/// "not in focus".
/// </para>
/// </remarks>
public sealed class WindowFocusCollector : ITelemetryCollector
{
    /// <summary>
    /// How often the foreground is read. Also the accuracy of both edges of a switch, which the summary
    /// line says out loud.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How often an unchanged foreground is written anyway, so every incident window holds one.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Process names by id, because <see cref="Process.GetProcessById(int)"/> opens a handle and this
    /// runs ten times a second against a foreground that changes a few times an hour.
    /// </summary>
    /// <remarks>
    /// Windows reuses process ids, so the cache is dropped whenever it grows past a size no desktop
    /// reaches honestly. A stale name would be attached to a foreground process that is not the one it
    /// describes, and the whole point of carrying the name is that it can be believed.
    /// </remarks>
    private readonly Dictionary<int, string> _namesByProcessId = [];

    private const int MaxCachedNames = 512;

    public string Name => "WindowFocus";

    public async Task RunAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        _namesByProcessId.Clear();

        var lastWritten = DateTimeOffset.MinValue;
        bool? lastFocus = null;
        var lastProcessId = -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            var timestamp = context.UtcNow();
            var target = context.ProcessResolver.TryGetTargetProcess();
            var (foregroundProcessId, foregroundName) = ReadForeground();

            // No game means no answer. Writing "not in focus" while the game is not running would turn
            // every second before it starts into excluded time and make the summary's share of the
            // session meaningless.
            if (target is null || foregroundProcessId <= 0)
            {
                lastFocus = null;
                lastProcessId = -1;
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var hasFocus = foregroundProcessId == target.ProcessId;
            var changed = lastFocus != hasFocus || lastProcessId != foregroundProcessId;

            if (changed || timestamp - lastWritten >= Heartbeat)
            {
                await context.Writer.WriteAsync(
                    new WindowFocusSample(timestamp, hasFocus, foregroundProcessId, foregroundName),
                    cancellationToken).ConfigureAwait(false);

                lastWritten = timestamp;
                lastFocus = hasFocus;
                lastProcessId = foregroundProcessId;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The process owning the foreground window, or <c>(-1, "")</c> when there is none.
    /// </summary>
    /// <remarks>
    /// A zero handle is the ordinary state for a fraction of a second during a switch and for as long as
    /// the workstation is locked. It is reported as "unknown" rather than as "the game lost focus",
    /// because the two have opposite consequences and only one of them is observed here.
    /// </remarks>
    private (int ProcessId, string Name) ReadForeground()
    {
        var window = WindowsInterop.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return (-1, string.Empty);
        }

        if (WindowsInterop.GetWindowThreadProcessId(window, out var processId) == 0 || processId <= 0)
        {
            return (-1, string.Empty);
        }

        return (processId, NameOf(processId));
    }

    private string NameOf(int processId)
    {
        if (_namesByProcessId.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Gone between the foreground read and this call, which is common for the transient windows
            // a shell switch puts up.
            name = $"pid {processId}";
        }

        if (_namesByProcessId.Count >= MaxCachedNames)
        {
            _namesByProcessId.Clear();
        }

        _namesByProcessId[processId] = name;
        return name;
    }
}
