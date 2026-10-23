using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// Petre: "language button needs to be clicked twice". The trace of those clicks: for the first two minutes after
// every start the bar rebuilt itself every second, at 200 to 740 ms a time over RDP at 250%, so a click that landed
// during one waited, nothing changed on screen, the second click followed, and the two ran back to back and switched
// the language there and back. The rebuilds came from the icon watch, which redrew the whole bar on every tick while
// any window was still without an icon of its own, whether or not one had arrived. It now asks those windows itself
// (IconCache.ProbePending) and redraws only when one has.
public class IconCacheWatchTests
{
    // Opaque, so IconCache.IsBlank does not throw it away as a loading PWA's empty icon.
    static BitmapSource OpaqueIcon()
    {
        const int size = 32;
        var stride = size * 4;
        var pixels = Enumerable.Range(0, stride * size).Select(i => i % 4 == 3 ? (byte)0xFF : (byte)0x80).ToArray();
        var icon = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        icon.Freeze();
        return icon;
    }

    static Window OffScreenWindow() => new() { Left = -32000, Top = -32000, Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false };

    [Fact]
    public void A_window_whose_icon_arrives_is_reported_once_by_the_next_probe() => StaThread.Run(() =>
    {
        var window = OffScreenWindow();
        window.Show();
        var handle = new WindowHandle(new WindowInteropHelper(window).Handle);
        IconCache.For(handle, null);
        Assert.True(IconCache.HasPendingIcons, "a window with no icon of its own should still be being asked");

        window.Icon = OpaqueIcon();
        // The probe is rate-limited to once a second per window (IconCache.ProbeInterval).
        Thread.Sleep(1100);

        Assert.True(IconCache.ProbePending() >= 1);
        // Answered for good: no longer pending, so no later tick redraws for it again.
        Assert.False(IconCache.IsPending(handle, null));
        window.Close();
    });

    [Fact]
    public void A_window_still_without_an_icon_is_not_news() => StaThread.Run(() =>
    {
        var window = OffScreenWindow();
        window.Show();
        var handle = new WindowHandle(new WindowInteropHelper(window).Handle);
        IconCache.For(handle, null);
        Thread.Sleep(1100);

        // Other tests can leave windows of their own pending, so this asks about this window only: still pending,
        // and so nothing about it to redraw for.
        IconCache.ProbePending();
        Assert.True(IconCache.IsPending(handle, null));
        window.Close();
    });
}
