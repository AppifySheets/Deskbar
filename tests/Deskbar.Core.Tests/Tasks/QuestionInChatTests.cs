using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// A question is in the chat too (task 968c2216, Petre: "when asking a question, let that also be present in the chat").
// TaskBoard.Ask writes the question into the thread as a comment marked Asks when the caller names who asked; the same
// question asked again adds nothing; answering it settles it with the rest; JsonTaskStore keeps the mark. The tasks
// panel's half (the open line leaving the question to the chat) is in Deskbar.Windows.Tests' TasksBoardTests.
public sealed class QuestionInChatTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    const string Question = "Merge the pull request?";

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-question-chat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    // A Claude task session s1 works on, nothing asked yet.
    static (TaskBoard Board, Guid Id) Working()
    {
        var made = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None,
            new TaskSession("s1", @"C:\repos\TaskSpaces", T0, Maybe<DateTimeOffset>.None), T0, Guid.NewGuid()).Value;
        return (made.Board, made.Task.Id);
    }

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    [Fact]
    public void An_ask_by_a_session_writes_the_question_into_the_thread_as_that_sessions()
    {
        var (board, id) = Working();
        var commentId = Guid.NewGuid();

        var task = Get(board.Ask(id, "  " + Question + "\n", At(10), ("s1", commentId)).Value, id);

        var comment = Assert.Single(task.Comments);
        Assert.Equal(commentId, comment.Id);
        Assert.Equal(Question, comment.Markdown);
        Assert.Equal("s1", comment.SessionId.Value);
        Assert.Equal(At(10), comment.At);
        Assert.True(comment.Asks);
        Assert.True(comment.BySession);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
    }

    [Fact]
    public void An_ask_naming_no_one_writes_nothing_into_the_thread()
    {
        var (board, id) = Working();

        Assert.Empty(Get(board.Ask(id, Question, At(10)).Value, id).Comments);
    }

    [Fact]
    public void The_same_question_asked_again_adds_no_second_comment_but_a_new_one_does()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), ("s1", Guid.NewGuid())).Value;

        board = board.Ask(id, Question, At(12), ("s1", Guid.NewGuid())).Value;
        Assert.Single(Get(board, id).Comments);

        board = board.Ask(id, "Merge it now?", At(14), ("s1", Guid.NewGuid())).Value;
        Assert.Equal([Question, "Merge it now?"], Get(board, id).Comments.Select(c => c.Markdown));
    }

    [Fact]
    public void Asking_again_the_question_Deskbar_set_aside_adds_no_second_comment()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), ("s1", Guid.NewGuid())).Value;
        board = board.SetQuestionAside(id, Question, "note", At(20), Guid.NewGuid());

        var task = Get(board.Ask(id, Question, At(30), ("s1", Guid.NewGuid())).Value, id);

        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Single(task.Comments, c => c.Asks);
    }

    [Fact]
    public void Answering_settles_the_question_comment_even_when_Deskbar_asked_it()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), (TaskComment.DeskbarAuthor, Guid.NewGuid())).Value;

        var task = Get(board.Answered("s1", At(20)), id);

        Assert.Equal(At(20), Assert.Single(task.Comments).Settled.Value);
    }

    [Fact]
    public void A_question_settled_by_a_merge_settles_its_comment()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), (TaskComment.DeskbarAuthor, Guid.NewGuid())).Value;

        var task = Get(board.SettleQuestion(id, Question, "merged", At(20), Guid.NewGuid()), id);

        Assert.True(task.Comments.Single(c => c.Asks).Settled.HasValue);
    }

    // Review of the change: a re-ask moves WaitingSince past the question's only comment, which must still settle.
    [Fact]
    public void A_question_asked_twice_and_then_answered_or_merged_settles_its_comment()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), ("s1", Guid.NewGuid())).Value;
        board = board.Ask(id, Question, At(12), ("s1", Guid.NewGuid())).Value;

        Assert.True(Get(board.Answered("s1", At(20)), id).Comments.Single(c => c.Asks).Settled.HasValue);
        Assert.True(Get(board.SettleQuestion(id, Question, "merged", At(20), Guid.NewGuid()), id).Comments.Single(c => c.Asks).Settled.HasValue);
    }

    // Review of the change: a question cleared without being answered (here a status set by hand) and later asked again
    // word for word is a new message at the bottom, not the old bubble further up, which folds away.
    [Fact]
    public void A_cleared_question_asked_again_word_for_word_is_a_new_message()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), ("s1", Guid.NewGuid())).Value;
        board = board.SetStatus(id, TaskStatus.InProgress, At(11)).Value;
        board = board.Comment(id, "s1", "Tried again", At(12), Guid.NewGuid()).Value;

        var task = Get(board.Ask(id, Question, At(20), (TaskComment.DeskbarAuthor, Guid.NewGuid())).Value, id);

        Assert.True(task.Comments[^1].Asks);
        Assert.Equal(At(20), task.Comments[^1].At);
        Assert.Equal(At(20), task.Comments.First(c => c.Asks).Settled.Value);
    }

    [Fact]
    public void An_answered_question_asked_again_is_written_again()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), ("s1", Guid.NewGuid())).Value;
        board = board.Answered("s1", At(20));

        var task = Get(board.Ask(id, Question, At(30), ("s1", Guid.NewGuid())).Value, id);

        Assert.Equal(2, task.Comments.Count(c => c.Asks));
        Assert.True(task.Comments[^1].Settled.HasNoValue);
    }

    [Fact]
    public void The_store_keeps_the_question_mark_and_writes_it_only_on_questions()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), ("s1", Guid.NewGuid())).Value;
        board = board.Comment(id, Maybe<string>.None, "Merged it", At(11), Guid.NewGuid()).Value;
        var store = new JsonTaskStore(dir);
        Assert.True(store.Save(board).IsSuccess);

        var loaded = Get(store.Load().Value, id);

        Assert.Equal([true, false], loaded.Comments.Select(c => c.Asks));
        // One "asks" in the whole file: a plain comment is written as it was before the mark existed.
        var text = File.ReadAllText(Path.Combine(dir, "tasks.json"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"asks\""));
    }

    [Fact]
    public void A_mark_of_the_wrong_type_reads_as_no_mark_and_the_file_still_loads()
    {
        var (board, id) = Working();
        board = board.Ask(id, Question, At(10), ("s1", Guid.NewGuid())).Value;
        var store = new JsonTaskStore(dir);
        Assert.True(store.Save(board).IsSuccess);
        var path = Path.Combine(dir, "tasks.json");
        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), "\"asks\":\\s*true", "\"asks\": \"yes\""));

        var loaded = store.Load();

        Assert.True(loaded.IsSuccess);
        Assert.False(Get(loaded.Value, id).Comments.Single().Asks);
    }
}
