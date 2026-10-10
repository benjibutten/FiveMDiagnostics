namespace FiveMDiagnostics.App.Wpf.Services;

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

using FiveMDiagnostics.App.Wpf.Properties;
using FiveMDiagnostics.Core;

/// <summary>Which mark a hotkey asked for.</summary>
public enum HotkeyMark
{
    /// <summary>It stuttered.</summary>
    Stutter,

    /// <summary>It stuttered badly.</summary>
    SevereStutter,
}

/// <summary>
/// System-wide hotkeys for marking a stutter, so the mark can be made while the game has focus.
/// </summary>
/// <remarks>
/// <para>
/// A mark pressed at the moment it stutters is the only reading that carries what the player
/// experienced, and it cannot be made from the app's own window — the game has focus, and alt-tabbing to
/// click a button is itself a stutter.
/// </para>
/// <para>
/// A registration can fail because another program holds the key, and that is reported rather than
/// swallowed. A hotkey nobody knows is dead is worse than no hotkey: the evening is played, nothing is
/// marked, and the silence reads as "it never stuttered".
/// </para>
/// </remarks>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    /// <summary>MOD_NOREPEAT, so holding the key marks once rather than sixty times.</summary>
    private const uint ModNoRepeat = 0x4000;

    private const int StutterId = 0xB101;
    private const int SevereId = 0xB102;

    private HwndSource? _source;
    private readonly List<int> _registered = [];

    /// <summary>Raised on the UI thread when a registered key is pressed.</summary>
    public event EventHandler<HotkeyMark>? Pressed;

    /// <summary>
    /// What happened when the keys were claimed, as a sentence for the window to show. Null when both
    /// were claimed and there is nothing to say.
    /// </summary>
    public string? RegistrationProblem { get; private set; }

    /// <summary>
    /// Claims <paramref name="keys"/> against a window that already exists, releasing any keys an
    /// earlier call claimed. Problems are left in <see cref="RegistrationProblem"/>.
    /// </summary>
    /// <remarks>
    /// The handle has to be a real one, so this belongs after <c>SourceInitialized</c> — a WPF window
    /// has no HWND before that and <c>RegisterHotKey</c> would bind to zero, which registers a
    /// thread-wide hotkey whose messages nothing in this process ever pumps.
    /// </remarks>
    public void Register(nint windowHandle, HotkeyOptions keys)
    {
        if (windowHandle == 0)
        {
            return;
        }

        if (_source is null)
        {
            _source = HwndSource.FromHwnd(windowHandle);
            if (_source is null)
            {
                RegistrationProblem = Strings.HotkeyNoWindow;
                return;
            }

            _source.AddHook(OnMessage);
        }

        Unregister();

        var problems = new[]
        {
            Claim(windowHandle, StutterId, keys.Stutter),
            Claim(windowHandle, SevereId, keys.Severe),
        };

        var said = problems.OfType<string>().ToArray();
        RegistrationProblem = said.Length == 0 ? null : string.Join(" ", said);
    }

    /// <summary>
    /// Reads a key gesture such as <c>Ctrl+Shift+F9</c> or <c>F13</c> into the modifier flags and
    /// virtual key <c>RegisterHotKey</c> takes. False for text that is not a gesture.
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(text.Trim()) is not KeyGesture gesture)
            {
                return false;
            }

            // WPF's ModifierKeys and the MOD_ flags RegisterHotKey takes share their values.
            modifiers = (uint)gesture.Modifiers;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
            return virtualKey != 0;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <returns>A sentence about what went wrong, or null when the key was claimed or none was set.</returns>
    private string? Claim(nint windowHandle, int id, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        if (!TryParse(gesture, out var modifiers, out var virtualKey))
        {
            return string.Format(Strings.HotkeyUnparseableFormat, gesture);
        }

        if (!RegisterHotKey(windowHandle, id, modifiers | ModNoRepeat, virtualKey))
        {
            return string.Format(Strings.HotkeyTakenFormat, gesture);
        }

        _registered.Add(id);
        return null;
    }

    private nint OnMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return 0;
        }

        var mark = (int)wParam switch
        {
            StutterId => HotkeyMark.Stutter,
            SevereId => (HotkeyMark?)HotkeyMark.SevereStutter,
            _ => null,
        };

        if (mark is not { } which)
        {
            return 0;
        }

        handled = true;
        Pressed?.Invoke(this, which);
        return 0;
    }

    private void Unregister()
    {
        if (_source is null)
        {
            return;
        }

        foreach (var id in _registered)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }

        Unregister();
        _source.RemoveHook(OnMessage);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
