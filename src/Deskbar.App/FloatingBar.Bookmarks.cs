using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Windows.Shell;

namespace Deskbar.App;

// BOOKMARK CHIPS in a workspace's row (docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md, "Showing",
// option A of the mockup Petre picked): the programs, apps and web links a workspace is for, as faded icons after
// the row's windows, in the row's first screen lane, so one click starts them there.
//
// What a chip is, decided here and asserted by FloatingBarBookmarksTests:
//
//   * a Button with Tag "bookmark", NEVER the window icons' IconTag: that tag makes a press-drag move a WINDOW
//     (StartedOnIcon) and marks a Button as a window to the tests and the hover card. A chip is not a window;
//   * the same cell width as a window icon (IconCellWidth), so ChipLayout can wrap chips with the icons by the bar's
//     own arithmetic (see GroupRow);
//   * half strength with a dashed outline at rest, full strength under the pointer, so a chip never passes for a
//     running window ("A chip that could pass for a running window" is a non-goal in the spec);
//   * a link's chip is drawn OPEN instead, full strength on a light backing with a solid outline, while the browser window its last open
//     went into still lives (BookmarkOpener.IsLinkOpen, option B). Deskbar sees windows, not tabs, so a link chip never
//     hides the way a running program's does: Petre clicked two links, they opened as tabs in a browser already on
//     that workspace, and the chips still looked "not started". Open is the answer to that, and a click on an open
//     chip opens the link again, as before. The outline stays (solid) so the chip still reads as a bookmark;
//   * a link's chip carries a small light dot at the bottom right, so a web page reads apart from a program;
//   * the row's first chip carries a divider, a 2 DIP bar snapped to device pixels, at its own left edge and drawn
//     into the gap before it, so the divider costs no width the wrap would have to know about. Drawn only when
//     windows precede it in the lane: a row holding only chips has nothing to divide them from.
//
// A click switches to the workspace and starts the bookmark (BookmarkOpener.Open, the same call `deskbar.cmd
// bookmark open` makes). A middle-drag on a chip moves the bar like a middle-drag anywhere else (the bar's own
// preview handlers do it), and its right-click is the chip menu below. Failures go to Report, as every other
// click on the bar does.
public partial class FloatingBar
{
    // Opens bookmarks and decides which chips show (a running program's chip hides). Set by the composition root,
    // which hands the same object to the control pipe so a chip and `bookmark open` cannot differ. Left null (as most
    // tests leave it) every bookmark shows and a click says this copy cannot open them.
    //
    // Setting it does NOT redraw the rows by itself. The constructor has drawn them before an object initializer gets
    // here, so they first show every bookmark; the redraw that TimeToday's init makes, later in the same initializer
    // (App.xaml.cs), is the one that draws with the opener. A second redraw here would only repeat it at startup.
    //
    // It does subscribe the rows to the opener's OpenedLinks: a link's browser window noted after a click, or dropped
    // when that window closes, redraws the rows so its chip turns open or back to rest. The opener lives as long as the
    // bar, so the subscription is not undone.
    //
    // The redraw is POSTED, never run inline. A note is made on the foreground path, inside WindowMonitor.OnWinEvent,
    // and that callback raises its foreground stream outside its own try/catch: an exception from an inline rebuild
    // would unwind through user32's native frame and take the process down, and Rx would end this subscription for
    // good (review of dea775c7). Posted, the rebuild runs from the dispatcher like any other queued work.
    public BookmarkOpener? BookmarkOpener
    {
        get => bookmarkOpener;
        init
        {
            bookmarkOpener = value;
            value?.OpenedLinks.Changed.Subscribe(_ => Dispatcher.BeginInvoke(new Action(Rebuild)));
        }
    }

    readonly BookmarkOpener? bookmarkOpener;

    // The disk and shell lookups a drop or a window's "Bookmark this" asks (Task 9). Set by the composition root.
    public IBookmarkSources? BookmarkSources { get; init; }

