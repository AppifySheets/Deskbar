using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Bookmarks;

public sealed class BookmarkPersistenceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-bookmark-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    string StateText => File.ReadAllText(Path.Combine(dir, "state.json"));

    void WriteState(string json)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), json);
    }

    [Fact]
    public void Bookmarks_round_trip_through_state_json()
    {
        var work = new Workspace(Guid.NewGuid(), "Work", null)
        {
            Bookmarks =
            [
                new Bookmark(Guid.NewGuid(), "Docs", BookmarkKind.Link, "https://example.com"),
                new Bookmark(Guid.NewGuid(), "Tool", BookmarkKind.Program, @"C:\t\tool.exe", "--x \"a b\"", @"C:\w"),
                new Bookmark(Guid.NewGuid(), "Chat", BookmarkKind.App, "Contoso.Chat_abc!App"),
            ],
        };
        var saved = AppState.Empty with { Workspaces = [work] };
        var store = new JsonPersistenceStore(dir);

        Assert.True(store.Save(saved).IsSuccess);
        var loaded = store.Load().Value;

        Assert.Equal(saved.Workspaces[0].Bookmarks, loaded.Workspaces[0].Bookmarks);
        Assert.Contains("\"Kind\": \"Program\"", StateText);
    }

    [Fact]
    public void A_bookmarks_screen_round_trips_and_an_old_entry_has_none()
    {
        var onTwo = new Bookmark(Guid.NewGuid(), "Tool", BookmarkKind.Program, @"C:\t\tool.exe", Screen: 2);
        var old = Guid.NewGuid();
        var store = new JsonPersistenceStore(dir);
        Assert.True(store.Save(AppState.Empty with { Workspaces = [new Workspace(Guid.NewGuid(), "Work", null) { Bookmarks = [onTwo] }] }).IsSuccess);

        Assert.Equal(onTwo, Assert.Single(store.Load().Value.Workspaces[0].Bookmarks));

        // An entry written before bookmarks had a screen, and a link someone gave one by hand.
        WriteState($$$"""
            {"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Work","DesktopId":null,"Bookmarks":[
                {"Id":"{{{old}}}","Name":"Tool","Kind":"Program","Target":"C:\\t\\tool.exe"},
                {"Id":"{{{Guid.NewGuid()}}}","Name":"Docs","Kind":"Link","Target":"https://example.com","Screen":2}]}],
             "WorkspaceRules":[],"RenameRules":[],"Inventory":{}}
            """);
        var loaded = new JsonPersistenceStore(dir).Load().Value.Workspaces[0].Bookmarks;

        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, b => Assert.Null(b.Screen));
    }

    [Fact]
    public void A_file_written_before_bookmarks_loads_with_none()
    {
        WriteState($$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Work","DesktopId":null}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        var loaded = new JsonPersistenceStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Workspaces[0].Bookmarks);
    }

    [Fact]
    public void An_unreadable_bookmark_is_dropped_and_the_rest_of_the_file_loads()
    {
        var good = Guid.NewGuid();
        WriteState($$$"""
            {"Workspaces":[
              {"Id":"{{{Guid.NewGuid()}}}","Name":"Work","DesktopId":null,"Bookmarks":[
                {"Id":"{{{Guid.NewGuid()}}}","Name":"x","Kind":"Teleport","Target":"y"},
                {"Id":"{{{Guid.NewGuid()}}}","Name":"x","Kind":"Link","Target":null},
                {"Id":"{{{good}}}","Name":"Docs","Kind":"Link","Target":"https://example.com"}]},
              {"Id":"{{{Guid.NewGuid()}}}","Name":"Other","DesktopId":null}],
             "WorkspaceRules":[],"RenameRules":[],"Inventory":{}}
            """);

        var load = new JsonPersistenceStore(dir).Load();

        // A fallback to state.json.previous (none exists here) could not give two workspaces.
        Assert.True(load.IsSuccess);
        Assert.Equal(2, load.Value.Workspaces.Count);
        Assert.Equal(good, Assert.Single(load.Value.Workspaces[0].Bookmarks).Id);
    }

    [Fact]
    public void Renaming_a_workspace_keeps_its_bookmarks()
    {
        var bookmark = new Bookmark(Guid.NewGuid(), "Docs", BookmarkKind.Link, "https://example.com");
        var work = new Workspace(Guid.NewGuid(), "Work", null) { Bookmarks = [bookmark] };
        var store = new FakeStore { Stored = AppState.Empty with { Workspaces = [work] } };
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        Assert.True(manager.RenameWorkspace(work.Id, "Play").IsSuccess);

        Assert.Equal(bookmark, Assert.Single(store.Stored.Workspaces.Single().Bookmarks));
    }
}
