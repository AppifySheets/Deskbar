using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;

namespace Deskbar.Core.Tasks;

// THE TASKS PANEL'S INBOX VIEW, ITS THREE TABS, pure (Deskbar task 740a2231; the design is the workspace's
// notes\tasks-inbox-design.md, section 4). The Inbox is a fourth view of the panel beside List, Matrix and Stats. It
// shows exactly the lines the List shows, under three buttons instead of one long list:
//
//   Needs you   the questions (waiting on you), the tasks handed back because their session went quiet, then the Done
//               tasks (full discs before rings, as the List's pinned block orders them);
//   Claude      the List's Claude working block: working now, then idle, then the ones stopped on the usage limit;
//   Notes       the List's four quadrants, captioned in plain words (Do first, Plan, Quick ones, Someday).
//
// THE INPUT IS THE LIST'S OWN GROUPS, after every filter and after the Subtasks checkbox has folded what it folds
// (TasksBoard.View's `listed`), so the three tabs together hold every line the List draws, each once, by construction.
// Nothing here decides what is shown; it only decides under which button a line is shown.
//
// A LINE MOVES WITH ITS BLOCK. The List draws a subtask right under its parent (TaskViews.UnderParents, `nested`), so a
// parent and the subtasks drawn under it are one block, placed by the parent. A subtask never leaves its parent for
// another tab: it would lose the parent it is drawn under. Its own mark still says what state it is in.
//
// TWO FACTS THE LIST DOES NOT GROUP BY are read here:
//   * handed back (TaskItem.HandedBack, StalledTasks): the task stays In progress, so the List draws it in the Claude
//     working block while a session is live on it, else in its quadrant. The Inbox puts it under "Went quiet";
//   * stopped on the usage limit: no field holds it. The app's session poll writes it as the task's status line
//     (ClaudeSessions.NoteLimits) and takes it back once the limit has reset; the caller passes that test in.
//
// While the search box has text, a search by id found a task, or the status filter is Closed or Snoozed, the tabs do not
// apply (TabsApply): the Inbox lists the matches in the List's order, in one list.
public enum InboxTab { NeedsYou, Claude, Notes }

/// <summary>
/// The captioned part of a tab a line sits in, in the order the parts are drawn. <see cref="Matches"/> is the one list
/// shown while the tabs do not apply.
/// </summary>
public enum InboxSection { Questions, WentQuiet, Finished, WorkingNow, Idle, UsageLimit, DoFirst, Plan, QuickOnes, Someday, Matches }

/// <summary>
/// A line's state mark: one shape per state, so the marks are told apart without their colours (spec, "A line").
/// </summary>
public enum InboxMark { Asks, WentQuiet, Finished, FinishedNotRunning, Working, Idle, UsageLimit, Note, Closed, Snoozed }

/// <summary>
/// One line of the Inbox: the task, its tab and section, its mark, when its state began (the time the line shows), whether
/// it is drawn under its parent, and whether it has a related or duplicate link (a small mark).
/// </summary>
public sealed record InboxLine(Guid Id, InboxTab Tab, InboxSection Section, InboxMark Mark, DateTimeOffset Since, bool Nested, bool Linked);

/// <summary>
/// The Inbox as drawn: whether the tabs apply, and every line in the order it is drawn (by tab, then section, then the
/// List's order; with no tabs, the List's order alone).
/// </summary>
public sealed record InboxView(bool Tabbed, IReadOnlyList<InboxLine> Lines)
{
    public static readonly InboxView Empty = new(true, []);

    /// <summary>How many lines a tab shows: its button's count.</summary>
    public int Count(InboxTab tab) => Lines.Count(l => l.Tab == tab);

    /// <summary>A tab's lines, in the order they are drawn.</summary>
    public IReadOnlyList<InboxLine> In(InboxTab tab) => Lines.Where(l => l.Tab == tab).ToArray();

    /// <summary>The tab that holds a task's line, none when the Inbox has no line for it.</summary>
    public Maybe<InboxTab> TabOf(Guid id) => Lines.TryFirst(l => l.Id == id).Map(l => l.Tab);
}

