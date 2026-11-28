using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Subtasks under their parent (follow-up to docs/superpowers/specs/2026-11-24-subtasks-design.md; Petre's screenshot of
// "↳ test1" four lines above its parent): the one ordering rule the bar and the tasks panel share, the parents
// a drag can nest under, and the count that never breaks across lines.
public class SubtaskOrderTests
{
    static readonly DateTimeOffset At = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    static TaskItem Task(string title, Maybe<Guid> parent = default, TaskStatus status = TaskStatus.Backlog, Maybe<Guid>? workspace = null) =>
        new(Guid.NewGuid(), title, workspace ?? ws, status, "", Maybe<string>.None, At, At, At, [], []) { ParentId = parent };

    static IReadOnlyList<string> Titles(IEnumerable<TaskItem> tasks) => tasks.Select(t => t.Title).ToArray();

    static IReadOnlyList<TaskItem> Nest(IReadOnlyList<TaskItem> tasks) =>
        SubtaskOrder.UnderParents(tasks, t => t.Id, t => t.ParentId, t => t.Status != TaskStatus.WaitingOnYou);

    [Fact]
    public void A_subtask_comes_right_after_its_parent()
    {
        var parent = Task("Parent");
        var a = Task("a", parent.Id);
        var b = Task("b", parent.Id);

        Assert.Equal(["a2", "Parent", "a", "b", "Other"],
            Titles(Nest([Task("a2"), a, b, Task("x") with { Title = "Parent", Id = parent.Id }, Task("Other")])));
    }

    [Fact]
    public void Siblings_keep_their_own_order()
    {
        var parent = Task("Parent");
        var b = Task("b", parent.Id);
        var a = Task("a", parent.Id);

        Assert.Equal(["Parent", "b", "a"], Titles(Nest([b, parent, a])));
    }

    [Fact]
    public void A_subtask_whose_parent_is_not_in_the_list_keeps_its_place()
    {
        var gone = Task("Gone");
        var child = Task("Child", gone.Id);

        Assert.Equal(["One", "Child", "Two"], Titles(Nest([Task("One"), child, Task("Two")])));
    }

    [Fact]
    public void A_subtask_that_may_not_move_keeps_its_place()
    {
        var parent = Task("Parent");
        var waiting = Task("Waiting", parent.Id, TaskStatus.WaitingOnYou);

        Assert.Equal(["Waiting", "One", "Parent"], Titles(Nest([waiting, Task("One"), parent])));
    }

    [Fact]
    public void A_self_parent_from_a_hand_edit_does_not_vanish()
    {
        var self = Task("Self");
        self = self with { ParentId = self.Id };

        Assert.Equal(["Self", "One"], Titles(Nest([self, Task("One")])));
    }

    // Review of #467, finding 5: a hand-edited file with a duplicate id must not crash a paint, nor draw children twice.
    [Fact]
    public void Duplicate_ids_neither_throw_nor_double_children()
    {
        var parent = Task("Parent");
        var child = Task("Child", parent.Id);

        var nested = Nest([parent, child, parent with { Title = "Copy" }]);

        Assert.Equal(["Parent", "Child", "Copy"], Titles(nested));
        Assert.Contains(child.Id, SubtaskOrder.Nested([parent, child, parent], t => t.Id, t => t.ParentId, _ => true));
    }

    [Fact]
    public void Nothing_is_lost_or_doubled()
    {
        var p = Task("P");
        var list = new[] { Task("c1", p.Id), p, Task("c2", p.Id), Task("x"), Task("c3", p.Id) };

        Assert.Equal(list.Select(t => t.Id).OrderBy(i => i), Nest(list).Select(t => t.Id).OrderBy(i => i));
    }

    // --- the tasks panel's groups ------------------------------------------------------------------

    [Fact]
    public void In_the_panel_an_important_subtask_moves_into_its_parents_group()
    {
        var parent = Task("Parent");
        var child = Task("Child", parent.Id) with { Important = true };
        var groups = new[] { new TaskGroup(TaskGroupKind.II, [child, Task("Two")]), new TaskGroup(TaskGroupKind.IV, [Task("Four"), parent, Task("Last")]) };

        var (nested, moved) = TaskViews.UnderParents(groups);

        Assert.Equal(["Two"], Titles(nested.Single(g => g.Kind == TaskGroupKind.II).Tasks));
        Assert.Equal(["Four", "Parent", "Child", "Last"], Titles(nested.Single(g => g.Kind == TaskGroupKind.IV).Tasks));
        Assert.Equal([child.Id], moved);
    }

    // Task f41d7857 ("neither moving them into each-other as subtasks. it should"): a parent waiting on Petre takes its
    // subtasks under it in the pinned block, the waiting ones and the plain ones alike.
    [Fact]
    public void In_the_panel_a_waiting_parent_takes_its_subtasks_into_the_pinned_block()
    {
        var parent = Task("Parent", status: TaskStatus.WaitingOnYou);
        var child = Task("Child", parent.Id);
        var askingChild = Task("Asking", parent.Id, TaskStatus.WaitingOnYou);
        var groups = new[] { new TaskGroup(TaskGroupKind.WaitingOnYou, [askingChild, parent]), new TaskGroup(TaskGroupKind.IV, [child]) };

        var (nested, moved) = TaskViews.UnderParents(groups);

        Assert.Equal(["Parent", "Asking", "Child"], Titles(nested.Single(g => g.Kind == TaskGroupKind.WaitingOnYou).Tasks));
        Assert.DoesNotContain(nested, g => g.Kind == TaskGroupKind.IV);
        Assert.Equal([askingChild.Id, child.Id], moved.OrderBy(id => id == child.Id));
    }

    // A question stays findable: a subtask waiting on Petre is not drawn under a plain parent, below every other question.
    [Fact]
    public void In_the_panel_a_waiting_subtask_of_a_plain_parent_stays_in_the_pinned_block()
    {
        var parent = Task("Parent");
        var askingChild = Task("Asking", parent.Id, TaskStatus.WaitingOnYou);
        var groups = new[] { new TaskGroup(TaskGroupKind.WaitingOnYou, [askingChild]), new TaskGroup(TaskGroupKind.IV, [parent]) };

        var (nested, moved) = TaskViews.UnderParents(groups);

        Assert.Equal(["Asking"], Titles(nested.Single(g => g.Kind == TaskGroupKind.WaitingOnYou).Tasks));
        Assert.Equal(["Parent"], Titles(nested.Single(g => g.Kind == TaskGroupKind.IV).Tasks));
        Assert.Empty(moved);
    }

    [Fact]
    public void A_group_emptied_by_the_move_is_left_out()
    {
        var parent = Task("Parent");
        var child = Task("Child", parent.Id) with { Important = true };
        var groups = new[] { new TaskGroup(TaskGroupKind.II, [child]), new TaskGroup(TaskGroupKind.IV, [parent]) };

        Assert.Equal([TaskGroupKind.IV], TaskViews.UnderParents(groups).Groups.Select(g => g.Kind));
    }

    // --- the "Subtasks" checkbox unticked (task 173ef268): subtasks fold into their parent's line --------------

    // The lines drawn under a parent go; the parent, a subtask whose parent is not on the list and one waiting on you stay,
    // and a group left empty goes with them.
    [Fact]
    public void Unticked_the_subtasks_under_a_parent_fold_into_it()
    {
        var parent = Task("Parent");
        var child = Task("Child", parent.Id);
        var important = Task("Important child", parent.Id) with { Important = true };
        var orphan = Task("Orphan", Guid.NewGuid());
        var asking = Task("Asking", parent.Id, TaskStatus.WaitingOnYou);
        var groups = new[]
        {
            new TaskGroup(TaskGroupKind.WaitingOnYou, [asking]),
            new TaskGroup(TaskGroupKind.II, [important]),
            new TaskGroup(TaskGroupKind.IV, [parent, child, orphan]),
        };
        var (under, nested) = TaskViews.UnderParents(groups);

        var folded = TaskViews.FoldSubtasks(under, nested);

        Assert.Equal([TaskGroupKind.WaitingOnYou, TaskGroupKind.IV], folded.Select(g => g.Kind));
        Assert.Equal(["Asking"], Titles(folded[0].Tasks));
        Assert.Equal(["Parent", "Orphan"], Titles(folded[1].Tasks));
    }

    // Task f41d7857: a question nested under a parent that waits on you too is drawn under it in the pinned block, and
    // folding still never hides it; a plain subtask under that parent folds as any other.
    [Fact]
    public void Unticked_a_question_nested_under_a_waiting_parent_stays_on_show()
    {
        var parent = Task("Parent", status: TaskStatus.WaitingOnYou);
        var asking = Task("Asking", parent.Id, TaskStatus.WaitingOnYou);
        var plain = Task("Plain", parent.Id);
        var groups = new[] { new TaskGroup(TaskGroupKind.WaitingOnYou, [parent, asking]), new TaskGroup(TaskGroupKind.IV, [plain]) };
        var (under, nested) = TaskViews.UnderParents(groups);

        var folded = TaskViews.FoldSubtasks(under, nested);

        Assert.Equal(["Parent", "Asking"], Titles(Assert.Single(folded).Tasks));
    }

    // The Matrix draws no subtask under its parent, so the rule is said in its own terms: an open subtask whose parent is
    // in any cell goes, unless it waits on you; all four cells stay, even one left empty.
    [Fact]
    public void Unticked_the_matrix_folds_subtasks_whose_parent_is_on_it()
    {
        var parent = Task("Parent");
        var child = Task("Child", parent.Id) with { Important = true, Urgent = true };
        var asking = Task("Asking", parent.Id, TaskStatus.WaitingOnYou);
        var orphan = Task("Orphan", Guid.NewGuid());
        var cells = new[]
        {
            new TaskCell(Quadrant.I, [child]),
            new TaskCell(Quadrant.II, []),
            new TaskCell(Quadrant.III, []),
            new TaskCell(Quadrant.IV, [asking, parent, orphan]),
        };

        var folded = TaskViews.FoldSubtasks(cells);

        Assert.Equal(4, folded.Count);
        Assert.Empty(folded[0].Tasks);
        Assert.Equal(["Asking", "Parent", "Orphan"], Titles(folded[3].Tasks));
    }

    // Task 29d4a541 (Petre: "subtask should be there for me to interact with, under that task"): a Done parent's close
    // waits on its open subtasks, so with the box unticked those keep their lines, drawn under the parent in the pinned
    // block, in the List and the Matrix alike; a plain parent's subtask still folds.
    [Fact]
    public void Unticked_an_open_subtask_of_a_done_parent_stays_under_it()
    {
        var done = Task("Done parent", status: TaskStatus.Done);
        var blocking = Task("Blocking", done.Id);
        var plain = Task("Plain parent");
        var child = Task("Child", plain.Id);
        var groups = new[] { new TaskGroup(TaskGroupKind.WaitingOnYou, [done]), new TaskGroup(TaskGroupKind.IV, [blocking, plain, child]) };
        var (under, nested) = TaskViews.UnderParents(groups);

        var folded = TaskViews.FoldSubtasks(under, nested);
        var cells = TaskViews.FoldSubtasks(
            [new TaskCell(Quadrant.I, []), new TaskCell(Quadrant.II, []), new TaskCell(Quadrant.III, []), new TaskCell(Quadrant.IV, [done, blocking, plain, child])]);

        Assert.Equal(["Done parent", "Blocking"], Titles(folded[0].Tasks));
        Assert.Equal(["Plain parent"], Titles(folded[1].Tasks));
        Assert.Equal(["Done parent", "Blocking", "Plain parent"], Titles(cells[3].Tasks));
    }

    // Task b0526e18 (Petre: "i can't interact with the subtask"): the subtask kept (the one selected in the panel, shown
    // from its parent's thread) never folds, in the List or the Matrix; its folded siblings stay folded.
    [Fact]
    public void Unticked_the_kept_subtask_stays_on_show()
    {
        var parent = Task("Parent");
        var kept = Task("Kept", parent.Id);
        var sibling = Task("Sibling", parent.Id);
        var (under, nested) = TaskViews.UnderParents([new TaskGroup(TaskGroupKind.IV, [parent, kept, sibling])]);

        var folded = TaskViews.FoldSubtasks(under, nested, kept.Id);
        var cells = TaskViews.FoldSubtasks(
            [new TaskCell(Quadrant.I, []), new TaskCell(Quadrant.II, []), new TaskCell(Quadrant.III, []), new TaskCell(Quadrant.IV, [parent, kept, sibling])],
            kept.Id);

        Assert.Equal(["Parent", "Kept"], Titles(Assert.Single(folded).Tasks));
        Assert.Equal(["Parent", "Kept"], Titles(cells[3].Tasks));
    }

    // Task 4c01747c (Petre: "unselected subtasks still shows subtasks in the main bar"): his Warehouse parent was snoozed,
    // so it was on no list and its eight subtasks never folded. The family of a snoozed open parent is every open task
    // under it, a grandchild included; a closed one, one under a parent that is not snoozed, one under a snoozed parent
    // that is closed, and one whose parent is missing are not in it.
    [Fact]
    public void The_family_of_a_snoozed_parent_is_its_open_subtasks_at_any_depth()
    {
        var parent = Task("Parent") with { SnoozedUntil = At.AddDays(1) };
        var child = Task("Child", parent.Id);
        var grandchild = Task("Grandchild", child.Id);
        var closedChild = Task("Closed child", parent.Id, TaskStatus.Closed);
        var awake = Task("Awake");
        var awakeChild = Task("Awake child", awake.Id);
        var closedSnoozed = Task("Closed snoozed", status: TaskStatus.Closed) with { SnoozedUntil = At.AddDays(1) };
        var underClosed = Task("Under closed", closedSnoozed.Id);
        var orphan = Task("Orphan", Guid.NewGuid());

        var family = TaskViews.UnderSnoozedParent(new TaskBoard([parent, child, grandchild, closedChild, awake, awakeChild, closedSnoozed, underClosed, orphan]), _ => true);

        Assert.Equal(new[] { child.Id, grandchild.Id }.ToHashSet(), family.ToHashSet());
    }

    // Task 4c01747c, Petre's second screenshot: "↳ test" stayed on the bar under a parent assigned to Claude. The bar
    // folds every open subtask whose parent is open, awake or snoozed, whoever it is assigned to; one under a closed
    // parent (even one whose own parent is open: a hand-edited file), or a missing one, is left out (nothing would show
    // it otherwise).
    [Fact]
    public void The_bar_folds_every_open_subtask_under_an_open_parent()
    {
        var claudes = Task("Claude's parent") with { Assignee = Assignee.Claude };
        var test = Task("test", claudes.Id);
        var snoozed = Task("Snoozed") with { SnoozedUntil = At.AddDays(1) };
        var deep = Task("Deep", test.Id);
        var underSnoozed = Task("Under snoozed", snoozed.Id);
        var closed = Task("Closed", status: TaskStatus.Closed);
        var underClosed = Task("Under closed", closed.Id);
        var closedMiddle = Task("Closed middle", claudes.Id, TaskStatus.Closed);
        var underClosedMiddle = Task("Under closed middle", closedMiddle.Id);
        var orphan = Task("Orphan", Guid.NewGuid());

        var folded = TaskViews.UnderOpenParent(new TaskBoard(
            [claudes, test, snoozed, deep, underSnoozed, closed, underClosed, closedMiddle, underClosedMiddle, orphan]));

        Assert.Equal(new[] { test.Id, deep.Id, underSnoozed.Id }.ToHashSet(), folded.ToHashSet());
    }

    // A loop in the parent ids (a hand-edited tasks.json) ends the walk instead of hanging.
    [Fact]
    public void A_loop_in_the_parents_ends_the_walk()
    {
        var a = Task("A");
        var b = Task("B", a.Id);
        var looped = a with { ParentId = b.Id };

        Assert.Empty(TaskViews.UnderSnoozedParent(new TaskBoard([looped, b]), _ => true));
    }

    // A snoozed parent the view would not list (filtered out by workspace or assignee) holds no family: the Snoozed chip
    // there would not lead to it, so its subtasks keep their lines.
    [Fact]
    public void A_snoozed_parent_the_view_leaves_out_holds_no_family()
    {
        var parent = Task("Parent", workspace: other) with { SnoozedUntil = At.AddDays(1) };
        var child = Task("Child", parent.Id);

        Assert.Empty(TaskViews.UnderSnoozedParent(new TaskBoard([parent, child]), TaskWorkspaceFilter.On(ws)));
        Assert.Equal([child.Id], TaskViews.UnderSnoozedParent(new TaskBoard([parent, child]), TaskWorkspaceFilter.All).ToArray());
    }

    // Unticked, a subtask of a snoozed parent folds in the List and the Matrix though its parent is in no group or cell;
    // one waiting on you and the kept one stay, as they do under a parent on the list, and a Done one stays in the pinned
    // block (review of task 4c01747c: the nesting rule never takes finished work out of it either).
    [Fact]
    public void Unticked_the_subtasks_of_a_snoozed_parent_fold_too()
    {
        var parent = Task("Parent") with { SnoozedUntil = At.AddDays(1) };
        var child = Task("Child", parent.Id);
        var asking = Task("Asking", parent.Id, TaskStatus.WaitingOnYou);
        var done = Task("Done", parent.Id, TaskStatus.Done);
        var kept = Task("Kept", parent.Id);
        var loose = Task("Other");
        var family = TaskViews.UnderSnoozedParent(new TaskBoard([parent, child, asking, done, kept, loose]), _ => true);
        var (under, nested) = TaskViews.UnderParents([new TaskGroup(TaskGroupKind.WaitingOnYou, [asking, done]), new TaskGroup(TaskGroupKind.IV, [child, kept, loose])]);

        var folded = TaskViews.FoldSubtasks(under, nested, kept.Id, family);
        var cells = TaskViews.FoldSubtasks(
            [new TaskCell(Quadrant.I, []), new TaskCell(Quadrant.II, []), new TaskCell(Quadrant.III, []), new TaskCell(Quadrant.IV, [asking, done, child, kept, loose])],
            kept.Id, family);

        Assert.Equal(["Asking", "Done"], Titles(folded[0].Tasks));
        Assert.Equal(["Kept", "Other"], Titles(folded[1].Tasks));
        Assert.Equal(["Asking", "Done", "Kept", "Other"], Titles(cells[3].Tasks));
    }

    // --- the tasks a drag can nest under --------------------------------------------------------

    [Fact]
    public void The_parents_offered_are_open_top_tasks_same_workspace_first()
    {
        var mine = Task("Mine");
        var here = Task("Here");
        var there = Task("There", workspace: other);
        var sub = Task("Sub", here.Id);
        var closed = Task("Closed", status: TaskStatus.Closed);
        var board = new TaskBoard([there, here, sub, closed, mine]);

        Assert.Equal(["Here", "There"], Titles(board.ParentCandidates(mine.Id)));
    }

    [Fact]
    public void A_task_with_subtasks_is_offered_no_parent()
    {
        var parent = Task("Parent");
        var board = new TaskBoard([parent, Task("Child", parent.Id), Task("Other")]);

        Assert.Empty(board.ParentCandidates(parent.Id));
    }

    [Fact]
    public void The_count_stays_on_one_line() =>
        Assert.DoesNotContain(' ', TaskViews.SubtaskCountText(2));
}
