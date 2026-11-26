using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
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
public partial class TaskDetailWindowInboxTests
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
        public required List<nint> Brought { get; init; }
        public required List<Uri> Opened { get; init; }

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
        var brought = new List<nint>();
        var opened = new List<Uri>();
        var seconds = 0;
        var board = new TasksBoard(panel, rig.Panel, tasks, harness.Manager, _ => Result.Success(), now: () => Start.AddHours(1))
        {
            // A scratch picture folder, a click on a picture recorded rather than opening a viewer, and a click a second
            // after the last one, so the click guard (a click right after a redraw is dropped) lets it by.
            NewTaskWindow = () => new TaskDetailWindow
            {
                StateDir = scratch, OpenImage = _ => { }, Now = () => Start.AddHours(1),
                PageClickClock = () => Start.UtcDateTime.AddSeconds(seconds++),
            },
            BringTaskWindowForward = brought.Add,
            // A link's final open recorded, never a real browser.
            OpenUri = opened.Add,
        };
        return new Rig { Harness = harness, Panel = panel, Board = board, Tasks = tasks, Scratch = scratch, Brought = brought, Opened = opened };
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

    static Visibility HeaderOf(TaskDetailWindow window) => ((FrameworkElement)window.FindName("Header")).Visibility;

    // Inbox off: the window is the old one, nothing of the page, and every other test of the old window stands.
    [Fact]
    public void With_the_inbox_off_the_window_is_the_old_one() => StaThread.Run(() =>
    {
        using var t = Build(inbox: false);
        var id = t.Tasks.AddNote("Long brief", Maybe<Guid>.None).Value.Id;

        Assert.True(t.Board.OpenWindow(id).IsSuccess);

        var window = t.Board.WindowOf(id)!;
        Assert.False(window.ShowsInboxPage);
        Assert.Equal(Visibility.Visible, HeaderOf(window));
        Assert.Null(t.Board.DetailViewFor(id)!.Page);
    });

    // Inbox on: the page is the window's content: its mark, its title, its comment box, no back arrow, no old header.
    [Fact]
    public void With_the_inbox_on_the_window_shows_the_page_without_a_back_arrow() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Store listing refresh", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Tasks.Describe(id, "New screenshots and copy.").IsSuccess);
        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, "A first finding").IsSuccess);

        Assert.True(t.Board.OpenWindow(id).IsSuccess);

        var window = t.Board.WindowOf(id)!;
        Assert.True(window.IsVisible);
        Assert.True(window.ShowsInboxPage);
        Assert.Equal(Visibility.Collapsed, HeaderOf(window));
        Assert.StartsWith("Store listing refresh", window.Title);
        Assert.Equal(id, window.TaskId);
        Assert.Contains(Inside(window).OfType<FrameworkElement>(), e => Equals(e.Tag, "inbox-mark"));
        Assert.DoesNotContain(Inside(window).OfType<Button>(), b => AutomationProperties.GetName(b) == TasksWindow.InboxPageBackText);
        var box = window.PageRenderer!.CommentBoxOf(id)!;
        Assert.Same(window, Window.GetWindow(box));
        Assert.Single(t.Brought);
    });

    // The page follows its task (a new comment shows, the title moves) and the window closes when the task is deleted.
    [Fact]
    public void The_page_follows_its_task_and_the_window_closes_when_it_is_deleted() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Long brief", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;
        Assert.Empty(window.PageRenderer!.ShownComments(id));

        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, "A new finding", wakes: false).IsSuccess);
        Assert.Single(window.PageRenderer!.ShownComments(id));
        Assert.True(t.Tasks.Rename(id, "A better title").IsSuccess);
        Assert.StartsWith("A better title", window.Title);

        Assert.True(t.Tasks.Delete(id).IsSuccess);
        Assert.False(window.IsVisible);
        Assert.False(t.Board.HasWindow(id));
    });

    static bool HasButtonNamed(object scope, string name) =>
        Inside(scope).OfType<Button>().Any(b => AutomationProperties.GetName(b) == name);

    static void Click(object scope, string name) =>
        ButtonNamed(scope, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static (TaskDetailWindow Window, TasksWindow Page) Open(Rig t, Guid id)
    {
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        return (t.Board.WindowOf(id)!, t.Board.WindowOf(id)!.PageRenderer!);
    }

    // The old window showed the whole thread: no "N earlier comments" fold, no comment cut to three lines.
    [Fact]
    public void The_page_in_the_window_shows_every_comment_whole() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Long brief", Maybe<Guid>.None).Value.Id;
        Enumerable.Range(1, 6).ToList().ForEach(i => Assert.True(t.Tasks.Comment(id, Maybe<string>.None, $"Comment {i}").IsSuccess));
        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Line {i}"))).IsSuccess);
        var (window, page) = Open(t, id);

        Assert.Equal(7, page.ShownComments(id).Count);
        Assert.Empty(Inside(window).OfType<Button>().Where(b => AutomationProperties.GetName(b) is "Earlier comments" or "Show less" && b.Visibility == Visibility.Visible));
        Assert.Empty(Inside(window).OfType<Button>().Where(b => AutomationProperties.GetName(b) == "Show more" && b.Visibility == Visibility.Visible).Select(b => $"{b.ToolTip} {b.ActualHeight}"));

        // A comment that arrives later is whole too.
        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Later {i}")), wakes: false).IsSuccess);
        Assert.Equal(8, page.ShownComments(id).Count);
        Assert.Empty(Inside(window).OfType<Button>().Where(b => AutomationProperties.GetName(b) == "Show more" && b.Visibility == Visibility.Visible).Select(b => $"{b.ToolTip} {b.ActualHeight}"));
    });

    // The old header's Undo: a close can be undone for its six seconds, from the window's page, which stays up meanwhile.
    [Fact]
    public void A_close_can_be_undone_from_the_page() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        var (window, page) = Open(t, id);
        Assert.False(HasButtonNamed(window, TasksWindow.UndoText));

        t.Panel.Actions!.Close(id);

        Assert.True(window.IsVisible);
        Assert.True(page.IsClosing(id));
        Click(window, TasksWindow.UndoText);
        Assert.False(page.IsClosing(id));
        Assert.False(HasButtonNamed(window, TasksWindow.UndoText));
        Assert.NotEqual(Deskbar.Core.Tasks.TaskStatus.Closed, t.Tasks.Board.Find(id.ToString()).Value.Status);
    });

    // The old chips: Important and Urgent toggle, each a press of the same SetFlag.
    [Fact]
    public void Important_and_urgent_toggle_from_the_page() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        var (window, _) = Open(t, id);

        Click(window, TasksWindow.ImportantText);
        Assert.True(t.Tasks.Board.Find(id.ToString()).Value.Important);
        Assert.False(t.Tasks.Board.Find(id.ToString()).Value.Urgent);
        Click(window, TasksWindow.UrgentText);
        Assert.True(t.Tasks.Board.Find(id.ToString()).Value.Urgent);
        Click(window, TasksWindow.ImportantText);
        Assert.False(t.Tasks.Board.Find(id.ToString()).Value.Important);
    });

    // A subtask and a parent open in windows of their own, as they always did; the panel's lists are never touched.
    [Fact]
    public void A_subtask_and_a_parent_open_in_their_own_windows() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", Maybe<Guid>.None).Value.Id;
        var child = t.Tasks.AddNote("Child", Maybe<Guid>.None, parent: parent).Value.Id;
        t.Board.Show();
        var (window, _) = Open(t, parent);

        Click(window, "Subtask Child");

        Assert.True(t.Board.HasWindow(child));
        Assert.NotSame(window, t.Board.WindowOf(child));
        Assert.Null(t.Panel.InboxPageShown);
        Click(t.Board.WindowOf(child)!, "Parent Parent");
        Assert.Null(t.Panel.InboxPageShown);
        // The parent's window was open already: its window is brought forward, not made again.
        Assert.Equal(3, t.Brought.Count);
    });

    // "Show in the tasks panel" is on the window's menu, "Open in window" is not; and the item shows the task in the panel.
    [Fact]
    public void The_pages_menu_shows_in_the_panel_and_does_not_open_a_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        t.Board.Show();
        var (_, page) = Open(t, id);

        var menu = page.OpenMenuFor(id);

        var names = menu.Items.OfType<MenuItem>().Select(i => (i.Header as TextBlock)?.Text).ToList();
        Assert.Contains(TasksWindow.ShowInPanelText, names);
        Assert.DoesNotContain(TasksWindow.OpenInWindowText, names);
        Assert.Contains("Edit", names);
        Assert.Contains("Delete", names);
        menu.Items.OfType<MenuItem>().Single(i => (i.Header as TextBlock)?.Text == TasksWindow.ShowInPanelText)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        Assert.Equal(id, t.Panel.InboxPageShown);
    });

    // The comment box posts as the panel's does, and what is typed outlives a redraw.
    [Fact]
    public void The_comment_box_posts_and_keeps_a_draft_over_a_redraw() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        var (_, page) = Open(t, id);

        Assert.False(page.TypeComment(id, "Half a thought", null));
        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, "Arrived meanwhile", wakes: false).IsSuccess);
        Assert.Equal("Half a thought", page.CommentDraft(id));

        Assert.True(page.TypeComment(id, "Done now", Key.Enter));
        Assert.Contains(t.Tasks.Board.Find(id.ToString()).Value.Comments, c => c.Markdown == "Done now");
        Assert.Equal("", page.CommentDraft(id));
    });

    // F2 edits the title and description in place, Enter saves through Edit; Esc keeps nothing and never goes "back".
    [Fact]
    public void F2_edits_in_place_and_Esc_goes_nowhere() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        var (window, page) = Open(t, id);

        Assert.True(window.Press(Key.F2, ModifierKeys.None));
        Assert.NotNull(page.Editor);
        Assert.True(window.Press(Key.Escape, ModifierKeys.None));
        Assert.Null(page.Editor);
        Assert.False(window.Press(Key.Escape, ModifierKeys.None));
        Assert.True(window.ShowsInboxPage);
        Assert.Equal(id, page.InboxPageShown);

        Assert.True(window.Press(Key.F2, ModifierKeys.None));
        page.Editor!.Text = "Errand, renamed";
        page.SaveEditor();
        Assert.Equal("Errand, renamed", t.Tasks.Board.Find(id.ToString()).Value.Title);
        Assert.True(window.IsVisible);
    });

    // A link in the description goes to the task's workspace first, as in the panel and the old window.
    [Fact]
    public void A_link_in_the_page_opens_in_the_tasks_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Services ticket", t.Sparrow).Value.Id;
        Assert.True(t.Tasks.Describe(id, "The [ticket](https://example.com/browse/SHAR-13).").IsSuccess);
        var (window, _) = Open(t, id);

        var link = Inside(window).OfType<RichTextBox>().SelectMany(b => b.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>())).Single();
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));

        Assert.Equal([t.Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").DesktopId!.Value], t.Harness.Desktops.Switched);
        Assert.Equal([new Uri("https://example.com/browse/SHAR-13")], t.Opened);
    });

    // A refusal on no task shows above the page, as it showed in the old header.
    [Fact]
    public void A_refusal_on_no_task_shows_above_the_page() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        var (window, _) = Open(t, id);

        window.ShowProblem("That did not work");

        Assert.Contains(Inside(window).OfType<TextBlock>(), b => b.Text == "That did not work" && b.Visibility == Visibility.Visible);
        window.ShowProblem(null);
        Assert.DoesNotContain(Inside(window).OfType<TextBlock>(), b => b.Text == "That did not work" && b.Visibility == Visibility.Visible);
    });

    // Stats laid over the Inbox does not flip a window: the look follows the chosen view, not what is on top of it.
    [Fact]
    public void Stats_over_the_inbox_leaves_the_window_as_it_is() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        t.Board.Show();
        var (window, _) = Open(t, id);

        t.Panel.Actions!.ShowStats!();

        Assert.True(window.ShowsInboxPage);
    });

    // The switch flips with a window open: the old window comes back, then the page again, and a comment typed meanwhile
    // goes with the window both ways.
    [Fact]
    public void Flipping_the_switch_swaps_the_look_and_keeps_a_typed_comment() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, "First", wakes: false).IsSuccess);
        t.Board.Show();
        var (window, page) = Open(t, id);
        var inboxBackground = window.Background;
        page.TypeComment(id, "Typed on the page", null);

        t.Panel.Actions!.ShowMatrix(false);

        Assert.False(window.ShowsInboxPage);
        Assert.Equal(Visibility.Visible, HeaderOf(window));
        Assert.Equal("Typed on the page", window.CommentDraft);
        Assert.Equal(1, Inside(window).OfType<FrameworkElement>().Count(e => Equals(e.Tag, TaskDetailWindow.CommentTag)));
        Assert.NotSame(inboxBackground, window.Background);

        // A change while the old look shows is drawn when the page comes back, and what was typed there goes back too.
        window.CommentDraft = "Typed in the old window";
        Assert.True(t.Tasks.Comment(id, Maybe<string>.None, "Second", wakes: false).IsSuccess);
        Assert.Equal(2, Inside(window).OfType<FrameworkElement>().Count(e => Equals(e.Tag, TaskDetailWindow.CommentTag)));
        t.Panel.Actions!.ShowInbox!(true);

        Assert.True(window.ShowsInboxPage);
        Assert.Equal(Visibility.Collapsed, HeaderOf(window));
        Assert.Equal("Typed in the old window", window.PageRenderer!.CommentDraft(id));
        Assert.Equal(2, window.PageRenderer!.ShownComments(id).Count);
        Assert.Same(inboxBackground, window.Background);
    });

    // The window's page and the panel's page of the same task share nothing: a draft, an editor, a scroll.
    [Fact]
    public void The_windows_page_and_the_panels_page_share_nothing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        t.Board.Show();
        t.Panel.Actions!.ShowTask!(id);
        Assert.Equal(id, t.Panel.InboxPageShown);
        var (_, page) = Open(t, id);

        t.Panel.TypeComment(id, "In the panel", null);
        page.TypeComment(id, "In the window", null);
        Assert.Equal("In the panel", t.Panel.CommentDraft(id));
        Assert.Equal("In the window", page.CommentDraft(id));

        page.BeginRename(id);
        Assert.Null(t.Panel.Editor);
        Assert.NotNull(page.Editor);
        Assert.Equal(id, t.Panel.InboxPageShown);
    });

    // The other way: the switch turns on while the old window's editor is open. The editor keeps nothing, as Esc would.
    [Fact]
    public void Turning_the_inbox_on_closes_the_old_editor_without_saving() => StaThread.Run(() =>
    {
        using var t = Build(inbox: false);
        var id = t.Tasks.AddNote("Errand", Maybe<Guid>.None).Value.Id;
        t.Board.Show();
        Assert.True(t.Board.OpenWindow(id).IsSuccess);
        var window = t.Board.WindowOf(id)!;
        window.BeginEdit();
        Assert.True(window.IsEditing);

        t.Panel.Actions!.ShowInbox!(true);

        Assert.True(window.ShowsInboxPage);
        Assert.False(window.IsEditing);
        Assert.Equal("Errand", t.Tasks.Board.Find(id.ToString()).Value.Title);
    });
}
