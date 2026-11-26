using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The two flags (important, urgent), Covey's four quadrants they make, and the manual order inside each quadrant.
// Design: progress.md Rulings 24, 25 and 28 (task-12-brief.md). The pile is Waiting on you pinned on top in its
// automatic order, then quadrants I to IV, each in an order Petre sets by dragging. A new task goes to the top of its
// quadrant, a Done task keeps its place, a reopened one comes back at the top, and a status line never moves a line.
public class TaskOrderTests
{
    DateTimeOffset clock = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid id1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    static readonly Guid id2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    static readonly Guid id3 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    static readonly Guid id4 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");
    static readonly Guid id5 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000005");
    static readonly Guid id6 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000006");

    static readonly Maybe<Guid> Free = Maybe<Guid>.None;

    void Later(int minutes = 1) => clock = clock.AddMinutes(minutes);

    TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    // Notes made a minute apart, the first made first, so with nothing dragged the newest is on top.
    TaskBoard Notes(params Guid[] ids) =>
        ids.Aggregate(TaskBoard.Empty, (board, id) =>
        {
            Later();
            return board.AddNote($"note {id.ToString()[^1]}", Free, clock, id).Value.Board;
        });

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    static IReadOnlyList<Guid> Pile(TaskBoard board) => board.Pile().Select(t => t.Id).ToArray();

    static IReadOnlyList<Guid> Group(TaskBoard board, Quadrant quadrant) => board.Group(quadrant).Select(t => t.Id).ToArray();

    [Fact]
    public void A_new_task_has_no_flags_and_tops_quadrant_IV()
    {
        var board = Notes(id1, id2);

        Assert.False(Get(board, id2).Important);
        Assert.False(Get(board, id2).Urgent);
        Assert.Equal(Quadrant.IV, Get(board, id2).Quadrant);
        Assert.Equal([id2, id1], Group(board, Quadrant.IV));
    }

    // Ruling 37: flags when adding. A note made with its flags lands at the top of the quadrant they give, among the
    // tasks already there, with both flags set as asked.
    [Theory]
    [InlineData(false, false, Quadrant.IV)]
    [InlineData(true, false, Quadrant.II)]
    [InlineData(false, true, Quadrant.III)]
    [InlineData(true, true, Quadrant.I)]
    public void A_note_added_with_flags_tops_the_quadrant_they_give(bool important, bool urgent, Quadrant quadrant)
    {
        var board = Notes(id1, id2);
        board = board.SetFlag(id1, TaskFlag.Important, important, clock).Value.SetFlag(id1, TaskFlag.Urgent, urgent, clock).Value;
        board = board.SetFlag(id2, TaskFlag.Important, important, clock).Value.SetFlag(id2, TaskFlag.Urgent, urgent, clock).Value;
        Later();

        board = board.AddNote("flagged", Free, clock, id3, QuadrantOf.Flags(important, urgent)).Value.Board;

        Assert.Equal(important, Get(board, id3).Important);
        Assert.Equal(urgent, Get(board, id3).Urgent);
        Assert.Equal(id3, Group(board, quadrant)[0]);
        Assert.Equal(3, Group(board, quadrant).Count);
    }

    [Fact]
    public void The_flags_make_covey_quadrants()
    {
        Assert.Equal(Quadrant.I, QuadrantOf.Flags(important: true, urgent: true));
        Assert.Equal(Quadrant.II, QuadrantOf.Flags(important: true, urgent: false));
        Assert.Equal(Quadrant.III, QuadrantOf.Flags(important: false, urgent: true));
        Assert.Equal(Quadrant.IV, QuadrantOf.Flags(important: false, urgent: false));
        Assert.True(Quadrant.I.IsImportant() && Quadrant.I.IsUrgent());
        Assert.True(Quadrant.II.IsImportant() && !Quadrant.II.IsUrgent());
        Assert.True(!Quadrant.III.IsImportant() && Quadrant.III.IsUrgent());
        Assert.True(!Quadrant.IV.IsImportant() && !Quadrant.IV.IsUrgent());
    }

