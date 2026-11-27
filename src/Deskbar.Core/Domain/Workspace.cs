using System.Text.Json.Serialization;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Domain;

// LEGACY: one screen's columns-of-rows tile sizes, as older builds stored them. Only migration reads it
// (JsonPersistenceStore turns each into a ScreenTileLayout on load).
public sealed record ScreenTileSizes(string Screen, TileSizes Sizes);

// One screen's stored split tree layout. Screen is an opaque key: the Tiler builds it from the screen's monitor
// bounds as "L,T,R,B", and this layer only stores and clears by it.
public sealed record ScreenTileLayout(string Screen, TileLayout Layout);

// A named group of windows, backed 1:1 by a Windows virtual desktop.
// DesktopId is the *live* desktop's GUID -- persisted so we can re-bind to the same
// desktop after an app restart, but desktops don't survive reboots, so reconcile
// logic (WorkspaceManager) may re-create the desktop and update this.
// Petre: "i also want different colors for different workspaces in the lanes", "configurable,
// along with shortcuts".
//
// Color is an optional "#RRGGBB" string, and Shortcut an optional chord like "Ctrl+Alt+1".
// Both are appended as OPTIONAL positional parameters so every existing construction site and
// every state.json written before this keeps working untouched.
//
// Why Shortcut lives on the workspace rather than staying implicit: the old Ctrl+Alt+1..9 bound
// by LIST POSITION, so reordering workspaces silently changed what each chord did. Naming the
// chord here makes the binding survive reordering, which is the whole point of being able to
// reorder. Those positional chords have since been removed and nothing reads Shortcut yet -- it
// is the groundwork for per-workspace direct jumps, done right, if they are wanted back.
public sealed record Workspace(Guid Id, string Name, Guid? DesktopId, string? Color = null, string? Shortcut = null)
{
    // FOLDED: the row is drawn as its name line alone, with no icons. Petre: "now i'm not working on
    // deskbar and i want to minimize it, to a single line, so i don't see any icons in it."
    //
    // This replaced "Minimize row" (#52, #111), which scaled the row to 0.43 of its height and kept every
    // icon, only smaller. That answered "this row matters less" and not "I do not want to see this row's
    // windows", and two ways of making a row smaller that disagree would be one too many, so the shrink
    // went and a minimized row from an older state.json loads folded.
    //
    // A property OF THE WORKSPACE rather than of the bar, which is what makes it survive a restart
    // without a second store to keep in step -- and it is a fact about the workspace anyway
    // ("I am not working on this right now"), not about the window that draws it. The bar decides
    // when a folded row still shows its icons (while it is the workspace you are on); this only
    // records the wish.
    //
    // Stored under its OLD name, "Minimized", so every state.json written before the rename keeps its
    // rows the way they were. Renaming the key would have unfolded every minimized row on the first
    // start of the new build for no reason anyone could see.
    //
    // An init property rather than another positional parameter: the list of those is already
    // five long and every one of them is optional, so a sixth would be one more thing to count
    // past at every construction site. Defaults to false, so a state.json without the key loads
    // with every row unfolded -- no migration, same pattern as everything in AppState.
    [JsonPropertyName("Minimized")]
    public bool Folded { get; init; }

    // Whether this workspace's windows are kept tiled in a grid (docs/superpowers/specs/2026-11-08-tiling-design.md).
    //
    // A property OF THE WORKSPACE for the reason Folded is: it survives a restart without a second store to
    // keep in step, and it is a fact about the workspace ("I work in a grid here"), not about the bar or any
    // window. Tiling acts only when this is on AND the copy may tile; see TilingGate.
    //
    // Init property defaulting to false, so a state.json written before tiling has no key and loads untiled,
    // with no migration.
    public bool Tiled { get; init; }

    // PINNED: this workspace gets an item on every screen strip, in the bar's header and on Windows' taskbar
    // that takes you into it, and, while you are in it, back to where you came from
    // (docs/superpowers/specs/2026-11-20-pinned-workspaces-design.md). Petre: "i'd have a messengers workspace
    // pinned and i'd go to it each time i need to do something there, and have a simple way to go back", "i
    // need to jump in and out of the messengers".
    //
    // Nothing about the workspace's WINDOWS changes: none is pinned to all desktops by this. What each item
    // reads and where it goes is WorkspaceJumps.Of.
    //
    // On the workspace for the reason Folded and Tiled are, and an init property defaulting to false for the
    // same reason: a state.json without the key loads with nothing pinned, with no migration.
    public bool Pinned { get; init; }

    // THE WORKSPACE'S FOLDER: where a Claude session for one of its tasks starts when the task has no session of its
    // own yet (ClaudeStart.NewTaskFolders lists it ahead of the folders guessed from earlier sessions and open VS Code
    // windows). Task 6edcc65a, Petre: "New workspace: option to set its folder path; default to a single repos folder
    // and offer a subfolder in it". The New workspace dialog asks for it (NewWorkspaceDialog); null is "no folder",
    // which is every workspace made before this and behaves exactly as before.
    //
    // A full path as the user chose it, not checked against the disk on load: a folder on a drive that is not plugged
    // in comes back once it is, and the session start reports a folder that is really gone.
    //
    // On the workspace for the reason Folded and Pinned are, and an init property defaulting to null for the same
    // reason: a state.json without the key loads with no folder, with no migration.
    public string? Folder { get; init; }

