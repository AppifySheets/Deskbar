using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.App;

// The row menu's tiling item (docs/superpowers/specs/2026-11-08-tiling-design.md). Its own file so the long
// WorkspaceMenu gains one call, after the Fold item, rather than another screenful of branches.
//
// The Tiler (Deskbar.Core.Tiling) does the tiling itself and listens to the manager on its own, so all this
// item does is set Workspace.Tiled through WorkspaceManager.SetWorkspaceTiled; the state pulse that follows
// reaches the Tiler and the bar alike.
public partial class FloatingBar
{
    // The Store page the free tier is sent to (Deskbar Pro's Store ID, 9PJX26NGFPNF). The web page rather
    // than UpdateService.StoreLink's ms-windows-store: link, because this is the copy that did NOT come from
    // the Store, and a machine without the Store app (LTSC, some managed installs) still has a browser.
    const string ProStorePage = "https://apps.microsoft.com/detail/9PJX26NGFPNF";

    // Whether this copy may tile: Deskbar Pro, or any copy while tiling is free (TilingGate.Free, for now, which
    // makes every branch below take the Pro side). Entitlement's cached verdict in the app; a seam for the tests,
    // which cannot fake a Store licence, and nothing else.
    internal Func<bool> IsPro { get; init; } = () => Core.Tiling.TilingGate.Allowed(Entitlement.Current.IsPro);

    // Always offered: there is no on/off switch for tiling any more (Manage's "Tiling (experimental)" box went
    // on 2026-11-19, see TilingGate).
    //
    // Pro is read when the menu is FILLED, the first time it opens (FloatingBar.LazyMenus). It can change
    // between a fill and a click only when a trial ends at that very moment, and the click then does what the
    // menu said, which the Tiler's own gate (TilingGate) still refuses to act on.
    //
    //   Pro         a checkable "Tile windows", ticked when Workspace.Tiled is set. Click flips the flag.
    //   free tier   "Tile windows (Deskbar Pro)", which explains where tiling comes from and offers the Store.
    //               It is never a checkbox: a tick that does nothing is worse than words that say why.
    //   free tier,  the same, plus "Stop tiling". A workspace tiled while Pro was on keeps its flag when Pro
    //   tiled      lapses (a trial ending). The Tiler already stops acting, so nothing moves either way, but
    //               the flag would come back to life on a later purchase; this clears it.
    void AddTilingItems(ContextMenu menu, Guid workspaceId, Action<string, string, Action> add)
    {
        // Read at CLICK time for the flip, as WorkspaceMenu's IndexOf is, so a rebuild between the fill and the
        // click can never invert the wrong value. False for a workspace that has gone, which SetWorkspaceTiled
        // then reports as missing.
        bool TiledNow() => manager.State.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.Tiled == true;

        if (IsPro())
        {
            var tile = new MenuItem
            {
                Header = "Tile windows",
                IsCheckable = true,
                IsChecked = TiledNow(),
                Icon = MenuGlyph("⊞"),
            };
            // Click rather than Checked/Unchecked, for the reason the "by folder" item gives: those fire while
            // the menu is being BUILT, when IsChecked is assigned above.
            tile.Click += (_, _) => Report(manager.SetWorkspaceTiled(workspaceId, !TiledNow()));
            menu.Items.Add(tile);

            // The tidy item, "Even out tiles" or "Reset to grid", directly under "Tile windows" and only while the
            // workspace is tiled (TidyItem says which, and when there is neither).
            if (TiledNow() && manager.State.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is { } stored)
                TidyItem(stored).Tap(item => menu.Items.Add(item));

            // The keep-space switch, last of the tiling items and only while the workspace is tiled (it means
            // nothing otherwise). Petre: a closed window's space can stay open for the next one. Ticked from
            // Workspace.TileKeepsSpace as the menu is filled; the flip is read at CLICK time, and Click is used
            // rather than Checked/Unchecked, for the reasons given above for "Tile windows".
            if (TiledNow())
                menu.Items.Add(KeepSpaceItem(workspaceId));
            return;
        }

        add("⊞", "Tile windows (Deskbar Pro)", OfferPro);
        if (TiledNow())
            add("⊟", "Stop tiling", () => Report(manager.SetWorkspaceTiled(workspaceId, false)));
    }

