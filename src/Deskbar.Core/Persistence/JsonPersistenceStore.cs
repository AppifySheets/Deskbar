using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Persistence;

// `trace` hears about a fallback to the previous copy (DurableFile), which is otherwise silent by design: the
// workspaces come back, and the line in the trace is how anyone learns the latest file was lost.
//
// `runningVersion` is this build's version. Save stamps it into state.json as WrittenBy and Load refuses a file
// stamped by a newer major version (StateVersionGate). Null (the tests, mostly) stamps nothing and refuses
// nothing. Top-level fields this build does not know ride through Load and Save in AppState.Extra.
public sealed class JsonPersistenceStore(string baseDirectory, Action<string>? trace = null, string? runningVersion = null) : IPersistenceStore
{
    // Finding 5 (reviewer, Important): without a string converter, RuleMatchKind (and any
    // future enum) serializes as a bare int -- unreadable in state.json and a silent
    // renumbering hazard if the enum's member order ever changes. JsonStringEnumConverter
    // still DESERIALIZES bare ints too (back-compat with any state.json written before this
    // fix), it just never WRITES them again.
    //
    // ScreenTileLayoutConverter reads each TileLayouts entry on its own, so one tree that cannot be read is dropped
    // instead of failing the whole file (see the converter).
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new ScreenTileLayoutConverter(), new BookmarkConverter() },
    };

    string StatePath => Path.Combine(baseDirectory, "state.json");

    // Missing file is the normal first-run case -> Empty. An unreadable state.json falls back to the previous
    // copy (DurableFile.Read); only when that cannot be read either is it a real failure the caller must
    // surface (we refuse to silently overwrite user data).
    public Result<AppState> Load() =>
        DurableFile.Read(StatePath,
            text => MigrateTileSizes(JsonSerializer.Deserialize<AppState>(text, Options) ?? throw new JsonException("state.json deserialized to null")),
            AppState.Empty, trace)
        // The gate runs AFTER the read, never inside the parse: a throw there would look like a corrupt file and
        // make DurableFile move the newer file aside and fall back to .previous, which is the opposite of
        // protecting it.
        .Bind(state => runningVersion is null
            ? Result.Success(state)
            : StateVersionGate.Check(state.WrittenBy, runningVersion).Map(() => state));

    // Tiling moved from columns-of-rows sizes to a split tree. A workspace written by an older build has TileSizes
    // and no TileLayouts: each entry becomes the tree TileLayout.FromGrid builds, and TileSizes is emptied so the
    // next save writes none (an older build handed a tree in TileSizes would crash, which is why the trees have a
    // property of their own). A workspace that already has layouts keeps them: they are newer than any sizes.
    //
    // A hand-trimmed or very old file can lack the Workspaces key, which deserializes to null despite the type, so
    // that is passed through untouched rather than crashing the load.
    //
    // NOTHING HERE MAY THROW. This runs inside DurableFile.Read's parse, and a throw there reads as a corrupt file:
    // the load falls back to state.json.previous and every change since that copy is lost, for the sake of one tiling
    // entry. So an entry that cannot be a layout is DROPPED instead (that screen goes back to the automatic grid,
    // which is all it costs): a null entry, null sizes, and sizes that are not a grid (IsGrid). A null TileSizes or
    // TileLayouts list already loads as empty (ValueListConverter); the `?? []` below only keeps this method safe on
    // its own for a workspace built in code.
    //
    // Tree entries get the same treatment (whole-branch review, item 4). One whose JSON is no tree (a missing or
    // unknown "kind") arrives here as null, from ScreenTileLayoutConverter, and is dropped with the null entries. One
    // that parses but is no layout (Loadable says what that is) is dropped too. Every tree kept goes through
    // TileLayout.Normal, so a hand edit that nests a split in one of its own axis, or leaves a split with one child,
    // reaches the Tiler in the normal form every edit assumes.
    static AppState MigrateTileSizes(AppState state) =>
        state.Workspaces is null ? state : state with
        {
            Workspaces = state.Workspaces.Select(MigrateTileSizes).ToList(),
        };

    static Workspace MigrateTileSizes(Workspace w)
    {
        var sizes = w.TileSizes ?? [];
        var layouts = (w.TileLayouts ?? [])
            .Where(e => e?.Layout is not null && Loadable(e.Layout.Root))
            .Select(e => e with { Layout = TileLayout.Normal(e.Layout.Root, e.Layout.Opening) })
            .ToValueList();
        return w with
        {
            TileLayouts = layouts.Count > 0
                ? layouts
                : sizes.Where(s => s is not null && IsGrid(s.Sizes))
                    .Select(s => new ScreenTileLayout(s.Screen, TileLayout.FromGrid(s.Sizes)))
                    .ToValueList(),
            TileSizes = [],
            // A bookmark BookmarkConverter could not read arrives as null; drop it (the rest of the list stays).
            // Cannot throw: `?? []` covers a workspace built in code with a null list. A second bookmark carrying an id
            // already seen in the same workspace is dropped too (a hand-edited or copy-pasted file): rename and remove
            // address a bookmark by id, so two with one id could not be told apart. The first one wins, order is kept.
            Bookmarks = (w.Bookmarks ?? []).Where(b => b is not null)
                .GroupBy(b => b.Id).Select(g => g.First()).ToValueList(),
        };
    }

    // Sizes TileLayout.FromGrid can read: at least one column, one non-null, non-empty row list per column. Anything
    // else (rows shorter or longer than the columns, a null list, no columns, a column with no rows) is a hand edit or
    // a bug, not a layout anyone made. An empty shape would otherwise load as a lone leaf (Normal turns a tree with no
    // leaf into one), a layout for one window that the screen never had (whole-branch review, item 3).
    static bool IsGrid(TileSizes? sizes) =>
        sizes is { Columns: not null, Rows: not null }
        && sizes.Columns.Count > 0
        && sizes.Rows.Count == sizes.Columns.Count
        && sizes.Rows.All(row => row is { Count: > 0 });

    // A tree as read from the file is a layout: every node present, every split with at least one child, and each
    // split's shares above 0 and summing to 1. A missing piece (a null child or node, a split with no children list)
    // is possible because System.Text.Json leaves a missing property at its default. NaN cannot pass (every
    // comparison with it is false), nor can a share of 0, which would lay out a tile of no size.
    const double ShareTolerance = 1e-6;

    static bool Loadable(TileNode? node) =>
        node switch
        {
            TileLeaf => true,
            TileBranch { Children: { Count: > 0 } children } b when Enum.IsDefined(b.Axis) =>
                children.All(c => c is not null && c.Share > 0 && Loadable(c.Node))
                && Math.Abs(children.Sum(c => c.Share) - 1) < ShareTolerance,
            _ => false,
        };

    // Flushed to the disk, then swapped in with the old file kept as state.json.previous (DurableFile.Write), so
    // neither a crash mid-write nor the machine restarting can leave nothing readable behind.
    public Result Save(AppState state) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            DurableFile.Write(StatePath, JsonSerializer.Serialize(runningVersion is null ? state : state with { WrittenBy = runningVersion }, Options));
        }, e => $"Could not write {StatePath}: {e.Message}");
}
