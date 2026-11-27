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
// nothing. Fields this build does not know ride through Load and Save in AppState.Extra (top level), Workspace.Extra
// and Bookmark.Extra. A file stamped by a newer build of the same major still loads, and is first copied to
// state.json.from-<version>.<date> (KeepCopyIfNewer), because what this build cannot see it cannot carry.
//
// `write` puts the serialized text on the disk. Null (the tests) is DurableFile.Write, synchronous. The app passes
// DeferredWrites.Write, so the flush to the disk happens off the UI thread (DeferredWrites has the measurement).
public sealed class JsonPersistenceStore(string baseDirectory, Action<string>? trace = null, string? runningVersion = null,
    Action<string, string>? write = null) : IPersistenceStore
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
            : StateVersionGate.Check(state.WrittenBy, runningVersion).Map(() => state))
        .Tap(KeepCopyIfNewer);

    // The file a newer build wrote, kept before this older build saves over it. Within a major an older build loads a
    // newer file (StateVersionGate), and Extra carries unknown fields through its save, but only the fields it can SEE:
    // a list it has never heard of inside a record it does know, or a field whose meaning changed, is gone after the
    // first save. That happened on 2026-11-27 (Deskbar task 0bd17709): the installed 2.6.48 started after a 2.6.208
    // test build, ran 47 seconds, and its save dropped every workspace's Bookmarks, which 2.6.48 predates. Seven
    // bookmarks were lost for good because nothing else held them.
    //
    // So before any save can happen (Load runs once, at start, before the first Save), the file as the newer build left
    // it is copied to state.json.from-<that build's version>.<yyyyMMdd-HHmmss>. Nothing reads the copy back: it is there
    // so a person can restore what was lost, by hand or by asking a session to.
    //
    // A copy is NEVER overwritten, and the date is in the name for that reason (whole-branch review): the same newer
    // version can write again AFTER the older build stripped the file (the test build restarted, loaded the stripped
    // file and saved it), and a copy named by version alone would then replace the one good copy with the stripped one.
    // A copy whose bytes match one already kept is not made again, so switching between the Store copy and the GitHub
    // copy several times a day costs nothing while neither changes. Copies go after 30 days, and past the newest 20:
    // a count as low as three let a few test builds in a row push out the only copy that still had the lost data,
    // before anyone had noticed the loss.
    //
    // The source is the file the load read: state.json, or state.json.previous when state.json could not be read
    // (DurableFile.Read has then moved it aside), since that one too can be the newer build's.
    //
    // NOTHING HERE MAY FAIL THE LOAD. A copy that cannot be made is traced and the start goes on: the user's settings
    // matter more than the backup of them.
    const int NewerCopiesKept = 20;
    static readonly TimeSpan NewerCopiesAge = TimeSpan.FromDays(30);

    void KeepCopyIfNewer(AppState state)
    {
        if (runningVersion is null || !StateVersionGate.IsNewer(state.WrittenBy, runningVersion)) return;
        var source = new[] { StatePath, DurableFile.PreviousOf(StatePath) }.FirstOrDefault(File.Exists);
        if (source is null) return;
        try
        {
            var bytes = File.ReadAllBytes(source);
            var same = new DirectoryInfo(baseDirectory).GetFiles("state.json.from-*")
                .FirstOrDefault(f => File.ReadAllBytes(f.FullName).AsSpan().SequenceEqual(bytes));
            if (same is not null)
            {
                trace?.Invoke($"state.json was written by the newer {state.WrittenBy}; this is {runningVersion}; already kept as {same.Name}");
                return;
            }

            // The version goes into a file name, so anything Windows refuses there ('+' is allowed) becomes '_'.
            var version = Path.GetInvalidFileNameChars().Aggregate(state.WrittenBy!, (text, bad) => text.Replace(bad, '_'));
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var copy = Enumerable.Range(0, int.MaxValue)
                .Select(n => Path.Combine(baseDirectory, n == 0 ? $"state.json.from-{version}.{stamp}" : $"state.json.from-{version}.{stamp}-{n}"))
                .First(candidate => !File.Exists(candidate));
            // WriteAllBytes rather than File.Copy, so the copy's write time is NOW: the pruning below ages copies from
            // when they were made, not from when the newer build last wrote the file.
            File.WriteAllBytes(copy, bytes);
            trace?.Invoke($"state.json was written by the newer {state.WrittenBy}; this is {runningVersion}; kept a copy as {Path.GetFileName(copy)}");

            var cutoff = DateTime.UtcNow - NewerCopiesAge;
            new DirectoryInfo(baseDirectory).GetFiles("state.json.from-*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Where((f, index) => index >= NewerCopiesKept || f.LastWriteTimeUtc < cutoff)
                .ToList()
                .ForEach(f => f.Delete());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            trace?.Invoke($"state.json was written by the newer {state.WrittenBy}; keeping a copy failed: {e.Message}");
        }
    }

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
            // Serialized HERE, on the caller's thread, so the text is this state and no other thread reads it.
            (write ?? DurableFile.Write)(StatePath, JsonSerializer.Serialize(runningVersion is null ? state : state with { WrittenBy = runningVersion }, Options));
        }, e => $"Could not write {StatePath}: {e.Message}");
}
