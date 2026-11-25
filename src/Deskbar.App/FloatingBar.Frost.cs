using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Core.Imaging;
using Deskbar.Windows.Monitoring;
using Deskbar.Windows.Shell;

namespace Deskbar.App;

// THE FROSTED BAND: the band behind the bar's idle workspace rows shows the wallpaper behind it, blurred and with its
// colour lifted, under a light veil, instead of 85% of the bar's near-black. Docked first; floating too since task
// b971ca3f (below).
//
// Petre, 2026-11-22, with a screenshot of the see-through band over his laptop's spring tree: "could you do
// transparency that's not black-ish?" At 85% of #1B1C21 only 15% of the picture got through, so bright green
// foliage read as dark olive. Shown five treatments over that tree and over his desktop's mountains (lighter, tinted
// from the wallpaper, frosted, light glass), he picked the frosted wallpaper: "d".
//
// How, given that WPF cannot blur what lies behind its own window and Windows' Acrylic would mean rebuilding the
// bar's window (it keeps AllowsTransparency for its corners). A DOCKED bar is a shell app bar, so the shell keeps
// every window out of its strip and the only thing ever behind it is the wallpaper. The bar draws that itself:
//
//   1. DesktopWallpaper.On asks Windows which picture is on the bar's screen and how it is laid (fill, fit, span...).
//   2. WallpaperCrop.SourceOf works out which part of the picture is behind the bar's window.
//   3. That part is cut, shrunk to half and frosted (Frost.Apply: blurred by Manage's slider, 8 screen pixels unless
//      moved, colour lifted by 1.6), then eased out of the untouched picture across the side facing the desktop
//      (Frost.Ease), and painted on FrostSurface, which covers the whole bar.
//   4. The top and the footer stay opaque over it (TopSurface, FootSurface), recent rows keep their opaque plate,
//      and the rows band lays BarSurface.FrostVeilFrom (half the bar's colour, easing in from the inner edge the
//      same way) over the frost, so the captions read.
//
// The first frost blurred by about 28 screen pixels with a hard inner edge. Petre, on his laptop: "it's too blurry",
// "make it a setting?", "ease into it" (the edge), "make edges less pronounced". Hence the slider (FrostBlur) and
// the ease.
//
// Steps 1 to 3 run on a pool thread: decoding a 4K picture takes tens of milliseconds, and the COM call must not run
// on the bar's thread, where it would pump messages in the middle of a rebuild (CLAUDE.md, traps).
//
// THE FLOATING BAR is frosted too (task b971ca3f, Petre: "Undocked bar style / transparency should match that of the
// docked", then "a" from the mockup at https://claude.ai/artifact/GfVqsCbLiZ6LMBcUU6W1Lb). A floating bar has windows
// behind it, so this is not what lies behind it: it is the wallpaper, the way Windows 11's own Mica material tints a
// window from the desktop picture and never from the windows under it. The other choice, Windows blurring whatever
// is behind (the Acrylic accent), was turned down: the bar is a layered window for its round corners, Windows' blur
// fills the window's square, its strength is Windows' and not the slider's, and docked would still look different.
//
// A floating bar moves, so it does not cut its own strip on every move. It frosts its screen's WHOLE picture once
// (the same Frosted, with the screen as the "bar" and no ease, because no side of it faces the desktop) and shows the
// slice under its window through the brush's Viewbox (FollowFloatingFrost), which a drag updates at once, on every
// move, for nothing. Only a new picture, fit, screen or blur strength makes a new frost, and the last one is kept
// while hidden, so a drag onto the next screen and back does not redo the blur.
//
// When there is no frost, the band is exactly what it was (BarSurface.SeeThrough, 85%): a bar across the seam spans
// two screens and two pictures, as does a floating bar dragged half onto the next screen, a tiled picture repeats, a
// solid-colour wallpaper has no picture, and Fit or Center can leave the bar over background colour (a floating bar
// over Fit or Center falls back whenever the picture does not cover its WHOLE screen, because it frosts the whole
// screen). Each of those is traced, so "why is my band dark" has an answer in the log.
//
// When it is redone. Only when something it was made from changed (the picture file or its write time, the fit, the
// bar's rectangle, its screen), checked at most twice a second while the bar is busy, after every dock, move and
// resize, when Windows announces a new wallpaper, and once a minute for Spotlight, which changes the picture without
// always announcing it.
public partial class FloatingBar
{
    // What the frost on screen was made from, so a check that finds nothing changed costs a COM call and a file stat
    // and nothing else. Null when no frost is showing.
    FrostSource? frostSource;

