using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Task links on the board: closing as a duplicate, related links read both ways, what deleting does to them, and the
// picker's candidates. Design: docs/superpowers/specs/2026-11-25-task-links-design.md.
public class TaskLinksTests
{
    DateTimeOffset clock = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid original = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    static readonly Guid copy = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    static readonly Guid third = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    static readonly Guid parent = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");
    static readonly Guid unknown = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000ff");

    void Later(int minutes = 1) => clock = clock.AddMinutes(minutes);

    TaskBoard Note(TaskBoard board, Guid id, string title) => board.AddNote(title, ws, clock, id).Value.Item1;

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    TaskBoard Three() => Note(Note(Note(TaskBoard.Empty, original, "Open Claude button"), copy, "Open claude without the task"), third, "Task links");

    TaskSession Live(string id) => new(id, @"C:\repos\Deskbar", clock, Maybe<DateTimeOffset>.None);

    // --- duplicate ---------------------------------------------------------------------------------

    [Fact]
    public void Marking_a_duplicate_closes_the_task_and_names_the_original()
    {
        var board = Three();
        Later();

        var after = Get(board.MarkDuplicate(copy, original, clock).Value, copy);

        Assert.Equal(TaskStatus.Closed, after.Status);
        Assert.Equal(Maybe.From(original), after.DuplicateOf);
        Assert.True(after.IsDuplicate);
        Assert.Equal(clock, after.UpdatedAt);
    }

    [Fact]
    public void The_original_lists_its_duplicates_and_the_duplicate_its_original()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;

