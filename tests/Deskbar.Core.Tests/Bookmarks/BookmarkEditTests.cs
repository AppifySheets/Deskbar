using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Bookmarks;

// Adding, renaming and removing a workspace's bookmarks, and the rules that decide what may be added.
public sealed class BookmarkEditTests : IDisposable
{
    readonly FakeStore store = new();
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-bookmark-edit-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    (WorkspaceManager manager, Guid workspace) Started()
    {
        var work = new Workspace(Guid.NewGuid(), "Work", null);
        store.Stored = AppState.Empty with { Workspaces = [work] };
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, work.Id);
    }

    static Bookmark Portainer() => BookmarkRules.Link("https://portainer.local:9443/").Value;

    static Bookmark RustDesk(string? args = null) =>
        new(Guid.NewGuid(), "RustDesk", BookmarkKind.Program, @"C:\Tools\rustdesk.exe", args);

    [Fact]
    public void Adding_a_link_saves_it_and_pulses()
    {
        var (manager, id) = Started();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);
        var bookmark = Portainer();

        Assert.True(manager.AddBookmark(id, bookmark).IsSuccess);

        Assert.Equal(bookmark, Assert.Single(store.Stored.Workspaces.Single().Bookmarks));
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void A_duplicate_is_refused_with_a_message()
    {
        var (manager, id) = Started();
        Assert.True(manager.AddBookmark(id, Portainer()).IsSuccess);

        var second = manager.AddBookmark(id, Portainer());

        Assert.True(second.IsFailure);
        Assert.Contains("already", second.Error);
        Assert.Single(manager.State.Workspaces.Single().Bookmarks);
    }

    [Fact]
    public void A_program_with_other_arguments_is_not_a_duplicate()
    {
        var (manager, id) = Started();
        Assert.True(manager.AddBookmark(id, RustDesk()).IsSuccess);

        Assert.True(manager.AddBookmark(id, RustDesk("--connect 123")).IsSuccess);
        Assert.True(manager.AddBookmark(id, RustDesk("  ")).IsFailure); // blank arguments equal none

        Assert.Equal(2, manager.State.Workspaces.Single().Bookmarks.Count);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x")]
    [InlineData("ftp://x")]
    [InlineData("https://a.com/\"&calc")]
    [InlineData("https://a.com/\nx")]
    [InlineData("portainer")]
    public void Only_plain_http_and_https_links_are_accepted(string url) => Assert.True(BookmarkRules.Link(url).IsFailure);

    [Fact]
    public void A_link_is_named_after_its_host() =>
        Assert.Equal("portainer.local", BookmarkRules.Link("https://portainer.local:9443/#!/home").Value.Name);

    [Fact]
    public void A_program_needs_a_full_exe_path()
    {
        Assert.True(BookmarkRules.Check(new Bookmark(Guid.NewGuid(), "x", BookmarkKind.Program, "rustdesk.exe")).IsFailure);
        Assert.True(BookmarkRules.Check(new Bookmark(Guid.NewGuid(), "x", BookmarkKind.Program, @"C:\x\readme.txt")).IsFailure);
        Assert.True(BookmarkRules.Check(RustDesk()).IsSuccess);
    }

    // Ruling P4: an app id reaches explorer.exe's command line.
    [Theory]
    [InlineData("")]
    [InlineData("Contoso Chat!App")]
    [InlineData("Contoso\"!App")]
    [InlineData("Contoso\n!App")]
    public void An_app_id_with_a_quote_whitespace_or_nothing_is_refused(string target) =>
        Assert.True(BookmarkRules.Check(new Bookmark(Guid.NewGuid(), "x", BookmarkKind.App, target)).IsFailure);

    [Fact]
    public void An_app_id_is_accepted() =>
        Assert.True(BookmarkRules.Check(new Bookmark(Guid.NewGuid(), "Chat", BookmarkKind.App, "Contoso.Chat_abc!App")).IsSuccess);

    // Ruling R1.
    [Fact]
    public void An_empty_id_is_refused() =>
        Assert.True(BookmarkRules.Check(Portainer() with { Id = Guid.Empty }).IsFailure);

    [Fact]
    public void Renaming_trims_and_refuses_empty()
    {
        var (manager, id) = Started();
        var bookmark = manager.AddBookmark(id, Portainer()).Value;

        Assert.True(manager.RenameBookmark(id, bookmark.Id, "  Docker  ").IsSuccess);
        Assert.Equal("Docker", manager.BookmarkOf(id, bookmark.Id).Value.Name);

        Assert.True(manager.RenameBookmark(id, bookmark.Id, "   ").IsFailure);
        Assert.Equal("Docker", manager.BookmarkOf(id, bookmark.Id).Value.Name);
    }

    [Fact]
    public void Removing_a_bookmark_leaves_the_others()
    {
        var (manager, id) = Started();
        var first = manager.AddBookmark(id, Portainer()).Value;
        var second = manager.AddBookmark(id, RustDesk()).Value;

        Assert.True(manager.RemoveBookmark(id, first.Id).IsSuccess);

        Assert.Equal(second, Assert.Single(store.Stored.Workspaces.Single().Bookmarks));
    }

    [Fact]
    public void Every_edit_on_a_missing_workspace_or_bookmark_fails()
    {
        var (manager, id) = Started();
        var bookmark = manager.AddBookmark(id, Portainer()).Value;
        var saves = store.SaveCount;

        Assert.True(manager.AddBookmark(Guid.NewGuid(), RustDesk()).IsFailure);
        Assert.True(manager.RenameBookmark(Guid.NewGuid(), bookmark.Id, "x").IsFailure);
        Assert.True(manager.RenameBookmark(id, Guid.NewGuid(), "x").IsFailure);
        Assert.True(manager.RemoveBookmark(Guid.NewGuid(), bookmark.Id).IsFailure);
        Assert.True(manager.RemoveBookmark(id, Guid.NewGuid()).IsFailure);
        Assert.True(manager.BookmarkOf(Guid.NewGuid(), bookmark.Id).HasNoValue);
        Assert.True(manager.BookmarkOf(id, Guid.NewGuid()).HasNoValue);

        Assert.Equal(saves, store.SaveCount);
    }

    // Plan omission (ruling P2) and R1: state.json goes through the same rules as the menu.
    [Fact]
    public void Loading_drops_what_the_rules_refuse_and_a_repeated_id_and_keeps_the_order()
    {
        var dupe = Guid.NewGuid();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), $$$"""
            {"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Work","DesktopId":null,"Bookmarks":[
              {"Id":"{{{Guid.NewGuid()}}}","Name":"First","Kind":"Link","Target":"https://one.example"},
              {"Id":"{{{Guid.NewGuid()}}}","Name":"Script","Kind":"Link","Target":"javascript:alert(1)"},
              {"Id":"{{{Guid.NewGuid()}}}","Name":"Relative","Kind":"Program","Target":"rustdesk.exe"},
              {"Id":"{{{Guid.NewGuid()}}}","Name":"   ","Kind":"Link","Target":"https://blank.example"},
              {"Id":"{{{Guid.NewGuid()}}}","Name":"Spaced","Kind":"App","Target":"a b"},
              {"Id":"{{{dupe}}}","Name":"Second","Kind":"Link","Target":"https://two.example"},
              {"Id":"{{{dupe}}}","Name":"Twin","Kind":"Link","Target":"https://three.example"},
              {"Id":"{{{Guid.NewGuid()}}}","Name":"Third","Kind":"App","Target":"Contoso.Chat_abc!App"}]}],
             "WorkspaceRules":[],"RenameRules":[],"Inventory":{}}
            """);

        var load = new JsonPersistenceStore(dir).Load();

        Assert.True(load.IsSuccess);
        Assert.Equal(["First", "Second", "Third"], load.Value.Workspaces[0].Bookmarks.Select(b => b.Name).ToList());
    }
}
