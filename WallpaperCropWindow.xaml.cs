using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicPlayer.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using WinRT.Interop;

namespace MusicPlayer;

/// <summary>
/// Wallpaper crop dialog: shows the picked image with a movable, resizable
/// crop frame. The frame ratio defaults to the main window's client aspect
/// and can be switched (16:9 / 4:3 / 3:2 / 1:1 / free). The frame is dragged
/// inside the image and resized from any of its four corner handles. OK
/// writes the cropped pixels (source-resolution) to a PNG next to
/// settings.json and reports the path via <see cref="ResultPath"/>; the
/// caller reads it after the window closes.
/// </summary>
public sealed partial class WallpaperCropWindow : Window
{
    private readonly string _sourcePath;
    private readonly double _windowRatio;   // main window client width / height
    private double? _ratioLock;             // null = free stretch, else w/h

    private int _srcW, _srcH;

    // Image display rect inside CanvasHost (DIP).
    private double _dispX, _dispY, _dispW, _dispH;
    // Crop frame rect (canvas DIP).
    private double _fx, _fy, _fw, _fh;

    private int _mode;                      // 0 none, 1 move, 2 TL, 3 TR, 4 BL, 5 BR
    private Windows.Foundation.Point _dragStart;
    private double _startFx, _startFy, _startFw, _startFh;

    /// <summary>Output path of the cropped wallpaper (null until OK).</summary>
    public string? ResultPath { get; private set; }

    public WallpaperCropWindow(string sourcePath, double targetRatio, bool dark)
    {
        _sourcePath = sourcePath;
        _windowRatio = targetRatio is > 0.2 and < 6 ? targetRatio : 16.0 / 9;
        _ratioLock = _windowRatio;

        InitializeComponent();
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        SrcText.Text = $"原图：{_sourcePath}";

        var bmp = new BitmapImage(new Uri(sourcePath));
        bmp.ImageOpened += (_, _) =>
        {
            _srcW = bmp.PixelWidth;
            _srcH = bmp.PixelHeight;
            SrcText.Text = $"原图 {_srcW}×{_srcH}";
            LayoutImage();
        };
        PreviewImage.Source = bmp;

        CanvasHost.SizeChanged += (_, _) => LayoutImage();

        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        appWindow.Resize(new Windows.Graphics.SizeInt32(980, 760));
        appWindow.Move(new Windows.Graphics.PointInt32(260, 140));
    }

    // ---------- layout ----------

    private void LayoutImage()
    {
        double cw = CanvasHost.ActualWidth, ch = CanvasHost.ActualHeight;
        if (cw <= 0 || ch <= 0 || _srcW <= 0 || _srcH <= 0)
            return;

        double scale = Math.Min(cw / _srcW, ch / _srcH);
        _dispW = _srcW * scale;
        _dispH = _srcH * scale;
        _dispX = (cw - _dispW) / 2;
        _dispY = (ch - _dispH) / 2;

        Canvas.SetLeft(PreviewImage, _dispX);
        Canvas.SetTop(PreviewImage, _dispY);
        PreviewImage.Width = _dispW;
        PreviewImage.Height = _dispH;

        ResetFrame();
    }

    /// <summary>Largest ratio-locked frame centred in the displayed image.</summary>
    private void ResetFrame()
    {
        double fw, fh;
        var ratio = _ratioLock ?? (_dispW / _dispH);
        if (_dispW / _dispH > ratio)
        {
            fh = _dispH;
            fw = fh * ratio;
        }
        else
        {
            fw = _dispW;
            fh = fw / ratio;
        }
        _fx = _dispX + (_dispW - fw) / 2;
        _fy = _dispY + (_dispH - fh) / 2;
        _fw = fw;
        _fh = fh;
        UpdateFrame();
    }

    private void UpdateFrame()
    {
        double hostW = CanvasHost.ActualWidth, hostH = CanvasHost.ActualHeight;

        Canvas.SetLeft(CropFrame, _fx);
        Canvas.SetTop(CropFrame, _fy);
        CropFrame.Width = _fw;
        CropFrame.Height = _fh;
        Canvas.SetLeft(HandleTL, _fx - 8); Canvas.SetTop(HandleTL, _fy - 8);
        Canvas.SetLeft(HandleTR, _fx + _fw - 8); Canvas.SetTop(HandleTR, _fy - 8);
        Canvas.SetLeft(HandleBL, _fx - 8); Canvas.SetTop(HandleBL, _fy + _fh - 8);
        Canvas.SetLeft(HandleBR, _fx + _fw - 8); Canvas.SetTop(HandleBR, _fy + _fh - 8);

        // Dim everything outside the crop frame with four mask rectangles.
        Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
        MaskTop.Width = hostW; MaskTop.Height = Math.Max(0, _fy);
        Canvas.SetLeft(MaskBottom, 0); Canvas.SetTop(MaskBottom, _fy + _fh);
        MaskBottom.Width = hostW; MaskBottom.Height = Math.Max(0, hostH - _fy - _fh);
        Canvas.SetLeft(MaskLeft, 0); Canvas.SetTop(MaskLeft, _fy);
        MaskLeft.Width = Math.Max(0, _fx); MaskLeft.Height = _fh;
        Canvas.SetLeft(MaskRight, _fx + _fw); Canvas.SetTop(MaskRight, _fy);
        MaskRight.Width = Math.Max(0, hostW - _fx - _fw); MaskRight.Height = _fh;

        if (_srcW > 0 && _dispW > 0)
        {
            double scale = _srcW / _dispW;
            HintText.Text = $"裁剪 {Math.Round(_fw * scale)}×{Math.Round(_fh * scale)} px";
        }
    }

