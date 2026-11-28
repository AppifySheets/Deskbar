using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// Where an arrow key goes in the workspace menu (ChipGrid, task f94524ce: "can't go to other grouped workspace").
// The fixture is the menu's shape: a block of three chips on one line and two on the next, then a group's caption
// (no chips, so no line) and a block of three chips on one line.
public class ChipGridTests
{
    static readonly ChipBox[] Menu =
    [
        // Ungrouped, line 1: 0, 1, 2.
        new(0, 0, 50, 20), new(53, 0, 60, 20), new(116, 0, 40, 20),
        // Ungrouped, line 2: 3, 4.
        new(0, 23, 80, 20), new(83, 23, 50, 20),
        // A group's caption sits from 49 to 63; its block's line: 5, 6, 7.
        new(0, 66, 30, 20), new(33, 66, 90, 20), new(126, 66, 40, 20),
    ];

    [Fact]
    public void Right_and_left_walk_every_chip_in_order_and_wrap()
    {
        Assert.Equal(3, ChipGrid.Next(Menu, 2, ChipStep.Right).Value);
        Assert.Equal(5, ChipGrid.Next(Menu, 4, ChipStep.Right).Value);
        Assert.Equal(0, ChipGrid.Next(Menu, 7, ChipStep.Right).Value);
        Assert.Equal(4, ChipGrid.Next(Menu, 5, ChipStep.Left).Value);
        Assert.Equal(7, ChipGrid.Next(Menu, 0, ChipStep.Left).Value);
    }

    [Fact]
    public void Down_crosses_into_the_next_group_to_the_nearest_chip()
    {
        // Chip 4's middle is at 108; chip 6's at 78 is nearer than chip 7's at 146.
        Assert.Equal(6, ChipGrid.Next(Menu, 4, ChipStep.Down).Value);
        // Chip 1's middle is at 83; chip 3's at 40 and chip 4's at 108: chip 4 is nearer.
        Assert.Equal(4, ChipGrid.Next(Menu, 1, ChipStep.Down).Value);
    }

    [Fact]
    public void Up_crosses_out_of_a_group_to_the_line_above()
    {
        // From the group's first chip (middle 15) up to the line above it, chip 3 (middle 40).
        Assert.Equal(3, ChipGrid.Next(Menu, 5, ChipStep.Up).Value);
        // And on up to the first line, chip 0 (middle 25).
        Assert.Equal(0, ChipGrid.Next(Menu, 3, ChipStep.Up).Value);
    }

    [Fact]
    public void Up_and_down_wrap_past_the_ends()
    {
        Assert.Equal(7, ChipGrid.Next(Menu, 2, ChipStep.Up).Value);
        Assert.Equal(0, ChipGrid.Next(Menu, 5, ChipStep.Down).Value);
    }

    [Fact]
    public void One_line_leaves_up_and_down_where_they_were_and_a_stranger_goes_nowhere()
    {
        ChipBox[] line = [new(0, 0, 50, 20), new(53, 0, 50, 20)];
        Assert.Equal(1, ChipGrid.Next(line, 1, ChipStep.Down).Value);
        Assert.Equal(0, ChipGrid.Next(line, 0, ChipStep.Up).Value);
        Assert.True(ChipGrid.Next(line, -1, ChipStep.Right).HasNoValue);
        Assert.True(ChipGrid.Next([], 0, ChipStep.Down).HasNoValue);
    }
}