    [Fact]
    public void The_pile_is_waiting_then_quadrants_I_to_IV()
    {
        var board = Notes(id1, id2, id3, id4, id5);
        Later();
        board = board.SetFlag(id1, TaskFlag.Important, true, clock).Value; // II
        board = board.SetFlag(id2, TaskFlag.Urgent, true, clock).Value; // III
        board = board.SetFlag(id3, TaskFlag.Important, true, clock).Value
            .SetFlag(id3, TaskFlag.Urgent, true, clock).Value; // I
        board = board.SetStatus(id4, TaskStatus.WaitingOnYou, clock).Value; // pinned, stays IV underneath
        // id5 is IV

        Assert.Equal([id4, id3, id1, id2, id5], Pile(board));
        Assert.Equal(Quadrant.I, Get(board, id3).Quadrant);
    }

    [Fact]
    public void Waiting_on_you_is_pinned_by_its_ask_whatever_its_flags()
    {
        var board = Notes(id1, id2, id3);
        Later();
        board = board.Join(id1, Session("s1"), clock).Value.Ask(id1, "One?", clock).Value;
        Later();
        board = board.Join(id2, Session("s2"), clock).Value.Ask(id2, "Two?", clock).Value;
        board = board.SetFlag(id1, TaskFlag.Important, true, clock).Value;

        Assert.Equal([id2, id1, id3], Pile(board));
        Assert.True(Get(board, id1).Important);
        Assert.Empty(Group(board, Quadrant.II));
    }

    [Fact]
    public void An_answered_task_lands_in_its_quadrant_at_its_rank()
    {
        var board = Notes(id1, id2, id3);
        Later();
        board = board.Join(id2, Session("s2"), clock).Value.Ask(id2, "Merge?", clock).Value;
        Assert.Equal([id2, id3, id1], Pile(board));

        Later();
        board = board.Answered("s2", clock);

        Assert.Equal([id3, id2, id1], Pile(board));
    }

    // Task eefb986a (Petre: "tasks marked as done should be in the waiting on me section"): a Done task is pinned on
    // top with the questions, under them, and keeps its rank, so it goes back to its old place when it is resumed.
    [Fact]
    public void A_done_task_is_pinned_under_the_questions_and_resumes_at_its_place()
    {
        var board = Notes(id1, id2, id3); // IV: 3 2 1
        Later();

        board = board.SetStatus(id1, TaskStatus.Done, clock).Value;
        Assert.Equal([id1, id3, id2], Pile(board));
        Assert.Equal([id3, id2], Group(board, Quadrant.IV));
        Assert.True(board.PositionOf(id1).HasNoValue);

        Later();
        board = board.SetStatus(id2, TaskStatus.WaitingOnYou, clock).Value;
        Assert.Equal([id2, id1, id3], Pile(board));

        board = board.SetStatus(id1, TaskStatus.InProgress, clock).Value;
        Assert.Equal([id2, id3, id1], Pile(board));
    }

    [Fact]
    public void The_latest_done_is_on_top_of_the_done_tasks()
    {
        var board = Notes(id1, id2, id3);
        Later();
        board = board.SetStatus(id3, TaskStatus.Done, clock).Value;
        Later();
        board = board.SetStatus(id1, TaskStatus.Done, clock).Value;

        Assert.Equal([id1, id3, id2], Pile(board));
    }

    // A done task takes its flags and stays pinned; a drag in its own quadrant moves it among the done tasks (task
    // f41d7857), never above the questions.
    [Fact]
    public void A_done_task_takes_its_flags_and_moves_only_among_the_done_tasks()
    {
        var board = Notes(id1, id2, id3, id4);
        board = board.SetStatus(id1, TaskStatus.Done, clock).Value;
        Later();
        board = board.MarkDone(id2, clock).Value;
        Later();
        board = board.Ask(id3, "which?", clock).Value;
        Assert.Equal([id3, id2, id1, id4], Pile(board));

        var flagged = board.SetFlag(id1, TaskFlag.Important, true, clock).Value;
        Assert.True(Get(flagged, id1).Important);
        Assert.Equal([id3, id2, id1, id4], Pile(flagged));

        board = board.Place(id1, Quadrant.IV, TaskPlace.Top, clock).Value;
        Assert.Equal([id3, id1, id2, id4], Pile(board));
        Assert.Contains("the done tasks on top", board.PlacePinned(id2, TaskPlace.Before(id3), clock).Error);
        Assert.Contains("the tasks waiting on you", board.PlacePinned(id3, TaskPlace.After(id1), clock).Error);
    }

