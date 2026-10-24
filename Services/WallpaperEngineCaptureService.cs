using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;

namespace MusicPlayer.Services;

/// <summary>
/// Captures the Wallpaper Engine desktop render window (wallpaper32/64.exe)
/// at a low frame rate and streams it into a WriteableBitmap so the player
/// background shows the live animated wallpaper — video, web and scene
/// wallpapers alike: whatever Wallpaper Engine is compositing on the desktop
/// behind the icons.
///
/// How it works: the WE render window's D3D/GL output is not reachable
/// through a plain GDI DC, but PrintWindow with PW_RENDERFULLCONTENT grabs
/// the DWM-composited frame into a top-down 32bpp DIB section. The frame is
/// cropped to the player window's on-screen rect (so the background reads as
/// "the wallpaper behind this window") via StretchBlt into a second DIB,
/// whose bits are then copied into the WriteableBitmap, invalidated on the
/// UI thread.
///
/// Threading contract: the capture thread works on an immutable
/// (bitmap, pointer, size) snapshot taken under _gate and publishes each
/// frame under the same lock, so the UI thread can swap the bitmap or
/// invalidate it without ever racing a copy. Stop() waits for a confirmed
/// thread exit, so the GDI surfaces are only ever owned by one live loop.
/// </summary>
public sealed class WallpaperEngineCaptureService : IDisposable
{
    // Lifetime state (_stop / _thread / _dying / IsRunning) is only written
    // under _stateGate; every Join on a capture thread happens OUTSIDE that
    // lock so the thread can always reach its flag checks.
    private readonly object _stateGate = new();
    private Thread? _thread;
    private Thread? _dying;                 // thread that missed Stop()'s confirm deadline
    private volatile bool _stop;
    private IntPtr _weHwnd;
    private long _nextFindAt;               // Environment.TickCount64; 0 = find now
    private int _printFails;                // consecutive PrintWindow failures (capture thread only)
    private DispatcherQueue? _dispatcher;
    private IntPtr _mainHwnd;
    private uint _mainPid;                 // host process: own child windows don't count as "away"

    private WriteableBitmap? _bitmap;       // UI thread created / replaced
    private FrameTarget? _target;           // immutable (bitmap, ptr, w, h) triple, published under _gate
    private readonly object _gate = new();  // guards pixel writes + target swaps
    private int _frameMs = 50;              // ~20 fps
    private volatile bool _windowFound;

    // Resize debounce (capture thread only): the bitmap is rebuilt only
    // after the same new size was seen a few frames in a row, so dragging
    // the window does not reallocate the bitmap on every frame.
    private int _pendW, _pendH, _pendCount;
    private int _queuedW, _queuedH;         // size a rebuild was already queued for

    // Reused GDI capture surfaces: full WE window frame + cropped output.
    private IntPtr _srcDib, _srcDc, _srcBits; private int _srcCapW, _srcCapH;
    private IntPtr _dstDib, _dstDc, _dstBits; private int _dstCapW, _dstCapH;

    private bool _running;                  // IsRunning backing field (guarded by _stateGate)

    /// <summary>Immutable snapshot of the pixel buffer the capture thread may
    /// write: the bitmap reference (keeps the native buffer alive even after
    /// the UI thread has replaced it), its base address and the matching
    /// dimensions, published as one unit under _gate. The capture thread
    /// copies against a local snapshot, so a bitmap swapped in on the UI
    /// thread mid-frame can never shrink the buffer under the copy.</summary>
    private sealed class FrameTarget
    {
        public FrameTarget(WriteableBitmap bitmap, IntPtr ptr, int width, int height)
        {
            Bitmap = bitmap;
            Ptr = ptr;
            Width = width;
            Height = height;
        }

        public WriteableBitmap Bitmap { get; }
        public IntPtr Ptr { get; }
        public int Width { get; }
        public int Height { get; }
    }

    /// <summary>The live bitmap the background Image binds to (UI thread).</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    /// <summary>Raised on the UI thread when the bitmap is replaced (window
    /// resize) so the host can re-bind its Image source.</summary>
    public event Action<WriteableBitmap>? BitmapReplaced;