        Assert.Equal([copy], board.DuplicatesOf(original).Select(t => t.Id));
        Assert.Equal(Maybe.From(original), board.DuplicateTarget(Get(board, copy)).Map(t => t.Id));
        Assert.Equal([copy], board.Links(original).Value.Duplicates.Select(t => t.Id));
        Assert.Equal(original, board.Links(copy).Value.DuplicateOf.Value.Id);
    }

    [Fact]
    public void A_task_cannot_duplicate_itself()
    {
        Assert.Equal("A task cannot be a duplicate of itself", Three().MarkDuplicate(copy, copy, clock).Error);
    }

    [Fact]
    public void An_unknown_original_is_refused_and_nothing_closes()
    {
        var board = Three();
        Assert.StartsWith("No task with id", board.MarkDuplicate(copy, unknown, clock).Error);
        Assert.True(Get(board, copy).IsOpen);
    }

    [Fact]
    public void A_duplicate_of_a_duplicate_names_the_original()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;

        var after = board.MarkDuplicate(third, copy, clock).Value;

        Assert.Equal(Maybe.From(original), Get(after, third).DuplicateOf);
    }

    [Fact]
    public void A_chain_leading_back_to_the_task_is_refused()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;
        Assert.Equal("A task cannot be a duplicate of itself", board.MarkDuplicate(original, copy, clock).Error);
    }

    [Fact]
    public void Reopening_a_duplicate_drops_the_link()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;

        var reopened = Get(board.Reopen(copy, clock).Value, copy);

        Assert.True(reopened.IsOpen);
        Assert.Equal(Maybe<Guid>.None, reopened.DuplicateOf);
        Assert.False(reopened.IsDuplicate);
    }

    [Fact]
    public void A_status_set_by_hand_on_a_duplicate_drops_the_link_too()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;
        Assert.Equal(Maybe<Guid>.None, Get(board.SetStatus(copy, TaskStatus.Done, clock).Value, copy).DuplicateOf);
    }

    [Fact]
    public void A_task_with_open_subtasks_cannot_close_as_a_duplicate()
    {
        var board = Note(Three(), parent, "Parent").SetParent(third, parent, clock).Value;
        Assert.Equal(TaskViews.CloseSubtasksFirst(["Task links"]), board.MarkDuplicate(parent, original, clock).Error);
    }

    [Fact]
    public void Claude_still_working_refuses_unless_the_session_itself_marks_it()
    {
        var board = Three().Join(copy, Live("s1"), clock).Value;

        Assert.Equal(TaskViews.ClaudeStillWorkingRefusal, board.MarkDuplicate(copy, original, clock).Error);
        Assert.True(Get(board.MarkDuplicate(copy, original, clock, stoppingClaude: true).Value, copy).IsDuplicate);
    }

    [Fact]
    public void A_closed_task_can_be_marked_a_duplicate_and_stays_closed()
    {
        var board = Three().Close(copy, clock).Value;
        Later();

        var after = Get(board.MarkDuplicate(copy, original, clock).Value, copy);

        Assert.True(after.IsDuplicate);
        Assert.Equal(clock, after.UpdatedAt);
    }

    [Fact]
    public void The_same_duplicate_again_is_the_same_board()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;
        Assert.Same(board, board.MarkDuplicate(copy, original, clock).Value);
    }

    [Fact]
    public void Deleting_the_original_leaves_its_duplicate_closed_without_a_link()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;

        var after = Get(board.Delete(original, clock).Value, copy);

        Assert.Equal(TaskStatus.Closed, after.Status);
        Assert.Equal(Maybe<Guid>.None, after.DuplicateOf);
    }

    // --- related -----------------------------------------------------------------------------------

    [Fact]
    public void A_related_link_reads_both_ways_and_is_stored_once()
    {
        var board = Three().Relate(third, original, clock).Value;

        Assert.Equal([original], board.RelatedOf(third).Select(t => t.Id));
        Assert.Equal([third], board.RelatedOf(original).Select(t => t.Id));
        Assert.Equal([original], Get(board, third).Related);
        Assert.Empty(Get(board, original).Related);
    }

    [Fact]
    public void Relating_again_either_way_is_the_same_board()
    {
        var board = Three().Relate(third, original, clock).Value;
        Assert.Same(board, board.Relate(original, third, clock).Value);
        Assert.Same(board, board.Relate(third, original, clock).Value);
    }

    [Fact]
    public void A_task_cannot_relate_to_itself()
    {
        Assert.Equal("A task cannot be related to itself", Three().Relate(copy, copy, clock).Error);
    }

    [Fact]
    public void Relating_moves_no_line()
    {
        var board = Three();
        var before = board.Pile().Select(t => t.Id).ToArray();
        Assert.Equal(before, board.Relate(third, original, clock).Value.Pile().Select(t => t.Id));
    }

    [Fact]
    public void Unrelating_from_either_side_removes_the_link()
    {
        var board = Three().Relate(third, original, clock).Value;

        var after = board.Unrelate(original, third, clock).Value;

        Assert.Empty(after.RelatedOf(third));
        Assert.Empty(after.RelatedOf(original));
        Assert.Same(after, after.Unrelate(original, third, clock).Value);
    }

    [Fact]
    public void Deleting_a_related_task_removes_the_link()
    {
        var board = Three().Relate(third, original, clock).Value;
        Assert.Empty(Get(board.Delete(original, clock).Value, third).Related);
    }

    [Fact]
    public void Related_lists_open_tasks_first_then_closed_ones()
    {
        var board = Three().Relate(third, original, clock).Value.Relate(third, copy, clock).Value.Close(original, clock).Value;
        Assert.Equal([copy, original], board.RelatedOf(third).Select(t => t.Id));
    }

    [Fact]
    public void A_task_without_links_has_no_links()
    {
        Assert.Equal(Maybe<TaskLinks>.None, Three().Links(third));
    }

    // --- the picker's candidates -------------------------------------------------------------------

    [Fact]
    public void Candidates_match_every_typed_word_and_leave_out_the_task_itself()
    {
        var board = Three();
        Assert.Equal([original, copy], board.LinkCandidates(third, "open CLAUDE", duplicate: false).Select(t => t.Id).OrderBy(x => x));
        Assert.DoesNotContain(third, board.LinkCandidates(third, "", duplicate: false).Select(t => t.Id));
    }

    [Fact]
    public void Candidates_for_related_leave_out_tasks_already_related()
    {
        var board = Three().Relate(third, original, clock).Value;
        Assert.Equal([copy], board.LinkCandidates(third, "", duplicate: false).Select(t => t.Id));
    }

    [Fact]
    public void Candidates_for_a_duplicate_leave_out_duplicates()
    {
        var board = Three().MarkDuplicate(copy, original, clock).Value;
        Assert.Equal([original], board.LinkCandidates(third, "", duplicate: true).Select(t => t.Id));
    }
}