    // Becoming Done puts a task on top of the done tasks, wherever the others were dragged; done again keeps its place.
    [Fact]
    public void A_task_becoming_done_goes_on_top_of_the_done_tasks()
    {
        var board = Notes(id1, id2, id3);
        board = board.MarkDone(id1, clock).Value;
        board = board.MarkDone(id2, clock).Value;
        board = board.PlacePinned(id1, TaskPlace.Top, clock).Value;
        Assert.Equal([id1, id2], board.PinnedPart(TaskStatus.Done).Select(t => t.Id));

        board = board.SetStatus(id3, TaskStatus.Done, clock).Value;
        board = board.MarkDone(id1, clock).Value;

        Assert.Equal([id3, id1, id2], board.PinnedPart(TaskStatus.Done).Select(t => t.Id));
    }

    // Task a1debf34: the Done tasks show the full discs (the change runs) first, then the hollow rings (not running yet),
    // each half in the pin order, under the questions. Without a hollow test the pin order stands alone, and the pins
    // themselves never move, so PinnedPart (which a drag places in) is untouched.
    [Fact]
    public void The_done_tasks_show_full_discs_first_then_hollow_rings_each_in_pin_order()
    {
        var board = Notes(id1, id2, id3, id4, id5);
        board = new[] { id1, id2, id3, id4 }.Aggregate(board, (b, id) => b.MarkDone(id, clock).Value);
        board = board.Join(id5, Session("s5"), clock).Value.Ask(id5, "Which one?", clock).Value;
        // Pin order, newest Done on top: id4, id3, id2, id1. The rings are id4 and id2.
        var rings = new[] { id4, id2 }.ToHashSet();
        bool Hollow(TaskItem t) => rings.Contains(t.Id);

        Assert.Equal([id5, id3, id1, id4, id2], board.Pinned(Hollow).Select(t => t.Id));
        Assert.Equal([id5, id3, id1, id4, id2], board.Pile(Hollow).Take(5).Select(t => t.Id));
        Assert.Equal([id5, id4, id3, id2, id1], board.Pinned().Select(t => t.Id));
        Assert.Equal([id4, id3, id2, id1], board.PinnedPart(TaskStatus.Done).Select(t => t.Id));
    }

    [Fact]
    public void A_status_line_or_a_comment_never_moves_a_line()
    {
        var board = Notes(id1, id2, id3);
        Later();
        board = board.Join(id1, Session("s1"), clock).Value;
        Later();

        board = board.SetStatusLine(id1, "waiting for CI", clock).Value.Comment(id1, Maybe<string>.None, "hi", clock, id6).Value;

        Assert.Equal([id3, id2, id1], Pile(board));
    }

    [Fact]
    public void Setting_a_flag_moves_the_task_to_the_top_of_its_new_quadrant_and_stamps_it()
    {
        var board = Notes(id1, id2, id3);
        Later();
        board = board.SetFlag(id1, TaskFlag.Important, true, clock).Value;
        board = board.SetFlag(id2, TaskFlag.Important, true, clock).Value;
        Later();

        board = board.SetFlag(id3, TaskFlag.Important, true, clock).Value;

        Assert.Equal([id3, id2, id1], Group(board, Quadrant.II));
        Assert.True(Get(board, id3).Important);
        Assert.Equal(clock, Get(board, id3).UpdatedAt);
    }

    [Fact]
    public void Setting_a_flag_to_what_it_already_is_changes_nothing()
    {
        var board = Notes(id1);
        Later();

        Assert.Same(board, board.SetFlag(id1, TaskFlag.Urgent, false, clock).Value);
    }

    [Fact]
    public void Turning_a_flag_off_moves_the_task_back()
    {
        var board = Notes(id1, id2);
        board = board.SetFlag(id1, TaskFlag.Urgent, true, clock).Value;
        Later();

        board = board.SetFlag(id1, TaskFlag.Urgent, false, clock).Value;

        Assert.False(Get(board, id1).Urgent);
        Assert.Equal([id1, id2], Group(board, Quadrant.IV));
    }

