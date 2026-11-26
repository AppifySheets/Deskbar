using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// A TASK'S PICTURE ENLARGED, inside Deskbar (task e5d85e5e, "make comment photos small, enlarge when i click on them").
//
// Thumbnails in a comment or a description are small (TaskImageRow.MaxHeight), so a click has to show the picture big.
// Until this, a click handed the file to the default image viewer through the shell. That opened a separate app in
// whatever workspace and screen it liked, and it took a second or two to start. This is a lightbox instead: a borderless
// window maximized over the screen under the cursor, the screen dimmed behind it, the picture centred on top.
//
// Size: the picture is drawn at most at its own pixel size on that screen (1:1 physical pixels, so a screenshot looks
// exactly as it was captured, not blown up by the screen's scaling), and shrunk to fit when it is bigger than the screen.
// The Image's MaxWidth/MaxHeight carry the 1:1 size in DIP; Stretch.Uniform then fits it to the room left, aspect kept.
//
// Closing: a click anywhere, Esc, or the window losing activation (Alt+Tab, a click on another app). "Open in viewer"
// at the bottom still hands the file to the default viewer, for zooming or copying, and closes the lightbox.
//
// Placement: the window is moved onto the cursor's screen in SourceInitialized, before WPF shows it, and shown
// maximized, so it is maximized on THAT screen. Moving it in physical pixels with SetWindowPos avoids WPF's DIP
// conversion, which uses the DPI of the screen the window was created on and can land on the wrong screen when the
// screens have different scaling.
//
// A picture that cannot be read (deleted since the thumbnail was drawn, locked) falls back to the default viewer, which
// says what is wrong in its own words; nothing is thrown into the click.
static class PictureViewer
{
    /// <summary>The Tag of the enlarged Image, for the tests.</summary>
    internal const string ImageTag = "picture-viewer-image";

    static readonly Brush Backdrop = Frozen(Color.FromArgb(0xD8, 0x10, 0x11, 0x14));
    static readonly Brush Edge = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    static readonly Brush HintInk = Frozen(Color.FromRgb(0xB8, 0xBB, 0xC4));

    /// <summary>Shows the picture at <paramref name="path"/> enlarged over the screen under the cursor.</summary>
    public static void Show(string path)
    {
        if (Build(path) is not { } window)
        {
            TasksWindow.OpenInViewer(path);
            return;
        }
        // Held like an open menu, so the bar, the tasks panel and the screen strips stop re-asserting topmost over it
        // until it closes (OwnMenus.Hold has the reason).
        var release = OwnMenus.Hold();
        window.Closed += (_, _) => release();
        window.Show();
        window.Activate();
    }

    /// <summary>The lightbox for this picture, not yet shown, or null when the file cannot be read.</summary>
    internal static Window? Build(string path) => Load(path) is { } bitmap ? Make(bitmap, path) : null;

