using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The tasks panel from the keyboard alone (TasksWindow.Keys.cs, Deskbar task b61f79de): the arrows move the selection,
// Enter opens, Esc comes back, Left/Right walk the tabs, Ctrl+1..4 the views, the menu key and the letters act on the
// task. Every key goes through Press, as the window's PreviewKeyDown hands it over.
public partial class TasksBoardTests
{
    static bool PressKey(Board t, Key key, ModifierKeys modifiers = ModifierKeys.None) => t.Window.Press(key, modifiers);

    [Fact]
    public void Down_and_Up_walk_the_inbox_lines_in_the_drawn_order() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        var keys = t.Window.Keys.ToArray();

        Assert.True(PressKey(t, Key.Down));
        Assert.Equal(keys[0], selection.Current.Value);
        Assert.True(PressKey(t, Key.Down));
        Assert.Equal(keys[1], selection.Current.Value);
        Assert.True(PressKey(t, Key.Up));
        Assert.Equal(keys[0], selection.Current.Value);
        Assert.True(PressKey(t, Key.End));
        Assert.Equal(keys[^1], selection.Current.Value);
        Assert.True(PressKey(t, Key.Home));
        Assert.Equal(keys[0], selection.Current.Value);
        // Moving never opens anything.
        Assert.Null(t.Window.InboxPageShown);
    });

    // Enter is the selected line's click, Esc comes back to the list; and the arrows work again at once (the duplicate
    // task bd149ac3: "after i leave the chat with an escape ... i can't navigate with arrow keys").
    [Fact]
    public void Enter_opens_the_selected_lines_page_and_after_Esc_the_arrows_go_on() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        var keys = t.Window.Keys.ToArray();
        PressKey(t, Key.Down);
        PressKey(t, Key.Down);

        Assert.True(PressKey(t, Key.Enter));
        Assert.Equal(keys[1], t.Window.InboxPageShown);
        // The page's box has the keyboard, as a page opened with a click gives it (PageFocus), and Esc leaves the page.
        t.Window.OpenPageBox?.Focus();
        Assert.True(PressKey(t, Key.Escape));
        Assert.Null(t.Window.InboxPageShown);

        Assert.True(PressKey(t, Key.Down));
        Assert.Equal(keys[2], selection.Current.Value);
    });

    [Fact]
    public void In_the_List_Enter_opens_and_folds_the_selected_thread() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        InboxTasks(t);
        t.Controller.Show();
        Assert.Equal(PanelPlace.List, t.Window.PlaceShown);

        PressKey(t, Key.Down);
        var id = selection.Current.Value;
        Assert.False(t.Window.ThreadShown(id));
        Assert.True(PressKey(t, Key.Enter));
        Assert.True(t.Window.ThreadShown(id));
        Assert.True(PressKey(t, Key.Enter));
        Assert.False(t.Window.ThreadShown(id));
    });

    [Fact]
    public void Down_after_a_redraw_moves_on_from_the_selected_line() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        var keys = t.Window.Keys.ToArray();

        PressKey(t, Key.Down);
        t.Controller.Refresh();
        PressKey(t, Key.Down);
        Assert.Equal(keys[1], selection.Current.Value);
    });

    [Fact]
    public void Keys_do_nothing_while_a_text_box_has_the_keyboard() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        selection.Select(k.Asking);
        PressKey(t, Key.Enter);
        var box = t.Window.OpenPageBox!;
        box.Focus();
        Assert.True(box.IsKeyboardFocused);

        // The box's own keys: its caret moves, its letters are typed, its Enter posts (PressInComment, after this).
        Assert.False(PressKey(t, Key.Down));
        Assert.False(PressKey(t, Key.Enter));
        Assert.False(PressKey(t, Key.W));
        Assert.False(PressKey(t, Key.N));
        Assert.Equal(k.Asking, t.Window.InboxPageShown);
        Assert.False(t.Window.AddBoxShown);
        Assert.Null(t.Window.OpenedMenu);
    });

    [Fact]
    public void Arrows_and_letters_do_nothing_in_an_empty_view() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();
        Assert.False(PressKey(t, Key.Down));
        Assert.False(PressKey(t, Key.Enter));
        Assert.False(PressKey(t, Key.W));
        Assert.False(PressKey(t, Key.C, ModifierKeys.Control));
    });

    [Fact]
    public void Left_and_Right_walk_the_inbox_tabs() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.NeedsYou);

        Assert.True(PressKey(t, Key.Right));
        Assert.Equal(InboxTab.Claude, t.Window.InboxTabShown);
        PressKey(t, Key.Right);
        PressKey(t, Key.Right);
        Assert.Equal(InboxTab.All, t.Window.InboxTabShown);
        PressKey(t, Key.Right);
        Assert.Equal(InboxTab.All, t.Window.InboxTabShown);
        PressKey(t, Key.Left);
        Assert.Equal(InboxTab.Notes, t.Window.InboxTabShown);
    });

    [Fact]
    public void Ctrl_digits_switch_the_views() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();

        Assert.True(PressKey(t, Key.D1, ModifierKeys.Control));
        Assert.Equal(PanelPlace.InboxList, t.Window.PlaceShown);
        Assert.True(PressKey(t, Key.D2, ModifierKeys.Control));
        Assert.Equal(PanelPlace.List, t.Window.PlaceShown);
        Assert.True(PressKey(t, Key.D3, ModifierKeys.Control));
        Assert.Equal(PanelPlace.Matrix, t.Window.PlaceShown);
        Assert.True(PressKey(t, Key.D4, ModifierKeys.Control));
        Assert.Equal(PanelPlace.Stats, t.Window.PlaceShown);
        Assert.Empty(t.Window.Keys);
    });

    [Fact]
    public void The_menu_key_and_Shift_F10_open_the_selected_tasks_menu() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        // Nothing selected: no task, no menu.
        Assert.False(PressKey(t, Key.Apps));
        PressKey(t, Key.Down);

        Assert.True(PressKey(t, Key.Apps));
        Assert.True(t.Window.OpenedMenu is { IsOpen: true });
        t.Window.OpenedMenu!.IsOpen = false;
        Assert.True(PressKey(t, Key.F10, ModifierKeys.Shift));
        Assert.True(t.Window.OpenedMenu is { IsOpen: true });
        t.Window.OpenedMenu!.IsOpen = false;
    });

    [Fact]
    public void C_puts_the_keyboard_in_the_comment_box_of_an_open_thread() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        InboxTasks(t);
        t.Controller.Show();
        PressKey(t, Key.Down);
        var id = selection.Current.Value;
        // Folded, there is no box to go to.
        Assert.False(PressKey(t, Key.C));
        PressKey(t, Key.Enter);

        Assert.True(PressKey(t, Key.C));
        Assert.True(t.Window.CommentBoxOf(id)!.IsKeyboardFocused);
    });

    // On a page the keyboard is in the box (PageFocus), so the actions answer to Alt+letter there.
    [Fact]
    public void P_opens_the_one_pull_request_and_Alt_P_works_from_a_pages_box() => StaThread.Run(() =>
    {
        var opened = new List<Uri>();
        var selection = new TaskSelection();
        using var t = Build(selection: selection, openUri: opened.Add);
        var k = InboxTasks(t);
        t.Tasks.AddPullRequest(k.Done, new PullRequestLink("AppifySheets", "Deskbar", 523));
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        selection.Select(k.Done);

        Assert.True(PressKey(t, Key.P));
        Assert.Single(opened);
        Assert.EndsWith("/pull/523", opened[0].ToString());

        PressKey(t, Key.Enter);
        t.Window.OpenPageBox!.Focus();
        Assert.False(PressKey(t, Key.P));
        Assert.True(PressKey(t, Key.P, ModifierKeys.Alt));
        Assert.Equal(2, opened.Count);
    });

    [Fact]
    public void P_offers_a_menu_when_a_task_has_several_pull_requests() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var k = InboxTasks(t);
        t.Tasks.AddPullRequest(k.Done, new PullRequestLink("AppifySheets", "Deskbar", 523));
        t.Tasks.AddPullRequest(k.Done, new PullRequestLink("AppifySheets", "Deskbar", 524));
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        selection.Select(k.Done);

        Assert.True(PressKey(t, Key.P));
        var menu = t.Window.OpenedMenu!;
        Assert.True(menu.IsOpen);
        Assert.Equal(2, menu.Items.Count);
        menu.IsOpen = false;
    });

    [Fact]
    public void W_opens_the_move_menu_and_N_the_add_box() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        InboxTasks(t);
        t.Controller.Show();
        PressKey(t, Key.Down);

        Assert.True(PressKey(t, Key.W));
        var menu = t.Window.OpenedMenu!;
        Assert.True(menu.IsOpen);
        // Under the task's line or its "⋯", not loose at the pointer.
        Assert.NotNull(menu.PlacementTarget);
        menu.IsOpen = false;

        Assert.True(PressKey(t, Key.N));
        Assert.True(t.Window.AddBoxShown);
    });

    // The keys card (TasksWindow.KeysCard.cs): ? and F1 open it for the place on show, Esc closes it first.
    [Fact]
    public void Question_mark_and_F1_open_the_keys_card_and_Esc_closes_it_first() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Assert.True(PressKey(t, Key.OemQuestion, ModifierKeys.Shift));
        Assert.True(t.Window.KeysCardShown);
        Assert.Contains("Open the task's page", t.Window.KeysCardTexts);
        Assert.True(PressKey(t, Key.Escape));
        Assert.False(t.Window.KeysCardShown);
        Assert.True(PressKey(t, Key.F1));
        Assert.True(t.Window.KeysCardShown);
        Assert.True(PressKey(t, Key.F1));
        Assert.False(t.Window.KeysCardShown);
    });

    [Fact]
    public void The_keys_card_lists_the_keys_of_the_place_it_opened_in() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);
        selection.Select(k.Asking);
        PressKey(t, Key.Enter);

        Assert.True(PressKey(t, Key.F1));
        Assert.Contains("Scroll the page", t.Window.KeysCardTexts);
        Assert.DoesNotContain("Select the line above or below", t.Window.KeysCardTexts);
        // A view switch closes it: the card never lists another place's keys.
        PressKey(t, Key.D2, ModifierKeys.Control);
        Assert.False(t.Window.KeysCardShown);
    });

    [Fact]
    public void The_foot_hint_opens_the_keys_card() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Click(ButtonNamed(t.Window.InboxFooterShown!, TasksWindow.KeysHintText));
        Assert.True(t.Window.KeysCardShown);
    });
}
