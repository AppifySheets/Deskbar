using Deskbar.App;
using Xunit;

namespace Deskbar.Windows.Tests;

// The line a window's picture brings into the new task's description (task 494d34ee, WindowShot): which window it is, by
// title and program, so the session knows what it looks at. Pure, so no window is opened.
public class WindowShotTests
{
    [Theory]
    [InlineData("Build failed - Code", "Code", "Picture of the window \"Build failed - Code\" (Code) on screen.")]
    [InlineData("", "explorer", "Picture of the explorer window on screen.")]
    [InlineData("  Inbox  ", "", "Picture of the window \"Inbox\" on screen.")]
    [InlineData("", "", "Picture of the window on screen.")]
    public void The_caption_names_the_window_by_what_can_be_read(string title, string process, string caption) =>
        Assert.Equal(caption, WindowShot.CaptionOf(title, process));

    [Theory]
    [InlineData("", "Picture.", "Picture.")]
    [InlineData("It was green", "Picture.", "It was green\n\nPicture.")]
    public void The_caption_goes_under_the_typed_lines(string body, string caption, string text) =>
        Assert.Equal(text, WindowShot.WithCaption(body, caption));
}