    // The chips' pictures (BookmarkIcons below). The default fetches nothing and keeps nothing on disk: a link shows
    // its letter tile. The composition root passes one with the favicon store and the one fetcher.
    internal BookmarkIcons BookmarkIcons { get; init; } = new();

    // Where "Bookmark link from clipboard" reads the clipboard's text (ruling P1). A seam so the tests never touch the
    // real clipboard, which Petre's own session shares with the hidden test desktop.
    internal Func<string?> ClipboardText { get; init; } = ReadClipboard;

    const string BookmarkTag = "bookmark";
    const double ChipRestingOpacity = 0.5;
    const string NoBookmarkOpener = "This copy of Deskbar cannot open bookmarks.";

    // Frozen, as every static brush on this bar must be (CLAUDE.md, "Static WPF brushes must be frozen").
    static readonly Brush ChipOutline = Frozen(0x80, 0xFF, 0xFF, 0xFF);
    static readonly Brush ChipLinkDot = Frozen(0xC0, 0xFF, 0xFF, 0xFF);
    // Light enough that a black favicon reads as lit on the dark row, not so light that a white one vanishes into it.
    static readonly Brush ChipOpenBacking = Frozen(0x90, 0xFF, 0xFF, 0xFF);
    // Bright and solid, so it reads as a bar between the windows and the chips. At 0x55 it was lost beside the dashed
    // outline 2 DIP away: Petre, on the first live try, "vertical bar between them, please".
    static readonly Brush ChipDivider = Frozen(0xC8, 0xFF, 0xFF, 0xFF);

    // Which chips each workspace row shows, worked out from ONE reading of the windows and the open app ids for the
    // whole rebuild (BookmarkOpener.ShowingNow), not one per row. With no opener every bookmark shows.
    Func<Workspace, IReadOnlyList<Bookmark>> ShowingBookmarks() =>
        BookmarkOpener?.ShowingNow() ?? (workspace => workspace.Bookmarks);

