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

// THE FROSTED BAND: while the bar is docked, the band behind its idle workspace rows shows the wallpaper behind it,
// blurred and with its colour lifted, under a light veil, instead of 85% of the bar's near-black.
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
// When there is no frost, the band is exactly what it was (BarSurface.SeeThrough, 85%): a FLOATING bar has windows
// behind it, a bar across the seam spans two screens and two pictures, a tiled picture repeats, a solid-colour
// wallpaper has no picture, and Fit or Center can leave the bar over background colour. Each of those is traced, so
// "why is my band dark" has an answer in the log.
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
        SizeChanged += (_, _) => ScheduleFrost();
        LocationChanged += (_, _) => ScheduleFrost();
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

        // Only an edge dock: floating, other windows are behind the bar; across the seam, two screens are.
        if (!manager.State.SeeThrough || dockedEdge is not ("left" or "right") || Minimized || hwnd == 0)
        {
            ShowFrost(null, manager.State.SeeThrough ? "the bar is not docked on one screen's edge" : null);
            return;
        }
        if (!NativeMethods.GetWindowRect(hwnd, out var window)) return;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        var bar = new WallpaperCrop.Rect(window.Left, window.Top, window.Right, window.Bottom);
        var screen = new WallpaperCrop.Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        var previous = frostSource;

        // The strength from Manage's slider, and the side that faces the desktop, where the frost eases out of the
        // sharp picture: the left of a bar docked on the right of its screen.
        var blur = FrostBlur.Clamp(manager.State.FrostBlur);
        var fromLeft = dockedEdge == "right";
        var made = await Task.Run(() => MakeFrost(bar, screen, blur, fromLeft, previous));
        if (generation != frostGeneration) return;

        made.Match(
            frost => { if (frost is not null) ShowFrost(frost, null); },
            reason => ShowFrost(null, reason));
    }

    // What the frost is cut from: when every field matches the frost on screen, there is nothing to redo.
    internal sealed record FrostSource(
        string Path, DateTime Written, WallpaperFit Fit, WallpaperCrop.Rect Bar, WallpaperCrop.Rect Screen, double Blur, bool FromLeft);

    // A finished frost: its source, and its pixels (Bgra32, Width x Height).
    internal sealed record FrostImage(FrostSource Source, byte[] Pixels, int Width, int Height);

    // Runs on a pool thread. Success(null) means nothing changed and the frost on screen stands; a failure is the
    // reason there is no frost, for the trace.
    internal static Result<FrostImage?> MakeFrost(
        WallpaperCrop.Rect bar, WallpaperCrop.Rect screen, double blur, bool fromLeft, FrostSource? previous)
    {
        if (DesktopWallpaper.On(screen) is not { HasValue: true } found)
            return Result.Failure<FrostImage?>("Windows reports no picture file for the bar's screen");
        var wallpaper = found.Value;
        var source = new FrostSource(wallpaper.Path, File.GetLastWriteTimeUtc(wallpaper.Path), wallpaper.Fit, bar, screen, blur, fromLeft);
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

    // How far in from the bar's inner edge the frost takes to reach full strength, as a share of the bar's width
    // (Frost.Ease). The veil eases over the same share (BarSurface.FrostVeilFrom), so picture and veil arrive
    // together. A third: Petre asked for the edge to be "less pronounced", and a third of a 210 DIP bar is about
    // 70 DIP, the width of an icon and a half, so the ramp is gentle without reaching the captions' gutter.
    internal const double FrostEase = 0.35;

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
        // Blurred to the slider's strength with the colour lifted, then eased out of the untouched picture across the
        // side that faces the desktop, so the bar's edge is not a hard line down the screen.
        var frosted = Frost.Apply(pixels, w, h, radius: FrostBlur.RadiusAt(source.Blur), saturation: 1.6);
        return new FrostImage(source, Frost.Ease(pixels, frosted, w, h, FrostEase, source.FromLeft), w, h);
    }

    // Puts a frost on the bar, or takes it off (null) with the reason why.
    void ShowFrost(FrostImage? frost, string? reason)
    {
        if (frost is null)
        {
            if (reason is not null && reason != frostFallbackTraced && ClickTrace.On) ClickTrace.Write($"frost: none, {reason}");
            frostFallbackTraced = reason;
            frostSource = null;
            FrostSurface.Background = null;
            PaintBand();
            return;
        }

        var image = BitmapSource.Create(frost.Width, frost.Height, 96, 96, PixelFormats.Bgra32, null, frost.Pixels, frost.Width * 4);
        image.Freeze();
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        // Smooth scaling: the frost is half the bar's size and must not show its pixels.
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();

        frostSource = frost.Source;
        frostFallbackTraced = null;
        FrostSurface.Background = brush;
        PaintBand();
        if (ClickTrace.On)
            ClickTrace.Write($"frost: {Path.GetFileName(frost.Source.Path)} laid as {frost.Source.Fit}, blur {frost.Source.Blur} px easing from the {(frost.Source.FromLeft ? "left" : "right")}, bar {frost.Source.Bar.Left},{frost.Source.Bar.Top} to "
                + $"{frost.Source.Bar.Right},{frost.Source.Bar.Bottom} on screen {frost.Source.Screen.Left},{frost.Source.Screen.Top} to "
                + $"{frost.Source.Screen.Right},{frost.Source.Screen.Bottom}, frost {frost.Width}x{frost.Height}");
    }
}