public static class InboxTabs
{
    /// <summary>The tab a section belongs to.</summary>
    public static InboxTab TabOf(InboxSection section) => section switch
    {
        InboxSection.Questions or InboxSection.WentQuiet or InboxSection.Finished => InboxTab.NeedsYou,
        InboxSection.WorkingNow or InboxSection.Idle or InboxSection.UsageLimit => InboxTab.Claude,
        _ => InboxTab.Notes,
    };

    /// <summary>
    /// A section's caption and the plain words after it ("Do first", "urgent and important"); "" for no words. The Notes
    /// sections carry Covey's words after the plain caption (spec, "The three tabs").
    /// </summary>
    public static (string Caption, string Words) CaptionOf(InboxSection section) => section switch
    {
        InboxSection.Questions => ("Questions", ""),
        InboxSection.WentQuiet => ("Went quiet", "no update for a while"),
        InboxSection.Finished => ("Finished", "check, then close"),
        InboxSection.WorkingNow => ("Working now", ""),
        InboxSection.Idle => ("Idle", "finished a turn, asked nothing"),
        InboxSection.UsageLimit => ("Stopped", "usage limit"),
        InboxSection.DoFirst => ("Do first", "urgent and important"),
        InboxSection.Plan => ("Plan", "important, not urgent"),
        InboxSection.QuickOnes => ("Quick ones", "urgent, not important"),
        InboxSection.Someday => ("Someday", "neither"),
        _ => ("Matches", ""),
    };

    /// <summary>
    /// Whether the three tabs apply: not while the search box has words, not while a search by id found a task, and not
    /// under the Closed or Snoozed status filter. Then the Inbox lists the matches in one list.
    /// </summary>
    public static bool TabsApply(TaskFilter filter, bool searchBlank, bool foundById) =>
        searchBlank && !foundById && filter is not (TaskFilter.Closed or TaskFilter.Snoozed);

    /// <summary>
    /// The Inbox's lines from the List's groups (see the header).
    /// </summary>
    /// <param name="listed">The groups the List draws, in order, after every filter and fold.</param>
    /// <param name="nested">The subtasks drawn right under their parent (TaskViews.UnderParents).</param>
    /// <param name="board">The board, for the links each line marks.</param>
    /// <param name="stoppedOnLimit">Whether a task's session is stopped on the usage limit.</param>
    /// <param name="busy">Whether a session of the task is working right now; none counts every live one as working, as the List does.</param>
    /// <param name="hollow">Whether a Done task's tick is the hollow ring (its change does not run yet).</param>
    /// <param name="tabbed">Whether the tabs apply (<see cref="TabsApply"/>).</param>
    public static InboxView Split(IReadOnlyList<TaskGroup> listed, IReadOnlySet<Guid> nested, TaskBoard board,
        Func<TaskItem, bool> stoppedOnLimit, Func<TaskItem, bool>? busy = null, Func<TaskItem, bool>? hollow = null, bool tabbed = true)
    {
        var lines = new List<InboxLine>();
        foreach (var group in listed)
        {
            var section = InboxSection.Matches;
            foreach (var task in group.Tasks)
            {
                // A line drawn under its parent goes where its parent went; a parent (or a line with no parent above it in
                // this group, which only a hand-edited file could make) is placed by its own state.
                var under = nested.Contains(task.Id) && lines.Count > 0 && section != InboxSection.Matches;
                if (!under) section = SectionOf(group.Kind, task, stoppedOnLimit);
                var mark = MarkOf(task, stoppedOnLimit, busy, hollow);
                lines.Add(new InboxLine(task.Id, TabOf(section), section, mark, SinceOf(task, mark), nested.Contains(task.Id),
                    board.Links(task.Id).Map(l => !l.IsEmpty).GetValueOrDefault(false)));
            }
        }
        // OrderBy is stable, so inside a section the List's order stands.
        return tabbed
            ? new InboxView(true, lines.OrderBy(l => l.Section).ToArray())
            : new InboxView(false, lines.Select(l => l with { Section = InboxSection.Matches }).ToArray());
    }