    // Coalesces the many reasons to check into one check: started when idle, never restarted, so a busy bar is
    // checked twice a second rather than postponed for ever.
    readonly DispatcherTimer frostDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Spotlight replaces the picture once a day, and does not always broadcast the change.
    readonly DispatcherTimer frostWatch = new() { Interval = TimeSpan.FromMinutes(1) };

    // Bumped by every check, so a slow check that finishes after a newer one has started is thrown away.
    int frostGeneration;

    // The last reason the band went without frost, so the trace says it once and not on every check.
    string? frostFallbackTraced;

    // The floating bar's frost of its whole screen, kept while it is hidden (the bar dragged half onto the next screen,
    // or docked for a while), so coming back to that screen shows it again without redoing the blur. Its picture is
    // frozen; the brush that shows it is not, because a move changes its Viewbox (FollowFloatingFrost).
    (FrostSource Source, BitmapSource Image)? floatingFrost;
    ImageBrush? floatingBrush;

    const int WM_SETTINGCHANGE = 0x001A;
    const int SPI_SETDESKWALLPAPER = 0x0014;

    // Wired once, from the constructor.
    void WatchFrost()
    {
        frostDelay.Tick += (_, _) =>
        {
            frostDelay.Stop();
            _ = RefreshFrost();
        };
        frostWatch.Tick += (_, _) => ScheduleFrost();
        frostWatch.Start();
        // A floating frost follows the window at once (only its slice moves); every move also schedules the check,
        // which notices a new screen or a dock.
        SizeChanged += (_, _) => { FollowFloatingFrost(); ScheduleFrost(); };
        LocationChanged += (_, _) => { FollowFloatingFrost(); ScheduleFrost(); };
        // Windows announces a new wallpaper to every top-level window with WM_SETTINGCHANGE / SPI_SETDESKWALLPAPER.
        SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((nint _, int message, nint wParam, nint _, ref bool _) =>
            {
                if (message == WM_SETTINGCHANGE && wParam == SPI_SETDESKWALLPAPER) ScheduleFrost();
                return 0;
            });
        Closed += (_, _) => { frostDelay.Stop(); frostWatch.Stop(); };
    }

    void ScheduleFrost()
    {
        if (!frostDelay.IsEnabled) frostDelay.Start();
    }

    async Task RefreshFrost()
    {
        var generation = ++frostGeneration;
        var hwnd = new WindowInteropHelper(this).Handle;

        // An edge dock or floating. Across the seam the bar spans two screens and two pictures.
        if (!manager.State.SeeThrough || dockedEdge is not (null or "left" or "right") || Minimized || hwnd == 0)
        {
            ShowFrost(null, manager.State.SeeThrough && !Minimized ? "the bar is docked across the seam" : null);
            return;
        }
        if (!NativeMethods.GetWindowRect(hwnd, out var window)) return;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        var bar = new WallpaperCrop.Rect(window.Left, window.Top, window.Right, window.Bottom);
        var screen = new WallpaperCrop.Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        var floating = dockedEdge is null;

        // A floating bar half on the next screen has no one picture behind it. Checked here, before any work, so a
        // drag across the seam costs nothing until the bar settles on one screen.
        if (floating && WallpaperCrop.ShareOf(screen, bar).HasNoValue)
        {
            ShowFrost(null, "the floating bar is across two screens");
            return;
        }

        // A floating bar frosts its whole screen and compares against the frost it keeps, shown or not; a docked one
        // cuts its own strip and compares against the frost on screen.
        var previous = floating ? floatingFrost?.Source : frostSource;

        // The strength from Manage's slider, and the side that faces the desktop, where a docked frost eases out of
        // the sharp picture: the left of a bar docked on the right of its screen. A floating frost does not ease.
        var blur = FrostBlur.Clamp(manager.State.FrostBlur);
        var fromLeft = dockedEdge == "right";
        // How much of the bar's width that edge eases over: a fixed few DIP (FrostEdge), so it is turned into a share
        // here, from the window's width in screen pixels and its screen's own scale. The monitor's DPI, not
        // VisualTreeHelper.GetDpi(this), which can lag a move between screens (see the GetDpiForMonitor note in
        // FloatingBar.xaml.cs). None for a floating frost, which does not ease.
        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _);
        var ease = floating ? 0 : FrostEdge.ShareOf(bar.Right - bar.Left, dpi / 96.0);
        var made = await Task.Run(() => MakeFrost(floating ? screen : bar, screen, blur, fromLeft, ease, floating, previous));
        if (generation != frostGeneration) return;

