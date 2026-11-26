using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// A task in a window of its own (task dca4ebeb; TaskDetailWindow, TasksBoard.OpenWindow). Built over the same harness as
// the board's tests (TasksBoardTests): a real TaskService on an in-memory store, the panel behind a TasksPanelRig whose
// seams are all faked. The task windows are real windows, so run these on the hidden desktop (scripts/test-hidden.ps1).
public class TaskDetailWindowTests
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
        public required List<nint> Brought { get; init; }
        public required List<Uri> Opened { get; init; }

        public void Dispose()
        {
            Board.Shutdown();
        }
    }

    static Rig Build()
    {
        var harness = Harness.Build();
        var minutes = 0;
        var tasks = new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
        var scratch = Path.Combine(Path.GetTempPath(), "deskbar-task-window-tests");
#pragma warning disable WPF0001 // ThemeMode is experimental: the app runs under ThemeMode="System" (App.xaml), so the window tests do too
        var panel = new TasksWindow { ThemeMode = ThemeMode.System, Quiet = true, Now = () => Start.AddHours(1), StateDir = scratch, OpenImage = _ => { } };
#pragma warning restore WPF0001
        Assert.True(harness.Manager.SaveTasksPanel(new TasksPanelState(false, 1000)).IsSuccess);
        var rig = new TasksPanelRig(panel).Docked("right", null);
        var brought = new List<nint>();
        var opened = new List<Uri>();
        var board = new TasksBoard(panel, rig.Panel, tasks, harness.Manager, _ => Result.Success(), now: () => Start.AddHours(1))
        {
            // A scratch picture folder, and a click on a picture recorded rather than opening a viewer.
            NewTaskWindow = () => new TaskDetailWindow { StateDir = scratch, OpenImage = _ => { }, Now = () => Start.AddHours(1) },
            // Recorded instead of moved between desktops and activated: the App's seam.
            BringTaskWindowForward = brought.Add,
            // A link's final open (task 26f15d70) recorded, never a real browser.
            OpenUri = opened.Add,
        };
        return new Rig { Harness = harness, Panel = panel, Board = board, Tasks = tasks, Brought = brought, Opened = opened };
    }

    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static int CommentsShown(TaskDetailWindow window) =>
        Inside(window).OfType<FrameworkElement>().Count(e => Equals(e.Tag, TaskDetailWindow.CommentTag));

    // The whole task, in a window titled after it: the description and EVERY comment, where the panel shows the latest
    // three, each cut to three lines.
    [Fact]
    public void Open_in_window_shows_the_whole_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Store listing refresh", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Tasks.Describe(id, "New screenshots and copy.\n\n### What we know\n- free for personal use").IsSuccess);
        Enumerable.Range(1, 5).ToList().ForEach(i => Assert.True(t.Tasks.Comment(id, Maybe<string>.None, $"Comment {i}").IsSuccess));

        Assert.True(t.Board.OpenWindow(id).IsSuccess);

        var window = t.Board.WindowOf(id);
        Assert.NotNull(window);
        Assert.True(window.IsVisible);
        Assert.StartsWith("Store listing refresh", window.Title);
        Assert.Equal(5, CommentsShown(window));
        Assert.Contains(Inside(window).OfType<RichTextBox>(), box => Equals(box.Tag, TaskDetailWindow.DescriptionTag));
        Assert.Single(t.Brought);
    });

    // Task 26f15d70: a link in the task window's description goes to the task's workspace first, as in the panel.
    [Fact]
    public void A_link_in_the_window_opens_in_the_tasks_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var sparrow = t.Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow");
        var id = t.Tasks.AddNote("Services ticket", sparrow.Id).Value.Id;
        Assert.True(t.Tasks.Describe(id, "The [ticket](https://example.com/browse/SHAR-13).").IsSuccess);
        Assert.True(t.Board.OpenWindow(id).IsSuccess);

        var description = Inside(t.Board.WindowOf(id)!).OfType<RichTextBox>().Single(box => Equals(box.Tag, TaskDetailWindow.DescriptionTag));
        var link = Assert.Single(description.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()));
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));

        Assert.Equal([sparrow.DesktopId!.Value], t.Harness.Desktops.Switched);
        Assert.Equal([new Uri("https://example.com/browse/SHAR-13")], t.Opened);
    });

    // Task 3757a3e1: the window's workspace chip offers the move, the compact workspace menu under the chip, and a pick
    // moves the task.
    [Fact]
    public void The_windows_workspace_chip_moves_the_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var sparrow = t.Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow");
        var id = t.Tasks.AddNote("Services ticket", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;

        var chip = Inside(window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == TaskDetailWindow.PlaceChipName);
        chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var move = Assert.IsAssignableFrom<ContextMenu>(window.MoveMenu);
        Assert.Same(chip, move.PlacementTarget);
        WorkspaceChipMenu.Chips(move).Single(c => AutomationProperties.GetName(c) == "Sparrow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(sparrow.Id, t.Tasks.Board.Tasks.Single(x => x.Id == id).WorkspaceId.Value);
    });

    // One window per task: asking again brings the same window forward, and an unknown task opens nothing.
    [Fact]
    public void Opening_a_task_again_brings_its_window_forward() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Long brief", Maybe<Guid>.None).Value.Id;

        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var first = t.Board.WindowOf(id);
        Assert.True(t.Board.OpenWindow(id).IsSuccess);

        Assert.Same(first, t.Board.WindowOf(id));
        Assert.Equal(2, t.Brought.Count);
        Assert.Equal(t.Brought[0], t.Brought[1]);
        Assert.True(t.Board.OpenWindow(Guid.NewGuid()).IsFailure);
    });

    // The window follows the task (a comment from a session shows at once, panel closed or not) and closes when the task
    // is deleted; the board lets go of it.
    [Fact]
    public void The_window_follows_its_task_and_closes_when_it_is_deleted() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Long brief", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;

        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, "A new finding", wakes: false).IsSuccess);
        Assert.Equal(1, CommentsShown(window));

        Assert.True(t.Tasks.Delete(id).IsSuccess);
        Assert.False(window.IsVisible);
        Assert.False(t.Board.HasWindow(id));
    });

    // Edit in the window (Petre's pick): the title and the description saved together through the panel's own Edit.
    [Fact]
    public void Editing_in_the_window_saves_the_title_and_description() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Short title", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;

        window.BeginEdit();
        Assert.True(window.IsEditing);
        window.TitleBox.Text = "A better title";
        window.DescriptionBox.Text = "A long brief.\n\n1. first\n2. second";
        Assert.True(window.SaveEdit());

        var task = t.Tasks.Board.Tasks.Single(x => x.Id == id);
        Assert.Equal("A better title", task.Title);
        Assert.Equal("A long brief.\n\n1. first\n2. second", task.Description);
        Assert.False(window.IsEditing);
    });

    // Esc in the editor keeps nothing.
    [Fact]
    public void Escape_in_the_editor_keeps_nothing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Keep me", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;

        Assert.True(window.Press(System.Windows.Input.Key.F2, System.Windows.Input.ModifierKeys.None));
        window.TitleBox.Text = "Changed";
        Assert.True(window.Press(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));

        Assert.False(window.IsEditing);
        Assert.Equal("Keep me", t.Tasks.Board.Tasks.Single(x => x.Id == id).Title);
    });

    // The comment box posts as Petre and empties; Ctrl+Enter is the key, a plain Enter is a new line.
    [Fact]
    public void The_comment_box_posts_and_empties() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Long brief", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;

        window.CommentDraft = "Looks right to me";
        Assert.True(window.Post());

        var comment = Assert.Single(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments);
        Assert.Equal("Looks right to me", comment.Markdown);
        Assert.True(comment.SessionId.HasNoValue);
        Assert.Equal("", window.CommentDraft);
        Assert.Equal(1, CommentsShown(window));
    });

    // The one shared place (Petre: "remember size and position") is saved when a task window closes, and the next task
    // window opens there.
    [Fact]
    public void A_closed_window_saves_the_place_the_next_one_opens_at() => StaThread.Run(() =>
    {
        using var t = Build();
        var first = t.Tasks.AddNote("First", Maybe<Guid>.None).Value.Id;
        var second = t.Tasks.AddNote("Second", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(first).IsSuccess);
        var window = t.Board.WindowOf(first)!;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 140;
        window.Top = 120;
        window.Width = 700;
        window.Height = 500;
        window.Close();

        var saved = t.Harness.Manager.State.TaskWindow;
        Assert.NotNull(saved);
        Assert.False(t.Board.HasWindow(first));

        Assert.True(t.Board.OpenWindow(second).IsSuccess);
        var next = t.Board.WindowOf(second)!;
        var hwnd = next.EnsureHandle();
        var placement = new Monitoring.NativeMethods.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Monitoring.NativeMethods.WINDOWPLACEMENT>() };
        Assert.True(Monitoring.NativeMethods.GetWindowPlacement(hwnd, ref placement));
        Assert.Equal(saved.Left, placement.rcNormalPosition.Left);
        Assert.Equal(saved.Top, placement.rcNormalPosition.Top);
        Assert.Equal(saved.Right - saved.Left, placement.rcNormalPosition.Right - placement.rcNormalPosition.Left);
    });

    // A subtask in the side column opens in a window of its own.
    [Fact]
    public void A_subtask_opens_in_its_own_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", Maybe<Guid>.None).Value.Id;
        var child = t.Tasks.AddNote("Child", Maybe<Guid>.None, parent: parent).Value.Id;
        Assert.True(t.Board.OpenWindow(parent).IsSuccess);
        var window = t.Board.WindowOf(parent)!;

        var entry = Inside(window).OfType<Button>().First(b => AutomationProperties.GetName(b) == "Child");
        entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(t.Board.HasWindow(child));
        Assert.NotSame(window, t.Board.WindowOf(child));
    });

    // Task 54b39c4c (Petre: "subtask should show its parent and have a way to open the parent easily"): a subtask's side
    // column has a Parent section, a button that opens the parent in a window of its own. A task with no parent has none.
    [Fact]
    public void A_subtasks_parent_opens_in_its_own_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", Maybe<Guid>.None).Value.Id;
        var child = t.Tasks.AddNote("Child", Maybe<Guid>.None, parent: parent).Value.Id;
        Assert.True(t.Board.OpenWindow(child).IsSuccess);
        var window = t.Board.WindowOf(child)!;

        var entry = Inside(window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Parent Parent");
        entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(t.Board.HasWindow(parent));
        Assert.NotSame(window, t.Board.WindowOf(parent));
        Assert.DoesNotContain(Inside(t.Board.WindowOf(parent)!).OfType<Button>(),
            b => AutomationProperties.GetName(b)?.StartsWith("Parent ") == true);
    });

    // Recurring tasks (task 0b78269e, Task 8): the window's "⋯" menu carries the same Repeat submenu as the panel's.
    [Fact]
    public void The_window_menu_has_Repeat_too() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Weekly look", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;

        var menu = window.OpenMenu();
        var heads = menu.Items.OfType<MenuItem>().ToDictionary(i => (i.Header as TextBlock)?.Text ?? "", i => i);
        menu.IsOpen = false;

        Assert.Equal([.. TaskRepeats.MenuChoices.Select(c => c.Label), "Custom…"],
            heads["Repeat"].Items.OfType<MenuItem>().Select(i => ((TextBlock)i.Header).Text));
    });

    // "Open in window" sits right after Edit on the panel's line menu.
    [Fact]
    public void The_panels_line_menu_offers_open_in_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Long brief", Maybe<Guid>.None).Value.Id;
        t.Board.Show();

        var menu = t.Panel.OpenMenuFor(id);
        var headers = menu.Items.OfType<MenuItem>().Select(i => (i.Header as TextBlock)?.Text ?? i.Header?.ToString()).ToArray();
        menu.IsOpen = false;

        Assert.Equal("Edit", headers[0]);
        Assert.Equal(TasksWindow.OpenInWindowText, headers[1]);
    });
}