    [Fact]
    public void A_closed_task_takes_no_flag_and_no_move()
    {
        var board = Notes(id1, id2).Close(id1, clock).Value;

        Assert.Equal("The task is closed.", board.SetFlag(id1, TaskFlag.Important, true, clock).Error);
        Assert.Equal("The task is closed.", board.Place(id1, Quadrant.IV, TaskPlace.Top, clock).Error);
    }

    [Fact]
    public void Place_top_end_and_at_a_position_within_the_group()
    {
        var board = Notes(id1, id2, id3, id4); // IV: 4 3 2 1
        Later();

        var top = board.Place(id1, Quadrant.IV, TaskPlace.Top, clock).Value;
        Assert.Equal([id1, id4, id3, id2], Group(top, Quadrant.IV));
        Assert.Equal(clock, Get(top, id1).UpdatedAt);

        var end = board.Place(id4, Quadrant.IV, TaskPlace.End, clock).Value;
        Assert.Equal([id3, id2, id1, id4], Group(end, Quadrant.IV));

        var third = board.Place(id4, Quadrant.IV, TaskPlace.At(3), clock).Value;
        Assert.Equal([id3, id2, id4, id1], Group(third, Quadrant.IV));

        // A position past the end is the end; one below 1 is the top.
        Assert.Equal([id3, id2, id1, id4], Group(board.Place(id4, Quadrant.IV, TaskPlace.At(99), clock).Value, Quadrant.IV));
        Assert.Equal([id1, id4, id3, id2], Group(board.Place(id1, Quadrant.IV, TaskPlace.At(0), clock).Value, Quadrant.IV));
    }

    [Fact]
    public void Place_before_and_after_a_task()
    {
        var board = Notes(id1, id2, id3, id4); // IV: 4 3 2 1
        Later();

        Assert.Equal([id4, id1, id3, id2], Group(board.Place(id1, Quadrant.IV, TaskPlace.Before(id3), clock).Value, Quadrant.IV));
        Assert.Equal([id3, id2, id4, id1], Group(board.Place(id4, Quadrant.IV, TaskPlace.After(id2), clock).Value, Quadrant.IV));
    }

    [Fact]
    public void Place_moves_only_the_task_it_moves()
    {
        var board = Notes(id1, id2, id3);
        Later();

        var moved = board.Place(id1, Quadrant.IV, TaskPlace.Before(id2), clock).Value;

        Assert.Equal(Get(board, id2), Get(moved, id2));
        Assert.Equal(Get(board, id3), Get(moved, id3));
    }

    [Fact]
    public void Placing_a_task_where_it_already_is_changes_nothing()
    {
        var board = Notes(id1, id2, id3); // IV: 3 2 1
        Later();

        Assert.Same(board, board.Place(id2, Quadrant.IV, TaskPlace.At(2), clock).Value);
        Assert.Same(board, board.Place(id3, Quadrant.IV, TaskPlace.Top, clock).Value);
        Assert.Same(board, board.Place(id2, Quadrant.IV, TaskPlace.Before(id1), clock).Value);
        Assert.Same(board, board.Place(id2, Quadrant.IV, TaskPlace.After(id3), clock).Value);
    }

    [Fact]
    public void Dropping_into_another_quadrant_sets_its_flags()
    {
        var board = Notes(id1, id2, id3);
        board = board.SetFlag(id1, TaskFlag.Important, true, clock).Value
            .SetFlag(id1, TaskFlag.Urgent, true, clock).Value;
        board = board.SetFlag(id2, TaskFlag.Important, true, clock).Value
            .SetFlag(id2, TaskFlag.Urgent, true, clock).Value; // I: 2 1
        Later();

        board = board.Place(id3, Quadrant.I, TaskPlace.Before(id1), clock).Value;

        Assert.Equal([id2, id3, id1], Group(board, Quadrant.I));
        Assert.True(Get(board, id3).Important);
        Assert.True(Get(board, id3).Urgent);
        Assert.Equal(clock, Get(board, id3).UpdatedAt);
    }

    [Fact]
    public void Dropping_into_an_empty_quadrant_works()
    {
        var board = Notes(id1, id2);

        board = board.Place(id1, Quadrant.III, TaskPlace.End, clock).Value;

        Assert.Equal([id1], Group(board, Quadrant.III));
        Assert.Equal(Quadrant.III, Get(board, id1).Quadrant);
    }

