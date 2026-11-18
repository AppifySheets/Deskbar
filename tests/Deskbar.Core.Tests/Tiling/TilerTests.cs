using System.Reactive;
using System.Reactive.Concurrency;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The Tiler: when and where a tiled workspace's windows move. Driven entirely through fakes: a started
// WorkspaceManager over FakeDesktops/FakeMonitor (as FoldTests builds one) and a FakeTileSurface standing in
// for Windows. Every test reads the outcome off the surface: what was placed, where, and what was watched.
public sealed class TilerTests : IDisposable
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeTileSurface surface = new();
    readonly List<string> traced = [];

    // Alpha is the desktop being looked at; Beta is somewhere else.
    readonly Workspace alpha = new(Guid.NewGuid(), "Alpha", Guid.NewGuid());
    readonly Workspace beta = new(Guid.NewGuid(), "Beta", Guid.NewGuid());

    // Read through a lambda by the Tiler, so a test can make Pro lapse by flipping it.
    bool pro = true;

    // The Tiler's clock, moved by hand for the put-back backstop.
    DateTimeOffset time = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    // The windows whose drop on a bar row is pending, as the bar would report them (FloatingBar's header drop).
    readonly HashSet<WindowHandle> dropping = [];

    WorkspaceManager manager = null!;
    Tiler tiler = null!;

    static readonly WindowRect Area = new(0, 0, 1920, 1040);
    static readonly WindowRect Area2 = new(1920, 0, 3840, 1040);
    const int Gap = AppState.DefaultTileGap;

    public void Dispose() => tiler?.Dispose();

    // --- building the world ----------------------------------------------------------------------------

    static WindowHandle W(int n) => new(n);

    static WindowInfo Info(WindowHandle h) => new(h, 1000 + (int)h.Value, "app", null, $"window {h.Value}", null);

    // A window that already exists, on `workspace`'s desktop, at a position that orders it by its number
    // (TileBoard.FirstFill sorts by Left), and nowhere near a slot so the first tidy-up has to move it.
    void Exists(WindowHandle h, Workspace workspace, int screen = 1)
    {
        monitor.InitialWindows.Add(Info(h));
        desktops.WindowPlacements[h] = workspace.DesktopId!.Value;
        surface.ScreenOfWindow[h] = screen;
        var left = (screen == 1 ? Area.Left : Area2.Left) + 50 * (int)h.Value;
        surface.Frames[h] = new WindowRect(left, 30, left + 300, 330);
    }

    // A window opened while the Tiler is running.
    void Opens(WindowHandle h, Workspace workspace, int screen = 1)
    {
        Exists(h, workspace, screen);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Info(h)));
    }

    void Closes(WindowHandle h)
    {
        monitor.InitialWindows.RemoveAll(w => w.Handle == h);
        monitor.Dead.Add(h);
        desktops.WindowPlacements.Remove(h);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Info(h)));
    }

    void Minimizes(WindowHandle h, bool minimized)
    {
        if (minimized) surface.Minimized.Add(h); else surface.Minimized.Remove(h);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.MinimizeChanged, Info(h)));
    }

    // The user (or a snap, or the app) left the window at `frame` and let go.
    void Settles(WindowHandle h, WindowRect frame, int screen = 1)
    {
        surface.Frames[h] = frame;
        surface.ScreenOfWindow[h] = screen;
        surface.SettledSubject.OnNext(h);
    }

    // Starts the manager and the Tiler with Alpha current. Alpha starts tiled, the experiment on and Pro on,
    // unless a test says otherwise.
    void Start(bool alphaTiled = true, bool betaTiled = false, bool experiment = true, IScheduler? inputs = null)
    {
        new[] { alpha, beta }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = alpha.DesktopId!.Value;
        store.Stored = AppState.Empty with
        {
            Workspaces = [alpha with { Tiled = alphaTiled }, beta with { Tiled = betaTiled }],
            ExperimentalTiling = experiment,
        };
        manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        tiler = new Tiler(manager, desktops, monitor, surface, () => pro, traced.Add, () => time, dropping.Contains, inputs);
        tiler.Start();
    }

    void SwitchTo(Workspace workspace)
    {
        desktops.CurrentDesktopId = workspace.DesktopId!.Value;
        desktops.CurrentChangedSubject.OnNext(workspace.DesktopId!.Value);
    }

    IReadOnlyList<WindowRect> Grid(int count, WindowRect? area = null) => GridLayout.Arrange(area ?? Area, count, Gap);

    // Every window's frame, in board order, against the grid for that many windows.
    void AssertTiled(IReadOnlyList<WindowHandle> order, WindowRect? area = null, int screen = 1)
    {
        Assert.Equal(order, tiler.Board.On(screen));
        Assert.Equal(Grid(order.Count, area), order.Select(w => surface.Frames[w]).ToList());
    }

    IReadOnlyList<WindowHandle> PlacedWindows => surface.Placed.Select(p => p.Window).ToList();

    // --- threading ------------------------------------------------------------------------------------------

    // Windows raises its desktop change on a thread of its own (measured on Petre's machine, 2026-11-18: the Tiler
    // installed its hook from thread 14, MTA, with no message loop). The surface's LOCATIONCHANGE hook is heard only
    // on the thread that installs it, so a Watch made there left every tile deaf: a hand resize stuck, a moved tile
    // stayed where it was dropped. Every input has to reach the Tiler on its own scheduler (the UI thread in the app).
    [Fact]
    public async Task A_desktop_change_from_another_thread_is_handled_on_the_tilers_scheduler()
    {
        Exists(W(1), alpha);
        Exists(W(2), beta);
        using var ui = new EventLoopScheduler();
        var uiThread = OnScheduler(ui, () => Environment.CurrentManagedThreadId);
        Start(betaTiled: true, inputs: ui);
        surface.WatchThreads.Clear();

        // Raised on a pool thread, as Windows raises it on its own.
        await Task.Run(() => SwitchTo(beta));
        OnScheduler(ui, () => 0);

        Assert.Contains(surface.Watched, set => set.SetEquals([W(2)]));
        Assert.NotEmpty(surface.WatchThreads);
        Assert.All(surface.WatchThreads, thread => Assert.Equal(uiThread, thread));
    }

    // Runs `work` on `scheduler` and waits for it, so everything queued there before it has run too.
    static T OnScheduler<T>(IScheduler scheduler, Func<T> work)
    {
        var result = default(T);
        using var done = new ManualResetEventSlim();
        scheduler.Schedule(() => { result = work(); done.Set(); });
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        return result!;
    }

    // --- turning it on and off ---------------------------------------------------------------------------

    [Fact]
    public void Turning_tiling_on_tiles_the_current_desktop()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start(alphaTiled: false);
        Assert.Empty(surface.Placed);

        Assert.True(manager.SetWorkspaceTiled(alpha.Id, true).IsSuccess);

        Assert.Equal(
            new[] { W(1), W(2), W(3) }.Zip(Grid(3)).Select(p => (p.First, p.Second)).ToList(),
            surface.Placed);
        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Assert.Equal([W(1), W(2), W(3)], surface.Rounded);
    }

    [Fact]
    public void Nothing_tiles_without_pro()
    {
        pro = false;
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        tiler.Refresh();
        Opens(W(3), alpha);

        Assert.Empty(surface.Placed);
        Assert.Empty(tiler.Board.Screens);
    }

    [Fact]
    public void Pro_lapsing_stops_tiling_and_moves_nothing()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.NotEmpty(surface.Placed);
        surface.Placed.Clear();

        pro = false;
        tiler.Refresh();
        Opens(W(3), alpha);

        Assert.Empty(surface.Placed);
        Assert.Empty(tiler.Board.Screens);
        Assert.Empty(surface.Watched[^1]);
    }

    [Fact]
    public void Nothing_tiles_and_nothing_is_watched_while_the_experiment_is_off()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start(experiment: false);

        Opens(W(3), alpha);
        tiler.Refresh();
        manager.SetTileGap(12);

        Assert.Empty(surface.Placed);
        Assert.All(surface.Watched, w => Assert.Empty(w));
    }

    [Fact]
    public void Turning_the_experiment_off_moves_nothing()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        Assert.True(manager.SetExperimentalTiling(false).IsSuccess);
        Opens(W(3), alpha);
        Closes(W(1));

        Assert.Empty(surface.Placed);
        Assert.Empty(surface.Watched[^1]);
    }

    [Fact]
    public void Untiling_moves_nothing()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        Assert.True(manager.SetWorkspaceTiled(alpha.Id, false).IsSuccess);
        Opens(W(3), alpha);
        Closes(W(1));

        Assert.Empty(surface.Placed);
        Assert.Empty(tiler.Board.Screens);
        Assert.Empty(surface.Watched[^1]);
    }

    [Fact]
    public void Watch_is_given_the_tiled_windows_and_emptied_when_leaving_a_tiled_desktop()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.Equal(new HashSet<WindowHandle> { W(1), W(2) }, surface.Watched[^1]);

        SwitchTo(beta);

        Assert.Empty(surface.Watched[^1]);
    }

    // --- keeping it tidy ---------------------------------------------------------------------------------

    [Fact]
    public void A_new_window_joins_at_the_end()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        Opens(W(3), alpha);

        AssertTiled([W(1), W(2), W(3)]);
        Assert.Contains(W(3), surface.Rounded);
    }

    [Fact]
    public void A_closed_window_gives_its_slot_back()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        Closes(W(1));

        AssertTiled([W(2), W(3)]);
    }

    // It used to rejoin at the END (the first design). Petre: "i'd like to bring back the window from the top back to
    // the top", so it now comes back to the slot it left (Tiler.Minimize.cs).
    [Fact]
    public void A_minimized_window_leaves_and_rejoins_where_it_was()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        Minimizes(W(1), true);
        AssertTiled([W(2), W(3)]);

        Minimizes(W(1), false);
        AssertTiled([W(1), W(2), W(3)]);
    }

    [Fact]
    public void A_maximized_window_keeps_its_slot_and_is_not_placed()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        // Maximized: the surface reports it through Settled, with the whole work area as its frame.
        surface.Maximized.Add(W(2));
        Settles(W(2), Area);
        Assert.Empty(surface.Placed);
        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));

        // A fourth window re-tiles the screen; W(2) keeps slot 1 and is left maximized.
        Opens(W(4), alpha);

        Assert.DoesNotContain(W(2), PlacedWindows);
        Assert.Equal([W(1), W(2), W(3), W(4)], tiler.Board.On(1));
        var grid = Grid(4);
        Assert.Equal(grid[0], surface.Frames[W(1)]);
        Assert.Equal(grid[2], surface.Frames[W(3)]);
        Assert.Equal(grid[3], surface.Frames[W(4)]);
        Assert.Equal(Area, surface.Frames[W(2)]);
    }

    // Maximized before tiling began, so restoring it puts it back where it used to be, which here is over
    // W(3)'s tile. Spec: "When it is restored, it goes back into its slot", so no swap.
    [Fact]
    public void A_restored_window_goes_back_into_its_slot()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.Maximized.Add(W(2));
        surface.Frames[W(2)] = Area;
        Start();
        Assert.DoesNotContain(W(2), PlacedWindows);
        var order = tiler.Board.On(1);

        surface.Maximized.Remove(W(2));
        Settles(W(2), Grid(3)[order.ToList().IndexOf(W(3))]);

        AssertTiled(order);
    }

    // Petre, turning tiling on over a maximized browser: "activating tile windows should unmaximize all windows".
    // Turning it on restores every maximized window there, and each drops into its slot when it settles.
    [Fact]
    public void Turning_tiling_on_restores_maximized_windows_into_their_slots()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.Maximized.Add(W(2));
        Start(alphaTiled: false);

        Assert.True(manager.SetWorkspaceTiled(alpha.Id, true).IsSuccess);

        Assert.Equal([W(2)], surface.Restored);
        // The restore is asynchronous: the window settles wherever it used to be, and goes into its slot.
        Settles(W(2), new WindowRect(400, 300, 900, 700));
        AssertTiled([W(1), W(2), W(3)]);
    }

    // Un-maximizing a window on another desktop makes Windows follow it there (spec, anti-goals), so a workspace
    // tiled from afar is restored only when Petre arrives at it, and only that once.
    [Fact]
    public void Tiling_a_workspace_you_are_not_on_restores_its_maximized_windows_when_you_arrive()
    {
        Exists(W(1), alpha);
        new[] { 2, 3 }.ToList().ForEach(n => Exists(W(n), beta));
        surface.Maximized.Add(W(3));
        Start();

        Assert.True(manager.SetWorkspaceTiled(beta.Id, true).IsSuccess);
        Assert.Empty(surface.Restored);

        SwitchTo(beta);
        Assert.Equal([W(3)], surface.Restored);

        // Maximized again by hand and the next visit leaves it alone: the restore belongs to turning tiling on.
        surface.Maximized.Add(W(3));
        SwitchTo(alpha);
        SwitchTo(beta);
        Assert.Equal([W(3)], surface.Restored);
    }

    // Petre: "opening a new window in a tiled bar should automatically unmaximize it and put it into the tile". A
    // browser that remembers being maximized opens maximized; on a tiled workspace it is restored into its slot.
    // Maximizing a tile by hand later still keeps it maximized (A_maximized_window_keeps_its_slot_and_is_not_placed).
    [Fact]
    public void A_window_that_opens_maximized_is_restored_into_its_slot()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        surface.Maximized.Add(W(3));
        Opens(W(3), alpha);

        Assert.Equal([W(3)], surface.Restored);
        Settles(W(3), new WindowRect(400, 300, 900, 700));
        AssertTiled([W(1), W(2), W(3)]);
    }

    // --- a drop by hand is judged at the pointer, and previewed before it ------------------------------------

    // Petre, dragging a tile down by its title bar: "it still goes back to the right". Its centre was below the
    // screen, under no tile. A drop by hand is judged where the pointer is. Four tiles fill columns first:
    // W1 top left, W2 bottom left, W3 top right, W4 bottom right.
    [Fact]
    public void A_drop_by_hand_swaps_with_the_tile_under_the_pointer_even_with_the_centre_off_screen()
    {
        new[] { 1, 2, 3, 4 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var grid = Grid(4);

        // W3 held by its title bar and pulled down over W2: most of it is below the screen.
        surface.PointerAt = ((grid[1].Left + grid[1].Right) / 2, (grid[1].Top + grid[1].Bottom) / 2);
        surface.MovedByHand.Add(W(3));
        Settles(W(3), new WindowRect(grid[1].Left, grid[1].Top, grid[1].Left + 900, grid[1].Top + 1600));

        AssertTiled([W(1), W(3), W(2), W(4)]);
    }

    // Petre: "i still can't drag below another window". Two tiles side by side; the right one is dropped on the
    // bottom quarter of the left one: one column, the left window on top, the dropped one under it. The shape is
    // kept, so the next re-tile does not put them back side by side.
    [Fact]
    public void A_drop_on_the_bottom_of_a_tile_stacks_the_window_under_it_and_keeps_that()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var grid = Grid(2);

        surface.PointerAt = ((grid[0].Left + grid[0].Right) / 2, grid[0].Bottom - 20);
        surface.MovedByHand.Add(W(2));
        Settles(W(2), new WindowRect(200, 600, 900, 1500));
        surface.MovedByHand.Remove(W(2));

        var stacked = GridLayout.Arrange(Area, 2, Gap, TileSizes.Equal([2]));
        Assert.Equal([W(1), W(2)], tiler.Board.On(1));
        Assert.Equal(stacked, new[] { W(1), W(2) }.Select(w => surface.Frames[w]).ToList());
        Assert.Equal([2], manager.State.Workspaces.Single(w => w.Id == alpha.Id).TileSizes.Single().Sizes.Shape);

        // A refresh lays them out the same way.
        tiler.Refresh();
        Assert.Equal(stacked, new[] { W(1), W(2) }.Select(w => surface.Frames[w]).ToList());
    }

    // Petre: "windows + left right keys don't seem to move windows to other screen". Win+Left snaps the window to
    // the left half of its screen with no button down; the Tiler reads that as a direction. From the left column of
    // the right screen it goes to the left screen; from the right column it swaps one column left.
    [Fact]
    public void Win_Left_from_the_leftmost_column_moves_the_tile_to_the_screen_on_the_left()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha);
        new[] { 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha, screen: 2));
        Start();

        // W2 is the left tile of screen 2; Windows snaps it to the left half of that screen.
        Settles(W(2), new WindowRect(Area2.Left, Area2.Top, Area2.Left + Area2.Width / 2, Area2.Bottom), screen: 2);

        Assert.Equal([W(1), W(2)], tiler.Board.On(1));
        Assert.Equal([W(3)], tiler.Board.On(2));
    }

    // A right-column tile nudged a few pixels by its own app is NOT Win+Right, though it is close to the right half
    // of the screen: it goes back to its slot and never leaves for the next screen.
    [Fact]
    public void A_tile_nudged_by_its_app_is_not_read_as_a_keyboard_snap()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var right = Grid(2)[1];

        Settles(W(2), right with { Left = right.Left + 5, Right = right.Right + 5 });

        AssertTiled([W(1), W(2)]);
        Assert.Empty(tiler.Board.On(2));
    }

    [Fact]
    public void Win_Left_from_an_inner_column_swaps_with_the_tile_beside_it()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        // W2, the right tile, snapped to the left half: it trades places with W1.
        Settles(W(2), new WindowRect(Area.Left, Area.Top, Area.Left + Area.Width / 2, Area.Bottom));

        AssertTiled([W(2), W(1)]);
    }

    // Petre: "if i change to a window that's part of a tile, it should activate all other tiled windows", and "if
    // another window, untiled, maximized, is another one, it would not bring up anything". Activating a tile brings
    // every other tile of the workspace up just behind it; activating anything else brings up nothing.
    void Activates(WindowHandle h) => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(h)));

    // Petre: "minimizing and maximizing windows shouldn't change their position, if possible. i'd like to bring back
    // the window from the top back to the top", "after it restores". A minimized tile gives its slot up as before,
    // and coming back it takes the same place again: exactly, when the screen still holds the same other windows.
    [Fact]
    public void A_minimized_tile_comes_back_to_the_same_place()
    {
        new[] { 1, 2, 3, 4 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        // W3 is the top right of the 2x2 grid.
        Minimizes(W(3), true);
        AssertTiled([W(1), W(2), W(4)]);

        Minimizes(W(3), false);
        AssertTiled([W(1), W(2), W(3), W(4)]);
    }

    // The same with a shape made by a drop: the top of a two-window stack comes back to the top of the stack, not
    // beside the other window.
    [Fact]
    public void A_minimized_tile_comes_back_into_its_stack()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.True(manager.SetTileSizes(alpha.Id, "0,0,1920,1040", TileSizes.Equal([2])).IsSuccess);
        var stacked = GridLayout.Arrange(Area, 2, Gap, TileSizes.Equal([2]));
        Assert.Equal(stacked, new[] { W(1), W(2) }.Select(w => surface.Frames[w]).ToList());

        Minimizes(W(1), true);
        Minimizes(W(1), false);

        Assert.Equal([W(1), W(2)], tiler.Board.On(1));
        Assert.Equal(stacked, new[] { W(1), W(2) }.Select(w => surface.Frames[w]).ToList());
    }

    // The screen changed while it was away (another window opened): it goes back to its old position in the order,
    // not to the end.
    [Fact]
    public void A_minimized_tile_keeps_its_place_in_the_order_when_the_screen_changed_meanwhile()
    {
        new[] { 1, 2, 3, 4 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        Minimizes(W(2), true);
        Opens(W(5), alpha);
        Minimizes(W(2), false);

        Assert.Equal([W(1), W(2), W(3), W(4), W(5)], tiler.Board.On(1));
    }

    [Fact]
    public void Activating_a_tile_brings_the_other_tiles_up_behind_it()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        Activates(W(2));

        var raised = Assert.Single(surface.Raised);
        Assert.Equal(W(2), raised.Front);
        Assert.Equal([W(1), W(3)], raised.Windows);
    }

    [Fact]
    public void Activating_a_window_that_is_not_a_tile_brings_up_nothing()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Exists(W(9), alpha);
        desktops.PinnedWindows.Add(W(9)); // floats: pinned windows never tile
        Start();

        Activates(W(9));

        Assert.Empty(surface.Raised);
    }

    // A maximized tile already covers the screen; bringing the others up under it would change nothing to see.
    // And a maximized tile left behind stays where it is, so the grid on top of it is not hidden.
    [Fact]
    public void A_maximized_tile_neither_raises_the_others_nor_is_raised()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Maximized.Add(W(3));
        Settles(W(3), Area);

        Activates(W(3));
        Assert.Empty(surface.Raised);

        Activates(W(1));
        Assert.Equal([W(2)], Assert.Single(surface.Raised).Windows);
    }

    // Without a mouse (a keyboard snap, an app moving itself) the centre still decides, as before.
    [Fact]
    public void A_move_without_the_mouse_is_still_judged_by_its_centre()
    {
        new[] { 1, 2, 3, 4 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var grid = Grid(4);

        surface.PointerAt = ((grid[1].Left + grid[1].Right) / 2, (grid[1].Top + grid[1].Bottom) / 2);
        Settles(W(3), new WindowRect(grid[1].Left, grid[1].Top, grid[1].Left + 900, grid[1].Top + 1600));

        AssertTiled([W(1), W(2), W(3), W(4)]);
    }

    // While the tile is moved, the surface shows where it will land (filled) and where the tile it swaps with goes
    // (outline). Letting go hides it.
    [Fact]
    public void Moving_a_tile_previews_the_swap_under_the_pointer_and_the_drop_hides_it()
    {
        new[] { 1, 2, 3, 4 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var grid = Grid(4);

        surface.Settling.Add(W(3));
        surface.PointerAt = ((grid[1].Left + grid[1].Right) / 2, (grid[1].Top + grid[1].Bottom) / 2);
        surface.Frames[W(3)] = new WindowRect(grid[1].Left, grid[1].Top, grid[1].Left + 900, grid[1].Top + 1600);
        surface.MovingSubject.OnNext(W(3));

        Assert.Equal([new TilePreview(grid[1], Dragged: true), new TilePreview(grid[2], Dragged: false)], surface.Previews.Last());

        surface.Settling.Remove(W(3));
        surface.MovedByHand.Add(W(3));
        Settles(W(3), surface.Frames[W(3)]);

        Assert.Empty(surface.Previews.Last());
        AssertTiled([W(1), W(3), W(2), W(4)]);
    }

    // A workspace that was already tiled when Deskbar started was not turned on just now: its maximized windows
    // stay maximized, as the user left them.
    [Fact]
    public void A_restart_does_not_restore_maximized_windows()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.Maximized.Add(W(2));
        Start();

        Assert.Empty(surface.Restored);
    }

    [Fact]
    public void A_maximized_window_that_closes_frees_its_slot()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Maximized.Add(W(2));
        Settles(W(2), Area);

        Closes(W(2));

        AssertTiled([W(1), W(3)]);
    }

    // --- which windows tile ------------------------------------------------------------------------------

    [Fact]
    public void Borrowed_and_pinned_windows_float()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        // On every desktop, so it is on this one too, but it lives nowhere: WorkspaceOf is None.
        Exists(W(9), alpha);
        desktops.PinnedWindows.Add(W(9));
        Start();
        Assert.True(manager.WorkspaceOf(W(9)).HasNoValue);

        Opens(W(3), alpha);

        Assert.DoesNotContain(W(9), PlacedWindows);
        Assert.DoesNotContain(W(9), tiler.Board.On(1));
        AssertTiled([W(1), W(2), W(3)]);
    }

    [Fact]
    public void Windows_that_cannot_tile_float()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.CannotTile.Add(W(2));
        Start();

        Assert.DoesNotContain(W(2), PlacedWindows);
        AssertTiled([W(1), W(3)]);
    }

    [Fact]
    public void Another_desktop_is_never_touched()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), beta));
        Start(alphaTiled: false);

        Assert.True(manager.SetWorkspaceTiled(beta.Id, true).IsSuccess);
        Opens(W(3), beta);
        tiler.Refresh();

        Assert.Empty(surface.Placed);
        Assert.All(surface.Watched, w => Assert.Empty(w));
    }

    [Fact]
    public void Arriving_at_a_tiled_workspace_tiles_what_changed_while_away()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), beta));
        Start(alphaTiled: false, betaTiled: true);
        Opens(W(3), beta);
        Assert.Empty(surface.Placed);

        SwitchTo(beta);

        AssertTiled([W(1), W(2), W(3)]);
        Assert.Equal(new HashSet<WindowHandle> { W(1), W(2), W(3) }, surface.Watched[^1]);
    }

    [Fact]
    public void Leaving_and_coming_back_keeps_the_slot_order()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Settles(W(1), Grid(3)[2]);
        AssertTiled([W(3), W(2), W(1)]);

        SwitchTo(beta);
        SwitchTo(alpha);

        AssertTiled([W(3), W(2), W(1)]);
    }

    // --- by hand -----------------------------------------------------------------------------------------

    [Fact]
    public void Settled_over_another_tile_swaps()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        // W(1) dropped with its centre over W(3)'s tile.
        var over = Grid(3)[2];
        Settles(W(1), over with { Left = over.Left + 40, Right = over.Right - 40 });

        AssertTiled([W(3), W(2), W(1)]);
    }

    // The manager pulses StateChanged on a drag's Moved, before the surface's debounce reports Settled. That
    // pulse must not put the window back into its own slot, or the swap below it could never happen.
    [Fact]
    public void A_drags_own_state_pulse_does_not_snap_the_window_back_before_it_settles()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        surface.Frames[W(1)] = Grid(3)[2];
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Moved, Info(W(1))));
        Assert.Empty(surface.Placed);

        surface.SettledSubject.OnNext(W(1));
        AssertTiled([W(3), W(2), W(1)]);
    }

    [Fact]
    public void Settled_where_it_was_put_does_nothing()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        // The LOCATIONCHANGE from Deskbar's own move: the frame is the slot it was just given.
        Settles(W(1), Grid(3)[0]);

        Assert.Empty(surface.Placed);
        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
    }

    [Fact]
    public void A_resized_window_goes_back_to_its_slot()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        var slot = Grid(3)[0];
        Settles(W(1), slot with { Right = slot.Right - 150, Bottom = slot.Bottom - 200 });

        Assert.Equal([(W(1), slot)], surface.Placed);
        AssertTiled([W(1), W(2), W(3)]);
    }

    [Fact]
    public void Settled_on_another_screen_joins_that_screen()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha);
        Exists(W(2), alpha);
        Exists(W(3), alpha, screen: 2);
        Start();
        AssertTiled([W(1), W(2)]);
        AssertTiled([W(3)], Area2, screen: 2);

        Settles(W(1), new WindowRect(2000, 100, 2600, 700), screen: 2);

        AssertTiled([W(2)]);
        AssertTiled([W(3), W(1)], Area2, screen: 2);
    }

    [Fact]
    public void A_window_dropped_on_another_workspace_leaves_the_grid()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        // The bar moved it to Beta; its frame is wherever it was dropped.
        desktops.WindowPlacements[W(1)] = beta.DesktopId!.Value;
        Settles(W(1), new WindowRect(700, 300, 1100, 700));

        Assert.DoesNotContain(W(1), PlacedWindows);
        AssertTiled([W(2), W(3)]);
        Assert.Equal(new HashSet<WindowHandle> { W(2), W(3) }, surface.Watched[^1]);
    }

    // A desktop change raises no window event and no Settled; WorkspaceManager.WindowMoved is all there is.
    [Fact]
    public void A_window_the_bar_moves_to_another_workspace_leaves_the_grid()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        Assert.True(manager.MoveToDesktop(W(2), beta.DesktopId!.Value).IsSuccess);

        Assert.DoesNotContain(W(2), PlacedWindows);
        AssertTiled([W(1), W(3)]);
        Assert.Equal(new HashSet<WindowHandle> { W(1), W(3) }, surface.Watched[^1]);
    }

    // Final review, item 1. Spec: "A drop on one of the bar's workspace rows is the bar's gesture (move to that
    // workspace) and wins over a tile swap." The bar puts the window back, waits for Snap, puts it back again
    // and only then moves it, so its Settled (with Snap's half-screen frame, centre over another tile) arrives
    // first. While the bar says its drop is pending, that Settled swaps nothing and no re-tile places it; the
    // move then takes it out of the grid and the rest close up.
    [Fact]
    public void A_window_whose_bar_drop_is_pending_does_not_swap_and_its_move_closes_the_grid()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        // Released on a bar row; Snap left it with its centre over W(3)'s tile.
        dropping.Add(W(1));
        var over = Grid(3)[2];
        Settles(W(1), over with { Left = over.Left + 40, Right = over.Right - 40 });

        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Assert.Empty(surface.Placed);

        // A structural re-tile while the drop is still pending does not place it either.
        Opens(W(4), alpha);
        Assert.DoesNotContain(W(1), PlacedWindows);

        // The bar's drop moves it to Beta (WindowMoved), then the bar clears the mark.
        Assert.True(manager.MoveToDesktop(W(1), beta.DesktopId!.Value).IsSuccess);
        dropping.Remove(W(1));

        Assert.DoesNotContain(W(1), PlacedWindows);
        AssertTiled([W(2), W(3), W(4)]);
    }

    [Fact]
    public void A_window_moved_onto_the_tiled_workspace_joins_it()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Exists(W(3), beta);
        Start();
        Assert.DoesNotContain(W(3), tiler.Board.On(1));

        Assert.True(manager.MoveToDesktop(W(3), alpha.DesktopId!.Value).IsSuccess);

        AssertTiled([W(1), W(2), W(3)]);
        Assert.Contains(W(3), surface.Rounded);
    }

    // Ruling 3: a Settled for a window that is not on the board joins it only if it now qualifies.
    [Fact]
    public void A_settled_window_that_now_lives_here_joins_at_the_end()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Exists(W(3), beta);
        Start();

        // Settled from a window on another workspace: ignored.
        Settles(W(3), new WindowRect(100, 100, 500, 500));
        Assert.DoesNotContain(W(3), tiler.Board.On(1));

        // Brought here (the bar moved it to Alpha), then settled: it joins like a new window.
        desktops.WindowPlacements[W(3)] = alpha.DesktopId!.Value;
        Settles(W(3), new WindowRect(100, 100, 500, 500));

        AssertTiled([W(1), W(2), W(3)]);
    }

    [Fact]
    public void A_window_that_will_not_shrink_is_placed_once()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.MinimumSize[W(1)] = (1500, 1000);
        Start();
        Assert.Single(PlacedWindows, W(1));

        // Its LOCATIONCHANGE after the place reports the size it insisted on, which is accepted.
        Settles(W(1), surface.Frames[W(1)]);
        tiler.Refresh();

        Assert.Single(PlacedWindows, W(1));
        Assert.Contains(traced, line => line.Contains("1") && line.Contains("kept"));
    }

    // The backstop (spec: "a short per-window quiet period as a backstop"). An app that puts itself back where
    // it wants to be after every Place would otherwise be fought for ever.
    [Fact]
    public void An_app_that_keeps_snapping_back_is_left_alone_after_three_put_backs()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();
        var slot = Grid(3)[0];
        var insisted = slot with { Right = slot.Right - 150, Bottom = slot.Bottom - 200 };

        Enumerable.Range(0, 3).ToList().ForEach(_ =>
        {
            time += TimeSpan.FromMilliseconds(400);
            Settles(W(1), insisted);
        });
        Assert.Equal(3, PlacedWindows.Count(w => w == W(1)));

        // The fourth within 2 seconds: no Place, and where it settled is now accepted.
        time += TimeSpan.FromMilliseconds(400);
        Settles(W(1), insisted);
        Assert.Equal(3, PlacedWindows.Count(w => w == W(1)));
        Assert.Equal(insisted, surface.Frames[W(1)]);
        Assert.Contains(traced, line => line.Contains("left at"));

        // Nor on a later re-tile or another Settled, while its slot stays the same...
        tiler.Refresh();
        time += TimeSpan.FromSeconds(5);
        Settles(W(1), insisted with { Left = insisted.Left + 10 });
        Assert.Equal(3, PlacedWindows.Count(w => w == W(1)));

        // ...until a structural change gives it another slot.
        Opens(W(4), alpha);
        Assert.Equal(Grid(4)[0], surface.Frames[W(1)]);
    }

    // Final review, item 2: a structural re-tile (another window opening on the same screen) must not place a
    // window the user is dragging, or it jumps out from under the pointer. Its own Settled puts it right.
    [Fact]
    public void A_window_being_dragged_is_not_placed_by_a_retile_and_its_settle_puts_it_right()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        // W(1) is under the pointer, part way through a drag.
        surface.Settling.Add(W(1));
        surface.Frames[W(1)] = new WindowRect(300, 200, 900, 700);
        Opens(W(4), alpha);

        Assert.DoesNotContain(W(1), PlacedWindows);
        Assert.Equal([W(1), W(2), W(3), W(4)], tiler.Board.On(1));

        // Let go a little off its new slot: it goes into it.
        surface.Settling.Remove(W(1));
        var slot = Grid(4)[0];
        Settles(W(1), slot with { Left = slot.Left + 20, Right = slot.Right + 20 });

        AssertTiled([W(1), W(2), W(3), W(4)]);
    }

    // A window Deskbar has just placed is settling too, for one quiet interval (its own LOCATIONCHANGE). A
    // re-tile in that interval holds it, and its Settled then reports exactly the frame it was given, which the
    // self-move guard would otherwise read as "nothing happened". It goes on into the slot it has now.
    [Fact]
    public void A_window_held_while_settling_from_its_own_place_goes_to_its_new_slot_when_it_settles()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();

        surface.Settling.Add(W(1));
        Opens(W(4), alpha);
        Assert.DoesNotContain(W(1), PlacedWindows);

        surface.Settling.Remove(W(1));
        Settles(W(1), Grid(3)[0]);

        AssertTiled([W(1), W(2), W(3), W(4)]);
    }

    // Final review, item 4: dropping a board forgets what the backstop decided. Untiling and tiling again is a
    // fresh start, so a window the backstop had left alone is placed in its slot.
    [Fact]
    public void A_window_left_alone_is_placed_again_after_untiling_and_tiling_again()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var slot = Grid(3)[0];
        var insisted = slot with { Right = slot.Right - 150, Bottom = slot.Bottom - 200 };
        Enumerable.Range(0, 4).ToList().ForEach(_ =>
        {
            time += TimeSpan.FromMilliseconds(400);
            Settles(W(1), insisted);
        });
        Assert.Equal(insisted, surface.Frames[W(1)]);

        Assert.True(manager.SetWorkspaceTiled(alpha.Id, false).IsSuccess);
        Assert.True(manager.SetWorkspaceTiled(alpha.Id, true).IsSuccess);

        AssertTiled([W(1), W(2), W(3)]);
    }

    // Item 4 again: a window maximized while tiled, then un-maximized while the experiment was off, is not
    // "being restored" when the experiment comes back on. Dragged over another tile, it swaps.
    [Fact]
    public void A_window_maximized_before_the_experiment_went_off_swaps_when_dragged_after_it_is_back_on()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Maximized.Add(W(2));
        Settles(W(2), Area);

        Assert.True(manager.SetExperimentalTiling(false).IsSuccess);
        surface.Maximized.Remove(W(2));
        surface.Frames[W(2)] = Grid(3)[1];
        Assert.True(manager.SetExperimentalTiling(true).IsSuccess);
        AssertTiled([W(1), W(2), W(3)]);

        var over = Grid(3)[2];
        Settles(W(2), over with { Left = over.Left + 40, Right = over.Right - 40 });

        AssertTiled([W(1), W(3), W(2)]);
    }

    [Fact]
    public void Put_backs_spread_over_time_are_not_counted_together()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Placed.Clear();
        var slot = Grid(3)[0];
        var insisted = slot with { Right = slot.Right - 150, Bottom = slot.Bottom - 200 };

        Enumerable.Range(0, 4).ToList().ForEach(_ =>
        {
            time += TimeSpan.FromMilliseconds(2100);
            Settles(W(1), insisted);
        });

        Assert.Equal(4, PlacedWindows.Count(w => w == W(1)));
        Assert.Equal(slot, surface.Frames[W(1)]);
    }

    [Fact]
    public void A_window_closing_mid_retile_leaves_the_rest_tiled()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.RefusePlace.Add(W(2));

        Start();

        var grid = Grid(3);
        Assert.Equal(grid[0], surface.Frames[W(1)]);
        Assert.Equal(grid[2], surface.Frames[W(3)]);
    }

    // Ruling 5: an exception in a handler costs that one event, never the ones after it.
    [Fact]
    public void A_throwing_surface_does_not_deafen_the_tiler()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.ThrowOnPlace.Add(W(3));

        Opens(W(3), alpha);
        Opens(W(4), alpha);

        AssertTiled([W(1), W(2), W(3), W(4)]);
        Assert.Contains(traced, line => line.Contains("threw"));
    }

    // Review fix: the throw is the LAST event, so nothing after it could repair a half-done re-tile. W(2) needs
    // a new slot when W(3) joins (the right column splits in two) and its Place throws; W(3), after it in the
    // loop, is still placed, and W(1) keeps its slot.
    [Fact]
    public void A_place_that_throws_as_the_last_event_leaves_the_rest_of_the_screen_tiled()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.ThrowOnPlace.Add(W(2));

        Opens(W(3), alpha);

        var grid = Grid(3);
        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Assert.Equal(grid[0], surface.Frames[W(1)]);
        Assert.Equal(grid[2], surface.Frames[W(3)]);
        Assert.Contains(traced, line => line.Contains("place 2 threw"));
    }

    [Fact]
    public void A_round_corners_that_throws_still_lets_the_window_join()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.ThrowOnRound.Add(W(3));

        Opens(W(3), alpha);

        AssertTiled([W(1), W(2), W(3)]);
        Assert.Contains(traced, line => line.Contains("round corners of 3 threw"));
    }

    // Final review, item 3: a licence refresh can land after SessionEnding or ExitApp disposed the Tiler. It
    // must not move a window or put the LOCATIONCHANGE hook back.
    [Fact]
    public void Refresh_after_dispose_places_nothing()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        tiler.Dispose();
        surface.Placed.Clear();

        // The manager hears the new window; the disposed Tiler is not listening, but a Refresh would find it.
        Opens(W(3), alpha);
        tiler.Refresh();

        Assert.Empty(surface.Placed);
        Assert.Empty(surface.Watched[^1]);
    }

    // --- screens -----------------------------------------------------------------------------------------

    [Fact]
    public void Windows_from_a_screen_that_went_away_join_the_screen_they_are_on()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha);
        Exists(W(2), alpha, screen: 2);
        Exists(W(3), alpha, screen: 2);
        Start();
        AssertTiled([W(2), W(3)], Area2, screen: 2);

        // Screen 2 unplugged: Windows moves its windows onto screen 1.
        surface.ScreenList.RemoveAll(s => s.Number == 2);
        surface.ScreenOfWindow[W(2)] = 1;
        surface.ScreenOfWindow[W(3)] = 1;
        surface.ScreensChangedSubject.OnNext(Unit.Default);

        AssertTiled([W(1), W(2), W(3)]);
        Assert.Empty(tiler.Board.On(2));
    }

    // Ruling 2: a work area too small for its grid is skipped, not thrown on and not half-placed.
    [Fact]
    public void A_screen_too_small_for_its_grid_is_skipped()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, new WindowRect(1920, 0, 1940, 20)));
        Exists(W(1), alpha);
        Exists(W(2), alpha, screen: 2);
        Exists(W(3), alpha, screen: 2);

        Start();

        AssertTiled([W(1)]);
        Assert.DoesNotContain(W(2), PlacedWindows);
        Assert.DoesNotContain(W(3), PlacedWindows);
        Assert.Equal([W(2), W(3)], tiler.Board.On(2));
    }

    [Fact]
    public void The_gap_scales_with_the_screens_dpi()
    {
        surface.ScreenList[0] = FakeTileSurface.Screen(1, Area, 144);
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));

        Start();

        Assert.Equal(GridLayout.Arrange(Area, 2, 12), new[] { W(1), W(2) }.Select(w => surface.Frames[w]).ToList());
    }

    // StateChanged fires on every persist; one that changes nothing about tiling must not ask about any window
    // (WorkspaceOf is a COM call per window on the UI thread).
    [Fact]
    public void A_state_pulse_that_changes_nothing_tiling_reads_no_windows()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var canTile = surface.CanTileCalls;
        var reads = surface.FrameReads;

        Assert.True(manager.RenameWorkspace(beta.Id, "Gamma").IsSuccess);
        Assert.True(manager.SetWorkspaceFolded(alpha.Id, true).IsSuccess);

        Assert.Equal(canTile, surface.CanTileCalls);
        Assert.Equal(reads, surface.FrameReads);
    }

    // ...while one that does change it (the gap) still re-tiles.
    [Fact]
    public void Changing_the_gap_retiles()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        Assert.True(manager.SetTileGap(20).IsSuccess);

        Assert.Equal(GridLayout.Arrange(Area, 2, 20), new[] { W(1), W(2) }.Select(w => surface.Frames[w]).ToList());
    }

    [Fact]
    public void A_retile_traces_the_layout_only_when_it_changed()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var lines = traced.Count(l => l.StartsWith("tile: Alpha screen 1:"));
        Assert.Equal(1, lines);

        tiler.Refresh();
        manager.SetWorkspaceFolded(alpha.Id, true);
        Assert.Equal(1, traced.Count(l => l.StartsWith("tile: Alpha screen 1:")));

        Opens(W(3), alpha);
        Assert.Equal(2, traced.Count(l => l.StartsWith("tile: Alpha screen 1:")));
    }
}