    // One chip. `divider`: this is the row's first chip and windows come before it in the lane.
    Button ChipButton(Guid workspaceId, Bookmark bookmark, bool divider)
    {
        var size = 20 * IconScale;
        // An opened link's chip (see the header): full strength at rest too, and its outline solid. Asked once per chip
        // per rebuild; the question is two user32 calls (BookmarkOpener.IsLinkOpen, LinkWindowSource.Alive).
        var open = BookmarkOpener?.IsLinkOpen(bookmark) ?? false;
        var resting = open ? 1.0 : ChipRestingOpacity;
        // The picture, the outline and (for a link) the dot. Faded as one, so the divider beside it is not.
        var face = new Grid { Width = size, Height = size, Opacity = resting, IsHitTestVisible = false };
        // An open chip sits on a light rounded backing, a small lit tile. Full strength alone was not enough: the GitHub
        // favicon is black, and on the dark row it looked the same opened as at rest (Petre: "github is started vs
        // isn't, i think it needs something"; he picked the backing, option A). Drawn first, under the picture.
        if (open)
            face.Children.Add(new Border { Background = ChipOpenBacking, CornerRadius = new CornerRadius(3) });
        var image = new Image { Width = size, Height = size, Source = BookmarkIcons.For(bookmark, Rebuild) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        face.Children.Add(image);
        var outline = new Rectangle { Stroke = ChipOutline, StrokeThickness = 1, RadiusX = 3, RadiusY = 3 };
        // Dashed at rest; an open chip keeps WPF's default, an empty dash array, which is a solid line.
        if (!open) outline.StrokeDashArray = [2, 2];
        face.Children.Add(outline);
        if (bookmark.Kind == BookmarkKind.Link)
            face.Children.Add(new Border
            {
                // A 4 DIP dot inside a 1 DIP dark rim, the rim for the reason the attention dot has one: it has to
                // hold on artwork nobody chose (a white favicon, say).
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = ChipLinkDot,
                BorderBrush = AttentionDotRim,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            });

        // The cell: a window icon's footprint (IconCellWidth wide, the icon plus its 2 DIP each side), the divider
        // standing at its left edge. The window icon before it ends 2 DIP short of this cell and the chip's face starts
        // 2 DIP in, so there is a 4 DIP gap; the divider is laid out at the cell's edge and drawn 1 DIP further left
        // (a render offset, which costs no width the wrap would have to know about), so it covers the middle 2 DIP of
        // the gap and stands clear of both the window's artwork and the dashed outline.
        //
        // 2 DIP wide and SNAPPED, because the 1 DIP line before it never reached the screen as a line. Measured
        // (BookmarkDividerPixelTests, rendered at the bar's default 0.9 scale): the bar's scale is a LayoutTransform on
        // the root, so 1 DIP is 0.9 px starting at a fractional x, and the line was smeared over two device pixels at
        // about a third and two thirds of its strength, 0x46 and 0x89 against the 0xC8 it was given, the dimmer half
        // against the window's artwork, where it read as the icon's own frame. Under the bar's idle fade that is next
        // to nothing, which is what Petre saw: no bar at all between the Edge icon and the first chip. Nothing clipped or
        // covered it; the pixels were there, too faint and too spread out. SnapsToDevicePixels puts the bar's edges on
        // whole pixels, so it lands as one full-strength column (0xCD measured) with a dark column either side, and
        // the 2 DIP keep it at least a pixel wide at any scale the bar allows, where a snapped 1 DIP line at 0.9 can
        // round to nothing.
        var cell = new Grid { Width = IconCellWidth, Height = size + 4 };
        cell.Children.Add(face);
        if (divider)
            cell.Children.Add(new Border
            {
                Width = 2,
                Background = ChipDivider,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1),
                RenderTransform = new TranslateTransform(-1, 0),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
            });

        var button = new Button
        {
            Content = cell,
            Width = IconCellWidth,
            // The window icon's vertical margin, so a line of chips is as tall as a line of icons.
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(0),
            // Transparent, not null: a null background takes no part in hit testing, so the gaps in the chip would
            // fall through to the row and switch workspace instead.
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            Focusable = false,
            Tag = BookmarkTag,
            ToolTip = TooltipOf(bookmark),
        };
        AutomationProperties.SetName(button, bookmark.Name);
        button.MouseEnter += (_, _) => face.Opacity = 1.0;
        button.MouseLeave += (_, _) => face.Opacity = resting;

        // MarkPressConsumed first, as every Button inside a row does: the row's own release handler, further up the
        // same route, would otherwise switch to the row's workspace on top of what the chip does.
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            var outcome = BookmarkOpener?.Open(workspaceId, bookmark.Id) ?? Result.Failure(NoBookmarkOpener);
            if (ClickTrace.On)
                ClickTrace.Write($"bookmark-click {bookmark.Kind} ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
            Report(outcome);
        };
        // Built on the first right-click, not now (FloatingBar.LazyMenus.cs).
        LazyMenu(button, menu => BookmarkMenu(workspaceId, bookmark, menu));
        return button;
    }

    // The name, then what it opens: a program with its arguments, so two chips on one exe can be told apart.
    static string TooltipOf(Bookmark bookmark) =>
        bookmark.Kind == BookmarkKind.Program && !string.IsNullOrWhiteSpace(bookmark.Arguments)
            ? $"{bookmark.Name}\n{bookmark.Target} {bookmark.Arguments}"
            : $"{bookmark.Name}\n{bookmark.Target}";

