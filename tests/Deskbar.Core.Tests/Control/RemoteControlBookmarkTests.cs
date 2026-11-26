using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Control;

// The `bookmark` pipe commands: the remote twins of the bookmark chip menu and the row menu's bookmark items.
public sealed class RemoteControlBookmarkTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeStore store = new();
    readonly List<string> calls = [];
    readonly WorkspaceManager manager;
    readonly RemoteControl control;
    readonly RemoteControl withoutOpener;
    readonly Workspace work;

    public RemoteControlBookmarkTests()
    {
        var home = new DesktopInfo(Guid.NewGuid(), "Home");
        var workDesktop = new DesktopInfo(Guid.NewGuid(), "Work");
        desktops.Desktops.AddRange([home, workDesktop]);
        desktops.CurrentDesktopId = home.Id;
        work = new Workspace(Guid.NewGuid(), "Work", workDesktop.Id);
        store.Stored = AppState.Empty with { Workspaces = [new Workspace(Guid.NewGuid(), "Home", home.Id), work] };
        manager = new WorkspaceManager(desktops, new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        var starts = new BookmarkStarts(
            Program: (exe, args, folder) => { calls.Add($"program {exe} [{args}]"); return Result.Success<int?>(7); },
            App: id => { calls.Add($"app {id}"); return Result.Success(); },
            Link: url => { calls.Add($"link {url}"); return Result.Success(); });
        control = new RemoteControl(manager, bookmarks: new BookmarkOpener(manager, starts, () => new HashSet<string>()));
        withoutOpener = new RemoteControl(manager);
    }

    static JsonElement Parsed(ControlReply reply)
    {
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        return JsonDocument.Parse(reply.Output).RootElement;
    }

    JsonElement Listed(string workspace = "Work") => Parsed(control.Execute(["bookmark", "list", workspace]));

    [Fact]
    public void Add_url_list_rename_remove_round_trip()
    {
        var added = Parsed(control.Execute(["bookmark", "add", "Work", "--url", "https://example.com/a"]));
        Assert.Equal("example.com", added.GetProperty("name").GetString());
        Assert.Equal("link", added.GetProperty("kind").GetString());

        var listed = Listed();
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal("Work", listed[0].GetProperty("workspace").GetString());
        Assert.False(listed[0].GetProperty("running").GetBoolean());

        Assert.Equal("Docs", Parsed(control.Execute(["bookmark", "rename", "Work", "example.com", "--name", " Docs "]))
            .GetProperty("name").GetString());
        Assert.Equal("Docs", manager.State.Workspaces.First(w => w.Id == work.Id).Bookmarks.Single().Name);

        Parsed(control.Execute(["bookmark", "remove", "work", "docs"]));
        Assert.Equal(0, Listed().GetArrayLength());
    }

    [Fact]
    public void Add_program_joins_the_arguments_after_the_separator()
    {
        var added = Parsed(control.Execute(
            ["bookmark", "add", "Work", "--program", @"C:\RustDesk\rustdesk.exe", "--cwd", @"C:\RustDesk", "--", "--connect", "a b"]));

        Assert.Equal("program", added.GetProperty("kind").GetString());
        Assert.Equal("rustdesk", added.GetProperty("name").GetString());
        Assert.Equal("--connect \"a b\"", added.GetProperty("arguments").GetString());
        Assert.Equal(@"C:\RustDesk", added.GetProperty("workingDirectory").GetString());
    }

    [Fact]
    public void Add_app_takes_the_id_and_an_optional_name()
    {
        var added = Parsed(control.Execute(["bookmark", "add", "Work", "--app", "Contoso.App_abc!App", "--name", "Contoso"]));

        Assert.Equal("app", added.GetProperty("kind").GetString());
        Assert.Equal("Contoso", added.GetProperty("name").GetString());
        Assert.Equal("Contoso.App_abc!App", added.GetProperty("target").GetString());
    }

    [Fact]
    public void Add_with_two_kinds_or_none_prints_the_usage_line()
    {
        foreach (var args in new[]
                 {
                     new[] { "bookmark", "add", "Work" },
                     ["bookmark", "add", "Work", "--url", "https://a.example", "--app", "x"],
                     ["bookmark", "add", "Work", "--url", "https://a.example", "--", "x"],
                 })
        {
            var reply = control.Execute(args);
            Assert.Equal(ControlReply.Failed, reply.ExitCode);
            Assert.Contains("usage: deskbar.cmd bookmark add <workspace>", reply.Output);
        }
    }

    [Fact]
    public void A_duplicate_add_is_refused()
    {
        Parsed(control.Execute(["bookmark", "add", "Work", "--url", "https://example.com/a"]));

        Assert.Equal(ControlReply.Failed, control.Execute(["bookmark", "add", "Work", "--url", "https://EXAMPLE.com/a"]).ExitCode);
    }

    [Fact]
    public void An_unsafe_url_is_refused()
    {
        Assert.Equal(ControlReply.Failed, control.Execute(["bookmark", "add", "Work", "--url", "javascript:alert(1)"]).ExitCode);
        Assert.Equal(0, Listed().GetArrayLength());
    }

    [Fact]
    public void An_ambiguous_name_is_refused_and_lists_both_ids()
    {
        var first = new Bookmark(Guid.NewGuid(), "Docs", BookmarkKind.Link, "https://a.example/");
        var second = new Bookmark(Guid.NewGuid(), "docs", BookmarkKind.Link, "https://b.example/");
        Assert.True(manager.AddBookmark(work.Id, first).IsSuccess);
        Assert.True(manager.AddBookmark(work.Id, second).IsSuccess);

        var reply = control.Execute(["bookmark", "remove", "Work", "DOCS"]);

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains($"Two bookmarks in Work are named 'DOCS': {first.Id}, {second.Id}. Name one by its id.", reply.Output);
        // Nothing was removed, and the id names one unambiguously.
        Assert.Equal(2, Listed().GetArrayLength());
        Parsed(control.Execute(["bookmark", "remove", "Work", second.Id.ToString()]));
        Assert.Equal(1, Listed().GetArrayLength());
    }

    [Fact]
    public void Open_switches_and_starts_through_the_opener()
    {
        Parsed(control.Execute(["bookmark", "add", "Work", "--url", "https://example.com/a", "--name", "Site"]));

        Parsed(control.Execute(["bookmark", "open", "Work", "site"]));

        Assert.Equal(["link https://example.com/a"], calls);
        Assert.Equal([work.DesktopId!.Value], desktops.Switches);
    }

    [Fact]
    public void Open_all_reports_what_it_opened()
    {
        Parsed(control.Execute(["bookmark", "add", "Work", "--url", "https://example.com/a", "--name", "Site"]));
        Parsed(control.Execute(["bookmark", "add", "Work", "--app", "Contoso.App_abc!App", "--name", "Contoso"]));

        var reply = Parsed(control.Execute(["bookmark", "open-all", "Work"]));

        Assert.Equal(["Site", "Contoso"], reply.GetProperty("opened").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(["link https://example.com/a", "app Contoso.App_abc!App"], calls);
    }

    [Fact]
    public void Open_without_an_opener_is_refused()
    {
        Parsed(withoutOpener.Execute(["bookmark", "add", "Work", "--url", "https://example.com/a", "--name", "Site"]));

        var open = withoutOpener.Execute(["bookmark", "open", "Work", "Site"]);
        var openAll = withoutOpener.Execute(["bookmark", "open-all", "Work"]);

        Assert.Equal(ControlReply.Failed, open.ExitCode);
        Assert.Equal("This copy of Deskbar cannot open bookmarks.", open.Output);
        Assert.Equal("This copy of Deskbar cannot open bookmarks.", openAll.Output);
        // list still works and reports nothing as running.
        Assert.False(Parsed(withoutOpener.Execute(["bookmark", "list"]))[0].GetProperty("running").GetBoolean());
    }

    // Option B: a link whose browser window is still open lists as running, the answer that draws its chip open, so
    // the pipe and the bar agree. The watch here reports its window at once and the window stays alive.
    [Fact]
    public void List_reports_an_opened_link_as_running()
    {
        var window = new LinkWindow(new WindowHandle(0x77), 900);
        var starts = new BookmarkStarts((_, _, _) => Result.Success<int?>(null), _ => Result.Success(), _ => Result.Success());
        var watched = new RemoteControl(manager, bookmarks: new BookmarkOpener(manager, starts, () => new HashSet<string>(),
            new LinkWindows(_ => System.Reactive.Linq.Observable.Return(window), _ => true, System.Reactive.Linq.Observable.Never<WindowHandle>())));
        Parsed(watched.Execute(["bookmark", "add", "Work", "--url", "https://example.com/a", "--name", "Site"]));
        Assert.False(Parsed(watched.Execute(["bookmark", "list", "Work"]))[0].GetProperty("running").GetBoolean());

        Parsed(watched.Execute(["bookmark", "open", "Work", "Site"]));

        Assert.True(Parsed(watched.Execute(["bookmark", "list", "Work"]))[0].GetProperty("running").GetBoolean());
    }

    // `bookmark move`, the twin of dragging a chip: to a screen, back to any screen, and to another workspace.
    [Fact]
    public void Move_sets_the_screen_forgets_it_and_moves_to_another_workspace()
    {
        Parsed(control.Execute(["bookmark", "add", "Work", "--program", @"C:\RustDesk\rustdesk.exe"]));

        Assert.Equal(2, Parsed(control.Execute(["bookmark", "move", "Work", "rustdesk", "--screen", "2"])).GetProperty("screen").GetInt32());
        Assert.Equal(2, Listed()[0].GetProperty("screen").GetInt32());

        Assert.Equal(JsonValueKind.Null, Parsed(control.Execute(["bookmark", "move", "Work", "rustdesk", "--any-screen"])).GetProperty("screen").ValueKind);

        var moved = Parsed(control.Execute(["bookmark", "move", "Work", "rustdesk", "--to", "Home", "--screen", "1"]));
        Assert.Equal("Home", moved.GetProperty("workspace").GetString());
        Assert.Equal(1, moved.GetProperty("screen").GetInt32());
        Assert.Equal(0, Listed().GetArrayLength());
        Assert.Equal(1, Listed("Home").GetArrayLength());
    }

    [Fact]
    public void Move_refuses_a_screen_for_a_link_and_a_duplicate_in_the_target()
    {
        Parsed(control.Execute(["bookmark", "add", "Work", "--url", "https://example.com/", "--name", "Site"]));
        Parsed(control.Execute(["bookmark", "add", "Home", "--url", "https://example.com/", "--name", "Same"]));

        var screen = control.Execute(["bookmark", "move", "Work", "Site", "--screen", "2"]);
        var duplicate = control.Execute(["bookmark", "move", "Work", "Site", "--to", "Home"]);

        Assert.NotEqual(ControlReply.Ok, screen.ExitCode);
        Assert.Contains("browser", screen.Output);
        Assert.NotEqual(ControlReply.Ok, duplicate.ExitCode);
        Assert.Contains("already has this bookmark", duplicate.Output);
        Assert.Equal(1, Listed().GetArrayLength());
    }

    [Theory]
    [InlineData("move", "Work", "x")] // nothing asked
    [InlineData("move", "Work", "x", "--screen", "0")]
    [InlineData("move", "Work", "x", "--screen", "two")]
    [InlineData("move", "Work", "x", "--screen", "2", "--any-screen")]
    [InlineData("move", "Work", "x", "--to")]
    [InlineData("move", "Work", "x", "--to", "Home", "--to", "Work")]
    [InlineData("move", "Work", "x", "--any-screen", "--any-screen")]
    public void Move_with_wrong_options_prints_the_usage(params string[] args)
    {
        var parsed = BookmarkArguments.Parse(args);

        Assert.True(parsed.IsFailure);
        Assert.Contains("bookmark move", parsed.Error);
    }

    [Fact]
    public void Move_parses_its_options_in_any_order()
    {
        var parsed = (BookmarkRequest.Move)BookmarkArguments.Parse(["move", "Work", "x", "--screen", "3", "--to", "Home"]).Value;

        Assert.Equal("Home", parsed.To);
        Assert.Equal(ScreenChange.On(3), parsed.Screen);
        Assert.Equal(ScreenChange.Any, ((BookmarkRequest.Move)BookmarkArguments.Parse(["move", "Work", "x", "--any-screen"]).Value).Screen);
    }

    // Ruling P6: C runtime quoting, as CommandLineToArgvW reads it back.
    [Theory]
    [InlineData(new[] { "a b", "c" }, "\"a b\" c")]
    [InlineData(new[] { "x\"y" }, "\"x\\\"y\"")]
    [InlineData(new[] { @"C:\dir\" }, @"C:\dir\")]
    [InlineData(new[] { "" }, "\"\"")]
    [InlineData(new[] { @"C:\my dir\" }, "\"C:\\my dir\\\\\"")]
    [InlineData(new[] { "a\\\"b" }, "\"a\\\\\\\"b\"")]
    public void Join_quotes_like_the_c_runtime(string[] arguments, string expected) =>
        Assert.Equal(expected, BookmarkArguments.Join(arguments));
}
