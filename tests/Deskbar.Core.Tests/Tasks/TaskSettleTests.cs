using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// RULING 62 (Petre: "if there's a question that has an answer, settle its comment, maybe remove it"): a comment may be
// settled, which keeps it but folds it away. `task answered` settles the session's own comments written since its
// question was asked; a session settles one of its own by id; Petre settles any.
public sealed class TaskSettleTests : IDisposable
{
    DateTimeOffset clock = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid id1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid before = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    static readonly Guid detail = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    static readonly Guid followUp = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    static readonly Guid his = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    static readonly Guid otherSession = Guid.Parse("cccccccc-0000-0000-0000-000000000005");

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-settle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    DateTimeOffset Later(int minutes) => clock = clock.AddMinutes(minutes);

    // A thread: one comment before the ask, the ask, then its detail, a follow-up, one of his and another session's.
    TaskBoard Asked()
    {
        var board = TaskBoard.Empty.Create("Tasks redesign", Maybe<Guid>.None, new TaskSession("s1", "f", clock, Maybe<DateTimeOffset>.None), clock, id1).Value.Board;
        board = board.Comment(id1, "s1", "older finding", Later(1), before).Value;
        board = board.Ask(id1, "Merge?", Later(1)).Value;
        board = board.Comment(id1, "s1", "detail of the question", Later(1), detail).Value;
        board = board.Comment(id1, "s1", "follow-up while open", Later(1), followUp).Value;
        board = board.Comment(id1, Maybe<string>.None, "my reply", Later(1), his).Value;
        return board.Comment(id1, "s2", "another session", Later(1), otherSession).Value;
    }

    static TaskComment C(TaskBoard board, Guid id) => board.Tasks.Single().Comments.Single(c => c.Id == id);

    [Fact]
    public void Answered_settles_exactly_the_sessions_comments_since_the_ask()
    {
        var answered = Asked().Answered("s1", Later(1));

        Assert.True(C(answered, before).Settled.HasNoValue);
        Assert.True(C(answered, detail).Settled.HasValue);
        Assert.True(C(answered, followUp).Settled.HasValue);
        Assert.Equal(clock, C(answered, detail).Settled.Value);
        Assert.True(C(answered, his).Settled.HasNoValue);
        Assert.True(C(answered, otherSession).Settled.HasNoValue);
        // Settled, not deleted: the history stays.
        Assert.Equal(5, answered.Tasks.Single().Comments.Count);
    }

    // A Done task going back to in progress was no question, so no comment is settled by it.
    [Fact]
    public void A_task_that_was_done_settles_nothing_when_it_resumes()
    {
        var board = Asked().Answered("s1", Later(1)).MarkDone(id1, Later(1)).Value;

        Assert.All(board.Tasks.Single().Comments.Where(c => c.Id is var i && i != detail && i != followUp), c => Assert.True(c.Settled.HasNoValue));
        var resumed = board.Answered("s1", Later(1));
        Assert.True(C(resumed, before).Settled.HasNoValue);
    }

    [Fact]
    public void A_session_settles_only_its_own_comment_and_Petre_settles_any()
    {
        var board = Asked();

        Assert.True(board.SettleComment(otherSession, true, "s1", clock).IsFailure);
        Assert.Equal("A session can settle only its own comments", board.SettleComment(his, true, "s1", clock).Error);
        Assert.True(C(board.SettleComment(before, true, "s1", Later(1)).Value, before).Settled.HasValue);

        var byPetre = board.SettleComment(otherSession, true, null, Later(1)).Value;
        Assert.True(C(byPetre, otherSession).Settled.HasValue);
        Assert.True(C(byPetre.SettleComment(otherSession, false, null, Later(1)).Value, otherSession).Settled.HasNoValue);
        Assert.Same(board, board.SettleComment(before, false, null, clock).Value); // already unsettled
        Assert.True(board.SettleComment(Guid.NewGuid(), true, null, clock).IsFailure);
    }

    [Fact]
    public void Settled_survives_a_save_and_a_load()
    {
        var board = Asked().Answered("s1", Later(1));
        Assert.True(new JsonTaskStore(dir).Save(board).IsSuccess);

        var loaded = new JsonTaskStore(dir).Load().Value;

        Assert.Equal(C(board, detail).Settled, C(loaded, detail).Settled);
        Assert.True(C(loaded, before).Settled.HasNoValue);
    }

    // Review: the question's time is when the task LAST BECAME Waiting, not Raised (which a status set by hand never moves).
    [Fact]
    public void A_status_set_to_waiting_by_hand_settles_only_comments_written_after_it()
    {
        var board = TaskBoard.Empty.Create("Tasks redesign", Maybe<Guid>.None, new TaskSession("s1", "f", clock, Maybe<DateTimeOffset>.None), clock, id1).Value.Board;
        board = board.Comment(id1, "s1", "written before", Later(1), before).Value;
        board = board.SetStatus(id1, TaskStatus.WaitingOnYou, Later(1)).Value;
        board = board.Comment(id1, "s1", "written while waiting", Later(1), detail).Value;

        var answered = board.Answered("s1", Later(1));

        Assert.True(C(answered, before).Settled.HasNoValue);
        Assert.True(C(answered, detail).Settled.HasValue);
    }

    [Fact]
    public void The_time_a_task_began_waiting_survives_a_save_and_load()
    {
        var board = Asked();
        Assert.True(new JsonTaskStore(dir).Save(board).IsSuccess);
        var loaded = new JsonTaskStore(dir).Load().Value;

        var answered = loaded.Answered("s1", Later(1));

        Assert.True(C(answered, before).Settled.HasNoValue);
        Assert.True(C(answered, detail).Settled.HasValue);
    }
}
