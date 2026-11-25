using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// Task 0bd79d0e, "give claude sessions claude icon instead of powershell". A window Deskbar opens on a Claude session
// is a Windows Terminal window, and Windows Terminal answers WM_GETICON with its own logo whatever its tab runs
// (measured), so the bar drew every session as a terminal. IconCache now draws Claude's icon, read from claude.exe, on a
// Windows Terminal window titled "<task> · Claude", and the terminal's icon on any other.
//
// A CI runner has no Claude Code, so the tests point IconCache.ClaudeExecutable at notepad.exe, which every Windows has
// and which carries an icon. The windows are real (off screen) and carry an icon of their own, so "the window's own
// icon" and "Claude's icon" are two different pictures the assertions can tell apart.
public class IconCacheClaudeWindowTests
{
    const string Terminal = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.25.3131.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe";

    static readonly string StandIn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");

    // Opaque, so IconCache.IsBlank does not throw it away as a loading PWA's empty icon.
    static BitmapSource OwnIcon()
    {
        const int size = 32;
        var stride = size * 4;
        var pixels = Enumerable.Range(0, stride * size).Select(i => i % 4 == 3 ? (byte)0xFF : (byte)0x40).ToArray();
        var icon = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        icon.Freeze();
        return icon;
    }

    static (Window Window, WindowHandle Handle) OffScreenWindow()
    {
        var window = new Window { Left = -32000, Top = -32000, Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false, Icon = OwnIcon() };
        window.Show();
        return (window, new WindowHandle(new WindowInteropHelper(window).Handle));
    }

    // Runs body with ClaudeExecutable pointing at exe, and puts the real one back after.
    static void WithClaudeAt(string? exe, Action body)
    {
        var real = IconCache.ClaudeExecutable;
        IconCache.ClaudeExecutable = () => exe;
        try { body(); }
        finally { IconCache.ClaudeExecutable = real; }
    }

    [Fact]
    public void Every_session_window_is_drawn_with_the_claude_icon_and_grouped_by_it() => StaThread.Run(() => WithClaudeAt(StandIn, () =>
    {
        var (first, a) = OffScreenWindow();
        var (second, b) = OffScreenWindow();

        var iconA = IconCache.For(a, Terminal, "fix the bar · Claude");
        var iconB = IconCache.For(b, Terminal, "write the docs · Claude");
        var own = IconCache.For(a, Terminal, "Windows PowerShell");

        Assert.NotNull(iconA);
        // One bitmap for every session window, and not the picture the window itself answers with.
        Assert.Same(iconA, iconB);
        Assert.NotNull(own);
        Assert.NotSame(iconA, own);
        // The ordinal band groups windows by artwork: two session windows are one family.
        Assert.NotNull(IconCache.ArtworkKeyOf(a, Terminal, "fix the bar · Claude"));
        Assert.Equal(IconCache.ArtworkKeyOf(a, Terminal, "fix the bar · Claude"), IconCache.ArtworkKeyOf(b, Terminal, "write the docs · Claude"));

        first.Close();
        second.Close();
    }));

    // A second tab opened in the window takes its title, and the window is then that terminal, so it is drawn as one.
    [Fact]
    public void A_session_window_whose_title_changes_is_drawn_as_the_terminal_again() => StaThread.Run(() => WithClaudeAt(StandIn, () =>
    {
        var (window, handle) = OffScreenWindow();

        var asSession = IconCache.For(handle, Terminal, "fix the bar · Claude");
        var asTerminal = IconCache.For(handle, Terminal, "Windows PowerShell");
        var asSessionAgain = IconCache.For(handle, Terminal, "fix the bar · Claude");

        Assert.NotSame(asSession, asTerminal);
        Assert.Same(asSession, asSessionAgain);
        window.Close();
    }));

    // No claude.exe (an npm install has only claude.cmd): the window keeps the terminal's icon rather than none.
    [Fact]
    public void Without_claude_exe_a_session_window_keeps_its_own_icon() => StaThread.Run(() => WithClaudeAt(null, () =>
    {
        var (window, handle) = OffScreenWindow();

        var icon = IconCache.For(handle, Terminal, "fix the bar · Claude");

        Assert.NotNull(icon);
        Assert.Same(IconCache.For(handle, Terminal, "Windows PowerShell"), icon);
        window.Close();
    }));

    // Task f171bca6, Petre picked "violet icon": the manager's window is drawn with Claude's icon in the manager's violet,
    // a picture of its own (so it is its own family in the ordinal band too), and every task session keeps the plain one.
    [Fact]
    public void The_manager_window_gets_its_own_violet_icon() => StaThread.Run(() => WithClaudeAt(StandIn, () =>
    {
        var (first, task) = OffScreenWindow();
        var (second, managerWindow) = OffScreenWindow();

        var taskIcon = IconCache.For(task, Terminal, "fix the bar · Claude");
        var managerIcon = IconCache.For(managerWindow, Terminal, "Deskbar manager · Claude");

        Assert.NotNull(managerIcon);
        Assert.NotSame(taskIcon, managerIcon);
        Assert.Same(managerIcon, IconCache.For(managerWindow, Terminal, "Deskbar manager · Claude"));
        Assert.NotEqual(IconCache.ArtworkKeyOf(task, Terminal, "fix the bar · Claude"),
            IconCache.ArtworkKeyOf(managerWindow, Terminal, "Deskbar manager · Claude"));
        first.Close();
        second.Close();
    }));

    // The recolouring itself: Claude's orange mark (E7724B, measured at the icon's centre) turns violet with its lightness
    // and saturation kept, the near-black disc a near-black violet, grey stays grey, and alpha is untouched.
    [Fact]
    public void Recolouring_sets_each_pixel_to_the_manager_violet_hue()
    {
        // BGRA: Claude's orange, its dark disc, a mid grey, and a half-transparent orange.
        byte[] pixels = [0x4B, 0x72, 0xE7, 0xFF, 0x05, 0x0B, 0x12, 0xFF, 0x80, 0x80, 0x80, 0xFF, 0x4B, 0x72, 0xE7, 0x80];
        var source = BitmapSource.Create(4, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 16);

        var violet = new byte[16];
        IconCache.InManagerViolet(source).CopyPixels(violet, 16, 0);

        // Blue takes the brightest channel, green the dimmest, red 40/115 of the way between: #B48CFF's own proportions.
        Assert.Equal([0xE7, 0x4B, 0x81, 0xFF, 0x12, 0x05, 0x0A, 0xFF, 0x80, 0x80, 0x80, 0xFF], violet[..12]);
        Assert.Equal(0x80, violet[15]);
    }

    // The title alone is not enough: another app's window with such a title is drawn as that app.
    [Fact]
    public void Another_app_titled_like_a_session_keeps_its_own_icon() => StaThread.Run(() => WithClaudeAt(StandIn, () =>
    {
        var (window, handle) = OffScreenWindow();
        const string code = @"C:\Program Files\Microsoft VS Code\Code.exe";

        Assert.Same(IconCache.For(handle, code, "notes"), IconCache.For(handle, code, "fix the bar · Claude"));
        window.Close();
    }));
}
