using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// THE CLAUDE BUTTON SITS LEFT OF THE DOCKED COMMENT BOX (task 625405cc; the owner: "move the open in claude button to the
// bottom, where i can easily click it if i need to"). On a task's page Open Claude, or Start Claude on this where there is
// nothing to open, leaves the row of buttons at the top of the thread (inside the scroller) and stands beside the docked
// box, outside it. The List's thread keeps it in its row.
public partial class TasksBoardTests
{
    // The page's docked frame, where the comment box (and now the Claude button) stands.
    static Border DockedBoxFrame(Board t) =>
        Inside(t.Window.InboxPageContent!).OfType<Border>().Single(b => Equals(b.Tag, TasksWindow.DockedBoxTag));

    static WrapPanel PageRow(Board t) =>
        Inside(t.Window.InboxPageContent!).OfType<WrapPanel>().Single(p => Equals(p.Tag, TasksWindow.ThreadActionsTag));

    [Fact]
    public void Open_claude_sits_left_of_the_docked_box_out_of_the_scroller_and_goes_to_the_session() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        using var t = Build(width: 460, goTo: task => { went.Add(task.Id); return Result.Success(); },
            sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Idle)).ToArray());
        var id = PageTaskWithLongChat(t);
        var page = (UIElement)t.Window.InboxPageContent!;
        var frame = DockedBoxFrame(t);
        var claude = ButtonNamed(frame, TasksWindow.OpenClaudeText);
        var box = t.Window.CommentBoxOf(id)!;

        Assert.DoesNotContain(claude, Inside(t.Window.InboxPageScroller!));
        Assert.DoesNotContain(TasksWindow.OpenClaudeText, PageRow(t).Children.OfType<Button>().Select(AutomationProperties.GetName));
        // Left of the box, and level with a one-line box: centres within a DIP.
        Assert.True(PlaceIn(claude, page).X + claude.ActualWidth <= PlaceIn(box, page).X);
        var field = Inside(frame).OfType<Border>().First(b => b.CornerRadius.TopLeft == 15);
        var claudeMiddle = PlaceIn(claude, page).Y + claude.ActualHeight / 2;
        var fieldMiddle = PlaceIn(field, page).Y + field.ActualHeight / 2;
        Console.WriteLine($"MEASURED: button middle {claudeMiddle:0.##}, field middle {fieldMiddle:0.##}, field {field.ActualHeight:0.##} tall");
        Assert.Equal(fieldMiddle, claudeMiddle, 1);

        // Still in view, at the same place, with the chat scrolled to its top.
        var at = PlaceIn(claude, page);
        t.Window.InboxPageScroller!.ScrollToVerticalOffset(0);
        t.Window.UpdateLayout();
        Assert.Equal(at.Y, PlaceIn(claude, page).Y, 1);

        Click(claude);
        Assert.Equal([id], went);
    });

    [Fact]
    public void The_docked_button_follows_the_box_bottom_as_the_box_grows() => StaThread.Run(() =>
    {
        using var t = Build(width: 460, sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Idle)).ToArray());
        var id = PageTaskWithLongChat(t, 5);
        var page = (UIElement)t.Window.InboxPageContent!;
        var frame = DockedBoxFrame(t);
        var field = Inside(frame).OfType<Border>().First(b => b.CornerRadius.TopLeft == 15);

        t.Window.CommentBoxOf(id)!.Text = string.Join("\n", Enumerable.Range(1, 4).Select(i => $"line {i}"));
        t.Window.UpdateLayout();
        t.Window.UpdateLayout();

        var claude = ButtonNamed(frame, TasksWindow.OpenClaudeText);
        var gapBelow = PlaceIn(field, page).Y + field.ActualHeight - (PlaceIn(claude, page).Y + claude.ActualHeight);
        Console.WriteLine($"MEASURED: field {field.ActualHeight:0.##} tall, {gapBelow:0.##} under the button");
        Assert.True(field.ActualHeight > 50);
        Assert.InRange(gapBelow, 2, 5);
    });

    [Fact]
    public void A_task_with_no_session_has_start_claude_beside_the_box_and_a_note_has_no_claude_button() => StaThread.Run(() =>
    {
        using var t = Build(width: 460, startClaude: _ => { });
        var free = t.Tasks.AddNote("Book the dentist", t.Gepha).Value.Id;
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(free);
        t.Window.UpdateLayout();

        Assert.True(HasButtonNamed(DockedBoxFrame(t), TasksWindow.StartClaudeText));
        Assert.DoesNotContain(TasksWindow.StartClaudeText, PageRow(t).Children.OfType<Button>().Select(AutomationProperties.GetName));

        // Without StartClaude wired, nothing stands beside the box: the box alone is the frame's child.
        using var plain = Build(width: 460);
        var note = plain.Tasks.AddNote("Book the dentist", plain.Gepha).Value.Id;
        plain.Controller.Show();
        ChooseInbox(plain);
        plain.Controller.ShowTask(note);
        plain.Window.UpdateLayout();
        Assert.False(HasButtonNamed(DockedBoxFrame(plain), TasksWindow.StartClaudeText));
        Assert.False(HasButtonNamed(DockedBoxFrame(plain), TasksWindow.OpenClaudeText));
        Assert.IsNotType<Grid>(DockedBoxFrame(plain).Child);
    });

    [Fact]
    public void The_lists_thread_keeps_open_claude_in_its_row() => StaThread.Run(() =>
    {
        using var t = Build(sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Idle)).ToArray());
        var id = t.Tasks.Create("Review the diff", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        // The line's own Open Claude may hide the row's (FollowLine), so ask whether the row was built with the button at
        // all, visible or not.
        t.Controller.ToggleThread(id);
        t.Window.UpdateLayout();

        var row = Inside(t.Window).OfType<WrapPanel>().Single(p => Equals(p.Tag, TasksWindow.ThreadActionsTag));
        Assert.Contains(TasksWindow.OpenClaudeText, row.Children.OfType<Button>().Select(AutomationProperties.GetName));
        Assert.Null(t.Window.InboxPageContent);
    });
}