    // A chip's right-click: Open, Open all bookmarks, Rename…, Remove (the spec's list, in that order).
    internal ContextMenu BookmarkMenu(Guid workspaceId, Bookmark bookmark, ContextMenu? into = null)
    {
        var menu = into ?? new ContextMenu(); // `into`: the empty menu a lazy element already holds (FloatingBar.LazyMenus.cs)
        HoldFadeWhileOpen(menu);

        void Add(string glyph, string header, Action click)
        {
            var item = new MenuItem { Header = header, Icon = MenuGlyph(glyph) };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        Add("↗", "Open", () => Report(BookmarkOpener?.Open(workspaceId, bookmark.Id) ?? Result.Failure(NoBookmarkOpener)));
        Add("▶", "Open all bookmarks", () => Report(OpenAllBookmarks(workspaceId)));
        menu.Items.Add(new Separator());
        // The name as it stands when the menu was filled; the manager trims and refuses an empty one.
        Add("✏", "Rename…", () =>
            PromptDialog.Ask("Rename bookmark", "Name:", bookmark.Name, owner: this)
                .Tap(name => Report(manager.RenameBookmark(workspaceId, bookmark.Id, name))));
        // No confirmation, as "Unpin from taskbar" has none: a bookmark is put back by a drop or the clipboard item.
        Add("✕", "Remove", () => Report(manager.RemoveBookmark(workspaceId, bookmark.Id)));
        return menu;
    }

    Result OpenAllBookmarks(Guid workspaceId) =>
        BookmarkOpener is { } opener ? opener.OpenAll(workspaceId) : Result.Failure(NoBookmarkOpener);

    // The row menu's two items, after Insert after… (WorkspaceMenu). "Open all bookmarks" only when the workspace has
    // any; "Bookmark link from clipboard" always, enabled only while the clipboard holds a web link.
    //
    // The enabled state is read again in the menu's Opened event, not only when the menu is filled: a lazy menu is
    // filled once and then kept for the row's life (FloatingBar.LazyMenus.cs), and the clipboard changes while it
    // lives.
    void AddBookmarkItems(ContextMenu menu, Guid workspaceId, Action<string, string, Action> add)
    {
        if (manager.State.Workspaces.TryFirst(w => w.Id == workspaceId).Map(w => w.Bookmarks.Count > 0).GetValueOrDefault(false))
            add("▶", "Open all bookmarks", () => Report(OpenAllBookmarks(workspaceId)));

        var fromClipboard = new MenuItem
        {
            Header = "Bookmark link from clipboard",
            Icon = MenuGlyph("🔖"),
            IsEnabled = ClipboardLink(ClipboardText).HasValue,
        };
        // Read again at the click: the menu may have stood open while the clipboard changed. A refusal (a duplicate,
        // say) is said through Report like any other.
        fromClipboard.Click += (_, _) => ClipboardLink(ClipboardText).Tap(url =>
            Report(BookmarkRules.Link(url).Bind(bookmark => manager.AddBookmark(workspaceId, bookmark))));
        menu.Opened += (_, _) => fromClipboard.IsEnabled = ClipboardLink(ClipboardText).HasValue;
        menu.Items.Add(fromClipboard);
    }

    // The clipboard's text, trimmed, when it is a web link BookmarkRules would take; nothing otherwise. Any exception
    // from reading the clipboard (another program holding it open, typically) is nothing too: this runs from a menu,
    // and a menu that throws takes the app with it.
    internal static Maybe<string> ClipboardLink(Func<string?> read)
    {
        try
        {
            return read()?.Trim() is { Length: > 0 } text && BookmarkRules.Link(text).IsSuccess ? text : Maybe<string>.None;
        }
        catch
        {
            return Maybe<string>.None;
        }
    }

    static string? ReadClipboard() => Clipboard.ContainsText() ? Clipboard.GetText() : null;
}

// THE CHIPS' PICTURES, kept for the app's life (one per bar; the composition root makes it once).
//
//   Program  the exe's own icon, from the loader the bar uses for windows (IconCache.For).
//   App      the app's icon from its shell item, as a pin's is (ITaskbarPins.ImageOfApp), kept per app id. Asked on a
//            POOL thread, never in the rebuild: the shell lookup is synchronous COM, and FloatingBar.Pins.cs records the
//            same kind of call adding seconds to a rebuild. The raw pixels come back through `toUi`, are judged (an
//            all-transparent picture is "not an answer yet", as IconCache.AskShell says) and become a bitmap only on
//            the UI thread, then `arrived` redraws. A miss is NOT kept for good (CLAUDE.md, "Never cache a lookup
//            failure that can succeed later"), but it is asked again at most once a minute (AppMissRetry), so an
//            uninstalled app does not cost a shell call per rebuild.
//   Link     the site's favicon: from memory, else from the store on disk, else a letter tile drawn now while ONE
//            fetch per origin per run goes out on a pool thread.
//
// The favicon's path, and the rules ruling R6 put on it:
//
//   * one FaviconFetcher for the app's life (it owns an HttpClient), handed in by the composition root;
//   * an origin already stored is never fetched again;
//   * the bytes come back to the UI thread and are decoded THERE, BOUNDED (Decode): the header is read first and an
//     image whose frames are all larger than 256 px is refused before a pixel is decoded, so a 256 KB PNG that
//     declares 60000 x 60000 cannot expand to gigabytes. Then one frame, the one nearest 32 px, is decoded: at most
//     256 x 256 x 4 bytes. All of it inside a catch, so a body WPF cannot read never throws into the dispatcher. Not
//     on the pool thread: any WPF bitmap made there gives that thread a Dispatcher and a hidden window (IconCache.
//     AskShell has the measurement), and with the header checked first the UI thread's share is small and bounded;
//   * an oversized or undecodable image is a failure like a network one: not saved, tried again at the next start;
//   * only a decoded icon is saved, so the store never holds something that would fail again at the next start;
//   * a failure marks the origin tried for THIS run only: it is asked again after a restart, and meanwhile the letter
//     tile stands (the privacy page promises exactly that);
//   * once an icon arrives, `arrived` asks the bar for a rebuild, which redraws the chip with it.
//
// Anything without a picture gets its letter tile (LetterTile: the name's first letter on a colour from the host), so a
// chip is never drawn as nothing, for the reason a window never is (FloatingBar.Placeholder).
sealed class BookmarkIcons
{
    readonly FaviconStore? store;
    readonly FaviconFetcher? fetcher;
    readonly Action<Action> toUi;
    readonly Func<string, Maybe<PinImage>> appImage;
    readonly Func<DateTime> now;

