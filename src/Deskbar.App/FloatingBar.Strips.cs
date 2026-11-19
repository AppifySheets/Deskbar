using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Core.Overview;
using Deskbar.Core.Persistence;

namespace Deskbar.App;

// THE BAR'S SIDE OF THE SCREEN STRIPS. With the window list's place set to the screens (WindowListPlace.Screens),
// every screen has a strip along its top edge (ScreenStrip, one window each) listing the current workspace's windows
// on that screen. Petre: "let's add a feature for a new taskbar-like bar at the top of each monitor, for its tasks".
// The design is docs/superpowers/specs/2026-11-19-screen-strips-design.md; this file is "The bar's rebuild" in its
// list of parts.
//
// THE BAR BUILDS EVERY TAB, the strip only hosts it. A tab is IconButton, the code that builds every window button on
// the bar, with a tab's dressing on top (StripTab), so its click, menu, drag, hover card and marks cannot drift from
// the bar's (the spec's third anti-goal). The cost of that choice is what IconButton assumes about living inside the
// bar's window, and the measurements say exactly which assumptions break
// (docs/superpowers/notes/2026-11-19-screen-strips-probes.md, section 2):
//
//   * PRESS STATE. The bar's own preview handlers record whether a press was seen, and an icon whose release looks
//     orphaned toggles its window a second time. A press on a strip never reaches the bar's window, so each strip
//     forwards its presses and releases here (NoteStripPress, NoteStripRelease), the press-state half only: the other
//     half of those handlers moves or resizes the bar.
//   * THE HOVER CARD. ShowHoverCard places the card beside the bar, level with the anchor measured in the bar's
//     coordinates, so a tab's card opened at the bar, on whichever screen that is. That measurement does not throw for
//     an element in another window (WPF translates through screen coordinates, measured in FloatingBarStripsTests); it
//     only puts the card in the wrong place. A tab's card opens below its strip, under the tab, placed against the
//     strip's window (PlaceHoverCardBelowStripTab). The watch that closes the card
//     asks Mouse.DirectlyOver, which WPF answers for every window on this thread, so a pointer over a strip's tab is
//     already a pointer the watch can see; it needs nothing more.
//
// THE STRIPS ARE FILLED INSIDE THE BAR'S REBUILD, never by a rebuild of their own: a rebuild can re-enter itself (COM
// calls pump messages, CLAUDE.md "Traps"), and the guard in Rebuild is what keeps a re-entered one from doubling
// anything. SetTabs replaces a strip's tabs outright, so a strip cannot double either way.
//
// THE STRIPS OUTLIVE THE BAR'S VISIBILITY. They are the window list, so while the bar stands behind its taskbar button
// the rebuild triggers that waited for a visible bar still run (RebuildWanted), and the rebuild fills the strips
// without building the bar's own rows (StripsAlone), which ShowBar rebuilds before it shows the bar anyway.
public partial class FloatingBar
{
    // The strips there are now, one per screen. Set by the composition root, which owns them (the strip set). Empty by
    // default, which is every test but the strip tests and every moment the place is not the screens.
    public Func<IReadOnlyList<ScreenStrip>> Strips { get; init; } = () => [];

    // Tags on a tab's parts, so a test can find them without knowing how the tab is laid out.
    internal const string StripUnderlineTag = "strip-underline";
    internal const string StripTitleTag = "strip-title";

    // The tab's two colours from the mockup Petre picked ("B", spec "The look"): the 2 DIP underline every tab has, grey
    // at rest, and the lighter fill behind the window you are in. Frozen, as every static brush must be (CLAUDE.md).
    static readonly Brush StripUnderlineRest = Frozen(0xFF, 0x3A, 0x3F, 0x49);
    static readonly Brush StripActiveFill = Frozen(0xFF, 0x1F, 0x23, 0x2B);

    // The window list is on the screens.
    bool StripsOn => manager.State.WindowListPlace == WindowListPlace.Screens;

