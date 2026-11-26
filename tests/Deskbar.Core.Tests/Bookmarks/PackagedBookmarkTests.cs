using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Rehydration;

namespace Deskbar.Core.Tests.Bookmarks;

// Bookmarks on packaged (Store) programs whose window carries no AppUserModelID, new Teams being the one measured
// (Deskbar task e7fb8e04, "no teams, why?"): the chip replayed Teams' autostart command line on an exe in one
// version's WindowsApps folder, and Teams, closed to the tray, stayed there.
public sealed class PackagedBookmarkTests
{
    const string TeamsExe = @"C:\Program Files\WindowsApps\MSTeams_26288.2004.5269.3420_x64__8wekyb3d8bbwe\ms-teams.exe";
    const string TeamsNextExe = @"C:\Program Files\WindowsApps\MSTeams_26301.100.1.1_x64__8wekyb3d8bbwe\ms-teams.exe";
    const string TeamsId = "MSTeams_8wekyb3d8bbwe!MSTeams";
    const string TeamsFamily = "MSTeams_8wekyb3d8bbwe";

    sealed class FakeSources : IBookmarkSources
    {
        public Dictionary<int, string> ProcessIds { get; } = [];
        public Dictionary<string, string> AppNames { get; } = [];

        public Maybe<ShortcutTarget> ResolveShortcut(string lnkPath) => Maybe<ShortcutTarget>.None;
        public string? DescribeExe(string exePath) => "Microsoft Teams";
        public string? NameOfApp(string appId) => AppNames.GetValueOrDefault(appId);
        public string? ReadText(string path) => null;
        public string? AppIdOfProcess(int processId) => ProcessIds.GetValueOrDefault(processId);
    }

    static WindowInfo Window(string path, string? commandLine = null, int pid = 7) =>
        new(new WindowHandle(1), pid, Path.GetFileNameWithoutExtension(path), path, "Chat | Microsoft Teams",
            commandLine ?? $"\"{path}\" msteams:system-initiated");

    // The bookmark the window's menu made before the fix, as found in Petre's state.json.
    static Bookmark OldTeamsBookmark() =>
        new(Guid.NewGuid(), "Microsoft Teams", BookmarkKind.Program, TeamsExe, "msteams:system-initiated");

    [Fact]
    public void A_packaged_window_with_no_app_id_takes_its_process_id_and_becomes_an_app()
    {
        var sources = new FakeSources();
        sources.ProcessIds[7] = TeamsId;
        sources.AppNames[TeamsId] = "Microsoft Teams";

        var bookmark = BookmarkCandidates.FromWindow(Window(TeamsExe), null, sources).Value;

        Assert.Equal(BookmarkKind.App, bookmark.Kind);
        Assert.Equal(TeamsId, bookmark.Target);
        Assert.Equal("Microsoft Teams", bookmark.Name);
        Assert.Null(bookmark.Arguments); // the autostart's "msteams:system-initiated" is not carried over
    }

    [Fact]
    public void A_packaged_window_whose_id_cannot_be_read_offers_nothing_rather_than_a_versioned_exe()
    {
        var bookmark = BookmarkCandidates.FromWindow(Window(TeamsExe), null, new FakeSources());

        Assert.True(bookmark.HasNoValue);
    }

    [Fact]
    public void The_window_s_own_app_id_still_wins_over_its_process_s()
    {
        var sources = new FakeSources();
        sources.ProcessIds[7] = "Some.Package_abc!Other";

        var bookmark = BookmarkCandidates.FromWindow(Window(TeamsExe), TeamsId, sources).Value;

        Assert.Equal(TeamsId, bookmark.Target);
    }

    [Fact]
    public void An_unpackaged_program_is_still_a_program_and_its_process_is_not_asked()
    {
        var sources = new FakeSources();
        sources.ProcessIds[7] = "should.not_be!Used";

        var bookmark = BookmarkCandidates.FromWindow(Window(@"C:\Tools\rustdesk.exe", "\"C:\\Tools\\rustdesk.exe\""), null, sources).Value;

        Assert.Equal(BookmarkKind.Program, bookmark.Kind);
        Assert.Equal(@"C:\Tools\rustdesk.exe", bookmark.Target);
    }

    [Fact]
    public void An_old_packaged_program_bookmark_heals_into_an_app_keeping_its_id_and_name()
    {
        var old = OldTeamsBookmark();

        Assert.Equal(TeamsFamily, BookmarkCandidates.PackageToHeal(old));
        var healed = BookmarkCandidates.Healed(old, family => family == TeamsFamily ? TeamsId : null);

        Assert.Equal(old with { Kind = BookmarkKind.App, Target = TeamsId, Arguments = null }, healed);
    }

