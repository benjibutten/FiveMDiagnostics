namespace FiveMDiagnostics.App.Wpf.Services;

using System.Runtime.InteropServices;
using System.Windows.Interop;

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
/// The investigation's standing problem is that its main measurement does not measure what is
/// experienced: seven evenings between 33 and 97 hitches per hour were all described as fine, and the
/// evening that was visibly bad on stream had an ordinary hitch rate and an extraordinary tail. A mark
/// pressed at the moment it stutters is the only reading that carries the experience, and it cannot be
/// made from the app's own window — the game has focus, and alt-tabbing to click a button is itself a
/// stutter.
/// </para>
/// <para>
/// F13 and F14 are chosen because no keyboard has them. They exist in the virtual key table, Windows
/// routes them, a stream deck can be told to send them, and nothing on the machine produces them by
/// accident — which matters for a key that spends a few hundred megabytes of trace when it is pressed.
/// Registered without modifiers for the same reason: a stream deck sends one key, and a combination
/// would have to be held.
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

    /// <summary>VK_F13 and VK_F14.</summary>
    private const uint VkF13 = 0x7C;
    private const uint VkF14 = 0x7D;

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
    /// Claims the keys against a window that already exists.
    /// </summary>
    /// <remarks>
    /// The handle has to be a real one, so this belongs after <c>SourceInitialized</c> — a WPF window
    /// has no HWND before that and <c>RegisterHotKey</c> would bind to zero, which registers a
    /// thread-wide hotkey whose messages nothing in this process ever pumps.
    /// </remarks>
    public void Register(nint windowHandle)
    {
        if (_source is not null || windowHandle == 0)
        {
            return;
        }

        _source = HwndSource.FromHwnd(windowHandle);
        if (_source is null)
        {
            RegistrationProblem = "Snabbtangenterna kunde inte kopplas till fönstret.";
            return;
        }

        _source.AddHook(OnMessage);

        var failed = new List<string>();

        if (RegisterHotKey(windowHandle, StutterId, ModNoRepeat, VkF13))
        {
            _registered.Add(StutterId);
        }
        else
        {
            failed.Add("F13");
        }

        if (RegisterHotKey(windowHandle, SevereId, ModNoRepeat, VkF14))
        {
            _registered.Add(SevereId);
        }
        else
        {
            failed.Add("F14");
        }

        RegistrationProblem = failed.Count == 0
            ? null
            : $"Snabbtangent {string.Join(" och ", failed)} kunde inte registreras — någon annan app "
                + "håller den. Markering från StreamDeck fungerar inte förrän den släpps.";
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

    public void Dispose()
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
