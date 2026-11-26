using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Bookmarks;

// Opening a bookmark and "Open all". The starts are fakes that record what they were asked, in order; the
// program's window "appears" through the fake monitor as the real one would.
public sealed class BookmarkOpenerTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeStore store = new();
    readonly List<string> calls = [];
    readonly List<(string Exe, string Arguments, string? Folder)> programs = [];
    // Names whose start is refused, so a failure can be made to happen on demand.
    readonly HashSet<string> refused = [];
    IReadOnlySet<string> appIds = new HashSet<string>();

    Workspace home = null!;
    Workspace work = null!;
    WorkspaceManager manager = null!;
    BookmarkOpener opener = null!;

    public BookmarkOpenerTests()
    {
        var homeDesktop = new DesktopInfo(Guid.NewGuid(), "Home");
        var workDesktop = new DesktopInfo(Guid.NewGuid(), "Work");
        desktops.Desktops.AddRange([homeDesktop, workDesktop]);
        desktops.CurrentDesktopId = homeDesktop.Id;
        home = new Workspace(Guid.NewGuid(), "Home", homeDesktop.Id);
        work = new Workspace(Guid.NewGuid(), "Work", workDesktop.Id);
        store.Stored = AppState.Empty with { Workspaces = [home, work] };
        manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        var starts = new BookmarkStarts(
            Program: (exe, args, folder) =>
            {
                calls.Add($"program {exe}");
                programs.Add((exe, args, folder));
                return refused.Contains(exe) ? Result.Failure<int?>("no such file") : Result.Success<int?>(5000);
            },
            App: id => { calls.Add($"app {id}"); return refused.Contains(id) ? Result.Failure("not installed") : Result.Success(); },
            Link: url => { calls.Add($"link {url}"); return refused.Contains(url) ? Result.Failure("no browser") : Result.Success(); });
        opener = new BookmarkOpener(manager, starts, () => appIds);
        // The same starts, plus the link-window watch the app wires (OpenedLinks): every watch listens to one fake
        // receiver, and takes its first window, as the real one does.
        watching = new BookmarkOpener(manager, starts, () => appIds, new LinkWindows(
            desktop => { watched.Add(desktop); return receiver.Take(1); },
            window => alive.Contains(window.Handle),
            gone));
    }

    // The link-window seams: which desktops a watch was asked for, the window a watch reports, which windows still
    // live, and the window list's "gone for real".
    readonly List<Guid> watched = [];
    readonly System.Reactive.Subjects.Subject<LinkWindow> receiver = new();
    readonly HashSet<WindowHandle> alive = [];
    readonly System.Reactive.Subjects.Subject<WindowHandle> gone = new();
    readonly BookmarkOpener watching;
    static readonly LinkWindow Edge = new(new WindowHandle(0x500), 4242);

    static Bookmark Link(string url) => BookmarkRules.Link(url).Value;

    static Bookmark Program(string name, string exe, string? args = null) =>
        new(Guid.NewGuid(), name, BookmarkKind.Program, exe, args);

    Bookmark Added(Workspace workspace, Bookmark bookmark)
    {
        Assert.True(manager.AddBookmark(workspace.Id, bookmark).IsSuccess);
        return bookmark;
    }

    [Fact]
    public void A_click_from_another_workspace_switches_then_starts()
    {
        var link = Added(work, Link("https://portainer.local/"));

        Assert.True(opener.Open(work.Id, link.Id).IsSuccess);

        Assert.Equal([work.DesktopId!.Value], desktops.Switches);
        Assert.Equal(["link https://portainer.local/"], calls);
    }

    [Fact]
    public void A_click_on_the_current_workspace_does_not_switch()
    {
        var link = Added(home, Link("https://portainer.local/"));

        Assert.True(opener.Open(home.Id, link.Id).IsSuccess);

        Assert.Empty(desktops.Switches);
        Assert.Single(calls);
    }

    [Fact]
    public void A_program_is_expected_in_its_workspace()
    {
        var program = Added(work, Program("Tool", @"C:\apps\tool.exe"));

        Assert.True(opener.Open(work.Id, program.Id).IsSuccess);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared,
            new WindowInfo(new WindowHandle(0x10), 5000, "tool", @"C:\apps\tool.exe", "tool window", null)));

        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Program_arguments_reach_the_start_verbatim()
    {
        var program = Added(work, Program("Tool", @"C:\apps\tool.exe", "--x \"a b\""));

        opener.Open(work.Id, program.Id);

        Assert.Equal("--x \"a b\"", programs.Single().Arguments);
    }

    [Fact]
    public void A_failed_start_names_the_bookmark_and_withdraws_the_expectation()
    {
        var program = Added(work, Program("Tool", @"C:\apps\tool.exe"));
        refused.Add(program.Target);

        var result = opener.Open(work.Id, program.Id);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared,
            new WindowInfo(new WindowHandle(0x11), 7000, "tool", @"C:\apps\tool.exe", "tool window", null)));

        Assert.Equal("Tool: no such file", result.Error);
        Assert.False(manager.AwaitsAny("tool"));
        Assert.DoesNotContain(new WindowHandle(0x11), desktops.WindowPlacements.Keys);
    }

    [Fact]
    public void Open_all_switches_once_skips_running_programs_and_opens_every_link()
    {
        Added(work, Link("https://a.local/"));
        Added(work, Program("Running", @"C:\apps\running.exe"));
        Added(work, Link("https://b.local/"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared,
            new WindowInfo(new WindowHandle(0x20), 1, "running", @"C:\apps\running.exe", "r", null)));

        var opened = opener.OpenAll(work.Id);

        Assert.True(opened.IsSuccess);
        Assert.Equal(["https://a.local/", "https://b.local/"], opened.Value.Select(b => b.Target));
        Assert.Equal(["link https://a.local/", "link https://b.local/"], calls);
        Assert.Single(desktops.Switches);
    }

    // The bar asks once per rebuild (Task 8): the open app ids are read when the snapshot is taken, once, however
    // many rows ask it afterwards, and the answer for each row is Showing's.
    [Fact]
    public void A_snapshot_reads_the_open_app_ids_once_and_answers_as_showing_does()
    {
        var reads = 0;
        var counted = new BookmarkOpener(manager, new BookmarkStarts((_, _, _) => Result.Success<int?>(null), _ => Result.Success(), _ => Result.Success()),
            () => { reads++; return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Calc!App" }; });
        Added(work, new Bookmark(Guid.NewGuid(), "Calc", BookmarkKind.App, "Calc!App"));
        var link = Added(work, Link("https://a.local/"));
        Added(home, Link("https://b.local/"));

        var showing = counted.ShowingNow();
        var forWork = showing(manager.State.Workspaces.Single(w => w.Id == work.Id));
        var forHome = showing(manager.State.Workspaces.Single(w => w.Id == home.Id));

        Assert.Equal(1, reads);
        Assert.Equal([link.Id], forWork.Select(b => b.Id));
        Assert.Single(forHome);
    }

    // Ruling P9: one bad bookmark must not keep the rest of the row from opening.
    [Fact]
    public void Open_all_carries_on_past_a_failure_and_reports_it_by_name()
    {
        Added(work, Link("https://a.local/"));
        var bad = Added(work, Program("Broken", @"C:\apps\broken.exe"));
        Added(work, Link("https://b.local/"));
        refused.Add(bad.Target);

        var opened = opener.OpenAll(work.Id);

        Assert.True(opened.IsFailure);
        Assert.Contains("Broken: no such file", opened.Error);
        Assert.Equal(["link https://a.local/", @"program C:\apps\broken.exe", "link https://b.local/"], calls);
    }

    // --- Opened links (option B): a link runs while the browser window it went into stays open -------------------------

    [Fact]
    public void An_opened_link_runs_while_the_browser_window_it_went_into_lives()
    {
        var link = Added(work, Link("https://gph.local/"));

        Assert.True(watching.Open(work.Id, link.Id).IsSuccess);
        Assert.False(watching.IsRunning(link));
        receiver.OnNext(Edge);
        alive.Add(Edge.Handle);

        Assert.Equal([work.DesktopId!.Value], watched);
        Assert.True(watching.IsRunning(link));
        Assert.True(watching.IsLinkOpen(link));
        // Still shown: a link chip never hides, it is drawn open instead.
        Assert.Equal([link.Id], watching.Showing(manager.State.Workspaces.Single(w => w.Id == work.Id)).Select(b => b.Id));

        alive.Remove(Edge.Handle);
        Assert.False(watching.IsRunning(link));
    }

    [Fact]
    public void A_window_gone_for_real_drops_its_notes()
    {
        var link = Added(work, Link("https://gph.local/"));
        watching.Open(work.Id, link.Id);
        receiver.OnNext(Edge);
        Assert.NotEmpty(watching.OpenedLinks.Noted);

        gone.OnNext(Edge.Handle);

        Assert.Empty(watching.OpenedLinks.Noted);
    }

    [Fact]
    public void A_failed_link_start_watches_for_nothing()
    {
        var link = Added(work, Link("https://gph.local/"));
        refused.Add(link.Target);

        Assert.True(watching.Open(work.Id, link.Id).IsFailure);
        receiver.OnNext(Edge);

        Assert.Empty(watching.OpenedLinks.Noted);
    }

    [Fact]
    public void Open_all_notes_one_window_for_every_link_that_went_into_it()
    {
        var a = Added(work, Link("https://a.local/"));
        var b = Added(work, Link("https://b.local/"));
        alive.Add(Edge.Handle);

        watching.OpenAll(work.Id);
        receiver.OnNext(Edge);

        Assert.True(watching.IsRunning(a));
        Assert.True(watching.IsRunning(b));
    }

    [Fact]
    public void Removing_the_bookmark_forgets_its_note()
    {
        var link = Added(work, Link("https://gph.local/"));
        alive.Add(Edge.Handle);
        watching.Open(work.Id, link.Id);
        receiver.OnNext(Edge);
        Assert.NotEmpty(watching.OpenedLinks.Noted);

        Assert.True(manager.RemoveBookmark(work.Id, link.Id).IsSuccess);

        Assert.Empty(watching.OpenedLinks.Noted);
    }

    // A window that turns up after its bookmark was removed is not noted for a bookmark that no longer exists.
    [Fact]
    public void A_window_arriving_after_the_bookmark_was_removed_is_not_noted()
    {
        var link = Added(work, Link("https://gph.local/"));
        watching.Open(work.Id, link.Id);
        manager.RemoveBookmark(work.Id, link.Id);

        receiver.OnNext(Edge);

        Assert.Empty(watching.OpenedLinks.Noted);
    }

    [Fact]
    public void Programs_and_apps_are_not_watched()
    {
        var program = Added(work, Program("Tool", @"C:\apps\tool.exe"));
        var app = Added(work, new Bookmark(Guid.NewGuid(), "Calc", BookmarkKind.App, "Calc!App"));

        watching.Open(work.Id, program.Id);
        watching.Open(work.Id, app.Id);

        Assert.Empty(watched);
    }

    // Without the watch (a test bar, or a copy that cannot see windows) a link is never running, as before.
    [Fact]
    public void Without_a_watch_a_link_never_runs()
    {
        var link = Added(work, Link("https://gph.local/"));

        opener.Open(work.Id, link.Id);
        receiver.OnNext(Edge);

        Assert.False(opener.IsRunning(link));
        Assert.False(opener.IsLinkOpen(link));
    }
}
