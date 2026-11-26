using System.IO;
using System.Reactive.Concurrency;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;

namespace Deskbar.Windows.Tests;

// The picker row and the answer buttons in a task's own window: the window draws the panel's page through a TasksWindow of
// its own with the same actions, so both are there, and an answer's click posts it.
public partial class TaskDetailWindowInboxTests
{
    sealed class NoClaude : IClaudeCli
    {
        public Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout) =>
            Task.FromResult(arguments is ["agents", "--json"] ? Result.Success("[]") : Result.Failure<string>("no claude here"));
    }

    [Fact]
    public void A_task_window_shows_the_picker_row_and_the_answer_buttons() => StaThread.Run(() =>
    {
        using var t = Build();
        var home = Path.Combine(Path.GetTempPath(), "deskbar-window-picker-" + Guid.NewGuid().ToString("N"));
        // A clock at a real moment: the class reckons back from its start.
        var clock = new TestScheduler();
        clock.AdvanceTo(Start.UtcTicks);
        using var sessions = new ClaudeSessions(t.Tasks, t.Harness.Manager, new NoClaude(), clock, _ => Maybe<Guid>.None, home,
            background: Scheduler.Immediate, folderExists: _ => true);
        t.Board.Sessions = sessions;
        var id = t.Tasks.Create("Ship the fix", t.Sparrow,
            Session("s1") with { LaunchedWith = new SessionSettings("sonnet", "low", "acceptEdits") }).Value.Id;
        Assert.True(t.Tasks.Ask(id, "OK to merge?", "s1", ["Yes, merge it", "No, wait"]).IsSuccess);

        var (window, _) = Open(t, id);

        var row = Inside(window).OfType<FrameworkElement>().Single(e => Equals(e.Tag, TasksWindow.PickerRowTag));
        Assert.Equal("Sonnet · low", string.Join(" ", Inside(ButtonNamed(row, TasksWindow.PickerModelName)).OfType<TextBlock>().Select(b => b.Text)));
        Assert.Equal("Edit by itself", string.Join(" ", Inside(ButtonNamed(row, TasksWindow.PickerModeName)).OfType<TextBlock>().Select(b => b.Text)));
        var answers = Inside(window).OfType<StackPanel>().Single(p => Equals(p.Tag, TasksWindow.AnswerButtonsTag));
        Assert.Equal(["Yes, merge it", "No, wait"], Inside(answers).OfType<Button>().Select(b => AutomationProperties.GetName(b)));

        Click(answers, "No, wait");

        Assert.Contains(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == "No, wait" && c.SessionId.HasNoValue);
    });
}
