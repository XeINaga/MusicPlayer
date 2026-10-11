using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace MusicPlayer.Services;

/// <summary>
/// Main-window background materials: Mica / Mica Alt / acrylic / thin acrylic.
///
/// Implemented with the composition controllers (MicaController /
/// DesktopAcrylicController) instead of the XAML wrapper classes
/// (MicaBackdrop / DesktopAcrylicBackdrop): only the controllers expose
/// <c>Kind</c>, so only they can express all four variants
/// (MicaKind.BaseAlt = Mica Alt, DesktopAcrylicKind.Thin = thin acrylic).
///
/// Controllers need manual lifecycle wiring — activation state, theme
/// following and disposal — handled by <see cref="Context"/> below. When the
/// material cannot be created (unsupported OS, broken compositor) the window
/// silently keeps a solid background; startup must never crash on this.
/// </summary>
public static class WindowMaterialService
{
    /// <summary>Per-window material context: holds the controller, the
    /// backdrop configuration and the event subscriptions, and releases the
    /// controller when the window closes so it never outlives a dead window.</summary>
    private sealed class Context : IDisposable
    {
        public required Window Window { get; init; }
        public required ISystemBackdropControllerWithTargets Controller { get; init; }
        public required SystemBackdropConfiguration Configuration { get; init; }
        public FrameworkElement? Root { get; init; }
        public bool IsDisposed { get; private set; }

        public void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (IsDisposed) return;
            // An inactive window's material must dim (system behaviour); the
            // configuration only knows that through IsInputActive.
            Configuration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }

        public void OnThemeChanged(FrameworkElement sender, object args)
        {
            if (IsDisposed) return;
            Configuration.Theme = MapTheme(sender.ActualTheme);
        }

        public void OnClosed(object sender, WindowEventArgs args)
        {
            // "Close to tray" cancels the close (Handled = true): the window
            // stays alive, so the material must remain attached. Disposing
            // here left a bare translucent window behind after a wake-up from
            // the tray.
            if (args.Handled)
                return;
            _contexts.Remove(Window);
            Dispose();
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Window.Activated -= OnActivated;
            Window.Closed -= OnClosed;
            if (Root is not null)
                Root.ActualThemeChanged -= OnThemeChanged;
            Controller.Dispose();
        }
    }

    private static readonly Dictionary<Window, Context> _contexts = new();

    /// <summary>ElementTheme → SystemBackdropTheme needs an explicit mapping:
    /// the numeric values disagree (ElementTheme.Light=0 but
    /// SystemBackdropTheme.Default=0), and a naive cast makes a light-theme
    /// window use the "follow the system" preset.</summary>
    private static SystemBackdropTheme MapTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => SystemBackdropTheme.Light,
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        _ => SystemBackdropTheme.Default,
    };

    /// <summary>Apply the material to the window (replacing any previous one).</summary>
    public static void Apply(Window window, WindowMaterialKind kind)
    {
        try
        {
            ApplyCore(window, kind);
        }
        catch
        {
            // Material creation failed (unsupported system / compositor
            // error): keep the solid background, never crash the startup.
        }
    }

    private static void ApplyCore(Window window, WindowMaterialKind kind)
    {
        // Mutually exclusive with the XAML backdrop property.
        window.SystemBackdrop = null;

        ISystemBackdropControllerWithTargets controller;
        switch (kind)
        {
            case WindowMaterialKind.None:
                Remove(window); // pure colour: no backdrop, root fills opaque
                return;
            case WindowMaterialKind.Mica:
            case WindowMaterialKind.MicaAlt:
                if (!MicaController.IsSupported()) { Remove(window); return; }
                controller = new MicaController
                {
                    Kind = kind == WindowMaterialKind.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base,
                };
                break;
            case WindowMaterialKind.AcrylicThin:
                if (!DesktopAcrylicController.IsSupported()) { Remove(window); return; }
                controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
                break;
            default:
                if (!DesktopAcrylicController.IsSupported()) { Remove(window); return; }
                controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
                break;
        }

        // Attach the NEW controller first and only then take the OLD one down:
        // removing first left the window on a plain solid colour until the new
        // controller's brush arrived — visible as a flash on every switch.
        _contexts.TryGetValue(window, out var previous);
        var context = Attach(window, controller);
        if (context is null)
            return; // attach failed: the old material (if any) stays in place
        _contexts[window] = context;
        previous?.Dispose(); // old controller out only once the new one is live
    }

    /// <summary>Detach and dispose the material currently on the window.</summary>
    public static void Remove(Window window)
    {
        if (_contexts.TryGetValue(window, out var context))
        {
            _contexts.Remove(window);
            context.Dispose();
        }
    }

    /// <summary>Wire the controller to the window. Returns the context on
    /// success; on failure the half-initialised controller is disposed and
    /// null is returned (the caller keeps the previous material, if any).</summary>
    private static Context? Attach(Window window, ISystemBackdropControllerWithTargets controller)
    {
        try
        {
            var root = window.Content as FrameworkElement;
            var configuration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = root is not null ? MapTheme(root.ActualTheme) : SystemBackdropTheme.Default,
            };

            controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
            controller.SetSystemBackdropConfiguration(configuration);

            var context = new Context
            {
                Window = window,
                Controller = controller,
                Configuration = configuration,
                Root = root,
            };

            window.Activated += context.OnActivated;
            window.Closed += context.OnClosed;
            if (root is not null)
                root.ActualThemeChanged += context.OnThemeChanged;
            return context;
        }
        catch
        {
            // Attach failed (compositor refused the target): fall back to a
            // solid background instead of leaking a half-wired controller.
            controller.Dispose();
            return null;
        }
    }
}

public enum WindowMaterialKind
{
    Mica,
    MicaAlt,
    Acrylic,
    AcrylicThin,

    /// <summary>No backdrop at all: the root fills with the opaque theme
    /// background (MainWindow swaps MainGlassBg for AppBackground).</summary>
    None,

    /// <summary>Live capture of the Wallpaper Engine desktop render window
    /// (no backdrop either — the captured frames cover the root).</summary>
    WallpaperEngine,
}