    // Whether a rebuild trigger should rebuild: a visible bar always did, and now so does a hidden one while there are
    // strips to fill. Every trigger that used to ask IsVisible asks this.
    bool RebuildWanted => IsVisible || (StripsOn && Strips().Count > 0);

    // A rebuild that fills the strips and nothing else: the list is on the screens and the bar is behind its taskbar
    // button. Minimized as well as not visible, because ShowBar rebuilds BEFORE it shows the bar, and that rebuild must
    // draw the rows the bar is about to show.
    bool StripsAlone => StripsOn && Minimized && !IsVisible;

    // The pointer is over one of the strips. Rebuild defers while the left button is down here as it does over the
    // bar: a rebuild between a press and its release throws the pressed tab away, and a button that no longer exists
    // raises no Click (#48's mechanism, in another window).
    bool PointerOnStrip() => Strips().Any(strip => strip.IsMouseOver);

    // The strips this bar has wired its press forwarding to, held weakly so a closed strip is not kept alive here.
    readonly ConditionalWeakTable<ScreenStrip, object> wiredStrips = new();

    // The rebuild of a minimized bar (see StripsAlone): the facts every icon reads while it is built, worked out as the
    // full rebuild works them out, then the strips. The aging overlays are forgotten as a full rebuild forgets them,
    // or each pass would add every tab's overlay to the list again; the bar's own hidden rows lose their ageing until
    // ShowBar redraws them, which costs nothing anyone can see. The icon watch is started as the full rebuild starts it,
    // so a tab drawn with a placeholder gets its real icon while the bar is away too.
    void FillStripsAlone(Overview overview)
    {
        ForgetAgingIcons();
        ForgetWindowFits(); // the maximized and tile badges belong to the tabs about to be replaced (FloatingBar.WindowState.cs)
        frontsElsewhere = FrontsElsewhere.Of(overview);
        ShowFocusRings(overview);
        NoteActivity(overview);
        pinnedNow = overview.Pinned;
        FillStrips(overview);
        if (IconCache.HasPendingIcons && !iconWatch.IsEnabled) iconWatch.Start();
    }

    // Hands every strip its screen's tabs: the windows of the workspace you are on (or of the unnamed desktop you are
    // on) whose screen is that strip's, pinned windows included, in the order the titled list uses, under the
    // workspace's name on the colour its row wears. With the list anywhere else this does nothing; the strip set has
    // closed the strips already.
    //
    // A window whose screen is unknown, or is a screen no strip stands on, goes on the first strip in reading order,
    // as the bar puts such a window in its first lane: a window drawn nowhere cannot be found.
    void FillStrips(Overview overview)
    {
        if (!StripsOn) return;
        var strips = Strips();
        if (strips.Count == 0) return;
        strips.ToList().ForEach(WireStrip);

        var workspace = overview.Workspaces.FirstOrDefault(w => w.IsCurrent);
        var desktop = workspace is null ? overview.OtherDesktops.FirstOrDefault(d => d.IsCurrent && d.DesktopId != Guid.Empty) : null;
        var name = workspace?.Workspace.Name ?? desktop?.Name ?? "";
        // An unnamed desktop has no colour: null puts its tag on the strip's neutral grey.
        var accent = workspace is null ? null : WorkspaceAccent(overview, workspace);

        // The titled list's order: the workspace's own windows as the manager orders them, then the pinned windows it
        // does not already hold (see GroupRow). The list's sort by screen is the split by strip below.
        var own = workspace?.Running ?? desktop?.Windows ?? [];
        var rows = own.Concat(overview.Pinned.Where(p => own.All(r => r.Window.Handle != p.Window.Handle))).ToList();

        var numbers = strips.ToDictionary(strip => strip, strip => ScreenNumberOf(overview, strip.Screen));
        var known = numbers.Values.Where(n => n.HasValue).Select(n => n.Value).ToHashSet();
        var order = MonitorArrangement.ReadingOrder(strips.Select((strip, i) => (i, strip.Screen)).ToDictionary(x => x.i, x => x.Screen));
        var first = strips[order[0]];

        bool OnStrip(WindowRow row, ScreenStrip strip) =>
            row.Monitor.HasValue && known.Contains(row.Monitor.Value)
                ? numbers[strip].HasValue && numbers[strip].Value == row.Monitor.Value
                : ReferenceEquals(strip, first);

        strips.ToList().ForEach(strip =>
            strip.SetTabs(name, accent, rows.Where(row => OnStrip(row, strip)).Select(row => StripTab(name, row, accent)).ToList()));
    }

