using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Windows.Shell;

namespace Deskbar.Windows.Tests;

// Bookmark chips on the bar (docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md, Task 8): faded icons
// after a workspace's windows, a click that opens them there, the chip menu and the row menu's two items. Nothing
// here starts a program or opens a page: the opener's starts are fakes that record the call, and the clipboard is a
// fake too (ruling P1), so the tests never touch Petre's real clipboard.
//
// The harness: GEPHA (current, holding "Remote Desktop Manager") and Sparrow ("Beeper", C:\beeper.exe).
public class FloatingBarBookmarksTests
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    // Every Button in tree order; the literals match FloatingBar's IconTag and BookmarkTag.
    static IReadOnlyList<Button> Buttons(DependencyObject root) => Descendants(root).OfType<Button>().ToList();

    static IReadOnlyList<Button> Chips(DependencyObject root) => Buttons(root).Where(b => b.Tag is "bookmark").ToList();

    static IReadOnlyList<string> ChipNames(DependencyObject root) => Chips(root).Select(AutomationProperties.GetName).ToList();

    static IReadOnlyList<string> IconTitles(DependencyObject root) =>
        Buttons(root).Where(b => b.Tag is "icon").Select(AutomationProperties.GetName).ToList();

    static Grid RowNamed(Panel rows, string name) =>
        rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(row => AutomationProperties.GetName(row) == name);

    static Guid Id(Harness harness, string name) => harness.Manager.State.Workspaces.Single(w => w.Name == name).Id;

    static Bookmark Link(string url, string? name = null) => BookmarkRules.Link(url, name).Value;

    static Bookmark Program(string name, string exe) => new(Guid.NewGuid(), name, BookmarkKind.Program, exe);

    static Bookmark Added(Harness harness, string workspace, Bookmark bookmark)
    {
        Assert.True(harness.Manager.AddBookmark(Id(harness, workspace), bookmark).IsSuccess);
        return bookmark;
    }

    static IReadOnlyList<string> Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "").ToList();

    static MenuItem Item(ContextMenu menu, string header) => menu.Items.OfType<MenuItem>().Single(i => i.Header as string == header);

    static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    // What the fake starts were asked, in order.
    sealed class Started
    {
        public List<string> Calls { get; } = [];

        public BookmarkStarts Starts => new(
            Program: (exe, _, _) => { Calls.Add($"program {exe}"); return Result.Success<int?>(null); },
            App: id => { Calls.Add($"app {id}"); return Result.Success(); },
            Link: url => { Calls.Add($"link {url}"); return Result.Success(); });
    }

    static BarUnderTest ShowBar(Harness harness, Started? started = null, Func<string?>? clipboard = null)
    {
        var bar = new FloatingBar(harness.Manager)
        {
            Left = -32000,
            Top = -32000,
            BookmarkOpener = new BookmarkOpener(harness.Manager, (started ?? new Started()).Starts, () => new HashSet<string>()),
            ClipboardText = clipboard ?? (() => null),
        };
        bar.Show();
        return new BarUnderTest(bar);
    }

    [Fact]
    public void A_workspace_shows_its_bookmarks_after_its_windows() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "GEPHA", Link("https://alpha.org", "Alpha"));
        Added(harness, "GEPHA", Link("https://beta.org", "Beta"));
        using var bar = ShowBar(harness);

        var row = Buttons(RowNamed(bar.Rows, "GEPHA"));
        Assert.Equal(["Alpha", "Beta"], ChipNames(RowNamed(bar.Rows, "GEPHA")));
        var lastIcon = row.ToList().FindLastIndex(b => b.Tag is "icon");
        var firstChip = row.ToList().FindIndex(b => b.Tag is "bookmark");
        Assert.True(lastIcon >= 0 && firstChip > lastIcon, $"icon at {lastIcon}, first chip at {firstChip}");
        // Only the workspace that has them.
        Assert.Empty(Chips(RowNamed(bar.Rows, "Sparrow")));
    });

    // The same in a row drawn as icons (Sparrow is not the workspace you are on, so it is not the titled list).
    [Fact]
    public void An_icon_row_puts_its_chips_in_the_same_line_as_its_windows() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Link("https://alpha.org", "Alpha"));
        using var bar = ShowBar(harness);

        var chip = Chips(RowNamed(bar.Rows, "Sparrow")).Single();
        var icon = Buttons(RowNamed(bar.Rows, "Sparrow")).Single(b => b.Tag is "icon");
        Assert.Same(VisualTreeHelper.GetParent(icon), VisualTreeHelper.GetParent(chip));
    });

    // The workspace you are on is drawn as its titled list (one window per line, with its title), and there the chips
    // get an icon line of their own under the list rather than a place in a window's line (ruling P12).
    [Fact]
    public void A_titled_row_puts_its_chips_on_a_line_of_their_own() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "GEPHA", Link("https://alpha.org", "Alpha"));
        Added(harness, "GEPHA", Link("https://beta.org", "Beta"));
        using var bar = ShowBar(harness);

        var chips = Chips(RowNamed(bar.Rows, "GEPHA"));
        var icon = Buttons(RowNamed(bar.Rows, "GEPHA")).Single(b => b.Tag is "icon");
        Assert.Same(VisualTreeHelper.GetParent(chips[0]), VisualTreeHelper.GetParent(chips[1]));
        Assert.NotSame(VisualTreeHelper.GetParent(icon), VisualTreeHelper.GetParent(chips[0]));
    });

    [Fact]
    public void A_chip_is_not_an_icon() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Link("https://alpha.org", "Alpha"));
        using var bar = ShowBar(harness);

        Assert.DoesNotContain("Alpha", IconTitles(bar.Rows));
        Assert.Contains("Alpha", ChipNames(bar.Rows));
    });

    [Fact]
    public void A_folded_row_shows_no_chips() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Link("https://alpha.org", "Alpha"));
        Assert.True(harness.Manager.SetWorkspaceFolded(Id(harness, "Sparrow"), true).IsSuccess);
        using var bar = ShowBar(harness);

        Assert.Empty(ChipNames(bar.Rows));
    });

    [Fact]
    public void An_empty_workspace_with_bookmarks_shows_its_chips() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Link("https://alpha.org", "Alpha"));
        harness.Monitor.Initial.Remove(harness.Second);
        harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Disappeared, harness.Second));
        using var bar = ShowBar(harness);

        Assert.Empty(IconTitles(RowNamed(bar.Rows, "Sparrow")));
        Assert.Equal(["Alpha"], ChipNames(RowNamed(bar.Rows, "Sparrow")));
    });

    [Fact]
    public void A_running_program_hides_its_chip() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Program("Beeper", @"C:\beeper.exe"));
        Added(harness, "Sparrow", Program("Other", @"C:\other.exe"));
        using var bar = ShowBar(harness);

        Assert.Equal(["Other"], ChipNames(RowNamed(bar.Rows, "Sparrow")));
    });

    [Fact]
    public void Clicking_a_chip_opens_it_through_the_opener() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var started = new Started();
        Added(harness, "Sparrow", Link("https://alpha.org/", "Alpha"));
        using var bar = ShowBar(harness, started);

        Chips(RowNamed(bar.Rows, "Sparrow")).Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(["link https://alpha.org/"], started.Calls);
        Assert.Equal([harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").DesktopId!.Value], harness.Desktops.Switched);
    });

    [Fact]
    public void The_chip_menu_has_open_open_all_rename_and_remove() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Link("https://alpha.org", "Alpha"));
        using var bar = ShowBar(harness);

        var menu = FloatingBar.MenuOf(Chips(bar.Rows).Single())!;

        Assert.Equal(["Open", "Open all bookmarks", "Rename…", "Remove"], Headers(menu));
    });

    [Fact]
    public void Remove_takes_the_chip_off_the_row() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Link("https://alpha.org", "Alpha"));
        using var bar = ShowBar(harness);

        Click(Item(FloatingBar.MenuOf(Chips(bar.Rows).Single())!, "Remove"));

        Assert.Empty(ChipNames(bar.Rows));
        Assert.Empty(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Bookmarks);
    });

    [Fact]
    public void The_row_menu_offers_open_all_only_with_bookmarks() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Added(harness, "Sparrow", Link("https://alpha.org", "Alpha"));
        using var bar = ShowBar(harness);

        var withBookmarks = Headers(bar.Bar.WorkspaceMenu(Id(harness, "Sparrow"), "Sparrow", folded: false));
        var without = Headers(bar.Bar.WorkspaceMenu(Id(harness, "GEPHA"), "GEPHA", folded: false));

        Assert.Contains("Open all bookmarks", withBookmarks);
        Assert.DoesNotContain("Open all bookmarks", without);
        // The clipboard item is always there, after Insert after… and before the group items.
        Assert.Contains("Bookmark link from clipboard", without);
        Assert.True(withBookmarks.ToList().IndexOf("Insert after…") < withBookmarks.ToList().IndexOf("Open all bookmarks"));
    });

    [Fact]
    public void Bookmark_link_from_clipboard_is_enabled_only_for_a_web_link() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var clipboard = "https://x.org ";
        using var bar = ShowBar(harness, clipboard: () => clipboard);

        var menu = bar.Bar.WorkspaceMenu(Id(harness, "Sparrow"), "Sparrow", folded: false);
        var item = Item(menu, "Bookmark link from clipboard");
        Assert.True(item.IsEnabled);

        // Read again when the menu opens: the clipboard changes while a kept menu lives.
        clipboard = "hello";
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.False(item.IsEnabled);

        clipboard = "https://x.org ";
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(item.IsEnabled);
    });

    [Fact]
    public void Bookmark_link_from_clipboard_adds_a_link_named_after_the_host() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, clipboard: () => "  https://portainer.local:9443/x \n");

        Click(Item(bar.Bar.WorkspaceMenu(Id(harness, "Sparrow"), "Sparrow", folded: false), "Bookmark link from clipboard"));

        var added = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Bookmarks.Single();
        Assert.Equal(("portainer.local", BookmarkKind.Link, "https://portainer.local:9443/x"), (added.Name, added.Kind, added.Target));
        Assert.Equal(["portainer.local"], ChipNames(RowNamed(bar.Rows, "Sparrow")));
    });

    [Fact]
    public void A_clipboard_that_throws_or_holds_no_link_offers_nothing()
    {
        Assert.Equal(Maybe<string>.None, FloatingBar.ClipboardLink(() => throw new System.Runtime.InteropServices.COMException("busy")));
        Assert.Equal(Maybe<string>.None, FloatingBar.ClipboardLink(() => null));
        Assert.Equal(Maybe<string>.None, FloatingBar.ClipboardLink(() => "javascript:alert(1)"));
        Assert.Equal("https://x.org", FloatingBar.ClipboardLink(() => " https://x.org\t").Value);
    }
}