    /// <summary>True once the WE render window has been located.</summary>
    public bool WindowFound => _windowFound;

    /// <summary>Additionally skip frames while the host window merely lost
    /// focus to another app (minimize/tray always pause regardless). Read by
    /// the capture thread on every frame; set by the host when the user
    /// toggles the corresponding setting.</summary>
    public volatile bool PauseOnBlur;

    /// <summary>True between Start() and Stop().</summary>
    public bool IsRunning
    {
        get { lock (_stateGate) return _running; }
        private set { lock (_stateGate) _running = value; }
    }

    /// <summary>Start the capture loop. <paramref name="mainHwnd"/> is the
    /// player window (its on-screen rect selects the wallpaper region and
    /// drives the output size — resizes are picked up automatically).</summary>
    public void Start(DispatcherQueue dq, IntPtr mainHwnd)
    {
        Thread? dying;
        lock (_stateGate)
        {
            if (IsRunning)
                return;
            dying = _dying;
        }
        // A capture thread that missed Stop()'s confirm deadline is still
        // tearing down (it always exits within ~100 ms — the loop sleeps in
        // 50 ms steps and polls _stop). Starting a second loop would race it
        // on the same GDI handles and pixel buffer, so wait for it first.
        if (dying != null && !dying.Join(2000))
            return;   // still alive: refuse to start until it has confirmed exit

        GetWindowRect(mainHwnd, out var r);
        int w = Math.Max(1, r.R - r.L), h = Math.Max(1, r.B - r.T);

        lock (_stateGate)
        {
            if (IsRunning)
                return;
            _dying = null;
            _dispatcher = dq;
            _mainHwnd = mainHwnd;
            _mainPid = (uint)Environment.ProcessId;
            _weHwnd = IntPtr.Zero;
            _windowFound = false;
            _printFails = 0;
            _pendW = _pendH = _pendCount = _queuedW = _queuedH = 0;
            _stop = false;
            ReplaceBitmap(w, h, raiseReplaced: false);
            _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WallpaperEngineCapture" };
            _thread.Start();
            IsRunning = true;
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_stateGate)
        {
            _stop = true;
            IsRunning = false;
            t = _thread;
            _thread = null;
        }
        if (t == null)
            return;
        // The loop sleeps in 50 ms steps that poll _stop, so it normally
        // exits within ~100 ms and this Join returns long before the cap.
        // If it really does not confirm, remember it as dying: Start() and
        // Dispose() then wait for (or refuse until) its exit, so two loops
        // can never race the same GDI handles or pixel buffer.
        if (t.Join(2000))
            return;
        lock (_stateGate)
            _dying = t;
    }

    /// <summary>Stops the capture loop and releases every GDI surface. The
    /// handles are only deleted after the capture thread has confirmed its
    /// exit, so the deletion can never race the loop's surface rebuilds.</summary>
    public void Dispose()
    {
        Stop();
        Thread? dying;
        lock (_stateGate)
        {
            dying = _dying;
            _dying = null;
        }
        if (dying != null && !dying.Join(2000))
            return;   // thread refuses to die: leak the handles rather than double-free them
        lock (_gate)
        {
            _target = null;
            if (_srcDc != IntPtr.Zero) { DeleteDC(_srcDc); _srcDc = IntPtr.Zero; }
            if (_srcDib != IntPtr.Zero) { DeleteObject(_srcDib); _srcDib = IntPtr.Zero; }
            _srcBits = IntPtr.Zero;
            _srcCapW = _srcCapH = 0;
            if (_dstDc != IntPtr.Zero) { DeleteDC(_dstDc); _dstDc = IntPtr.Zero; }
            if (_dstDib != IntPtr.Zero) { DeleteObject(_dstDib); _dstDib = IntPtr.Zero; }
            _dstBits = IntPtr.Zero;
            _dstCapW = _dstCapH = 0;
        }
    }