    // --- the Waiting on you block's own order (task f41d7857: "drag and drop of tasks waiting on me doesn't work ...
    // it should") ---------------------------------------------------------------------------------------------------

    // Three notes asked about in turn, id1 first: the latest ask is on top, as before pins existed.
    TaskBoard Asked(params Guid[] ids) =>
        ids.Aggregate(Notes(ids), (board, id) =>
        {
            Later();
            return board.Ask(id, $"question {id.ToString()[^1]}", clock).Value;
        });

    static IReadOnlyList<Guid> Block(TaskBoard board) => board.PinnedPart(TaskStatus.WaitingOnYou).Select(t => t.Id).ToArray();

    [Fact]
    public void A_new_ask_goes_on_top_of_the_waiting_block()
    {
        var board = Asked(id1, id2, id3);

        Assert.Equal([id3, id2, id1], Block(board));
        Assert.Equal([id3, id2, id1], Pile(board).Take(3));
    }

    [Fact]
    public void A_waiting_task_is_dragged_within_the_block_and_keeps_its_flags_and_rank()
    {
        var board = Asked(id1, id2, id3);
        var rank = Get(board, id3).Rank;

        board = board.PlacePinned(id3, TaskPlace.After(id1), clock).Value;
        Assert.Equal([id2, id1, id3], Block(board));
        board = board.PlacePinned(id1, TaskPlace.Before(id2), clock).Value;
        Assert.Equal([id1, id2, id3], Block(board));
        board = board.PlacePinned(id3, TaskPlace.At(2), clock).Value;
        Assert.Equal([id1, id3, id2], Block(board));

        Assert.Equal(rank, Get(board, id3).Rank);
        Assert.Equal(Quadrant.IV, Get(board, id3).Quadrant);
        Assert.Equal(clock, Get(board, id3).UpdatedAt);
    }

    [Fact]
    public void A_drop_where_a_waiting_task_already_is_returns_the_same_board()
    {
        var board = Asked(id1, id2);

        Assert.Same(board, board.PlacePinned(id2, TaskPlace.Top, clock).Value);
        Assert.Same(board, board.PlacePinned(id2, TaskPlace.Before(id1), clock).Value);
    }

    // `task order` and the bar's drop say the task's own quadrant: for a waiting task that is a place in the block.
    [Fact]
    public void Place_in_its_own_quadrant_moves_a_waiting_task_within_the_block()
    {
        var board = Asked(id1, id2, id3);

        board = board.Place(id3, Quadrant.IV, TaskPlace.End, clock).Value;

        Assert.Equal([id2, id1, id3], Block(board));
        Assert.Equal(TaskStatus.WaitingOnYou, Get(board, id3).Status);
    }

    // The Matrix's drag to another cell: the flags change, as a toggle would change them, and the task stays pinned.
    [Fact]
    public void Place_in_another_quadrant_gives_a_waiting_task_its_flags_and_it_stays_pinned()
    {
        var board = Asked(id1, id2);

        board = board.Place(id1, Quadrant.II, TaskPlace.Top, clock).Value;

        Assert.Equal(Quadrant.II, Get(board, id1).Quadrant);
        Assert.Equal(TaskStatus.WaitingOnYou, Get(board, id1).Status);
        Assert.Equal([id2, id1], Block(board));
        Assert.Empty(Group(board, Quadrant.II));
    }

    [Fact]
    public void An_answered_task_goes_back_to_its_place_in_its_quadrant_wherever_it_was_dragged_in_the_block()
    {
        var board = Notes(id1, id2, id3); // IV: id3, id2, id1
        board = board.Ask(id2, "which?", clock).Value;
        board = board.Ask(id1, "and this?", clock).Value;
        board = board.PlacePinned(id2, TaskPlace.Top, clock).Value;

        board = board.SetStatus(id2, TaskStatus.InProgress, clock).Value;

        Assert.Equal([id3, id2], Group(board, Quadrant.IV));
    }

    [Fact]
    public void A_task_set_to_waiting_by_hand_joins_the_block_on_top_and_a_waiting_one_keeps_its_place()
    {
        var board = Asked(id1, id2, id3);
        board = board.PlacePinned(id3, TaskPlace.End, clock).Value; // id2, id1, id3
        board = board.AddNote("new", Free, clock, id4).Value.Board;

        board = board.SetStatus(id4, TaskStatus.WaitingOnYou, clock).Value;
        Assert.Equal([id4, id2, id1, id3], Block(board));

        board = board.SetStatus(id3, TaskStatus.WaitingOnYou, clock).Value;
        Assert.Equal([id4, id2, id1, id3], Block(board));
    }

