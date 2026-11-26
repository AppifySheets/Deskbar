using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// A task in a window of its own, in the Inbox look (TaskDetailWindow.Inbox.cs, TasksWindow.InboxWindow.cs): while the
// Inbox is the chosen view the window shows the very page the panel shows for the task. Built over the same harness as
// TaskDetailWindowTests (a real TaskService on an in-memory store, the panel behind a TasksPanelRig). The task windows
// are real windows, so run these on the hidden desktop (scripts/test-hidden.ps1).
public class TaskDetailWindowInboxTests
{
    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    static readonly DateTimeOffset Start = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    sealed class Rig : IDisposable
    {
        public required Harness Harness { get; init; }
        public required TasksWindow Panel { get; init; }
        public required TasksBoard Board { get; init; }
        public required TaskService Tasks { get; init; }
        public required string Scratch { get; init; }

        public Guid Sparrow => Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;

        public void Dispose()
        {
            Board.Shutdown();
        }
    }

    static Rig Build(bool inbox = true)
    {
        var harness = Harness.Build();
        var minutes = 0;
        var tasks = new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
        var scratch = Path.Combine(Path.GetTempPath(), "deskbar-task-window-inbox-tests");
#pragma warning disable WPF0001 // ThemeMode is experimental: the app runs under ThemeMode="System" (App.xaml), so the window tests do too
        var panel = new TasksWindow { ThemeMode = ThemeMode.System, Quiet = true, Now = () => Start.AddHours(1), StateDir = scratch, OpenImage = _ => { } };
#pragma warning restore WPF0001
        Assert.True(harness.Manager.SaveTasksPanel(new TasksPanelState(false, 1000) { Inbox = inbox }).IsSuccess);
        var rig = new TasksPanelRig(panel).Docked("right", null);
        var board = new TasksBoard(panel, rig.Panel, tasks, harness.Manager, _ => Result.Success(), now: () => Start.AddHours(1));
        return new Rig { Harness = harness, Panel = panel, Board = board, Tasks = tasks, Scratch = scratch };
    }

    static TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", Start, Maybe<DateTimeOffset>.None);

    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static Button ButtonNamed(object scope, string name) =>
        Inside(scope).OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

    // THE SPIKE (the gate of this feature): a TasksWindow that is never shown draws the page from a view of one task, its
    // page border is moved into another window, and there the page is laid out, a button on it reaches the actions and its
    // comment box takes text and posts it.
    [Fact]
    public void A_page_drawn_by_a_never_shown_window_works_in_another_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Lighter terminal", t.Sparrow, Session("s1")).Value.Id;
        Enumerable.Range(1, 5).ToList().ForEach(i => Assert.True(t.Tasks.Comment(id, Maybe<string>.None, $"Comment {i}").IsSuccess));
        t.Tasks.MarkDone(id);
        var detail = t.Board.DetailViewFor(id);
        Assert.NotNull(detail?.Page);

        var closed = new List<Guid>();
        var posted = new List<string>();
        var seconds = 0;
        // A click a second after the last one, so the click guard (a click right after a redraw is dropped) lets it by.
        var renderer = new TasksWindow
        {
            Quiet = true, Now = () => Start.AddHours(1), StateDir = t.Scratch, OpenImage = _ => { },
            ClickClock = () => Start.UtcDateTime.AddSeconds(seconds++),
        };
        renderer.Actions = t.Panel.Actions! with
        {
            Close = closed.Add,
            Comment = (_, text) =>
            {
                posted.Add(text);
                return true;
            },
        };
        renderer.ShowPage(detail!.Page!, id);
        var host = renderer.TakePageHost();
        var window = new Window { Width = 900, Height = 700, Content = host, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();

            // Laid out: the page has a size, and the page's scroller has a view and was put at the thread's end.
            Assert.True(host.ActualWidth > 600 && host.ActualHeight > 400);
            var scroller = renderer.InboxPageScroller!;
            Assert.True(scroller.ViewportHeight > 100);
            Assert.True(renderer.InboxPageContent is not null);

            // Clickable: the finished task's button reaches the actions.
            ButtonNamed(host, TasksWindow.CloseTaskPageText).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([id], closed);

            // Typing: the comment box takes text, and Enter posts it.
            Assert.True(renderer.TypeComment(id, "A word from the window", Key.Enter));
            Assert.Equal(["A word from the window"], posted);

            // A real key event in the moved tree reaches the box's own handler too (not only the test's shortcut), and
            // the box is in the window's tree, with a presentation source of that window.
            var box = renderer.CommentBoxOf(id)!;
            Assert.Same(window, Window.GetWindow(box));
            box.Text = "Typed for real";
            var source = PresentationSource.FromVisual(box)!;
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Assert.Equal(["A word from the window", "Typed for real"], posted);
        }
        finally
        {
            window.Close();
            renderer.CloseForGood();
        }
    });
}
