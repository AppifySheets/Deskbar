using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// A NEW WINDOW SPLITS THE TILE YOU LAST USED (tiling-predictable-placement design, change 2). A window that arrives
// on a tiled workspace no longer joins the end of its screen and rebuilds the grid from the count: it takes half of
// the tile the user last activated, on that tile's screen (TileArrival.Choose picks the tile and the side). These
// tests drive the whole Tiler through the shared TilerTests harness (TilerTests.cs).
public sealed partial class TilerTests
{
    // The key the first fake screen's sizes are stored under (its bounds, "L,T,R,B").
    const string AreaKey = "0,0,1920,1040";

    // Windows' event clock (WindowEvent.Time, GetTickCount milliseconds), moved by hand. Starts well away from 0,
    // which means "not from a WinEvent".
    uint clock = 100_000;

    // The user activates a window: a second after whatever happened last, far outside the hand-out window.
    void Uses(WindowHandle h)
    {
        clock += 1000;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(h), clock));
    }

    // Windows activates a window by itself (after a close or minimize), a few milliseconds from it: `time` is the
    // activation's own event time.
    void HandedFocus(WindowHandle h, uint time) =>
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(h), time));

    // The next moment on Windows' event clock, a second on: the time of a close or minimize.
    uint Later() => clock += 1000;

    // The board order of a screen and every window's frame, against the grid laid out with these sizes.
    void AssertArranged(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowRect? area = null, int screen = 1)
    {
        Assert.Equal(order, tiler.Board.On(screen));
        Assert.Equal(GridLayout.Arrange(area ?? Area, order.Count, Gap, layout), order.Select(w => surface.Frames[w]).ToList());
    }

    // Two columns, W1 last used: W1 is taller than wide, so W3 goes below it and W2 keeps its whole column.
    [Fact]
    public void A_new_window_splits_the_tile_last_used()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(1));

        Opens(W(3), alpha);

        AssertArranged([W(1), W(3), W(2)], Columns([0.5, 0.5], [0.5, 0.5], [1.0]));
    }

    // Review focus 1: the window opened on screen 2, but the tile last used is on screen 1, so it goes there.
    [Fact]
    public void A_new_window_lands_on_the_screen_of_the_tile_last_used()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Exists(W(2), alpha, screen: 2);
        Start();
        Uses(W(1));

        Opens(W(3), alpha, screen: 2);

        // W1 had the whole of screen 1, wider than tall and alone in its column: a new column to its right.
        AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
        AssertArranged([W(2)], TileLayout.Automatic(1), Area2, screen: 2);
    }

    // The newcomer is the last-used tile as soon as the user activates it, so the next window splits it.
    [Fact]
    public void The_newcomer_is_the_next_target_once_it_has_joined()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));

        Opens(W(2), alpha);
        AssertArranged([W(1), W(2)], Columns([0.5, 0.5], [1.0], [1.0]));

        Uses(W(2));
        Opens(W(3), alpha);
        AssertArranged([W(1), W(2), W(3)], Columns([0.5, 0.5], [1.0], [0.5, 0.5]));
    }

    // Review focus 2: closing W3 makes Windows activate another window (here W1) 30 ms later. That
    // activation is not the user's, so the target stays W2, the last tile the user chose. Had it counted, W4 would
    // be below W1: [W1, W4, W2].
    [Fact]
    public void Focus_Windows_hands_out_after_a_close_does_not_count_when_it_comes_after()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Uses(W(2));
        Uses(W(3));

        var closed = Later();
        Closes(W(3), closed);
        HandedFocus(W(1), closed + 30);
        Opens(W(4), alpha);

        AssertArranged([W(1), W(2), W(4)], Columns([0.5, 0.5], [1.0], [0.5, 0.5]));
    }

    // The same when Windows' hand-out activation comes 30 ms BEFORE the window's Disappeared.
    [Fact]
    public void Focus_Windows_hands_out_after_a_close_does_not_count_when_it_comes_before()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(2));
        Uses(W(3));

        var closed = Later();
        HandedFocus(W(1), closed - 30);
        Closes(W(3), closed);
        Opens(W(4), alpha);

        AssertArranged([W(1), W(2), W(4)], Columns([0.5, 0.5], [1.0], [0.5, 0.5]));
    }

    // Minimizing a tile hands focus out the same way, and that activation does not count either: W4 goes below W2,
    // the tile the user chose. Had it counted, W4 would be below W1.
    [Fact]
    public void Focus_Windows_hands_out_after_a_minimize_does_not_count()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(2));
        Uses(W(3));

        var minimized = Later();
        Minimizes(W(3), true, minimized);
        HandedFocus(W(1), minimized + 30);
        Opens(W(4), alpha);

        AssertArranged([W(1), W(2), W(4)], Columns([0.5, 0.5], [1.0], [0.5, 0.5]));
    }

    // Final review M3: a leave with no event time (0, not from a WinEvent) must not wipe out the time of the real
    // close just before it. W4 closes at a real time, W3 closes with none, and Windows hands focus to W1 30 ms after
    // W4's close: still not the user's. W2 stays the target. W1 over W2 is left, each the full width, so wider than
    // tall: W5 goes beside W2 (the split tree's arrival side rule, Right for any tile wider than tall; the grid put it
    // below W2, because W2 was not alone in its column). Had the hand-out counted, W5 would be beside W1 instead.
    [Fact]
    public void A_leave_with_no_event_time_does_not_hide_the_hand_out_and_the_wide_target_is_split_beside_itself()
    {
        Enumerable.Range(1, 4).ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(2));
        Uses(W(4));

        var closed = Later();
        Closes(W(4), closed);
        Closes(W(3));
        HandedFocus(W(1), closed + 30);
        Opens(W(5), alpha);

        AssertArranged([W(1), W(2), W(5)], Tree(Down(C(L, .5), C(Across(C(L, .5), C(L, .5)), .5))));
    }

    // A click a second after a close is the user's: W1 becomes the target, and W4 goes below it.
    [Fact]
    public void A_user_click_long_after_a_close_counts()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(2));
        Uses(W(3));

        Closes(W(3), Later());
        Uses(W(1));
        Opens(W(4), alpha);

        AssertArranged([W(1), W(4), W(2)], Columns([0.5, 0.5], [0.5, 0.5], [1.0]));
    }

    // A window that is not a tile (here one that cannot tile) leaves the target as it was.
    [Fact]
    public void Activating_a_floating_window_keeps_the_target()
    {
        new[] { 1, 2, 9 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.CannotTile.Add(W(9));
        Start();
        Assert.DoesNotContain(W(9), tiler.Board.On(1));
        Uses(W(1));
        Uses(W(9));

        Opens(W(3), alpha);

        AssertArranged([W(1), W(3), W(2)], Columns([0.5, 0.5], [0.5, 0.5], [1.0]));
    }

    // Review focus 3: a window that opens maximized is restored, and lands in the half it split off.
    [Fact]
    public void A_window_opening_maximized_is_restored_into_its_split()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));

        surface.Maximized.Add(W(2));
        Opens(W(2), alpha);

        Assert.Contains(W(2), surface.Restored);
        Assert.Equal([W(1), W(2)], tiler.Board.On(1));
        // Windows restores it to wherever it was before it was maximized; the Tiler puts it into its slot.
        Settles(W(2), new WindowRect(400, 300, 900, 700));
        AssertArranged([W(1), W(2)], Columns([0.5, 0.5], [1.0], [1.0]));
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap)[1], surface.Frames[W(2)]);
    }

    // An opening comes before any split: W3 fills the room kept bottom right, and W1, the tile last used, keeps its
    // whole column.
    [Fact]
    public void A_new_window_goes_into_an_opening_before_any_split()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var opened = Columns([0.5, 0.5], [1.0], [0.5, 0.5]) with { Opening = 2 };
        Assert.True(manager.SetTileLayout(alpha.Id, AreaKey, opened).IsSuccess);
        var w1Before = surface.Frames[W(1)];
        Uses(W(1));

        Opens(W(3), alpha);

        var (order, filled) = TileArrange.Filled([W(1), W(2)], opened, W(3));
        AssertArranged(order, filled);
        Assert.Equal([W(1), W(2), W(3)], order);
        Assert.Equal(w1Before, surface.Frames[W(1)]);
    }

    // Two windows opened on Alpha while the user was on Beta: the first splits the tile last used (W1, sideways,
    // since it has the whole screen), the second the largest tile. The two halves tie, and the later in slot order
    // (W2) wins; it is taller than wide, so W3 goes below it.
    [Fact]
    public void Several_arrivals_at_once_split_the_target_then_the_largest()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));

        SwitchTo(beta);
        Opens(W(2), alpha);
        Opens(W(3), alpha);
        // Another desktop's windows are never moved: nothing is placed until the user comes back.
        Assert.DoesNotContain(W(2), PlacedWindows);
        Assert.DoesNotContain(W(3), PlacedWindows);
        SwitchTo(alpha);

        AssertArranged([W(1), W(2), W(3)], Columns([0.5, 0.5], [1.0], [0.5, 0.5]));
    }

    // A maximized window is never split (the newcomer would sit behind it). It used to keep a slot that a split had to
    // step around; it now has none (Petre, 2026-11-20: "let's not give space to the maximized window"), so W3 splits
    // W1, the screen's only tile, and W2 stays maximized and off the board.
    [Fact]
    public void A_maximized_window_is_never_the_one_a_new_window_splits()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        surface.Maximized.Add(W(2));
        Settles(W(2), Area);

        Opens(W(3), alpha);

        AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
        Assert.Equal(Area, surface.Frames[W(2)]);
    }

    // Final review M2: every arrival decision leaves a trace line, also when the screen the window opened on is not
    // one the surface reports (unplugged mid-evaluate). It joins that screen, and the line says why.
    [Fact]
    public void An_arrival_on_a_screen_that_is_not_reported_is_traced()
    {
        Exists(W(1), alpha);
        Start();

        Opens(W(3), alpha, screen: 2);

        Assert.Equal([W(3)], tiler.Board.On(2));
        Assert.Single(traced, line => line.StartsWith("tile: ") && line.Contains($"{W(3).Value:X} arrived on screen 2, which is not reported"));
    }

    // A window the bar (or the pipe's `move`) brings over from another workspace splits the tile last used too.
    [Fact]
    public void A_window_brought_from_another_workspace_splits_the_tile_last_used()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Exists(W(3), beta);
        Start();
        Uses(W(1));

        Assert.True(manager.MoveToDesktop(W(3), alpha.DesktopId!.Value).IsSuccess);

        AssertArranged([W(1), W(3), W(2)], Columns([0.5, 0.5], [0.5, 0.5], [1.0]));
    }

    // --- keep the space when a window closes (change 3) -------------------------------------------------------

    // The window went to the tray: the shell drops it from its taskbar list (FakeMonitor.IsListed reads
    // InitialWindows) and WindowMonitor raises Hidden. The window is still alive, so it is not marked dead.
    void Hides(WindowHandle h, uint time = 0)
    {
        monitor.InitialWindows.RemoveAll(w => w.Handle == h);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Hidden, Info(h), time));
    }

    // Two layouts are the same tree with the same shares (to 6 places, Trees.Text) and the same opening. Compared as
    // text, because shares made by halving and rescaling need not be bit-equal to the ones a test writes down.
    static void Same(TileLayout expected, TileLayout actual) => Assert.Equal(Text(expected), Text(actual));

    // Alpha's stored layout for the first fake screen, if it has any.
    Maybe<TileLayout> StoredOnAlpha() =>
        manager.State.Workspaces.First(w => w.Id == alpha.Id).TileLayouts
            .TryFirst(s => s.Screen == AreaKey)
            .Map(s => s.Layout);

    // A 2x2 of W1..W4 on Alpha (W1 top left, W2 below it, W3 top right, W4 bottom right), keep-the-space on or off.
    void TwoByTwo(bool keepsSpace)
    {
        Enumerable.Range(1, 4).ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.True(manager.SetTileKeepsSpace(alpha.Id, keepsSpace).IsSuccess);
        AssertTiled([W(1), W(2), W(3), W(4)]);
    }

    IReadOnlyList<WindowRect> FramesOf(params int[] windows) => windows.Select(n => surface.Frames[W(n)]).ToList();

    [Fact]
    public void A_closing_tile_leaves_its_space_and_nothing_else_moves()
    {
        TwoByTwo(keepsSpace: true);
        var before = FramesOf(1, 2, 3);

        Closes(W(4));

        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Assert.Equal(before, FramesOf(1, 2, 3));
        Assert.Equal(Grid(4).Take(3).ToList(), FramesOf(1, 2, 3));
        Same(TileLayout.Automatic(4) with { Opening = 3 }, StoredOnAlpha().GetValueOrThrow());
        Assert.Single(traced, line => line.StartsWith("tile: ") && line.Contains("space kept"));
    }

    [Fact]
    public void The_next_window_fills_the_kept_space()
    {
        TwoByTwo(keepsSpace: true);
        var w4Was = surface.Frames[W(4)];
        Closes(W(4));
        // W1 last used: without the kept space W5 would split W1 and go below it. The opening comes first.
        Uses(W(1));

        Opens(W(5), alpha);

        Assert.Equal([W(1), W(2), W(3), W(5)], tiler.Board.On(1));
        Assert.Equal(w4Was, surface.Frames[W(5)]);
        AssertTiled([W(1), W(2), W(3), W(5)]);
        // Filled, the grid is even again, and even sizes on the automatic shape are not stored at all.
        Assert.True(StoredOnAlpha().HasNoValue);
    }

    // Review focus 5: many apps close to the tray, which is Hidden, not Disappeared. The space is kept, and when the
    // window shows again it is an arrival like any other, so it goes into the opening, which is the place it left.
    [Fact]
    public void A_tile_hidden_to_the_tray_keeps_its_space_and_fills_it_when_it_shows_again()
    {
        TwoByTwo(keepsSpace: true);
        var w4Was = surface.Frames[W(4)];
        var others = FramesOf(1, 2, 3);

        Hides(W(4));

        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Assert.Equal(others, FramesOf(1, 2, 3));
        Same(TileLayout.Automatic(4) with { Opening = 3 }, StoredOnAlpha().GetValueOrThrow());

        // Shown again: listed again and Appeared, somewhere Windows chose, not its old place.
        Opens(W(4), alpha);

        Assert.Equal([W(1), W(2), W(3), W(4)], tiler.Board.On(1));
        Assert.Equal(w4Was, surface.Frames[W(4)]);
        Assert.Equal(others, FramesOf(1, 2, 3));
        Assert.True(StoredOnAlpha().HasNoValue);
    }

    // A minimize is not a close: change 1's rule, W4's cell goes and W3 takes the whole right column.
    [Fact]
    public void Minimizing_does_not_keep_the_space()
    {
        TwoByTwo(keepsSpace: true);

        Minimizes(W(4), true);

        var closedUp = Columns([0.5, 0.5], [0.5, 0.5], [1.0]);
        AssertArranged([W(1), W(2), W(3)], closedUp);
        Same(closedUp, StoredOnAlpha().GetValueOrThrow());
        Assert.DoesNotContain(traced, line => line.Contains("space kept"));
    }

    // One opening per screen: the second close removes its own cell as in change 1, and the first opening stays
    // where W4 was. W1's column had two rows, so W2 has the whole left column now.
    [Fact]
    public void A_second_close_while_a_space_is_kept_closes_up_as_usual()
    {
        TwoByTwo(keepsSpace: true);
        var w3Was = surface.Frames[W(3)];
        var w4Was = surface.Frames[W(4)];

        Closes(W(4));
        Closes(W(1));

        var expected = Columns([0.5, 0.5], [1.0], [0.5, 0.5]) with { Opening = 2 };
        Same(expected, StoredOnAlpha().GetValueOrThrow());
        AssertArranged([W(2), W(3)], expected);
        Assert.Equal(w3Was, surface.Frames[W(3)]);
        Assert.Equal(w4Was, GridLayout.OpeningOf(Area, 2, Gap, expected).GetValueOrThrow());
        Assert.Single(traced, line => line.Contains("space kept"));
    }

    // Review focus 4: the last tile closing leaves an empty screen, which has no grid to keep a space in.
    [Fact]
    public void The_last_tile_on_a_screen_closing_keeps_nothing()
    {
        Exists(W(1), alpha);
        Start();
        Assert.True(manager.SetTileKeepsSpace(alpha.Id, true).IsSuccess);

        Closes(W(1));

        Assert.Empty(tiler.Board.On(1));
        Assert.True(StoredOnAlpha().HasNoValue);
        Assert.DoesNotContain(traced, line => line.Contains("threw"));
        Assert.DoesNotContain(traced, line => line.Contains("space kept"));

        // Nothing left over: the next window has the whole screen.
        Opens(W(2), alpha);
        AssertTiled([W(2)]);
    }

    // Review finding: W2 closing keeps its space, then W1 closing empties the screen. The kept opening must go with
    // it, or W3 would be laid out in half the screen beside an empty half that nothing can fill.
    [Fact]
    public void When_the_last_tile_closes_the_kept_space_goes_with_it()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.True(manager.SetTileKeepsSpace(alpha.Id, true).IsSuccess);

        Closes(W(2));
        Same(TileLayout.Automatic(2) with { Opening = 1 }, StoredOnAlpha().GetValueOrThrow());
        Closes(W(1));
        Assert.True(StoredOnAlpha().HasNoValue);
        Assert.Contains(traced, line => line.StartsWith("tile: ") && line.Contains("emptied, its opening closed"));

        Opens(W(3), alpha);

        AssertTiled([W(3)]);
        Assert.Equal(Area.Width - 2 * Gap, surface.Frames[W(3)].Width);
        Assert.True(StoredOnAlpha().HasNoValue);
    }

    // --- a leave and an arrival in one evaluate (final review, C2) ---------------------------------------------
    //
    // The leave is settled first (its leaf goes, or is kept as the opening), and only then does the newcomer arrive,
    // into the layout the leave left. The arrival used to be placed first, on a layout the leaver was still in, and
    // the leaver's leaf was taken out afterwards from a layout that had changed under it.

    // The final reviewer's probe: W4 closes and W5 opens on Alpha while the user is on Beta. W4's leaf goes, so W3 has
    // the whole right column, and W5 splits W1, the tile last used. W1 is wider than tall, so W5 goes beside it (the
    // split tree's arrival side rule; the grid put it below, W1 not being alone in its column) and W2 is untouched.
    [Fact]
    public void A_close_and_an_arrival_in_one_evaluate_keep_the_shape_and_split_the_wide_tile_last_used_beside_it()
    {
        Enumerable.Range(1, 4).ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(1));
        var w3Was = surface.Frames[W(3)];

        SwitchTo(beta);
        Closes(W(4));
        Opens(W(5), alpha);
        SwitchTo(alpha);

        var expected = Tree(Across(C(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), .5), C(L, .5)));
        AssertArranged([W(1), W(5), W(2), W(3)], expected);
        Same(expected, StoredOnAlpha().GetValueOrThrow());
        // W3 only grew down into W4's room; it never changed column.
        Assert.Equal(w3Was.Left, surface.Frames[W(3)].Left);
        Assert.Equal(w3Was.Right, surface.Frames[W(3)].Right);
    }

    // With keep-the-space on, the closed tile's space is the opening the arrival fills (precedence step 2), so the
    // newcomer lands exactly where W4 was and nothing else moves. W5 becomes tileable in the same evaluate that takes
    // W4 off the grid, which is how a close and an arrival meet while the user is on the workspace.
    [Fact]
    public void A_close_and_an_arrival_in_one_evaluate_fill_the_kept_space()
    {
        Enumerable.Range(1, 5).ToList().ForEach(n => Exists(W(n), alpha));
        surface.CannotTile.Add(W(5));
        Start();
        Assert.True(manager.SetTileKeepsSpace(alpha.Id, true).IsSuccess);
        AssertTiled([W(1), W(2), W(3), W(4)]);
        Uses(W(1));
        var w4Was = surface.Frames[W(4)];
        var others = FramesOf(1, 2, 3);

        surface.CannotTile.Remove(W(5));
        Closes(W(4));

        AssertTiled([W(1), W(2), W(3), W(5)]);
        Assert.Equal(w4Was, surface.Frames[W(5)]);
        Assert.Equal(others, FramesOf(1, 2, 3));
        Assert.Single(traced, line => line.Contains("space kept"));
        Assert.Contains(traced, line => line.Contains("took the opening"));
    }

    // A shape made by hand survives a leave and an arrival together. Three columns of one tile each plus W2 in the
    // middle column above W3: W2 closes and W5 opens while the user is away. Only W2's cell goes (W3 keeps the whole
    // middle column) and W5 then splits W1, the tile last used, taller than wide, so below it.
    [Fact]
    public void A_custom_shape_survives_a_close_and_an_arrival_in_one_evaluate()
    {
        Enumerable.Range(1, 4).ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var custom = Columns([0.4, 0.3, 0.3], [1.0], [0.5, 0.5], [1.0]);
        Assert.True(manager.SetTileLayout(alpha.Id, AreaKey, custom).IsSuccess);
        AssertArranged([W(1), W(2), W(3), W(4)], custom);
        Uses(W(1));

        SwitchTo(beta);
        Closes(W(2));
        Opens(W(5), alpha);
        SwitchTo(alpha);

        var expected = Columns([0.4, 0.3, 0.3], [0.5, 0.5], [1.0], [1.0]);
        AssertArranged([W(1), W(5), W(3), W(4)], expected);
        Same(expected, StoredOnAlpha().GetValueOrThrow());
    }

    // Final review I2, the design's precedence step 1: a minimized tile coming back goes to its old place BEFORE any
    // opening. W1 (top left) is minimized, then W4 closes with the switch on and keeps its space bottom right. W1
    // comes back top left, and W4's space stays open for the next window. It used to be pulled into the opening.
    [Fact]
    public void A_minimized_tile_comes_back_to_its_place_and_not_into_a_kept_space()
    {
        TwoByTwo(keepsSpace: true);
        var w1Was = surface.Frames[W(1)];

        Minimizes(W(1), true);
        Closes(W(4));
        Minimizes(W(1), false);

        var kept = TileLayout.Automatic(4) with { Opening = 3 };
        AssertArranged([W(1), W(2), W(3)], kept);
        Assert.Equal(w1Was, surface.Frames[W(1)]);
        Assert.Equal(Grid(4).Take(3).ToList(), FramesOf(1, 2, 3));
        Same(kept, StoredOnAlpha().GetValueOrThrow());
        Assert.DoesNotContain(traced, line => line.Contains("took the opening"));
    }

    // The switch is off by default: a close closes up (change 1), and no opening is stored.
    [Fact]
    public void With_the_switch_off_a_close_closes_up()
    {
        TwoByTwo(keepsSpace: false);

        Closes(W(4));

        var closedUp = Columns([0.5, 0.5], [0.5, 0.5], [1.0]);
        AssertArranged([W(1), W(2), W(3)], closedUp);
        Same(closedUp, StoredOnAlpha().GetValueOrThrow());
        Assert.Null(StoredOnAlpha().GetValueOrThrow().Opening);
    }
}
