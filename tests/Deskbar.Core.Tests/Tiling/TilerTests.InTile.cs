using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests.Tiling;

// Tiler.InTile, the bar's tile badge question. Petre: "icons in them are showing if they're tiled or they're not when
// it's active; let's show it for collapsed workspaces as well". The workspace you are on answers from the board; a
// tiled workspace you are not on has no board, so the Tiler answers with its own rule for what takes a tile there.
public sealed partial class TilerTests
{
    [Fact]
    public void InTile_answers_from_the_board_on_the_workspace_you_are_on()
    {
        Exists(W(1), alpha);
        surface.CannotTile.Add(W(2));
        Exists(W(2), alpha);
        Start();

        Assert.True(tiler.InTile(W(1), alpha.Id));
        // A window the grid does not hold is not in a tile, whatever the row says.
        Assert.False(tiler.InTile(W(2), alpha.Id));
    }

    [Fact]
    public void InTile_on_a_tiled_workspace_you_are_not_on_uses_the_tilers_own_rule()
    {
        new[] { 3, 4, 5, 6 }.ToList().ForEach(n => Exists(W(n), beta));
        surface.Minimized.Add(W(4));
        surface.CannotTile.Add(W(5));
        surface.Maximized.Add(W(6));
        Start(betaTiled: true);

        // Nothing is arranged there: the Tiler only acts on the desktop you look at.
        Assert.DoesNotContain(W(3), PlacedWindows);
        Assert.True(tiler.InTile(W(3), beta.Id));
        Assert.False(tiler.InTile(W(4), beta.Id));
        Assert.False(tiler.InTile(W(5), beta.Id));
        Assert.False(tiler.InTile(W(6), beta.Id));
    }

    [Fact]
    public void InTile_is_no_for_an_untiled_workspace_a_lapsed_Pro_and_a_row_that_is_not_a_workspace()
    {
        Exists(W(3), beta);
        Start(alphaTiled: false, betaTiled: false);
        Assert.False(tiler.InTile(W(3), beta.Id));
        Assert.False(tiler.InTile(W(3), null));

        Assert.True(manager.SetWorkspaceTiled(beta.Id, true).IsSuccess);
        Assert.True(tiler.InTile(W(3), beta.Id));

        pro = false;
        Assert.False(tiler.InTile(W(3), beta.Id));
    }
}