    // The bookmarks in this workspace's row: links, programs and Store apps one click opens
    // (docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md). A fact about the workspace, so it sits
    // here for the reason Folded and Pinned do and survives a restart with no second store.
    //
    // A ValueList, not an IReadOnlyList, for the reason TileSizes is one: the record compares by contents. Init
    // property defaulting to empty, so a state.json from before bookmarks loads with none, with no migration. A
    // bookmark that cannot be read is dropped at load (BookmarkConverter), never the whole file.
    public ValueList<Bookmark> Bookmarks { get; init; } = [];

    // LEGACY: the columns-of-rows sizes older builds stored here, one entry per screen. Only migration reads it:
    // Load turns each entry into a TileLayouts entry (JsonPersistenceStore.MigrateTileSizes) and empties this, so a
    // saved state always writes it as []. Kept as a property, rather than dropped, so a state.json from an older
    // build still has somewhere to land.
    //
    // A ValueList, not a plain list: a record compares a list member by reference, so two workspaces holding
    // the same entries would be unequal. ValueList compares by contents, which keeps the record's generated
    // equality right for every property, this one and any added later.
    public ValueList<ScreenTileSizes> TileSizes { get; init; } = [];

    // This workspace's tiling layouts as split trees (TileLayout), one entry per screen: the shape a drop made and
    // the splits the user dragged. Petre: "i want to be able to resize tiles by dragging the separation" and "keep my
    // sizes".
    //
    // On the workspace for the reason Tiled is: it survives a restart with no second store to keep in step, and it is
    // a fact about this workspace's layout. A screen that drops out keeps its entry, so plugging it back in restores
    // the layout. A plain layout (even, the automatic shape, no opening) is never stored, because no entry means
    // exactly that. "Even out tiles" evens every entry and keeps its shape (dropping one that is then plain); only
    // "Reset to grid", untiling and deleting the workspace forget them.
    //
    // A NEW property rather than a tree handed to ScreenTileSizes.Sizes: an older build reading a tree there would
    // crash on load, whereas it just ignores an unknown key (design, "Storing and migration"). Init property
    // defaulting to empty, so a state.json without the key loads with nothing stored; a null list loads as empty
    // too (ValueListConverter).
    public ValueList<ScreenTileLayout> TileLayouts { get; init; } = [];

    // Whether a tiled window that closes leaves its space empty for the next window to open into, instead of
    // the grid closing up around the gap. Only the Tiler acts on it, and only while the workspace is tiled.
    //
    // Init-only and false by default: a state.json written before this has no key and loads with it off, with
    // no migration, which is how tiling behaved before the switch existed. Untiling deliberately leaves it
    // alone, so a user who tiles again finds their choice as they left it (unlike TileLayouts, it is a
    // preference, not a layout).
    public bool TileKeepsSpace { get; init; }

    // The group this workspace belongs to, or null for one that stands on its own.
    //
    // App-level metadata over flat OS desktops, which is the same move this app already makes by
    // NAMING them: Windows has no notion of a desktop belonging to a set, and inventing one here
    // costs nothing at the OS level because nothing at the OS level is asked to understand it.
    //
    // ONE LEVEL ONLY: a group holds workspaces, never other groups. Not a limitation of the model
    // but of what can be read at a glance on a bar ten rows tall. Deep nesting would need
    // collapsing, and collapsing needs a whole interaction nobody has asked for.
    //
    // This replaced ParentId (#42's original shape), where a nested workspace pointed straight at
    // its parent workspace. That could not express #84's groups, which have a name and no parent
    // workspace at all, so membership became a group id. AppState.Migrated converts older files, so
    // nothing has to be re-grouped by hand.
    public Guid? GroupId { get; init; }

    // Read by the migration only, and never written with a value. A state.json from before groups
    // records nesting as ParentId on the child, and this is what lets that file still be understood.
    //
    // AppState.Migrated turns it into groups and then CLEARS it: nesting no longer exists, so there is
    // no downgrade to keep it for, and a stale value would rebuild groups the user had since ungrouped
    // (the ParentId step runs whenever Groups is empty).
    //
    // Deliberately not used anywhere else: everything reads GroupId. Deleting this property would
    // silently drop every nesting in a file that has not been loaded since groups arrived, which is
    // data loss that looks like a rendering bug.
    public Guid? ParentId { get; init; }

    // Fields of this workspace in state.json that this build does not know, carried through a save unchanged, as
    // AppState.Extra does for the top level. Without it an older build drops anything a newer one added INSIDE a
    // workspace on its first save: that is how 2.6.48 lost every bookmark on 2026-11-27 (Deskbar task 0bd17709),
    // since Bookmarks is a field of the workspace and 2.6.48 had never heard of it. This bag only protects fields
    // added from now on; builds older than it still drop them, which is what state.json.from-<version>
    // (JsonPersistenceStore.KeepCopyIfNewer) is for.
    //
    // Every edit goes through `with`, which copies this reference, so an edited workspace keeps what it carried. A new
    // workspace has none. Null when the file had nothing unknown, the normal case, which leaves equality as it was.
    // A mutable dictionary only because System.Text.Json fills extension data into IDictionary; nothing writes it.
    [JsonExtensionData]
    public IDictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}
