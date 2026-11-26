using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The tasks panel's resizable columns (task 3df2d28f, TaskColumnWidths): how a divider's drag becomes a width.
public class TaskColumnsTests
{
    static readonly TaskColumnWidths Start = TaskColumnWidths.Default;

    // The line is anchored on its right, so the divider on a column's LEFT edge widens it when dragged left and narrows
    // it when dragged right, and no other column changes.
    [Theory]
    [InlineData(-30, 100)]
    [InlineData(20, 50)]
    [InlineData(0, 70)]
    public void Dragging_a_columns_left_divider_left_widens_it(double moved, double expected)
    {
        var dragged = TaskColumnWidths.Dragged(Start, TaskColumn.Sessions, moved, room: 300);

        Assert.Equal(expected, dragged.Sessions);
        Assert.Equal(Start with { Sessions = expected }, dragged);
    }

    // A column never goes under MinWidth, however far right its divider is dragged.
    [Fact]
    public void A_column_is_never_narrower_than_the_minimum() =>
        Assert.Equal(TaskColumnWidths.MinWidth, TaskColumnWidths.Dragged(Start, TaskColumn.Workspace, 500, room: 300).Workspace);

    // Widening stops at the room the line can give (the title over its floor, the menu strip over "⋯"): 300 of it lets a
    // column grow by 300.
    [Fact]
    public void A_column_grows_only_as_far_as_the_title_can_give() =>
        Assert.Equal(Start.Status + 300,
            TaskColumnWidths.Dragged(Start, TaskColumn.Status, -1000, room: 300).Status);

    // A line with no room to give lets nothing grow, but a column can still be narrowed to give the title room back.
    [Fact]
    public void A_squeezed_title_still_lets_a_column_shrink()
    {
        Assert.Equal(Start.Age, TaskColumnWidths.Dragged(Start, TaskColumn.Age, -40, room: 0).Age);
        Assert.Equal(Start.Age - 10, TaskColumnWidths.Dragged(Start, TaskColumn.Age, 10, room: 0).Age);
    }

    // The widths read and set by column, in the order they stand.
    [Fact]
    public void Each_column_reads_and_sets_its_own_width() =>
        Enum.GetValues<TaskColumn>().Select((column, i) => (column, i)).ToList().ForEach(c =>
        {
            var set = Start.With(c.column, 123);
            Assert.Equal(123, set[c.column]);
            Assert.Equal(123, set.InOrder[c.i]);
        });

    // The defaults leave a full line's text columns far narrower than the old fixed line's 366 DIP for the same five.
    [Fact]
    public void The_default_text_columns_come_to_under_300_DIP() => Assert.True(Start.InOrder.Sum() <= 300);
}
