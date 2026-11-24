using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Subtasks under their parent (follow-up to docs/superpowers/specs/2026-11-24-subtasks-design.md; Petre's screenshot of
// "↳ test1" four lines above its parent): the one ordering rule the bar and the tasks panel share, the parents
// "Subtask of…" offers, and the count that never breaks across lines.
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

    // --- "Subtask of…" ------------------------------------------------------------------------

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
    public void The_menus_choices_name_the_current_parent_and_cut_long_titles()
    {
        var parent = Task(new string('p', 80));
        var child = Task("Child", parent.Id);
        var choices = TaskViews.ParentChoices(new TaskBoard([parent, child, Task("Other")]), child.Id);

        Assert.False(choices.Blocked);
        Assert.Equal(Maybe.From(parent.Id), choices.Current);
        Assert.Equal(2, choices.Candidates.Count);
        Assert.All(choices.Candidates, c => Assert.True(c.Title.Length <= 50));
    }

    [Fact]
    public void A_parents_menu_choice_is_blocked()
    {
        var parent = Task("Parent");
        Assert.True(TaskViews.ParentChoices(new TaskBoard([parent, Task("Child", parent.Id)]), parent.Id).Blocked);
    }

    [Fact]
    public void The_count_stays_on_one_line() =>
        Assert.DoesNotContain(' ', TaskViews.SubtaskCountText(2));
}
