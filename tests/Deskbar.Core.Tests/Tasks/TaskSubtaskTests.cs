using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Subtasks on the board: the parent link's rules (one level), adding a subtask by hand or from a session, deleting a
// parent, the family view a session is briefed from, and the offer rule for a free subtask. Design:
// docs/superpowers/specs/2026-11-24-subtasks-design.md ("The link", "Making subtasks").
public class TaskSubtaskTests
{
    DateTimeOffset clock = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid parent = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid child = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    static readonly Guid sibling = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    static readonly Guid loner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    static readonly Guid fresh = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");

    static readonly Maybe<Guid> Free = Maybe<Guid>.None;

    void Later(int minutes = 1) => clock = clock.AddMinutes(minutes);

    TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    TaskBoard Note(TaskBoard board, Guid id, Maybe<Guid> workspace, string title = "A note") =>
        board.AddNote(title, workspace, clock, id).Value.Item1;

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    // A parent on `ws` with two subtasks, plus an unrelated task.
    TaskBoard Family()
    {
        var board = Note(TaskBoard.Empty, parent, ws, "Parent");
        board = Note(board, child, Free, "Child");
        board = Note(board, sibling, Free, "Sibling");
        board = Note(board, loner, ws, "Loner");
        board = board.SetParent(child, parent, clock).Value;
        return board.SetParent(sibling, parent, clock).Value;
    }

    // --- SetParent -------------------------------------------------------------------------------

    [Fact]
    public void Setting_a_parent_links_and_stamps_and_moves_nothing()
    {
        var board = Note(Note(TaskBoard.Empty, parent, ws), child, ws);
        var before = Get(board, child);
        Later();

        var after = Get(board.SetParent(child, parent, clock).Value, child);

        Assert.Equal(Maybe.From(parent), after.ParentId);
        Assert.Equal(clock, after.UpdatedAt);
        Assert.Equal(before.Rank, after.Rank);
        Assert.Equal(before.Raised, after.Raised);
    }

    [Fact]
    public void An_unknown_parent_is_refused()
    {
        var board = Note(TaskBoard.Empty, child, ws);
        Assert.StartsWith("No task with id", board.SetParent(child, fresh, clock).Error);
    }

    [Fact]
    public void A_task_cannot_be_its_own_subtask()
    {
        var board = Note(TaskBoard.Empty, child, ws);
        Assert.Equal("A task cannot be its own subtask", board.SetParent(child, child, clock).Error);
    }

    [Fact]
    public void A_subtask_cannot_be_a_parent()
    {
        var board = Family();
        Assert.StartsWith("Subtasks go one level deep", board.SetParent(loner, child, clock).Error);
    }

    [Fact]
    public void A_task_with_subtasks_cannot_become_one()
    {
        var board = Family();
        Assert.Contains("has subtasks of its own", board.SetParent(parent, loner, clock).Error);
    }

    [Fact]
    public void The_same_parent_again_is_the_same_board()
    {
        var board = Family();
        Assert.Same(board, board.SetParent(child, parent, clock).Value);
        Assert.Same(board, board.SetParent(loner, Maybe<Guid>.None, clock).Value);
    }

    [Fact]
    public void Clearing_the_parent_frees_the_subtask()
    {
        var board = Family().SetParent(child, Maybe<Guid>.None, clock).Value;
        Assert.True(Get(board, child).ParentId.HasNoValue);
    }

    [Fact]
    public void A_closed_parent_is_allowed()
    {
        var board = Note(Note(TaskBoard.Empty, parent, ws), child, ws);
        board = board.Close(parent, clock).Value;

        Assert.Equal(Maybe.From(parent), Get(board.SetParent(child, parent, clock).Value, child).ParentId);
    }

    // --- AddNote with a parent -------------------------------------------------------------------

    [Fact]
    public void A_note_with_a_parent_and_no_workspace_takes_the_parents()
    {
        var board = Note(TaskBoard.Empty, parent, ws);
        var (_, task) = board.AddNote("Child", Free, clock, child, Quadrant.IV, parent).Value;

        Assert.Equal(Maybe.From(parent), task.ParentId);
        Assert.Equal(Maybe.From(ws), task.WorkspaceId);
    }

    [Fact]
    public void A_note_with_a_parent_and_a_workspace_keeps_the_workspace()
    {
        var board = Note(TaskBoard.Empty, parent, ws);
        var (_, task) = board.AddNote("Child", other, clock, child, Quadrant.IV, parent).Value;

        Assert.Equal(Maybe.From(other), task.WorkspaceId);
    }

    [Fact]
    public void A_note_under_a_subtask_is_refused()
    {
        var board = Family();
        Assert.StartsWith("Subtasks go one level deep", board.AddNote("Grandchild", ws, clock, fresh, Quadrant.IV, child).Error);
    }

    [Fact]
    public void A_note_under_an_unknown_parent_is_refused() =>
        Assert.StartsWith("No task with id", TaskBoard.Empty.AddNote("Child", ws, clock, child, Quadrant.IV, parent).Error);