    /// <summary>Create a bitmap of the given size and publish it together
    /// with its raw pixel pointer and dimensions as one atomic triple under
    /// _gate. The capture thread snapshots this triple per frame, so it can
    /// never see a pointer that does not match the dims it writes.
    /// Must run on the UI thread (WriteableBitmap construction).</summary>
    private void ReplaceBitmap(int w, int h, bool raiseReplaced)
    {
        var bmp = new WriteableBitmap(w, h);
        var ptr = GetPixelPtr(bmp);
        lock (_gate)
        {
            _bitmap = bmp;
            _target = new FrameTarget(bmp, ptr, w, h);
        }
        if (raiseReplaced)
            BitmapReplaced?.Invoke(bmp);
    }

    private static IntPtr GetPixelPtr(WriteableBitmap bmp)
    {
        try
        {
            return bmp.PixelBuffer.As<IBufferByteAccess>().Buffer();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // ---------- capture thread ----------

    private void CaptureLoop()
    {
        while (!StopRequested())
        {
            try
            {
                if (_weHwnd == IntPtr.Zero && Environment.TickCount64 >= _nextFindAt)
                {
                    _weHwnd = FindRenderWindow();
                    _nextFindAt = Environment.TickCount64 + 3000;
                    _windowFound = _weHwnd != IntPtr.Zero;
                }
                if (_weHwnd == IntPtr.Zero || _target == null)
                {
                    InterruptibleSleep(500);
                    continue;
                }
                CaptureFrame();
                InterruptibleSleep(_frameMs);
            }
            catch
            {
                _weHwnd = IntPtr.Zero;   // window died or chain broke: re-find
                _windowFound = false;
                _nextFindAt = 0;
                InterruptibleSleep(500);
                if (StopRequested())
                    break;
            }
        }
    }

    /// <summary>Sleep that polls the stop flag in 50 ms steps, so Stop()
    /// (which joins this thread on the UI thread) returns within ~100 ms
    /// even while the loop is idling.</summary>
    private void InterruptibleSleep(int ms)
    {
        for (int remaining = ms; remaining > 0; remaining -= 50)
        {
            if (StopRequested())
                return;
            Thread.Sleep(Math.Min(50, remaining));
        }
    }

    private bool StopRequested()
    {
        lock (_stateGate)
            return _stop;
    }

    /// <summary>Locate the Wallpaper Engine render window. WE parents its
    /// render surface (video player / web view / scene window — class names
    /// vary, e.g. WPEVideoWallpaper) into the desktop layer (Progman or a
    /// WorkerW), so it is a CHILD window, invisible to a plain top-level
    /// EnumWindows. Also checks the unlikely case of a top-level WE window.
    /// The match is "visible window of the wallpaper32/64 process, big
    /// enough to be the desktop render surface".</summary>
    private IntPtr FindRenderWindow()
    {
        IntPtr found = IntPtr.Zero;

        // 1) top-level WE windows (fallback)
        EnumWindows((h, _) =>
        {
            if (found != IntPtr.Zero || !IsWindowVisible(h))
                return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!IsWallpaperProcess(pid))
                return true;
            GetWindowRect(h, out var r);
            if (r.R - r.L >= 800 && r.B - r.T >= 600)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero)
            return found;

        // 2) children of Progman / WorkerW (the desktop layer)
        EnumWindows((h, _) =>
        {
            if (found != IntPtr.Zero)
                return false;
            var sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, 64);
            var cls = sb.ToString();
            if (cls is not ("Progman" or "WorkerW"))
                return true;
            EnumChildWindows(h, (hc, _) =>
            {
                if (found != IntPtr.Zero || !IsWindowVisible(hc))
                    return true;
                GetWindowThreadProcessId(hc, out var pid);
                if (!IsWallpaperProcess(pid))
                    return true;
                GetWindowRect(hc, out var r);
                if (r.R - r.L >= 800 && r.B - r.T >= 600)
                {
                    found = hc;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsWallpaperProcess(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName is "wallpaper32" or "wallpaper64";
        }
        catch
        {
            return false;
        }
    }

    private void CaptureFrame()
    {
        // Stale HWND (WE changed the wallpaper or restarted): drop it and
        // re-find. GetWindowRect on a dead HWND just returns false, so
        // without this the picture would freeze forever — or, once the HWND
        // is recycled, capture the wrong window entirely.
        if (_weHwnd != IntPtr.Zero && !IsWindow(_weHwnd))
        {
            _weHwnd = IntPtr.Zero;
            _windowFound = false;
            _nextFindAt = 0;    // re-find immediately
            return;
        }
        // Minimized OR hidden (close-to-tray does AppWindow.Hide → SW_HIDE):
        // GetWindowRect returns the icon rect when minimized (bogus size for
        // the resize path), and capturing frames nobody can see burns a full
        // 20 fps PrintWindow/blit loop for nothing. Skip the frame either
        // way; the loop keeps polling at its normal pace and resumes by
        // itself once the window is visible again.
        if (IsIconic(_mainHwnd) || !IsWindowVisible(_mainHwnd))
            return;

        // Focus lost to another app (opt-in via PauseOnBlur — the user
        // toggle in settings): same pause condition the host applies to its
        // video wallpaper. Windows of our own process (sound fx panel, lyric
        // search, crop dialog…) are exempt — the main window stays visible
        // behind them.
        if (PauseOnBlur)
        {
            var fg = GetForegroundWindow();
            if (fg != _mainHwnd)
            {
                GetWindowThreadProcessId(fg, out var fgPid);
                if (fgPid != _mainPid)
                    return;
            }
        }

        GetWindowRect(_mainHwnd, out var mr);
        int wantW = Math.Max(1, mr.R - mr.L), wantH = Math.Max(1, mr.B - mr.T);

        // Snapshot the buffer triple once; the copy at the bottom only ever
        // touches this local (never the fields), so a bitmap replaced on the
        // UI thread mid-frame cannot shrink the buffer under us.
        FrameTarget? target;
        lock (_gate)
            target = _target;
        if (target == null || target.Ptr == IntPtr.Zero)
            return;
        int outW = target.Width, outH = target.Height;

        // Player window resized: this frame no longer matches the bitmap.
        // Skip the copy (losing a frame beats writing past the buffer) and
        // queue a rebuild only once the same size has settled.
        if (wantW != outW || wantH != outH)
        {
            QueueResize(wantW, wantH);
            return;
        }

        if (!GetWindowRect(_weHwnd, out var wr))
            return;
        int weW = wr.R - wr.L, weH = wr.B - wr.T;
        if (weW <= 0 || weH <= 0)
            return;

        // Full WE window frame into a top-down 32bpp DIB.
        EnsureSurface(ref _srcDib, ref _srcDc, ref _srcBits, ref _srcCapW, ref _srcCapH, weW, weH, exact: false);
        if (!PrintWindow(_weHwnd, _srcDc, PW_RENDERFULLCONTENT))
        {
            // Single failures are skipped; a streak means the window went
            // away for good: force a fresh search next round.
            if (++_printFails >= 5)
            {
                _weHwnd = IntPtr.Zero;
                _windowFound = false;
                _printFails = 0;
                _nextFindAt = 0;
            }
            return;
        }
        _printFails = 0;

        // Source rect: the player window's area relative to the WE surface.
        int sx = Math.Clamp(mr.L - wr.L, 0, Math.Max(0, weW - 1));
        int sy = Math.Clamp(mr.T - wr.T, 0, Math.Max(0, weH - 1));
        int cw = Math.Min(mr.R - mr.L, weW - sx);
        int ch = Math.Min(mr.B - mr.T, weH - sy);
        if (cw <= 0 || ch <= 0)
            return;

        // Crop + scale into an output DIB sized exactly like the bitmap, so
        // its stride equals width*4 and the frame is one contiguous block.
        EnsureSurface(ref _dstDib, ref _dstDc, ref _dstBits, ref _dstCapW, ref _dstCapH, outW, outH, exact: true);
        if (!StretchBlt(_dstDc, 0, 0, outW, outH, _srcDc, sx, sy, cw, ch, SRCCOPY))
            return;   // blit failed: skip the frame

        // Whole-frame copy: the dst DIB and the WriteableBitmap are both
        // top-down BGRA with stride == width*4, so one native block copy
        // replaces the old per-row double marshal. Held under _gate against
        // the UI thread's Invalidate and bitmap swap.
        lock (_gate)
        {
            if (_target != target)   // bitmap was replaced while we blitted
                return;
            MoveMemory(target.Ptr, _dstBits, (IntPtr)((long)outW * 4 * outH));
        }
        // _dispatcher is assigned in Start() before the capture thread starts,
        // so it is never null on this thread.
        _dispatcher!.TryEnqueue(() =>
        {
            lock (_gate)
            {
                if (_target == target)   // only invalidate the frame we wrote
                    target.Bitmap.Invalidate();
            }
        });
    }

    /// <summary>Resize debounce: the same new size must be seen 3 frames in
    /// a row (~150 ms at 20 fps) before a bitmap rebuild is queued, so a
    /// window drag does not reallocate the bitmap on every frame.</summary>
    private void QueueResize(int w, int h)
    {
        if (_queuedW == w && _queuedH == h)
            return;                       // rebuild already queued for this size
        if (_pendW == w && _pendH == h)
            _pendCount++;
        else
        {
            _pendW = w;
            _pendH = h;
            _pendCount = 1;
        }
        if (_pendCount < 3)
            return;
        _queuedW = w;
        _queuedH = h;
        _pendCount = 0;
        _dispatcher!.TryEnqueue(() => ReplaceBitmap(w, h, raiseReplaced: true));
    }

    /// <summary>Top-down 32bpp DIB section + compatible memory DC, rebuilt
    /// when the requested size no longer fits. With <paramref name="exact"/>
    /// the DIB is (re)built whenever it is not exactly the requested size,
    /// so the output stride equals width*4 and the frame copies as one
    /// block; the source surface just grows and reuses its allocation.</summary>
    private static void EnsureSurface(ref IntPtr dib, ref IntPtr dc, ref IntPtr bits,
        ref int capW, ref int capH, int wantW, int wantH, bool exact)
    {
        bool fits = exact
            ? capW == wantW && capH == wantH
            : capW >= wantW && capH >= wantH;
        if (dib != IntPtr.Zero && dc != IntPtr.Zero && bits != IntPtr.Zero && fits)
            return;
        if (dc != IntPtr.Zero) { DeleteDC(dc); dc = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
        bits = IntPtr.Zero;
        capW = exact ? wantW : Math.Max(wantW, 64);
        capH = exact ? wantH : Math.Max(wantH, 64);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = capW,
            biHeight = -capH,   // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,  // BI_RGB
        };
        dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
        dc = CreateCompatibleDC(IntPtr.Zero);
        // A failed allocation must not continue: with dib/bits/dc zeroed the
        // following StretchBlt + frame copy would dereference null and take
        // the whole process down with an uncatchable AV.
        if (dib == IntPtr.Zero || bits == IntPtr.Zero || dc == IntPtr.Zero)
        {
            if (dc != IntPtr.Zero) { DeleteDC(dc); dc = IntPtr.Zero; }
            if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
            bits = IntPtr.Zero;
            capW = capH = 0;
            throw new InvalidOperationException($"CreateDIBSection failed for {wantW}x{wantH}");
        }
        SelectObject(dc, dib);
    }

    // ---------- Win32 ----------

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr lp);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwnd, EnumProc cb, IntPtr lp);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDst, int xDst, int yDst, int wDst, int hDst,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void MoveMemory(IntPtr dest, IntPtr src, IntPtr size);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    private struct RECT
    {
        public int L, T, R, B;
    }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>Gives the raw pointer of a WinUI WriteableBitmap's pixel
    /// buffer, so the capture thread can write frames without marshalling
    /// through streams.</summary>
    [ComImport]
    [Guid("905A0FEF-BC53-11DF-8C85-001114C3E4C7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess
    {
        [PreserveSig]
        IntPtr Buffer();
    }
}
