using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicPlayer.Services;

/// <summary>Snapshot of app state the right-click menu renders. The host
/// sets <see cref="TrayIconService.MenuStateProvider"/>; it is invoked on the
/// tray thread right before the menu opens (plain field reads only).</summary>
public sealed class TrayMenuState
{
    public bool IsPlaying;
    public string TrackTitle = "";
    public bool Favorite;
    public int VolumePercent;
    public bool DesktopLyricsOn;
    public bool DynamicVolume;
    public PlayMode PlayMode;
    /// <summary>Match the main window theme so the panel blends in.</summary>
    public bool DarkTheme;
    /// <summary>Accent color as "#RRGGBB" — drives the slider, switch and
    /// the active states of the transport buttons.</summary>
    public string AccentColor = "#8F0FC2";
}

public enum TrayMenuCommand
{
    Previous, PlayPause, Next, ToggleFavorite, ToggleDesktopLyrics,
    TogglePlayMode, ToggleDynamicVolume, Settings, WallpaperSettings, Open,
}

/// <summary>
/// System-tray icon backed by a hidden top-level Win32 window running on its
/// own thread. Left click raises <see cref="OpenRequested"/>; the panel's
/// 退出 row raises <see cref="ExitRequested"/>. Events fire on the tray
/// thread — callers must marshal back to their own UI thread. Re-adds the
/// icon when Explorer restarts ("TaskbarCreated") so it never gets lost.
///
/// The right-click menu is a QQ-Music-style card drawn entirely with GDI on
/// its own popup window: a transport row (circular previous / play / next),
/// the current title with favourite + play-mode buttons, a dynamic-volume
/// switch row, a live volume slider, the desktop-lyrics rows, settings and
/// the red exit row. The panel follows the host theme, uses the configured
/// accent colour, stays open while transport/toggle rows are used (like QQ
/// Music) and dismisses on focus loss.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public event Action? OpenRequested;
    public event Action? ExitRequested;

    /// <summary>Provides the state snapshot when the panel opens.</summary>
    public Func<TrayMenuState>? MenuStateProvider;

    /// <summary>Raised on the tray thread when the user picks a command;
    /// the host marshals to its UI thread.</summary>
    public event Action<TrayMenuCommand>? MenuCommand;

    /// <summary>Live volume from the panel slider (0-100), raised while the
    /// thumb is dragged.</summary>
    public event Action<int>? VolumeSet;

    private const uint WmAppShow = 0x8000 + 1;
    private const uint WmAppHide = 0x8000 + 2;
    private const uint WmAppBalloon = 0x8000 + 3;
    private const uint WmAppQuit = 0x8000 + 4;
    private const uint WmAppRefresh = 0x8000 + 6;
    private const uint TrayCallback = 0x8000 + 5;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_CAPTURECHANGED = 0x0215;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint CS_DROPSHADOW = 0x00020000;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private const int IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x01;

    private const int TimerRefresh = 1;
    private const uint WmAppDismiss = 0x8000 + 7;
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;

    // DrawText formats.
    private const uint DT_LEFT = 0x0000, DT_CENTER = 0x0001, DT_VCENTER = 0x0004,
        DT_SINGLELINE = 0x0020, DT_END_ELLIPSIS = 0x8000, DT_NOPREFIX = 0x0800;

    // GDI stock objects.
    private const int NULL_BRUSH = 5, NULL_PEN = 8;

    private const uint SRCCOPY = 0x00CC0020;

    private readonly Native.WndProc _proc;
    private readonly Native.WndProc _menuProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private string _iconPath = "";
    private string _tooltip = "";
    private string _balloonTitle = "";
    private string _balloonText = "";
    private IntPtr _hIcon;
    private bool _added;
    private int _taskbarCreated;

    // ---------- panel state (tray thread only) ----------

    private IntPtr _menuHwnd;
    private TrayMenuState _state = new();
    private bool _dark;
    private uint _accent;
    private float _scale = 1f;
    private uint _dpi = 96;

    private int _menuW, _menuH;
    private IntPtr _backDc, _backBmp, _backOld;
    private int _backW, _backH;

    private IntPtr _fontRow, _fontTitle, _fontGlyph, _fontGlyphBig, _fontWord;
    private uint _fontDpi;

    private Hot _hot, _pressed;
    private bool _dragging;
    private long _lastHiddenTick;
    private IntPtr _mouseHook, _keyboardHook;
    private Native.HookProc? _mouseProc, _keyProc;

    private Native.RECT _rcPrev, _rcPlay, _rcNext, _rcFavorite, _rcMode, _rcTitle;
    private Native.RECT _rcToggleRow, _rcSwitch, _rcVolumeRow, _rcSpeaker;
    private Native.RECT _rcLyricsRow, _rcDesktopRow, _rcSettingsRow, _rcOpenRow, _rcExitRow;
    private int _sliderX0, _sliderX1, _sliderY;

    private enum Hot
    {
        None, Previous, Play, Next, Favorite, Mode, ToggleRow, Slider,
        LyricsRow, DesktopRow, SettingsRow, OpenRow, ExitRow,
    }

    public TrayIconService()
    {
        _proc = WndProc;
        _menuProc = MenuWndProc;
    }

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

    /// <summary>Re-reads the host state and repaints the panel if it is open.
    /// Called by the host after a menu command has been applied (any thread).</summary>
    public void RefreshState() => Post(WmAppRefresh);

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

        var menuWc = new Native.WNDCLASS
        {
            // Drop shadow makes the card read as a floating layer, like the
            // QQ Music tray panel.
            style = CS_DROPSHADOW,
            lpfnWndProc = _menuProc,
            lpszClassName = "MusicPlayerTrayMenuWnd",
            hInstance = Native.GetModuleHandle(null),
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)32512 /* IDC_ARROW */),
        };
        Native.RegisterClass(ref menuWc);

        // Hidden top-level window (see WS_OVERLAPPED above): never shown, but
        // it receives broadcasts and can take the foreground.
        _hwnd = Native.CreateWindowEx(
            0, "MusicPlayerTrayWnd", "", 0x00000000 /* WS_OVERLAPPED */, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        UninstallDismissHooks();
        if (_menuHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_menuHwnd);
            _menuHwnd = IntPtr.Zero;
        }
        ReleaseBackBuffer();
        ReleaseFonts();
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Explorer restarted: the old icon is gone, add it again.
        if (_taskbarCreated != 0 && msg == (uint)_taskbarCreated)
        {
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
                HidePanel();
                RemoveIcon();
                return IntPtr.Zero;

            case WmAppBalloon:
                if (_added)
                    ShowBalloonCore();
                return IntPtr.Zero;

            case WmAppRefresh:
                if (_menuHwnd != IntPtr.Zero && Native.IsWindowVisible(_menuHwnd) && !_dragging)
                {
                    ReloadState();
                    InvalidatePanel();
                }
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
                {
                    HidePanel();
                    OpenRequested?.Invoke();
                }
                else if (mouse == WM_RBUTTONUP)
                {
                    ShowPanel();
                }
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
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(),
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
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(),
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
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Native.Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;

        if (_hIcon != IntPtr.Zero)
        {
            Native.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    // ---------- panel window ----------

    private void EnsurePanelWindow()
    {
        if (_menuHwnd != IntPtr.Zero)
            return;
        _menuHwnd = Native.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, "MusicPlayerTrayMenuWnd", "", WS_POPUP,
            0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (_menuHwnd == IntPtr.Zero)
            return;

        // Win11 rounded corners for the borderless card; ignored on Win10.
        int pref = 2 /* DWMWCP_ROUND */;
        Native.DwmSetWindowAttribute(_menuHwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, 4);
    }

    private void ShowPanel()
    {
        if (_menuHwnd != IntPtr.Zero && Native.IsWindowVisible(_menuHwnd))
        {
            HidePanel();
            return;
        }

        // A click on the tray icon while the panel is open deactivates (and
        // therefore hides) it before the callback arrives; don't immediately
        // reopen in that case so right-click still acts as a toggle.
        if (Environment.TickCount64 - _lastHiddenTick < 220)
            return;

        EnsurePanelWindow();
        if (_menuHwnd == IntPtr.Zero)
            return;

        Native.GetCursorPos(out var pt);
        _dpi = GetDpiAt(pt);
        _scale = _dpi / 96f;
        EnsureFonts();
        ReloadState();
        LayoutPanel();

        var monitor = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(Native.MonitorFromPoint(pt, 2 /* NEAREST */), ref monitor);
        var wa = monitor.rcWork;
        int margin = S(8), gap = S(10);
        int x = Math.Clamp(pt.X - _menuW / 2, wa.Left + margin,
            Math.Max(wa.Left + margin, wa.Right - _menuW - margin));
        int y = pt.Y - _menuH - gap;
        if (y < wa.Top + margin)
            y = pt.Y + gap;
        y = Math.Clamp(y, wa.Top + margin,
            Math.Max(wa.Top + margin, wa.Bottom - _menuH - margin));

        _hot = Hot.None;
        _pressed = Hot.None;
        _dragging = false;

        Native.MoveWindow(_menuHwnd, x, y, _menuW, _menuH, false);
        // Match the painted radius exactly: DWM's own rounding uses a fixed
        // radius and would leave the panel's rounded outline floating inside
        // a squarer clip at high DPI. SetWindowRgn takes ownership.
        var rgn = Native.CreateRoundRectRgn(0, 0, _menuW + 1, _menuH + 1, S(20), S(20));
        if (rgn != IntPtr.Zero)
            Native.SetWindowRgn(_menuHwnd, rgn, false);
        // Deliberately NOT activating: Explorer reclaims the foreground right
        // after a tray callback, and activation-based dismissal made the panel
        // vanish instantly. An unactivated panel plus a low-level mouse hook
        // behaves like a real tray menu and never steals focus.
        Native.ShowWindow(_menuHwnd, SW_SHOWNOACTIVATE);
        InstallDismissHooks();
        InvalidatePanel();
    }

    private void HidePanel()
    {
        if (_menuHwnd == IntPtr.Zero)
            return;
        if (_dragging)
        {
            Native.ReleaseCapture();
            _dragging = false;
        }
        _hot = Hot.None;
        _pressed = Hot.None;
        _lastHiddenTick = Environment.TickCount64;
        Native.KillTimer(_menuHwnd, TimerRefresh);
        UninstallDismissHooks();
        Native.ShowWindow(_menuHwnd, SW_HIDE);
    }

    // Low-level hooks live only while the panel is open. The panel never
    // takes activation, so outside clicks are detected here instead of via
    // focus loss (which Explorer steals back immediately after a tray click).
    private void InstallDismissHooks()
    {
        _mouseProc ??= MouseHookProc;
        _keyProc ??= KeyboardHookProc;
        var inst = Native.GetModuleHandle(null);
        if (_mouseHook == IntPtr.Zero)
            _mouseHook = Native.SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, inst, 0);
        if (_keyboardHook == IntPtr.Zero)
            _keyboardHook = Native.SetWindowsHookExW(WH_KEYBOARD_LL, _keyProc, inst, 0);
    }

    private void UninstallDismissHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Any exception escaping a low-level hook kills the process (the
        // callback has no managed frame above it), so stay defensive.
        try
        {
            if (nCode >= 0 && _menuHwnd != IntPtr.Zero && Native.IsWindowVisible(_menuHwnd))
            {
                uint msg = (uint)wParam.ToInt64();
                if (msg == WM_LBUTTONDOWN || msg == 0x0204 /* WM_RBUTTONDOWN */ || msg == 0x0207 /* WM_MBUTTONDOWN */)
                {
                    var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    Native.GetWindowRect(_menuHwnd, out var rc);
                    bool inside = info.pt.X >= rc.Left && info.pt.X < rc.Right
                        && info.pt.Y >= rc.Top && info.pt.Y < rc.Bottom;
                    if (!inside)
                        Native.PostMessage(_menuHwnd, WmAppDismiss, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        catch
        {
            // ignore; dismissal is best-effort
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam.ToInt64() == WM_KEYDOWN && _menuHwnd != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if (info.vkCode == 27 /* VK_ESCAPE */)
                    Native.PostMessage(_menuHwnd, WmAppDismiss, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // ignore; Esc dismissal is best-effort
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void ReloadState()
    {
        _state = MenuStateProvider?.Invoke() ?? new TrayMenuState();
        _dark = _state.DarkTheme;
        _accent = ParseColor(_state.AccentColor, 0x8F0FC2);
    }

    private static uint GetDpiAt(Native.POINT pt)
    {
        var under = Native.WindowFromPoint(pt);
        var dpi = under != IntPtr.Zero ? Native.GetDpiForWindow(under) : 96;
        return dpi < 96 ? 96 : Math.Min(dpi, 480);
    }

    private void InvalidatePanel()
    {
        if (_menuHwnd != IntPtr.Zero)
            Native.InvalidateRect(_menuHwnd, IntPtr.Zero, false);
    }

    private IntPtr MenuWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return (IntPtr)1;

            case WM_PAINT:
                try
                {
                    PaintPanel(hwnd);
                }
                catch (Exception ex)
                {
                    AppLog.WriteLyricCompletion($"托盘菜单绘制失败: {ex.Message}");
                }
                Native.ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                OnMouseMove(GetX(lParam), GetY(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                OnMouseDown(GetX(lParam), GetY(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnMouseUp(GetX(lParam), GetY(lParam));
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                HidePanel();
                return IntPtr.Zero;

            case WM_SETCURSOR:
                if ((lParam.ToInt64() & 0xFFFF) == 1 /* HTCLIENT */)
                {
                    Native.GetCursorPos(out var pt);
                    Native.ScreenToClient(hwnd, ref pt);
                    var hot = HitTest(pt.X, pt.Y);
                    Native.SetCursor(Native.LoadCursorW(IntPtr.Zero,
                        (IntPtr)(hot == Hot.None ? 32512 /* IDC_ARROW */ : 32649 /* IDC_HAND */)));
                    return (IntPtr)1;
                }
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);

            case WM_KEYDOWN:
                if ((int)wParam == 27 /* VK_ESCAPE */)
                    HidePanel();
                return IntPtr.Zero;

            case WM_CAPTURECHANGED:
                if (_dragging)
                {
                    _dragging = false;
                    InvalidatePanel();
                }
                return IntPtr.Zero;

            case WM_TIMER:
                OnTimer(hwnd, (int)wParam);
                return IntPtr.Zero;

            case WmAppDismiss:
                HidePanel();
                return IntPtr.Zero;

            case WM_DESTROY:
                if (hwnd == _menuHwnd)
                    _menuHwnd = IntPtr.Zero;
                return IntPtr.Zero;

            default:
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void OnTimer(IntPtr hwnd, int id)
    {
        if (id == TimerRefresh)
        {
            Native.KillTimer(hwnd, TimerRefresh);
            if (!_dragging && Native.IsWindowVisible(hwnd))
            {
                ReloadState();
                InvalidatePanel();
            }
            return;
        }

    }

    private static int GetX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int GetY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private void OnMouseMove(int x, int y)
    {
        if (_dragging)
        {
            SetVolumeFromX(x);
            return;
        }
        var hot = HitTest(x, y);
        if (hot != _hot)
        {
            _hot = hot;
            InvalidatePanel();
        }
    }

    private void OnMouseDown(int x, int y)
    {
        if (HitTest(x, y) == Hot.Slider)
        {
            _dragging = true;
            Native.SetCapture(_menuHwnd);
            SetVolumeFromX(x);
            return;
        }
        _pressed = HitTest(x, y);
        InvalidatePanel();
    }

    private void OnMouseUp(int x, int y)
    {
        if (_dragging)
        {
            _dragging = false;
            Native.ReleaseCapture();
            InvalidatePanel();
            return;
        }

        var pressed = _pressed;
        _pressed = Hot.None;
        if (pressed != Hot.None && HitTest(x, y) == pressed)
            Execute(pressed);
        else
            InvalidatePanel();
    }

    private void SetVolumeFromX(int x)
    {
        int span = Math.Max(1, _sliderX1 - _sliderX0);
        int pct = (int)Math.Round((x - _sliderX0) * 100.0 / span);
        pct = Math.Clamp(pct, 0, 100);
        if (pct == _state.VolumePercent)
        {
            InvalidatePanel();
            return;
        }
        _state.VolumePercent = pct;
        try
        {
            VolumeSet?.Invoke(pct);
        }
        catch (Exception ex)
        {
            AppLog.WriteLyricCompletion($"托盘音量设置失败: {ex.Message}");
        }
        InvalidatePanel();
    }

    private void Execute(Hot hot)
    {
        switch (hot)
        {
            case Hot.Previous:
                RaiseCommand(TrayMenuCommand.Previous);
                break;
            case Hot.Play:
                _state.IsPlaying = !_state.IsPlaying; // optimistic; refreshed below
                RaiseCommand(TrayMenuCommand.PlayPause);
                break;
            case Hot.Next:
                RaiseCommand(TrayMenuCommand.Next);
                break;
            case Hot.Favorite:
                _state.Favorite = !_state.Favorite;
                RaiseCommand(TrayMenuCommand.ToggleFavorite);
                break;
            case Hot.Mode:
                _state.PlayMode = NextMode(_state.PlayMode);
                RaiseCommand(TrayMenuCommand.TogglePlayMode);
                break;
            case Hot.ToggleRow:
                _state.DynamicVolume = !_state.DynamicVolume;
                RaiseCommand(TrayMenuCommand.ToggleDynamicVolume);
                break;
            case Hot.LyricsRow:
                _state.DesktopLyricsOn = !_state.DesktopLyricsOn;
                RaiseCommand(TrayMenuCommand.ToggleDesktopLyrics);
                break;
            case Hot.DesktopRow:
                HidePanel();
                RaiseCommand(TrayMenuCommand.WallpaperSettings);
                return;
            case Hot.SettingsRow:
                HidePanel();
                RaiseCommand(TrayMenuCommand.Settings);
                return;
            case Hot.OpenRow:
                HidePanel();
                RaiseCommand(TrayMenuCommand.Open);
                return;
            case Hot.ExitRow:
                HidePanel();
                try
                {
                    ExitRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    AppLog.WriteLyricCompletion($"托盘退出失败: {ex.Message}");
                }
                return;
            default:
                return;
        }

        InvalidatePanel();
        // Host state settles asynchronously (play/pause especially), so
        // re-read a moment later and let the icon catch up.
        Native.SetTimer(_menuHwnd, TimerRefresh, 300, IntPtr.Zero);
    }

    private void RaiseCommand(TrayMenuCommand cmd)
    {
        try
        {
            MenuCommand?.Invoke(cmd);
        }
        catch (Exception ex)
        {
            AppLog.WriteLyricCompletion($"托盘菜单命令失败: {ex.Message}");
        }
    }

    private static PlayMode NextMode(PlayMode mode) => mode switch
    {
        PlayMode.Sequential => PlayMode.LoopAll,
        PlayMode.LoopAll => PlayMode.LoopOne,
        PlayMode.LoopOne => PlayMode.Random,
        _ => PlayMode.Sequential,
    };

    private static char ModeGlyph(PlayMode mode) => mode switch
    {
        PlayMode.LoopAll => '\uE8EE',
        PlayMode.LoopOne => '\uE8ED',
        PlayMode.Random => '\uE8B1',
        _ => '\uE8FD',
    };

    // ---------- layout ----------

    private int S(int logical) => (int)Math.Round(logical * _scale);

    private static Native.RECT R(int l, int t, int r, int b) =>
        new() { Left = l, Top = t, Right = r, Bottom = b };

    private void LayoutPanel()
    {
        _menuW = S(300);
        _menuH = S(422);

        const int side = 40, play = 56, gap = 30, left = 52;
        _rcPrev = R(S(left), S(26), S(left + side), S(66));
        _rcPlay = R(S(left + side + gap), S(18), S(left + side + gap + play), S(74));
        _rcNext = R(S(left + side + gap + play + gap), S(26), S(left + side + gap + play + gap + side), S(66));

        _rcFavorite = R(S(12), S(78), S(44), S(110));
        _rcTitle = R(S(48), S(78), S(252), S(110));
        _rcMode = R(S(256), S(78), S(288), S(110));

        _rcToggleRow = R(0, S(112), _menuW, S(164));
        _rcSwitch = R(S(240), S(127), S(280), S(149));
        _rcVolumeRow = R(0, S(164), _menuW, S(208));
        _rcSpeaker = R(S(14), S(168), S(46), S(204));
        _sliderX0 = S(52);
        _sliderX1 = S(280);
        _sliderY = S(186);

        _rcLyricsRow = R(0, S(208), _menuW, S(248));
        _rcDesktopRow = R(0, S(248), _menuW, S(282));
        _rcSettingsRow = R(0, S(282), _menuW, S(328));
        _rcOpenRow = R(0, S(328), _menuW, S(372));
        _rcExitRow = R(0, S(372), _menuW, S(416));
    }

    private Hot HitTest(int x, int y)
    {
        if (InRect(_rcPrev, x, y)) return Hot.Previous;
        if (InRect(_rcPlay, x, y)) return Hot.Play;
        if (InRect(_rcNext, x, y)) return Hot.Next;
        if (InRect(_rcFavorite, x, y)) return Hot.Favorite;
        if (InRect(_rcMode, x, y)) return Hot.Mode;
        if (x >= _sliderX0 - S(10) && x <= _sliderX1 + S(10)
            && y >= _sliderY - S(14) && y <= _sliderY + S(14))
            return Hot.Slider;
        if (InRect(_rcToggleRow, x, y)) return Hot.ToggleRow;
        if (InRect(_rcLyricsRow, x, y)) return Hot.LyricsRow;
        if (InRect(_rcDesktopRow, x, y)) return Hot.DesktopRow;
        if (InRect(_rcSettingsRow, x, y)) return Hot.SettingsRow;
        if (InRect(_rcOpenRow, x, y)) return Hot.OpenRow;
        if (InRect(_rcExitRow, x, y)) return Hot.ExitRow;
        return Hot.None;
    }

    private static bool InRect(Native.RECT rc, int x, int y) =>
        x >= rc.Left && x < rc.Right && y >= rc.Top && y < rc.Bottom;

    // ---------- painting ----------

    private uint ColCard => _dark ? Rgb(43, 43, 49) : Rgb(255, 255, 255);
    private uint ColBorder => _dark ? Rgb(58, 58, 66) : Rgb(236, 236, 240);
    private uint ColDivider => _dark ? Rgb(55, 55, 62) : Rgb(239, 239, 242);
    private uint ColHover => _dark ? Rgb(58, 58, 66) : Rgb(244, 244, 247);
    private uint ColText => _dark ? Rgb(236, 236, 241) : Rgb(33, 33, 37);
    private uint ColSecondary => _dark ? Rgb(158, 158, 168) : Rgb(150, 150, 160);
    private uint ColIcon => _dark ? Rgb(203, 203, 212) : Rgb(92, 92, 102);
    private uint ColCircleFill => _dark ? Rgb(47, 47, 54) : Rgb(255, 255, 255);
    private uint ColCircleHover => _dark ? Rgb(61, 61, 70) : Rgb(246, 246, 249);
    private uint ColCircleBorder => _dark ? Rgb(85, 85, 95) : Rgb(208, 208, 216);
    private uint ColSwitchOff => _dark ? Rgb(76, 76, 86) : Rgb(217, 217, 223);
    private uint ColTrackOff => _dark ? Rgb(75, 75, 85) : Rgb(229, 229, 234);
    private uint ColKnob => Rgb(255, 255, 255);
    private uint ColExit => _dark ? Rgb(255, 108, 108) : Rgb(223, 69, 74);

    private static uint Rgb(uint r, uint g, uint b) => (b << 16) | (g << 8) | r;

    private void EnsureBackBuffer(int w, int h)
    {
        if (_backDc != IntPtr.Zero && _backW == w && _backH == h)
            return;
        ReleaseBackBuffer();

        var screen = Native.GetDC(IntPtr.Zero);
        _backDc = Native.CreateCompatibleDC(screen);
        _backBmp = Native.CreateCompatibleBitmap(screen, w, h);
        _backOld = Native.SelectObject(_backDc, _backBmp);
        _backW = w;
        _backH = h;
        Native.ReleaseDC(IntPtr.Zero, screen);
    }

    private void ReleaseBackBuffer()
    {
        if (_backDc != IntPtr.Zero)
        {
            if (_backOld != IntPtr.Zero)
                Native.SelectObject(_backDc, _backOld);
            if (_backBmp != IntPtr.Zero)
                Native.DeleteObject(_backBmp);
            Native.DeleteDC(_backDc);
        }
        _backDc = _backBmp = _backOld = IntPtr.Zero;
        _backW = _backH = 0;
    }

    private void PaintPanel(IntPtr hwnd)
    {
        Native.GetClientRect(hwnd, out var client);
        int w = Math.Max(1, client.Right);
        int h = Math.Max(1, client.Bottom);
        EnsureBackBuffer(w, h);
        var hdc = _backDc;
        Native.SetBkMode(hdc, 1 /* TRANSPARENT */);

        var full = R(0, 0, w, h);
        using (var bg = new Native.GdiBrush(ColCard))
            Native.FillRect(hdc, ref full, bg.Handle);

        // ---- transport ----
        DrawCircleButton(hdc, _rcPrev, '\uE892', _fontGlyph, _hot == Hot.Previous);
        DrawCircleButton(hdc, _rcPlay,
            _state.IsPlaying ? '\uE769' : '\uE768', _fontGlyphBig, _hot == Hot.Play);
        DrawCircleButton(hdc, _rcNext, '\uE893', _fontGlyph, _hot == Hot.Next);

        // ---- title row ----
        DrawHoverCircle(hdc, _rcFavorite, _hot == Hot.Favorite);
        DrawGlyph(hdc, _rcFavorite,
            _state.Favorite ? '\uEB52' : '\uEB51',
            _state.Favorite || _hot == Hot.Favorite ? _accent : ColIcon, _fontGlyph);

        string title = string.IsNullOrEmpty(_state.TrackTitle) ? "未在播放" : _state.TrackTitle;
        DrawText(hdc, title, _fontTitle,
            string.IsNullOrEmpty(_state.TrackTitle) ? ColSecondary : ColText,
            _rcTitle, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);

        DrawHoverCircle(hdc, _rcMode, _hot == Hot.Mode);
        DrawGlyph(hdc, _rcMode, ModeGlyph(_state.PlayMode),
            _hot == Hot.Mode ? _accent : ColIcon, _fontGlyph);

        // ---- dynamic volume row ----
        DrawDivider(hdc, S(112), w);
        if (_hot == Hot.ToggleRow)
            DrawRowHover(hdc, _rcToggleRow);
        DrawText(hdc, "动态音量（响度均衡）", _fontRow, ColText,
            R(S(20), _rcToggleRow.Top, S(230), _rcToggleRow.Bottom),
            DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        DrawSwitch(hdc, _rcSwitch, _state.DynamicVolume);

        // ---- volume row ----
        DrawDivider(hdc, S(164), w);
        DrawGlyph(hdc, _rcSpeaker, _state.VolumePercent == 0 ? '\uE74F' : '\uE767',
            _hot == Hot.Slider ? _accent : ColIcon, _fontGlyph);
        DrawSlider(hdc);

        // ---- desktop lyrics ----
        DrawDivider(hdc, S(208), w);
        if (_hot == Hot.LyricsRow)
            DrawRowHover(hdc, _rcLyricsRow);
        DrawText(hdc, "词", _fontWord, _state.DesktopLyricsOn ? _accent : ColText,
            R(S(18), _rcLyricsRow.Top, S(44), _rcLyricsRow.Bottom),
            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        DrawText(hdc, "显示桌面歌词", _fontRow, ColText,
            R(S(48), _rcLyricsRow.Top, w - S(20), _rcLyricsRow.Bottom),
            DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        if (_hot == Hot.DesktopRow)
            DrawRowHover(hdc, _rcDesktopRow);
        DrawText(hdc, "动态桌面", _fontRow, ColText,
            R(S(48), _rcDesktopRow.Top, w - S(20), _rcDesktopRow.Bottom),
            DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        // ---- settings ----
        DrawDivider(hdc, S(282), w);
        if (_hot == Hot.SettingsRow)
            DrawRowHover(hdc, _rcSettingsRow);
        DrawGlyph(hdc, R(S(14), S(289), S(46), S(321)), '\uE713', ColIcon, _fontGlyph);
        DrawText(hdc, "设置", _fontRow, ColText,
            R(S(48), _rcSettingsRow.Top, w - S(20), _rcSettingsRow.Bottom),
            DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        // ---- open / exit ----
        DrawDivider(hdc, S(328), w);
        if (_hot == Hot.OpenRow)
            DrawRowHover(hdc, _rcOpenRow);
        DrawText(hdc, "打开 MusicPlayer", _fontRow, ColText,
            R(S(48), _rcOpenRow.Top, w - S(20), _rcOpenRow.Bottom),
            DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        DrawDivider(hdc, S(372), w);
        if (_hot == Hot.ExitRow)
            DrawRowHover(hdc, _rcExitRow);
        DrawGlyph(hdc, R(S(14), S(379), S(46), S(411)), '\uE783', ColExit, _fontGlyph);
        DrawText(hdc, "退出 MusicPlayer", _fontRow, ColExit,
            R(S(48), _rcExitRow.Top, w - S(20), _rcExitRow.Bottom),
            DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        // ---- card border on top ----
        using (var pen = new Native.GdiPen(ColBorder, _dpi, 1f))
        {
            IntPtr prevBrush = Native.SelectObject(hdc, Native.GetStockObject(NULL_BRUSH));
            IntPtr prevPen = Native.SelectObject(hdc, pen.Handle);
            Native.RoundRect(hdc, 0, 0, w, h, S(22), S(22));
            Native.SelectObject(hdc, prevPen);
            Native.SelectObject(hdc, prevBrush);
        }

        var win = Native.GetDC(hwnd);
        Native.BitBlt(win, 0, 0, w, h, hdc, 0, 0, SRCCOPY);
        Native.ReleaseDC(hwnd, win);
    }

    private void DrawCircleButton(IntPtr hdc, Native.RECT rc, char glyph, IntPtr font, bool hot)
    {
        using (var fill = new Native.GdiBrush(hot ? ColCircleHover : ColCircleFill))
        using (var pen = new Native.GdiPen(hot ? _accent : ColCircleBorder, _dpi, 1.4f))
        {
            IntPtr pb = Native.SelectObject(hdc, fill.Handle);
            IntPtr pp = Native.SelectObject(hdc, pen.Handle);
            Native.Ellipse(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom);
            Native.SelectObject(hdc, pp);
            Native.SelectObject(hdc, pb);
        }
        DrawGlyph(hdc, rc, glyph, hot ? _accent : ColIcon, font);
    }

    private void DrawHoverCircle(IntPtr hdc, Native.RECT rc, bool hot)
    {
        if (!hot)
            return;
        using var brush = new Native.GdiBrush(ColHover);
        IntPtr pb = Native.SelectObject(hdc, brush.Handle);
        IntPtr pp = Native.SelectObject(hdc, Native.GetStockObject(NULL_PEN));
        Native.Ellipse(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom);
        Native.SelectObject(hdc, pp);
        Native.SelectObject(hdc, pb);
    }

    private void DrawSwitch(IntPtr hdc, Native.RECT rc, bool on)
    {
        int hgt = rc.Bottom - rc.Top;
        using (var fill = new Native.GdiBrush(on ? _accent : ColSwitchOff))
        {
            IntPtr pb = Native.SelectObject(hdc, fill.Handle);
            IntPtr pp = Native.SelectObject(hdc, Native.GetStockObject(NULL_PEN));
            Native.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, hgt, hgt);
            Native.SelectObject(hdc, pp);
            Native.SelectObject(hdc, pb);
        }

        int d = hgt - S(4);
        int kx = on ? rc.Right - S(2) - d : rc.Left + S(2);
        int ky = rc.Top + S(2);
        using (var knob = new Native.GdiBrush(ColKnob))
        using (var pen = new Native.GdiPen(_dark ? Rgb(90, 90, 100) : Rgb(214, 214, 220), _dpi, 1f))
        {
            IntPtr pb = Native.SelectObject(hdc, knob.Handle);
            IntPtr pp = Native.SelectObject(hdc, pen.Handle);
            Native.Ellipse(hdc, kx, ky, kx + d, ky + d);
            Native.SelectObject(hdc, pp);
            Native.SelectObject(hdc, pb);
        }
    }

    private void DrawSlider(IntPtr hdc)
    {
        int pct = Math.Clamp(_state.VolumePercent, 0, 100);
        int th = Math.Max(S(3), 1);
        int top = _sliderY - th / 2;
        int tx = _sliderX0 + (int)Math.Round((_sliderX1 - _sliderX0) * pct / 100.0);

        using (var track = new Native.GdiBrush(ColTrackOff))
        {
            IntPtr pb = Native.SelectObject(hdc, track.Handle);
            IntPtr pp = Native.SelectObject(hdc, Native.GetStockObject(NULL_PEN));
            Native.RoundRect(hdc, _sliderX0, top, _sliderX1, top + th, th, th);
            Native.SelectObject(hdc, pp);
            Native.SelectObject(hdc, pb);
        }
        if (tx > _sliderX0)
        {
            using var fill = new Native.GdiBrush(_accent);
            IntPtr pb = Native.SelectObject(hdc, fill.Handle);
            IntPtr pp = Native.SelectObject(hdc, Native.GetStockObject(NULL_PEN));
            Native.RoundRect(hdc, _sliderX0, top, tx, top + th, th, th);
            Native.SelectObject(hdc, pp);
            Native.SelectObject(hdc, pb);
        }

        int r = (_dragging || _hot == Hot.Slider) ? S(7) : S(6);
        using (var thumb = new Native.GdiBrush(_accent))
        using (var pen = new Native.GdiPen(ColKnob, _dpi, 1.6f))
        {
            IntPtr pb = Native.SelectObject(hdc, thumb.Handle);
            IntPtr pp = Native.SelectObject(hdc, pen.Handle);
            Native.Ellipse(hdc, tx - r, _sliderY - r, tx + r, _sliderY + r);
            Native.SelectObject(hdc, pp);
            Native.SelectObject(hdc, pb);
        }
    }

    private void DrawRowHover(IntPtr hdc, Native.RECT row)
    {
        using var brush = new Native.GdiBrush(ColHover);
        var rc = R(row.Left + S(8), row.Top + S(2), row.Right - S(8), row.Bottom - S(2));
        IntPtr pb = Native.SelectObject(hdc, brush.Handle);
        IntPtr pp = Native.SelectObject(hdc, Native.GetStockObject(NULL_PEN));
        Native.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, S(14), S(14));
        Native.SelectObject(hdc, pp);
        Native.SelectObject(hdc, pb);
    }

    private void DrawDivider(IntPtr hdc, int y, int w)
    {
        var rc = R(0, y, w, y + Math.Max(1, S(1)));
        using var brush = new Native.GdiBrush(ColDivider);
        Native.FillRect(hdc, ref rc, brush.Handle);
    }

    private void DrawGlyph(IntPtr hdc, Native.RECT rc, char glyph, uint color, IntPtr font)
    {
        Native.SelectObject(hdc, font);
        Native.SetTextColor(hdc, color);
        var r = rc;
        Native.DrawTextW(hdc, glyph.ToString(), 1, ref r,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    }

    private void DrawText(IntPtr hdc, string text, IntPtr font, uint color,
        Native.RECT rc, uint flags)
    {
        Native.SelectObject(hdc, font);
        Native.SetTextColor(hdc, color);
        var r = rc;
        Native.DrawTextW(hdc, text, text.Length, ref r, flags);
    }

    // ---------- fonts ----------

    private void EnsureFonts()
    {
        if (_fontRow != IntPtr.Zero && _fontDpi == _dpi)
            return;
        // The cached back DC may still reference the old font objects; drop
        // it first so DeleteObject actually frees them.
        ReleaseBackBuffer();
        ReleaseFonts();
        _fontDpi = _dpi;
        _fontRow = CreateScaledFont("Segoe UI", 10.5f, _dpi);
        _fontTitle = CreateScaledFont("Segoe UI", 11f, _dpi);
        _fontGlyph = CreateScaledFont("Segoe Fluent Icons", 16f, _dpi);
        _fontGlyphBig = CreateScaledFont("Segoe Fluent Icons", 24f, _dpi);
        _fontWord = CreateScaledFont("Microsoft YaHei UI", 12f, _dpi, 600);
    }

    private static IntPtr CreateScaledFont(string face, float pt, uint dpi, int weight = 400)
    {
        // Negative height = character cell height, so glyphs line up on the
        // visual size instead of the em box.
        int h = -(int)Math.Round(pt * dpi / 72f);
        return Native.CreateFontW(
            h, 0, 0, 0, weight, 0, 0, 0, 0 /* DEFAULT_CHARSET */,
            0 /* OUT_DEFAULT_PRECIS */, 0, 4 /* CLEARTYPE_QUALITY */, 0, face);
    }

    private void ReleaseFonts()
    {
        if (_fontRow != IntPtr.Zero) { Native.DeleteObject(_fontRow); _fontRow = IntPtr.Zero; }
        if (_fontTitle != IntPtr.Zero) { Native.DeleteObject(_fontTitle); _fontTitle = IntPtr.Zero; }
        if (_fontGlyph != IntPtr.Zero) { Native.DeleteObject(_fontGlyph); _fontGlyph = IntPtr.Zero; }
        if (_fontGlyphBig != IntPtr.Zero) { Native.DeleteObject(_fontGlyphBig); _fontGlyphBig = IntPtr.Zero; }
        if (_fontWord != IntPtr.Zero) { Native.DeleteObject(_fontWord); _fontWord = IntPtr.Zero; }
    }

    private static uint ParseColor(string hex, uint fallback)
    {
        // "#RRGGBB" → COLORREF 0x00BBGGRR. Malformed input keeps the default.
        if (string.IsNullOrEmpty(hex)) return fallback;
        var s = hex.TrimStart('#');
        if (s.Length == 8) s = s.Substring(2); // tolerate "#AARRGGBB"
        if (s.Length != 6) return fallback;
        try
        {
            uint r = Convert.ToUInt32(s.Substring(0, 2), 16);
            uint g = Convert.ToUInt32(s.Substring(2, 2), 16);
            uint b = Convert.ToUInt32(s.Substring(4, 2), 16);
            return b << 16 | g << 8 | r;
        }
        catch
        {
            return fallback;
        }
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
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
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

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONDATA
        {
            // cbSize MUST be the first field and set to the marshalled size of
            // the struct before every Shell_NotifyIcon call — otherwise the API
            // reads the window handle as the size, fails the validation, and
            // silently never adds the tray icon.
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
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

        [DllImport("user32.dll")]
        public static extern bool ValidateRect(IntPtr hWnd, IntPtr rect);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern UIntPtr SetTimer(IntPtr hWnd, int id, uint elapse, IntPtr timerProc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadCursorW")]
        public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int DrawTextW(IntPtr hdc, string text, int len, ref RECT rect, uint format);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImage(IntPtr inst, string name, int type, int cx, int cy, uint load);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? name);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ----- GDI -----

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(
            int height, int width, int esc, int orient, int weight,
            int italic, int underline, int strikeOut, int charset,
            int outPrecision, int clipPrecision, int quality, int pitchFamily, string face);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr hdc, uint color);

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        // FillRect lives in user32, not gdi32 (same trap as TextOut below).
        [DllImport("user32.dll")]
        public static extern bool FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        public static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int rw, int rh);

        [DllImport("gdi32.dll")]
        public static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, uint rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr GetStockObject(int index);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreatePen(int style, int width, uint color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int rw, int rh);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        /// <summary>Solid brush with DeleteObject via using — keeps the draw
        /// paths free of manual brush lifetime bookkeeping.</summary>
        public sealed class GdiBrush : IDisposable
        {
            public IntPtr Handle { get; }

            public GdiBrush(uint colorRef)
            {
                Handle = CreateSolidBrush(colorRef & 0xFFFFFF);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                    DeleteObject(Handle);
            }

            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateSolidBrush(uint color);
        }

        /// <summary>Pen that scales its width with the monitor DPI.</summary>
        public sealed class GdiPen : IDisposable
        {
            public IntPtr Handle { get; }

            public GdiPen(uint colorRef, uint dpi, float width)
            {
                int w = Math.Max(1, (int)Math.Round(width * dpi / 96f));
                Handle = CreatePen(0 /* PS_SOLID */, w, colorRef & 0xFFFFFF);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                    DeleteObject(Handle);
            }
        }
    }
}
