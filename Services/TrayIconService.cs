using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicPlayer.Services;

/// <summary>
/// System-tray icon backed by a message-only Win32 window running on its own
/// thread. Left click (or the "打开" menu item) raises <see cref="OpenRequested"/>;
/// "退出" raises <see cref="ExitRequested"/>. Events fire on the tray thread —
/// callers must marshal back to their own UI thread. Re-adds the icon when
/// Explorer restarts ("TaskbarCreated") so the icon never gets lost.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public event Action? OpenRequested;
    public event Action? ExitRequested;

    private const uint WmAppShow = 0x8000 + 1;
    private const uint WmAppHide = 0x8000 + 2;
    private const uint WmAppBalloon = 0x8000 + 3;
    private const uint WmAppQuit = 0x8000 + 4;
    private const uint TrayCallback = 0x8000 + 5;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_NULL = 0x0000;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x01;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const int IMAGE_ICON = 1;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private const int MenuOpen = 1001, MenuExit = 1002;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Native.WndProc _proc;
    private Thread? _thread;
    private volatile IntPtr _hwnd;
    private IntPtr _hIcon;          // tray thread only
    private bool _added;            // tray thread only
    private int _taskbarCreated;    // tray thread only
    private string _iconPath = "";
    private string _tooltip = "";
    private string _balloonTitle = "";
    private string _balloonText = "";

    public TrayIconService() => _proc = WndProc;

    public void Show(string iconPath, string tooltip)
    {
        StartThread();
        _iconPath = iconPath;
        _tooltip = tooltip;
        Post(WmAppShow);
    }

    public void ShowBalloon(string title, string text)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        _balloonTitle = title;
        _balloonText = text;
        Post(WmAppBalloon);
    }

    public void Hide() => Post(WmAppHide);

    public void Dispose()
    {
        var t = _thread;
        if (t == null)
            return;
        _thread = null;
        Post(WmAppQuit);
        t.Join(1500);
    }

    // ---------- tray thread ----------

    private void StartThread()
    {
        if (_thread != null)
            return;
        _thread = new Thread(Run) { Name = "MusicPlayerTray", IsBackground = true };
        _thread.Start();

        // Wait for the message window to exist so posted messages aren't lost.
        for (var i = 0; i < 100 && _hwnd == IntPtr.Zero; i++)
            Thread.Sleep(10);
    }

    private void Post(uint msg)
    {
        var h = _hwnd;
        if (h != IntPtr.Zero)
            Native.PostMessage(h, msg, 0, 0);
    }

    private void Run()
    {
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        var wc = new Native.WNDCLASS
        {
            lpfnWndProc = _proc,
            lpszClassName = "MusicPlayerTrayWnd",
            hInstance = Native.GetModuleHandle(null),
        };
        Native.RegisterClass(ref wc);

        _hwnd = Native.CreateWindowEx(
            0, "MusicPlayerTrayWnd", "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Explorer restarted: the old icon is gone, add it again.
        if (_taskbarCreated != 0 && msg == (uint)_taskbarCreated)
        {
            if (_added)
                _added = false;
            AddOrModifyIcon();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmAppShow:
                AddOrModifyIcon();
                return IntPtr.Zero;

            case WmAppHide:
                RemoveIcon();
                return IntPtr.Zero;

            case WmAppBalloon:
                if (_added)
                    ShowBalloonCore();
                return IntPtr.Zero;

            case WmAppQuit or WM_CLOSE:
                Native.DestroyWindow(hwnd); // WM_DESTROY below does the cleanup
                return IntPtr.Zero;

            case WM_DESTROY:
                RemoveIcon();
                Native.PostQuitMessage(0);
                return IntPtr.Zero;

            case TrayCallback:
                var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouse == WM_LBUTTONUP || mouse == WM_LBUTTONDBLCLK)
                    OpenRequested?.Invoke();
                else if (mouse == WM_RBUTTONUP)
                    ShowContextMenu(hwnd);
                return IntPtr.Zero;

            default:
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void AddOrModifyIcon()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        if (_hIcon == IntPtr.Zero && !string.IsNullOrEmpty(_iconPath))
        {
            var cx = Native.GetSystemMetrics(SM_CXSMICON);
            var cy = Native.GetSystemMetrics(SM_CYSMICON);
            _hIcon = Native.LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
        }
        if (_hIcon == IntPtr.Zero)
            return;

        var data = new Native.NOTIFYICONDATA
        {
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallback,
            hIcon = _hIcon,
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        };
        if (Native.Shell_NotifyIcon(_added ? NIM_MODIFY : NIM_ADD, ref data))
            _added = true;
    }

    private void ShowBalloonCore()
    {
        var data = new Native.NOTIFYICONDATA
        {
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfo = _balloonText.Length > 255 ? _balloonText[..255] : _balloonText,
            szInfoTitle = _balloonTitle.Length > 63 ? _balloonTitle[..63] : _balloonTitle,
            dwInfoFlags = NIIF_INFO,
        };
        Native.Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (!_added)
            return;
        var data = new Native.NOTIFYICONDATA { hWnd = _hwnd, uID = 1 };
        Native.Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;

        if (_hIcon != IntPtr.Zero)
        {
            Native.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    private void ShowContextMenu(IntPtr hwnd)
    {
        Native.GetCursorPos(out var pt);
        // Required so the menu also dismisses when clicking elsewhere.
        Native.SetForegroundWindow(hwnd);

        var menu = Native.CreatePopupMenu();
        Native.AppendMenu(menu, 0, MenuOpen, "打开 MusicPlayer");
        Native.AppendMenu(menu, MF_SEPARATOR, 0, "-");
        Native.AppendMenu(menu, 0, MenuExit, "退出");

        var cmd = Native.TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);
        Native.PostMessage(hwnd, WM_NULL, 0, 0);

        if (cmd == MenuOpen)
            OpenRequested?.Invoke();
        else if (cmd == MenuExit)
            ExitRequested?.Invoke();
    }

    // ---------- Win32 ----------

    private static class Native
    {
        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClass(ref WNDCLASS wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr menu, uint flags, int id, string text);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenu(
            IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImage(IntPtr inst, string name, int type, int cx, int cy, uint load);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? name);
    }
}
