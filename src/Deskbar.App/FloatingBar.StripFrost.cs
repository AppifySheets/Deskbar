using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Core.Imaging;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE SCREEN STRIPS' FROST (Deskbar task d52ca36e). Petre, showing the bar's flat top: "i want to make this like the rest
// of the bar; and the top bar too". The top bar is the screen strips (ScreenStrip), which were flat BarSurface.Solid.
//
// A strip is a shell app bar along its screen's top edge, so the shell keeps every window out of it and the only thing
// behind it is the wallpaper, exactly the docked bar's case (FloatingBar.Frost.cs). So each strip gets the docked bar's
// treatment: its own slice of its screen's picture, cut, shrunk, blurred by Manage's slider and colour-lifted by
// MakeFrost, painted as the strip's background under the band's veil (ScreenStrip.ShowFrost).
//
// No ease. The docked bar eases its frost out of the sharp picture across the side facing the desktop, so its edge is
// not a hard line down the screen. A strip spans its screen's full width, and its one free side, the bottom, meets the
// windows the shell keeps below it, not the desktop. MakeFrost's `floating` is what skips the ease (a floating frost has
// no side facing the desktop either), so it is passed as true here, with the strip's own rectangle as the "bar".
//
// When it is redone: with the bar's own frost check (RefreshFrost calls this first), so on every rebuild, move, new
// wallpaper and the one-minute Spotlight watch, at most twice a second. A check where nothing changed costs a COM call
// and a file stat per strip, on a pool thread.
//
// Without frost (see-through off in Manage, a solid-colour or tiled wallpaper, a picture laid Fit or Center that leaves
// the strip over background colour) the strip is flat, as before. Each fallback is traced once per strip.
public partial class FloatingBar
{
    // One strip check at a time. The bar's check starts one every half second while rebuilds keep coming, and a pass
    // that decodes a 4K picture per strip can take longer than that; throwing a slow pass away for a newer one (as the
    // bar's own check does) could leave the strips flat for as long as the bar stays busy. So a check asked for while
    // one runs is noted, and runs once, right after it, reading everything afresh.
    bool stripFrostRunning;
    bool stripFrostAgain;

    async Task RefreshStripFrost()
    {
        if (stripFrostRunning)
        {
            stripFrostAgain = true;
            return;
        }
        stripFrostRunning = true;
        try
        {
            await CheckStripFrost();
        }
        finally
        {
            stripFrostRunning = false;
        }
        if (!stripFrostAgain) return;
        stripFrostAgain = false;
        await RefreshStripFrost();
    }

    async Task CheckStripFrost()
    {
        var strips = StripsOn ? Strips() : [];
        if (strips.Count == 0) return;

        if (!manager.State.SeeThrough)
        {
            strips.ToList().ForEach(strip => TakeStripFrost(strip, null, null));
            return;
        }

        // Read on this thread: each strip's rectangle as the shell granted it, its screen, and the frost it shows now.
        var blur = FrostBlur.Clamp(manager.State.FrostBlur);
        var jobs = strips
            .Select(strip => (Strip: strip, Hwnd: new WindowInteropHelper(strip).Handle))
            .Where(job => job.Hwnd != 0)
            .Select(job => NativeMethods.GetWindowRect(job.Hwnd, out var window)
                ? (job.Strip, Rect: new WallpaperCrop.Rect(window.Left, window.Top, window.Right, window.Bottom),
                    Shown: job.Strip.FrostSource, Failed: job.Strip.FrostFailedFor)
                : default)
            .Where(job => job.Strip is not null)
            .ToList();

        var made = await Task.Run(() => jobs.Select(job => (job.Strip, Frost: MakeStripFrost(job.Strip, job.Rect, blur, job.Shown, job.Failed))).ToList());

        // A strip closed while the pass ran (a screen went) is skipped; the rest take what was made for them.
        made.Where(job => job.Strip.IsLoaded).ToList().ForEach(job => job.Frost.Match(
            frost => { if (frost is not null) ShowStripFrost(job.Strip, frost); },
            failed => { if (failed.Reason is not null) TakeStripFrost(job.Strip, failed.Reason, failed.Source); }));
    }

    // One strip's frost, on a pool thread. Success(null): nothing to change (the frost on show still matches, or the
    // last failure does and the strip stays flat). A failure carries its reason and, when Windows named a picture, the
    // key it failed for, so the next check can skip the picture's decode until the picture, its fit, the strip's
    // rectangle or the blur changes. Without that, a picture laid Fit that leaves the strip over background colour
    // would be decoded on every check, twice a second per strip while the bar is busy (review of task d52ca36e).
    static Result<FrostImage?, (string? Reason, FrostSource? Source)> MakeStripFrost(
        ScreenStrip strip, WallpaperCrop.Rect rect, double blur, FrostSource? shown, FrostSource? failed)
    {
        var screen = new WallpaperCrop.Rect(strip.Screen.Left, strip.Screen.Top, strip.Screen.Right, strip.Screen.Bottom);
        // `floating`: no ease, see the top of this file.
        var key = SourceFor(rect, screen, blur, fromLeft: false, ease: 0, floating: true);
        if (key.HasValue && (key.Value == shown || key.Value == failed)) return Result.Success<FrostImage?, (string?, FrostSource?)>(null);
        return MakeFrost(rect, screen, blur, fromLeft: false, ease: 0, floating: true, previous: null)
            .MapError(reason => ((string?)reason, key.HasValue ? key.Value : null));
    }

    static void ShowStripFrost(ScreenStrip strip, FrostImage frost)
    {
        var image = BitmapSource.Create(frost.Width, frost.Height, 96, 96, PixelFormats.Bgra32, null, frost.Pixels, frost.Width * 4);
        image.Freeze();
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        // Smooth scaling: the frost is half the strip's size and must not show its pixels.
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();

        strip.FrostFallback = null;
        strip.FrostFailedFor = null;
        strip.ShowFrost(frost.Source, brush);
        if (ClickTrace.On)
            ClickTrace.Write($"strip frost: {Path.GetFileName(frost.Source.Path)} laid as {frost.Source.Fit}, blur {frost.Source.Blur} px, strip "
                + $"{frost.Source.Bar.Left},{frost.Source.Bar.Top} to {frost.Source.Bar.Right},{frost.Source.Bar.Bottom}, frost {frost.Width}x{frost.Height}");
    }

    // Takes a strip's frost off, and traces why once (no reason: the setting is off, which needs no line). `failedFor`
    // is the key the frost failed for, remembered so the next check does not decode the picture again for nothing.
    static void TakeStripFrost(ScreenStrip strip, string? reason, FrostSource? failedFor)
    {
        if (reason is not null && reason != strip.FrostFallback && ClickTrace.On)
            ClickTrace.Write($"strip frost: none on {strip.Screen.Left},{strip.Screen.Top} to {strip.Screen.Right},{strip.Screen.Bottom}, {reason}");
        strip.FrostFallback = reason;
        strip.FrostFailedFor = failedFor;
        strip.ShowFrost(null, null);
    }
}
