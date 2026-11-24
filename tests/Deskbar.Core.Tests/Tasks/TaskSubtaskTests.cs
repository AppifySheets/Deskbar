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

    // --- an open subtask under a closed parent reopens the parent (task f47b18df, Petre: "reopen parent") ---------

    [Fact]
    public void An_open_task_put_under_a_closed_parent_reopens_it()
    {
        var board = Note(Note(TaskBoard.Empty, parent, ws), child, ws).Close(parent, clock).Value;
        Later();

        var after = board.SetParent(child, parent, clock).Value;

        Assert.Equal(Maybe.From(parent), Get(after, child).ParentId);
        Assert.Equal(TaskStatus.Backlog, Get(after, parent).Status);
        Assert.Equal(clock, Get(after, parent).UpdatedAt);
    }

    [Fact]
    public void A_closed_task_put_under_a_closed_parent_leaves_it_closed()
    {
        var board = Note(Note(TaskBoard.Empty, parent, ws), child, ws).Close(child, clock).Value.Close(parent, clock).Value;

        var after = board.SetParent(child, parent, clock).Value;

        Assert.Equal(Maybe.From(parent), Get(after, child).ParentId);
        Assert.Equal(TaskStatus.Closed, Get(after, parent).Status);
    }

    [Fact]
    public void A_parent_with_a_live_session_comes_back_in_progress()
    {
        var board = TaskBoard.Empty.Create("Parent", ws, Session("s1"), clock, parent).Value.Item1;
        board = Note(board, child, ws).SetParent(child, parent, clock).Value
            .Close(child, clock).Value.Close(parent, clock, stoppingClaude: true).Value;

        var after = board.Reopen(child, clock).Value;

        Assert.Equal(TaskStatus.InProgress, Get(after, parent).Status);
        Assert.True(Get(after, parent).Assignee.IsClaude);
    }

    // The closed family the next tests reopen a subtask of: parent, child and sibling, all three closed.
    TaskBoard ClosedFamily() =>
        Family().Close(child, clock).Value.Close(sibling, clock).Value.Close(parent, clock).Value;

    [Fact]
    public void Reopening_a_subtask_reopens_its_closed_parent()
    {
        var after = ClosedFamily().Reopen(child, clock).Value;

        Assert.True(Get(after, child).IsOpen);
        Assert.True(Get(after, parent).IsOpen);
        // Only the parent comes back with it: the other subtask stays closed.
        Assert.False(Get(after, sibling).IsOpen);
    }

    [Fact]
    public void Setting_a_closed_subtasks_status_reopens_its_parent() =>
        Assert.True(Get(ClosedFamily().SetStatus(child, TaskStatus.InProgress, clock).Value, parent).IsOpen);

    [Fact]
    public void Undoing_a_subtasks_close_reopens_its_parent() =>
        Assert.True(Get(ClosedFamily().UndoClose(child, TaskStatus.Backlog, Maybe<string>.None, clock).Value, parent).IsOpen);

    [Fact]
    public void A_note_added_under_a_closed_parent_reopens_it()
    {
        var (after, task) = ClosedFamily().AddNote("More", ws, clock, fresh, Quadrant.IV, parent).Value;

        Assert.Equal(Maybe.From(parent), task.ParentId);
        Assert.True(Get(after, parent).IsOpen);
    }

    [Fact]
    public void The_reopened_parent_goes_to_the_top_of_its_quadrant()
    {
        var board = ClosedFamily();
        Later();

        var after = board.Reopen(child, clock).Value;

        // The lowest rank is the top (TaskBoard.TopRank). The parent is reopened after its subtask, so it lands just above it.
        Assert.True(Get(after, parent).Rank < Get(after, child).Rank);
        Assert.True(Get(after, child).Rank < Get(after, loner).Rank);
    }

    [Fact]
    public void Reopening_a_plain_task_touches_no_other()
    {
        var board = Note(TaskBoard.Empty, loner, ws).Close(loner, clock).Value;
        board = Note(board, parent, ws).Close(parent, clock).Value;

        Assert.False(Get(board.Reopen(loner, clock).Value, parent).IsOpen);
    }

    // --- AddNote with a parent -------------------------------------------------------------------

    [Fact]
    public void A_note_with_a_parent_is_its_subtask()
    {
        var board = Note(TaskBoard.Empty, parent, ws);
        var (_, task) = board.AddNote("Child", ws, clock, child, Quadrant.IV, parent).Value;

        Assert.Equal(Maybe.From(parent), task.ParentId);
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

    // --- what a line shows (Petre's pick A) -------------------------------------------------------

    [Fact]
    public void A_parents_count_is_its_open_subtasks_only()
    {
        var board = Family();
        Assert.Equal(2, board.OpenSubtaskCount(parent));
        Assert.Equal(1, board.Close(sibling, clock).Value.OpenSubtaskCount(parent));
        Assert.Equal(0, board.OpenSubtaskCount(loner));
    }

    // --- closing a parent (task 842e1ba4: "closing a parent task shouldn't be possible if not all children are also
    // closed") -------------------------------------------------------------------------------------

    [Fact]
    public void A_parent_with_open_subtasks_cannot_be_closed()
    {
        var board = Family();

        var refused = board.Close(parent, clock);

        Assert.Equal("Close its 2 open subtasks first", refused.Error);
        Assert.True(Get(board, parent).IsOpen);
    }

    [Fact]
    public void The_refusal_counts_the_one_subtask_still_open()
    {
        var board = Family().Close(sibling, clock).Value;
        Assert.Equal("Close its open subtask first", board.Close(parent, clock).Error);
    }

    [Fact]
    public void A_parent_closes_once_every_subtask_is_closed()
    {
        var board = Family().Close(child, clock).Value.Close(sibling, clock).Value;
        Later();

        var closed = Get(board.Close(parent, clock).Value, parent);

        Assert.Equal(TaskStatus.Closed, closed.Status);
        Assert.Equal(clock, closed.UpdatedAt);
    }

    // "Stop Claude and close" (task 37cd7081) gets past a live session only, never past open subtasks.
    [Fact]
    public void Stopping_claude_never_closes_a_parent_with_open_subtasks() =>
        Assert.Equal(TaskViews.CloseSubtasksFirst(2), Family().Close(parent, clock, stoppingClaude: true).Error);

    [Fact]
    public void A_subtask_closes_while_its_parent_is_open() =>
        Assert.Equal(TaskStatus.Closed, Get(Family().Close(child, clock).Value, child).Status);

    [Fact]
    public void Closing_a_closed_parent_still_says_it_is_closed()
    {
        // A board from before the rule can hold a closed parent with an open subtask; closing it again is refused for
        // being closed, not for the subtask. SetParent would reopen the parent now (task f47b18df), so the old board is
        // built by hand.
        var board = Note(Note(TaskBoard.Empty, parent, ws), child, ws).Close(parent, clock).Value;
        board = new TaskBoard(board.Tasks.Select(t => t.Id == child ? t with { ParentId = parent } : t).ToArray());

        Assert.Equal("The task is closed.", board.Close(parent, clock).Error);
    }

    [Theory]
    [InlineData(false, 0, "Tick off: closes the note")]
    [InlineData(true, 0, "Tick off: closes Claude's task")]
    [InlineData(true, 1, "Close its open subtask first")]
    [InlineData(false, 3, "Close its 3 open subtasks first")]
    public void The_tick_says_why_it_will_not_close_a_parent(bool claudes, int open, string tip) =>
        Assert.Equal(tip, TaskViews.TickTip(claudes, open));

    [Fact]
    public void A_subtasks_parent_title_is_read_from_the_board()
    {
        var board = Family();
        Assert.Equal("Parent", board.ParentOf(Get(board, child)).Value.Title);
        Assert.True(board.ParentOf(Get(board, loner)).HasNoValue);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1\u00A0subtask")]
    [InlineData(3, "3\u00A0subtasks")]
    public void The_count_reads_in_words(int count, string words) =>
        Assert.Equal(words, TaskViews.SubtaskCountText(count));

    [Fact]
    public void The_arrow_names_the_parent_on_a_full_line_and_not_on_a_slim_one()
    {
        Assert.Equal("↳ Parent › ", TaskViews.Breadcrumb("Parent", full: true));
        Assert.Equal("↳ ", TaskViews.Breadcrumb("Parent", full: false));
        Assert.Equal("", TaskViews.Breadcrumb(null, full: true));
        // A long parent title is cut, so the subtask's own title keeps the line.
        Assert.Equal("↳ Subtasks support on de… › ", TaskViews.Breadcrumb("Subtasks support on deskbar", full: true));
    }

    // Final review, finding 1: an adopted task is dropped when its only session moves on (Ruling 11), but not once it has
    // subtasks: a subtask is something added to it, and dropping it would leave the subtasks naming nothing.
    [Fact]
    public void An_adopted_parent_with_subtasks_survives_its_session_moving_on()
    {
        var board = TaskBoard.Empty.Create("Adopted", ws, Session("s1"), clock, parent).Value.Item1;
        board = new TaskBoard(board.Tasks.Select(t => t with { Adopted = true }).ToArray());
        board = board.AddSubtask("s1", "Piece", clock, child).Value.Item1;

        board = board.Create("Elsewhere", ws, Session("s1"), clock, fresh).Value.Item1;

        Assert.Contains(board.Tasks, t => t.Id == parent);
        Assert.Equal(parent, board.Family(child).Value.Parent.Value.Id);
    }

    // Final review, finding 4: "none" chosen for a subtask means none. The board takes the workspace it is given; the
    // callers (the add boxes, `task add --parent` without --workspace, a session's `task subtask`) fill in the parent's.
    [Fact]
    public void A_subtask_given_no_workspace_stays_on_none()
    {
        var board = Note(TaskBoard.Empty, parent, ws);
        var (_, task) = board.AddNote("Child", Free, clock, child, Quadrant.IV, parent).Value;

        Assert.True(task.WorkspaceId.HasNoValue);
    }

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
