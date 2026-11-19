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

    // The Tiler's clock, moved by hand for the grace period of a returning screen.
    DateTimeOffset time = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

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

    void Minimizes(WindowHandle h, bool minimized)
    {
        if (minimized) surface.Minimized.Add(h); else surface.Minimized.Remove(h);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.MinimizeChanged, Info(h)));
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

    // Whether this copy may tile, as the Tiler asks it. A test turns it off (and calls Refresh, as the App does
    // after a licence refresh) to stand tiling down without touching Alpha's Tiled flag or its sizes.
    bool pro = true;

    // Alpha current and tiled (unless `tiled` says otherwise), Pro on, `sizes` already stored on Alpha. The
    // Places of the first tidy-up are echoed, so the tests start from a grid at rest.
    void Start(bool tiled = true, ValueList<ScreenTileSizes>? sizes = null)
    {
        new[] { alpha, beta }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = alpha.DesktopId!.Value;
        store.Stored = AppState.Empty with
        {
            Workspaces = [alpha with { Tiled = tiled, TileSizes = sizes ?? [] }, beta],
        };
        manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        tiler = new Tiler(manager, desktops, monitor, surface, () => pro, traced.Add, () => time, dropping.Contains);
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

    // LIVE (Tiler.Drag.cs). Petre: "can you resize, or show me how it would end up on the screen, before i let it
    // go?" While the separation is still being dragged, the tile beside it follows at once, laid out from the same
    // arithmetic the drop uses; nothing is stored until the drop, and the drop then moves nothing more.
    [Fact]
    public void While_a_separation_is_dragged_the_neighbour_follows_live_and_the_drop_stores_it()
    {
        StartTwo();
        surface.Placed.Clear();

        // Mid-drag: the button is still down (Settling), the left tile's right edge at 1337.
        surface.Settling.Add(W(1));
        surface.Frames[W(1)] = new WindowRect(8, 8, 1337, 1032);
        surface.MovingSubject.OnNext(W(1));

        Assert.Equal([(W(2), new WindowRect(1345, 8, 1912, 1032))], surface.Placed);
        Assert.Empty(StoredSizes);

        // The neighbour's echo arrives before the dragged window settles: it waits, and is not put back.
        surface.SettledSubject.OnNext(W(2));
        Assert.Single(surface.Placed);

        // Let go where it is: the split is stored, and nothing moves again.
        surface.Settling.Remove(W(1));
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));
        Assert.Single(surface.Placed);
        Assert.Equal(1333.0 / 1904, Stored(AreaKey).Columns[0], 9);
    }

    // The dragged window itself is never placed during the drag: it is under the pointer.
    [Fact]
    public void A_live_resize_never_places_the_window_being_dragged()
    {
        StartTwo();
        surface.Placed.Clear();

        surface.Settling.Add(W(2));
        surface.Frames[W(2)] = new WindowRect(700, 8, 1912, 1032);
        surface.MovingSubject.OnNext(W(2));

        Assert.DoesNotContain(W(2), surface.Placed.Select(p => p.Window));
        Assert.Equal(new WindowRect(8, 8, 692, 1032), surface.Frames[W(1)]);
    }

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

    // The stored sizes are in the Tiler's state key (ruling 3), so "Even out tiles" re-tiles. The Tiler's OWN store
    // must not pay for that with a second full evaluate, which asks WorkspaceOf (COM) of every known window: after
    // its own SetTileSizes the Tiler takes the new key as seen, so its own pulse compares equal (final review, item
    // 2). OnSettled asks CanTile once, of the window that settled; a full evaluate would ask of both windows again.
    // The same holds for a refit when a window joins: one evaluate (the Appeared one), not a second for the store.
    [Fact]
    public void A_split_move_does_not_re_evaluate_every_window()
    {
        StartTwo();
        surface.Placed.Clear();
        echoed = 0;
        var asked = surface.CanTileCalls;

        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));

        // W(1) is already where its new slot is, so only W(2) moves, and only once.
        Assert.Equal([(W(2), new WindowRect(1345, 8, 1912, 1032))], surface.Placed);
        Assert.Equal(1, surface.CanTileCalls - asked);

        // W(2)'s own echo, and a refresh, move nothing either.
        Echo();
        tiler.Refresh();
        Assert.Single(surface.Placed);

        // A third window splits the largest tile, W(1), into two columns and stores the new shape (predictable
        // placement, change 2: it used to refit to three windows, the right column gaining a row).
        // The Appeared evaluate asks of the three windows once; the store's pulse asks nothing more.
        asked = surface.CanTileCalls;
        Opens(W(3), alpha);
        Assert.Equal(3, Stored(AreaKey).Columns.Count);
        Assert.Equal(3, surface.CanTileCalls - asked);
    }

    // A drag held against the 10% clamp ends at the split that is already stored: nothing new is stored (final
    // review, item 3), and the screen is still re-tiled, which puts the dragged window back on the clamped split.
    [Fact]
    public void A_drag_that_ends_at_the_same_split_stores_nothing()
    {
        StartTwo();
        ResizedByHand(W(1), new WindowRect(8, 8, 100, 1032));
        Echo();
        Assert.Equal(0.1, Stored(AreaKey).Columns[0], 9);
        var slot = GridLayout.Arrange(Area, 2, Gap, Stored(AreaKey))[0];
        Assert.Equal(slot, surface.Frames[W(1)]);
        var saves = store.SaveCount;
        surface.Placed.Clear();

        ResizedByHand(W(1), slot with { Right = 60 });

        Assert.Equal(saves, store.SaveCount);
        Assert.Equal([(W(1), slot)], surface.Placed);
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

    // A corner with one INNER edge and one OUTER edge does both halves: the inner edge moves its split, and the outer
    // edge leaves an opening as it would on its own. Petre: "resizing a tiled window with the corner doesn't currently
    // change height or width of the window; it should". Measured in his trace: a full-height tile's top-right corner
    // dragged down and right moved the column split, but the top edge was dropped and the tile went back to full height.
    // Here the left tile's top-right corner goes to (1300, 400): the right edge puts the split at 1304 (one half gap
    // beyond it), the top edge leaves 392 of the 1024 px inner height open above the tile.
    [Fact]
    public void A_corner_with_an_outer_edge_moves_the_split_and_leaves_an_opening()
    {
        StartTwo();
        var left = GridLayout.Arrange(Area, 2, Gap)[0];

        ResizedByHand(W(1), left with { Top = 400, Right = 1300 });
        Echo();

        var sizes = Stored(AreaKey);
        Assert.Equal(1296.0 / 1904, sizes.Columns[0], 9);
        Assert.Equal(0, sizes.Opening);
        Assert.Equal(392.0 / 1024, sizes.Rows[0][0], 9);
        Assert.Equal(new WindowRect(8, 404, 1300, 1032), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(1308, 8, 1912, 1032), surface.Frames[W(2)]);
    }

    // The same corner with the outer edge only nudged: the split moves and the nudge snaps back, as before.
    [Fact]
    public void A_corner_with_an_outer_edge_nudge_only_moves_the_split()
    {
        StartTwo();
        var left = GridLayout.Arrange(Area, 2, Gap)[0];

        ResizedByHand(W(1), left with { Top = 40, Right = 1300 });
        Echo();

        var sizes = Stored(AreaKey);
        Assert.Equal(1296.0 / 1904, sizes.Columns[0], 9);
        Assert.Null(sizes.Opening);
        Assert.Equal(new WindowRect(8, 8, 1300, 1032), surface.Frames[W(1)]);
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

    // A small pull on an outer edge is a nudge: it snaps back as outer edges always did, and nothing is stored.
    [Fact]
    public void An_outer_edge_nudge_snaps_back()
    {
        StartTwo();
        surface.Placed.Clear();
        var slot = GridLayout.Arrange(Area, 2, Gap)[0];

        // The left window's LEFT edge is on the screen side of the grid: there is no split behind it. 52 px of a
        // 1904 px inner width is under the 10% an opening needs.
        ResizedByHand(W(1), slot with { Left = 60 });

        Assert.Equal([(W(1), slot)], surface.Placed);
        Assert.Empty(StoredSizes);
    }

    // --- openings: room kept for the next window ---------------------------------------------------------

    // Petre: "if i resize a window in a tiled setting, i want to keep the opening for the new window to jump into".
    // Measured first: the right tile's top edge pulled down to y 911 and 735 snapped back to the top both times.
    // Now the room stays open, and the next window to open goes into it. Pulling the right tile's top edge to 400
    // leaves 392 of the 1024 px inner height open above it (a row share of 392/1024), so the tile starts one half
    // gap below the opening's row, at 404, and the window that opens next fills 8..396.
    [Fact]
    public void Pulling_an_outer_edge_in_leaves_an_opening_the_next_window_takes()
    {
        StartTwo();
        var right = GridLayout.Arrange(Area, 2, Gap)[1];

        ResizedByHand(W(2), right with { Top = 400 });
        Echo();

        var opened = Stored(AreaKey);
        Assert.Equal(1, opened.Opening);
        Assert.Equal(392.0 / 1024, opened.Rows[1][0], 9);
        Assert.Equal(right with { Top = 404 }, surface.Frames[W(2)]);
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap)[0], surface.Frames[W(1)]);

        Opens(W(3), alpha);
        Echo();

        Assert.Equal(right with { Bottom = 396 }, surface.Frames[W(3)]);
        Assert.Equal(right with { Top = 404 }, surface.Frames[W(2)]);
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap)[0], surface.Frames[W(1)]);
        Assert.Null(Stored(AreaKey).Opening);
        Assert.Contains(traced, l => l.Contains("took the opening"));
    }

    // Pulling the edge back over most of the opening gives the room back: the opening closes and, the sizes being
    // even again, the stored entry goes.
    [Fact]
    public void Pulling_the_edge_back_over_the_opening_closes_it()
    {
        StartTwo();
        var right = GridLayout.Arrange(Area, 2, Gap)[1];
        ResizedByHand(W(2), right with { Top = 400 });
        Echo();

        ResizedByHand(W(2), right with { Top = 60 });
        Echo();

        Assert.Empty(StoredSizes);
        Assert.Equal(right, surface.Frames[W(2)]);
    }

    // A tile of the same screen dragged into the opening moves there, and the place it left closes up: the left tile
    // dropped into the opening above the right one leaves one column, itself on top where the opening was.
    [Fact]
    public void A_tile_dragged_into_the_opening_moves_there()
    {
        StartTwo();
        var right = GridLayout.Arrange(Area, 2, Gap)[1];
        ResizedByHand(W(2), right with { Top = 400 });
        Echo();

        surface.PointerAt = (1400, 200);
        ResizedByHand(W(1), new WindowRect(1100, 100, 1700, 500));
        Echo();

        Assert.Equal(new WindowRect(8, 8, 1912, 396), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(8, 404, 1912, 1032), surface.Frames[W(2)]);
        Assert.Null(Stored(AreaKey).Opening);
    }

    // Another tile closing keeps the opening: only the leaver's cell goes, so the next window still has its room.
    [Fact]
    public void A_window_closing_keeps_the_opening()
    {
        StartTwo();
        var right = GridLayout.Arrange(Area, 2, Gap)[1];
        ResizedByHand(W(2), right with { Top = 400 });
        Echo();

        Closes(W(1));
        Echo();

        // One column now, the opening still above the tile.
        var sizes = Stored(AreaKey);
        Assert.Equal(0, sizes.Opening);
        Assert.Equal([2], sizes.Shape);
        Assert.Equal(new WindowRect(8, 404, 1912, 1032), surface.Frames[W(2)]);

        Opens(W(3), alpha);
        Echo();

        Assert.Equal(new WindowRect(8, 8, 1912, 396), surface.Frames[W(3)]);
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

        // W(2) is the tile last used, so W(3) splits it (predictable placement, change 2; it used to join the end).
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(W(2)), 1000));
        Opens(W(3), alpha);

        // The left column keeps its width; the right column splits into two equal rows.
        Assert.Equal(new WindowRect(8, 8, 1337, 1032), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(1345, 8, 1912, 516), surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(1345, 524, 1912, 1032), surface.Frames[W(3)]);
        var sizes = Stored(AreaKey);
        Assert.Equal(1333.0 / 1904, sizes.Columns[0], 9);
        Assert.Equal(new[] { 0.5, 0.5 }, sizes.Rows[1]);
    }

    // The most common layout there is: two windows, the split moved to about 0.7. One of them is minimized, so the
    // screen holds one window and its fit is "equal". An equal fit is laid out but never stored (final review, item
    // 1): storing it would REMOVE the entry, and the restored window would come back at 50/50. Petre: "keep my
    // sizes". The entry stays exactly as the drag left it, before, during and after.
    [Fact]
    public void Minimizing_one_of_two_tiles_keeps_the_split()
    {
        StartTwo();
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));
        Echo();
        var split = Stored(AreaKey);

        Minimizes(W(2), true);
        Echo();

        Assert.Equal(GridLayout.Arrange(Area, 1, Gap).Single(), surface.Frames[W(1)]);
        Assert.Equal(split, Stored(AreaKey));

        Minimizes(W(2), false);
        Echo();

        Assert.Equal(split, Stored(AreaKey));
        Assert.Equal(new WindowRect(8, 8, 1337, 1032), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(1345, 8, 1912, 1032), surface.Frames[W(2)]);
    }

    // The same with a window that closes for good: the entry stays exactly as the drag left it while one window is
    // left. The next window to open then splits the remaining tile, like any arrival.
    [Fact]
    public void Closing_down_to_one_window_keeps_the_sizes_for_the_next()
    {
        StartTwo();
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));
        Echo();
        var split = Stored(AreaKey);

        Closes(W(2));
        Echo();
        Assert.Equal(split, Stored(AreaKey));

        Opens(W(3), alpha);
        Echo();

        // The new window takes half of W(1), the tile on screen (predictable placement, change 2). The old count
        // rule put it back at the closed window's split; keeping that place is now change 3's opt-in kept space.
        Assert.Equal([W(1), W(3)], tiler.Board.On(1));
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap), FramesOf([W(1), W(3)]));
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

    // Screen 2 holds five windows in three columns (1 + 2 + 2) with sizes stored for it; screen 1 holds W(1).
    // The screen drops out and Windows moves its windows onto screen 1; then it comes back with the same bounds
    // and Windows moves them back ONE AT A TIME, each arriving as its own Settled 200 ms after the last. In
    // between, each re-tile of screen 2 lays out a smaller grid (1, 2, 3, 4 windows), and storing those fits would
    // lose columns and rows (at one window, the whole entry). Controller ruling: for 3 seconds after a screen key
    // appears, its fits are laid out but not stored.
    static readonly TileSizes ThreeColumns = new([0.5, 0.3, 0.2], [[1.0], [0.6, 0.4], [0.3, 0.7]]);
    static readonly IReadOnlyList<WindowHandle> Returning = new[] { 2, 3, 4, 5, 6 }.Select(W).ToList();

    void ScreenDropsOutAndItsWindowsComeBackOneByOne()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha);
        Returning.ToList().ForEach(w => Exists(w, alpha, screen: 2));
        Start(sizes: [new ScreenTileSizes(Area2Key, ThreeColumns)]);
        Assert.Equal(GridLayout.Arrange(Area2, 5, Gap, ThreeColumns), FramesOf(Returning));

        surface.ScreenList.RemoveAll(s => s.Number == 2);
        Returning.ToList().ForEach(w => surface.ScreenOfWindow[w] = 1);
        surface.ScreensChangedSubject.OnNext(Unit.Default);
        Echo();
        Assert.Equal(6, tiler.Board.On(1).Count);

        time += TimeSpan.FromMinutes(1);
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        surface.ScreensChangedSubject.OnNext(Unit.Default);
        Returning.ToList().ForEach(w =>
        {
            time += TimeSpan.FromMilliseconds(200);
            Settles(w, new WindowRect(2000, 100, 2600, 700), screen: 2);
            Echo();
            Assert.Equal(ThreeColumns, Stored(Area2Key));
        });
    }

    [Fact]
    public void A_screen_that_returns_window_by_window_keeps_its_sizes()
    {
        ScreenDropsOutAndItsWindowsComeBackOneByOne();

        Assert.Equal(ThreeColumns, Stored(Area2Key));
        Assert.Equal(Returning, tiler.Board.On(2));
        Assert.Equal(GridLayout.Arrange(Area2, 5, Gap, ThreeColumns), FramesOf(Returning));
    }

    // Pins that the grace period ENDS: once it is over, a refit is stored again. (Which row's share goes is
    // TileSizes.Fit's business and is pinned by Closing_a_middle_window_keeps_the_other_rows_proportions; here the
    // expected value only uses the same leaver so the two agree.)
    [Fact]
    public void After_the_grace_period_a_closing_window_reshapes_the_stored_sizes_again()
    {
        ScreenDropsOutAndItsWindowsComeBackOneByOne();
        time += TimeSpan.FromSeconds(3.1);

        // W(4) is slot 2 of 1 + 2 + 2: column 1, row 1.
        Closes(W(4));

        // Only W4's cell goes (tiling-predictable-placement, change 1): ThreeColumns is custom, so this was Fit
        // before and is now RemoveCell, leaving 1 + 1 + 2.
        Assert.Equal(TileArrange.RemoveCell(ThreeColumns, (1, 1)), Stored(Area2Key));
        var order = new[] { 2, 3, 5, 6 }.Select(W).ToList();
        Assert.Equal(GridLayout.Arrange(Area2, 4, Gap, Stored(Area2Key)), FramesOf(order));
    }

    // A split moved by hand while the screen is still in its grace period is the user's own change and is stored
    // at once. It is worked out from the layout the user sees: the screen's sizes are fitted ONCE per Settled and
    // the same fit lays out the slots and moves the split (final review, item 4), so a fit with side effects (it
    // takes a pending leaver) is never asked twice with two different answers.
    //
    // ThreeColumns on screen 2: the inner area is 1928..3832 (1904 wide), so column 0 (0.5) ends at the split
    // 2880 and W(2)'s right edge is at 2876. Dragged to 2700, the split goes to 2704: column 0 takes 776/1904 and
    // column 1 the rest of the pair's 0.8. Column 2 and every row keep what they had.
    [Fact]
    public void A_hand_split_move_during_grace_is_stored()
    {
        ScreenDropsOutAndItsWindowsComeBackOneByOne();
        Assert.Equal(new WindowRect(1928, 8, 2876, 1032), surface.Frames[W(2)]);

        time += TimeSpan.FromMilliseconds(200);
        ResizedByHand(W(2), new WindowRect(1928, 8, 2700, 1032), screen: 2);

        var stored = Stored(Area2Key);
        Assert.Equal(776.0 / 1904, stored.Columns[0], 9);
        Assert.Equal(0.8 - 776.0 / 1904, stored.Columns[1], 9);
        Assert.Equal(0.2, stored.Columns[2], 9);
        Assert.Equal(ThreeColumns.Rows, stored.Rows);
        Assert.Equal(GridLayout.Arrange(Area2, 5, Gap, stored), FramesOf(Returning));
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

    // The spec asks for one trace line per reset (final review, item 7). A split moved by hand writes its own line,
    // not this one.
    [Fact]
    public void Evening_out_writes_one_trace_line()
    {
        StartTwo();
        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));
        Echo();
        Assert.DoesNotContain(traced, l => l.Contains("evened out"));

        Assert.True(manager.EvenOutTiles(alpha.Id).IsSuccess);

        Assert.Single(traced, l => l.Contains("evened out"));
        Assert.Contains("tile: Alpha sizes evened out", traced);
    }

    // --- the guards --------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_moves_a_split_while_tiling_stands_down()
    {
        StartTwo();
        pro = false;
        tiler.Refresh();
        surface.Placed.Clear();

        ResizedByHand(W(1), new WindowRect(8, 8, 1337, 1032));

        Assert.Empty(surface.Placed);
        Assert.Empty(StoredSizes);
        Assert.Equal(new WindowRect(964, 8, 1912, 1032), surface.Frames[W(2)]);
    }

    // Tiling standing down forgets the echo marks too (final review, item 5). It used to be switched off with
    // Manage's "Tiling (experimental)" box, gone since 2026-11-19; Pro lapsing is the same stand-down today.
    // W(2) and W(3) are placed when W(3) opens, and tiling goes off before their echoes come back (the hook goes with it, so they never will). When
    // tiling comes back every window is already in its slot, so nothing is placed and no new mark is set; a stale
    // mark would then swallow the first real drag as an echo and snap it back.
    [Fact]
    public void A_drag_after_tiling_is_switched_off_and_on_moves_the_split()
    {
        StartTwo();
        Opens(W(3), alpha);
        var slot = GridLayout.Arrange(Area, 3, Gap)[1];
        Assert.Equal(slot, surface.Frames[W(2)]);

        pro = false;
        tiler.Refresh();
        pro = true;
        tiler.Refresh();
        Assert.Equal(GridLayout.Arrange(Area, 3, Gap), FramesOf(tiler.Board.On(1)));

        // W(2)'s bottom edge (the row split in the right column) dragged down to 700: the split goes to 704.
        ResizedByHand(W(2), slot with { Bottom = 700 });

        Assert.Equal(696.0 / 1024, Stored(AreaKey).Rows[1][0], 9);
        Assert.Equal(slot with { Bottom = 700 }, surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(964, 708, 1912, 1032), surface.Frames[W(3)]);
    }

    // A screen plugged in while Alpha is not tiled: nothing is tiled, so the trace says nothing about refitting
    // (final review, item 6). The bookkeeping still happens, quietly.
    [Fact]
    public void A_screen_appearing_while_nothing_is_tiled_writes_no_trace()
    {
        Exists(W(1), alpha);
        Start(tiled: false);

        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        surface.ScreensChangedSubject.OnNext(Unit.Default);

        Assert.DoesNotContain(traced, l => l.Contains("appeared"));
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
