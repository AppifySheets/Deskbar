using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests.Bookmarks;

// When a bookmark's chip hides because its app is already running.
public sealed class BookmarkRunningTests
{
    static readonly IReadOnlySet<string> NoApps = new HashSet<string>();

    static WindowInfo Window(string path, string? commandLine = null, nint handle = 1) =>
        new(new WindowHandle(handle), (int)handle, Path.GetFileNameWithoutExtension(path), path, "w", commandLine ?? $"\"{path}\"");

    static Bookmark Program(string path, string? args = null) =>
        new(Guid.NewGuid(), "p", BookmarkKind.Program, path, args);

    static Bookmark App(string id) => new(Guid.NewGuid(), "a", BookmarkKind.App, id);

    [Fact]
    public void A_link_never_hides()
    {
        var link = BookmarkRules.Link("https://example.com/").Value;
        Assert.False(BookmarkRunning.IsRunning(link, [Window(@"C:\Edge\msedge.exe")], NoApps));
    }

    [Fact]
    public void A_program_hides_while_its_exe_has_a_window_whatever_the_arguments()
    {
        var bookmark = Program(@"C:\Tools\rustdesk.exe", "--connect 1");
        Assert.True(BookmarkRunning.IsRunning(bookmark, [Window(@"c:\tools\RustDesk.exe", @"""c:\tools\RustDesk.exe""")], NoApps));
    }

    [Fact]
    public void A_program_in_the_tray_only_still_shows()
    {
        var bookmark = Program(@"C:\Tools\rustdesk.exe");
        Assert.False(BookmarkRunning.IsRunning(bookmark, [Window(@"C:\Tools\other.exe")], NoApps));
    }

    [Fact]
    public void A_pwa_program_does_not_hide_for_a_plain_browser_window()
    {
        var bookmark = Program(@"C:\Edge\msedge.exe", "--profile-directory=Default --app-id=abc");
        var plain = Window(@"C:\Edge\msedge.exe", @"""C:\Edge\msedge.exe"" --profile-directory=Default");
        Assert.False(BookmarkRunning.IsRunning(bookmark, [plain], NoApps));
    }

    [Fact]
    public void A_pwa_program_hides_while_its_app_window_runs()
    {
        var bookmark = Program(@"C:\Edge\msedge.exe", "--profile-directory=Default --app-id=abc");
        var app = Window(@"C:\Edge\msedge.exe", @"""C:\Edge\msedge.exe"" --profile-directory=Default --app-id=abc");
        Assert.True(BookmarkRunning.IsRunning(bookmark, [app], NoApps));
    }

    [Fact]
    public void An_app_hides_by_its_app_user_model_id_even_inside_the_browser_process()
    {
        const string id = "music.youtube.com-5929F88E_vezhnr0wkvrcy!App";
        var browser = Window(@"C:\Edge\msedge.exe");
        Assert.True(BookmarkRunning.IsRunning(App(id), [browser], new HashSet<string> { id }));
    }

    [Fact]
    public void A_plain_edge_window_never_hides_an_app_bookmark()
    {
        // A plain Edge window carries the AUMID "MSEdge"; the PWA's id differs, and the browser's own
        // exe and command line must not count for an App bookmark.
        var browser = Window(@"C:\Edge\msedge.exe");
        var bookmark = App("app.iam.ge-D9839553_en4scw7w9zwzy!App");
        Assert.False(BookmarkRunning.IsRunning(bookmark, [browser], new HashSet<string> { "MSEdge" }));
    }

    [Fact]
    public void A_proxy_browser_bookmark_matches_the_real_browser_window()
    {
        var bookmark = Program(@"C:\Edge\msedge_proxy.exe", "--profile-directory=Default --app-id=abc");
        var app = Window(@"C:\Edge\msedge.exe", @"""C:\Edge\msedge.exe"" --profile-directory=Default --app-id=abc");
        Assert.True(BookmarkRunning.IsRunning(bookmark, [app], NoApps));
    }

    [Fact]
    public void Showing_keeps_add_order_and_drops_the_running()
    {
        var first = Program(@"C:\a.exe");
        var running = Program(@"C:\b.exe");
        var last = BookmarkRules.Link("https://example.com/").Value;

        var showing = BookmarkRunning.Showing([first, running, last], [Window(@"C:\b.exe")], NoApps);

        Assert.Equal([first, last], showing);
    }
}
