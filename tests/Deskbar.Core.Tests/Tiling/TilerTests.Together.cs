using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// Tiles that are away AT THE SAME TIME (Tiler.Minimize.cs, "Several tiles away at once"). Petre, 2026-11-23: "when i
// minimize tiles and maximize them, it seems their position may change. shouldn't". His trace: four windows of one
// workspace minimized within 50 ms (a minimize-all) and restored together five seconds later. Only the first had its
// place remembered, because the evaluate that followed its event took every minimized tile off the board before the
// other events were read; it came back alone on its screen, and the others arrived as new windows, splitting the
// tile last used. The top-left tile came back as a full-height column.
//
// Every test here starts from his shape: A over B on the left, C full height on the right, on a 1000x600 screen with
// no gap, so the frames are exact numbers.
public sealed partial class TilerTests
{
    static readonly WindowRect Small = new(0, 0, 1000, 600);
    static readonly TileLayout StackAndColumn = Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5)));

    void StartWithStackAndColumn()
    {
        surface.ScreenList[0] = FakeTileSurface.Screen(1, Small);
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start(gap: 0, layouts: [new ScreenTileLayout("0,0,1000,600", StackAndColumn)]);
    }

    // The shape as it started: W1 top left, W2 bottom left, W3 the right column, and the layout stored as it was.
    void AssertStackAndColumn()
    {
        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Assert.Equal(new WindowRect(0, 0, 500, 300), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(0, 300, 500, 600), surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(500, 0, 1000, 600), surface.Frames[W(3)]);
        Assert.Equal(Text(StackAndColumn), Text(manager.State.Workspaces.Single(w => w.Id == alpha.Id).TileLayouts.Single().Layout));
    }

    // Windows minimizes every window before the first of the events reaches the Tiler, so each window is already
    // minimized when the first event is handled. The events then arrive one by one.
    void MinimizedTogether(params int[] windows)
    {
        windows.ToList().ForEach(n => surface.Minimized.Add(W(n)));
        windows.ToList().ForEach(n => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.MinimizeChanged, Info(W(n)))));
    }

    void RestoredTogether(params int[] windows)
    {
        windows.ToList().ForEach(n => surface.Minimized.Remove(W(n)));
        windows.ToList().ForEach(n => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.MinimizeChanged, Info(W(n)))));
    }

    // His case: everything minimized at once and restored at once comes back exactly as it was.
    [Fact]
    public void Tiles_minimized_together_and_restored_together_come_back_to_their_places()
    {
        StartWithStackAndColumn();

        MinimizedTogether(1, 2, 3);
        Assert.Empty(tiler.Board.On(1));

        RestoredTogether(1, 2, 3);

        AssertStackAndColumn();
    }

    // Restored one at a time, in an order of their own (taskbar clicks): each comes back where it was among the ones
    // already back, and the last one puts the whole shape back. W3 alone has the screen; W1 comes back to the LEFT of
    // it (the stack's place, not a split of the tile last used); W2 comes back below W1.
    [Fact]
    public void Tiles_minimized_together_come_back_one_at_a_time_where_they_were()
    {
        StartWithStackAndColumn();
        MinimizedTogether(1, 2, 3);

        RestoredTogether(3);
        Assert.Equal(new WindowRect(0, 0, 1000, 600), surface.Frames[W(3)]);

        RestoredTogether(1);
        Assert.Equal([W(1), W(3)], tiler.Board.On(1));
        Assert.Equal(new WindowRect(0, 0, 500, 600), surface.Frames[W(1)]);
        Assert.Equal(new WindowRect(500, 0, 1000, 600), surface.Frames[W(3)]);

        RestoredTogether(2);
        AssertStackAndColumn();
    }

    // Minimized one after the other, each with its own click, and restored in the same order (the first one minimized
    // first). The second minimize found the screen exactly as the first one left it, so both remember the screen with
    // all three: W1 comes back to the left of W3, where the stack was, and W2 back below W1.
    [Fact]
    public void Tiles_minimized_one_after_another_come_back_to_their_places_in_any_order()
    {
        StartWithStackAndColumn();

        Minimizes(W(1), true);
        Minimizes(W(2), true);
        Assert.Equal(new WindowRect(0, 0, 1000, 600), surface.Frames[W(3)]);

        Minimizes(W(1), false);
        Assert.Equal([W(1), W(3)], tiler.Board.On(1));
        Assert.Equal(new WindowRect(0, 0, 500, 600), surface.Frames[W(1)]);

        Minimizes(W(2), false);
        AssertStackAndColumn();
    }

    // W1 minimized leaves W2 beside W3, two even columns. That fit is plain and used not to be stored, so the next
    // full re-tile (a switch to another desktop and back) fitted the old three-tile entry again with no leaver, dropped
    // its LAST leaf and stacked W2 over W3 (Tiler.Resize.cs, LayoutOn). The tiles stay where the minimize left them, and
    // W1 still comes back to its place.
    [Fact]
    public void A_desktop_switch_while_a_tile_is_minimized_moves_no_other_tile()
    {
        StartWithStackAndColumn();
        Minimizes(W(1), true);
        Assert.Equal(new WindowRect(0, 0, 500, 600), surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(500, 0, 1000, 600), surface.Frames[W(3)]);

        SwitchTo(beta);
        SwitchTo(alpha);

        Assert.Equal(new WindowRect(0, 0, 500, 600), surface.Frames[W(2)]);
        Assert.Equal(new WindowRect(500, 0, 1000, 600), surface.Frames[W(3)]);

        Minimizes(W(1), false);
        AssertStackAndColumn();
    }

    // A screen that changed while the tiles were away is not put back to the old shape: a window that opened there
    // stays where it went, and the old rule (beside what it was next to) places the returning tile.
    [Fact]
    public void Tiles_minimized_together_do_not_undo_a_window_that_opened_meanwhile()
    {
        StartWithStackAndColumn();
        MinimizedTogether(1, 2);
        Opens(W(4), alpha);

        RestoredTogether(1, 2);

        Assert.Equal(4, tiler.Board.On(1).Count);
        Assert.Contains(W(4), tiler.Board.On(1));
        Assert.DoesNotContain(traced, line => line.Contains($"{W(1).Value:X} came back to its place"));
        Assert.DoesNotContain(manager.State.Workspaces.Single(w => w.Id == alpha.Id).TileLayouts,
            stored => Text(stored.Layout) == Text(StackAndColumn));
    }
}
