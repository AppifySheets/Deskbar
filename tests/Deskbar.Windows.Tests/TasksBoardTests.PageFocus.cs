using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// A PAGE OPENS WITH THE KEYBOARD IN ITS BOX (Deskbar task 64744611, "when i open manager or any other session, typing box
// should be active immediately"; TasksWindow.PageFocus.cs). The panel is activated first, as the board's arrival does
// (TasksBoard.Arrived): the rig's window is Quiet, so its own activation is skipped there.
public partial class TasksBoardTests
{
    [Fact]
    public void A_tasks_page_opens_with_the_keyboard_in_its_comment_box() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        t.Window.Activate();
        ChooseInbox(t);

        Click(LineOf(t.Window, k.Asking));

        Assert.Equal(k.Asking, t.Window.InboxPageShown);
        Assert.Same(t.Window.CommentBoxOf(k.Asking), t.Window.OpenPageBox);
        Assert.True(t.Window.OpenPageBox!.IsKeyboardFocused, "the page's comment box has no keyboard focus");
    });

    [Fact]
    public void The_manager_page_opens_with_the_keyboard_in_its_box() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        InboxTasks(t);
        t.Controller.Show();
        t.Window.Activate();

        t.Controller.ShowManager();

        Assert.True(t.Window.ManagerPageShown);
        Assert.True(t.Window.ManagerBox!.IsKeyboardFocused, "the manager's box has no keyboard focus");
    });

    // The box has the keyboard from the start now, so an empty box gives Esc up and one Esc still goes back; with text in
    // it Esc stays the box's (review M5 of the List), and the text is kept.
    [Fact]
    public void Esc_in_an_empty_page_box_goes_back_and_in_one_with_text_stays() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        t.Controller.Show();
        t.Window.Activate();
        t.Controller.ShowManager();
        Assert.True(t.Window.ManagerBox!.IsKeyboardFocused);

        t.Window.ManagerBox!.Text = "half a thought";
        PressKey(t.Window, Key.Escape);
        Assert.True(t.Window.ManagerPageShown);
        Assert.Equal("half a thought", t.Window.ManagerBox!.Text);

        t.Window.ManagerBox!.Text = "";
        PressKey(t.Window, Key.Escape);
        Assert.False(t.Window.ManagerPageShown);
        Assert.NotNull(t.Window.InboxShown);
    });

    // F2 from the empty box the page opened with still renames the page's task.
    [Fact]
    public void F2_in_an_empty_page_box_renames_the_pages_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        t.Window.Activate();
        ChooseInbox(t);
        Click(LineOf(t.Window, k.Asking));
        Assert.True(t.Window.OpenPageBox!.IsKeyboardFocused);

        Assert.True(t.Window.Press(Key.F2, ModifierKeys.None));

        Assert.NotNull(t.Window.Editor);
        Assert.True(Inside(t.Window.InboxPageContent!).Contains(t.Window.Editor));
    });

    // A redraw of the same page never moves the keyboard: put elsewhere by the owner (here the toolbar), it stays there.
    [Fact]
    public void A_redraw_of_an_open_page_leaves_the_keyboard_where_it_is() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        t.Window.Activate();
        ChooseInbox(t);
        Click(LineOf(t.Window, k.Asking));
        t.Window.Toolbar.Focus();
        var rebuilds = t.Window.Rebuilds;

        Assert.True(t.Tasks.Comment(k.Asking, Maybe<string>.None, "a new line").IsSuccess);

        Assert.True(t.Window.Rebuilds > rebuilds);
        Assert.True(t.Window.Toolbar.IsKeyboardFocused);
    });
}
