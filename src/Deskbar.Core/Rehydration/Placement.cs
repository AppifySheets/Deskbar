using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Rehydration;

// Where a window was last deliberately put. Petre: "last placement in the workspace should
// be where it's placed when started" / "last placement IS the rule".
public enum PlacementKind
{
    Workspace, // in workspace WorkspaceId
    Pinned,    // on ALL workspaces (Windows' own pin)
    Detached,  // deliberately dragged onto a plain desktop -- no workspace owns it
}

public sealed record Placement(PlacementKind Kind, Guid WorkspaceId)
{
    // Pinned/Detached carry no workspace, hence Guid.Empty -- used here purely as "not
    // applicable", never as a lookup key (that overload of Guid.Empty already means the
    // "Unplaced" catch-all over in OverviewBuilder, which is a different notion entirely).
    public static Placement Pinned { get; } = new(PlacementKind.Pinned, Guid.Empty);
    public static Placement Detached { get; } = new(PlacementKind.Detached, Guid.Empty);
    public static Placement In(Guid workspaceId) => new(PlacementKind.Workspace, workspaceId);
}

// Reads the persisted placement of a window BY IDENTITY (exe path + args) rather than by
// window handle. That distinction is the whole point:
//
// Petre's defect -- "move Beeper to pinned, close the app, open again, it is incorrectly
// placed in GEPHA workspace" -- happened because Windows' pin is keyed to the HWND, and
// Beeper is an Electron app that closes to tray, destroying and recreating its window. The
// pin died with the old handle and the fresh window appeared on whatever desktop was
// current. Nothing in the app recorded the pin, because AppState had nowhere to put it.
//
// Pure by design (no COM, no OS calls): every fact arrives as AppState, so all the
// precedence rules below are unit-testable. Same shape as OverviewBuilder.
public static class PlacementMemory
{
    // Precedence within memory: an identity is in at most ONE of these three states, so
    // order only matters for a hand-edited state.json. Pinned is checked first because it
    // is the least destructive to re-apply (a pin makes a window appear everywhere; it
    // does not move it).
    //
    // THE WORKSPACE HALF IS OPT-IN (AppState.RememberedApps), and the pin and detach halves are not.
    //
    // Petre, 2026-11-22: "mstsc opened in another workspace" / "should in the same". The trace showed
    // the Remote Desktop dialog appear on the workspace he was on and land on Meta 35 ms later, because
    // an argument-less mstsc had been dragged to Meta once. It was the fourth report of exactly this
    // shape: the Run box, Snipping Tool and Notepad came first, and each was fixed by adding one more
    // name to RosterIdentity.StayWhereOpened. A drag says where ONE window goes now; it says nothing
    // about where the app belongs the next time it is opened, and for most apps nothing does. His
    // ruling: "placement memory has to always consider the context that the window is in. i can't
    // think of anything other than code windows moving to their corresponding workspaces", and "let's
    // keep memory, but only for specific apps".
    //
    // So a workspace is remembered only for an app he has opted in by name. The context tiers above
    // memory in WorkspaceManager are untouched: the container tier (a VS Code window goes where its
    // folder lives) and launched-by (a window opened from an app goes to that app's workspace). So are
    // workspace rules, which he writes himself.
    //
    // Pins and detaches stay automatic because neither can send a window AWAY from where he is: a pin
    // puts it on every workspace including this one, and detaching only marks it. Beeper's re-pin after
    // closing to tray (the defect this class was written for) keeps working without an opt-in.
    //
    // `processName` without the extension, the way WindowInfo.ProcessName has it.
    public static Maybe<Placement> For(string identity, string processName, AppState state) =>
        Claims(state.PinnedApps, identity) ? Maybe<Placement>.From(Placement.Pinned)
        : Claims(state.DetachedApps, identity) ? Maybe<Placement>.From(Placement.Detached)
        : RemembersWorkspace(processName, state) ? WorkspaceClaiming(identity, state).Map(Placement.In)
        : Maybe<Placement>.None;

    // A window with no readable process path (elevated) has no identity, so nothing can be
    // remembered about it.
    public static Maybe<Placement> For(WindowInfo window, AppState state) =>
        RosterIdentity.Of(window).Bind(identity => For(identity, window.ProcessName, state));

    // Whether this app was opted in to workspace memory. Case-insensitive, like every other
    // process-name list in AppState.
    public static bool RemembersWorkspace(string processName, AppState state) =>
        state.RememberedApps.Any(name => name.Equals(processName, StringComparison.OrdinalIgnoreCase));

    // The roster IS the workspace half of placement memory: Place() -> RosterAdd() ->
    // AddEntry() already records identity -> workspace on every placement, and AddEntry
    // strips the identity from every OTHER workspace first, so exactly one workspace can
    // claim it. The Count == 1 guard is therefore defensive (a hand-edited state.json):
    // ambiguity is skipped rather than guessed, so a wrong guess can never move a window
    // somewhere Petre never put it.
    static Maybe<Guid> WorkspaceClaiming(string identity, AppState state) =>
        state.Inventory.Where(kv => Claims(kv.Value, identity)).Select(kv => kv.Key).ToList() is { Count: 1 } single
            ? Maybe<Guid>.From(single[0])
            : Maybe<Guid>.None;

    static bool Claims(IReadOnlyList<InventoryEntry> apps, string identity) =>
        apps.Any(entry => RosterIdentity.Of(entry) == identity);
}
