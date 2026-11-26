using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Bookmarks;

// The second bookmarks PR: a bookmark that remembers a screen, and a bookmark moved to another workspace. The manager's
// MoveBookmark is what a chip drop and `bookmark move` both call; the opener puts a started window on the screen.
public sealed class BookmarkScreenTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeStore store = new();
    readonly FakeScreenLayout screens = new();
    readonly Dictionary<WindowHandle, string> appIdOf = [];
    // The manager's clock, moved by hand for the screen watch and the app start's lifetime.
    DateTimeOffset time = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    static readonly MonitorBounds Left = new(-1920, 0, 0, 1080);
    static readonly MonitorBounds Right = new(0, 0, 1920, 1080);

    readonly Workspace home;
    readonly Workspace work;
    readonly WorkspaceManager manager;
    readonly BookmarkOpener opener;

    public BookmarkScreenTests()
    {
        var homeDesktop = new DesktopInfo(Guid.NewGuid(), "Home");
        var workDesktop = new DesktopInfo(Guid.NewGuid(), "Work");
        desktops.Desktops.AddRange([homeDesktop, workDesktop]);
        desktops.CurrentDesktopId = workDesktop.Id;
        home = new Workspace(Guid.NewGuid(), "Home", homeDesktop.Id);
        work = new Workspace(Guid.NewGuid(), "Work", workDesktop.Id);
        store.Stored = AppState.Empty with { Workspaces = [home, work] };
        screens.Facts = ScreenFacts.Empty with { MonitorPlacement = new Dictionary<int, MonitorBounds> { [1] = Left, [2] = Right } };
        manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store, () => time, screenLayout: screens);
        Assert.True(manager.Start().IsSuccess);
        opener = new BookmarkOpener(manager,
            new BookmarkStarts((_, _, _) => Result.Success<int?>(5000), _ => Result.Success(), _ => Result.Success()),
            () => new HashSet<string>(), windowAppId: window => appIdOf.GetValueOrDefault(window));
    }

    static Bookmark Program(string exe = @"C:\apps\tool.exe") => new(Guid.NewGuid(), "Tool", BookmarkKind.Program, exe);
    static Bookmark App(string id = "Contoso.Chat_abc!App") => new(Guid.NewGuid(), "Chat", BookmarkKind.App, id);
    static Bookmark Link(string url = "https://portainer.local/") => BookmarkRules.Link(url).Value;

    Bookmark Added(Workspace workspace, Bookmark bookmark)
    {
        Assert.True(manager.AddBookmark(workspace.Id, bookmark).IsSuccess);
        return bookmark;
    }

    IReadOnlyList<Bookmark> BookmarksOf(Workspace workspace) => manager.State.Workspaces.Single(w => w.Id == workspace.Id).Bookmarks;

    // A new window appears on the screen at the left, as a program started on the current desktop would.
    WindowInfo Appears(nint handle, string process, string path, int processId = 5000)
    {
        var window = new WindowInfo(new WindowHandle(handle), processId, process, path, $"{process} window", null);
        screens.Rects[window.Handle] = new WindowRect(-1500, 100, -500, 700);
        desktops.WindowPlacements[window.Handle] = work.DesktopId!.Value;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return window;
    }

    bool OnRight(WindowHandle window) => screens.Rects[window].Left >= 0;

    // --- remembering a screen ----------------------------------------------------------------------------------------

    [Fact]
    public void A_program_remembers_a_screen_and_forgets_it_again()
    {
        var tool = Added(work, Program());

        Assert.True(manager.MoveBookmark(work.Id, tool.Id, null, ScreenChange.On(2)).IsSuccess);
        Assert.Equal(2, BookmarksOf(work).Single().Screen);
        Assert.Equal(2, store.Stored.Workspaces.Single(w => w.Id == work.Id).Bookmarks.Single().Screen);

        Assert.True(manager.MoveBookmark(work.Id, tool.Id, null, ScreenChange.Any).IsSuccess);
        Assert.Null(BookmarksOf(work).Single().Screen);
    }

    [Fact]
    public void A_link_is_refused_a_screen()
    {
        var link = Added(work, Link());

        var result = manager.MoveBookmark(work.Id, link.Id, null, ScreenChange.On(2));

        Assert.True(result.IsFailure);
        Assert.Contains("browser", result.Error);
        Assert.Null(BookmarksOf(work).Single().Screen);
    }

    [Fact]
    public void A_screen_numbered_below_one_is_refused() =>
        Assert.True(manager.MoveBookmark(work.Id, Added(work, Program()).Id, null, ScreenChange.On(0)).IsFailure);

    // --- moving to another workspace ---------------------------------------------------------------------------------

    [Fact]
    public void A_bookmark_moves_to_another_workspace_with_its_id_in_one_save()
    {
        var link = Added(work, Link());
        var already = Added(home, Program());
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        var moved = manager.MoveBookmark(work.Id, link.Id, home.Id, ScreenChange.Keep);

        Assert.True(moved.IsSuccess);
        Assert.Empty(BookmarksOf(work));
        Assert.Equal([already.Id, link.Id], BookmarksOf(home).Select(b => b.Id));
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void A_move_can_set_the_screen_in_the_same_step()
    {
        var tool = Added(work, Program());

        Assert.True(manager.MoveBookmark(work.Id, tool.Id, home.Id, ScreenChange.On(1)).IsSuccess);

        Assert.Equal(1, BookmarksOf(home).Single().Screen);
    }

    [Fact]
    public void A_link_moves_but_keeps_no_screen()
    {
        var link = Added(work, Link());

        Assert.True(manager.MoveBookmark(work.Id, link.Id, home.Id, ScreenChange.Keep).IsSuccess);
        Assert.True(manager.MoveBookmark(home.Id, link.Id, home.Id, ScreenChange.On(1)).IsFailure);
    }

    [Fact]
    public void A_move_to_a_workspace_with_the_same_bookmark_is_refused_and_nothing_changes()
    {
        var here = Added(work, Link());
        Added(home, BookmarkRules.Link("https://portainer.local/", "Mine").Value);

        var result = manager.MoveBookmark(work.Id, here.Id, home.Id, ScreenChange.Keep);

        Assert.True(result.IsFailure);
        Assert.Contains("Home already has this bookmark ('Mine')", result.Error);
        Assert.Single(BookmarksOf(work));
        Assert.Single(BookmarksOf(home));
    }

    [Fact]
    public void A_stale_bookmark_or_workspace_is_an_ordinary_failure()
    {
        var tool = Added(work, Program());

        Assert.True(manager.MoveBookmark(work.Id, Guid.NewGuid(), home.Id, ScreenChange.Keep).IsFailure);
        Assert.True(manager.MoveBookmark(work.Id, tool.Id, Guid.NewGuid(), ScreenChange.Keep).IsFailure);
        Assert.Single(BookmarksOf(work));
    }

    // --- opening on the remembered screen ----------------------------------------------------------------------------

    [Fact]
    public void A_program_with_a_screen_opens_its_window_there()
    {
        var tool = Added(work, Program() with { Screen = 2 });

        Assert.True(opener.Open(work.Id, tool.Id).IsSuccess);
        var window = Appears(0x10, "tool", @"C:\apps\tool.exe");

        Assert.True(OnRight(window.Handle));
    }

    [Fact]
    public void A_program_without_a_screen_is_not_moved()
    {
        var tool = Added(work, Program());

        Assert.True(opener.Open(work.Id, tool.Id).IsSuccess);
        Appears(0x10, "tool", @"C:\apps\tool.exe");

        Assert.Empty(screens.Moved);
    }

    [Fact]
    public void A_screen_no_longer_connected_opens_without_placement()
    {
        var tool = Added(work, Program() with { Screen = 3 });

        Assert.True(opener.Open(work.Id, tool.Id).IsSuccess);
        var window = Appears(0x10, "tool", @"C:\apps\tool.exe");

        Assert.Empty(screens.Moved);
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[window.Handle]);
    }

    [Fact]
    public void An_app_with_a_screen_puts_the_first_window_with_its_app_id_there()
    {
        var chat = Added(work, App() with { Screen = 2 });
        appIdOf[new WindowHandle(0x21)] = "Contoso.Chat_abc!App";

        Assert.True(opener.Open(work.Id, chat.Id).IsSuccess);
        var other = Appears(0x20, "notepad", @"C:\Windows\notepad.exe", 6000);
        var app = Appears(0x21, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe", 6001);
        // Only the first: a second window of the app opened a moment later stays where it opened.
        appIdOf[new WindowHandle(0x22)] = "Contoso.Chat_abc!App";
        var second = Appears(0x22, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe", 6001);

        Assert.False(OnRight(other.Handle));
        Assert.True(OnRight(app.Handle));
        Assert.False(OnRight(second.Handle));
    }

    [Fact]
    public void A_packaged_program_without_an_app_id_is_known_by_its_package()
    {
        var teams = Added(work, App("MSTeams_8wekyb3d8bbwe!MSTeams") with { Screen = 2 });

        Assert.True(opener.Open(work.Id, teams.Id).IsSuccess);
        var window = Appears(0x30, "ms-teams", @"C:\Program Files\WindowsApps\MSTeams_25.1.0.0_x64__8wekyb3d8bbwe\ms-teams.exe", 7000);

        Assert.True(OnRight(window.Handle));
    }

    // Review of the second PR: an app that restores its own saved bounds after showing undoes the move, so the window is
    // watched for a moment and moved back; after the watch, a move is the user's and stays.
    [Fact]
    public void A_started_window_that_drifts_off_its_screen_is_moved_back_for_a_moment()
    {
        var tool = Added(work, Program() with { Screen = 2 });
        Assert.True(opener.Open(work.Id, tool.Id).IsSuccess);
        var window = Appears(0x10, "tool", @"C:\apps\tool.exe");
        Assert.True(OnRight(window.Handle));

        // The app puts itself back where it was last time.
        screens.Rects[window.Handle] = new WindowRect(-1500, 100, -500, 700);
        time += TimeSpan.FromSeconds(2);
        manager.ResyncActiveWindow();
        Assert.True(OnRight(window.Handle));

        time += WorkspaceManager.ScreenWatch;
        screens.Rects[window.Handle] = new WindowRect(-1500, 100, -500, 700);
        manager.ResyncActiveWindow();
        Assert.False(OnRight(window.Handle));
    }

    // An app's window may take its app id after it appears: asked again on the tick, it is still placed.
    [Fact]
    public void An_app_window_that_takes_its_app_id_late_is_placed_on_the_tick()
    {
        var chat = Added(work, App() with { Screen = 2 });
        Assert.True(opener.Open(work.Id, chat.Id).IsSuccess);
        var app = Appears(0x21, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe", 6001);
        Assert.False(OnRight(app.Handle));

        appIdOf[app.Handle] = "Contoso.Chat_abc!App";
        time += TimeSpan.FromSeconds(1);
        manager.ResyncActiveWindow();

        Assert.True(OnRight(app.Handle));
    }

    // An app start that never matched lapses, so a window of that app opened by hand later is left alone.
    [Fact]
    public void A_lapsed_app_start_claims_no_later_window()
    {
        var chat = Added(work, App() with { Screen = 2 });
        Assert.True(opener.Open(work.Id, chat.Id).IsSuccess);

        time += WorkspaceManager.AppExpectationLifetime + TimeSpan.FromSeconds(1);
        appIdOf[new WindowHandle(0x21)] = "Contoso.Chat_abc!App";
        var byHand = Appears(0x21, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe", 6001);
        manager.ResyncActiveWindow();

        Assert.False(OnRight(byHand.Handle));
    }

    [Fact]
    public void An_app_without_a_screen_expects_nothing()
    {
        var chat = Added(work, App());
        appIdOf[new WindowHandle(0x21)] = "Contoso.Chat_abc!App";

        Assert.True(opener.Open(work.Id, chat.Id).IsSuccess);
        Appears(0x21, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe");

        Assert.Empty(screens.Moved);
    }
}
