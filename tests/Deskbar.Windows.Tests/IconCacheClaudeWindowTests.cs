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
