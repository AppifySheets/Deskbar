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

    static BarUnderTest ShowBar(Harness harness, Started? started = null, Func<string?>? clipboard = null, Action<string>? said = null)
    {
        var bar = new FloatingBar(harness.Manager)
        {
            ShowRefusal = said ?? (_ => { }),
            Left = -32000,
            Top = -32000,
            BookmarkOpener = new BookmarkOpener(harness.Manager, (started ?? new Started()).Starts, () => new HashSet<string>()),
            ClipboardText = clipboard ?? (() => null),
            BookmarkSources = new FakeSources(),
            // As in App.xaml.cs: setting the opener does not redraw, TimeToday's init (after it) does, and that redraw
            // is the first one drawn with the opener (so a running program's chip is hidden).
            TimeToday = _ => TimeSpan.Zero,
        };
        bar.Show();
        return new BarUnderTest(bar);
    }

    // Disk and shell lookups that answer nothing: a drop of a link, or a .txt, never reaches them.
    sealed class FakeSources : IBookmarkSources
    {
        public Maybe<ShortcutTarget> ResolveShortcut(string lnkPath) => Maybe<ShortcutTarget>.None;
        public string? DescribeExe(string exePath) => null;
        public string? NameOfApp(string appId) => null;
        public string? ReadText(string path) => null;
    }

    // A DragEventArgs cannot be built by hand (its constructor is internal), so reflection makes one and the event is
    // raised on the row container exactly as OLE would raise it.
    static string Drag(BarUnderTest bar, string workspace, IDataObject data, RoutedEvent kind)
    {
        var container = RowNamed(bar.Rows, workspace);
        var args = (DragEventArgs)typeof(DragEventArgs)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single()
            .Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move, container, new Point(5, 5)]);
        args.RoutedEvent = kind;
        container.RaiseEvent(args);
        return args.Effects.ToString();
    }

    static DataObject LinkDrag(string url)
    {
        var data = new DataObject();
        data.SetData("UniformResourceLocatorW", url + "\0");
        data.SetData("FileGroupDescriptorW", BookmarkDropDataTests.Descriptor("Portainer.url"));
        return data;
    }

    [Fact]
    public void Dropping_a_link_on_a_row_adds_it_there() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);

        var effects = Drag(bar, "Sparrow", LinkDrag("https://portainer.local:9443/"), DragDrop.DragOverEvent);
        Drag(bar, "Sparrow", LinkDrag("https://portainer.local:9443/"), DragDrop.DropEvent);

        Assert.NotEqual("None", effects);
        var added = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Bookmarks.Single();
        Assert.Equal("Portainer", added.Name);
        Assert.Equal("https://portainer.local:9443/", added.Target);
    });

    [Fact]
    public void The_readout_says_where_the_bookmark_goes() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);

        Drag(bar, "Sparrow", LinkDrag("https://portainer.local:9443/"), DragDrop.DragOverEvent);

        Assert.Equal("→ bookmark in Sparrow", bar.Bar.ReadoutText);
    });

    [Fact]
    public void A_refused_drop_says_why_and_adds_nothing() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\notes.txt" });

        // No Drop is raised: with the effect None OLE never delivers one, and a refusal that did reach Drop would go to
        // Report, which is a modal MessageBox and would block this test.
        var effects = Drag(bar, "Sparrow", data, DragDrop.DragOverEvent);

        Assert.Equal("None", effects);
        Assert.StartsWith("can't bookmark: ", bar.Bar.ReadoutText);
        Assert.Empty(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Bookmarks);
    });

    // The refusal is a modal box in the real bar, so it must wait until Drop has returned: a box raised inside the OLE
    // Drop handler freezes the program that started the drag.
    [Fact]
    public void A_refused_drop_is_said_only_after_drop_returns() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var said = new List<string>();
        using var bar = ShowBar(harness, said: said.Add);
        Drag(bar, "Sparrow", LinkDrag("https://portainer.local:9443/"), DragDrop.DropEvent);

        // The same link again is a duplicate, which is refused.
        Drag(bar, "Sparrow", LinkDrag("https://portainer.local:9443/"), DragDrop.DropEvent);

        Assert.Empty(said);
        bar.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Assert.Single(said);
        Assert.Contains("already has this bookmark", said[0]);
    });

    [Fact]
    public void A_window_drag_still_takes_the_old_path() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);
        var data = new DataObject();
        data.SetData("Deskbar.DraggedWindow", new DraggedWindow(harness.First.Handle, "workspace:other"));

        var effects = Drag(bar, "Sparrow", data, DragDrop.DragOverEvent);

        Assert.Equal("Move", effects);
        Assert.Equal("→ move to Sparrow", bar.Bar.ReadoutText);
    });

    [Fact]
    public void The_window_menu_offers_bookmark_for_a_desktop_app() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);
        var icon = Buttons(RowNamed(bar.Rows, "Sparrow")).Single(b => b.Tag is "icon");

        var menu = FloatingBar.MenuOf(icon)!;
        Click(Item(menu, "Bookmark in this workspace"));

        var added = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Bookmarks.Single();
        Assert.Equal(BookmarkKind.Program, added.Kind);
        Assert.Equal(@"C:\beeper.exe", added.Target);
    });

    [Fact]
    public void The_window_menu_has_no_bookmark_item_for_an_explorer_window() => StaThread.Run(() =>
    {
        var harness = Harness.Build(extraWorkspace: true);
        using var bar = ShowBar(harness);
        var icon = Buttons(RowNamed(bar.Rows, "Archive")).Single(b => b.Tag is "icon");

        Assert.DoesNotContain("Bookmark in this workspace", Headers(FloatingBar.MenuOf(icon)!));
    });

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

    // --- I2 of the Task 8 review: the decode is bounded --------------------------------------------------------------

    // A real PNG of the given size, uniform, so it compresses to almost nothing however large it is.
    static byte[] Png(int size)
    {
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, new byte[size * size * 4], size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // A small real PNG whose header is rewritten to claim `width` x `height` (CRC fixed), with the original tiny image
    // data: the "256 KB that expands to gigabytes" case, without the test having to make gigabytes.
    static byte[] PngClaiming(int width, int height)
    {
        var bytes = Png(1);
        // Signature (8), IHDR length (4), "IHDR" (4), then width and height, big-endian.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29), Crc32(bytes.AsSpan(12, 17)));
        return bytes;
    }

    // The PNG chunk CRC (CRC-32, polynomial 0xEDB88320), bit by bit: short and only ever run on 17 bytes.
    static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void A_favicon_larger_than_256_px_is_refused_and_not_saved() => StaThread.Run(() =>
    {
        var server = new FakeServer(Png(300), "image/png");
        var icons = Icons(server);
        var arrived = 0;

        var first = icons.For(Portainer, () => arrived++);
        Assert.True(posted.TryTake(out var work, TimeSpan.FromSeconds(10)));
        work!();

        Assert.False(Stored);
        Assert.Equal(0, arrived);
        Assert.Same(first, icons.For(Portainer, () => arrived++));
    });

    [Fact]
    public void A_header_claiming_a_huge_image_is_refused_before_any_pixel_is_decoded() => StaThread.Run(() =>
    {
        Assert.Equal(Maybe<ImageSource>.None, BookmarkIcons.Decode(PngClaiming(60000, 60000)));
        // The rewrite itself is sound: the same bytes claiming 1 x 1 still decode.
        Assert.True(BookmarkIcons.Decode(PngClaiming(1, 1)).HasValue);
    });

    [Fact]
    public void A_favicon_of_256_px_or_less_decodes_to_its_own_size() => StaThread.Run(() =>
    {
        var image = (BitmapSource)BookmarkIcons.Decode(Png(256)).Value;

        Assert.Equal(256, image.PixelWidth);
        Assert.True(image.IsFrozen);
    });

    // --- I1 of the Task 8 review: an app's icon is asked off the UI thread, and a miss waits ----------------------------

    static readonly Bookmark Calc = new(Guid.NewGuid(), "Calc", BookmarkKind.App, "Calc!App");
    static PinImage Opaque => new(2, 2, Enumerable.Repeat((byte)0xFF, 16).ToArray());
    static PinImage Blank => new(2, 2, new byte[16]);

    [Fact]
    public void An_app_icon_is_asked_off_the_ui_thread_and_redraws_when_it_arrives() => StaThread.Run(() =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var askedOn = 0;
        var asks = 0;
        var arrived = 0;
        var icons = new BookmarkIcons(toUi: posted.Add, appImage: _ =>
        {
            askedOn = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref asks);
            return Opaque;
        });

        Assert.IsType<DrawingImage>(icons.For(Calc, () => arrived++));
        // Asked once, however many rebuilds come before the answer.
        Assert.IsType<DrawingImage>(icons.For(Calc, () => arrived++));
        Assert.True(posted.TryTake(out var work, TimeSpan.FromSeconds(10)));
        work!();

        Assert.NotEqual(uiThread, askedOn);
        Assert.Equal(1, asks);
        Assert.Equal(1, arrived);
        Assert.IsAssignableFrom<BitmapSource>(icons.For(Calc, () => arrived++));
        Assert.False(posted.TryTake(out _, TimeSpan.FromMilliseconds(200)));
    });

    [Fact]
    public void A_blank_app_icon_is_a_miss_asked_again_only_after_a_minute() => StaThread.Run(() =>
    {
        var clock = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var asks = 0;
        var arrived = 0;
        var icons = new BookmarkIcons(toUi: posted.Add, now: () => clock,
            appImage: _ => Interlocked.Increment(ref asks) == 1 ? Blank : Opaque);

        icons.For(Calc, () => arrived++);
        Assert.True(posted.TryTake(out var first, TimeSpan.FromSeconds(10)));
        first!();
        Assert.Equal(0, arrived);
        Assert.IsType<DrawingImage>(icons.For(Calc, () => arrived++));

        // Within the minute: no second ask.
        clock += TimeSpan.FromSeconds(59);
        Assert.IsType<DrawingImage>(icons.For(Calc, () => arrived++));
        Assert.False(posted.TryTake(out _, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, asks);

        // After it: asked again, and this time it answers.
        clock += TimeSpan.FromSeconds(2);
        icons.For(Calc, () => arrived++);
        Assert.True(posted.TryTake(out var second, TimeSpan.FromSeconds(10)));
        second!();
        Assert.Equal(2, asks);
        Assert.Equal(1, arrived);
        Assert.IsAssignableFrom<BitmapSource>(icons.For(Calc, () => arrived++));
    });

    [Fact]
    public void An_app_icon_lookup_that_throws_is_a_miss() => StaThread.Run(() =>
    {
        var arrived = 0;
        var icons = new BookmarkIcons(toUi: posted.Add, appImage: _ => throw new System.Runtime.InteropServices.COMException("no such app"));

        icons.For(Calc, () => arrived++);
        Assert.True(posted.TryTake(out var work, TimeSpan.FromSeconds(10)));
        work!();

        Assert.Equal(0, arrived);
        Assert.IsType<DrawingImage>(icons.For(Calc, () => arrived++));
    });
}

// Which app ids, read for windows after a rebuild, redraw the rows (FloatingBar.HidesAnAppChip): only an id no window
// carried before, that is some App bookmark's target.
public sealed class HidesAnAppChipTests
{
    static readonly Workspace WithCalc = new Workspace(Guid.NewGuid(), "Tools", Guid.NewGuid())
    {
        Bookmarks = [new Bookmark(Guid.NewGuid(), "Calc", BookmarkKind.App, "Calc!App")],
    };

    static IReadOnlySet<string> Known(params string[] ids) => new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_new_id_of_an_app_bookmark_redraws() =>
        Assert.True(FloatingBar.HidesAnAppChip(["calc!app"], Known(), [WithCalc]));

    [Fact]
    public void An_id_already_open_does_not() =>
        Assert.False(FloatingBar.HidesAnAppChip(["Calc!App"], Known("Calc!App"), [WithCalc]));

    [Fact]
    public void An_id_no_bookmark_names_does_not() =>
        Assert.False(FloatingBar.HidesAnAppChip(["Other!App", null], Known(), [WithCalc]));
}
