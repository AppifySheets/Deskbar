using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Control;

// The workspace row menu, creation, desktops and the bar. See RemoteControl.cs for the shared parts.
public sealed partial class RemoteControl
{
    ControlReply Workspaces() =>
        Success(manager.State.Workspaces.Select(w => Describe(w.Id)));

    // Named or not, so an agent can reach the unnamed desktops the bar lists separately. `number` is the
    // 1-based position in Windows' own order, which is what name-desktop also accepts.
    ControlReply Desktops() =>
        manager.LiveDesktops().Match(
            live => Success(live.Select((d, at) => new
            {
                number = at + 1,
                id = d.Id,
                windowsName = d.Name,
                workspace = manager.State.Workspaces.FirstOrDefault(w => w.DesktopId == d.Id)?.Name,
                current = manager.State.Workspaces.FirstOrDefault(w => w.DesktopId == d.Id) is { } w && manager.CurrentWorkspaceId == w.Id,
            })),
            ControlReply.Fail);

    // Idempotent on purpose. An agent that runs its setup twice, or a worktree whose name happens to
    // match a workspace Petre already has, both mean "make sure it exists", and failing them would only
    // teach the caller to ignore errors from this command. An existing workspace is left WHERE IT IS
    // whatever placement was asked for, and the reply's group and row say where that is.
    ControlReply Create(CreateRequest request) =>
        Ensure(request.Name, create: true, request.Placement).Match(
            found => Success(new { created = found.Created, workspace = Describe(found.Workspace.Id) }),
            ControlReply.Fail);

    Result<(Workspace Workspace, bool Created)> Ensure(string name, bool create, Placement placement) =>
        FindWorkspace(name) is { HasValue: true } existing
            ? (existing.Value, false)
            : create
                ? Add(name, placement).Map(w => (w, true))
                : Result.Failure<(Workspace, bool)>($"There is no workspace named '{name}'. Pass --create to make it.");

    // Each placement is the manager call the matching bar gesture makes: the bottom of a group is where
    // "Move into group" puts a joiner, and before/after is the row menu's "Insert before/after…", which
    // inserts at the target's list index and at the target's depth.
    Result<Workspace> Add(string name, Placement placement) => placement switch
    {
        Placement.InGroup inGroup => GroupNamed(inGroup.Group).Bind(group => manager.AddWorkspaceToGroup(name, group.Id)),
        Placement.AtRow atRow => manager.AddWorkspaceAtRow(name, atRow.Row),
        Placement.Beside beside => WorkspaceNamed(beside.Workspace).Bind(target => manager.InsertWorkspace(
            name,
            manager.State.Workspaces.ToList().FindIndex(w => w.Id == target.Id) + (beside.After ? 1 : 0),
            manager.State.GroupOf(target.Id)?.Id)),
        _ => manager.AddWorkspace(name),
    };

