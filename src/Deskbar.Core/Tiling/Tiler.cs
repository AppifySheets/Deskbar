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
//
// `dropPending` is the bar's answer to "is this window being dropped on one of my workspace rows right now"
// (FloatingBar's header drop, wired by App). The spec: "A drop on one of the bar's workspace rows is the bar's
// gesture (move to that workspace) and wins over a tile swap." The bar puts the window back, waits for Snap,
// and only then moves it, so the window's Settled arrives in between with Snap's half-screen frame; while the
// drop is pending that Settled is ignored and no re-tile places the window, and WorkspaceManager.WindowMoved
// takes it out of the grid when the move happens. Null (tests, and an app without a bar) means never.
public sealed class Tiler(WorkspaceManager manager, IVirtualDesktopService desktops, IWindowMonitor monitor,
    ITileSurface surface, Func<bool> isPro, Action<string>? trace = null, Func<DateTimeOffset>? clock = null,
    Func<WindowHandle, bool>? dropPending = null) : IDisposable
{
    // The put-back backstop (spec: "a short per-window quiet period as a backstop"; anti-goal: a re-tile loop
    // with an app that insists on its own size or place). A window put back into its slot this many times
    // within this window of time is left where it settles until its next structural change.
    const int PutBacksBeforeGivingUp = 3;
    static readonly TimeSpan PutBackWindow = TimeSpan.FromSeconds(2);

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Func<WindowHandle, bool> dropping = dropPending ?? (_ => false);

    // Set by Dispose. A licence refresh can land after SessionEnding or ExitApp disposed the Tiler (it is
    // posted through the dispatcher), and a disposed Tiler must not place a window or hand the surface a set to
    // watch, which would put the LOCATIONCHANGE hook back. Every entry point goes through Run, which checks it.
    bool disposed;

    // When each window was last put back after a Settled, newest last; only the recent ones are kept.
    readonly Dictionary<WindowHandle, IReadOnlyList<DateTimeOffset>> putBacks = [];

    // Windows the backstop has given up on. Their accepted frame is wherever they settled, and nothing places
    // them again until a STRUCTURAL change hands them a different slot: they join, leave, swap, change screen,
    // or the grid around them changes count (PlaceIfNeeded sees a slot that differs from the one they had).
    readonly HashSet<WindowHandle> leftAlone = [];

    // What the manager's state said about tiling at the last evaluate (see OnStateChanged).
    Maybe<StateKey> stateKey = Maybe<StateKey>.None;

    // The few facts a StateChanged pulse can change that matter to tiling. Everything else the manager persists
    // (a rename, a fold, a colour) leaves it equal, and such a pulse reads no windows at all.
    sealed record StateKey(Maybe<Guid> Workspace, bool Tiled, bool Experiment, int Gap, bool Pro);

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

    // Windows last seen maximized while in a grid. When one of them settles un-maximized it goes straight back
    // into its slot (spec: "When it is restored, it goes back into its slot"), rather than being judged like a
    // drop: a window maximized before tiling began restores to wherever it used to be, and if that happened to
    // be over another tile it would otherwise swap.
    readonly HashSet<WindowHandle> maximized = [];

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
        subscriptions.Add(manager.StateChanged.Subscribe(_ => Run("state changed", OnStateChanged)));
        subscriptions.Add(manager.WindowMoved.Subscribe(w => Run($"window {w.Value:X} moved to another desktop", () => OnWindowMoved(w))));

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
        if (disposed) return;
        subscriptions.ForEach(s => s.Dispose());
        subscriptions.Clear();
        // Leave no hook behind: the surface's LOCATIONCHANGE hook exists only while something is tiled.
        Guarded("dispose", () => WatchOnly(new HashSet<WindowHandle>()));
        // From here on nothing runs: Refresh and any queued handler are ignored (see `disposed`).
        disposed = true;
        pending.Clear();
    }

    // --- serialising and surviving ------------------------------------------------------------------------
    //
    // Ruling 5, and the reason is the same as WorkspaceManager.OnWindowEvent's: Rx disposes a Subscribe(action)
    // subscription the first time its handler throws, and WindowMonitor swallows the rethrow to protect the
    // native callback. One exception from the surface (a window dying mid-call, a DWM call failing) would
    // otherwise leave the Tiler deaf until the app restarted. So every handler is wrapped: a failure costs that
    // one event, the next event re-evaluates from scratch, and the trace names what threw.
    //
    // Nothing runs once the Tiler is disposed, including work queued before the Dispose that a running call has
    // not reached yet (a handler can dispose the Tiler mid-loop only in theory, but the check costs nothing).
    void Run(string what, Action act)
    {
        if (disposed) return;
        pending.Enqueue((what, act));
        if (running) return;
        running = true;
        try
        {
            while (!disposed && pending.TryDequeue(out var next))
                Guarded(next.What, next.Act);
        }
        finally { running = false; }
    }

    //
    // A throw can stop a re-tile halfway, after the board was already stored, and the partial re-tile that
    // follows ordinary events only revisits screens whose window list changed. So the catch also forgets the
    // context and the state key: whatever runs next (an event, a pulse, a Refresh) re-tiles every screen and
    // repairs whatever the throw left half done. Per-window calls have their own guard as well (PerWindow), so
    // one bad window does not stop the rest in the first place; this is the backstop for a throw anywhere else.
    void Guarded(string what, Action act)
    {
        try { act(); }
        catch (Exception ex)
        {
            context = Maybe<(Guid, int)>.None;
            stateKey = Maybe<StateKey>.None;
            trace?.Invoke($"tile: {what} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // One window's share of a loop over several. A window that throws (it died mid-call, DWM refused) is traced
    // and skipped, and the loop carries on with the next one: one bad window must never leave the rest of its
    // screen where they were.
    void PerWindow(WindowHandle window, string what, Action act)
    {
        try { act(); }
        catch (Exception ex)
        {
            trace?.Invoke($"tile: {what} {window.Value:X} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
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
            maximized.Remove(e.Window.Handle);
            putBacks.Remove(e.Window.Handle);
            leftAlone.Remove(e.Window.Handle);
        }
        if (e.Kind is WindowEventKind.Appeared or WindowEventKind.Hidden or WindowEventKind.Disappeared or WindowEventKind.MinimizeChanged)
            Evaluate(everyScreen: false);
    }

    // The manager moved a window to another desktop (WorkspaceManager.WindowMoved). Nothing else announces
    // that: no window event and no LOCATIONCHANGE, because the window keeps its rectangle. The spec counts a
    // drop on one of the bar's workspace rows as the bar's gesture, and the grid it leaves closes up.
    // WorkspaceOf is asked once, for this window only:
    //   - on the board and no longer living here: it leaves and its screen re-tiles without it;
    //   - not on the board, living here now and eligible: it joins at the end, like a new window;
    //   - anything else: nothing.
    void OnWindowMoved(WindowHandle window)
    {
        if (!Acting().TryGetValue(out var workspace) || !boards.TryGetValue(workspace.Id, out var board))
            return;
        var livesHere = LivesOn(window, workspace);

        if (board.ScreenOf(window).TryGetValue(out var screen))
        {
            if (livesHere) return;
            Keep(workspace, board.Leave(window));
            Retile(workspace, [screen]);
            return;
        }

        if (!livesHere || !Eligible(window) || !surface.VisibleFrameOf(window).HasValue) return;
        var to = surface.ScreenOf(window);
        Keep(workspace, board.Join(to, window));
        Retile(workspace, [to]);
    }

    // StateChanged fires on every persist and on several window events, and a full evaluate asks WorkspaceOf
    // (a COM DesktopOf call) of every known window on the UI thread. So a pulse evaluates only when one of the
    // facts in StateKey moved: the workspace here, its Tiled flag, the experiment, the gap, or Pro. Changes to
    // which windows are in the grid reach the Tiler through window events and Settled instead.
    void OnStateChanged()
    {
        if (stateKey.HasValue && stateKey.Value == CurrentStateKey()) return;
        Evaluate(everyScreen: false);
    }

    StateKey CurrentStateKey()
    {
        var state = manager.State;
        var here = currentDesktop.Bind(desktop => state.Workspaces.TryFirst(w => w.DesktopId == desktop));
        return new StateKey(here.Map(w => w.Id), here.Map(w => w.Tiled).GetValueOrDefault(false),
            state.ExperimentalTiling, state.TileGap, isPro());
    }

    // A watched window stopped moving with the buttons up (spec, "By hand"). In order:
    //   0. Its drop on a bar row is pending (see `dropPending`): the bar's gesture wins, so nothing at all. The
    //      move that follows raises WindowMoved, which takes it out of the grid.
    //   1. Not on the board: ruling 3, SettledMove.Decide is never asked about it. It joins at the end if it
    //      now qualifies (the bar brought it here, it became tileable), which an ordinary re-evaluate does.
    //   2. No longer qualifies: the bar dropped it on another workspace, it was minimized. It leaves the grid
    //      and the rest close up, again by re-evaluating; it is not placed.
    //   3. Maximized: it keeps its slot and stays maximized; nothing moves.
    //   4. Its frame is the one it accepted from our last Place: our own move coming back, not a move by hand.
    //      Nothing, unless a re-tile held it while it was settling (PlaceIfNeeded) and its slot changed since:
    //      then it goes into the slot it has now.
    //   5. Un-maximized on the screen it tiles on: straight back into its slot.
    //   6. Otherwise SettledMove.Decide: swap with the tile under its centre, back into its slot, or join the
    //      grid of the screen it is now on.
    void OnSettled(WindowHandle window)
    {
        if (dropping(window))
        {
            trace?.Invoke($"tile: {window.Value:X} settled during a drop on the bar, ignored");
            return;
        }
        if (!Acting().TryGetValue(out var workspace) || !Board.ScreenOf(window).HasValue || !Qualifies(window, workspace))
        {
            Evaluate(everyScreen: false);
            return;
        }
        if (surface.IsMaximized(window))
        {
            maximized.Add(window);
            return;
        }
        if (!surface.VisibleFrameOf(window).TryGetValue(out var frame))
            return;
        var restored = maximized.Remove(window);

        var board = Board;
        var screens = surface.Screens().ToDictionary(s => s.Number);
        var screenNow = surface.ScreenOf(window);
        var slots = screens.TryGetValue(screenNow, out var screen) ? SlotsOf(screen, board.On(screenNow)) : new Dictionary<WindowHandle, WindowRect>();

        // Step 4. PlaceIfNeeded does nothing when the slot is the one this frame was accepted for, so for the
        // ordinary echo of our own Place this moves nothing; it moves the window only when a re-tile held it.
        if (given.TryGetValue(window, out var last) && last.Accepted == frame)
        {
            if (slots.TryGetValue(window, out var current))
                PlaceIfNeeded(window, current, force: false);
            return;
        }

        if (restored && slots.TryGetValue(window, out var home))
        {
            PutBack(window, home, frame);
            return;
        }

        switch (SettledMove.Decide(window, frame, screenNow, board, slots))
        {
            case MoveOutcome.Swap swap:
                boards[workspace.Id] = board.Swap(window, swap.With);
                Retile(workspace, [screenNow]);
                break;
            case MoveOutcome.BackToSlot:
                if (slots.TryGetValue(window, out var slot))
                    PutBack(window, slot, frame);
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

    // Does this window take part in `workspace`'s grid right now? Asked of every known window on each structural
    // event, so the cheap questions go first: IsListed is a set lookup and IsMinimized one user32 call, while
    // LivesOn is two COM calls (IsPinned and DesktopOf). CanTile comes last, after LivesOn, so its one-off
    // SetWindowPos probe is only ever made of windows that live here. The same four questions as before, in a
    // cheaper order; the answer is the same.
    bool Qualifies(WindowHandle window, Workspace workspace) =>
        monitor.IsListed(window) && !surface.IsMinimized(window) && LivesOn(window, workspace) && surface.CanTile(window);

    // "Lives here" (spec: the test is the one WorkspaceOf answers, so pinned and borrowed windows float).
    bool LivesOn(WindowHandle window, Workspace workspace) =>
        manager.WorkspaceOf(window).Map(w => w.Id == workspace.Id).GetValueOrDefault(false);

    // Everything else a window needs to be in a grid: on the taskbar, not minimized, and tileable.
    bool Eligible(WindowHandle window) =>
        monitor.IsListed(window) && !surface.IsMinimized(window) && surface.CanTile(window);

    // `everyScreen` re-tiles every screen whatever changed (the screens themselves changed). Otherwise only the
    // screens whose window list changed are re-tiled, unless the context changed (see `context`).
    void Evaluate(bool everyScreen)
    {
        var state = manager.State;
        var pro = isPro();
        stateKey = CurrentStateKey();

        // Forget the boards of workspaces the gate no longer acts on: untiled, or the experiment or Pro gone.
        var dropped = boards.Keys
            .Where(id => !state.Workspaces.TryFirst(w => w.Id == id)
                .Map(w => TilingGate.Acts(w, pro, state.ExperimentalTiling)).GetValueOrDefault(false))
            .ToList();
        var droppedWindows = dropped.SelectMany(id => boards[id].Screens.Values).SelectMany(w => w).ToList();
        dropped.ForEach(id => boards.Remove(id));
        Forget(droppedWindows);

        if (!Acting().TryGetValue(out var workspace))
        {
            // Nothing here is tiled: move nothing, and take the hook away.
            context = Maybe<(Guid, int)>.None;
            WatchOnly(new HashSet<WindowHandle>());
            return;
        }

        var tiling = (workspace.Id, state.TileGap);
        var full = everyScreen || !context.HasValue || context.Value != tiling;
        context = tiling;

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
        Keep(workspace, board);

        var changed = full || before is null
            ? board.Screens.Keys.ToList()
            : board.Screens.Keys.Union(before.Screens.Keys)
                .Where(s => !board.On(s).SequenceEqual(before.On(s)))
                .ToList();
        Retile(workspace, changed);
    }

    // A dropped board's windows lose what the Tiler had decided about them while they were tiled (final review,
    // item 4). Turning tiling back on is a fresh start (TileBoard.FirstFill, from where the windows are), and
    // stale per-window state would make it act on the old one:
    //   - leftAlone and given: a window the backstop gave up on would be skipped while its slot came out the
    //     same, and `given` alone would skip it too (its accepted frame is where it was left, and the "will not
    //     shrink" check reads that as accepted). So both go, and the window is placed like any other.
    //   - putBacks: the backstop's count starts clean.
    //   - maximized: a window maximized while tiled and restored while tiling was off is not "being restored"
    //     when tiling comes back; left in, its next drag over another tile would be read as a restore and put
    //     back instead of swapping.
    // A window still on another remembered board keeps its state, since that board still counts on it.
    // `rounded` stays: rounded corners are never undone (spec, "Rounded corners").
    void Forget(IReadOnlyList<WindowHandle> windows)
    {
        var stillGridded = boards.Values.SelectMany(b => b.Screens.Values).SelectMany(w => w).ToHashSet();
        windows.Where(w => !stillGridded.Contains(w)).ToList().ForEach(w =>
        {
            leftAlone.Remove(w);
            putBacks.Remove(w);
            maximized.Remove(w);
            given.Remove(w);
        });
    }

    // Stores a workspace's new board and does what every board change owes the windows on it.
    void Keep(Workspace workspace, TileBoard board)
    {
        boards[workspace.Id] = board;

        // Leaving a grid is a structural change too: a window that comes back starts with a clean put-back count.
        var gridded = boards.Values.SelectMany(b => b.Screens.Values).SelectMany(w => w).ToHashSet();
        putBacks.Keys.Where(w => !gridded.Contains(w)).ToList().ForEach(w => putBacks.Remove(w));
        leftAlone.RemoveWhere(w => !gridded.Contains(w));

        // Rounded once, as each window first joins a grid (spec, "Rounded corners"). Marked before the call, so a
        // window whose DWM call throws is tried once and traced once, not on every evaluate after.
        board.Screens.Values.SelectMany(w => w).Where(w => !rounded.Contains(w)).ToList()
            .ForEach(w =>
            {
                rounded.Add(w);
                PerWindow(w, "round corners of", () => surface.RoundCorners(w));
            });

        WatchOnly(board.Screens.Values.SelectMany(w => w).ToHashSet());
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

        // A maximized window's slot still counts (the tiles beneath do not reflow) but it is never placed. It is
        // noted, so that when it is restored it goes back into this slot (see `maximized`). Each window is asked
        // and placed under its own guard, so a throw for one still leaves the others in their slots.
        var maximizedNow = new HashSet<WindowHandle>();
        windows.ToList().ForEach(w => PerWindow(w, "place", () =>
        {
            if (surface.IsMaximized(w))
            {
                maximizedNow.Add(w);
                maximized.Add(w);
                return;
            }
            PlaceIfNeeded(w, slots[w], force: false);
        }));

        var line = $"tile: {workspace.Name} screen {screen.Number}: " +
                   string.Join(" ", windows.Select(w => $"{w.Value:X}@{Show(slots[w])}{(maximizedNow.Contains(w) ? "(max)" : "")}"));
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

    // A settled window goes back into its slot, unless the backstop says this has become a fight. An app that
    // snaps itself back (or insists on its own place) would otherwise be placed, move itself, settle and be
    // placed again for ever. The fourth put-back within PutBackWindow is not made: the window keeps where it
    // settled, that becomes its accepted frame, and it is left alone until its slot next changes.
    void PutBack(WindowHandle window, WindowRect slot, WindowRect frame)
    {
        var at = now();
        var recent = (putBacks.TryGetValue(window, out var times) ? times : [])
            .Where(t => at - t < PutBackWindow)
            .ToList();

        if (leftAlone.Contains(window) || recent.Count >= PutBacksBeforeGivingUp)
        {
            if (leftAlone.Add(window))
                trace?.Invoke($"tile: {window.Value:X} put back {recent.Count} times in {PutBackWindow.TotalSeconds:0}s, left at {Show(frame)}");
            given[window] = new Given(slot, frame);
            return;
        }

        putBacks[window] = recent.Append(at).ToList();
        // Forced: its last accepted frame may be its slot too, and it is not there now.
        PlaceIfNeeded(window, slot, force: true);
    }

    // Puts a window on its slot unless it is there already, or unless it is exactly where it ended up the last
    // time it was given this same slot (an app that will not shrink: placed once, accepted, not retried).
    // `force` drops the second exception, for a window the user moved and that has to go back.
    void PlaceIfNeeded(WindowHandle window, WindowRect slot, bool force)
    {
        // Never a window that is still moving (final review, item 2): a structural re-tile (another window
        // opening or closing on this screen) would otherwise yank a window the user is dragging by its title
        // bar or with AltSnap out from under the pointer. Nor one whose drop on a bar row is pending (item 1):
        // the bar is about to move it to another workspace. Its `given` is left as it was, so its Settled is
        // judged against what it was last given: a move by hand is decided like any drop, and the echo of our
        // own last Place goes on into the slot it has now (OnSettled, step 4).
        if (surface.IsSettling(window) || dropping(window))
        {
            trace?.Invoke($"tile: {window.Value:X} is {(dropping(window) ? "being dropped on the bar" : "still moving")}, not placed at {Show(slot)}");
            return;
        }

        // A window the backstop left alone stays where it is while its slot is the same one; a different slot is
        // a structural change (a join, a leave, a swap, another screen, a new count), and it is tiled again from
        // a clean count.
        if (leftAlone.Contains(window))
        {
            if (given.TryGetValue(window, out var held) && held.Slot == slot) return;
            leftAlone.Remove(window);
            putBacks.Remove(window);
        }

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
