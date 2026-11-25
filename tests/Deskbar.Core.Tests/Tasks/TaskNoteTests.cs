using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// A note of several lines typed in an add box (TaskNote; task 98420dbc): the first line with words is the title, the rest
// the description. These pin that a one-line note is unchanged, how blank lines and line endings are read, and that
// pictures alone still name the task.
public class TaskNoteTests
{
    [Fact]
    public void One_line_is_a_title_alone() =>
        Assert.Equal(new TaskNoteParts("Bar looks wrong", ""), TaskNote.Split("  Bar looks wrong ", 0));

    [Fact]
    public void Later_lines_are_the_body() =>
        Assert.Equal(new TaskNoteParts("Fix the strip", "it overlaps the taskbar\non the second screen"),
            TaskNote.Split("Fix the strip\nit overlaps the taskbar\non the second screen", 0));

    [Fact]
    public void Windows_line_endings_split_the_same() =>
        Assert.Equal(new TaskNoteParts("Fix the strip", "second line"), TaskNote.Split("Fix the strip\r\nsecond line\r\n", 0));

    [Fact]
    public void Blank_lines_at_the_ends_go_but_those_inside_and_indentation_stay() =>
        Assert.Equal(new TaskNoteParts("Title", "  - one\n\n  - two"), TaskNote.Split("\n\n  Title\n\n\n  - one\n\n  - two  \n\n", 0));

    [Theory]
    [InlineData("", 1, "Picture")]
    [InlineData("\n  \n", 2, "Pictures")]
    [InlineData("", 0, "")]
    public void Nothing_typed_is_named_by_the_pictures(string text, int pictures, string title) =>
        Assert.Equal(new TaskNoteParts(title, ""), TaskNote.Split(text, pictures));
}
