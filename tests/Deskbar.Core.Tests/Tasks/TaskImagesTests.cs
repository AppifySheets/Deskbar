using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Images in a task's markdown (TaskImages; docs/superpowers/specs/2026-11-24-task-images-design.md, PR 1). An image is a
// line of its own whose path is relative to Deskbar's state folder; these pin how such lines are told apart from text,
// written, named and made absolute for a session, and that nothing outside task-images counts as one of ours.
public class TaskImagesTests
{
    static readonly Guid Task = Guid.Parse("d4ea6cbe-12ef-420c-8847-fa40c302ac48");

    [Fact]
    public void Split_takes_image_lines_out()
    {
        var parts = TaskImages.Split("Look\n\n![image](task-images/abc/1.png)");

        Assert.Equal("Look", parts.Text);
        Assert.Equal(["task-images/abc/1.png"], parts.Images);
    }

    [Fact]
    public void Split_reads_an_angle_bracketed_path_too()
    {
        var parts = TaskImages.Split("![shot](<task-images/abc/1.png>)");

        Assert.Equal("", parts.Text);
        Assert.Equal(["task-images/abc/1.png"], parts.Images);
    }

    [Theory]
    [InlineData("see ![x](task-images/abc/1.png) here")]
    [InlineData("![x](C:/Windows/a.png)")]
    [InlineData("![x](task-images/../state.json)")]
    [InlineData("![x](https://example.com/a.png)")]
    [InlineData("![x](task-images/abc/run.exe)")]
    public void Split_keeps_foreign_and_inline_images_as_text(string markdown)
    {
        var parts = TaskImages.Split(markdown);

        Assert.Equal(markdown, parts.Text);
        Assert.Empty(parts.Images);
    }

    [Fact]
    public void Join_then_Split_round_trips_text_and_images()
    {
        var joined = TaskImages.Join("Fix **this**", ["task-images/t/1.png", "task-images/t/2.jpg"]);

        Assert.Equal("Fix **this**\n\n![image](task-images/t/1.png)\n![image](task-images/t/2.jpg)", joined);
        var parts = TaskImages.Split(joined);
        Assert.Equal("Fix **this**", parts.Text);
        Assert.Equal(["task-images/t/1.png", "task-images/t/2.jpg"], parts.Images);
    }

    [Fact]
    public void Join_of_images_alone_has_no_blank_line()
    {
        Assert.Equal("![image](task-images/t/1.png)", TaskImages.Join("", ["task-images/t/1.png"]));
        Assert.Equal("![image](task-images/t/1.png)", TaskImages.Join("  \n", ["task-images/t/1.png"]));
    }

    [Fact]
    public void Join_of_text_alone_is_the_text() => Assert.Equal("just text", TaskImages.Join("just text", []));

    [Fact]
    public void RelativeFor_names_by_time_and_index()
    {
        var at = new DateTimeOffset(2026, 11, 24, 5, 6, 7, 89, TimeSpan.Zero);

        Assert.Equal($"task-images/{Task}/20261124-050607-089.png", TaskImages.RelativeFor(Task, at, 0, jpeg: false));
        Assert.Equal($"task-images/{Task}/20261124-050607-089-2.jpg", TaskImages.RelativeFor(Task, at, 2, jpeg: true));
    }

    [Fact]
    public void RelativeFor_uses_universal_time()
    {
        var at = new DateTimeOffset(2026, 11, 24, 9, 6, 7, 89, TimeSpan.FromHours(4));

        Assert.Equal($"task-images/{Task}/20261124-050607-089.png", TaskImages.RelativeFor(Task, at, 0, jpeg: false));
    }

    [Fact]
    public void Expand_makes_paths_absolute() =>
        Assert.Equal(@"a" + "\n" + @"![image](<C:\S\task-images\t\1.png>)", TaskImages.Expand("a\n![image](task-images/t/1.png)", @"C:\S"));

    [Fact]
    public void Expand_leaves_foreign_images_alone() =>
        Assert.Equal("![x](C:/Windows/a.png)", TaskImages.Expand("![x](C:/Windows/a.png)", @"C:\S"));

    [Fact]
    public void Resolve_gives_the_absolute_path_of_one_of_ours() =>
        Assert.Equal(@"C:\S\task-images\t\1.PNG", TaskImages.Resolve("task-images/t/1.PNG", @"C:\S").GetValueOrDefault());

    [Theory]
    [InlineData("task-images/../state.json")]
    [InlineData("task-images/t/../../x.png")]
    [InlineData(@"task-images\t\1.png")]
    [InlineData("task-images/t/a.exe")]
    [InlineData("other/t/1.png")]
    [InlineData("/task-images/t/1.png")]
    [InlineData("task-images/t/1 2.png")]
    public void Resolve_refuses_anything_else(string relative) =>
        Assert.True(TaskImages.Resolve(relative, @"C:\S").HasNoValue);

    // A new task made of pictures alone (the tasks panel's add box, Petre: "let me add just the image without a title
    // too"): it needs a title all the same, so it gets one that says what it is. Words typed with the pictures win.
    [Theory]
    [InlineData("", 1, "Picture")]
    [InlineData("   ", 2, "Pictures")]
    [InlineData("Bar looks wrong", 1, "Bar looks wrong")]
    [InlineData("", 0, "")]
    public void TitleFor_names_a_task_of_pictures_alone(string text, int pictures, string title) =>
        Assert.Equal(title, TaskImages.TitleFor(text, pictures));
}
