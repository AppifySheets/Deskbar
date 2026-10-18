using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;

namespace TaskSpaces.App;

// AGING ICONS. Petre: "let's age icons in workspaces", then "a" from three mockups: an icon fades to grey the
// longer its window goes unused, so the windows in use stand out from the ones left alone.
//
//   up to 15 minutes   full colour
//   then               fading, most of it in the first hours (eased by ^0.7, as in the mockup)
//   8 hours and on     fully grey, and 15% darker
//
// COLOUR, not opacity, and that is the whole design: opacity on this bar already means "behind another
// window" and "minimised" (WithState's ladder), and the two must not fight. Drawn as a grey copy of the icon
// laid over the coloured one, with the age as the copy's opacity, which blends the two exactly; WPF has no
// built-in grayscale for an element, and a pixel shader would mean shipping compiled HLSL for one effect.
//
// "Used" is the manager's to say (WorkspaceManager.LastUsed): a window is stamped when it first becomes known
// and whenever it comes to the front. The 15 minutes and 8 hours are first guesses, to tune.
public partial class FloatingBar
{
    static readonly TimeSpan AgingStarts = TimeSpan.FromMinutes(15);
    static readonly TimeSpan FullyAged = TimeSpan.FromHours(8);

    // The grey overlays the current rows carry, so the clock can age them in place every minute without a
    // rebuild. Emptied at the start of every rebuild, which replaces them all.
    readonly List<(WindowHandle Window, UIElement Grey)> agingIcons = [];
    readonly DispatcherTimer agingTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    // One grey copy per icon image, kept as long as the image itself is.
    static readonly ConditionalWeakTable<ImageSource, ImageSource> greyCopies = new();

    void StartAging()
    {
        agingTimer.Tick += (_, _) => agingIcons.ForEach(a => a.Grey.Opacity = AgeOf(a.Window));
        agingTimer.Start();
        Closed += (_, _) => agingTimer.Stop();
    }

    void ForgetAgingIcons() => agingIcons.Clear();

    // 0 for fresh, rising to 1 when fully aged.
    internal static double AgeOf(TimeSpan unused) =>
        unused <= AgingStarts ? 0 : Math.Pow(Math.Min(1, (unused - AgingStarts) / (FullyAged - AgingStarts)), 0.7);

    double AgeOf(WindowHandle window) => manager.LastUsed(window) is { } at ? AgeOf(manager.Now - at) : 0;

    // An icon's artwork, with its grey copy over it at the window's age. The window you are IN is never aged:
    // being in it is using it, whatever the last stamp says.
    UIElement Aged(ImageSource icon, WindowRow row)
    {
        var colour = new Image { Source = icon, Width = 20, Height = 20 };
        if (GreyOf(icon) is not { } grey) return colour;
        var overlay = new Image
        {
            Source = grey,
            Width = 20,
            Height = 20,
            IsHitTestVisible = false,
            Opacity = row.IsActive ? 0 : AgeOf(row.Window.Handle),
        };
        agingIcons.Add((row.Window.Handle, overlay));
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
