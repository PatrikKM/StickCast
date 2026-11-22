using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace StickCast;

/// <summary>
/// Lightweight Windows notification area (system tray) icon using Win32 Shell_NotifyIconW,
/// avoiding any System.Windows.Forms dependency.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 1;
    private const int WM_COMMAND = 0x0111;
    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int CMD_TOGGLE = 1001;
    private const int CMD_PAUSE_RESUME = 1002;
    private const int CMD_STOP = 1003;
    private const int CMD_MIRACAST = 1004;
    private const int CMD_EXIT = 1005;

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _hIcon;
    private readonly uint _taskbarCreatedMsg;
    private readonly Func<bool> _isWindowVisible;
    private bool _disposed;

    public event Action? ToggleWindowRequested;
    public event Action? PauseResumeRequested;
    public event Action? StopRequested;
    public event Action? MiracastRequested;
    public event Action? ExitRequested;

    public TrayIcon(string tooltip, Func<bool> isWindowVisible)
    {
        _isWindowVisible = isWindowVisible;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        _hIcon = CreateStickCastIcon();

        var p = new HwndSourceParameters("StickCastTrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _hwndSource = new HwndSource(p);
        _hwndSource.AddHook(WndProc);

        AddOrUpdateIcon(NIM_ADD, tooltip);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        AddOrUpdateIcon(NIM_MODIFY, tooltip);
    }

    private void AddOrUpdateIcon(uint message, string tooltip)
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwndSource.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _hIcon,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        };
        Shell_NotifyIconW(message, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AddOrUpdateIcon(NIM_ADD, "StickCast");
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_TRAY)
        {
            int evt = unchecked((int)(lParam.ToInt64() & 0xFFFF));
            if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                ToggleWindowRequested?.Invoke();
                handled = true;
            }
            else if (evt == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        else if (msg == WM_COMMAND)
        {
            int cmd = unchecked((int)(wParam.ToInt64() & 0xFFFF));
            HandleMenuCommand(cmd);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_TOGGLE, _isWindowVisible() ? "Skrýt okno" : "Zobrazit okno");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_PAUSE_RESUME, "Pauza / Pokračovat");
            AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_STOP, "Stop");
            AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_MIRACAST, "Zrcadlení (Win+K)");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_EXIT, "Ukončit");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwndSource.Handle);
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwndSource.Handle, IntPtr.Zero);
            PostMessageW(_hwndSource.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0) HandleMenuCommand((int)cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case CMD_TOGGLE: ToggleWindowRequested?.Invoke(); break;
            case CMD_PAUSE_RESUME: PauseResumeRequested?.Invoke(); break;
            case CMD_STOP: StopRequested?.Invoke(); break;
            case CMD_MIRACAST: MiracastRequested?.Invoke(); break;
            case CMD_EXIT: ExitRequested?.Invoke(); break;
        }
    }

    private static IntPtr CreateStickCastIcon()
    {
        // 16x16 BGRA icon: blue rounded square (#2563EB) with white TV/play glyph
        const int w = 16, h = 16;
        var xor = new byte[w * h * 4];
        var and = new byte[h * 2];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool corner = (x is 0 or 15) && (y is 0 or 15);
                int idx = (y * w + x) * 4;
                if (corner)
                {
                    int andIdx = y * 2 + (x / 8);
                    and[andIdx] |= (byte)(1 << (7 - (x % 8)));
                    continue;
                }

                // White play/cast triangle in center
                bool inPlay = x >= 5 && x <= 11 && y >= 4 && y <= 11 &&
                              Math.Abs(y - 7.5) <= (11 - x) * 0.65;
                if (inPlay)
                {
                    xor[idx + 0] = 255;
                    xor[idx + 1] = 255;
                    xor[idx + 2] = 255;
                    xor[idx + 3] = 255;
                }
                else
                {
                    // VLC Traffic Cone Orange #FF8800 -> B=0, G=136, R=255
                    xor[idx + 0] = 0;
                    xor[idx + 1] = 136;
                    xor[idx + 2] = 255;
                    xor[idx + 3] = 255;
                }
            }
        }

        return CreateIcon(IntPtr.Zero, w, h, 1, 32, and, xor);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwndSource.Handle,
            uID = 1,
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIcon(IntPtr hInstance, int nWidth, int nHeight, byte cPlanes, byte cBitsPixel, byte[] lpbANDbits, byte[] lpbXORbits);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