    // --- a session's subtask ---------------------------------------------------------------------

    [Fact]
    public void A_session_on_a_top_task_makes_a_child_for_claude()
    {
        var board = TaskBoard.Empty.Create("Parent", ws, Session("s1"), clock, parent).Value.Item1;

        var (after, task) = board.AddSubtask("s1", "Next piece", clock, fresh).Value;

        Assert.Equal(Maybe.From(parent), task.ParentId);
        Assert.Equal(TaskStatus.Backlog, task.Status);
        Assert.True(task.Assignee.IsClaude);
        Assert.Equal(Maybe.From(ws), task.WorkspaceId);
        Assert.Empty(task.Sessions);
        Assert.Equal(Get(board, parent), Get(after, parent));
    }

    [Fact]
    public void A_session_on_a_subtask_makes_a_sibling()
    {
        var board = Family().Join(child, Session("s1"), clock).Value;

        var (_, task) = board.AddSubtask("s1", "Another piece", clock, fresh).Value;

        Assert.Equal(Maybe.From(parent), task.ParentId);
    }

    [Fact]
    public void A_session_with_no_task_cannot_make_a_subtask() =>
        Assert.True(Family().AddSubtask("nobody", "Piece", clock, fresh).IsFailure);

    // --- "Add subtask…" on a line ----------------------------------------------------------------

    [Fact]
    public void Add_subtask_on_a_top_line_is_under_that_line() =>
        Assert.Equal(parent, Family().SubtaskParentFor(parent).Value.Id);

    [Fact]
    public void Add_subtask_on_a_subtask_line_is_under_its_parent() =>
        Assert.Equal(parent, Family().SubtaskParentFor(child).Value.Id);

    [Fact]
    public void Add_subtask_on_an_unknown_line_is_none() =>
        Assert.True(Family().SubtaskParentFor(fresh).HasNoValue);

    // --- Delete ----------------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_parent_frees_its_subtasks()
    {
        var board = Family();
        Later();

        var after = board.Delete(parent, clock).Value;

        Assert.All(new[] { child, sibling }, id =>
        {
            Assert.True(Get(after, id).ParentId.HasNoValue);
            Assert.Equal(clock, Get(after, id).UpdatedAt);
        });
        Assert.Equal(Get(board, loner), Get(after, loner));
    }

    // --- Family ----------------------------------------------------------------------------------

    [Fact]
    public void A_subtasks_family_is_its_parent_and_siblings()
    {
        var family = Family().Family(child).Value;

        Assert.Equal(child, family.Task.Id);
        Assert.Equal(parent, family.Parent.Value.Id);
        Assert.Equal([sibling], family.Siblings.Select(t => t.Id));
        Assert.Empty(family.Subtasks);
    }

    [Fact]
    public void A_parents_family_is_its_subtasks_in_pile_order()
    {
        var board = Family();
        var family = board.Family(parent).Value;

        Assert.True(family.Parent.HasNoValue);
        Assert.Empty(family.Siblings);
        // Both are notes in quadrant IV; the later added is on top, as the pile shows them.
        Assert.Equal(board.Pile().Where(t => t.ParentId == parent).Select(t => t.Id), family.Subtasks.Select(t => t.Id));
    }

    [Fact]
    public void Closed_subtasks_come_after_open_ones_latest_closed_first()
    {
        var board = Family();
        Later();
        board = board.Close(sibling, clock).Value;

        Assert.Equal([child, sibling], board.Family(parent).Value.Subtasks.Select(t => t.Id));
    }

    [Fact]
    public void A_parent_that_no_longer_exists_reads_as_none()
    {
        var board = new TaskBoard(Family().Tasks.Where(t => t.Id != parent).ToArray());

        Assert.True(board.Family(child).HasNoValue);
    }

    [Fact]
    public void A_lone_task_has_no_family() =>
        Assert.True(Family().Family(loner).HasNoValue);

    // --- Offered and the pile --------------------------------------------------------------------

    [Fact]
    public void A_free_claude_subtask_of_a_parent_with_sessions_is_offered()
    {
        var board = TaskBoard.Empty.Create("Parent", Free, Session("s1"), clock, parent).Value.Item1;
        board = board.AddSubtask("s1", "Piece", clock, fresh).Value.Item1;

        Assert.Contains(board.Offered(ws), t => t.Id == fresh);
        Assert.Contains(board.Offered(Free), t => t.Id == fresh);
    }

    [Fact]
    public void A_free_subtask_of_a_parent_without_sessions_is_not_offered()
    {
        var board = Family().Assign(child, Assignee.Claude, clock).Value;

        Assert.DoesNotContain(board.Offered(ws), t => t.Id == child);
    }

    [Fact]
    public void Parent_links_do_not_move_lines()
    {
        var linked = Family();
        var unlinked = new TaskBoard(linked.Tasks.Select(t => t with { ParentId = Maybe<Guid>.None }).ToArray());

        Assert.Equal(unlinked.Pile().Select(t => t.Id), linked.Pile().Select(t => t.Id));
    }
}
