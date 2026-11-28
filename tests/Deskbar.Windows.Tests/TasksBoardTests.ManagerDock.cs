using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The manager in its own docked panel (task 71aae045, TasksBoard.ManagerDock.cs): a second TasksWindow made with
// ManagerOnly, on a strip of its own, given to the board with DockManager. The strip here is a TasksPanelRig of its own
// on a made-up screen, as the tasks panel's is; the sends are ManagerFake's (TasksBoardTests.ManagerPage.cs).
public partial class TasksBoardTests
{
    sealed class DockedManager : IDisposable
    {
        public required TasksWindow Window { get; init; }
        public required TasksPanelRig Strip { get; init; }

        public void Dispose() => Window.CloseForGood();
    }

    static DockedManager DockManager(Board t)
    {
#pragma warning disable WPF0001 // ThemeMode is experimental: the app runs under ThemeMode="System", so the tests do too
        var window = new TasksWindow
        {
            ManagerOnly = true, ThemeMode = System.Windows.ThemeMode.System, Quiet = true,
            ClickClock = () => ClickStart, Now = () => Start.AddHours(1), StateDir = System.IO.Path.GetTempPath(), OpenImage = _ => { },
        };
#pragma warning restore WPF0001
        var strip = new TasksPanelRig(window).Docked("right", null);
        t.Controller.DockManager(window, strip.Panel);
        return new DockedManager { Window = window, Strip = strip };
    }

    static Button DockButton(DockedManager d, string name) =>
        Inside(d.Window.InboxPageContent!).OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