    // --- the header's tiling button ---------------------------------------------------------------------------
    //
    // Petre, testing tiling live: "maybe add a button at the top of the bar", then from a mockup, "Header, beside
    // Search": one button for the workspace you are ON, beside Start and Search, that tiles it or stops tiling it.
    //
    //   * Shown on every named workspace (an unnamed desktop has no Tiled flag to flip). Since 2026-11-19 it is
    //     THE way to turn tiling on: Manage's "Tiling (experimental)" box is gone (Petre: "enable tiling in the
    //     settings and remove it as an option; it's enough to have a button at the top").
    //   * Lit while that workspace is tiled: the hover square kept, as Start and Search show while their flyout
    //     is open, so "on" reads the same everywhere in the header. The glyph fills in too.
    //   * The glyph is a layout mark, one tall pane beside two stacked ones. Not a 2x2 grid: Start is Windows' four
    //     tiles in blue, right beside it, and a second grid would read as a second Start button.
    //   * The free tier gets the row menu's answer: an untiled workspace is offered Pro, and a workspace left
    //     tiled after Pro lapsed is untiled, which is the one thing still worth doing there.
    //
    // Two buttons, the header's and the seam twin's (FloatingBar.Shell.cs, BuildShellStrip), kept identical.

    readonly List<Button> tileButtons = [];

    // The buttons, for the tests.
    internal IReadOnlyList<Button> TileButtons => tileButtons;

    // The workspace the buttons act on: the one you are on, as of the last rebuild. Null on an unnamed desktop.
    Guid? tileTarget;

    // What the buttons last showed, so a rebuild that changes nothing about them touches nothing.
    (bool Shown, bool Tiled, string Name)? tileShown;

    const string TilePanes = "M1.5,2 L7,2 L7,14 L1.5,14 Z M9,2 L14.5,2 L14.5,7.25 L9,7.25 Z M9,8.75 L14.5,8.75 L14.5,14 L9,14 Z";

    Button TileButton()
    {
        var button = BareShellButton("Tile windows", TileGlyph(tiled: false));
        button.Visibility = Visibility.Collapsed;
        button.Click += (_, _) => OnTileButton();
        // Worked out again as it opens, as Search's tip is: Pro can change (a licence refresh, a trial ending)
        // without anything pulsing the state, so a tip painted at the last rebuild could name the wrong tier.
        button.ToolTipOpening += (_, _) => button.ToolTip = TileTip();
        // Right-click: the row menu's tidy item ("Even out tiles" or "Reset to grid") and the keep-space switch, for
        // the workspace you are on. Filled as it opens, from the state as it is then; with nothing to offer no menu
        // opens at all, rather than an empty one.
        button.ContextMenu = new ContextMenu();
        button.ContextMenuOpening += (_, e) =>
        {
            FillTileMenu(button.ContextMenu);
            if (button.ContextMenu.Items.Count == 0) e.Handled = true;
        };
        tileButtons.Add(button);
        return button;
    }

    // The checkable "Keep the space when a window closes", shared by the row menu and the tile button's menu.
    // IsChecked is read from the state when the item is built; the flip is read at CLICK time (so a rebuild between
    // the fill and the click cannot invert the wrong value), and Click is used, not Checked/Unchecked, which fire
    // while the menu is being built when IsChecked is assigned.
    MenuItem KeepSpaceItem(Guid workspaceId)
    {
        bool KeepsNow() => manager.State.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.TileKeepsSpace == true;
        var keep = new MenuItem { Header = "Keep the space when a window closes", IsCheckable = true, IsChecked = KeepsNow() };
        keep.Click += (_, _) => Report(manager.SetTileKeepsSpace(workspaceId, !KeepsNow()));
        return keep;
    }

    // THE TIDY ITEM, shared by the row menu and the tile button's menu: one item, in two steps (spec
    // docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md, "Even out tiles"). Petre picked "Even, then
    // reset" over "even only" and over the item as it was, which forgot every stored size in one click:
    //
    //   a split is uneven       "Even out tiles": every split equal, the shape you made kept (EvenOutTiles). The
    //                           Tiler re-tiles by itself when the stored layouts change, so the click only stores.
    //   all even, a hand-made   "Reset to grid": the stored layouts forgotten, so the screens go back to the
    //   shape still stored      automatic grid (ResetTileGrid). Since Even out keeps the shape, this is now the one
    //                           way back to the grid short of untiling.
    //   nothing stored          nothing: the screens already are the automatic grid, evenly split.
    //
    // TileArrange.TidyStep decides, from the workspace's stored layouts (Workspace.TileLayouts) as they are when the
    // menu is filled, and the click does what the label said. Same glyph for both: it is one item that changes its
    // words. An even automatic layout is never stored (TileArrange.IsPlain), so "all even and something stored" really
    // does mean a shape the user made.
    Maybe<MenuItem> TidyItem(Workspace workspace) =>
        TileArrange.TidyStep(workspace.TileLayouts.Select(l => l.Layout)) switch
        {
            TileArrange.TileTidy.EvenOut => TidyMenuItem("Even out tiles", () => manager.EvenOutTiles(workspace.Id)),
            TileArrange.TileTidy.ResetGrid => TidyMenuItem("Reset to grid", () => manager.ResetTileGrid(workspace.Id)),
            _ => Maybe<MenuItem>.None,
        };

