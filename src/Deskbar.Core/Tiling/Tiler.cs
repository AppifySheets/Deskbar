using System.Reactive.Concurrency;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tiling;

// The tiling coordinator: decides WHEN a tiled workspace's windows move and WHERE they go. It owns no Win32;
// everything it learns about a window and every move it makes goes through ITileSurface, so the whole of the
// behaviour in the spec (docs/superpowers/specs/2026-11-08-tiling-design.md, "Behaviour") is tested against
// fakes in TilerTests.
//
// The rules it keeps, each from the spec:
//   - It acts only where TilingGate.Acts says so: the workspace is tiled and the copy may tile. While it does
//     not act it moves nothing and watches nothing, so turning either off leaves every window where it is.
//   - Only the desktop being looked at is tiled. Another desktop's windows are never moved, restored or
//     un-maximized; arriving at a tiled workspace re-tiles it once, which picks up what changed while away.
//   - Only windows that LIVE on the workspace tile (WorkspaceManager.WorkspaceOf, which is None for a pinned
//     window and the home workspace for a borrowed one), that the surface says can tile, and that are not
//     minimized. Everything else floats above the grid.
//   - A maximized window keeps its slot (the tiles beneath do not reflow) and is never placed.
//   - A new window takes half of the tile the user last used, or fills an opening, and no other tile moves
//     (Tiler.Arrival.cs). A restored one goes back to the place it left when it was minimized (Tiler.Minimize.cs).
//   - A tile that leaves takes only its own leaf of the screen's split tree (TileLayout), and the tiles of its
//     split grow into the room. On a workspace that keeps a closed tile's space (Workspace.TileKeepsSpace), a tile
//     that CLOSES leaves its leaf as the screen's opening instead (LayoutOn).
//   - A drop on a tile's edge puts the window beside THAT tile only, at any depth of the tree (TileArrange.Drop;
//     docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md). A drop in the band along an edge of a screen
//     makes it a full-height column or full-width row of that whole screen (TileArrange.DropAtEdge).
//   - The first fill (a start, or tiling switched on) matches the windows to a stored layout's cells by where they
//     sit, so a hand-made shape survives a restart (TileBoard.FirstFill).
//
// Threading: every entry point runs on the UI thread, and the Tiler makes sure of it rather than trusting its
// sources. `inputs` is the UI thread's scheduler (App passes one; tests leave it null, and then nothing is
// re-scheduled). Every subscription in Start is observed on it, because two of the sources do NOT arrive on the
// UI thread: Windows raises its desktop change on a thread of its own, and the manager pulses StateChanged (and
// may move windows home) from inside that same notification. Measured on Petre's machine on 2026-11-18: the
// Tiler handed the surface its windows from thread 14 (MTA, no message loop), and the surface's LOCATIONCHANGE
// hook, which is heard only on the thread that installs it, was never heard at all. A hand resize stuck and a
// moved tile stayed where it was dropped. ALL inputs go through the one scheduler, not just those two, so they
// are queued in the order they happened and none can overtake another.
//
// Calls are still SERIALISED through Run, because a move into another process's
// window can pump messages while it waits, and a handler that re-entered itself mid-re-tile would work from
// a half-updated board. A re-entrant call is queued and runs when the current one finishes.
//
// `dropPending` is the bar's answer to "is this window being dropped on one of my workspace rows right now"
// (FloatingBar's header drop, wired by App). The spec: "A drop on one of the bar's workspace rows is the bar's
// gesture (move to that workspace) and wins over a tile swap." The bar puts the window back, waits for Snap,
// and only then moves it, so the window's Settled arrives in between with Snap's half-screen frame; while the
// drop is pending that Settled is ignored and no re-tile places the window, and WorkspaceManager.WindowMoved
// takes it out of the grid when the move happens. Null (tests, and an app without a bar) means never.
//
// Partial: resizable tiles (moving a split by dragging a separation, and keeping the sizes) live in
// Tiler.Resize.cs. This file only calls into it from SlotsOf, Keep, OnSettled and PlaceIfNeeded.
public sealed partial class Tiler(WorkspaceManager manager, IVirtualDesktopService desktops, IWindowMonitor monitor,
    ITileSurface surface, Func<bool> isPro, Action<string>? trace = null, Func<DateTimeOffset>? clock = null,
    Func<WindowHandle, bool>? dropPending = null, IScheduler? inputs = null) : IDisposable
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
    // (a rename, a fold, a colour) leaves it equal, and such a pulse reads no windows at all. Layouts is the current
    // workspace's stored split trees (Workspace.TileLayouts), so "Even out tiles", "Reset to grid", untile and the
    // pipe's `even` (which only change state) re-tile; a ValueList, so it compares by contents.
    sealed record StateKey(Maybe<Guid> Workspace, bool Tiled, int Gap, bool Pro, ValueList<ScreenTileLayout> Layouts);

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

    // TURNING TILING ON UN-MAXIMIZES. Petre, on tiling a workspace with a maximized browser over the grid:
    // "activating tile windows should unmaximize all windows". The design otherwise leaves a maximized window
    // maximized (it keeps its slot and covers the tiles), and that still holds afterwards: this is only for the
    // moment the workspace's Tiled flag turns on.
    //
    // `tiledLast` is which workspaces were tiled at the last evaluate; None until the first one, so the workspaces
    // already tiled when Deskbar starts were not "just turned on" and a restart restores nothing. A workspace that
    // turns on waits in `restoreOnArrival` until the Tiler acts on it, because un-maximizing a window on another
    // desktop makes Windows follow it there (spec, anti-goals). It is restored once; untiling it first cancels it.
    Maybe<IReadOnlySet<Guid>> tiledLast = Maybe<IReadOnlySet<Guid>>.None;
    readonly HashSet<Guid> restoreOnArrival = [];

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
        // Ruling 1: the Tiler listens to the manager itself, so a workspace tiled or untiled or the gap changed
        // (all persisted through WorkspaceManager, all pulsing StateChanged) take
        // effect without the App relaying them. The App only calls Refresh after a licence refresh, because
        // Pro is the one input the manager does not hold.
        // Every source through OnInputs, so each handler runs on the UI thread whatever thread raised it (see
        // "Threading" in the class header).
        subscriptions.Add(OnInputs(monitor.Events).Subscribe(e => Run($"window event {e.Kind} for {e.Window.Handle.Value:X}", () => OnWindowEvent(e))));
        subscriptions.Add(OnInputs(desktops.CurrentChanged).Subscribe(id => Run($"desktop change to {id}", () =>
        {
            currentDesktop = id;
            Evaluate(everyScreen: false);
        })));
        subscriptions.Add(OnInputs(surface.Settled).Subscribe(w => Run($"settled {w.Value:X}", () => OnSettled(w))));
        // A tile still being dragged: live resize or a move preview (Tiler.Drag.cs).
        subscriptions.Add(OnInputs(surface.Moving).Subscribe(w => Run($"moving {w.Value:X}", () => OnMoving(w))));
        // A screen plugged in or out, or a work area that changed: every screen is re-tiled. A screen that came
        // back first starts its grace period (RememberScreens, Tiler.Resize.cs), so its sizes survive its windows
        // rejoining one at a time.
        subscriptions.Add(OnInputs(surface.ScreensChanged).Subscribe(_ => Run("screens changed", () =>
        {
            RememberScreens();
            Evaluate(everyScreen: true);
        })));
        subscriptions.Add(OnInputs(manager.StateChanged).Subscribe(_ => Run("state changed", OnStateChanged)));
        subscriptions.Add(OnInputs(manager.WindowMoved).Subscribe(w => Run($"window {w.Value:X} moved to another desktop", () => OnWindowMoved(w))));

        // The screens there are now, so the first ScreensChanged can tell which screen came back.
        Run("remember screens", RememberScreens);
        // The first evaluate reads the current desktop and, if it is tiled, tiles it.
        Refresh();
    }

    // A source observed on the UI thread's scheduler, or as it is when there is none (tests drive the Tiler
    // synchronously and need nothing re-scheduled).
    IObservable<T> OnInputs<T>(IObservable<T> source) => inputs is null ? source : source.ObserveOn(inputs);

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
        // `closing` belongs to this one event (Tiler.Resize.cs). NoteLeavers clears it when the evaluate below reaches
        // Keep; the finally clears it when it does not (a throw, or no board change at all), so a mark can never
        // outlive its event and turn a later minimize of the same window into a close (final review, M4).
        try { HandleWindowEvent(e); }
        finally { closing.Clear(); }
    }

    void HandleWindowEvent(WindowEvent e)
    {
        // A tile leaving its grid (it closed, went to the tray, or was minimized), noted with Windows' own time for
        // the event, while it is still on the board: Windows is about to activate whatever is next in z-order, and that
        // activation must not move the tile a new window splits (Tiler.Arrival.cs).
        if ((e.Kind is WindowEventKind.Disappeared or WindowEventKind.Hidden
             || (e.Kind == WindowEventKind.MinimizeChanged && surface.IsMinimized(e.Window.Handle)))
            && Board.ScreenOf(e.Window.Handle).HasValue)
            NoteLeft(e.Window.Handle, e.Time);

        // KEEP THE SPACE (change 3): a tile that CLOSES (Disappeared, or Hidden, which is how many apps close to the
        // tray) on a workspace whose switch is on leaves its leaf empty for the next window. Noted here, while it is
        // still on the board, so the evaluate below hands NoteLeavers a leaver marked as closed (Tiler.Resize.cs,
        // `closing`). A minimize is not a close and is never noted: it keeps change 1's rule.
        if (e.Kind is WindowEventKind.Disappeared or WindowEventKind.Hidden
            && Board.ScreenOf(e.Window.Handle).HasValue
            && Acting().Map(w => w.TileKeepsSpace).GetValueOrDefault(false))
            closing.Add(e.Window.Handle);

        if (e.Kind == WindowEventKind.Disappeared)
        {
            // A dead hwnd can be recycled for an unrelated window; forget what we knew about this one.
            given.Remove(e.Window.Handle);
            rounded.Remove(e.Window.Handle);
            maximized.Remove(e.Window.Handle);
            ForgetDrag(e.Window.Handle);
            away.Remove(e.Window.Handle);
            putBacks.Remove(e.Window.Handle);
            leftAlone.Remove(e.Window.Handle);
            ForgetUsed(e.Window.Handle);
        }
        if (e.Kind == WindowEventKind.Activated)
        {
            // Recorded first, for every window: the tile a new window splits is the one last used (Tiler.Arrival.cs).
            NoteUsed(e.Window.Handle, e.Time);
            RaiseTogether(e.Window.Handle);
            return;
        }
        // Remembered BEFORE the evaluate takes it out of the grid, so it can come back to its place.
        if (e.Kind == WindowEventKind.MinimizeChanged) NoteMinimized(e.Window.Handle);
        if (e.Kind is WindowEventKind.Appeared or WindowEventKind.Hidden or WindowEventKind.Disappeared or WindowEventKind.MinimizeChanged)
            Evaluate(everyScreen: false);

        // A window that OPENS maximized on a tiled workspace is restored into the slot it just joined. Petre:
        // "opening a new window in a tiled bar should automatically unmaximize it and put it into the tile". A
        // browser remembers being maximized and comes back that way, covering the grid it was meant to join. Only
        // on Appeared: a tile maximized by hand afterwards stays maximized, as the design says.
        if (e.Kind == WindowEventKind.Appeared
            && Acting().TryGetValue(out var workspace)
            && Board.ScreenOf(e.Window.Handle).HasValue
            && surface.IsMaximized(e.Window.Handle))
            Unmaximize(e.Window.Handle, $"{workspace.Name}: it opened maximized");
    }

    // The manager moved a window to another desktop (WorkspaceManager.WindowMoved). Nothing else announces
    // that: no window event and no LOCATIONCHANGE, because the window keeps its rectangle. The spec counts a
    // drop on one of the bar's workspace rows as the bar's gesture, and the grid it leaves closes up.
    // WorkspaceOf is asked once, for this window only:
    //   - on the board and no longer living here: it leaves and its screen re-tiles without it;
    //   - not on the board, living here now and eligible: it arrives like a new window (Tiler.Arrival.cs), into an
    //     opening or splitting the tile last used, which can be on another screen than the one it is on;
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
        Keep(workspace, Arrived(workspace, board.Join(to, window), [window]));
        // The screen it is on and the screen it landed on, which differ when the tile last used is on another one.
        Retile(workspace, [to, Board.ScreenOf(window).GetValueOrDefault(to)]);
    }

    // StateChanged fires on every persist and on several window events, and a full evaluate asks WorkspaceOf
    // (a COM DesktopOf call) of every known window on the UI thread. So a pulse evaluates only when one of the
    // facts in StateKey moved: the workspace here, its Tiled flag, the gap, Pro, or its stored layouts.
    // Changes to which windows are in the grid reach the Tiler through window events and Settled instead.
    //
    // Every screen is re-tiled. For the first four facts that is what Evaluate did anyway (each one changes or
    // clears `context`, which makes the re-tile full); the layouts change no window list, so without it "Even out
    // tiles" would re-tile nothing. The Tiler's own SetTileLayout (a split moved, or a layout refitted to a new count)
    // pulses too, but StoreLayout has already taken its layout into the key, so that pulse compares equal and
    // returns here without reading a single window (final review, item 2).
    //
    // A reset writes one trace line (the spec asks for one per reset; final review, item 7), when the same
    // workspace's layouts changed from outside the Tiler and came out even: some were stored and none are now (Even
    // out to the automatic layout, "Reset to grid", untile), or some split was uneven and every one is even now (Even
    // out that keeps a hand-made shape, which the split tree added). The Tiler itself never evens a layout out; its
    // own stores are already in the key and never reach this line.
    void OnStateChanged()
    {
        var current = CurrentStateKey();
        if (stateKey.HasValue && stateKey.Value == current) return;
        if (stateKey.TryGetValue(out var previous)
            && current.Workspace.HasValue && previous.Workspace == current.Workspace
            && EvenedOut(previous.Layouts, current.Layouts))
            manager.State.Workspaces.TryFirst(w => w.Id == current.Workspace.Value)
                .Tap(w => trace?.Invoke($"tile: {w.Name} sizes evened out"));
        Evaluate(everyScreen: true);
    }

    // The layouts went from something to nothing, or from some uneven split to every split even (see OnStateChanged).
    static bool EvenedOut(ValueList<ScreenTileLayout> before, ValueList<ScreenTileLayout> after) =>
        before != after
        && after.All(e => e.Layout.IsEven)
        && ((before.Count > 0 && after.Count == 0) || before.Any(e => !e.Layout.IsEven));

    StateKey CurrentStateKey()
    {
        var state = manager.State;
        var here = currentDesktop.Bind(desktop => state.Workspaces.TryFirst(w => w.DesktopId == desktop));
        return new StateKey(here.Map(w => w.Id), here.Map(w => w.Tiled).GetValueOrDefault(false),
            state.TileGap, isPro(), here.Map(w => w.TileLayouts).GetValueOrDefault([]));
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
    //   6. One or two adjacent edges dragged by hand: a split moves, or the window snaps back when no moved edge
    //      faces a neighbour (MovedASplit, Tiler.Resize.cs).
    //   7. Otherwise SettledMove.Decide: swap with the tile under its centre, back into its slot, or join the
    //      grid of the screen it is now on.
    void OnSettled(WindowHandle window)
    {
        // Taken first, whatever happens below: this Settled is the one any pending echo of our own Place was for.
        var echo = EchoOfOurPlace(window);

        // One line per Settled, naming every input the early returns below decide on. Without it a Settled that
        // ends in one of them leaves no trace at all, and "the tiler never heard the move" cannot be told apart
        // from "it heard and chose to do nothing" (Petre's live test, 2026-11-18: a hand resize stuck with no line).
        if (trace is not null)
            trace($"tile: {window.Value:X} settled: acting={Acting().Map(w => w.Name).GetValueOrDefault("none")}, "
                + $"board screen={Board.ScreenOf(window).Map(s => s.ToString()).GetValueOrDefault("none")}, "
                + $"frame={surface.VisibleFrameOf(window).Map(Show).GetValueOrDefault("none")}, "
                + $"given={(given.TryGetValue(window, out var g) ? Show(g.Accepted) : "none")}, echo={echo}, "
                + $"maximized={surface.IsMaximized(window)}, byHand={surface.ByHand(window)}");

        // A live drag (Tiler.Drag.cs): the dragged window's own Settled ends it and hides the preview; a
        // neighbour's, while the dragged window has not settled yet, is the echo of a live placement and waits.
        if (EndOrIgnoreDuringDrag(window))
        {
            trace?.Invoke($"tile: {window.Value:X} settled during another tile's drag, ignored");
            return;
        }
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
        var onScreen = board.On(screenNow);
        // The screen's layout, fitted ONCE for this Settled (final review, item 4). LayoutOn has side effects (it
        // takes the screen's pending leaver and may store), so the same fit lays out the slots here and is handed
        // to MovedASplit, which moves a split on exactly the layout these slots describe.
        var layout = screens.TryGetValue(screenNow, out var screen)
            ? Maybe.From(LayoutOn(workspace, screen, onScreen.Count))
            : Maybe<TileLayout>.None;
        var slots = layout.Map(fitted => SlotsOf(screens[screenNow], onScreen, fitted))
            .GetValueOrDefault(new Dictionary<WindowHandle, WindowRect>());

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

        // Step 6. A resize by hand is handled there in full; anything else goes on to the move rules unchanged.
        if (MovedASplit(workspace, window, frame, screenNow, screens, board, slots, layout, echo))
            return;

        // A drop by hand is judged where the pointer let go (SettledMove.DecideAt has the reason), on the screen the
        // pointer is on; anything else (a keyboard snap, an app moving itself) by the window's centre, as before.
        // A swap or a put-back happens on the window's OWN screen (DecideAt joins whenever the pointer is on
        // another), so with the pointer the decision is made against that screen's slots, which are not the frame's
        // screen's when a window held by its title bar hangs mostly over the next screen.
        var decidedOn = screenNow;
        var decidedSlots = slots;
        MoveOutcome outcome;
        if (surface.ByHand(window) && surface.Pointer().TryGetValue(out var pointer))
        {
            decidedOn = board.ScreenOf(window).GetValueOrDefault(screenNow);
            if (decidedOn != screenNow && screens.TryGetValue(decidedOn, out var own))
                decidedSlots = SlotsOf(own, board.On(decidedOn), ShownLayout(workspace, own, board.On(decidedOn).Count));
            var pointerOn = ScreenAt(screens.Values, pointer.X, pointer.Y).GetValueOrDefault(screenNow);
            outcome = SettledMove.DecideAt(window, pointer.X, pointer.Y, pointerOn, board, decidedSlots,
                SlotsThere(workspace, screens, pointerOn, decidedOn),
                screens.TryGetValue(decidedOn, out var homeScreen) ? OpeningOn(workspace, homeScreen) : Maybe<WindowRect>.None,
                InnerOf(screens, pointerOn));
        }
        // Win+Left / Win+Right (no button down, the frame exactly half the screen): a direction, not a place
        // (SettledMove.SnapSide has the measurement). The tile beside it on that side, found from the slots (a tree
        // has no columns to count), or the next screen when nothing lies on that side.
        else if (board.ScreenOf(window).Map(h => h == screenNow).GetValueOrDefault(false)
                 && screens.TryGetValue(screenNow, out var snapScreen)
                 && SettledMove.SnapSide(frame, snapScreen.WorkArea).TryGetValue(out var side)
                 // With no gap a tile IS half the screen; a window still at its own slot made no snap.
                 && !(slots.TryGetValue(window, out var ownSlot) && ownSlot == frame))
        {
            outcome = SettledMove.Snapped(window, side, screenNow, board, slots,
                ScreenBeside(screens.Values, snapScreen, side));
            trace?.Invoke($"tile: {window.Value:X} snapped {side} by keyboard: {outcome}");
        }
        else
            outcome = SettledMove.Decide(window, frame, screenNow, board, slots);

        switch (outcome)
        {
            // A swap on the same screen, or across two (each takes the other's screen and place): Exchange does both.
            // Keep, so a cross-screen exchange is seen by the leaver bookkeeping like any other board change.
            case MoveOutcome.Swap swap:
                var otherScreen = board.ScreenOf(swap.With).GetValueOrDefault(decidedOn);
                Keep(workspace, board.Exchange(window, swap.With));
                Retile(workspace, [decidedOn, otherScreen]);
                break;
            case MoveOutcome.BackToSlot:
                if (decidedSlots.TryGetValue(window, out var slot))
                    PutBack(window, slot, frame);
                break;
            // Dropped on another tile's edge zone: beside THAT tile on that side, at whatever depth of the tree it is
            // (TileArrange.Drop). The new layout is stored BEFORE the re-tile, so the re-tile reads it; the board takes
            // the new order. The target may be on another screen (dragged across): that screen takes the window in,
            // and the one it left gives its leaf up through Keep's leaver bookkeeping.
            case MoveOutcome.Beside beside when board.ScreenOf(beside.Target).TryGetValue(out var targetOn)
                                               && screens.TryGetValue(targetOn, out var besideScreen):
                var arranged = Rearranged(workspace, besideScreen, window, beside);
                var leftFrom = board.ScreenOf(window).GetValueOrDefault(targetOn);
                Keep(workspace, board.Into(targetOn, arranged.Order));
                StoreLayout(workspace, KeyOf(besideScreen), arranged.Layout)
                    .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {targetOn}: {window.Value:X} dropped {beside.Side} of {beside.Target.Value:X}, sizes {Show(arranged.Layout)}"))
                    .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {targetOn}: storing the new arrangement failed: {error}"));
                Retile(workspace, [leftFrom, targetOn]);
                break;
            // In the screen-edge band of the screen under the pointer (TileArrange.DropAtEdge): a full-height column or a
            // full-width row of that whole screen, on its own screen or dragged across. The new layout is stored FIRST,
            // so Keep's IntoOpenings already sees it (the drop closed any opening there, and the window is no arrival to
            // pull into one) and the re-tile lays it out; the screen it left gives its leaf up through Keep's leaver
            // bookkeeping. Should the arrangement not hold the window (it cannot, by SettledMove's guards: a band is
            // never offered on an empty screen or for a lone tile), it goes back to its slot rather than being lost.
            case MoveOutcome.AtEdge edge when screens.TryGetValue(edge.Screen, out var edgeScreen):
                var banded = Banded(workspace, edgeScreen, window, edge);
                if (!banded.Order.Contains(window))
                {
                    if (decidedSlots.TryGetValue(window, out var stay)) PutBack(window, stay, frame);
                    break;
                }
                var bandFrom = board.ScreenOf(window).GetValueOrDefault(edge.Screen);
                StoreLayout(workspace, KeyOf(edgeScreen), banded.Layout)
                    .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {edge.Screen}: {window.Value:X} dropped in the {edge.Side} band, sizes {Show(banded.Layout)}"))
                    .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {edge.Screen}: storing the band drop failed: {error}"));
                Keep(workspace, board.Into(edge.Screen, banded.Order));
                Retile(workspace, [bandFrom, edge.Screen]);
                break;
            // Into its own screen's opening (TileArrange.IntoOpening): the new order and layout, the layout stored first.
            case MoveOutcome.IntoOpening when screens.TryGetValue(decidedOn, out var openScreen):
                var into = TileArrange.IntoOpening(board.On(decidedOn), ShownLayout(workspace, openScreen, board.On(decidedOn).Count), window);
                Keep(workspace, board.Reordered(decidedOn, into.Order));
                StoreLayout(workspace, KeyOf(openScreen), into.Layout)
                    .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {decidedOn}: {window.Value:X} dropped into the opening, sizes {Show(into.Layout)}"))
                    .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {decidedOn}: storing the filled opening failed: {error}"));
                Retile(workspace, [decidedOn]);
                break;
            case MoveOutcome.JoinScreen join:
                var from = board.ScreenOf(window).GetValueOrDefault(join.Screen);
                // Through Keep, like every other board change, so the screen it left notes it as its leaver.
                Keep(workspace, board.Join(join.Screen, window));
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
            .Where(w => TilingGate.Acts(w, pro));
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

        // Forget the boards of workspaces the gate no longer acts on: untiled, or Pro gone.
        var dropped = boards.Keys
            .Where(id => !state.Workspaces.TryFirst(w => w.Id == id)
                .Map(w => TilingGate.Acts(w, pro)).GetValueOrDefault(false))
            .ToList();
        var droppedWindows = dropped.SelectMany(id => boards[id].Screens.Values).SelectMany(w => w).ToList();
        dropped.ForEach(id => boards.Remove(id));
        Forget(droppedWindows);
        NoteTiledJustNow(state);

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
        // The first fill matches windows to the cells of each screen's stored layout when it has one for that many
        // windows (TileBoard.FirstFill has the reason), so a hand-made shape survives a restart.
        var board = before is null ? TileBoard.FirstFill(windows, StoredCells(workspace)) : Reconciled(before, windows);
        // Minimized tiles that came back in this evaluate. Keep's IntoOpenings never pulls one into an opening: it went
        // back to its own place first (the design's precedence, step 1; final review, I2).
        IReadOnlySet<WindowHandle> returned = new HashSet<WindowHandle>();
        if (before is not null)
        {
            // The windows new to this board, in board order (screen by screen).
            var newcomers = board.Screens.OrderBy(s => s.Key).SelectMany(s => s.Value)
                .Where(w => !before.ScreenOf(w).HasValue)
                .ToList();
            // Split BEFORE ReturnedToTheirPlaces, which empties `away`: a minimized tile coming back to the screen it
            // left is not an arrival, it goes back to its old place (Tiler.Minimize.cs). Every other newcomer arrives,
            // one that was minimized elsewhere included, and its remembered place is forgotten.
            var reconciled = board;
            var returning = newcomers.Where(w => ComesBack(w, workspace, reconciled.ScreenOf(w))).ToList();
            var arrivals = newcomers.Where(w => !returning.Contains(w)).ToList();
            arrivals.ForEach(w => away.Remove(w));
            returned = returning.ToHashSet();

            // LEAVES FIRST (final review, C2). When windows left AND windows came in one evaluate (both happened while
            // the user was on another workspace, or a window became tileable in the evaluate a close caused), the
            // leaves are settled before anything arrives: the board without the newcomers is kept, which notes each
            // screen's leaver, and the screens that lost a window are fitted, which takes the leaver's leaf out (or
            // keeps it as the opening, change 3) and stores the result. Only then do the newcomers arrive, into the
            // layout the leave left: a kept space is an opening the first arrival fills (the design's precedence, step
            // 2), and a split halves a tile of the shape the user sees. Placed first, the arrival used to split a layout
            // the leaver was still in, and the leaver's leaf was then taken from a layout that had changed under it.
            var stayed = newcomers.Aggregate(board, (b, w) => b.Leave(w));
            if (newcomers.Count > 0 && before.Screens.Any(s => s.Value.Any(w => !stayed.On(s.Key).Contains(w))))
            {
                Keep(workspace, stayed);
                SettleLeaves(workspace, stayed);
            }

            // A minimized tile that just came back takes its old place first, not the end (Tiler.Minimize.cs, step 1);
            // then every other newcomer, back on the screen it opened on, fills an opening or splits the tile last used
            // (Tiler.Arrival.cs, steps 2 to 4). The first fill is left alone: it orders the windows by where they
            // already are.
            var placed = ReturnedToTheirPlaces(workspace, stayed, returning);
            board = Arrived(workspace,
                arrivals.Aggregate(placed, (b, w) => b.Join(reconciled.ScreenOf(w).GetValueOrDefault(surface.ScreenOf(w)), w)),
                arrivals);
        }
        Keep(workspace, board, returned);
        RestoreIfJustTiled(workspace, board);

        var changed = full || before is null
            ? board.Screens.Keys.ToList()
            : board.Screens.Keys.Union(before.Screens.Keys)
                .Where(s => !board.On(s).SequenceEqual(before.On(s)))
                .ToList();
        Retile(workspace, changed);
    }

    // For the first fill: the window rectangles of the layout stored for screen `number`, in slot order, when it lays
    // out exactly `count` windows as it is (one leaf per window, or one each plus its opening); None otherwise, and the
    // first fill sorts that screen by position. Read without side effects (no pending leaver, nothing stored).
    Func<int, int, Maybe<IReadOnlyList<WindowRect>>> StoredCells(Workspace workspace)
    {
        var screens = surface.Screens().ToDictionary(s => s.Number);
        return (number, count) => screens.TryGetValue(number, out var screen)
            ? StoredOn(workspace.Id, KeyOf(screen))
                .Where(stored => stored.HasOpeningFor(count) || (stored.Opening is null && stored.Count == count))
                .Map(stored => GridLayout.Arrange(screen.WorkArea, count, GapOn(screen), stored))
            : Maybe<IReadOnlyList<WindowRect>>.None;
    }

    // Which workspaces turned tiled since the last evaluate (see `restoreOnArrival`). The flag alone, not the gate:
    // Pro coming back is not Petre activating tiling on a workspace.
    void NoteTiledJustNow(AppState state)
    {
        var tiledNow = state.Workspaces.Where(w => w.Tiled).Select(w => w.Id).ToHashSet();
        tiledLast.Tap(last => tiledNow.Where(id => !last.Contains(id)).ToList().ForEach(id => restoreOnArrival.Add(id)));
        restoreOnArrival.RemoveWhere(id => !tiledNow.Contains(id));
        tiledLast = Maybe.From<IReadOnlySet<Guid>>(tiledNow);
    }

    // The board's maximized windows are un-maximized, once, the first time the Tiler acts on a workspace that
    // was just turned tiled.
    void RestoreIfJustTiled(Workspace workspace, TileBoard board)
    {
        if (!restoreOnArrival.Remove(workspace.Id)) return;
        board.Screens.Values.SelectMany(w => w)
            .Where(surface.IsMaximized)
            .ToList()
            .ForEach(w => Unmaximize(w, $"{workspace.Name}: tiling was just turned on"));
    }

    // Un-maximizes a window on the grid so it drops into its slot. Noted in `maximized` HERE, before the restore,
    // and not left to Retile noticing it: the restore is asynchronous in Windows but need not be, and if the window
    // were already restored when Retile looked, its Settled would be judged as a drag and could swap with whatever
    // tile it settled over, instead of going into its own slot.
    void Unmaximize(WindowHandle window, string why)
    {
        trace?.Invoke($"tile: {window.Value:X} un-maximized, {why}");
        maximized.Add(window);
        PerWindow(window, "restore", () => surface.Restore(window));
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
            // A Place whose echo never came back before tiling went off (the hook goes with it, so it never will).
            // Left in, the mark would swallow the window's first real drag after tiling returns as "our own echo"
            // and snap it back (final review, item 5).
            awaitingEcho.Remove(w);
        });
    }

    // Stores a workspace's new board and does what every board change owes the windows on it. `returned` names the
    // minimized tiles that just came back to their own places (Evaluate), which IntoOpenings must leave there.
    void Keep(Workspace workspace, TileBoard board, IReadOnlySet<WindowHandle>? returned = null)
    {
        board = IntoOpenings(workspace, board, returned ?? new HashSet<WindowHandle>());

        // Before the old board is replaced: the leaf of a window leaving a screen decides which leaf goes.
        NoteLeavers(workspace.Id, board);
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

    // A window that JOINS a screen with an opening (TileLayout.Opening) goes into it rather than to the end. Petre: "keep
    // the opening for the new window to jump into". Every way onto a screen comes through Keep (a new window, one moved
    // here from another workspace, one dragged over from another screen), so this one place covers them all. The
    // opening is filled only when the windows that stayed are exactly the ones it was laid out around; with more
    // arriving at once the first takes it and the rest join at the end. The filled layout is stored before anything is
    // laid out, so the re-tile reads it.
    //
    // A minimized tile that just came back (`returned`) is no arrival here: it is already in its own place, and the
    // opening was laid out around it (the stored layout it came back to holds the opening past its leaf). It counts with the
    // windows that stayed, so the opening is still offered to the next new window (the design's precedence, step 1
    // before step 2; final review, I2: it used to be pulled out of its place into the kept space).
    TileBoard IntoOpenings(Workspace workspace, TileBoard after, IReadOnlySet<WindowHandle> returned)
    {
        var before = boards.TryGetValue(workspace.Id, out var b) ? b : TileBoard.Empty;
        var screens = surface.Screens().ToDictionary(s => s.Number);
        return after.Screens.Keys.ToList().Aggregate(after, (board, n) =>
        {
            var windows = board.On(n);
            var stayed = windows.Where(w => before.On(n).Contains(w) || returned.Contains(w)).ToList();
            var arrived = windows.Where(w => !stayed.Contains(w)).ToList();
            if (arrived.Count == 0
                || !screens.TryGetValue(n, out var screen)
                || !StoredOn(workspace.Id, KeyOf(screen)).TryGetValue(out var stored)
                || !stored.HasOpeningFor(stayed.Count))
                return board;

            var (order, filled) = TileArrange.Filled(stayed, stored, arrived[0]);
            StoreLayout(workspace, KeyOf(screen), filled)
                .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {n}: {arrived[0].Value:X} took the opening at slot {stored.Opening}"))
                .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {n}: storing the filled opening failed: {error}"));
            return board.Reordered(n, order.Concat(arrived.Skip(1)).ToList());
        });
    }

    // The remembered board brought up to date: windows that no longer qualify leave (the rest close up),
    // windows on a screen that has gone join the screen they are on now, and windows new to the grid join at
    // the end of their screen, in the order the manager knows them (which is the order they appeared). Evaluate
    // then hands those newcomers to Arrived (Tiler.Arrival.cs), which places each one where it really goes.
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

        var slots = SlotsOf(workspace, screen, windows);
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

    // The slot of each window on a screen, by window; empty when the grid does not fit (see RetileScreen). The grid
    // is the layout stored for this screen (LayoutOn: the automatic layout when there is none, which is the equal grid
    // exactly), fitted to this many windows, the windows taking its leaves in slot order around any opening.
    IReadOnlyDictionary<WindowHandle, WindowRect> SlotsOf(Workspace workspace, TileScreen screen, IReadOnlyList<WindowHandle> windows) =>
        SlotsOf(screen, windows, LayoutOn(workspace, screen, windows.Count));

    // The same, with a layout the caller already fitted (OnSettled, which also hands it to MovedASplit).
    IReadOnlyDictionary<WindowHandle, WindowRect> SlotsOf(TileScreen screen, IReadOnlyList<WindowHandle> windows, TileLayout layout)
    {
        var tiles = GridLayout.Arrange(screen.WorkArea, windows.Count, GapOn(screen), layout);
        return tiles.Any(t => t.Width <= 0 || t.Height <= 0)
            ? new Dictionary<WindowHandle, WindowRect>()
            : windows.Zip(tiles).ToDictionary(p => p.First, p => p.Second);
    }

    // The gap is set at 100% scaling and scaled by the screen's DPI, so it looks the same on every screen.
    int GapOn(TileScreen screen) =>
        (int)Math.Round(manager.State.TileGap * screen.Dpi / 96.0, MidpointRounding.AwayFromZero);

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
                AwaitEcho(window, frame, accepted);
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
        // The hook is out of context, so its callbacks arrive through the message queue of the thread that installs
        // it. Named here so a hook installed on a thread with no message loop (and so never heard) shows in the trace.
        trace?.Invoke($"tile: watching {windows.Count} window(s) from thread {Environment.CurrentManagedThreadId} "
            + $"({Thread.CurrentThread.GetApartmentState()}, {(Thread.CurrentThread.IsThreadPoolThread ? "pool" : "own")})");
        surface.Watch(windows);
    }

    static string Show(WindowRect r) => $"{r.Left},{r.Top},{r.Right},{r.Bottom}";
}