    [Fact]
    public void ShowManager_opens_the_docked_panel_and_leaves_the_tasks_panel_alone() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);

        t.Controller.ShowManager();

        Assert.True(t.Controller.ManagerDockOpen);
        Assert.True(d.Window.IsOpen);
        Assert.True(d.Window.ManagerPageShown);
        Assert.NotNull(d.Window.ManagerBox);
        Assert.True(t.Harness.Manager.State.ManagerDockOpen);
        // The tasks panel did not open, and draws no manager page of its own.
        Assert.False(t.Controller.IsOpen);
        Assert.False(t.Window.ManagerPageShown);
        // The docked window draws only the page: no toolbar, no add row.
        Assert.Equal(System.Windows.Visibility.Collapsed, ((System.Windows.FrameworkElement)d.Window.FindName("Toolbar")).Visibility);
    });

    // Task b62c4472 (Petre: "should automatically focus the field for me to enter text"): the first open after a start
    // came up with no keyboard in the box, because the seam refused the window the shell had not placed on a desktop yet
    // (the trace: "not on the desktop in front" 100 ms after "docked (shown)"). The arrival is tried again, and the box
    // gets the keyboard once the seam says yes.
    [Fact]
    public void The_docked_manager_box_gets_the_keyboard_once_the_shell_has_placed_the_panel() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        var asked = 0;
        using var t = BuildWithManager(fake, activates: () => ++asked >= 2);
        using var d = DockManager(t);

        t.Controller.ShowManager();
        Assert.False(d.Window.ManagerBox!.IsKeyboardFocused);

        t.Timers.FireAll();

        Assert.Equal(2, asked);
        Assert.True(d.Window.ManagerBox!.IsKeyboardFocused, "the docked manager's box has no keyboard focus");
    });

    [Fact]
    public void A_send_from_the_docked_panel_names_the_task_open_in_the_Inbox() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        var (asking, _, _, _, _) = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(asking);

        t.Controller.ShowManager();
        Assert.True(d.Window.TypeComment(ManagerSession.PageId, "is this one stuck?", Key.Enter));

        Assert.Equal(asking, fake.Sent.Single().Task.Value.Id);
        Assert.Equal("", d.Window.ManagerBox!.Text);
        // The tasks panel keeps its task page: the manager opened beside it, not over it.
        Assert.Equal(asking, t.Window.InboxPageShown);
    });

    [Fact]
    public void A_send_with_no_task_page_open_names_none() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        t.Controller.ShowManager();

        d.Window.TypeComment(ManagerSession.PageId, "what is stuck?", Key.Enter);

        Assert.False(fake.Sent.Single().Task.HasValue);
    });

    [Fact]
    public void The_close_button_closes_the_docked_panel_and_saves_it_closed() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        t.Controller.ShowManager();

        Click(DockButton(d, TasksWindow.CloseManagerText));

        Assert.False(t.Controller.ManagerDockOpen);
        Assert.False(d.Window.IsOpen);
        Assert.False(t.Harness.Manager.State.ManagerDockOpen);
        Assert.False(t.Controller.ManagerPageInFront);
    });

    // Task dcfc8287: Esc in the docked manager closes it as its × does, the space given back and the state saved closed.
    // The seam says the box holds no words, as when the panel has just opened with the keyboard in its empty box.
    [Fact]
    public void Esc_closes_the_docked_panel_and_saves_it_closed() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        t.Controller.ShowManager();
        d.Window.EscapeIgnoresThreadWhile = () => false;

        Assert.True(d.Window.Press(Key.Escape, ModifierKeys.None));

        Assert.False(t.Controller.ManagerDockOpen);
        Assert.False(d.Window.IsOpen);
        Assert.False(t.Harness.Manager.State.ManagerDockOpen);
        Assert.False(t.Controller.ManagerPageInFront);
        // Only the manager's panel: the tasks panel was not opened or touched by the key.
        Assert.False(t.Controller.IsOpen);
    });

    // An Esc close drops nothing: words in the box (the keyboard elsewhere on the page) come back on the next open.
    [Fact]
    public void A_draft_comes_back_after_Esc_closed_the_docked_panel() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        t.Controller.ShowManager();
        d.Window.ManagerBox!.Text = "not sent yet";
        d.Window.EscapeIgnoresThreadWhile = () => false; // the keyboard is not in the box

        Assert.True(d.Window.Press(Key.Escape, ModifierKeys.None));
        Assert.False(t.Controller.ManagerDockOpen);

        t.Controller.ShowManager();

        Assert.True(t.Controller.ManagerDockOpen);
        Assert.Equal("not sent yet", d.Window.ManagerBox!.Text);
        Assert.Empty(fake.Sent);
    });

    // While the box has the keyboard and words in it, Esc is the box's (as on a task's page): the panel stays and the words
    // with it, so a stray Esc mid-message does not hide what was being written. Not taken, so nothing redraws either.
    [Fact]
    public void Esc_with_words_in_the_box_leaves_the_docked_panel_open() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        t.Controller.ShowManager();
        d.Window.ManagerBox!.Text = "half a thought";
        d.Window.EscapeIgnoresThreadWhile = () => true; // the box has the keyboard, with text in it

        var rebuilds = d.Window.Rebuilds;

        Assert.False(d.Window.Press(Key.Escape, ModifierKeys.None));
        Assert.Equal(rebuilds, d.Window.Rebuilds);
        Assert.True(t.Controller.ManagerDockOpen);
        Assert.True(d.Window.ManagerPageShown);
        Assert.Equal("half a thought", d.Window.ManagerBox!.Text);
    });

    [Fact]
    public void The_Claude_button_closes_the_docked_panel_and_opens_the_terminal() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        t.Controller.ShowManager();

        Click(DockButton(d, TasksWindow.OpenClaudeText));

        Assert.Equal(1, fake.Terminals);
        Assert.False(t.Controller.ManagerDockOpen);
    });

    [Fact]
    public void A_refused_send_shows_on_the_docked_panel_and_a_failed_terminal_keeps_it_open() => StaThread.Run(() =>
    {
        using var t = Build(sendToManager: (_, _) => Result.Failure("Claude sessions are off"),
            openManagerTerminal: () => Result.Failure("No terminal"), openManager: () => Result.Success());
        t.Harness.Manager.SetManagerSession(new ManagerRecord("5a1e0c2b-1111-4222-8333-944455556666", DateOnly.FromDateTime(Start.Date)));
        using var d = DockManager(t);
        t.Controller.ShowManager();

        d.Window.TypeComment(ManagerSession.PageId, "keep me", Key.Enter);
        Assert.Equal("Claude sessions are off", d.Window.Problem);
        Assert.Equal("keep me", d.Window.ManagerBox!.Text);

        Click(DockButton(d, TasksWindow.OpenClaudeText));
        Assert.True(t.Controller.ManagerDockOpen);
        Assert.Equal("No terminal", d.Window.Problem);
    });

    [Fact]
    public void A_second_ShowManager_keeps_it_open_and_RestoreManager_reopens_it() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        using var d = DockManager(t);
        t.Controller.ShowManager();
        t.Controller.ShowManager();
        Assert.True(t.Controller.ManagerDockOpen);

        t.Controller.HideManager();
        t.Controller.RestoreManager();

        Assert.True(t.Controller.ManagerDockOpen);
        Assert.True(d.Window.ManagerPageShown);
    });
}