    // How long an app id whose icon the shell did not give waits before it is asked again.
    internal static readonly TimeSpan AppMissRetry = TimeSpan.FromMinutes(1);

    // The largest favicon frame, either side, that is decoded at all. A real favicon is 16 to 256 px.
    internal const int MaxFaviconPixels = 256;

    // Decoded favicons by origin (FaviconKey), app icons by app id, letter tiles by letter and hue.
    readonly Dictionary<string, ImageSource> favicons = new(StringComparer.Ordinal);
    readonly Dictionary<string, ImageSource> apps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(char Letter, double Hue), ImageSource> tiles = [];
    // Origins looked up this run (read from the store, fetched, being fetched, or failed): never asked again until a restart.
    readonly HashSet<string> tried = new(StringComparer.Ordinal);
    // App ids being asked on a pool thread now, and those whose last answer was a miss, with when to ask again.
    readonly HashSet<string> appsAsking = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> appMissUntil = new(StringComparer.OrdinalIgnoreCase);

    // `toUi` posts work to the UI thread: the dispatcher of the thread that made this object, unless a test says
    // otherwise. With no store and no fetcher, links keep their letter tiles and nothing touches disk or network.
    // `appImage` is the shell's app icon lookup (IconCache.Shell at 48 px, as for a pin) and `now` the clock for the
    // miss retry; both are seams for the tests.
    internal BookmarkIcons(FaviconStore? store = null, FaviconFetcher? fetcher = null, Action<Action>? toUi = null,
        Func<string, Maybe<PinImage>>? appImage = null, Func<DateTime>? now = null)
    {
        this.store = store;
        this.fetcher = fetcher;
        var dispatcher = Dispatcher.CurrentDispatcher;
        this.toUi = toUi ?? (work => dispatcher.BeginInvoke(work));
        this.appImage = appImage ?? (id => IconCache.Shell.ImageOfApp(id, 48));
        this.now = now ?? (() => DateTime.UtcNow);
    }

