using System;
using System.Runtime.InteropServices;

namespace MusicPlayer.Services;

/// <summary>
/// Minimal Win32 interop used for the desktop-lyrics overlay:
/// always-on-top, click-through, and hiding from the taskbar.
/// </summary>
internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_POPUP = 0x80000000;

    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    // Virtual screen = union of ALL monitors (multi-monitor safe).
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // DWM "accent policy" — the lower-level mechanism behind a truly
    // transparent window. ACCENT_ENABLE_TRANSPARENTGRADIENT makes the window's
    // background show the desktop through it while any opaque XAML content
    // (the lyric text) still composites on top, crisp and fully opaque. This is
    // the same per-pixel-alpha path the acrylic SystemBackdrop uses, minus the
    // frosted blur / gray tint, and it does NOT require AppWindow.TransparencyKind
    // (which is absent from this SDK's metadata).
    internal enum ACCENT_STATE
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ACCENT_POLICY
    {
        public int nAccentState;
        public int nFlags;
        public int nColor; // 0xAABBGGRR
        public int nAnimationId;
    }

    internal enum WINDOWCOMPOSITIONATTRIB
    {
        WCA_ACCENT_POLICY = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINCOMPATTRDATA
    {
        public WINDOWCOMPOSITIONATTRIB attrib;
        public IntPtr pvData;
        public int cbData;
    }

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WINCOMPATTRDATA pAttrData);
}
