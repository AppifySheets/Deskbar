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
//   * a link's chip carries a small light dot at the bottom right, so a web page reads apart from a program;
//   * the row's first chip carries a 1 DIP divider at its own left edge, INSIDE its cell, so the divider costs no
//     width the wrap would have to know about. Drawn only when windows precede it in the lane: a row holding only
//     chips has nothing to divide them from.
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
    // Setting it redraws the rows once, as TimeToday does: the constructor has already drawn them by the time an
    // object initializer assigns this, so without the redraw a running program's chip would show until some window
    // event happened to rebuild the bar.
    public BookmarkOpener? BookmarkOpener
    {
        get => bookmarkOpener;
        init
        {
            bookmarkOpener = value;
            if (value is not null) Rebuild();
        }
    }

    BookmarkOpener? bookmarkOpener;

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
    static readonly Brush ChipDivider = Frozen(0x55, 0xFF, 0xFF, 0xFF);

    // Which chips each workspace row shows, worked out from ONE reading of the windows and the open app ids for the
    // whole rebuild (BookmarkOpener.ShowingNow), not one per row. With no opener every bookmark shows.
    Func<Workspace, IReadOnlyList<Bookmark>> ShowingBookmarks() =>
        BookmarkOpener?.ShowingNow() ?? (workspace => workspace.Bookmarks);

    // One chip. `divider`: this is the row's first chip and windows come before it in the lane.
    Button ChipButton(Guid workspaceId, Bookmark bookmark, bool divider)
    {
        var size = 20 * IconScale;
        // The picture, the dashed outline and (for a link) the dot. Faded as one, so the divider beside it is not.
        var face = new Grid { Width = size, Height = size, Opacity = ChipRestingOpacity, IsHitTestVisible = false };
        var image = new Image { Width = size, Height = size, Source = BookmarkIcons.For(bookmark, Rebuild) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        face.Children.Add(image);
        face.Children.Add(new Rectangle
        {
            Stroke = ChipOutline,
            StrokeThickness = 1,
            StrokeDashArray = [2, 2],
            RadiusX = 3,
            RadiusY = 3,
        });
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
        // standing in its left 1 DIP.
        var cell = new Grid { Width = IconCellWidth, Height = size + 4 };
        cell.Children.Add(face);
        if (divider)
            cell.Children.Add(new Border
            {
                Width = 1,
                Background = ChipDivider,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 3),
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
        button.MouseLeave += (_, _) => face.Opacity = ChipRestingOpacity;

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
//   App      the app's icon from its shell item, as a pin's is (ITaskbarPins.ImageOfApp), kept per app id. A miss is
//            NOT kept, so an app whose icon the shell could not give yet is asked again next time (CLAUDE.md, "Never
//            cache a lookup failure that can succeed later").
//   Link     the site's favicon: from memory, else from the store on disk, else a letter tile drawn now while ONE
//            fetch per origin per run goes out on a pool thread.
//
// The favicon's path, and the rules ruling R6 put on it:
//
//   * one FaviconFetcher for the app's life (it owns an HttpClient), handed in by the composition root;
//   * an origin already stored is never fetched again;
//   * the bytes come back to the UI thread and are decoded THERE (BitmapDecoder, the frame nearest 32 px, frozen),
//     inside a catch, so a body WPF cannot read never throws into the dispatcher;
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

    // Decoded favicons by origin (FaviconKey), app icons by app id, letter tiles by letter and hue.
    readonly Dictionary<string, ImageSource> favicons = new(StringComparer.Ordinal);
    readonly Dictionary<string, ImageSource> apps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(char Letter, double Hue), ImageSource> tiles = [];
    // Origins looked up this run (read from the store, fetched, being fetched, or failed): never asked again until a restart.
    readonly HashSet<string> tried = new(StringComparer.Ordinal);

    // `toUi` posts work to the UI thread: the dispatcher of the thread that made this object, unless a test says
    // otherwise. With no store and no fetcher, links keep their letter tiles and nothing touches disk or network.
    internal BookmarkIcons(FaviconStore? store = null, FaviconFetcher? fetcher = null, Action<Action>? toUi = null)
    {
        this.store = store;
        this.fetcher = fetcher;
        var dispatcher = Dispatcher.CurrentDispatcher;
        this.toUi = toUi ?? (work => dispatcher.BeginInvoke(work));
    }

    // The picture for a chip, now. UI thread only. `arrived` is called (on the UI thread) if a better picture turns up
    // later, which only a link's favicon can.
    internal ImageSource For(Bookmark bookmark, Action arrived) =>
        (bookmark.Kind switch
        {
            BookmarkKind.Program => IconCache.For(bookmark.Target),
            BookmarkKind.App => AppIcon(bookmark.Target),
            _ => LinkIcon(bookmark, arrived),
        }) ?? Tile(bookmark);

    ImageSource? AppIcon(string appId) =>
        apps.TryGetValue(appId, out var known)
            ? known
            : IconCache.Shell.ImageOfApp(appId, 48).Map(IconCache.ToImageSource).Tap(found => apps[appId] = found).GetValueOrDefault();

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
        var decoded = fetched.Bind(bytes => Decode(bytes).ToResult("The answer is not an image WPF can read.").Map(image => (Bytes: bytes, Image: image)));
        if (ClickTrace.On) ClickTrace.Write($"bookmark favicon {key}: {(decoded.IsSuccess ? "ok" : decoded.Error)}");
        if (decoded.IsFailure) return;

        favicons[key] = decoded.Value.Image;
        // A failed save costs only the next start's fetch, so it is traced, not reported.
        store?.Save(link, decoded.Value.Bytes).TapError(error => { if (ClickTrace.On) ClickTrace.Write($"bookmark favicon {key}: {error}"); });
        arrived();
    }

    // An icon file, a PNG or anything else WPF's decoders read, as the frame nearest 32 px (the bar draws a chip at 20
    // DIP on screens up to 200%), frozen so it can be shared. Nothing a decoder throws gets out of here.
    internal static Maybe<ImageSource> Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - 32))
                .ThenByDescending(f => f.PixelWidth)
                .First();
            frame.Freeze();
            return frame;
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