    // The picture for a chip, now. UI thread only. `arrived` is called (on the UI thread) if a better picture turns up
    // later: an app's icon or a link's favicon.
    internal ImageSource For(Bookmark bookmark, Action arrived) =>
        (bookmark.Kind switch
        {
            // IconCache.For keeps a miss for the whole run, so an exe on a drive that mounts later stays a letter tile
            // until Deskbar restarts. Left as it is: the same cache serves every window icon, and one rare case is not
            // worth a second lookup path here.
            BookmarkKind.Program => IconCache.For(bookmark.Target),
            BookmarkKind.App => AppIcon(bookmark.Target, arrived),
            _ => LinkIcon(bookmark, arrived),
        }) ?? Tile(bookmark);

    // Known, or asked once on a pool thread; meanwhile (and after a miss, for AppMissRetry) the letter tile stands.
    ImageSource? AppIcon(string appId, Action arrived)
    {
        if (apps.TryGetValue(appId, out var known)) return known;
        if (appsAsking.Contains(appId) || (appMissUntil.TryGetValue(appId, out var until) && until > now())) return null;

        appsAsking.Add(appId);
        // A lookup that throws is a miss like any other. The pool thread makes no WPF object: raw pixels only.
        Task.Run(() => appImage(appId))
            .ContinueWith(done =>
            {
                var image = done.IsCompletedSuccessfully ? done.Result : Maybe<PinImage>.None;
                toUi(() => TakeApp(appId, image, arrived));
            }, TaskScheduler.Default);
        return null;
    }

    // Back on the UI thread. A blank picture is a store app still loading, so it is a miss, not an answer.
    void TakeApp(string appId, Maybe<PinImage> image, Action arrived)
    {
        appsAsking.Remove(appId);
        if (image.HasNoValue || IconCache.IsBlank(image.Value))
        {
            appMissUntil[appId] = now() + AppMissRetry;
            return;
        }
        appMissUntil.Remove(appId);
        apps[appId] = IconCache.ToImageSource(image.Value);
        arrived();
    }

    ImageSource? LinkIcon(Bookmark bookmark, Action arrived)
    {
        if (!Uri.TryCreate(bookmark.Target, UriKind.Absolute, out var link)) return null;
        var key = FaviconKey.Of(link);
        if (favicons.TryGetValue(key, out var known)) return known;
        // Looked up once a run. A stored icon that would not decode (only possible if the file was damaged, since
        // only decoded icons are saved) keeps its letter tile and is not fetched over: "never fetch an origin
        // already stored".
        if (!tried.Add(key)) return null;

        var stored = store?.Cached(link) ?? Maybe<byte[]>.None;
        if (stored.HasValue)
            return Decode(stored.Value).Tap(image => favicons[key] = image).GetValueOrDefault();

        Fetch(link, key, arrived);
        return null;
    }

    // On a pool thread; the answer, success or not, comes back through `toUi`. A faulted task (the fetcher never
    // throws, but a pool thread could still be refused) is a failure like any other.
    void Fetch(Uri link, string key, Action arrived)
    {
        if (fetcher is not { } client) return;
        Task.Run(() => client.Fetch(link, CancellationToken.None))
            .ContinueWith(done =>
            {
                var fetched = done.IsCompletedSuccessfully ? done.Result : Result.Failure<byte[]>("The fetch did not finish.");
                toUi(() => Take(link, key, fetched, arrived));
            }, TaskScheduler.Default);
    }

