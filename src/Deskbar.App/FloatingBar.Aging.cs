using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.App;

// AGING ICONS. Petre: "let's age icons in workspaces", then "a" from three mockups: an icon fades to grey the
// longer its window goes unused, so the windows in use stand out from the ones left alone.
//
//   up to 5 minutes    full colour
//   then               fading, most of it early (eased by ^0.7, as in the mockup)
//   2 hours and on     fully grey, and 15% darker
//
// COLOUR, not opacity, and that is the whole design: opacity on this bar already means "behind another
// window" and "minimised" (WithState's ladder), and the two must not fight. Drawn as a grey copy of the icon
// laid over the coloured one, with the age as the copy's opacity, which blends the two exactly; WPF has no
// built-in grayscale for an element, and a pixel shader would mean shipping compiled HLSL for one effect.
//
// "Used" is the manager's to say (WorkspaceManager.LastUsed): a window is stamped whenever it comes to the front,
// and a window never focused since it opened has no stamp and is drawn fully aged. The ends were 15 minutes and
// 8 hours until Petre: "it should decay much faster" (WorkspaceFade, which owns the curve).
//
// BY WORKSPACE, NOT BY WINDOW, on a workspace's row. Petre: "let's fade windows not based on individual age, but
// the age of the workspace". So every icon in a workspace row takes that row's fade (FloatingBar.Recency.cs: the time
// since ANY of its windows was used), and the row's icons are one shade, fading together with its rail and name.
// What that gives up: inside one busy row, the bar no longer shows which windows were left alone; the hover card's
// "last used 2 h ago" still does, per window. Rows that are not workspaces (the pinned row, unbound desktops) keep
// aging each window on its own, as before.
public partial class FloatingBar
{
    // The grey overlays the current rows carry, with the workspace row each sits on (null on a row that is not a
    // workspace), so the clock can age them in place every minute without a rebuild. Emptied at the start of every
    // rebuild, which replaces them all.
    readonly List<(Guid? Workspace, WindowHandle Window, UIElement Grey)> agingIcons = [];
    readonly DispatcherTimer agingTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    // One grey copy per icon image, kept as long as the image itself is.
    static readonly ConditionalWeakTable<ImageSource, ImageSource> greyCopies = new();

    void StartAging()
    {
        agingTimer.Tick += (_, _) =>
        {
            agingIcons.ForEach(a => a.Grey.Opacity = FadeOf(a.Workspace, a.Window));
            // The rails, tints and names are drawn at a STEP of the fade, so they change only when a step moves: a
            // rebuild then, every few minutes while a row is fading, and never otherwise (FloatingBar.Recency.cs, RefreshFades).
            RefreshFades();
        };
        agingTimer.Start();
        Closed += (_, _) => agingTimer.Stop();
    }

    void ForgetAgingIcons() => agingIcons.Clear();

    // 0 for fresh, rising to 1 when fully aged. The curve lives in Core now (WorkspaceFade.Curve), because the
    // workspace rows fade on the same one and the two must not drift apart.
    internal static double AgeOf(TimeSpan unused) => WorkspaceFade.Curve(unused);

    // No stamp means never focused since it opened, which is not use: fully aged, as WorkspaceFade treats a workspace
    // with no stamped windows.
    double AgeOf(WindowHandle window) => manager.LastUsed(window) is { } at ? AgeOf(manager.Now - at) : 1;

    // An icon's artwork, with its grey copy over it at its workspace row's fade (or, off a workspace row, the window's
    // own age). The window you are IN is never aged: being in it is using it, whatever the last stamp says.
    UIElement Aged(ImageSource icon, WindowRow row, Guid? workspace)
    {
        var colour = new Image { Source = icon, Width = 20, Height = 20 };
        if (GreyOf(icon) is not { } grey) return colour;
        var overlay = new Image
        {
            Source = grey,
            Width = 20,
            Height = 20,
            IsHitTestVisible = false,
            Opacity = row.IsActive ? 0 : FadeOf(workspace, row.Window.Handle),
        };
        agingIcons.Add((workspace, row.Window.Handle, overlay));
        return new Grid { Children = { colour, overlay } };
    }

    static ImageSource? GreyOf(ImageSource icon) =>
        icon is BitmapSource bitmap ? greyCopies.GetValue(icon, _ => Grey(bitmap)) : null;

    // Luminance per pixel, alpha untouched, and 15% darker, so a fully aged icon also sits back a little.
    static ImageSource Grey(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luminance = (byte)Math.Min(255, (0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2]) * 0.85);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = luminance;
        }
        var grey = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        grey.Freeze();
        return grey;
    }

    // For the hover card: "last used 2 h ago", or nothing for a window used in the last minute.
    string? LastUsedText(WindowHandle window)
    {
        if (manager.LastUsed(window) is not { } at) return null;
        var ago = manager.Now - at;
        return ago < TimeSpan.FromMinutes(1) ? null
            : ago < TimeSpan.FromHours(1) ? $"last used {(int)ago.TotalMinutes} min ago"
            : ago < TimeSpan.FromDays(1) ? $"last used {(int)ago.TotalHours} h ago"
            : $"last used {(int)ago.TotalDays} d ago";
    }
}
