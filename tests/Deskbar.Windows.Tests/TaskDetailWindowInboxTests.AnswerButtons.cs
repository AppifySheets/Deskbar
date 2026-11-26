using System.Windows.Automation;
using System.Windows.Controls;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The answer buttons in a task's own window: the window draws the panel's page through a TasksWindow of its own with the
// same actions, so they are there, and an answer's click posts it.
public partial class TaskDetailWindowInboxTests
{
    [Fact]
    public void A_task_window_shows_the_answer_buttons() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Ship the fix", t.Sparrow, Session("s1")).Value.Id;
        Assert.True(t.Tasks.Ask(id, "OK to merge?", "s1", ["Yes, merge it", "No, wait"]).IsSuccess);

        var (window, _) = Open(t, id);

        var answers = Inside(window).OfType<StackPanel>().Single(p => Equals(p.Tag, TasksWindow.AnswerButtonsTag));
        Assert.Equal(["Yes, merge it", "No, wait"], Inside(answers).OfType<Button>().Select(b => AutomationProperties.GetName(b)));

        Click(answers, "No, wait");

        Assert.Contains(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == "No, wait" && c.SessionId.HasNoValue);
    });
}