    // ---------- pointer interaction ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(CanvasHost).Position;
        _startFx = _fx; _startFy = _fy; _startFw = _fw; _startFh = _fh;
        _mode = sender switch
        {
            _ when ReferenceEquals(sender, CropFrame) => 1,
            _ when ReferenceEquals(sender, HandleTL) => 2,
            _ when ReferenceEquals(sender, HandleTR) => 3,
            _ when ReferenceEquals(sender, HandleBL) => 4,
            _ when ReferenceEquals(sender, HandleBR) => 5,
            _ => 0,
        };
        if (_mode != 0)
            (sender as UIElement)?.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_mode == 0)
            return;
        var p = e.GetCurrentPoint(CanvasHost).Position;
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        double dispL = _dispX, dispR = _dispX + _dispW, dispT = _dispY, dispB = _dispY + _dispH;

        if (_mode == 1)
        {
            _fx = Math.Clamp(_startFx + dx, dispL, Math.Max(dispL, dispR - _fw));
            _fy = Math.Clamp(_startFy + dy, dispT, Math.Max(dispT, dispB - _fh));
        }
        else
        {
            ResizeFromCorner(_mode, Math.Clamp(p.X, dispL, dispR), Math.Clamp(p.Y, dispT, dispB),
                dispL, dispR, dispT, dispB);
        }
        UpdateFrame();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _mode = 0;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Resize the frame from one of its corners: the opposite corner
    /// stays anchored; with a ratio lock the larger of the two constrained
    /// dimensions wins (width limited by the horizontal gap, height by the
    /// vertical gap). Free mode sizes width and height independently.</summary>
    private void ResizeFromCorner(int corner, double mx, double my,
        double dispL, double dispR, double dispT, double dispB)
    {
        // Anchored (fixed) corner, and which sides the moving corner drives.
        bool anchorRight = corner is 2 or 4;   // TL/BL keep the right edge fixed
        bool anchorBottom = corner is 2 or 3;  // TL/TR keep the top edge fixed
        double ax = anchorRight ? _startFx + _startFw : _startFx;
        double ay = anchorBottom ? _startFy + _startFh : _startFy;
        double availW = anchorRight ? ax - dispL : dispR - ax;
        double availH = anchorBottom ? ay - dispT : dispB - ay;
        double minW = Math.Min(60, availW), minH = Math.Min(40, availH);

        double w, h;
        if (_ratioLock is double ratio && ratio > 0)
        {
            w = Math.Clamp(Math.Abs(mx - ax), minW, Math.Min(availW, availH * ratio));
            h = w / ratio;
            if (h < minH)
            {
                h = minH;
                w = Math.Min(h * ratio, availW);
            }
        }
        else
        {
            w = Math.Clamp(Math.Abs(mx - ax), minW, availW);
            h = Math.Clamp(Math.Abs(my - ay), minH, availH);
        }

        _fx = anchorRight ? ax - w : ax;
        _fy = anchorBottom ? ay - h : ay;
        _fw = w;
        _fh = h;
    }

    // ---------- toolbar ----------

    private void RatioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ratioLock = RatioCombo.SelectedIndex switch
        {
            0 => _windowRatio,
            1 => 16.0 / 9,
            2 => 4.0 / 3,
            3 => 3.0 / 2,
            4 => 1.0,
            _ => null, // free
        };
        // The XAML-selected default fires while the window is still parsing
        // (no layout yet) — only re-layout once the image is actually in.
        if (_srcW > 0 && CanvasHost.ActualWidth > 0)
            ResetFrame();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetFrame();

    // ---------- output ----------

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OkBtn.IsEnabled = false;
            if (_srcW <= 0 || _dispW <= 0)
                return;
            double scale = _srcW / _dispW;
            int sx = (int)Math.Max(0, Math.Round((_fx - _dispX) * scale));
            int sy = (int)Math.Max(0, Math.Round((_fy - _dispY) * scale));
            int sw = (int)Math.Min(_srcW - sx, Math.Round(_fw * scale));
            int sh = (int)Math.Min(_srcH - sy, Math.Round(_fh * scale));
            if (sw < 8 || sh < 8)
                return;

            var dest = Path.Combine(DataLocation.Root, "wallpaper.png");
            using var inStream = System.IO.File.OpenRead(_sourcePath);
            var decoder = await BitmapDecoder.CreateAsync(inStream.AsRandomAccessStream());
            var transform = new BitmapTransform();
            transform.Bounds = new BitmapBounds { X = (uint)sx, Y = (uint)sy, Width = (uint)sw, Height = (uint)sh };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            byte[] pixels = pixelData.DetachPixelData();

            using var outStream = System.IO.File.Create(dest);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)sw, (uint)sh, 96, 96, pixels);
            await encoder.FlushAsync();

            ResultPath = dest;
            Close();
        }
        catch
        {
            OkBtn.IsEnabled = true;
            HintText.Text = "裁剪失败，请重试";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
