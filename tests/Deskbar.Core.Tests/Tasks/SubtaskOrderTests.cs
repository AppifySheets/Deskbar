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

    [Fact]
    public void In_the_panel_waiting_stays_waiting_on_either_side()
    {
        var parent = Task("Parent", status: TaskStatus.WaitingOnYou);
        var child = Task("Child", parent.Id);
        var askingChild = Task("Asking", parent.Id, TaskStatus.WaitingOnYou);
        var groups = new[] { new TaskGroup(TaskGroupKind.WaitingOnYou, [askingChild, parent]), new TaskGroup(TaskGroupKind.IV, [child]) };

        var (nested, moved) = TaskViews.UnderParents(groups);

        Assert.Equal(["Asking", "Parent"], Titles(nested.Single(g => g.Kind == TaskGroupKind.WaitingOnYou).Tasks));
        Assert.Equal(["Child"], Titles(nested.Single(g => g.Kind == TaskGroupKind.IV).Tasks));
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
