using System.Reactive;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The Tiler's half of resizable tiles (docs/superpowers/specs/2026-11-08-tile-resize-design.md). Petre: "i want
// to be able to resize tiles by dragging the separation" and "keep my sizes". Built like TilerTests: a started
// WorkspaceManager over the fakes and a FakeTileSurface, with FakeTileSurface.MovedByHand standing for "a mouse
// button was down during the change".
//
// The numbers, worked once so every test can use them. The work area is 1920x1040 and the gap 8, so the inner
// area (inside the outer gap) is 8,8 to 1912,1032: 1904 wide and 1024 high, and each side of a shared edge
// takes 4 px of the gap. Two equal tiles are 8..956 and 964..1912. Dragging the left tile's right edge to 1337
// puts the split at 1341 (the edge plus half the gap), a column fraction of 1333/1904, so the right tile then
// starts at 1345: exactly one gap further on.
public sealed class TilerResizeTests : IDisposable
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeTileSurface surface = new();
    readonly List<string> traced = [];

    readonly Workspace alpha = new(Guid.NewGuid(), "Alpha", Guid.NewGuid());
    readonly Workspace beta = new(Guid.NewGuid(), "Beta", Guid.NewGuid());

    // The windows whose drop on a bar row is pending, as the bar would report them.
    readonly HashSet<WindowHandle> dropping = [];

    // How many of surface.Placed have already come back as their own Settled (see Echo).
    int echoed;

    WorkspaceManager manager = null!;
    Tiler tiler = null!;

    static readonly WindowRect Area = new(0, 0, 1920, 1040);
    static readonly WindowRect Area2 = new(1920, 0, 3840, 1040);

    // The screen keys sizes are stored by: the monitor's bounds as "L,T,R,B". A fake screen's bounds are its
    // work area.
    const string AreaKey = "0,0,1920,1040";
    const string Area2Key = "1920,0,3840,1040";
    const int Gap = AppState.DefaultTileGap;

    public void Dispose() => tiler?.Dispose();

    // --- building the world (as TilerTests does) ---------------------------------------------------------

    static WindowHandle W(int n) => new(n);

    static WindowInfo Info(WindowHandle h) => new(h, 1000 + (int)h.Value, "app", null, $"window {h.Value}", null);

    void Exists(WindowHandle h, Workspace workspace, int screen = 1)
    {
        monitor.InitialWindows.Add(Info(h));
        desktops.WindowPlacements[h] = workspace.DesktopId!.Value;
        surface.ScreenOfWindow[h] = screen;
        var left = (screen == 1 ? Area.Left : Area2.Left) + 50 * (int)h.Value;
        surface.Frames[h] = new WindowRect(left, 30, left + 300, 330);
    }

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

    void Settles(WindowHandle h, WindowRect frame, int screen = 1)
    {
        surface.Frames[h] = frame;
        surface.ScreenOfWindow[h] = screen;
        surface.SettledSubject.OnNext(h);
    }

    // The user dragged an edge of the window (a button was down) and let go at `frame`. The surface's by-hand
    // answer lasts until the window's next change starts, which the fake stands for by taking it away after.
    void ResizedByHand(WindowHandle h, WindowRect frame, int screen = 1)
    {
        surface.MovedByHand.Add(h);
        Settles(h, frame, screen);
        surface.MovedByHand.Remove(h);
    }

    // Every Place comes back as that window's own Settled once it has been still for the quiet interval (its own
    // LOCATIONCHANGE), at the frame it was given. On Windows that always happens; the fake has to be told.
    void Echo()
    {
        var placed = surface.Placed.Skip(echoed).Select(p => p.Window).Distinct().ToList();
        echoed = surface.Placed.Count;
        placed.ForEach(w => surface.SettledSubject.OnNext(w));
    }

    // Alpha current and tiled, the experiment and Pro on, `sizes` already stored on Alpha. The Places of the
    // first tidy-up are echoed, so the tests start from a grid at rest.
    void Start(bool experiment = true, ValueList<ScreenTileSizes>? sizes = null)
    {
        new[] { alpha, beta }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = alpha.DesktopId!.Value;
        store.Stored = AppState.Empty with
        {
            Workspaces = [alpha with { Tiled = true, TileSizes = sizes ?? [] }, beta],
            ExperimentalTiling = experiment,
        };
        manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        tiler = new Tiler(manager, desktops, monitor, surface, () => true, traced.Add, () => DateTimeOffset.UnixEpoch, dropping.Contains);
        tiler.Start();
        Echo();
    }

    // Two windows side by side, at rest.
    void StartTwo()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap), new[] { W(1), W(2) }.Select(w => surface.Frames[w]).ToList());
    }

    ValueList<ScreenTileSizes> StoredSizes => manager.State.Workspaces.Single(w => w.Id == alpha.Id).TileSizes;

    TileSizes Stored(string key) => StoredSizes.Single(s => s.Screen == key).Sizes;

    IReadOnlyList<WindowRect> FramesOf(IEnumerable<WindowHandle> windows) => windows.Select(w => surface.Frames[w]).ToList();

    // --- moving a split ----------------------------------------------------------------------------------

    [Fact]
    public void A_hand_resize_of_a_separation_moves_the_split_and_retiles()
    {
        StartTwo();

        // The left window's right edge (the separation) dragged to 1337, about 0.7 of the width.
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));

        // Both sides of the separation take the new split, exactly one gap apart, and the sizes are stored.
        Assert.Equal(new WindowRect(8, 8, 1337, 1032), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(1345, 8, 1912, 1032), surface.Frames[W(2)]);
        Assert.Equal(1333.0 / 1904, Stored(AreaKey).Columns[0], 9);
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap, Stored(AreaKey)), FramesOf([W(1), W(2)]));
    }

    // Ruling 3: the stored sizes are in the Tiler's state key, so the Tiler's own SetTileSizes causes one more
    // evaluate after the split move. That evaluate places nothing, because every window is already in its slot.
    [Fact]
    public void A_split_move_places_only_the_neighbour_and_the_evaluate_after_it_places_nothing()
    {
        StartTwo();
        surface.Placed.Clear();
        echoed = 0;
        var asked = surface.CanTileCalls;

        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));

        // W(1) is already where its new slot is, so only W(2) moves, and only once.
        Assert.Equal([(W(2), new WindowRect(1345, 8, 1912, 1032))], surface.Placed);
        // The evaluate did run (it asked about both windows, beyond OnSettled's one question about W(1)).
        Assert.True(surface.CanTileCalls - asked > 1, $"expected an evaluate after the split move, CanTile was asked {surface.CanTileCalls - asked} times");

        // W(2)'s own echo, and a refresh, move nothing either.
        Echo();
        tiler.Refresh();
        Assert.Single(surface.Placed);
    }

    [Fact]
    public void A_corner_resize_moves_both_splits_and_retiles()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        // One column on the left, two rows on the right.
        Assert.Equal(new WindowRect(964, 8, 1912, 516), surface.Frames[W(2)]);

        // W(2)'s bottom-left corner dragged: the column split and the right column's row split both move.
        ResizedByHand(W(2), new WindowRect(1300, 8, 1912, 700));

        Assert.Equal(new WindowRect(8, 8, 1292, 1032), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(1300, 8, 1912, 700), surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(1300, 708, 1912, 1032), surface.Frames[W(3)]);
        var sizes = Stored(AreaKey);
        Assert.Equal(1288.0 / 1904, sizes.Columns[0], 9);
        Assert.Equal(696.0 / 1024, sizes.Rows[1][0], 9);
    }

    [Fact]
    public void An_app_resizing_itself_snaps_back_and_moves_no_split()
    {
        StartTwo();
        surface.Placed.Clear();
        var slot = GridLayout.Arrange(Area, 2, Gap)[0];

        // The same edge change as a drag, but no button was down: the app grew itself.
        Settles(W(1), slot with { Right = 1337 });

        Assert.Equal([(W(1), slot)], surface.Placed);
        Assert.Empty(StoredSizes);
    }

    [Fact]
    public void An_outer_edge_resize_snaps_back()
    {
        StartTwo();
        surface.Placed.Clear();
        var slot = GridLayout.Arrange(Area, 2, Gap)[0];

        // The left window's LEFT edge is on the screen side of the grid: there is no split behind it.
        ResizedByHand(W(1), slot with { Left = 200 });

        Assert.Equal([(W(1), slot)], surface.Placed);
        Assert.Empty(StoredSizes);
    }

    // Ruling 2: Deskbar placed the window and the app then adjusted itself within the same change, while a mouse
    // button happened to be down for something else. The surface says "by hand", but the change began with
    // Deskbar's own Place, so it is not a drag: it is put back and no split moves.
    [Fact]
    public void A_settle_that_follows_our_own_place_is_never_a_hand_resize()
    {
        StartTwo();
        Opens(W(3), alpha);
        // W(2) was placed into the top right slot; before its echo arrives the app shrinks its own bottom edge.
        var slot = GridLayout.Arrange(Area, 3, Gap)[1];
        Assert.Equal(slot, surface.Frames[W(2)]);
        surface.Placed.Clear();

        ResizedByHand(W(2), slot with { Bottom = slot.Bottom - 150 });

        Assert.Equal([(W(2), slot)], surface.Placed);
        Assert.Empty(StoredSizes);
    }

    // --- keeping sizes -----------------------------------------------------------------------------------

    [Fact]
    public void A_new_window_keeps_the_column_widths()
    {
        StartTwo();
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));
        Echo();

        Opens(W(3), alpha);

        // The left column keeps its width; the right column splits into two equal rows.
        Assert.Equal(new WindowRect(8, 8, 1337, 1032), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(1345, 8, 1912, 516), surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(1345, 524, 1912, 1032), surface.Frames[W(3)]);
        var sizes = Stored(AreaKey);
        Assert.Equal(1333.0 / 1904, sizes.Columns[0], 9);
        Assert.Equal(new[] { 0.5, 0.5 }, sizes.Rows[1]);
    }

    // Eight windows: columns of 2, 3 and 3. W(4) holds the middle row of the middle column, whose rows are
    // 0.5, 0.2 and 0.3. When it closes, that column has two rows and they keep their 5:3 proportion (0.625 and
    // 0.375); without knowing which row left, the bottom row would have gone instead. The other columns and the
    // column widths do not change.
    [Fact]
    public void Closing_a_middle_window_keeps_the_other_rows_proportions()
    {
        Enumerable.Range(1, 8).ToList().ForEach(n => Exists(W(n), alpha));
        var before = new TileSizes([0.3, 0.35, 0.35], [[0.5, 0.5], [0.5, 0.2, 0.3], [0.2, 0.3, 0.5]]);
        Start(sizes: [new ScreenTileSizes(AreaKey, before)]);
        Assert.Equal(GridLayout.Arrange(Area, 8, Gap, before), FramesOf(Enumerable.Range(1, 8).Select(W)));

        Closes(W(4));

        var after = Stored(AreaKey);
        Assert.Equal(before.Columns, after.Columns);
        Assert.Equal(before.Rows[0], after.Rows[0]);
        Assert.Equal(0.625, after.Rows[1][0], 9);
        Assert.Equal(0.375, after.Rows[1][1], 9);
        Assert.Equal(before.Rows[2], after.Rows[2]);
        var order = new[] { 1, 2, 3, 5, 6, 7, 8 }.Select(W).ToList();
        Assert.Equal(order, tiler.Board.On(1));
        Assert.Equal(GridLayout.Arrange(Area, 7, Gap, after), FramesOf(order));
    }

    // Sizes are kept by the screen's bounds, never its number, and ScreensChanged never deletes them. The screen
    // drops out for a moment (a dock reconnecting): while it is gone the surface knows no screen for its windows
    // (TileSurface.ScreenOf answers 0 for a monitor that is not among the numbered ones). It comes back under
    // another number with the same bounds, its windows somewhere else on it, and they are tiled with the sizes
    // they had.
    [Fact]
    public void A_screen_that_returns_with_the_same_bounds_gets_its_sizes_back()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha);
        Exists(W(2), alpha, screen: 2);
        Exists(W(3), alpha, screen: 2);
        Start();
        ResizedByHand(W(2), new WindowRect(1928, 8, 3257, 1032), screen: 2);
        Echo();
        Assert.Equal(new WindowRect(3265, 8, 3832, 1032), surface.Frames[W(3)]);
        var sizes = Stored(Area2Key);

        surface.ScreenList.RemoveAll(s => s.Number == 2);
        new[] { W(2), W(3) }.ToList().ForEach(w => surface.ScreenOfWindow[w] = 0);
        surface.ScreensChangedSubject.OnNext(Unit.Default);
        Assert.Equal(sizes, Stored(Area2Key));

        surface.ScreenList.Add(FakeTileSurface.Screen(3, Area2));
        new[] { W(2), W(3) }.ToList().ForEach(w => surface.ScreenOfWindow[w] = 3);
        surface.Frames[W(2)] = new WindowRect(2000, 100, 2600, 700);
        surface.Frames[W(3)] = new WindowRect(2700, 100, 3300, 700);
        surface.ScreensChangedSubject.OnNext(Unit.Default);

        Assert.Equal([W(2), W(3)], tiler.Board.On(3));
        Assert.Equal(new WindowRect(1928, 8, 3257, 1032), surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(3265, 8, 3832, 1032), surface.Frames[W(3)]);
        Assert.Equal(sizes, Stored(Area2Key));
    }

    // "Even out tiles" only changes state and pulses StateChanged; the sizes in the Tiler's state key make that
    // pulse re-tile the screen.
    [Fact]
    public void Evening_out_retiles_to_equal()
    {
        StartTwo();
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));
        Echo();
        Assert.NotEmpty(StoredSizes);

        Assert.True(manager.EvenOutTiles(alpha.Id).IsSuccess);

        Assert.Empty(StoredSizes);
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap), FramesOf([W(1), W(2)]));
    }

    // --- the guards --------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_moves_a_split_while_the_experiment_is_off()
    {
        StartTwo();
        Assert.True(manager.SetExperimentalTiling(false).IsSuccess);
        surface.Placed.Clear();

        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));

        Assert.Empty(surface.Placed);
        Assert.Empty(StoredSizes);
        Assert.Equal(new WindowRect(964, 8, 1912, 1032), surface.Frames[W(2)]);
    }

    [Fact]
    public void A_drop_pending_window_does_not_move_a_split()
    {
        StartTwo();
        surface.Placed.Clear();

        dropping.Add(W(1));
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));

        Assert.Empty(surface.Placed);
        Assert.Empty(StoredSizes);
    }
}
