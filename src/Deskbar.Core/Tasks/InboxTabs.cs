using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;

namespace Deskbar.Core.Tasks;

// THE TASKS PANEL'S INBOX VIEW, ITS THREE TABS, pure (Deskbar task 740a2231; the design is the workspace's
// notes\tasks-inbox-design.md, section 4). The Inbox is a fourth view of the panel beside List, Matrix and Stats. It
// shows exactly the lines the List shows, under three buttons instead of one long list:
//
//   Needs you   the questions (waiting on you), the tasks handed back because their session went quiet, the tasks that
//               only wait for his deploy, then the Done tasks (full discs before rings, as the List's pinned block
//               orders them);
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
//
// A FOURTH TAB, ALL (the owner: "let's add all tasks here as well"), holds every line the other three hold, each once. It
// is not a place a line is sorted into: a line's own Tab is always one of the three, and All is only a way of looking at
// them together (InboxView.Count and In answer for it from the three). Its order is the one the lines already have,
// Needs you's lines first, then Claude's, then Notes', each in its own tab's order under its own captions: Split sorts
// the lines by section, and the sections are declared tab by tab, so that order costs nothing and the captions still say
// which part a line is in. The List's order (WaitingOnYou, Claude working, the quadrants, Done last) was the other
// candidate; it was not taken because it would part a tab's sections (Finished would go to the end, away from the
// questions) and the captions would no longer match any tab's. All is declared last: its card is the row's last.
public enum InboxTab { NeedsYou, Claude, Notes, All }

/// <summary>
/// The captioned part of a tab a line sits in, in the order the parts are drawn. <see cref="Matches"/> is the one list
/// shown while the tabs do not apply.
/// </summary>
// Permissions (Allow / Deny from a task's page) comes first: a permission question holds Claude mid-step, so it is the
// most urgent thing the Inbox can show.
// Deploys (task 1f6ae26e): a task that only waits for his deploy (InboxMark.WaitsForDeploy, DeployWait) sits below the
// questions and the quiet sessions, above Finished. Petre, on such a line among the questions: "this chip shouldn't ask
// for my attention, or should it? if it shouldn't, let it sit below those that are asking for my attention". It is still
// his step, so it stays in Needs you, but it holds up no session: the change is merged, the session has nothing left to
// do until the deploy, and the deploy has no deadline the way a question has. A question ("A or B?") or a quiet session
// stops work until he answers, so those come first. Sections are compared by declaration order (Split's OrderBy), and
// none is stored or traced as a number (the Inbox's signature writes names), so adding one in the middle is safe.
public enum InboxSection { Permissions, Questions, WentQuiet, Deploys, Finished, WorkingNow, Idle, UsageLimit, DoFirst, Plan, QuickOnes, Someday, Matches }

/// <summary>
/// A line's state mark: one shape per state, so the marks are told apart without their colours (spec, "A line").
/// </summary>
// ReadyToMerge (task b99e5596): the question only asks for a pull request's merge (AskedPullRequests.AsksMerge), so it gets
// a merge glyph instead of the "?" bubble. Added last, so no stored or traced number of the others changes.
// DeployUnchecked (task 485ba96b): finished and merged, and its session cannot tell whether it was deployed
// (TaskItem.DeployUnknown), so a mark of its own instead of "finished, not running yet", and never the "?" bubble.
public enum InboxMark { Asks, WentQuiet, Finished, FinishedNotRunning, Working, Idle, UsageLimit, Note, Closed, Snoozed, AsksPermission, WaitsForDeploy, ReadyToMerge, DeployUnchecked }

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

    /// <summary>How many lines a tab shows: its button's count. All counts every line, each once.</summary>
    public int Count(InboxTab tab) => Lines.Count(l => Holds(tab, l));

    /// <summary>
    /// A tab's lines, in the order they are drawn. All's are every line in the order Split left them: by tab, then by
    /// section, then the List's order (see the header).
    /// </summary>
    public IReadOnlyList<InboxLine> In(InboxTab tab) => Lines.Where(l => Holds(tab, l)).ToArray();

    /// <summary>The tab that holds a task's line, none when the Inbox has no line for it. Never All: a line's own tab.</summary>
    public Maybe<InboxTab> TabOf(Guid id) => Lines.TryFirst(l => l.Id == id).Map(l => l.Tab);

    /// <summary>
    /// The tab to show a task's line in, from the tab on show: the tab on show while it holds the line (All holds every
    /// line, so a user looking at All stays there), else the line's own tab; none when the Inbox has no line for it.
    /// Every "bring this task's line on show" asks this (a task shown from the bar, a task just added, a terminal in front),
    /// so none of them takes the user out of All to a tab that holds less.
    /// </summary>
    public Maybe<InboxTab> TabToShow(Guid id, InboxTab onShow) =>
        Lines.TryFirst(l => l.Id == id).Map(l => Holds(onShow, l) ? onShow : l.Tab);

    static bool Holds(InboxTab tab, InboxLine line) => tab == InboxTab.All || line.Tab == tab;
}

public static class InboxTabs
{
    /// <summary>The tab a section belongs to.</summary>
    public static InboxTab TabOf(InboxSection section) => section switch
    {
        InboxSection.Permissions or InboxSection.Questions or InboxSection.WentQuiet or InboxSection.Deploys or InboxSection.Finished => InboxTab.NeedsYou,
        InboxSection.WorkingNow or InboxSection.Idle or InboxSection.UsageLimit => InboxTab.Claude,
        _ => InboxTab.Notes,
    };

