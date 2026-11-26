using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Bookmarks;

// A bookmark's own colour (Petre: "i want to be able to change color of the bookmark"): "#RRGGBB" or none, checked on
// the way in and out of state.json, set from the chip menu and `bookmark color`.
public sealed class BookmarkColourTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-bookmark-colour-{Guid.NewGuid():N}");
    readonly FakeStore store = new();
    readonly WorkspaceManager manager;
    readonly RemoteControl control;
    readonly Workspace work = new(Guid.NewGuid(), "Work", null);

    public BookmarkColourTests()
    {
        store.Stored = AppState.Empty with { Workspaces = [work] };
        manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        control = new RemoteControl(manager);
    }

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static Bookmark Tool(string? colour = null) => new(Guid.NewGuid(), "Tool", BookmarkKind.Program, @"C:\t\tool.exe", Color: colour);

    [Theory]
    [InlineData(null, true)]
    [InlineData("#1A2b3C", true)]
    [InlineData("1A2B3C", false)]
    [InlineData("#1A2B3", false)]
    [InlineData("#1A2B3CDD", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("teal", false)]
    [InlineData("none", false)]
    public void Check_takes_only_none_or_a_hex_colour(string? colour, bool accepted) =>
        Assert.Equal(accepted, BookmarkRules.Check(Tool(colour)).IsSuccess);

    [Fact]
    public void A_colour_round_trips_through_state_json_and_an_old_entry_has_none()
    {
        var coloured = Tool("#3A7BD5");
        var fileStore = new JsonPersistenceStore(dir);
        Assert.True(fileStore.Save(AppState.Empty with { Workspaces = [work with { Bookmarks = [coloured, Tool()] }] }).IsSuccess);

        var loaded = fileStore.Load().Value.Workspaces[0].Bookmarks;

        Assert.Equal("#3A7BD5", loaded[0].Color);
        Assert.Null(loaded[1].Color);
    }

    [Fact]
    public void A_hand_edited_colour_or_screen_that_is_wrong_costs_only_that_field()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), $$$"""
            {"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Work","DesktopId":null,"Bookmarks":[
                {"Id":"{{{Guid.NewGuid()}}}","Name":"Bad","Kind":"Program","Target":"C:\\t\\a.exe","Color":"red","Screen":0},
                {"Id":"{{{Guid.NewGuid()}}}","Name":"Good","Kind":"Program","Target":"C:\\t\\b.exe","Color":"#ABCDEF","Screen":2}]}],
             "WorkspaceRules":[],"RenameRules":[],"Inventory":{}}
            """);

        var loaded = new JsonPersistenceStore(dir).Load().Value.Workspaces[0].Bookmarks;

        Assert.Equal(["Bad", "Good"], loaded.Select(b => b.Name));
        Assert.Null(loaded[0].Color);
        Assert.Null(loaded[0].Screen);
        Assert.Equal(("#ABCDEF", 2), (loaded[1].Color, loaded[1].Screen));
    }

    [Fact]
    public void The_manager_sets_and_clears_a_colour()
    {
        var tool = Tool();
        Assert.True(manager.AddBookmark(work.Id, tool).IsSuccess);

        Assert.Equal("#ABCDEF", manager.SetBookmarkColor(work.Id, tool.Id, "#abcdef").Value.Color);
        Assert.Equal("#ABCDEF", store.Stored.Workspaces.Single().Bookmarks.Single().Color);
        Assert.True(manager.SetBookmarkColor(work.Id, tool.Id, "blue").IsFailure);
        Assert.True(manager.SetBookmarkColor(work.Id, tool.Id, null).IsSuccess);
        Assert.Null(store.Stored.Workspaces.Single().Bookmarks.Single().Color);
    }

    static JsonElement Parsed(ControlReply reply)
    {
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        return JsonDocument.Parse(reply.Output).RootElement;
    }

    [Fact]
    public void Bookmark_color_sets_a_hex_a_palette_name_and_default()
    {
        Assert.True(manager.AddBookmark(work.Id, Tool()).IsSuccess);

        Assert.Equal("#12AB34", Parsed(control.Execute(["bookmark", "color", "Work", "Tool", "#12ab34"])).GetProperty("color").GetString());
        var named = WorkspacePalette.Swatches[0];
        Assert.Equal(named.Hex.ToUpperInvariant(), Parsed(control.Execute(["bookmark", "color", "Work", "Tool", named.Name])).GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, Parsed(control.Execute(["bookmark", "color", "Work", "Tool", "default"])).GetProperty("color").ValueKind);

        var wrong = control.Execute(["bookmark", "color", "Work", "Tool", "#12"]);
        Assert.NotEqual(ControlReply.Ok, wrong.ExitCode);
        Assert.Contains("bookmark color", wrong.Output);
    }
}
