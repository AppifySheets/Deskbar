using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.App;

// KEEPS A LOCK MARK ON EACH TILE of the tiled workspace being looked at. Petre wanted to lock a tile from the window
// itself rather than the bar; spec 2026-11-22-tile-lock-and-other-screens-design.md, "The pin on the window". LockMarks
// (Core) says where each mark goes and which ones show; LockMarkWindow draws one; this class holds them on the tiles.
//
// ONE TIMER DOES EVERYTHING. The spec has to watch the pointer anyway (an unlocked tile shows its mark only while the
// mouse is on its title bar), and it chose a cursor poll of about 100 ms over a low-level mouse hook, which would cost
// every mouse event on the machine. The same tick also re-reads each tile's rectangles and re-places its mark, which
// replaced "moved on the Tiler's LOCATIONCHANGE" from the spec: one code path, no second subscription, and the
// rectangles are cheap reads. While nothing is tiled on the desktop being looked at, the tick drops to Slow and does
// nothing but ask the Tiler for its (empty) board.
//
// A TILE THAT IS MOVING HAS NO MARK. While a tile settles (dragged, or being placed by a re-tile; TileSurface.IsSettling)
// its mark is hidden, and it comes back on the first tick after the tile settles (the surface's quiet interval is
// 150 ms). Following it on a 100 ms tick would leave the mark trailing behind a dragged title bar.
//
// What the tick reads of each tile never waits on the tile's thread (LockMarkNatives), so a hung app cannot freeze the
// bar through this; and the mark is never owned by the tile, for the same reason (LockMarkWindow's header).
//
// A click on a mark toggles the lock through the Tiler (the same call as the icon menu's "Don't split this tile" and the
// pipe's `lock-tile`), then ticks at once so the new look shows without waiting for the timer.
sealed class TileLockMarks : IDisposable
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(500);

    readonly Func<IReadOnlyList<WindowHandle>> tiles;
    readonly Func<WindowHandle, Maybe<bool>> lockOf;
    readonly Func<WindowHandle, bool, Result> setLocked;
    readonly Func<WindowHandle, bool> settling;
    readonly Action<nint> ignore;
    readonly Func<nint, Result> pinToAllDesktops;
    readonly Func<WindowHandle, Maybe<LockMarkGeometry>> geometryOf;
    readonly Func<Maybe<(int X, int Y)>> pointer;
    readonly Func<WindowHandle, bool> drawable;
    readonly DispatcherTimer timer;

    // The mark on each tile now.
    readonly Dictionary<WindowHandle, LockMarkWindow> shown = new();

    // Marks no tile is using, hidden, kept for the next tile: each new one costs an Ignore and a COM call to pin it.
    readonly Stack<LockMarkWindow> idle = new();

    // Every mark ever made, to close them all on Dispose, and the ones whose pin to every desktop has not worked yet
    // (a window never shown is on no desktop; App.PinFloatingBar has the story). Retried on every tick, as WindowRings does.
    readonly List<LockMarkWindow> made = [];
    readonly HashSet<LockMarkWindow> unpinned = [];

    // What each tile's mark last showed, so the trace writes only when it changes.
    readonly Dictionary<WindowHandle, string> traced = new();

    bool disposed;

    public TileLockMarks(
        Func<IReadOnlyList<WindowHandle>> tiles,
        Func<WindowHandle, Maybe<bool>> lockOf,
        Func<WindowHandle, bool, Result> setLocked,
        Func<WindowHandle, bool> settling,
        Action<nint> ignore,
        Func<nint, Result> pinToAllDesktops,
        Func<WindowHandle, Maybe<LockMarkGeometry>>? geometryOf = null,
        Func<Maybe<(int X, int Y)>>? pointer = null,
        Func<WindowHandle, bool>? drawable = null)
    {
        this.tiles = tiles;
        this.lockOf = lockOf;
        this.setLocked = setLocked;
        this.settling = settling;
        this.ignore = ignore;
        this.pinToAllDesktops = pinToAllDesktops;
        this.geometryOf = geometryOf ?? LockMarkNatives.GeometryOf;
        this.pointer = pointer ?? LockMarkNatives.Pointer;
        this.drawable = drawable ?? LockMarkNatives.Drawable;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Slow };
        timer.Tick += (_, _) => Tick();
    }

    // How often the timer ticks now: Fast while there are tiles, Slow while there are none.
    internal TimeSpan Interval { get; private set; } = Slow;

    // The mark on each tile, for the tests.
    internal IReadOnlyDictionary<WindowHandle, LockMarkWindow> Shown => shown;

    public void Start()
    {
        if (!disposed) timer.Start();
    }

    // One pass: which tiles get a mark, where, in which look; every other mark hidden. Never throws: a window that went
    // between two reads, or anything else unexpected, is traced, and the next tick starts from scratch.
    internal void Tick()
    {
        if (disposed) return;
        try
        {
            var board = tiles();
            var candidates = board
                .Where(w => drawable(w) && !settling(w))
                .SelectMany(w => geometryOf(w).Bind(LockMarks.Place)
                    .Map(spot => (Window: w, Spot: spot, Locked: lockOf(w).GetValueOrDefault(false)))
                    .ToList())
                .ToList();
            var wanted = LockMarks.Wanted(candidates, pointer());

            var keep = wanted.Select(m => m.Window).ToHashSet();
            shown.Keys.Where(w => !keep.Contains(w)).ToList().ForEach(Release);
            wanted.ToList().ForEach(Put);
            unpinned.Where(m => pinToAllDesktops(m.Handle).IsSuccess).ToList().ForEach(m => unpinned.Remove(m));

            Interval = board.Count > 0 ? Fast : Slow;
            if (timer.Interval != Interval) timer.Interval = Interval;
        }
        catch (Exception e)
        {
            Trace($"lock marks: tick failed: {e.GetType().Name}: {e.Message}");
        }
    }

    void Put(LockMark mark)
    {
        var window = shown.TryGetValue(mark.Window, out var had) ? had : Take();
        shown[mark.Window] = window;
        window.Put(mark, mark.Window.Value);
        Note(mark.Window, mark.Locked ? "locked" : "hover");
    }

    void Release(WindowHandle tile)
    {
        var window = shown[tile];
        shown.Remove(tile);
        window.Hide();
        idle.Push(window);
        Note(tile, "gone");
    }

    LockMarkWindow Take()
    {
        if (idle.TryPop(out var reused)) return reused;
        var window = new LockMarkWindow();
        ignore(window.Handle);
        window.Show();
        // The click toggles the lock of whichever tile this mark is on at the time: marks move between tiles.
        window.Clicked.Subscribe(_ => Clicked(window));
        made.Add(window);
        unpinned.Add(window);
        return window;
    }

    void Clicked(LockMarkWindow window)
    {
        var tile = shown.FirstOrDefault(p => p.Value == window).Key;
        if (tile == default) return;
        var on = !lockOf(tile).GetValueOrDefault(false);
        setLocked(tile, on).TapError(error => Trace($"lock mark 0x{tile.Value:X}: {(on ? "lock" : "unlock")} refused: {error}"));
        Tick();
    }

    void Note(WindowHandle tile, string state)
    {
        if (traced.TryGetValue(tile, out var was) && was == state) return;
        if (state == "gone") traced.Remove(tile); else traced[tile] = state;
        Trace($"lock mark 0x{tile.Value:X}: {state}");
    }

    // Behind the one trace switch (CLAUDE.md, Instrumentation), and only on a change (Note).
    static void Trace(string line)
    {
        if (ClickTrace.On) ClickTrace.Write(line);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        made.ForEach(m => m.Close());
        made.Clear();
        shown.Clear();
        idle.Clear();
        unpinned.Clear();
    }
}
