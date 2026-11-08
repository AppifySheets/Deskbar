using System.Reactive;
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
    void Start(bool alphaTiled = true, bool betaTiled = false, bool experiment = true)
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
        tiler = new Tiler(manager, desktops, monitor, surface, () => pro, traced.Add, () => time);
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

    [Fact]
    public void A_minimized_window_leaves_and_rejoins_at_the_end()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();

        Minimizes(W(1), true);
        AssertTiled([W(2), W(3)]);

        Minimizes(W(1), false);
        AssertTiled([W(2), W(3), W(1)]);
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
        surface.ScreenList.Add(new TileScreen(2, Area2, 96));
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

    // --- screens -----------------------------------------------------------------------------------------

    [Fact]
    public void Windows_from_a_screen_that_went_away_join_the_screen_they_are_on()
    {
        surface.ScreenList.Add(new TileScreen(2, Area2, 96));
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
        surface.ScreenList.Add(new TileScreen(2, new WindowRect(1920, 0, 1940, 20), 96));
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
        surface.ScreenList[0] = new TileScreen(1, Area, 144);
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