    [Fact]
    public void A_family_that_cannot_be_resolved_leaves_the_bookmark_alone()
    {
        var old = OldTeamsBookmark();

        Assert.Same(old, BookmarkCandidates.Healed(old, _ => null));
    }

    [Fact]
    public void Only_packaged_program_bookmarks_need_healing()
    {
        Assert.Null(BookmarkCandidates.PackageToHeal(new Bookmark(Guid.NewGuid(), "RustDesk", BookmarkKind.Program, @"C:\Tools\rustdesk.exe")));
        Assert.Null(BookmarkCandidates.PackageToHeal(new Bookmark(Guid.NewGuid(), "Teams", BookmarkKind.App, TeamsId)));
    }

    [Fact]
    public void An_app_bookmark_counts_as_running_while_a_window_of_its_package_is_open_even_after_an_update()
    {
        var bookmark = new Bookmark(Guid.NewGuid(), "Microsoft Teams", BookmarkKind.App, TeamsId);
        var none = new HashSet<string>();

        Assert.True(BookmarkRunning.IsRunning(bookmark, [Window(TeamsNextExe)], none));
        // Closed to the tray: no window, so the chip shows and a click brings Teams back.
        Assert.False(BookmarkRunning.IsRunning(bookmark, [], none));
        // Another package's window, a framed app's host and a browser are not Teams.
        Assert.False(BookmarkRunning.IsRunning(bookmark,
            [Window(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.0.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe"),
             Window(@"C:\Windows\System32\ApplicationFrameHost.exe"),
             Window(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")], none));
    }

    [Fact]
    public void Package_paths_name_their_family()
    {
        Assert.Equal(TeamsFamily, PackagedPaths.FamilyOf(TeamsExe));
        Assert.Equal(TeamsFamily, PackagedPaths.FamilyOfAppId(TeamsId));
        Assert.Null(PackagedPaths.FamilyOfAppId("MSEdge"));
        Assert.Null(PackagedPaths.FamilyOf(@"C:\Tools\rustdesk.exe"));
    }

    // The manager rewrites every healed bookmark in one persist, and writes nothing when nothing changed.
    readonly FakeStore store = new();


    [Fact]
    public void The_manager_heals_in_one_persist_and_only_when_something_changed()
    {
        var old = OldTeamsBookmark();
        var link = BookmarkRules.Link("https://portainer.local:9443/").Value;
        var work = new Workspace(Guid.NewGuid(), "GPH", null) { Bookmarks = [link, old] };
        store.Stored = AppState.Empty with { Workspaces = [work] };
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        manager.HealBookmarks(b => b);
        Assert.Equal(0, pulses);

        manager.HealBookmarks(b => BookmarkCandidates.Healed(b, _ => TeamsId));
        Assert.Equal(1, pulses);
        var saved = store.Stored.Workspaces.Single().Bookmarks;
        Assert.Equal(link, saved[0]);
        Assert.Equal(new Bookmark(old.Id, "Microsoft Teams", BookmarkKind.App, TeamsId), saved[1]);
    }

    [Fact]
    public void Two_old_bookmarks_that_heal_into_one_app_leave_one_chip()
    {
        // One saved from the autostarted Teams, one from a Teams started by hand: different arguments, so both were
        // allowed in, and both become the same App.
        var autostarted = OldTeamsBookmark();
        var byHand = new Bookmark(Guid.NewGuid(), "Teams", BookmarkKind.Program, TeamsNextExe);
        var work = new Workspace(Guid.NewGuid(), "GPH", null) { Bookmarks = [autostarted, byHand] };
        store.Stored = AppState.Empty with { Workspaces = [work] };
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        manager.HealBookmarks(b => BookmarkCandidates.Healed(b, _ => TeamsId));

        var kept = Assert.Single(store.Stored.Workspaces.Single().Bookmarks);
        Assert.Equal(autostarted.Id, kept.Id);
        Assert.Equal(BookmarkKind.App, kept.Kind);
    }

    [Fact]
    public void In_a_package_whose_windows_carry_ids_one_app_does_not_hide_another()
    {
        const string word = "Microsoft.Office.Desktop_8wekyb3d8bbwe!Word";
        const string excel = "Microsoft.Office.Desktop_8wekyb3d8bbwe!Excel";
        var excelWindow = Window(@"C:\Program Files\WindowsApps\Microsoft.Office.Desktop_16.0.1.0_x86__8wekyb3d8bbwe\Office16\EXCEL.EXE");
        var bookmark = new Bookmark(Guid.NewGuid(), "Word", BookmarkKind.App, word);

        Assert.False(BookmarkRunning.IsRunning(bookmark, [excelWindow], new HashSet<string> { excel }));
        // With no ids at all, the package's window is all there is to go by.
        Assert.True(BookmarkRunning.IsRunning(bookmark, [excelWindow], new HashSet<string>()));
    }
}
