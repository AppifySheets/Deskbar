using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// Task 5016d533: a link to a task, written in a pull request or issue, that opens the task in Deskbar when clicked.
// TaskLink builds the web form (the one GitHub keeps) and reads back both it and the deskbar: form Windows hands over.
public class TaskLinkTests
{
    static readonly Guid Id = Guid.Parse("5016d533-39f8-4bb2-a454-6302b3ab2754");

    [Fact]
    public void Builds_the_web_link_the_app_link_and_the_markdown()
    {
        Assert.Equal("https://deskbar.app/task#5016d533-39f8-4bb2-a454-6302b3ab2754", TaskLink.Web(Id));
        Assert.Equal("deskbar://task/5016d533-39f8-4bb2-a454-6302b3ab2754", TaskLink.App(Id));
        Assert.Equal("[Deskbar task 5016d533](https://deskbar.app/task#5016d533-39f8-4bb2-a454-6302b3ab2754)", TaskLink.Markdown(Id));
    }

    // Every form Windows or a browser may hand over names the same id: with or without the slashes, a trailing slash,
    // upper case, escaped, and the web form itself (which Deskbar's own markdown opens without the browser).
    [Theory]
    [InlineData("deskbar://task/5016d533-39f8-4bb2-a454-6302b3ab2754")]
    [InlineData("deskbar://task/5016d533-39f8-4bb2-a454-6302b3ab2754/")]
    [InlineData("deskbar:task/5016d533-39f8-4bb2-a454-6302b3ab2754")]
    [InlineData("DESKBAR://TASK/5016D533-39F8-4BB2-A454-6302B3AB2754")]
    [InlineData("  deskbar://task/5016d533-39f8-4bb2-a454-6302b3ab2754  ")]
    [InlineData("https://deskbar.app/task#5016d533-39f8-4bb2-a454-6302b3ab2754")]
    [InlineData("https://deskbar.app/task/#5016d533-39f8-4bb2-a454-6302b3ab2754")]
    [InlineData("https://www.deskbar.app/task#5016d533-39f8-4bb2-a454-6302b3ab2754")]
    public void Reads_the_whole_id_back_from_every_form(string link) =>
        Assert.Equal("5016d533-39f8-4bb2-a454-6302b3ab2754", TaskLink.TaskIdOf(link).GetValueOrDefault());

    [Theory]
    [InlineData("deskbar://task/5016d533", "5016d533")]
    [InlineData("deskbar://task/5016d533-39f8", "5016d533-39f8")]
    public void Takes_a_start_of_the_id_as_every_task_command_does(string link, string id) =>
        Assert.Equal(id, TaskLink.TaskIdOf(link).GetValueOrDefault());

    // Anything else stays out, so the protocol cannot carry a command, a path or a shorter prefix into the pipe.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deskbar://task/")]
    [InlineData("deskbar://task/5016d5")]
    [InlineData("deskbar://task/5016d533 --close-windows")]
    [InlineData("deskbar://task/5016d533/../windows")]
    [InlineData("deskbar://workspace/5016d533")]
    [InlineData("deskbar://task/zzzzzzzz")]
    [InlineData("https://evil.example/task#5016d533")]
    [InlineData("https://deskbar.app.evil.example/task#5016d533")]
    [InlineData("https://deskbar.app/other#5016d533")]
    [InlineData("ctl")]
    public void Refuses_anything_that_is_not_a_task_link(string? link) =>
        Assert.False(TaskLink.TaskIdOf(link).HasValue);
}