    // One window's tab: IconButton, exactly as the bar's own buttons are built, under the titled list's group key, then
    // dressed as a tab. Icon on the left (with the pin badge for a pinned window, as in the titled list), the title in
    // the titled list's strength (TitleBrush), a grey ring round the tab, and for the window you are in the
    // lighter fill and the ring in `accent`, the workspace's colour.
    //
    // The bare chrome rather than the marked one the titled list uses: the mockup's fill and underline are the tab's
    // "you are in this window", and the marker's ring, glow and bar on top of them would say it three more times. Every
    // mark on the ICON stays (the opacity ladder, the attention dot, the tab count, the sound ring, the family band,
    // ageing), since those are the icon's content, which this keeps whole.
    //
    // The title hides itself when the tab is too narrow to show a letter of it (StripPlan.ShowsTitle). The strip sets
    // each tab's Width and never looks inside, so the title follows the Width through a binding, which answers at
    // once, before any layout pass, and holds nothing once the tab is thrown away.
    FrameworkElement StripTab(string groupLabel, WindowRow row, Brush? accent)
    {
        // onScreen: every tab's window is on the desktop in view, so its hover card has no picture (see IconButton).
        var button = (Button)IconButton(groupLabel, CurrentListGroupKey, row, onScreen: true);
        var art = (UIElement)button.Content;
        button.Content = null;
        var icon = pinnedNow.Any(p => p.Window.Handle == row.Window.Handle) ? WithPinBadge(art) : art;
        DockPanel.SetDock(icon, Dock.Left);

        var title = new TextBlock
        {
            Tag = StripTitleTag,
            Text = row.Window.DisplayName,
            // A FIXED 12, deliberately NOT through the bar's text size setting. That setting exists because the bar is
            // narrow and far from the eye, and Petre has it at +4.5, which made these titles 14 ("it's kinda large
            // still"). A strip runs the full width of its screen and needs none of that. Then 9.5 fixed was "too small
            // now", 11 was planned, and he asked for the middle of what he had seen: 9.5 and 14, so 12.
            FontSize = 12,
            Foreground = TitleBrush(row),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 2, 0),
        };
        title.SetBinding(VisibilityProperty, new Binding(nameof(Width)) { Source = button, Converter = TitleShownAtWidth.Instance });

        var line = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(icon);
        line.Children.Add(title);

        // A RING round each tab, grey at rest and in the workspace's colour round the window you are in, with the lighter
        // fill inside it. The mockup had a 2 DIP underline instead (the tag still says so). The focus ring (WindowRings)
        // frames a maximized window just inside the work area, so its top line runs right under the strip in the same
        // colour as the active tab's underline: the two merged into one full-width line and Petre could no longer tell
        // which tab was active ("fullscreen masks which window is active in the strip"). He offered lines at the top or
        // rings, tried the lines, then "let's try rings on buttons in the strip".
        //
        // Inset 2 DIP from every edge of the tab, so two neighbours' rings never touch and the bottom of the ring stays
        // clear of the frame's line under the strip. Behind the icon and title (added first), so its fill is under them.
        var ring = new Border
        {
            Tag = StripUnderlineTag,
            Margin = new Thickness(2),
            // ONE PHYSICAL PIXEL, set once the ring knows its screen's scale (OnePixel, on Loaded). It went 1.5 DIP (3 px
            // plus an anti-aliased fourth at Petre's 225%), then 1 DIP (2 px, Windows' own border), then "can you do
            // 1px?", like the window's ring frame (RingFrame).
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            CornerRadius = new CornerRadius(4),
            BorderBrush = row.IsActive ? accent ?? StripUnderlineRest : StripUnderlineRest,
            Background = row.IsActive ? StripActiveFill : Brushes.Transparent,
            IsHitTestVisible = false,
        };

