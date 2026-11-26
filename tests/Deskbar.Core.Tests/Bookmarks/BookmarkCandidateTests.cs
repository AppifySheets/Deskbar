using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Bookmarks;

// Which bookmark a window's menu offers.
public sealed class BookmarkCandidateTests
{
    sealed class FakeSources : IBookmarkSources
    {
        public Dictionary<string, string> Descriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> AppNames { get; } = [];

        public Maybe<ShortcutTarget> ResolveShortcut(string lnkPath) => Maybe<ShortcutTarget>.None;
        public string? DescribeExe(string exePath) => Descriptions.GetValueOrDefault(exePath);
        public string? NameOfApp(string appId) => AppNames.GetValueOrDefault(appId);
        public string? ReadText(string path) => null;
    }

    const string PwaId = "music.youtube.com-5929F88E_vezhnr0wkvrcy!App";

    static WindowInfo Window(string path, string? commandLine = null, string title = "w", nint handle = 1, int pid = 7) =>
        new(new WindowHandle(handle), pid, Path.GetFileNameWithoutExtension(path), path, title, commandLine ?? $"\"{path}\"");

    [Fact]
    public void A_window_with_a_startable_app_id_becomes_an_app()
    {
        var sources = new FakeSources();
        sources.AppNames["Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"] = "Calculator";

        var bookmark = BookmarkCandidates.FromWindow(
            Window(@"C:\Windows\System32\ApplicationFrameHost.exe", title: "Calculator"),
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", sources).Value;

        Assert.Equal(BookmarkKind.App, bookmark.Kind);
        Assert.Equal("Calculator", bookmark.Name);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", bookmark.Target);
    }

    [Fact]
    public void An_app_with_no_known_name_takes_the_window_title()
    {
        var bookmark = BookmarkCandidates.FromWindow(Window(@"C:\Edge\msedge.exe", title: "YouTube Music"), PwaId, new FakeSources()).Value;

        Assert.Equal("YouTube Music", bookmark.Name);
    }

    [Fact]
    public void A_pwa_inside_the_browser_process_becomes_an_app()
    {
        // The browser was already running, so the PWA window reports msedge.exe and the browser's own command
        // line: no roster identity, but the packaged id is startable.
        var pwa = Window(@"C:\Edge\msedge.exe", @"""C:\Edge\msedge.exe"" --profile-directory=Default");

        var bookmark = BookmarkCandidates.FromWindow(pwa, PwaId, new FakeSources()).Value;

        Assert.Equal(BookmarkKind.App, bookmark.Kind);
        Assert.Equal(PwaId, bookmark.Target);
    }

    [Fact]
    public void A_desktop_app_window_becomes_a_program_with_its_arguments()
    {
        var sources = new FakeSources();
        sources.Descriptions[@"C:\Tools\rustdesk.exe"] = "RustDesk";
        var window = Window(@"C:\Tools\rustdesk.exe", @"""C:\Tools\rustdesk.exe"" --connect 1");

        var bookmark = BookmarkCandidates.FromWindow(window, null, sources).Value;

        Assert.Equal(BookmarkKind.Program, bookmark.Kind);
        Assert.Equal("RustDesk", bookmark.Name);
        Assert.Equal(@"C:\Tools\rustdesk.exe", bookmark.Target);
        Assert.Equal("--connect 1", bookmark.Arguments);
    }

    [Fact]
    public void A_program_with_no_description_is_named_after_its_process_and_has_no_arguments()
    {
        var bookmark = BookmarkCandidates.FromWindow(Window(@"C:\Tools\buzz.exe"), null, new FakeSources()).Value;

        Assert.Equal("buzz", bookmark.Name);
        Assert.Null(bookmark.Arguments);
    }

    [Fact]
    public void A_plain_browser_or_explorer_window_offers_nothing()
    {
        // "MSEdge" is the plain browser's AppUserModelID: no "!", so not startable, and the browser has no
        // roster identity, so the Program route is closed too.
        Assert.True(BookmarkCandidates.FromWindow(Window(@"C:\Edge\msedge.exe"), "MSEdge", new FakeSources()).HasNoValue);
        Assert.True(BookmarkCandidates.FromWindow(Window(@"C:\Edge\msedge.exe"), null, new FakeSources()).HasNoValue);
        Assert.True(BookmarkCandidates.FromWindow(Window(@"C:\Windows\explorer.exe"), null, new FakeSources()).HasNoValue);
    }

    [Fact]
    public void A_terminal_window_offers_an_app_because_its_id_is_startable()
    {
        // Ruling P5: Windows Terminal's AppUserModelID has a "!", and shell:AppsFolder starts it.
        const string id = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

        var bookmark = BookmarkCandidates.FromWindow(Window(@"C:\Program Files\WindowsApps\wt\WindowsTerminal.exe"), id, new FakeSources()).Value;

        Assert.Equal(BookmarkKind.App, bookmark.Kind);
        Assert.Equal(id, bookmark.Target);
    }

    [Fact]
    public void An_elevated_window_with_no_path_offers_nothing()
    {
        var window = new WindowInfo(new WindowHandle(1), 7, "elevated", null, "w", null);

        Assert.True(BookmarkCandidates.FromWindow(window, null, new FakeSources()).HasNoValue);
    }

    [Theory]
    [InlineData("music.youtube.com-5929F88E_vezhnr0wkvrcy!App", true)]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", true)]
    [InlineData("MSEdge", false)]
    [InlineData("Chrome", false)]
    [InlineData("Chrome.Default.abc", false)]
    [InlineData("", false)]
    public void Only_the_packaged_form_is_startable(string id, bool startable) =>
        Assert.Equal(startable, BookmarkCandidates.IsStartableAppId(id));

    [Fact]
    public void Deskbars_own_windows_offer_nothing()
    {
        var work = new Workspace(Guid.NewGuid(), "Work", null);
        var store = new FakeStore { Stored = AppState.Empty with { Workspaces = [work] } };
        var monitor = new FakeMonitor();
        var ours = Window(@"C:\Deskbar\Deskbar.exe", handle: 10, pid: 4242);
        var theirs = Window(@"C:\Tools\rustdesk.exe", handle: 11, pid: 99);
        monitor.InitialWindows.AddRange([ours, theirs]);
        var manager = new WorkspaceManager(new FakeDesktops(), monitor, new FakeTitles(), store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);

        var sources = new FakeSources();
        Assert.True(manager.BookmarkFor(ours.Handle, null, sources).HasNoValue);
        Assert.True(manager.BookmarkFor(theirs.Handle, null, sources).HasValue);
        Assert.True(manager.BookmarkFor(new WindowHandle(12345), null, sources).HasNoValue); // not a window it knows
    }
}