    // Back on the UI thread: decode, and only then keep, save and redraw.
    void Take(Uri link, string key, Result<byte[]> fetched, Action arrived)
    {
        var decoded = fetched.Bind(bytes => Decode(bytes).ToResult($"The answer is not an image WPF can read, or is over {MaxFaviconPixels} px.").Map(image => (Bytes: bytes, Image: image)));
        if (ClickTrace.On) ClickTrace.Write($"bookmark favicon {key}: {(decoded.IsSuccess ? "ok" : decoded.Error)}");
        if (decoded.IsFailure) return;

        favicons[key] = decoded.Value.Image;
        // A failed save costs only the next start's fetch, so it is traced, not reported.
        store?.Save(link, decoded.Value.Bytes).TapError(error => { if (ClickTrace.On) ClickTrace.Write($"bookmark favicon {key}: {error}"); });
        arrived();
    }

    // An icon file, a PNG or anything else WPF's decoders read, as the frame nearest 32 px (the bar draws a chip at 20
    // DIP on screens up to 200%), frozen so it can be shared. BOUNDED, because the bytes come from any web server:
    //
    //   1. the decoder is opened with no cache (BitmapCacheOption.None), which reads the header and the frames' sizes
    //      and decodes no pixels;
    //   2. frames larger than MaxFaviconPixels either side are left out, and none left means no icon;
    //   3. only the chosen frame is decoded, converted to Pbgra32 and copied out, so the work is at most 256 x 256 x 4
    //      bytes whatever the file declares, and the copy keeps no hold on the stream.
    //
    // Nothing a decoder throws gets out of here.
    internal static Maybe<ImageSource> Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames
                .Where(f => f.PixelWidth is > 0 and <= MaxFaviconPixels && f.PixelHeight is > 0 and <= MaxFaviconPixels)
                .OrderBy(f => Math.Abs(f.PixelWidth - 32))
                .ThenByDescending(f => f.PixelWidth)
                .FirstOrDefault();
            if (frame is null) return Maybe<ImageSource>.None;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            var stride = frame.PixelWidth * 4;
            var pixels = new byte[stride * frame.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var image = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            image.Freeze();
            return image;
        }
        catch
        {
            return Maybe<ImageSource>.None;
        }
    }

    ImageSource Tile(Bookmark bookmark)
    {
        var host = bookmark.Kind == BookmarkKind.Link && Uri.TryCreate(bookmark.Target, UriKind.Absolute, out var link)
            ? link.Host
            : bookmark.Target;
        var (letter, hue) = LetterTile.Of(bookmark.Name, host);
        if (!tiles.TryGetValue((letter, hue), out var tile)) tiles[(letter, hue)] = tile = DrawTile(letter, hue);
        return tile;
    }

    // A 32-unit rounded square in the host's colour with the letter in white, as a vector image so it is sharp at
    // every icon size and screen scale.
    static ImageSource DrawTile(char letter, double hue)
    {
        var text = new FormattedText(letter.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 20, Brushes.White, 1.0);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(FromHsl(hue, 0.45, 0.42)), null,
            new RectangleGeometry(new Rect(0, 0, 32, 32), 6, 6)));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null,
            text.BuildGeometry(new Point((32 - text.Width) / 2, (32 - text.Height) / 2))));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    // The standard HSL to RGB conversion; hue in degrees, saturation and lightness 0 to 1.
    static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var sector = hue / 60;
        var second = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (r, g, b) = sector switch
        {
            < 1 => (chroma, second, 0.0),
            < 2 => (second, chroma, 0.0),
            < 3 => (0.0, chroma, second),
            < 4 => (0.0, second, chroma),
            < 5 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };
        var lift = lightness - chroma / 2;
        static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromRgb(Byte(r + lift), Byte(g + lift), Byte(b + lift));
    }
}
