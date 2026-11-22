using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace StickCast.Services;

internal sealed record MonitorChoice(string Label, int OffsetX, int OffsetY, int Width, int Height)
{
    public override string ToString() => Label;
}

internal sealed record AudioChoice(string Label, string? DeviceName)
{
    public override string ToString() => Label;
}

/// <summary>
/// Encapsulates Win32 P/Invoke utilities: monitor enumeration, Miracast Win+K trigger,
/// and optional global media hotkeys (Ctrl+Alt+Space/Left/Right).
/// </summary>
internal static class Win32Helpers
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static List<MonitorChoice> EnumerateMonitors()
    {
        var list = new List<MonitorChoice>
        {
            new("Celá plocha (všechny monitory)", 0, 0, 0, 0),
        };
        int idx = 1;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfoW(hMon, ref mi))
                {
                    int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    bool primary = (mi.dwFlags & 1) != 0;
                    list.Add(new MonitorChoice(
                        $"Monitor {idx++} ({w}×{h}){(primary ? " [hlavní]" : "")}",
                        mi.rcMonitor.Left, mi.rcMonitor.Top, w, h));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    public static bool TrySendWinK(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!window.IsActive || hwnd == IntPtr.Zero || GetForegroundWindow() != hwnd)
            return false;

        const byte VK_LWIN = 0x5B, VK_K = 0x4B;
        const uint KEYUP = 0x0002;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_K, 0, 0, UIntPtr.Zero);
        keybd_event(VK_K, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
        return true;
    }
}

internal sealed class GlobalHotkeyHost : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const int HK_PLAYPAUSE = 9001;
    private const int HK_PREV = 9002;
    private const int HK_NEXT = 9003;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;

    public event Action? PlayPausePressed;
    public event Action? PrevPressed;
    public event Action? NextPressed;

    public GlobalHotkeyHost(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);

            bool ok1 = RegisterHotKey(_hwnd, HK_PLAYPAUSE, MOD_CONTROL | MOD_ALT, (uint)KeyInterop.VirtualKeyFromKey(Key.Space));
            bool ok2 = RegisterHotKey(_hwnd, HK_PREV, MOD_CONTROL | MOD_ALT, (uint)KeyInterop.VirtualKeyFromKey(Key.Left));
            bool ok3 = RegisterHotKey(_hwnd, HK_NEXT, MOD_CONTROL | MOD_ALT, (uint)KeyInterop.VirtualKeyFromKey(Key.Right));
            _registered = ok1 || ok2 || ok3;
        }
        catch { /* global hotkeys are optional */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HK_PLAYPAUSE: PlayPausePressed?.Invoke(); handled = true; break;
                case HK_PREV: PrevPressed?.Invoke(); handled = true; break;
                case HK_NEXT: NextPressed?.Invoke(); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_registered) return;
        _registered = false;
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hwnd, HK_PLAYPAUSE);
                UnregisterHotKey(_hwnd, HK_PREV);
                UnregisterHotKey(_hwnd, HK_NEXT);
            }
            _source?.RemoveHook(WndProc);
        }
        catch { }
    }
}