    ControlReply Rename(string workspace, string name) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.RenameWorkspace(w.Id, name), () => Describe(w.Id)),
            ControlReply.Fail);

    // The row menu's four moves, plus an exact position. Every one is among PEERS, as the menu's are
    // (#85): a grouped workspace moves inside its group's box, an ungrouped one among the bar's top-level
    // rows. A whole group moves with group-reorder (RemoteControl.Groups.cs). "end" passes the list's
    // length, the same number the menu passes, which the manager clamps to the last peer.
    ControlReply Reorder(string workspace, string where) =>
        WorkspaceNamed(workspace).Bind(w => (where.ToLowerInvariant() switch
            {
                "up" => manager.MoveWorkspace(w.Id, -1),
                "down" => manager.MoveWorkspace(w.Id, +1),
                "top" => manager.MoveWorkspaceTo(w.Id, 0),
                "end" or "bottom" => manager.MoveWorkspaceTo(w.Id, manager.State.Workspaces.Count - 1),
                _ => ControlArguments.Position(where)
                    .MapError(_ => $"reorder takes up, down, top, end or a position from 1, not '{where}'.")
                    .Bind(position => manager.MoveWorkspaceTo(w.Id, position - 1)),
            }).Map(() => w.Id))
            .Match(id => Success(Describe(id)), ControlReply.Fail);

    ControlReply SetRowFolded(string workspace, bool folded) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.SetWorkspaceFolded(w.Id, folded), () => Describe(w.Id)),
            ControlReply.Fail);

    // The row menu's "Pin to strip" / "Unpin from strip" (Workspace.Pinned). Named pin-workspace rather than pin,
    // because pin and unpin already pin a WINDOW to every desktop, which this does not do.
    // Into the workspace, or back out of it when you are in it (WorkspaceManager.Jump); replies with where it landed.
    ControlReply Jump(string workspace) =>
        WorkspaceNamed(workspace).Match(
            w => manager.Jump(w.Id).Match(landed => Success(Describe(landed)), ControlReply.Fail),
            ControlReply.Fail);

    ControlReply SetRowPinned(string workspace, bool pinned) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.SetWorkspacePinned(w.Id, pinned), () => Describe(w.Id)),
            ControlReply.Fail);

    // `tile` is refused only where this copy may not tile (isPro is TilingGate.Allowed, so never while tiling
    // is free). `untile` is never refused: releasing a workspace must always work, whatever the tier says.
    // There is no on/off switch to refuse on any more (TilingGate, 2026-11-19).
    ControlReply SetRowTiled(string workspace, bool tiled) =>
        WorkspaceNamed(workspace).Match(
            w => tiled && isPro?.Invoke() != true
                ? ControlReply.Fail("Tiling needs Deskbar Pro.")
                : Reply(manager.SetWorkspaceTiled(w.Id, tiled), () => Describe(w.Id)),
            ControlReply.Fail);

    // `even` and `reset-grid` are the two steps of the row menu's tidy item (FloatingBar.Tiling.cs, TidyItem). `even`
    // gives every stored split equal shares and keeps the shape the user made (WorkspaceManager.EvenOutTiles);
    // `reset-grid` forgets the stored layouts, so the screens go back to the automatic grid (ResetTileGrid). The menu
    // offers one step at a time; the pipe takes either whenever it is asked, as the menu's two items would do. Both are
    // refused like `tile` is, with the same message: they are tiling commands. On a workspace with nothing stored,
    // tiled or not, both succeed and change nothing.
    ControlReply EvenOutRow(string workspace) => TilingRow(workspace, manager.EvenOutTiles);

    ControlReply ResetGridRow(string workspace) => TilingRow(workspace, manager.ResetTileGrid);

    ControlReply TilingRow(string workspace, Func<Guid, Result> act) =>
        WorkspaceNamed(workspace).Match(
            w => isPro?.Invoke() != true
                ? ControlReply.Fail("Tiling needs Deskbar Pro.")
                : Reply(act(w.Id), () => Describe(w.Id)),
            ControlReply.Fail);

    // `keep-space <workspace> on|off` sets whether a closed tile's space is kept for the next window. A tiling
    // command, so refused exactly as `even` is, with the same message: only where this copy may not tile.
    ControlReply SetRowKeepsSpace(string workspace, bool keep) =>
        WorkspaceNamed(workspace).Match(
            w => isPro?.Invoke() != true
                ? ControlReply.Fail("Tiling needs Deskbar Pro.")
                : Reply(manager.SetTileKeepsSpace(w.Id, keep), () => Describe(w.Id)),
            ControlReply.Fail);

    // SetWorkspaceColor already hands a grouped workspace's colour to its group, as the menu does.
    ControlReply Colour(string workspace, string colour) =>
        WorkspaceNamed(workspace).Bind(w => ParseColour(colour).Bind(c => manager.SetWorkspaceColor(w.Id, c)).Map(() => w.Id))
            .Match(id => Success(Describe(id)), ControlReply.Fail);

    // The menu asks first and offers to close what is open; a command cannot ask, so closing has to be
    // SAID. Without --close-windows a workspace with windows is refused with the manager's own reason,
    // which is what an agent tidying up after itself should hit before it closes anybody's editor.
    ControlReply Delete(string workspace, bool closeWindows) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(closeWindows ? manager.DeleteWorkspaceClosingWindows(w.Id) : manager.DeleteWorkspaceIfEmpty(w.Id),
                () => new { deleted = w.Name }),
            ControlReply.Fail);

    ControlReply Switch(string workspace) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.Switch(w.Id), () => Describe(w.Id)),
            ControlReply.Fail);

    // By id, or by the number `desktops` prints. A desktop that already has a workspace is refused by the
    // manager; rename covers that one.
    ControlReply NameDesktop(string desktop, string name) =>
        manager.LiveDesktops()
            .Bind(live => live.Select((d, at) => (d, number: at + 1))
                .TryFirst(x => (Guid.TryParse(desktop, out var id) && id == x.d.Id) || x.number.ToString() == desktop.Trim())
                .ToResult($"There is no desktop '{desktop}'. `desktops` lists them with their numbers."))
            .Bind(x => manager.NameDesktop(x.d.Id, name))
            .Match(w => Success(new { created = true, workspace = Describe(w.Id) }), ControlReply.Fail);

    // Minimize bar is the app's business, not the manager's (the bar swaps for a stand-in), so the app
    // hands in how to do it. Null in tests and anywhere the bar does not exist.
    // `dock` and `undock` set the docking SETTING (Manage's checkbox), which the bar follows; they need no
    // window of their own, so they answer even where the bar cannot be minimized from here.
    ControlReply Bar(string state) => state.ToLowerInvariant() switch
    {
        "dock" => Reply(manager.SetDockBar(true), () => new { bar = "docked" }),
        "undock" => Reply(manager.SetDockBar(false), () => new { bar = "undocked" }),
        "across" => Reply(manager.SetDockAcrossScreens(true), () => new { bar = "docked across the screens" }),
        "edge" => Reply(manager.SetDockAcrossScreens(false), () => new { bar = "docked on one screen's edge" }),
        "minimize" or "restore" when setBarVisible is null => ControlReply.Fail("The bar cannot be controlled from here."),
        "minimize" => Reply(setBarVisible!(false), () => new { bar = "minimized" }),
        "restore" => Reply(setBarVisible!(true), () => new { bar = "restored" }),
        _ => ControlReply.Fail(ControlUsage.LineFor("bar")),
    };

    // The stats line's detach (its right-click menu) and the window's "Put stats back on the bar". The window is the
    // app's, so the app hands in how to do it, as for `bar minimize`; null in tests and in compatibility mode.
    ControlReply Stats(string state) => state.ToLowerInvariant() switch
    {
        "detach" or "attach" when setStatsDetached is null => ControlReply.Fail("The stats window cannot be controlled from here."),
        "detach" => Reply(setStatsDetached!(true), () => new { stats = "detached" }),
        "attach" => Reply(setStatsDetached!(false), () => new { stats = "on the bar" }),
        _ => ControlReply.Fail(ControlUsage.LineFor("stats")),
    };

    // The bar's arrange mode (drag rows to arrange the workspace list), the remote twin of the menu item that flips it.
    // The mode lives on the bar, so the app hands in how to turn it on or off, as for `stats`; null in tests and in
    // compatibility mode. The reply says which way it ended up, so a caller can check rather than assume.
    ControlReply Arrange(string state) => state.ToLowerInvariant() switch
    {
        "on" or "off" when setArranging is null => ControlReply.Fail("The bar is not running."),
        "on" => Reply(setArranging!(true), () => new { arranging = true }),
        "off" => Reply(setArranging!(false), () => new { arranging = false }),
        _ => ControlReply.Fail(ControlUsage.LineFor("arrange")),
    };

    // The split window lines setting (AppState.SplitWindowLines), the remote twin of the menu item that flips it.
    ControlReply WindowLines(string mode) => mode.ToLowerInvariant() switch
    {
        "split" => Reply(manager.SetSplitWindowLines(true), () => new { windowLines = "split" }),
        "whole" => Reply(manager.SetSplitWindowLines(false), () => new { windowLines = "whole" }),
        _ => ControlReply.Fail(ControlUsage.LineFor("window-lines")),
    };

    // Where the current workspace's titled window list goes (AppState.WindowListPlace), the remote twin of the
    // menu choice: `row` is the expanded row, `bottom` the bottom of the bar, `screens` a strip along the top of
    // every screen. Words are matched case-insensitively like `window-lines`; anything else is a usage error
    // that changes nothing.
    ControlReply WindowList(string place) => place.ToLowerInvariant() switch
    {
        "row" => Reply(manager.SetWindowListPlace(WindowListPlace.Row), () => new { windowList = "row" }),
        "bottom" => Reply(manager.SetWindowListPlace(WindowListPlace.Bottom), () => new { windowList = "bottom" }),
        "screens" => Reply(manager.SetWindowListPlace(WindowListPlace.Screens), () => new { windowList = "screens" }),
        _ => ControlReply.Fail(ControlUsage.LineFor("window-list")),
    };

    // The collapsed bar click setting (AppState.CollapsedBarSwitchesWhole), the remote twin of the menu item that
    // flips it: `bar` switches to the workspace wherever the collapsed bar is clicked, `icon` lets an icon bring
    // up its own window.
    ControlReply CollapsedClick(string mode) => mode.ToLowerInvariant() switch
    {
        "bar" => Reply(manager.SetCollapsedBarSwitchesWhole(true), () => new { collapsedClick = "bar" }),
        "icon" => Reply(manager.SetCollapsedBarSwitchesWhole(false), () => new { collapsedClick = "icon" }),
        _ => ControlReply.Fail(ControlUsage.LineFor("collapsed-click")),
    };

    // The full-screen setting (AppState.FloatingBarOverFullScreen), the remote twin of the menu item that flips
    // it: `step` lets a full-screen window on the bar's screen cover the floating bar, `top` keeps the bar on top.
    ControlReply FullScreen(string mode) => mode.ToLowerInvariant() switch
    {
        "step" => Reply(manager.SetFloatingBarOverFullScreen(false), () => new { fullScreen = "step" }),
        "top" => Reply(manager.SetFloatingBarOverFullScreen(true), () => new { fullScreen = "top" }),
        _ => ControlReply.Fail(ControlUsage.LineFor("full-screen")),
    };

    // The palette's names, a #RRGGBB of your own (what the Custom… picker stores), "none" for the
    // Transparent entry, or "default" for By position, which is the absence of a colour.
    static Result<string?> ParseColour(string raw) =>
        raw.Trim() is var colour && colour.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? Result.Success<string?>(null)
            : WorkspacePalette.IsNone(colour)
                ? Result.Success<string?>(WorkspacePalette.None)
                // Named also takes the retired muted names (teal, rust, steel...), as the vivid colour each
                // became, so scripts written against the old palette keep working (WorkspacePalette.Retired).
                : WorkspacePalette.Named(colour) is { } swatch
                    ? Result.Success<string?>(swatch.Hex)
                    : Regex.IsMatch(colour, "^#[0-9A-Fa-f]{6}$")
                        ? Result.Success<string?>(colour.ToUpperInvariant())
                        : Result.Failure<string?>(
                            $"'{raw}' is not a colour. Use {string.Join(", ", WorkspacePalette.Swatches.Select(s => s.Name.ToLowerInvariant()))}, #RRGGBB, none or default.");
}
