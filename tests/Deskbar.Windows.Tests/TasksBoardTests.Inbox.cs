using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The tasks panel's Inbox view (Deskbar task 740a2231, slice 1): a fourth view beside List, Matrix and Stats, three tabs
// over the List's own lines, a task opened as a page. Built on the same rig as the rest of TasksBoardTests.
public partial class TasksBoardTests
{
    // Every kind of line the Inbox sorts: a question, a Done task, Claude working and idle, a note.
    static (Guid Asking, Guid Done, Guid Working, Guid Idle, Guid Note) InboxTasks(Board t)
    {
        var note = t.Tasks.AddNote("Book the dentist", Maybe<Guid>.None).Value.Id;
        var working = t.Tasks.Create("Review the diff", t.Gepha, Session("s1")).Value.Id;
        var idle = t.Tasks.Create("Read the extension", t.Gepha, Session("s2")).Value.Id;
        var asking = t.Tasks.Create("Docked bar jumps", t.Gepha, Session("s3")).Value.Id;
        var done = t.Tasks.Create("Lighter terminal", t.Sparrow, Session("s4")).Value.Id;
        t.Tasks.Ask(asking, "Does the bar stay where you put it?");
        t.Tasks.MarkDone(done);
        return (asking, done, working, idle, note);
    }

    // The switch's Inbox, as a click on it calls it.
    static void ChooseInbox(Board t) => t.Window.Actions!.ShowInbox!(true);

    [Fact]
    public void A_panel_state_without_the_key_keeps_todays_view() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);

        t.Controller.Show();

        Assert.Null(t.Window.InboxShown);
        Assert.Contains("Waiting on you", t.Window.Headers);
    });

    [Fact]
    public void The_inbox_is_remembered_once_chosen_and_left_by_list_or_matrix() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();

        ChooseInbox(t);
        Assert.NotNull(t.Window.InboxShown);
        Assert.True(t.Harness.Manager.State.TasksPanel!.Inbox);
        // Stats goes over it without forgetting it; Inbox leaves Stats again.
        t.Window.Actions!.ShowStats!();
        Assert.Null(t.Window.InboxShown);
        Assert.True(t.Harness.Manager.State.TasksPanel!.Inbox);
        ChooseInbox(t);
        Assert.NotNull(t.Window.InboxShown);

        t.Window.Actions!.ShowMatrix(false);
        Assert.Null(t.Window.InboxShown);
        Assert.False(t.Harness.Manager.State.TasksPanel!.Inbox);

        ChooseInbox(t);
        t.Window.Actions!.ShowMatrix(true);
        Assert.Null(t.Window.InboxShown);
        Assert.True(t.Window.MatrixShown);
    });

    // Criteria 2 and 3: the three tabs together hold exactly the List's lines, each once, and each count is its lines.
    [Fact]
    public void The_three_tabs_together_hold_the_lists_lines() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: task => task.Title == "Review the diff" ? 1 : 0);
        var k = InboxTasks(t);
        t.Controller.Show();
        var listKeys = t.Window.Keys.ToArray();

        ChooseInbox(t);
        var inbox = t.Window.InboxShown!;

        Assert.True(inbox.Tabbed);
        Assert.Equal(listKeys.Order(), inbox.Lines.Select(l => l.Id).Order());
        Assert.Equal([k.Asking, k.Done], inbox.In(InboxTab.NeedsYou).Select(l => l.Id));
        Assert.Equal([k.Working, k.Idle], inbox.In(InboxTab.Claude).Select(l => l.Id));
        Assert.Equal([k.Note], inbox.In(InboxTab.Notes).Select(l => l.Id));
        Assert.Equal([2, 2, 1], new[] { InboxTab.NeedsYou, InboxTab.Claude, InboxTab.Notes }.Select(inbox.Count));
        // The Inbox draws from the List's rows: the view keeps its groups, and the Matrix setting does not take over.
        Assert.False(t.Window.MatrixShown);
    });

    // A session stopped on the usage limit: Deskbar's own status line says so, and the line moves to the Claude tab's
    // last part.
    [Fact]
    public void A_task_stopped_on_the_usage_limit_is_under_stopped() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: _ => 0);
        var limited = t.Tasks.Create("Store icons", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.SetStatusLine(limited, ClaudeSessions.LimitLine + "Thu 06:00");
        t.Controller.Show();
        ChooseInbox(t);

        var line = Assert.Single(t.Window.InboxShown!.In(InboxTab.Claude));
        Assert.Equal(InboxSection.UsageLimit, line.Section);
        Assert.Equal(InboxMark.UsageLimit, line.Mark);
    });

    // Under a search the tabs do not apply: one list of the matches.
    [Fact]
    public void A_search_shows_the_matches_without_tabs() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.Search("dentist");

        Assert.False(t.Window.InboxShown!.Tabbed);
        Assert.Equal([k.Note], t.Window.InboxShown!.Lines.Select(l => l.Id));
    });
}