// The chips' pictures (BookmarkIcons): a link's favicon is fetched once per origin, decoded on the UI thread, and
// stored only when it decoded (ruling R6). No real request: the fetcher gets a fake server, and what the fetch posts
// back to the UI thread is queued here and run by the test on its own STA thread.
public sealed class BookmarkIconsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "deskbar-bookmark-icons-" + Guid.NewGuid().ToString("N"));
    readonly BlockingCollection<Action> posted = [];

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    sealed class FakeServer(byte[] body, string type) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body) { Headers = { { "Content-Type", type } } },
            });
        }
    }

    // A real 32 px PNG, made here, so the decode has something it can read.
    static byte[] Png()
    {
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Pbgra32, null, Enumerable.Repeat((byte)0xFF, 32 * 32 * 4).ToArray(), 32 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static readonly Bookmark Portainer = BookmarkRules.Link("https://portainer.local:9443/x", "Portainer").Value;

    BookmarkIcons Icons(FakeServer server) =>
        new(new FaviconStore(folder), new FaviconFetcher(server, TimeSpan.FromSeconds(5)), posted.Add);

    // The tests take what the fetch posted back (`posted.TryTake`) and run it themselves, as the dispatcher would.
    bool Stored => Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any();

    [Fact]
    public void A_favicon_that_does_not_decode_is_not_saved() => StaThread.Run(() =>
    {
        var server = new FakeServer("not an image"u8.ToArray(), "image/x-icon");
        var icons = Icons(server);
        var arrived = 0;

        var first = icons.For(Portainer, () => arrived++);
        Assert.True(posted.TryTake(out var work, TimeSpan.FromSeconds(10)));
        work!();

        Assert.False(Stored);
        Assert.Equal(0, arrived);
        // Tried for this run: the letter tile stays and no second request goes out.
        Assert.Same(first, icons.For(Portainer, () => arrived++));
        Assert.Equal(1, server.Requests);
    });

    [Fact]
    public void A_favicon_that_decodes_is_saved_and_asks_for_a_redraw() => StaThread.Run(() =>
    {
        var server = new FakeServer(Png(), "image/png");
        var icons = Icons(server);
        var arrived = 0;

        var tile = icons.For(Portainer, () => arrived++);
        Assert.True(posted.TryTake(out var work, TimeSpan.FromSeconds(10)));
        work!();

        Assert.True(Stored);
        Assert.Equal(1, arrived);
        var favicon = icons.For(Portainer, () => arrived++);
        Assert.NotSame(tile, favicon);
        Assert.IsAssignableFrom<BitmapSource>(favicon);
    });

    [Fact]
    public void A_stored_favicon_is_never_fetched_again() => StaThread.Run(() =>
    {
        Assert.True(new FaviconStore(folder).Save(new Uri(Portainer.Target), Png()).IsSuccess);
        var server = new FakeServer(Png(), "image/png");

        var favicon = Icons(server).For(Portainer, () => { });

        Assert.IsAssignableFrom<BitmapSource>(favicon);
        Assert.Equal(0, server.Requests);
    });

    [Fact]
    public void Without_a_fetcher_a_link_is_its_letter_tile() => StaThread.Run(() =>
    {
        var icons = new BookmarkIcons();

        Assert.IsType<DrawingImage>(icons.For(Portainer, () => { }));
        Assert.Empty(posted);
    });
}