    MenuItem TidyMenuItem(string header, Func<Result> act)
    {
        var item = new MenuItem { Header = header, Icon = MenuGlyph("⊟") };
        item.Click += (_, _) => Report(act());
        return item;
    }

    // The button's right-click menu, on the same terms as the row menu: Pro and the workspace tiled. The tidy item
    // first when there is one (TidyItem), then the keep-space switch always, so a tiled workspace never opens an
    // empty menu.
    internal void FillTileMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (tileTarget is not { } id || !IsPro()) return;
        var current = manager.State.Workspaces.FirstOrDefault(w => w.Id == id);
        if (current is not { Tiled: true }) return;
        TidyItem(current).Tap(item => menu.Items.Add(item));
        menu.Items.Add(KeepSpaceItem(id));
    }

    // What the button would do now, in words. Read from the state and Pro as they are this moment.
    internal string TileTip()
    {
        var current = manager.State.Workspaces.FirstOrDefault(w => w.Id == tileTarget);
        return current is null ? "Tile windows"
            : current.Tiled ? $"Stop tiling {current.Name}"
            : IsPro() ? $"Tile windows on {current.Name}"
            : "Tile windows (Deskbar Pro)";
    }

    // Outlined while the workspace is not tiled, filled while it is.
    static UIElement TileGlyph(bool tiled) => tiled
        ? new Viewbox
        {
            Width = HeaderGlyphSize,
            Height = HeaderGlyphSize,
            IsHitTestVisible = false,
            Child = new Canvas { Width = 16, Height = 16, Children = { new System.Windows.Shapes.Path { Data = Geometry.Parse(TilePanes), Fill = ShellGlyphBrush } } },
        }
        : Glyph(TilePanes, size: HeaderGlyphSize);

    // Called by every rebuild with the workspace you are on (Rebuild, beside liveListTarget), so the buttons follow
    // a switch, the row menu and `deskbar.cmd tile` alike: all of them pulse the state.
    void UpdateTileButtons(Workspace? current)
    {
        tileTarget = current?.Id;
        var now = (Shown: current is not null, Tiled: current?.Tiled == true, Name: current?.Name ?? "");
        if (tileShown == now) return;
        tileShown = now;

        var tip = TileTip();
        tileButtons.ForEach(button =>
        {
            button.Visibility = now.Shown ? Visibility.Visible : Visibility.Collapsed;
            button.Content = TileGlyph(now.Tiled);
            button.Background = now.Tiled ? ShellOpenBrush : Brushes.Transparent;
            button.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
        });
    }

    // The flag is read at CLICK time, as the row menu's item reads it, so a rebuild between the last paint and the
    // click can never flip it the wrong way.
    void OnTileButton()
    {
        // Same as Start and Search: this click is the press's whole meaning, so the header row it sits in must not
        // treat the release as a click of its own.
        MarkPressConsumed();
        if (tileTarget is not { } id) return;
        var tiled = manager.State.Workspaces.FirstOrDefault(w => w.Id == id)?.Tiled == true;
        if (IsPro() || tiled)
            Report(manager.SetWorkspaceTiled(id, !tiled));
        else
            OfferPro();
    }

    // The free tier's answer. Owned by the bar for the reason Report gives: the bar is Topmost, so an ownerless
    // box can open behind it and strand the user with an invisible modal. Yes opens the Store page in the browser through App.OpenUrl, the one way this app opens a
    // link (it reports a failure instead of swallowing it); No just closes.
    void OfferPro()
    {
        if (MessageBox.Show(this, "Tiling comes with Deskbar Pro, from the Microsoft Store.", "Deskbar",
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) == MessageBoxResult.Yes)
            App.OpenUrl(ProStorePage);
    }
}
