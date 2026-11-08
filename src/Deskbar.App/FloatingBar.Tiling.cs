using System.Windows;
using System.Windows.Controls;

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

    // Whether this copy is Deskbar Pro. Entitlement's cached verdict in the app; a seam for the tests, which
    // cannot fake a Store licence, and nothing else.
    internal Func<bool> IsPro { get; init; } = () => Entitlement.Current.IsPro;

    // Nothing at all while the experiment is off (AppState.ExperimentalTiling, Manage's "Tiling
    // (experimental)" box). Petre: "disabled by default, until i have tested it well". That includes a
    // workspace still marked tiled from before the box was unticked: the flag is kept for when it is ticked
    // again, and the menu says nothing about it meanwhile.
    //
    // Pro and the experiment are read when the menu is FILLED, the first time it opens (FloatingBar.LazyMenus).
    // Rows are rebuilt on every state pulse, so the experiment cannot go stale in a reachable way; Pro can
    // change between a fill and a click only when a trial ends at that very moment, and the click then does
    // what the menu said, which the Tiler's own gate (TilingGate) still refuses to act on.
    //
    //   Pro         a checkable "Tile windows", ticked when Workspace.Tiled is set. Click flips the flag.
    //   free tier   "Tile windows (Deskbar Pro)", which explains where tiling comes from and offers the Store.
    //               It is never a checkbox: a tick that does nothing is worse than words that say why.
    //   free tier,  the same, plus "Stop tiling". A workspace tiled while Pro was on keeps its flag when Pro
    //   tiled      lapses (a trial ending). The Tiler already stops acting, so nothing moves either way, but
    //               the flag would come back to life on a later purchase; this clears it.
    void AddTilingItems(ContextMenu menu, Guid workspaceId, Action<string, string, Action> add)
    {
        if (!manager.State.ExperimentalTiling) return;

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
            return;
        }

        add("⊞", "Tile windows (Deskbar Pro)", OfferPro);
        if (TiledNow())
            add("⊟", "Stop tiling", () => Report(manager.SetWorkspaceTiled(workspaceId, false)));
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