    [Fact]
    public void Placing_in_the_waiting_block_refuses_a_task_not_waiting_a_closed_one_and_a_stranger()
    {
        var board = Asked(id1, id2);
        board = board.AddNote("plain", Free, clock, id3).Value.Board;

        Assert.Contains("neither waiting", board.PlacePinned(id3, TaskPlace.Top, clock).Error);
        Assert.Contains("the tasks waiting on you", board.PlacePinned(id1, TaskPlace.Before(id3), clock).Error);
        Assert.True(board.PlacePinned(Guid.NewGuid(), TaskPlace.Top, clock).IsFailure);
    }

    // As with ranks: dragging into one gap over and over halves it until the numbers run out, and then the block is
    // numbered afresh. The order must survive that.
    [Fact]
    public void Many_drops_into_one_gap_of_the_waiting_block_keep_its_order()
    {
        var ids = Enumerable.Range(1, 60).Select(n => Guid.Parse($"dddddddd-0000-0000-0000-{n:D12}")).ToArray();
        var board = Asked(ids); // the block: ids[59] first
        var expected = Block(board).ToList();

        ids.Skip(2).Take(56).ToList().ForEach(id =>
        {
            board = board.PlacePinned(id, TaskPlace.After(ids[0]), clock).Value;
            expected.Remove(id);
            expected.Insert(expected.IndexOf(ids[0]) + 1, id);
        });

        Assert.Equal(expected, Block(board));
    }

    [Fact]
    public void Placing_beside_a_task_outside_the_group_is_refused()
    {
        var board = Notes(id1, id2, id3);
        board = board.SetFlag(id3, TaskFlag.Urgent, true, clock).Value;
        board = board.SetStatus(id2, TaskStatus.WaitingOnYou, clock).Value;

        Assert.True(board.Place(id1, Quadrant.IV, TaskPlace.Before(id3), clock).IsFailure);
        Assert.True(board.Place(id1, Quadrant.IV, TaskPlace.After(id2), clock).IsFailure);
        Assert.True(board.Place(id1, Quadrant.IV, TaskPlace.Before(Guid.NewGuid()), clock).IsFailure);
        Assert.True(board.Place(Guid.NewGuid(), Quadrant.IV, TaskPlace.Top, clock).IsFailure);
    }

    // Each move takes the middle of its neighbours' ranks; dragging into the same gap over and over halves it until
    // the numbers run out, and then the group is numbered afresh. The order must survive that.
    [Fact]
    public void Many_drops_into_one_gap_keep_the_order()
    {
        var ids = Enumerable.Range(1, 80).Select(n => Guid.Parse($"cccccccc-0000-0000-0000-{n:D12}")).ToArray();
        var board = Notes(ids); // IV: newest (ids[79]) first
        var expected = ids.Reverse().ToList();

        // Every time, the last task of the group goes to second place: always between the same first task and the
        // one that went there before.
        board = Enumerable.Range(0, 78).Aggregate(board, (b, _) =>
        {
            Later();
            var last = expected[^1];
            expected.RemoveAt(expected.Count - 1);
            expected.Insert(1, last);
            return b.Place(last, Quadrant.IV, TaskPlace.At(2), clock).Value;
        });

        Assert.Equal(expected, Group(board, Quadrant.IV));
    }

    [Fact]
    public void A_reopened_task_comes_back_at_the_top_of_its_group()
    {
        var board = Notes(id1, id2, id3); // IV: 3 2 1
        board = board.Close(id1, clock).Value;
        Later();

        var reopened = board.Reopen(id1, clock).Value;
        Assert.Equal([id1, id3, id2], Group(reopened, Quadrant.IV));

        // Set by hand from Closed (a closed task set to Done is reopened as Done) is a reopening too: pinned while Done,
        // and on top of its quadrant once it is resumed.
        var done = board.SetStatus(id1, TaskStatus.Done, clock).Value;
        Assert.Equal([id1, id3, id2], Pile(done));
        Assert.Equal([id1, id3, id2], Group(done.SetStatus(id1, TaskStatus.InProgress, clock).Value, Quadrant.IV));
    }