    /// <summary>
    /// A section's caption and the plain words after it ("Do first", "urgent and important"); "" for no words. The Notes
    /// sections carry Covey's words after the plain caption (spec, "The three tabs").
    /// </summary>
    public static (string Caption, string Words) CaptionOf(InboxSection section) => section switch
    {
        InboxSection.Permissions => ("Asks permission", "a yes or a no"),
        InboxSection.Questions => ("Questions", ""),
        InboxSection.WentQuiet => ("Went quiet", "no update for a while"),
        InboxSection.Deploys => ("To deploy", "merged, not running yet"),
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
    /// <param name="asksPermission">Whether a session of the task has a permission question on show (PermissionDesk).</param>
    /// <param name="unchecked">Whether the task's delivery is DeliveryStage.Unchecked (task 485ba96b, TaskDelivery.Unchecked).</param>
    public static InboxView Split(IReadOnlyList<TaskGroup> listed, IReadOnlySet<Guid> nested, TaskBoard board,
        Func<TaskItem, bool> stoppedOnLimit, Func<TaskItem, bool>? busy = null, Func<TaskItem, bool>? hollow = null, bool tabbed = true,
        Func<TaskItem, bool>? asksPermission = null, Func<TaskItem, bool>? @unchecked = null)
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
                var mark = MarkOf(task, stoppedOnLimit, busy, hollow, asksPermission, @unchecked);
                if (!under) section = ForDeploy(SectionOf(group.Kind, task, stoppedOnLimit, asksPermission), mark);
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
    // A permission question puts its task first in Needs you, whatever its group (an open task: a closed or snoozed one
    // stays among the matches).
    static InboxSection SectionOf(TaskGroupKind kind, TaskItem task, Func<TaskItem, bool> stoppedOnLimit, Func<TaskItem, bool>? asksPermission) =>
        kind switch
        {
            not (TaskGroupKind.Closed or TaskGroupKind.Snoozed) when asksPermission?.Invoke(task) == true => InboxSection.Permissions,
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

    // A question whose mark says it only waits for his deploy moves from Questions to Deploys (task 1f6ae26e, see the
    // section's comment). Asked of the mark, so the section can never disagree with the ring the line shows: a merged task asking
    // with answer buttons, or one whose session said it cannot check the deploy, stays a question here as in MarkOf.
    static InboxSection ForDeploy(InboxSection section, InboxMark mark) =>
        section == InboxSection.Questions && mark == InboxMark.WaitsForDeploy ? InboxSection.Deploys : section;

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
    // A permission question on show (asksPermission, Allow / Deny from a task's page) outranks every
    // mark but closed and snoozed: the shield.
    // `unchecked` (task 485ba96b): the delivery is DeliveryStage.Unchecked, its session cannot check the deploy. Asked of
    // the delivery rather than read from TaskItem.DeployUnknown (review finding): the field stands while a pull request
    // is still open or not asked about yet, and the mark must agree with the chip and the tick, which follow the stage.
    public static InboxMark MarkOf(TaskItem task, Func<TaskItem, bool> stoppedOnLimit, Func<TaskItem, bool>? busy = null,
        Func<TaskItem, bool>? hollow = null, Func<TaskItem, bool>? asksPermission = null, Func<TaskItem, bool>? @unchecked = null) =>
        task.Status == TaskStatus.Closed ? InboxMark.Closed
        : task.IsSnoozed ? InboxMark.Snoozed
        : asksPermission?.Invoke(task) == true ? InboxMark.AsksPermission
        : task.Status == TaskStatus.WaitingOnYou ? SessionKeeper.GivenUp(task) ? InboxMark.WentQuiet
            // Merged, not running yet and no answer buttons: waiting for his deploy, not a question (task 1565789d,
            // DeployWait). `hollow` is the task's delivery: for a task that is not Done it is shipping only when merged.
            // A hand-over the session marked (`task ask --deploy`, task a87891ca) waits for the deploy too.
            // A deploy its session already said it cannot check (task 485ba96b) is not waiting for his deploy: a plain
            // question asked after it stays a question.
            : DeployWait.Waits(task.Status, hollow?.Invoke(task) == true && @unchecked?.Invoke(task) != true, task.QuestionOptions.Count, task.AwaitsDeploy) ? InboxMark.WaitsForDeploy
            // A question that only asks for a merge (task b99e5596): the merge glyph, not the "?" bubble.
            : AskedPullRequests.AsksMerge(task) ? InboxMark.ReadyToMerge
            : InboxMark.Asks
        // Not running yet, and its session said it cannot check the deploy (task 485ba96b): the mark that says so.
        : task.Status == TaskStatus.Done ? @unchecked?.Invoke(task) == true ? InboxMark.DeployUnchecked
            : hollow?.Invoke(task) == true ? InboxMark.FinishedNotRunning : InboxMark.Finished
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
        InboxMark.Asks or InboxMark.ReadyToMerge or InboxMark.WaitsForDeploy => task.WaitingSince.GetValueOrDefault(task.Raised),
        InboxMark.WentQuiet => task.HandedBack.HasValue ? task.HandedBack.Value : task.WaitingSince.GetValueOrDefault(task.Raised),
        InboxMark.Finished or InboxMark.FinishedNotRunning or InboxMark.DeployUnchecked => task.Raised,
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
