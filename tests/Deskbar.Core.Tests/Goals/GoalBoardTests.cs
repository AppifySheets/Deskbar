using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;

namespace Deskbar.Core.Tests.Goals;

// The goal rules, pure and clock-free: every method takes "now", and the test moves a captured clock. Design:
// docs/superpowers/specs/2026-11-22-goals-planner-design.md ("The goal", "Calls made in writing this up") and the plan's
// "Calls this plan makes" (rulings G4 and G5 in the goals ledger).
public sealed class GoalBoardTests
{
    DateTimeOffset clock = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid id1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    static readonly Guid id2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    static readonly Guid id3 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    static readonly Guid id4 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");

    static readonly GoalPeriod Week23 = GoalPeriod.WeekOf(new DateOnly(2026, 11, 23));
    static readonly GoalPeriod Week30 = GoalPeriod.WeekOf(new DateOnly(2026, 11, 30));
    static readonly GoalPeriod Week07 = GoalPeriod.WeekOf(new DateOnly(2026, 12, 7));
    static readonly GoalPeriod October = GoalPeriod.MonthOf(new DateOnly(2026, 10, 1));
    static readonly GoalPeriod November = GoalPeriod.MonthOf(new DateOnly(2026, 11, 1));
    static readonly GoalPeriod December = GoalPeriod.MonthOf(new DateOnly(2026, 12, 1));

    void Later(int minutes = 1) => clock = clock.AddMinutes(minutes);

    GoalBoard Add(GoalBoard board, Guid id, GoalPeriod period, string title = "Ship the goals planner") =>
        board.Add(title, period, clock, id).Value.Board;

    static Goal Get(GoalBoard board, Guid id) => board.Get(id).Value;

    [Fact]
    public void Adding_appends_to_its_period()
    {
        var (board, first) = GoalBoard.Empty.Add("  Ship the goals planner ", Week23, clock, id1).Value;
        board = Add(board, id2, Week23, "Read Covey");
        board = Add(board, id3, Week30, "Next week");

        Assert.Equal("Ship the goals planner", first.Title);
        Assert.Equal(GoalStatus.Open, first.Status);
        Assert.True(first.IsOpen);
        Assert.Equal(GoalKind.Week, first.Kind);
        Assert.True(first.ParentId.HasNoValue);
        Assert.Equal(clock, first.Created);
        Assert.Equal(clock, first.UpdatedAt);
        Assert.Equal([id1, id2], board.In(Week23).Select(g => g.Id));
        Assert.Equal([0, 1], board.In(Week23).Select(g => g.Order));
        Assert.Equal(0, Get(board, id3).Order);
    }

    [Fact]
    public void An_empty_title_is_refused() =>
        Assert.Equal("A goal needs a title", GoalBoard.Empty.Add("  ", Week23, clock, id1).Error);

    [Fact]
    public void Done_drop_and_reopen()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        Later();

        var done = board.MarkDone(id1, clock).Value;
        Assert.Equal(GoalStatus.Done, Get(done, id1).Status);
        Assert.False(Get(done, id1).IsOpen);
        Assert.Equal(clock, Get(done, id1).UpdatedAt);
        Later();

        var reopened = done.Reopen(id1, clock).Value;
        Assert.Equal(GoalStatus.Open, Get(reopened, id1).Status);
        Assert.Equal(clock, Get(reopened, id1).UpdatedAt);
        Later();

        var dropped = reopened.Drop(id1, clock).Value;
        Assert.Equal(GoalStatus.Dropped, Get(dropped, id1).Status);
        Assert.Equal(clock, Get(dropped, id1).UpdatedAt);
        Assert.Equal(GoalStatus.Done, Get(dropped.MarkDone(id1, clock).Value, id1).Status);

