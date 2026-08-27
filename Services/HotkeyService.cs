using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicPlayer.Services;

/// <summary>
/// Global hotkey service backed by a message-only Win32 window running on its
/// own thread. Default hotkeys: Ctrl+Alt+Space (play/pause), Ctrl+Alt+Left
/// (previous), Ctrl+Alt+Right (next). Events fire on the hotkey thread —
/// callers must marshal back to their own UI thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    /// <summary>
    /// Raised when a hotkey registration fails because another application
    /// already owns the key combination. The string is a human-readable
    /// description of which combination conflicted.
    /// </summary>
    public event Action<string>? RegistrationFailed;

    private const int IdPlayPause = 1;
    private const int IdNext = 2;
    private const int IdPrevious = 3;

    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_SPACE = 0x20;
    private const uint VK_LEFT = 0x25;
    private const uint VK_RIGHT = 0x27;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Native.WndProc _proc;
    private Thread? _thread;
    private volatile IntPtr _hwnd;
    private bool _registered;

    public HotkeyService() => _proc = WndProc;

    /// <summary>Start the hidden message window and register all hotkeys.</summary>
    public void Register()
    {
        StartThread();
    }

    /// <summary>Unregister all hotkeys and tear down the message window.</summary>
    public void Dispose()
    {
        var t = _thread;
        if (t == null)
            return;
        _thread = null;
        Post(WM_CLOSE);
        t.Join(1500);
    }

    // ---------- Thread ----------

    private void StartThread()
    {
        if (_thread != null)
            return;
        _registered = false;
        _thread = new Thread(Run) { Name = "MusicPlayerHotkey", IsBackground = true };
        _thread.Start();

        // Wait for the message window to exist so posted messages aren't lost.
        for (var i = 0; i < 100 && _hwnd == IntPtr.Zero; i++)
            Thread.Sleep(10);

        // Registration happens on the thread after the window exists.
        for (var i = 0; i < 100 && !_registered; i++)
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
        var wc = new Native.WNDCLASS
        {
            lpfnWndProc = _proc,
            lpszClassName = "MusicPlayerHotkeyWnd",
            hInstance = Native.GetModuleHandle(null),
        };
        Native.RegisterClass(ref wc);

        _hwnd = Native.CreateWindowEx(
            0, "MusicPlayerHotkeyWnd", "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        RegisterAll();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        _hwnd = IntPtr.Zero;
    }

    private void RegisterAll()
    {
        TryRegister(IdPlayPause, MOD_CONTROL | MOD_ALT, VK_SPACE, "Ctrl+Alt+Space（播放/暂停）");
        TryRegister(IdNext, MOD_CONTROL | MOD_ALT, VK_RIGHT, "Ctrl+Alt+Right（下一首）");
        TryRegister(IdPrevious, MOD_CONTROL | MOD_ALT, VK_LEFT, "Ctrl+Alt+Left（上一首）");
        _registered = true;
    }

    private void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Native.UnregisterHotKey(_hwnd, IdPlayPause);
        Native.UnregisterHotKey(_hwnd, IdNext);
        Native.UnregisterHotKey(_hwnd, IdPrevious);
    }

    private void TryRegister(int id, uint modifiers, uint vk, string description)
    {
        if (!Native.RegisterHotKey(_hwnd, id, modifiers, vk))
            RegistrationFailed?.Invoke(description);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                var id = wParam.ToInt32();
                switch (id)
                {
                    case IdPlayPause:
                        PlayPauseRequested?.Invoke();
                        break;
                    case IdNext:
                        NextRequested?.Invoke();
                        break;
                    case IdPrevious:
                        PreviousRequested?.Invoke();
                        break;
                }
                return IntPtr.Zero;

            case WM_CLOSE:
                UnregisterAll();
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
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
        public struct POINT
        {
            public int X;
            public int Y;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