        made.Match(
            frost =>
            {
                if (frost is not null) ShowFrost(frost, null);
                // Nothing changed. Docked, the frost on screen stands. Floating, the kept frost may be hidden (the bar
                // came back from the next screen, or was just undocked), so it goes back up if it is not showing.
                else if (floating && floatingFrost is { } kept && frostSource != kept.Source) ShowFloatingFrost(kept.Source, kept.Image);
            },
            reason => ShowFrost(null, reason));
    }

    // What the frost is cut from: when every field matches the frost on screen, there is nothing to redo. Ease is the
    // share of the bar's width its inner edge eases over (FrostEdge.ShareOf); the band's veil reads it from here, so
    // the veil and the picture always share one ramp. Floating means the frost is of the whole screen (Bar is then the
    // screen, Ease is 0) and is shown a slice at a time.
    internal sealed record FrostSource(
        string Path, DateTime Written, WallpaperFit Fit, WallpaperCrop.Rect Bar, WallpaperCrop.Rect Screen, double Blur, bool FromLeft,
        double Ease, bool Floating);

    // A finished frost: its source, and its pixels (Bgra32, Width x Height).
    internal sealed record FrostImage(FrostSource Source, byte[] Pixels, int Width, int Height);

    // Runs on a pool thread. Success(null) means nothing changed and the frost on screen stands; a failure is the
    // reason there is no frost, for the trace.
    internal static Result<FrostImage?> MakeFrost(
        WallpaperCrop.Rect bar, WallpaperCrop.Rect screen, double blur, bool fromLeft, double ease, bool floating, FrostSource? previous)
    {
        if (DesktopWallpaper.On(screen) is not { HasValue: true } found)
            return Result.Failure<FrostImage?>("Windows reports no picture file for the bar's screen");
        var wallpaper = found.Value;
        var source = new FrostSource(wallpaper.Path, File.GetLastWriteTimeUtc(wallpaper.Path), wallpaper.Fit, bar, screen, blur, fromLeft, ease, floating);
        if (source == previous) return Result.Success<FrostImage?>(null);

        try
        {
            BitmapSource picture;
            using (var stream = File.OpenRead(wallpaper.Path))
                picture = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

            return WallpaperCrop.SourceOf(wallpaper.Fit, wallpaper.Screen, wallpaper.Desktop, picture.PixelWidth, picture.PixelHeight, bar)
                .ToResult($"the bar is not over one part of the picture (laid as {wallpaper.Fit})")
                .Map(area => Frosted(picture, area, bar, source));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return Result.Failure<FrostImage?>($"the picture would not load ({e.GetType().Name}: {e.Message})");
        }
    }

    static FrostImage? Frosted(BitmapSource picture, WallpaperCrop.Area area, WallpaperCrop.Rect bar, FrostSource source)
    {
        var cut = new Int32Rect(
            (int)Math.Floor(area.X), (int)Math.Floor(area.Y),
            Math.Max(1, (int)Math.Round(area.Width)), Math.Max(1, (int)Math.Round(area.Height)));
        cut.Width = Math.Min(cut.Width, picture.PixelWidth - cut.X);
        cut.Height = Math.Min(cut.Height, picture.PixelHeight - cut.Y);

        // Worked at half size (FrostBlur.Shrink): cheap, and the sharp edge it eases out of still matches the
        // wallpaper beside the bar.
        var width = Math.Max(4, bar.Width / FrostBlur.Shrink);
        var height = Math.Max(4, bar.Height / FrostBlur.Shrink);
        var shrunk = new TransformedBitmap(new CroppedBitmap(picture, cut),
            new ScaleTransform((double)width / cut.Width, (double)height / cut.Height));
        var bgra = new FormatConvertedBitmap(shrunk, PixelFormats.Bgra32, null, 0);

        // The scaled size can round a pixel either way, so the buffer follows what WPF actually made.
        var (w, h) = (bgra.PixelWidth, bgra.PixelHeight);
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        // Blurred to the slider's strength with the colour lifted, then, docked, eased out of the untouched picture
        // across the side that faces the desktop, so the bar's edge is not a hard line down the screen. A floating
        // frost is the whole screen and has no such side: the bar floats over it with its own rounded edge.
        var frosted = Frost.Apply(pixels, w, h, radius: FrostBlur.RadiusAt(source.Blur), saturation: 1.6);
        return new FrostImage(source, source.Floating ? frosted : Frost.Ease(pixels, frosted, w, h, source.Ease, source.FromLeft), w, h);
    }

    // Puts a frost on the bar, or takes it off (null) with the reason why.
    void ShowFrost(FrostImage? frost, string? reason)
    {
        if (frost is null)
        {
            if (reason is not null && reason != frostFallbackTraced && ClickTrace.On) ClickTrace.Write($"frost: none, {reason}");
            frostFallbackTraced = reason;
            frostSource = null;
            floatingBrush = null;
            FrostSurface.Background = null;
            PaintBand();
            return;
        }

        var image = BitmapSource.Create(frost.Width, frost.Height, 96, 96, PixelFormats.Bgra32, null, frost.Pixels, frost.Width * 4);
        image.Freeze();
        if (frost.Source.Floating)
        {
            // Kept for coming back to this screen; the pixel array is dropped with the FrostImage.
            floatingFrost = (frost.Source, image);
            ShowFloatingFrost(frost.Source, image);
            return;
        }

        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        // Smooth scaling: the frost is half the bar's size and must not show its pixels.
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();

        frostSource = frost.Source;
        floatingBrush = null;
        frostFallbackTraced = null;
        FrostSurface.Background = brush;
        PaintBand();
        if (ClickTrace.On)
            ClickTrace.Write($"frost: {Path.GetFileName(frost.Source.Path)} laid as {frost.Source.Fit}, blur {frost.Source.Blur} px easing from the {(frost.Source.FromLeft ? "left" : "right")} over {frost.Source.Ease:P1} of its width, bar {frost.Source.Bar.Left},{frost.Source.Bar.Top} to "
                + $"{frost.Source.Bar.Right},{frost.Source.Bar.Bottom} on screen {frost.Source.Screen.Left},{frost.Source.Screen.Top} to "
                + $"{frost.Source.Screen.Right},{frost.Source.Screen.Bottom}, frost {frost.Width}x{frost.Height}");
    }

    // Puts the floating bar's frost of its whole screen up, showing the slice under the window. The brush is the
    // bar's own and not frozen, because every move changes its Viewbox.
    void ShowFloatingFrost(FrostSource source, BitmapSource image)
    {
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
        // Smooth scaling: the frost is half the screen's size and must not show its pixels.
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);

        frostSource = source;
        floatingBrush = brush;
        frostFallbackTraced = null;
        FrostSurface.Background = brush;
        if (!FollowFloatingFrost()) return;
        PaintBand();
        if (ClickTrace.On)
            ClickTrace.Write($"frost: floating, {Path.GetFileName(source.Path)} laid as {source.Fit}, blur {source.Blur} px, screen "
                + $"{source.Screen.Left},{source.Screen.Top} to {source.Screen.Right},{source.Screen.Bottom}, frost {image.PixelWidth}x{image.PixelHeight}");
    }

    // Moves the floating frost's slice to where the window now is: called on every move and resize, and cheap (one
    // GetWindowRect and a Viewbox). False, and the frost taken off, when the window has left the frost's screen; the
    // check that every move also schedules then frosts the new screen once the bar is wholly on it. Does nothing
    // while the frost on show is a docked one, or there is none.
    bool FollowFloatingFrost()
    {
        if (floatingBrush is null || frostSource is not { Floating: true } source) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var window)) return false;

        var share = WallpaperCrop.ShareOf(source.Screen, new WallpaperCrop.Rect(window.Left, window.Top, window.Right, window.Bottom));
        if (share.HasNoValue)
        {
            ShowFrost(null, "the floating bar left the frost's screen");
            return false;
        }
        var slice = share.Value;
        floatingBrush.Viewbox = new Rect(slice.X, slice.Y, slice.Width, slice.Height);
        return true;
    }
}