    // Undo on a line just closed puts it back where it was, not on top: the line never left its place on screen.
    [Fact]
    public void Undoing_a_close_keeps_the_place()
    {
        var board = Notes(id1, id2, id3); // IV: 3 2 1
        board = board.SetStatus(id2, TaskStatus.Done, clock).Value;
        board = board.Close(id2, clock).Value.Close(id1, clock).Value;
        Later();

        board = board.UndoClose(id2, TaskStatus.Done, Maybe<string>.None, clock).Value.UndoClose(id1, TaskStatus.Backlog, Maybe<string>.None, clock).Value;

        // id2 is Done again, so pinned on top; its rank is untouched, so resumed it is back between id3 and id1.
        Assert.Equal([id2, id3, id1], Pile(board));
        Assert.Equal([id3, id2, id1], Group(board.SetStatus(id2, TaskStatus.InProgress, clock).Value, Quadrant.IV));
        Assert.Equal(TaskStatus.Done, Get(board, id2).Status);
        Assert.Equal(TaskStatus.Backlog, Get(board, id1).Status);
        Assert.True(board.UndoClose(id3, TaskStatus.Backlog, Maybe<string>.None, clock).IsFailure);
    }

    // Ruling 31: Undo means exactly undo. The status the task had before the close comes back as it was, whatever it
    // was, its question with it, and Raised is not touched (the pinned block's order stays as it was).
    [Fact]
    public void Undo_brings_back_a_task_waiting_on_you_with_its_question_and_its_place_in_the_block()
    {
        var board = TaskBoard.Empty.Create("Tasks redesign", Free, Session("s1"), clock, id1).Value.Board;
        Later();
        board = board.Ask(id1, "Merge?", clock).Value;
        var raised = Get(board, id1).Raised;
        Later();
        board = board.Close(id1, clock, stoppingClaude: true).Value;
        Later();

        board = board.UndoClose(id1, TaskStatus.WaitingOnYou, Maybe.From("Merge?"), clock).Value;

        Assert.Equal(TaskStatus.WaitingOnYou, Get(board, id1).Status);
        Assert.Equal("Merge?", Get(board, id1).Question.Value);
        Assert.Equal(raised, Get(board, id1).Raised);
        Assert.Equal([id1], board.Pile().Select(t => t.Id));
    }

    [Fact]
    public void Undo_brings_back_in_progress_and_a_status_set_by_hand_on_a_note()
    {
        var board = TaskBoard.Empty.Create("work", Free, Session("s1"), clock, id1).Value.Board;
        board = board.AddNote("a note set to in progress by hand", Free, clock, id2).Value.Board;
        board = board.SetStatus(id2, TaskStatus.InProgress, clock).Value;
        board = board.SessionEnded("s1", clock);
        board = board.Close(id1, clock).Value.Close(id2, clock).Value;
        Later();

        board = board.UndoClose(id1, TaskStatus.InProgress, Maybe<string>.None, clock).Value
            .UndoClose(id2, TaskStatus.InProgress, Maybe<string>.None, clock).Value;

        // Both were in progress, and that is what comes back, though neither has a live session now.
        Assert.Equal(TaskStatus.InProgress, Get(board, id1).Status);
        Assert.Equal(TaskStatus.InProgress, Get(board, id2).Status);
    }

    [Fact]
    public void Undo_brings_back_done_without_a_new_raised()
    {
        var board = Notes(id1).SetStatus(id1, TaskStatus.Done, clock).Value;
        var raised = Get(board, id1).Raised;
        Later();
        board = board.Close(id1, clock).Value;
        Later();

        board = board.UndoClose(id1, TaskStatus.Done, Maybe<string>.None, clock).Value;

        Assert.Equal(TaskStatus.Done, Get(board, id1).Status);
        Assert.Equal(raised, Get(board, id1).Raised);
    }

    [Fact]
    public void Group_leaves_out_waiting_and_closed_tasks()
    {
        var board = Notes(id1, id2, id3);
        board = board.SetStatus(id1, TaskStatus.WaitingOnYou, clock).Value.Close(id2, clock).Value;

        Assert.Equal([id3], Group(board, Quadrant.IV));
    }
}
