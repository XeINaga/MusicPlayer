using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace MusicPlayer.Services;

/// <summary>
/// Ensures only one instance of the application runs at a time.
/// A second launch signals the first instance to restore its window.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Global\MusicPlayer.SingleInstance";
    private const string EventName = @"Global\MusicPlayer.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static DispatcherQueue? _dispatcher;
    private static Window? _window;

    /// <summary>
    /// Tries to acquire the single-instance mutex.
    /// Returns <c>true</c> if this is the first instance (caller should continue).
    /// Returns <c>false</c> if another instance already exists (caller should exit).
    /// </summary>
    public static bool TryAcquire(Window window)
    {
        _window = window;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        bool createdNew;
        try
        {
            _mutex = new Mutex(false, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing — treat as new owner.
            createdNew = true;
        }

        if (!createdNew)
        {
            // Another instance is running — signal it to bring its window to the front.
            SignalExistingInstance();
            return false;
        }

        // We are the first instance. Create the activation event and start listening.
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        StartActivationListener();
        return true;
    }

    /// <summary>
    /// Signals an already-running instance to bring its window to the foreground.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(EventName);
            evt.Set();
        }
        catch
        {
            // Event may not exist yet if the first instance is still initializing.
        }
    }

    /// <summary>
    /// Background thread that waits for activation signals from a second launch.
    /// </summary>
    private static void StartActivationListener()
    {
        var thread = new Thread(() =>
        {
            while (_activateEvent != null)
            {
                if (_activateEvent.WaitOne())
                {
                    // Signal received — marshal to the UI thread to restore the window.
                    _dispatcher?.TryEnqueue(() => ActivateWindow());
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener"
        };
        thread.Start();
    }

    /// <summary>
    /// Restores and brings the main window to the foreground.
    /// </summary>
    private static void ActivateWindow()
    {
        if (_window == null)
            return;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            if (hwnd == IntPtr.Zero)
                return;

            // SW_RESTORE = 9 — restores a minimized or maximized window.
            ShowWindow(hwnd, 9);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // best-effort; the user can Alt-Tab to the window if this fails.
        }
    }

    /// <summary>
    /// Releases the mutex. Call from <c>App.OnExit</c> or equivalent.
    /// </summary>
    public static void Release()
    {
        _activateEvent?.Dispose();
        _activateEvent = null;
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
