using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FiveMDiagnostics.App.Wpf.Services;

using FiveMDiagnostics.Core;

public enum HotKeyAction
{
    MarkStutter = 1,
    MarkSevereStutter = 2,
    ExportCurrentIncident = 3,
}

public sealed class GlobalHotKeyManager : IDisposable
{
    private const int WmHotKey = 0x0312;
    private IntPtr _windowHandle;
    private HwndSource? _source;

    public event EventHandler<HotKeyAction>? Triggered;

    public void Attach(Window window, HotKeyOptions options)
    {
        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        Register((int)HotKeyAction.MarkStutter, options.MarkStutter);
        Register((int)HotKeyAction.MarkSevereStutter, options.MarkSevereStutter);
        Register((int)HotKeyAction.ExportCurrentIncident, options.ExportCurrentIncident);
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, (int)HotKeyAction.MarkStutter);
            UnregisterHotKey(_windowHandle, (int)HotKeyAction.MarkSevereStutter);
            UnregisterHotKey(_windowHandle, (int)HotKeyAction.ExportCurrentIncident);
        }

        _source?.RemoveHook(WndProc);
    }

    private void Register(int identifier, HotKeyBinding binding)
    {
        RegisterHotKey(_windowHandle, identifier, (uint)binding.Modifiers, binding.VirtualKey);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey)
        {
            handled = true;
            Triggered?.Invoke(this, (HotKeyAction)wParam.ToInt32());
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}