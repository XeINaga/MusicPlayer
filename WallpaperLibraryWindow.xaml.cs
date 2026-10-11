using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicPlayer.Services;
using WinRT.Interop;

namespace MusicPlayer;

/// <summary>
/// Lets the user pick one of the locally installed Wallpaper Engine
/// wallpapers (video / image / web / scene) with a searchable preview list.
/// The selected <see cref="WallpaperEngineLibrary.WeWallpaperInfo"/> is
/// reported via <see cref="Selected"/>; the caller reads it after the window
/// closes.
/// </summary>
public sealed partial class WallpaperLibraryWindow : Window
{
    private readonly List<WeWallpaperInfo> _all;

    /// <summary>The wallpaper the user picked (null = cancelled).</summary>
    public WeWallpaperInfo? Selected { get; private set; }

    public WallpaperLibraryWindow(bool dark)
    {
        InitializeComponent();
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        _all = WallpaperEngineLibrary.Scan();
        ApplyFilter("");

        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        appWindow.Resize(new Windows.Graphics.SizeInt32(880, 720));
        appWindow.Move(new Windows.Graphics.PointInt32(300, 140));
    }

    private void ApplyFilter(string query)
    {
        var items = new List<WeWallpaperItem>();
        foreach (var info in _all)
        {
            if (!string.IsNullOrEmpty(query) &&
                !info.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            items.Add(new WeWallpaperItem(info));
        }
        WallpaperList.ItemsSource = items;
        CountText.Text = $"共 {_all.Count} 个壁纸";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text.Trim());

    private void WallpaperList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (WallpaperList.SelectedItem is WeWallpaperItem item)
            Confirm();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
        => Confirm();

    private void Confirm()
    {
        Selected = (WallpaperList.SelectedItem as WeWallpaperItem)?.Info;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>List row view-model: lazy preview thumbnail + display strings.</summary>
public sealed class WeWallpaperItem
{
    public WeWallpaperInfo Info { get; }
    public BitmapImage? PreviewSource { get; }
    public string Resolution { get; }

    public WeWallpaperItem(WeWallpaperInfo info)
    {
        Info = info;
        if (!string.IsNullOrEmpty(info.PreviewPath))
        {
            try { PreviewSource = new BitmapImage(new Uri(info.PreviewPath)); }
            catch { /* missing/unsupported preview: placeholder shows */ }
        }
        Resolution = System.IO.Path.GetFileName(info.FilePath);
    }
}