        Assert.Equal("The goal is open", board.Reopen(id1, clock).Error);
        Assert.Equal("The goal is already done", done.MarkDone(id1, clock).Error);
        Assert.Equal("The goal is already dropped", dropped.Drop(id1, clock).Error);
    }

    [Fact]
    public void Renaming_trims_and_refuses_an_empty_title()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        Later();

        var renamed = board.Rename(id1, " Ship it ", clock).Value;

        Assert.Equal("Ship it", Get(renamed, id1).Title);
        Assert.Equal(clock, Get(renamed, id1).UpdatedAt);
        Assert.Equal("A goal needs a title", board.Rename(id1, "", clock).Error);
    }

    [Fact]
    public void Unknown_ids_fail()
    {
        Assert.Equal($"No goal with id {id1}", GoalBoard.Empty.MarkDone(id1, clock).Error);
        Assert.True(GoalBoard.Empty.Rename(id1, "x", clock).IsFailure);
        Assert.True(GoalBoard.Empty.Carry(id1, Week30, clock).IsFailure);
        Assert.True(GoalBoard.Empty.Delete(id1, clock).IsFailure);
        Assert.True(GoalBoard.Empty.SetParent(id1, Maybe<Guid>.None, clock).IsFailure);
        Assert.Equal($"No goal with id {id2}", Add(GoalBoard.Empty, id1, Week23).SetParent(id1, id2, clock).Error);
    }

    // Review focus 1: the week of 30 November touches November and December, so a monthly goal of either can be its
    // parent; October's cannot.
    [Fact]
    public void A_weekly_goal_takes_a_monthly_parent_of_a_month_it_touches()
    {
        var board = Add(GoalBoard.Empty, id1, Week30, "Weekly");
        board = Add(board, id2, November, "November goal");
        board = Add(board, id3, December, "December goal");
        board = Add(board, id4, October, "October goal");
        Later();

        var inNovember = board.SetParent(id1, id2, clock).Value;
        Assert.Equal(id2, Get(inNovember, id1).ParentId.Value);
        Assert.Equal(clock, Get(inNovember, id1).UpdatedAt);
        Assert.Equal([id1], inNovember.ChildrenOf(id2).Select(g => g.Id));

        Assert.Equal(id3, Get(board.SetParent(id1, id3, clock).Value, id1).ParentId.Value);
        Assert.Equal("'October goal' is a goal of October 2026, a month this week does not touch",
            board.SetParent(id1, id4, clock).Error);

        // Back to none, and setting what it already is changes nothing.
        Assert.True(Get(inNovember.SetParent(id1, Maybe<Guid>.None, clock).Value, id1).ParentId.HasNoValue);
        Assert.Same(inNovember, inNovember.SetParent(id1, id2, clock).Value);
    }

    [Fact]
    public void Only_a_weekly_goal_has_a_parent_and_only_a_monthly_one_is_one()
    {
        var board = Add(GoalBoard.Empty, id1, Week23, "Weekly");
        board = Add(board, id2, November, "Monthly");
        board = Add(board, id3, Week23, "Another weekly");

        Assert.Equal("Only a weekly goal has a monthly parent", board.SetParent(id2, id2, clock).Error);
        Assert.Equal("'Another weekly' is not a monthly goal", board.SetParent(id1, id3, clock).Error);
    }

    [Fact]
    public void Carrying_keeps_the_id_and_moves_to_the_end_of_the_new_period()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        board = Add(board, id2, Week30);
        board = Add(board, id3, Week30);
        Later();

        var carried = board.Carry(id1, Week30, clock).Value;
        var goal = Get(carried, id1);

        Assert.Equal(Week30, goal.Period);
        Assert.Equal(2, goal.Order);
        Assert.Equal(GoalStatus.Open, goal.Status);
        Assert.Equal(clock, goal.UpdatedAt);
        Assert.Equal([id2, id3, id1], carried.In(Week30).Select(g => g.Id));
        Assert.Empty(carried.In(Week23));
        Assert.Same(carried, carried.Carry(id1, Week30, clock).Value);
    }

    [Fact]
    public void Carrying_a_weekly_goal_keeps_its_parent_only_when_the_new_week_touches_that_month()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        board = Add(board, id2, November, "November goal");
        board = board.SetParent(id1, id2, clock).Value;

        Assert.Equal(id2, Get(board.Carry(id1, Week30, clock).Value, id1).ParentId.Value);
        Assert.True(Get(board.Carry(id1, Week07, clock).Value, id1).ParentId.HasNoValue);
    }

    // G4: a carried monthly goal leaves its weekly goals where they were; their weeks served it.
    [Fact]
    public void Carrying_a_monthly_goal_leaves_its_weekly_goals()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        board = Add(board, id2, November, "November goal");
        board = board.SetParent(id1, id2, clock).Value;

        var carried = board.Carry(id2, December, clock).Value;

        Assert.Equal(December, Get(carried, id2).Period);
        Assert.Equal(id2, Get(carried, id1).ParentId.Value);
        Assert.Equal(Week23, Get(carried, id1).Period);
    }

    // Review I1 (ruling G6): after the carry above, the November week still names the goal now in December. Picking
    // that same parent again is no change, so it is the same board, not a refusal for a month the week does not touch.
    [Fact]
    public void Picking_the_current_parent_again_is_no_change()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        board = Add(board, id2, November, "November goal");
        board = board.SetParent(id1, id2, clock).Value.Carry(id2, December, clock).Value;

        var again = board.SetParent(id1, id2, clock);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error : "");
        Assert.Same(board, again.Value);
    }

    // Review M4: a parent that no longer exists (deleted elsewhere, a file restored by hand) is let go on a carry, even
    // to a week that would touch its month.
    [Fact]
    public void Carrying_lets_go_of_a_vanished_parent()
    {
        var orphan = new Goal(id1, "Orphan", Week23, GoalStatus.Open, 0, id2, clock, clock);
        var board = new GoalBoard([orphan]);

        Assert.True(Get(board.Carry(id1, Week30, clock).Value, id1).ParentId.HasNoValue);
    }

    // Review M3: renaming a goal to the title it has is no change.
    [Fact]
    public void Renaming_to_the_same_title_is_the_same_board()
    {
        var board = Add(GoalBoard.Empty, id1, Week23, "Ship it");

        Assert.Same(board, board.Rename(id1, " Ship it ", clock).Value);
    }

    [Fact]
    public void Carrying_never_changes_the_kind()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        board = Add(board, id2, November);

        Assert.Equal("A weekly goal moves to another week", board.Carry(id1, December, clock).Error);
        Assert.Equal("A monthly goal moves to another month", board.Carry(id2, Week30, clock).Error);
    }

    [Fact]
    public void Deleting_a_monthly_goal_frees_its_weekly_children()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        board = Add(board, id2, Week30);
        board = Add(board, id3, November);
        board = Add(board, id4, Week23);
        board = board.SetParent(id1, id3, clock).Value.SetParent(id2, id3, clock).Value;
        var untouched = Get(board, id4);
        Later();

        var deleted = board.Delete(id3, clock).Value;

        Assert.True(deleted.Get(id3).HasNoValue);
        Assert.All(new[] { id1, id2 }.Select(id => Get(deleted, id)), g =>
        {
            Assert.True(g.ParentId.HasNoValue);
            Assert.Equal(clock, g.UpdatedAt);
        });
        Assert.Equal(untouched, Get(deleted, id4));
    }

    [Fact]
    public void Seen_records_the_periods_and_is_the_same_board_when_nothing_changes()
    {
        var board = Add(GoalBoard.Empty, id1, Week23);
        Assert.True(board.WeekSeen.HasNoValue);
        Assert.True(board.MonthSeen.HasNoValue);

        var seen = board.Seen(Week23, November);

        Assert.Equal(new DateOnly(2026, 11, 23), seen.WeekSeen.Value);
        Assert.Equal(new DateOnly(2026, 11, 1), seen.MonthSeen.Value);
        Assert.Same(seen, seen.Seen(Week23, November));
        Assert.Equal(seen.Goals, seen.Seen(Week30, December).Goals);
        Assert.Equal(new DateOnly(2026, 11, 30), seen.Seen(Week30, December).WeekSeen.Value);
    }

    // A goal change keeps what was seen: the review state rides along with the goals.
    [Fact]
    public void Changes_keep_the_seen_periods()
    {
        var board = Add(GoalBoard.Empty, id1, Week23).Seen(Week23, November);

        var changed = board.MarkDone(id1, clock).Value;

        Assert.Equal(board.WeekSeen, changed.WeekSeen);
        Assert.Equal(board.MonthSeen, changed.MonthSeen);
    }

    [Fact]
    public void In_orders_by_order_then_created_then_id()
    {
        var a = new Goal(id2, "a", Week23, GoalStatus.Open, 1, Maybe<Guid>.None, clock, clock);
        var b = new Goal(id1, "b", Week23, GoalStatus.Open, 1, Maybe<Guid>.None, clock, clock);
        var c = new Goal(id3, "c", Week23, GoalStatus.Done, 0, Maybe<Guid>.None, clock.AddDays(1), clock);
        var d = new Goal(id4, "d", Week23, GoalStatus.Open, 1, Maybe<Guid>.None, clock.AddMinutes(-1), clock);

        Assert.Equal([id3, id4, id1, id2], new GoalBoard([a, b, c, d]).In(Week23).Select(g => g.Id));
    }

    [Fact]
    public void Find_takes_a_prefix_of_four()
    {
        var board = Add(Add(GoalBoard.Empty, id1, Week23, "a"), id2, Week23, "b");

        Assert.Equal(id1, board.Find(id1.ToString()).Value.Id);
        Assert.Equal(id2, board.Find("BBBBBBBB-0000-0000-0000-000000000002").Value.Id);
        Assert.Equal("'bbbbbbbb' matches several goals", board.Find("bbbbbbbb").Error);
        Assert.Equal("No goal with id x", board.Find("x").Error);
        Assert.True(board.Find("bbb").IsFailure);
        Assert.Equal("No goal with id ffff", board.Find("ffff").Error);
    }
}