        ring.Loaded += (_, _) => ring.BorderThickness = new Thickness(1 / VisualTreeHelper.GetDpi(ring).DpiScaleX);

        // Transparent rather than null, so the whole tab answers the pointer, not only its icon and text.
        var body = new Grid { Height = ScreenStrip.HeightDip, Background = Brushes.Transparent };
        body.Children.Add(ring);
        body.Children.Add(line);

        button.Content = body;
        button.Template = BareStretchButton;
        // What PaintMarker gave the button (the focused fill and ring) is the marker's, which a tab does not draw.
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(0);
        return button;
    }

    // A titled line's title: white for the window you are in, dimmer for one covered on its screen, the resting
    // strength otherwise, unknown z-order counting as resting for the reason WithState gives. One rule for the titled
    // list (Titled) and the tabs.
    static Brush TitleBrush(WindowRow row) =>
        row.IsActive ? TitleActive
        : row.IsFrontmostOnMonitor.HasValue && !row.IsFrontmostOnMonitor.Value ? TitleBehind
        : TitleResting;

    // A tab's Width to its title's visibility. No width yet (NaN, before the strip has sized it) shows the title.
    sealed class TitleShownAtWidth : IValueConverter
    {
        internal static readonly TitleShownAtWidth Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double width && !double.IsNaN(width) && !StripPlan.ShowsTitle(width) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException("A title's visibility does not set its tab's width.");
    }

    // The colour a workspace's row wears, as RebuildCore's WorkspaceRow works it out, and from the same method, so the
    // strips' name tag and underline cannot disagree with the row. The bright version, the one the titled list's spine
    // wears for the workspace you are on (ListSpine), because a tag carries white text on it.
    Brush? WorkspaceAccent(Overview overview, WorkspaceGroup workspace)
    {
        var group = workspace.Workspace.GroupId is { } id ? manager.State.Groups.FirstOrDefault(g => g.Id == id) : null;
        var (colour, slot, _) = RowColour(overview, workspace, group);
        return ListSpine(colour, slot);
    }

    // A workspace row's colour: the colour in force, the palette slot it falls back to, and the row's own position in
    // the list.
    //
    // The slot is the row's group's, or its own when ungrouped. The colour, for a member, comes from the GROUP (#90): a
    // group is one colour whichever row set it, so a member's own Workspace.Color is ignored while it is inside a box
    // and waiting for it if it ever leaves.
    //
    // ...in the member's own SHADE of it (WorkspacePalette.Shade), by its place in the group: "let them be different
    // shades of that color". The first member keeps the group's colour exactly, and the group's caption, bracket and
    // outline stay on that colour too, so the family still reads as one. Resolved to a hex here, so everything that
    // paints with it (tint, rail, the current name, a strip's tag) takes the shade without knowing about it. A group
    // set to "none" stays none.
    (string? Colour, int Slot, int OwnSlot) RowColour(Overview overview, WorkspaceGroup row, Core.Domain.Group? group)
    {
        var ownSlot = overview.Workspaces.ToList().FindIndex(w => w.Workspace.Id == row.Workspace.Id);
        var slot = group is not null ? manager.State.ColourSlotOf(group) : ownSlot;
        var memberStep = group is null ? 0 : Math.Max(0, manager.State.MembersOf(group.Id).ToList().FindIndex(m => m.Id == row.Workspace.Id));
        var colour = group is null ? row.Workspace.Color
            : WorkspacePalette.IsNone(group.Color) ? group.Color
            : WorkspacePalette.Shade(WorkspacePalette.For(group.Color, slot), memberStep);
        return (colour, slot, ownSlot);
    }

    // The display number of the screen with this rectangle, from the overview's list of screens (Overview.Screens),
    // which is the same sweep that numbered every row's Monitor. Every connected screen is in it, not only those
    // holding a window, which is what lets a drop on the strip of an empty screen land there. None for a screen the
    // sweep did not report (it left, or there is no screen layout). Internal so a test can show that a strip made from
    // the screens the strip set reconciles on (ScreenLayout.Screens) always finds its number here.
    internal static Maybe<int> ScreenNumberOf(Overview overview, MonitorBounds screen) =>
        (overview.Screens ?? new Dictionary<int, MonitorBounds>()).Where(p => p.Value == screen).Select(p => p.Key).TryFirst();

    // A window icon dropped on a strip (ScreenStrip.Dropped, which the strip set points here). It lands on that strip's
    // screen, in the workspace you are on: AssignWindow, the call the titled list's drop makes. On an unnamed desktop
    // there is no workspace to assign to, so it only moves to that screen, through the window menu's own move
    // (MoveWindowToMonitor), and its desktop is left alone. Read against the last overview drawn, which is what the
    // strips show.
    internal void DropOnStrip(ScreenStrip strip, WindowHandle window)
    {
        if (lastOverview is not { } overview) return;
        var number = ScreenNumberOf(overview, strip.Screen);
        var outcome = overview.Workspaces.FirstOrDefault(w => w.IsCurrent) is { } here
            ? manager.AssignWindow(window, here.Workspace.Id, number.HasValue ? number.Value : null)
            : number.HasValue
                ? manager.MoveWindowToMonitor(window, number.Value)
                : Result.Failure("That screen is no longer there.");
        if (ClickTrace.On)
            ClickTrace.Write($"strip drop hwnd={window.Value:X} screen={(number.HasValue ? number.Value.ToString() : "unknown")} " +
                             $"ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
        Report(outcome);
    }

    // Each strip reports its presses and releases to the bar, wired here the first time the bar fills it, so the strip
    // set has nothing to remember and a strip knows nothing of the bar. Once per strip, however many fills follow.
    void WireStrip(ScreenStrip strip)
    {
        if (!wiredStrips.TryAdd(strip, new object())) return;
        strip.PreviewMouseLeftButtonDown += (_, _) => NoteStripPress();
        strip.PreviewMouseLeftButtonUp += (_, _) => NoteStripRelease();
        // Any press on a tab puts its hover card away at once, as OnPreviewMouseDown does for the bar's icons.
        strip.PreviewMouseDown += (_, e) => DismissHoverCard(e.OriginalSource);
    }

    // A press on a strip: the press-state half of OnPreviewMouseLeftButtonDown, which has the reasons for each line.
    // Not the other half: a drag start or a resize from here would move or resize the bar from another window.
    internal void NoteStripPress()
    {
        if (ClickTrace.On) ClickTrace.Write($"strip press rebuilding={rebuilding} pending={rebuildRequested}");
        pressConsumedByChild = false;
        pressedRowKey = null;
        dragJustFinished = false;
        pressSeen = true;
    }

    // ...and its release: the press-state half of OnPreviewMouseLeftButtonUp, so a press on a tab makes its release a
    // matched one and the icon's orphan rescue stands down. The rebuild a press over the strip deferred is served here,
    // as the bar's release serves its own.
    internal void NoteStripRelease()
    {
        orphanRelease = !pressSeen;
        pressSeen = false;
        if (orphanRelease) pressConsumedByChild = false;
        if (ClickTrace.On) ClickTrace.Write($"strip up orphan={orphanRelease}");
        FlushDeferredRebuild();
    }

    // The card for a tab, which lives in a strip's window: below the strip, the side with room on a strip along a
    // screen's top edge, and moved right by the tab's place in the strip so it opens under the tab. False for an anchor
    // in the bar, which ShowHoverCard places as it always has. Also false for an anchor in no window at all (a rebuild
    // replaced it), which keeps that case exactly as it was.
    //
    // Against the STRIP'S WINDOW, not the tab, for the reason the bar's card is placed against the bar: a rebuild throws
    // every tab away, and a rebuild while the pointer rests on a tab is routine (a title or focus change). The new tab
    // under the pointer is the same window, so the hover watch keeps the card open and the dwell does not re-arm; a card
    // placed against the old tab would stay pointing at a detached element, which WPF goes on repositioning. The strip
    // outlives every rebuild. The offset is read when the card opens, as the bar reads its vertical one; a rebuild that
    // keeps the order keeps the tab where it was.
    bool PlaceHoverCardBelowStripTab(Popup popup, FrameworkElement anchor)
    {
        if (IsAncestorOf(anchor) || Window.GetWindow(anchor) is not { } host || ReferenceEquals(host, this)) return false;
        popup.PlacementTarget = host;
        popup.Placement = PlacementMode.Bottom;
        popup.HorizontalOffset = anchor.TranslatePoint(new Point(0, 0), host).X;
        popup.VerticalOffset = 0;
        return true;
    }

    // The hover card's popup, for the tests.
    internal Popup? HoverPopup => hoverPopup;

    // What the last MouseEnter armed the hover card with, for the tests: the dwell that shows it asks where the real
    // pointer is, which a test cannot place, so a test reads what was armed and shows the card from that.
    internal (string Label, WindowRow Row, FrameworkElement Anchor, bool Here)? Dwelling => dwelling;

    // The bar's background menu, opened at the pointer over a strip (ScreenStrip.MenuRequested, which the strip set
    // points here): a right-click on the name tag or the strip's free space. It holds the window list choice, so the way
    // back to the other two places is on the strip itself (spec "What a tab does"). One menu, refilled on every open as
    // the bar's own is, and held like every menu on this surface (HoldFadeWhileOpen), so a rebuild waits for it.
    ContextMenu? stripMenu;

    internal void OpenStripMenu()
    {
        if (stripMenu is null)
        {
            stripMenu = new ContextMenu { Placement = PlacementMode.MousePoint };
            HoldFadeWhileOpen(stripMenu);
        }
        FillBarMenu(stripMenu);
        stripMenu.IsOpen = true;
    }

    // Topmost is a shared band (CLAUDE.md): the strips join the bar's re-assertion, on foreground change and on the 1 s
    // timer. Its own method rather than part of ReclaimTopmost, which stands down while the bar is hidden: the strips
    // stay while the bar is minimized and must keep their place in the band then too.
    internal void ReclaimStripTopmost() => Strips().ToList().ForEach(strip => strip.ReclaimTopmost());

    // The strip set changed (a strip came or went; ScreenStrips.Changed): a new strip is empty until a rebuild hands it
    // its tabs, and a bar whose strips all went draws its list in its own place again. RebuildWanted, as every rebuild
    // trigger asks: a hidden bar rebuilds only while there are strips to fill, and ShowBar rebuilds before it shows.
    internal void RequestRebuild()
    {
        if (RebuildWanted) Rebuild();
    }

    // The end of a settled display change (SettleLayout), for the strip set, which reconciles on the same settle as
    // the bar so both act on the last arrangement of a burst and never on a transient one (spec "Constraints"). An
    // observable rather than a call into the strip set, so the bar knows nothing of who owns the strips.
    readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> layoutSettled = new();

    public IObservable<System.Reactive.Unit> LayoutSettled => layoutSettled;
}
