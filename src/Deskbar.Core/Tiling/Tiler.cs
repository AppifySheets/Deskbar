using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// The tiling coordinator: decides WHEN a tiled workspace's windows move and WHERE they go. It owns no Win32;
// everything it learns about a window and every move it makes goes through ITileSurface, so the whole of the
// behaviour in the spec (docs/superpowers/specs/2026-11-08-tiling-design.md, "Behaviour") is tested against
// fakes in TilerTests.
//
// The rules it keeps, each from the spec:
//   - It acts only where TilingGate.Acts says so: the experiment is on, the workspace is tiled, the copy is
//     Pro (Petre: "disabled by default, until i have tested it well"). While it does not act it moves nothing
//     and watches nothing, so turning any of the three off leaves every window where it is.
//   - Only the desktop being looked at is tiled. Another desktop's windows are never moved, restored or
//     un-maximized; arriving at a tiled workspace re-tiles it once, which picks up what changed while away.
//   - Only windows that LIVE on the workspace tile (WorkspaceManager.WorkspaceOf, which is None for a pinned
//     window and the home workspace for a borrowed one), that the surface says can tile, and that are not
//     minimized. Everything else floats above the grid.
//   - A maximized window keeps its slot (the tiles beneath do not reflow) and is never placed.
//   - Slot order is join order; a new or restored window goes to the end (TileBoard).
//
// Threading: every entry point runs on the UI thread (window events, desktop changes and the surface's
// Settled all arrive there). Calls are still SERIALISED through Run, because a move into another process's
// window can pump messages while it waits, and a handler that re-entered itself mid-re-tile would work from
// a half-updated board. A re-entrant call is queued and runs when the current one finishes.
public sealed class Tiler(WorkspaceManager manager, IVirtualDesktopService desktops, IWindowMonitor monitor,
    ITileSurface surface, Func<bool> isPro, Action<string>? trace = null) : IDisposable
{
    // What a window was last given and what it accepted. Accepted is the visible frame read back after the
    // Place: the slot itself, or the size an app insisted on (a minimum size larger than its slot). It is the
    // SELF-MOVE GUARD: Deskbar's own SetWindowPos raises LOCATIONCHANGE like any other move, so a Settled whose
    // frame equals the accepted frame is our own move coming back and is not a reason to do anything. The slot
    // is kept beside it so a window that would not shrink is not placed again on every re-tile, only when its
    // slot actually changes (spec: "Accept what the window ends up as, do not retry in a loop").
    sealed record Given(WindowRect Slot, WindowRect Accepted);

    // The board of every tiled workspace visited this session, by workspace id. Kept across desktop switches
    // so coming back to a workspace keeps its slot order instead of re-deriving it from positions (a maximized
    // window's full-screen frame would otherwise sort it first). Dropped when the gate stops acting for that
    // workspace, so turning tiling back on starts from where the windows are (TileBoard.FirstFill).
    readonly Dictionary<Guid, TileBoard> boards = [];
    readonly Dictionary<WindowHandle, Given> given = [];

    // Windows already given rounded corners, so DWM is asked once per window rather than on every re-tile.
    readonly HashSet<WindowHandle> rounded = [];

    // The last trace line per workspace and screen, so the trace writes only when a layout changed.
    readonly Dictionary<(Guid Workspace, int Screen), string> lastLines = [];

    // Work queued by a re-entrant call (see the class comment), and whether a call is running now.
    readonly Queue<(string What, Action Act)> pending = new();
    bool running;

    readonly List<IDisposable> subscriptions = [];

    // The desktop being looked at, as CurrentChanged last reported it. Kept rather than asked each time
    // because during a switch the OS's answer and the event can disagree for a moment (see
    // WorkspaceManager.CurrentWorkspaceId); the event is the one that says where the user is going.
    Maybe<Guid> currentDesktop = Maybe<Guid>.None;

    // The workspace being tiled right now and the gap it was last tiled with. When either changes (arriving
    // at a tiled workspace, tiling turned on, the gap changed) the next re-tile covers every screen; otherwise
    // only the screens whose window list changed are re-tiled, which is what keeps an unrelated StateChanged
    // pulse (a rename, a fold, a drag's own Moved) from yanking a window the user is in the middle of moving.
    Maybe<(Guid Workspace, int Gap)> context = Maybe<(Guid, int)>.None;

    // The set the surface's LOCATIONCHANGE hook is narrowed to. Starts empty, which is also "no hook".
    IReadOnlySet<WindowHandle> watched = new HashSet<WindowHandle>();

    // The current tiled workspace's board, or Empty while nothing here is tiled. For tests and the trace.
    public TileBoard Board =>
        context.Map(c => boards.TryGetValue(c.Workspace, out var board) ? board : TileBoard.Empty)
            .GetValueOrDefault(TileBoard.Empty);

    public void Start()
    {
        // Ruling 1: the Tiler listens to the manager itself, so a workspace tiled or untiled, the gap changed
        // or the experiment switched (all persisted through WorkspaceManager, all pulsing StateChanged) take
        // effect without the App relaying them. The App only calls Refresh after a licence refresh, because
        // Pro is the one input the manager does not hold.
        subscriptions.Add(monitor.Events.Subscribe(e => Run($"window event {e.Kind} for {e.Window.Handle.Value:X}", () => OnWindowEvent(e))));
        subscriptions.Add(desktops.CurrentChanged.Subscribe(id => Run($"desktop change to {id}", () =>
        {
            currentDesktop = id;
            Evaluate(everyScreen: false);
        })));
        subscriptions.Add(surface.Settled.Subscribe(w => Run($"settled {w.Value:X}", () => OnSettled(w))));
        // A screen plugged in or out, or a work area that changed: every screen is re-tiled.
        subscriptions.Add(surface.ScreensChanged.Subscribe(_ => Run("screens changed", () => Evaluate(everyScreen: true))));
        subscriptions.Add(manager.StateChanged.Subscribe(_ => Run("state changed", () => Evaluate(everyScreen: false))));

        // The first evaluate reads the current desktop and, if it is tiled, tiles it.
        Refresh();
    }

    // Re-reads the gate (Pro included) and brings the current desktop in line with it. Cheap and idempotent:
    // with nothing changed it moves nothing.
    public void Refresh() => Run("refresh", () =>
    {
        currentDesktop = desktops.CurrentDesktop().AsMaybe().Or(currentDesktop);
        Evaluate(everyScreen: false);
    });

    public void Dispose()
    {
        subscriptions.ForEach(s => s.Dispose());
        subscriptions.Clear();
        // Leave no hook behind: the surface's LOCATIONCHANGE hook exists only while something is tiled.
        Guarded("dispose", () => WatchOnly(new HashSet<WindowHandle>()));
    }

    // --- serialising and surviving ------------------------------------------------------------------------
    //
    // Ruling 5, and the reason is the same as WorkspaceManager.OnWindowEvent's: Rx disposes a Subscribe(action)
    // subscription the first time its handler throws, and WindowMonitor swallows the rethrow to protect the
    // native callback. One exception from the surface (a window dying mid-call, a DWM call failing) would
    // otherwise leave the Tiler deaf until the app restarted. So every handler is wrapped: a failure costs that
    // one event, the next event re-evaluates from scratch, and the trace names what threw.
    void Run(string what, Action act)
    {
        pending.Enqueue((what, act));
        if (running) return;
        running = true;
        try
        {
            while (pending.TryDequeue(out var next))
                Guarded(next.What, next.Act);
        }
        finally { running = false; }
    }

    void Guarded(string what, Action act)
    {
        try { act(); }
        catch (Exception ex)
        {
            trace?.Invoke($"tile: {what} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // --- events -------------------------------------------------------------------------------------------

    // Appeared, Hidden, Disappeared and MinimizeChanged can each change which windows are in the grid, so each
    // re-evaluates. A maximize or un-maximize has no event here (ruling 4): it arrives as a Settled from the
    // surface, and as IsMaximized at re-tile time. Moved is left to Settled as well, which sees the same drag
    // end plus every move Moved never reports (keyboard snaps, AltSnap, an app resizing itself).
    void OnWindowEvent(WindowEvent e)
    {
        if (e.Kind == WindowEventKind.Disappeared)
        {
            // A dead hwnd can be recycled for an unrelated window; forget what we knew about this one.
            given.Remove(e.Window.Handle);
            rounded.Remove(e.Window.Handle);
        }
        if (e.Kind is WindowEventKind.Appeared or WindowEventKind.Hidden or WindowEventKind.Disappeared or WindowEventKind.MinimizeChanged)
            Evaluate(everyScreen: false);
    }

    // A watched window stopped moving with the buttons up (spec, "By hand"). In order:
    //   1. Not on the board: ruling 3, SettledMove.Decide is never asked about it. It joins at the end if it
    //      now qualifies (the bar brought it here, it became tileable), which an ordinary re-evaluate does.
    //   2. No longer qualifies: the bar dropped it on another workspace, it was minimized. It leaves the grid
    //      and the rest close up, again by re-evaluating; it is not placed.
    //   3. Maximized: it keeps its slot and stays maximized; nothing moves.
    //   4. Its frame is the one it accepted from our last Place: our own move coming back. Nothing.
    //   5. Otherwise SettledMove.Decide: swap with the tile under its centre, back into its slot, or join the
    //      grid of the screen it is now on.
    void OnSettled(WindowHandle window)
    {
        if (!Acting().TryGetValue(out var workspace) || !Board.ScreenOf(window).HasValue || !Qualifies(window, workspace))
        {
            Evaluate(everyScreen: false);
            return;
        }
        if (surface.IsMaximized(window) || !surface.VisibleFrameOf(window).TryGetValue(out var frame))
            return;
        if (given.TryGetValue(window, out var last) && last.Accepted == frame)
            return;

        var board = Board;
        var screens = surface.Screens().ToDictionary(s => s.Number);
        var screenNow = surface.ScreenOf(window);
        var slots = screens.TryGetValue(screenNow, out var screen) ? SlotsOf(screen, board.On(screenNow)) : new Dictionary<WindowHandle, WindowRect>();

        switch (SettledMove.Decide(window, frame, screenNow, board, slots))
        {
            case MoveOutcome.Swap swap:
                boards[workspace.Id] = board.Swap(window, swap.With);
                Retile(workspace, [screenNow]);
                break;
            case MoveOutcome.BackToSlot:
                // Forced: its last accepted frame may be its slot too, and it is not there now.
                if (slots.TryGetValue(window, out var slot))
                    PlaceIfNeeded(window, slot, force: true);
                break;
            case MoveOutcome.JoinScreen join:
                var from = board.ScreenOf(window).GetValueOrDefault(join.Screen);
                boards[workspace.Id] = board.Join(join.Screen, window);
                Retile(workspace, [from, join.Screen]);
                break;
        }
    }

    // --- the core: bring the board in line with the world, then re-tile -----------------------------------

    // The current workspace, if tiling acts on it right now.
    Maybe<Workspace> Acting()
    {
        var state = manager.State;
        var pro = isPro();
        return currentDesktop
            .Bind(desktop => state.Workspaces.TryFirst(w => w.DesktopId == desktop))
            .Where(w => TilingGate.Acts(w, pro, state.ExperimentalTiling));
    }

    // Does this window take part in `workspace`'s grid right now?
    bool Qualifies(WindowHandle window, Workspace workspace) =>
        manager.WorkspaceOf(window).Map(w => w.Id == workspace.Id).GetValueOrDefault(false)
        && monitor.IsListed(window)
        && !surface.IsMinimized(window)
        && surface.CanTile(window);

    // `everyScreen` re-tiles every screen whatever changed (the screens themselves changed). Otherwise only the
    // screens whose window list changed are re-tiled, unless the context changed (see `context`).
    void Evaluate(bool everyScreen)
    {
        var state = manager.State;
        var pro = isPro();

        // Forget the boards of workspaces the gate no longer acts on: untiled, or the experiment or Pro gone.
        boards.Keys
            .Where(id => !state.Workspaces.TryFirst(w => w.Id == id)
                .Map(w => TilingGate.Acts(w, pro, state.ExperimentalTiling)).GetValueOrDefault(false))
            .ToList()
            .ForEach(id => boards.Remove(id));

        if (!Acting().TryGetValue(out var workspace))
        {
            // Nothing here is tiled: move nothing, and take the hook away.
            context = Maybe<(Guid, int)>.None;
            WatchOnly(new HashSet<WindowHandle>());
            return;
        }

        var now = (workspace.Id, state.TileGap);
        var full = everyScreen || !context.HasValue || context.Value != now;
        context = now;

        var before = boards.TryGetValue(workspace.Id, out var known) ? known : null;
        var windows = manager.KnownWindows
            .Select(w => w.Handle)
            .Where(h => Qualifies(h, workspace))
            .Select(h => (Window: h, Screen: surface.ScreenOf(h), Frame: surface.VisibleFrameOf(h)))
            // A window with no frame has gone between the list and the question.
            .Where(w => w.Frame.HasValue)
            .Select(w => (w.Window, w.Screen, Frame: w.Frame.Value))
            .ToList();
        var board = before is null ? TileBoard.FirstFill(windows) : Reconciled(before, windows);
        boards[workspace.Id] = board;

        // Rounded once, as each window first joins a grid (spec, "Rounded corners").
        board.Screens.Values.SelectMany(w => w).Where(w => !rounded.Contains(w)).ToList()
            .ForEach(w => { surface.RoundCorners(w); rounded.Add(w); });

        WatchOnly(board.Screens.Values.SelectMany(w => w).ToHashSet());

        var changed = full || before is null
            ? board.Screens.Keys.ToList()
            : board.Screens.Keys.Union(before.Screens.Keys)
                .Where(s => !board.On(s).SequenceEqual(before.On(s)))
                .ToList();
        Retile(workspace, changed);
    }

    // The remembered board brought up to date: windows that no longer qualify leave (the rest close up),
    // windows on a screen that has gone join the screen they are on now, and windows new to the grid join at
    // the end of their screen, in the order the manager knows them (which is the order they appeared).
    TileBoard Reconciled(TileBoard before, IReadOnlyList<(WindowHandle Window, int Screen, WindowRect Frame)> windows)
    {
        var live = windows.Select(w => w.Window).ToHashSet();
        var screens = surface.Screens().Select(s => s.Number).ToHashSet();
        var onBoard = before.Screens.Values.SelectMany(w => w).ToList();

        var kept = onBoard.Where(w => !live.Contains(w)).Aggregate(before, (board, w) => board.Leave(w));
        var rehomed = kept.Screens
            .Where(s => !screens.Contains(s.Key))
            .SelectMany(s => s.Value)
            .ToList()
            .Aggregate(kept, (board, w) => board.Join(surface.ScreenOf(w), w));
        return windows
            .Where(w => !onBoard.Contains(w.Window))
            .Aggregate(rehomed, (board, w) => board.Join(w.Screen, w.Window));
    }

    // Re-tiles the listed screens of the current board. A screen the surface no longer reports is skipped (its
    // windows are re-homed by the next evaluate, which ScreensChanged triggers).
    void Retile(Workspace workspace, IReadOnlyList<int> numbers)
    {
        var board = boards.TryGetValue(workspace.Id, out var b) ? b : TileBoard.Empty;
        var screens = surface.Screens().ToDictionary(s => s.Number);
        numbers.Distinct()
            .Where(screens.ContainsKey)
            .ToList()
            .ForEach(n => RetileScreen(workspace, screens[n], board.On(n)));
    }

    void RetileScreen(Workspace workspace, TileScreen screen, IReadOnlyList<WindowHandle> windows)
    {
        if (windows.Count == 0)
        {
            lastLines.Remove((workspace.Id, screen.Number));
            return;
        }

        var slots = SlotsOf(screen, windows);
        // Ruling 2: a work area too small for its grid (GridLayout does not guard it) produces tiles of zero
        // or negative size. Placing those would crush windows into nothing, so the screen is skipped for this
        // re-tile: none of its windows move, and the board keeps them for when there is room.
        if (slots.Count == 0)
        {
            trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: work area {Show(screen.WorkArea)} is too small for {windows.Count} windows, skipped");
            return;
        }

        // A maximized window's slot still counts (the tiles beneath do not reflow) but it is never placed.
        windows.Where(w => !surface.IsMaximized(w)).ToList()
            .ForEach(w => PlaceIfNeeded(w, slots[w], force: false));

        var line = $"tile: {workspace.Name} screen {screen.Number}: " +
                   string.Join(" ", windows.Select(w => $"{w.Value:X}@{Show(slots[w])}{(surface.IsMaximized(w) ? "(max)" : "")}"));
        if (lastLines.TryGetValue((workspace.Id, screen.Number), out var last) && last == line) return;
        lastLines[(workspace.Id, screen.Number)] = line;
        trace?.Invoke(line);
    }

    // The slot of each window on a screen, by window; empty when the grid does not fit (see RetileScreen).
    IReadOnlyDictionary<WindowHandle, WindowRect> SlotsOf(TileScreen screen, IReadOnlyList<WindowHandle> windows)
    {
        // The gap is set at 100% scaling and scaled by the screen's DPI, so it looks the same on every screen.
        var gap = (int)Math.Round(manager.State.TileGap * screen.Dpi / 96.0, MidpointRounding.AwayFromZero);
        var tiles = GridLayout.Arrange(screen.WorkArea, windows.Count, gap);
        return tiles.Any(t => t.Width <= 0 || t.Height <= 0)
            ? new Dictionary<WindowHandle, WindowRect>()
            : windows.Zip(tiles).ToDictionary(p => p.First, p => p.Second);
    }

    // Puts a window on its slot unless it is there already, or unless it is exactly where it ended up the last
    // time it was given this same slot (an app that will not shrink: placed once, accepted, not retried).
    // `force` drops the second exception, for a window the user moved and that has to go back.
    void PlaceIfNeeded(WindowHandle window, WindowRect slot, bool force)
    {
        var frame = surface.VisibleFrameOf(window);
        if (frame.HasValue && frame.Value == slot)
        {
            given[window] = new Given(slot, slot);
            return;
        }
        if (!force && frame.HasValue && given.TryGetValue(window, out var last) && last.Slot == slot && last.Accepted == frame.Value)
            return;

        surface.Place(window, slot)
            .Tap(() =>
            {
                var accepted = surface.VisibleFrameOf(window).GetValueOrDefault(slot);
                given[window] = new Given(slot, accepted);
                if (accepted != slot)
                    trace?.Invoke($"tile: {window.Value:X} kept {Show(accepted)}, wanted {Show(slot)}");
            })
            // Ordinary: the window closed mid-re-tile, or it is elevated. The rest of the screen is still tiled,
            // and the window's own Disappeared (or the next re-tile) settles the board.
            .TapError(error =>
            {
                given.Remove(window);
                trace?.Invoke($"tile: place {window.Value:X} at {Show(slot)} failed: {error}");
            });
    }

    // Tells the surface which windows to watch, only when the set changed: an empty set removes the hook.
    void WatchOnly(IReadOnlySet<WindowHandle> windows)
    {
        if (watched.SetEquals(windows)) return;
        watched = windows;
        surface.Watch(windows);
    }

    static string Show(WindowRect r) => $"{r.Left},{r.Top},{r.Right},{r.Bottom}";
}