    static Window Make(BitmapSource bitmap, string path)
    {
        var screen = ScreenUnderCursor();
        var scale = screen?.Scale ?? 1.0;

        var image = new Image
        {
            Tag = ImageTag,
            Source = bitmap,
            Stretch = Stretch.Uniform,
            // 1:1 physical pixels on the target screen: the picture's pixel size divided by that screen's scale.
            MaxWidth = bitmap.PixelWidth / scale,
            MaxHeight = bitmap.PixelHeight / scale,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var openInViewer = new Button
        {
            Content = "Open in viewer",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
        };

        var hint = new TextBlock
        {
            Text = "Click anywhere or press Esc to close",
            Foreground = HintInk,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var layout = new Grid { Margin = new Thickness(32) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var framed = new Border
        {
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = image,
        };
        Grid.SetRow(framed, 0);
        Grid.SetRow(openInViewer, 1);
        Grid.SetRow(hint, 2);
        layout.Children.Add(framed);
        layout.Children.Add(openInViewer);
        layout.Children.Add(hint);

        var window = new Window
        {
            Title = "Picture",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Backdrop,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            // Normal here, Maximized at the end of SourceInitialized (see Placement above, and the handler below).
            WindowState = WindowState.Normal,
            Content = layout,
        };

        // Closing is guarded: Close() deactivates the window, and Deactivated would ask for a second Close. The guard is
        // also set by Closing, which EVERY way of closing raises (Alt+F4 reaches this window too: WPF gives even a
        // borderless window a system menu). Without that, Alt+F4 -> WM_CLOSE -> DestroyWindow -> Deactivated ->
        // window.Close() while the window is already closing, which WPF answers with InvalidOperationException out of a
        // window procedure, and that ends the app.
        var closing = false;
        window.Closing += (_, _) => closing = true;
        void CloseOnce()
        {
            if (closing) return;
            closing = true;
            window.Close();
        }

        // The double-click trap: the second click of a double-click on the thumbnail lands on the lightbox, which has
        // just opened over it, and would close it at once. Mouse-ups within the double-click time of the window's first
        // render are ignored.
        var openedAt = long.MaxValue;
        window.ContentRendered += (_, _) => openedAt = Environment.TickCount64;
        bool TooSoon() => Environment.TickCount64 - openedAt < GetDoubleClickTime();

        window.SourceInitialized += (_, _) =>
        {
            if (screen is { } target)
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                // A small rectangle in the middle of the target screen's work area; the maximize then fills that screen.
                var (cx, cy) = ((target.Work.Left + target.Work.Right) / 2, (target.Work.Top + target.Work.Bottom) / 2);
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, cx - 50, cy - 50, 100, 100, NativeMethods.SWP_NOACTIVATE);
            }
            // Only now, not in the initializer: a window created Maximized gets WS_MAXIMIZE in CreateWindowEx, so it is
            // already maximized on its creation screen when this handler runs. Shrinking it then leaves WS_MAXIMIZE set,
            // and the SW_SHOWMAXIMIZED of Show only shows it, at 100x100. Set while the window is still hidden, WPF just
            // records the state, and Show maximizes it on the screen holding its rectangle: the one moved to above.
            window.WindowState = WindowState.Maximized;
        };
        openInViewer.Click += (_, e) =>
        {
            e.Handled = true;
            CloseOnce();
            TasksWindow.OpenInViewer(path);
        };
        // MouseUp rather than MouseDown: the press that opened the lightbox was on the thumbnail, so only a full click
        // made inside the lightbox closes it. A press on "Open in viewer" never gets here: ButtonBase marks the
        // mouse-up handled before it raises Click.
        window.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled || TooSoon()) return;
            CloseOnce();
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseOnce();
        };
        window.Deactivated += (_, _) => CloseOnce();
        return window;
    }

    // The whole file read into memory (OnLoad) so it is not held open after the lightbox closes, and IgnoreImageCache so a
    // picture edited and saved under the same name shows its new pixels. Frozen: usable from any thread, cheaper to draw.
    static BitmapSource? Load(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        // FormatException covers FileFormatException (a corrupt or cut-off file) and UriFormatException; COMException is
        // any other WIC failure. The thumbnail was read fine moments ago, so these mean the file changed since.
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException
                                      or ArgumentException or COMException or InvalidOperationException)
        {
            ClickTrace.Write($"picture viewer: could not read {path}: {e.Message}");
            return null;
        }
    }

    // The work area (physical pixels) and the scale of the screen under the cursor, or null when Windows cannot say.
    // The DPI comes from GetDpiForMonitor on that monitor, the way DetachedStatsWindow.MonitorAt does it, because a
    // window-scoped DPI is not known before the window exists.
    static (NativeMethods.RECT Work, double Scale)? ScreenUnderCursor()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return null;
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == nint.Zero) return null;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return null;
        var scale = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0
            : 1.0;
        return (info.rcWork, scale);
    }

    // Milliseconds, the user's double-click time (500 by default).
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