    // Where a block goes, by its first line and the List's group it is in (see the header).
    static InboxSection SectionOf(TaskGroupKind kind, TaskItem task, Func<TaskItem, bool> stoppedOnLimit) =>
        kind switch
        {
            TaskGroupKind.WaitingOnYou => task.Status == TaskStatus.Done ? InboxSection.Finished : InboxSection.Questions,
            TaskGroupKind.Closed or TaskGroupKind.Snoozed => InboxSection.Matches,
            _ when HandedBack(task) => InboxSection.WentQuiet,
            _ when stoppedOnLimit(task) => InboxSection.UsageLimit,
            TaskGroupKind.ClaudeWorking => InboxSection.WorkingNow,
            TaskGroupKind.ClaudeIdle => InboxSection.Idle,
            TaskGroupKind.I => InboxSection.DoFirst,
            TaskGroupKind.II => InboxSection.Plan,
            TaskGroupKind.III => InboxSection.QuickOnes,
            _ => InboxSection.Someday,
        };

    // Handed back to the user because its session went quiet, and still the user's (any change of assignee or status
    // clears the mark; TaskBoard.Change).
    static bool HandedBack(TaskItem task) => task.HandedBack.HasValue && task.Assignee.IsMe && task.IsOpen;

    /// <summary>
    /// A task's state mark (spec, "A line"). Waiting on you is the question bubble, or the warning triangle when the
    /// question is Deskbar's own "could not be restarted" (SessionKeeper.GivenUp); handed back is the triangle too; Done is
    /// the filled disc, or the ring while its change does not run here yet; a session stopped on the usage limit is the
    /// clock; a live session is working or idle by <paramref name="busy"/>; a Claude task with no live session is idle
    /// (the pause sign; its text line says "no live session"); a task no session ever worked on is a note, the empty box.
    /// </summary>
    public static InboxMark MarkOf(TaskItem task, Func<TaskItem, bool> stoppedOnLimit, Func<TaskItem, bool>? busy = null,
        Func<TaskItem, bool>? hollow = null) =>
        task.Status == TaskStatus.Closed ? InboxMark.Closed
        : task.IsSnoozed ? InboxMark.Snoozed
        : task.Status == TaskStatus.WaitingOnYou ? SessionKeeper.GivenUp(task) ? InboxMark.WentQuiet : InboxMark.Asks
        : task.Status == TaskStatus.Done ? hollow?.Invoke(task) == true ? InboxMark.FinishedNotRunning : InboxMark.Finished
        : HandedBack(task) ? InboxMark.WentQuiet
        : !task.IsClaudes ? InboxMark.Note
        : stoppedOnLimit(task) ? InboxMark.UsageLimit
        : TaskViews.AtWork(task) && (busy?.Invoke(task) ?? true) ? InboxMark.Working
        : InboxMark.Idle;

    /// <summary>
    /// When the state a mark shows began, for the line's time: since it asked (WaitingSince, else Raised), since it was
    /// handed back, since it finished (Raised, which becoming Done sets; UpdatedAt moves with every comment), since its
    /// last update while Claude has it, since it was added for a note.
    /// </summary>
    public static DateTimeOffset SinceOf(TaskItem task, InboxMark mark) => mark switch
    {
        InboxMark.Asks => task.WaitingSince.GetValueOrDefault(task.Raised),
        InboxMark.WentQuiet => task.HandedBack.HasValue ? task.HandedBack.Value : task.WaitingSince.GetValueOrDefault(task.Raised),
        InboxMark.Finished or InboxMark.FinishedNotRunning => task.Raised,
        InboxMark.Note => task.Created,
        _ => task.UpdatedAt,
    };

    /// <summary>
    /// How many filters are on in the Filter sheet, for its button ("Filter · 2"): a status other than Open, the Claude
    /// switch, a workspace picked by name (All and "this workspace" are the header's own switch), an assignee, a goal, and
    /// the Subtasks checkbox unticked.
    /// </summary>
    public static int FiltersOn(TaskFilter status, ClaudeSessionFilter claude, TaskWorkspaceFilter where, TaskAssigneeFilter who,
        TaskGoalFilter goal, bool subtasksFolded) =>
        new[]
        {
            status != TaskFilter.Open,
            claude != ClaudeSessionFilter.Off,
            !where.Any && !where.FollowsCurrent,
            who.Kind.HasValue,
            !goal.Any,
            subtasksFolded,
        }.Count(on => on);
}
