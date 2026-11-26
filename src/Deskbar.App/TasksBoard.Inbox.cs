using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE INBOX VIEW, THE BOARD'S HALF (Deskbar task 740a2231, slice 1; design: the workspace's notes\tasks-inbox-design.md).
// The Inbox is a fourth view of the tasks panel beside List, Matrix and Stats: three tabs, Needs you, Claude and Notes,
// over exactly the lines the List shows, and a task opened as a page inside the panel instead of a thread unfolded in
// the list. The board decides, the window draws (TasksWindow.Inbox*.cs), as for every other view.
//
// WHAT THE BOARD ADDS, and nothing more:
//   * whether the Inbox shows: the remembered choice (TasksPanelState.Inbox, opt in), and not while Stats shows. While
//     it shows the view's Matrix is off, so the List's groups stay the one list of lines every lookup reads (DrawnRows,
//     RowFor, OpenMenuFor, Draws): the Inbox draws those very rows;
//   * the three tabs (InboxTabs.Split in Core) from the List's own groups and nesting, with the two facts the List does
//     not group by: handed back (TaskItem.HandedBack) and stopped on the usage limit (the status line ClaudeSessions
//     writes, LimitLine);
//   * two actions: ShowInbox (the view switch) and OpenPage (a line's click: picked and its thread open, never toggled
//     shut, which is the difference from Pick).
// Every other click of the Inbox calls what the List calls.
public sealed partial class TasksBoard
{
    // The Inbox is on show: chosen, and Stats is not over it.
    bool InboxShown => PanelState.Inbox && !statsShown;

    /// <summary>
    /// The view switch's Inbox (and `tasks-window inbox`): the panel shows the Inbox, remembered across opens and
    /// restarts. Leaves Stats, as List and Matrix do.
    /// </summary>
    public void ShowInbox(bool on)
    {
        statsShown = false;
        // A no-op write when it is already so (SaveTasksPanel compares); the redraw then still runs, for Stats' sake.
        _ = manager.SaveTasksPanel(PanelState with { Inbox = on });
        Refresh();
    }

    // List or Matrix chosen: the Inbox is left, and remembered as left. The caller redraws.
    void LeaveInbox() => _ = manager.SaveTasksPanel(PanelState with { Inbox = false });

    /// <summary>
    /// An Inbox line's click: the task picked, in both views, and its thread open, which is what the Inbox draws as the
    /// task's page. Unlike Pick it never folds a thread that was open already (from the List, or left open), since a click
    /// on a line is always "open this".
    /// </summary>
    public void OpenPage(Guid id)
    {
        selection.Select(id);
        expanded.Add(id);
        Refresh();
    }

    // The view with the Inbox's lines, while the Inbox shows; untouched otherwise, so List, Matrix and Stats draw from
    // exactly the view they always had. `listed` and `nested` are the List's groups and nesting as View made them.
    TasksView WithInbox(TasksView view, IReadOnlyList<TaskGroup> listed, IReadOnlySet<Guid> nested, bool foundById)
    {
        if (!InboxShown) return view;
        var inbox = InboxTabs.Split(listed, nested, tasks.Board, InboxMarks.StoppedOnLimit,
            busy: knowsActivity() ? Busy : null,
            hollow: t => tasks.DeliveryOf(t).Shipping,
            tabbed: InboxTabs.TabsApply(filter, search.IsBlank, foundById));
        return view with
        {
            Inbox = inbox,
            FiltersOn = InboxTabs.FiltersOn(filter, claudeFilter, where, assigneeFilter, goalFilter, PanelState.HideSubtasks),
        };
    }


    // A TASK WINDOW IN THE INBOX LOOK (TaskDetailWindow.Inbox.cs). While the Inbox is the chosen view (PanelState.Inbox, not
    // InboxShown: Stats laid over the Inbox must not flip a window's look) a task window draws the Inbox's page for its
    // task. The page is drawn by a TasksWindow of its own, from a view of one task: its line with the thread open as
    // DetailOf made it, and the mark the Inbox's own split gives it (went quiet and the usage limit included, which the row
    // alone cannot tell). The List's filters, tabs and search have no part in it: a task window shows its task whatever
    // the panel filters.
    TaskDetailView WithInboxPage(TaskDetailView detail, TaskItem task)
    {
        if (!PanelState.Inbox) return detail;
        var group = new TaskGroup(TaskViews.KindOf(detail.Row.Quadrant), [task]);
        var inbox = InboxTabs.Split([group], new HashSet<Guid>(), tasks.Board, InboxMarks.StoppedOnLimit,
            busy: knowsActivity() ? Busy : null,
            hollow: t => tasks.DeliveryOf(t).Shipping,
            tabbed: false);
        var page = new TasksView([], "", where, detail.Places, [new TaskGroupView(group.Kind, "", [detail.Row])], "",
            PlaceOf(manager.CurrentWorkspaceId))
        {
            Inbox = inbox,
        };
        return detail with { Page = page };
    }

    /// <summary>For the tests: what a task window would be handed for the task now, null for an unknown task.</summary>
    internal TaskDetailView? DetailViewFor(Guid id) =>
        tasks.Board.Tasks.TryFirst(t => t.Id == id).Match(task => (TaskDetailView?)DetailOf(task), () => null);
}

internal sealed partial record TaskDetailView
{
    /// <summary>
    /// The Inbox's page for this task, as a view for the page's own window (TasksWindow.ShowPage). Set only while the Inbox
    /// is the chosen view; null otherwise, and the task window then draws as it always did.
    /// </summary>
    public TasksView? Page { get; init; }
}

internal sealed partial record TasksView
{
    /// <summary>
    /// The Inbox's lines while the Inbox view shows (task 740a2231), null in List, Matrix and Stats. Each line is one of
    /// the Groups' rows, by id.
    /// </summary>
    public InboxView? Inbox { get; init; }

    /// <summary>How many filters are on in the Inbox's Filter sheet, for its button ("Filter · 2"). 0 outside the Inbox.</summary>
    public int FiltersOn { get; init; }
}

internal sealed partial record TasksWindowActions
{
    /// <summary>The view switch's Inbox (task 740a2231): TasksBoard.ShowInbox. None, no Inbox button.</summary>
    public Action<bool>? ShowInbox { get; init; }

    /// <summary>An Inbox line's click: the task picked and its page open (TasksBoard.OpenPage).</summary>
    public Action<Guid>? OpenPage { get; init; }
}
