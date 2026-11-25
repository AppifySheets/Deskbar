using System.Collections.Immutable;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// A waiting task stops waiting once Claude works on it again after Petre's reply (task 8bd18246). RepliedTasks holds the
// rule, TaskBoard.SetQuestionAside / Answered / HandBack the changes, JsonTaskStore keeps the set-aside question and
// TaskService.Prompted + Stalled drive it. The Stop hook's side is in SetAsideStopHookTests.
public sealed class QuestionSetAsideTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    const string Question = "Review and merge the pull request?";

    static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    static TaskSession Session(string id, bool ended = false) =>
        new(id, @"C:\repos\TaskSpaces", T0, ended ? At(1) : Maybe<DateTimeOffset>.None);

    static readonly ImmutableDictionary<string, DateTimeOffset> NoPrompts = ImmutableDictionary<string, DateTimeOffset>.Empty;
    static readonly IReadOnlySet<string> NobodyWorks = new HashSet<string>();
    static readonly IReadOnlySet<string> S1Works = new HashSet<string> { "s1" };

    // A Claude task whose session s1 asked Question at minute 10.
    static (TaskBoard Board, Guid Id) Waiting(string session = "s1", bool ended = false)
    {
        var made = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session(session, ended), T0, Guid.NewGuid()).Value;
        return (made.Board.Ask(made.Task.Id, Question, At(10)).Value, made.Task.Id);
    }

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    // A board whose task has had Question set aside at minute 20.
    static (TaskBoard Board, Guid Id) Aside()
    {
        var (board, id) = Waiting();
        return (board.SetQuestionAside(id, Question, "note", At(20), Guid.NewGuid()), id);
    }

    // --- RepliedTasks.SetAside -------------------------------------------------------------------------------------

    [Fact]
    public void A_user_comment_after_the_ask_and_a_working_session_sets_the_question_aside()
    {
        var (board, id) = Waiting();
        board = board.Comment(id, Maybe<string>.None, "remove the footnote", At(12), Guid.NewGuid()).Value;

        var found = Assert.Single(RepliedTasks.SetAside(board, S1Works, NoPrompts));

        Assert.Equal((id, Question), found);
    }

    [Fact]
    public void Working_without_a_reply_sets_nothing_aside()
    {
        var (board, _) = Waiting();

        Assert.Empty(RepliedTasks.SetAside(board, S1Works, NoPrompts));
    }

    [Fact]
    public void A_reply_without_a_working_session_sets_nothing_aside()
    {
        var (board, id) = Waiting();
        board = board.Comment(id, Maybe<string>.None, "remove the footnote", At(12), Guid.NewGuid()).Value;

        Assert.Empty(RepliedTasks.SetAside(board, NobodyWorks, NoPrompts));
        // Another session working is not this task's session.
        Assert.Empty(RepliedTasks.SetAside(board, new HashSet<string> { "s2" }, NoPrompts));
    }

    [Fact]
    public void A_prompt_after_the_ask_counts_as_a_reply()
    {
        var (board, id) = Waiting();

        Assert.Equal([(id, Question)], RepliedTasks.SetAside(board, S1Works, NoPrompts.SetItem("s1", At(11))));
    }

    [Fact]
    public void A_prompt_or_comment_before_the_ask_does_not_count()
    {
        var made = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session("s1"), T0, Guid.NewGuid()).Value;
        var commented = made.Board.Comment(made.Task.Id, Maybe<string>.None, "early thought", At(5), Guid.NewGuid()).Value;
        var board = commented.Ask(made.Task.Id, Question, At(10)).Value;

        Assert.Empty(RepliedTasks.SetAside(board, S1Works, NoPrompts.SetItem("s1", At(5))));
        Assert.Empty(RepliedTasks.SetAside(board, S1Works, NoPrompts));
    }

    [Fact]
    public void A_sessions_own_comment_or_a_Deskbar_comment_is_not_a_reply()
    {
        var (board, id) = Waiting();
        board = board.Comment(id, "s1", "details of the ask", At(11), Guid.NewGuid()).Value;
        board = board.Comment(id, TaskComment.DeskbarAuthor, "Deskbar settled something", At(12), Guid.NewGuid()).Value;

        Assert.Empty(RepliedTasks.SetAside(board, S1Works, NoPrompts));
    }

    [Fact]
    public void An_ended_session_does_not_count_as_working()
    {
        var (board, id) = Waiting(ended: true);
        board = board.Comment(id, Maybe<string>.None, "remove the footnote", At(12), Guid.NewGuid()).Value;

        Assert.Empty(RepliedTasks.SetAside(board, S1Works, NoPrompts.SetItem("s1", At(12))));
    }

    // --- TaskBoard.SetQuestionAside ---------------------------------------------------------------------------------

    [Fact]
    public void Setting_aside_moves_the_task_to_Claude_keeps_the_question_and_writes_why()
    {
        var (board, id) = Waiting();
        board = board.Comment(id, "s1", "details of the ask", At(11), Guid.NewGuid()).Value;
        var noteId = Guid.NewGuid();

        var task = Get(board.SetQuestionAside(id, Question, "Deskbar set it aside", At(20), noteId), id);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.Assignee.IsClaude);
        Assert.True(task.Question.HasNoValue);
        Assert.Equal(Question, task.QuestionSetAside.Value);
        Assert.Equal(At(20), task.UpdatedAt);
        var note = task.Comments.Last();
        Assert.Equal(noteId, note.Id);
        Assert.True(note.ByDeskbar);
        Assert.Equal("Deskbar set it aside", note.Markdown);
        // Nothing is settled: the question may still hold, so the session's comment stays open.
        Assert.True(task.Comments[0].Settled.HasNoValue);
    }

    [Fact]
    public void Setting_aside_a_different_question_or_a_task_not_waiting_changes_nothing()
    {
        var (board, id) = Waiting();

        Assert.Same(board, board.SetQuestionAside(id, "A newer question?", "note", At(20), Guid.NewGuid()));

        var answered = board.Answered("s1", At(15));
        Assert.Same(answered, answered.SetQuestionAside(id, Question, "note", At(20), Guid.NewGuid()));
        Assert.Same(answered, answered.SetQuestionAside(Guid.NewGuid(), Question, "note", At(20), Guid.NewGuid()));
    }

    // --- TaskBoard.Answered, Ask, status ----------------------------------------------------------------------------

    [Fact]
    public void Answered_settles_a_set_aside_question_and_the_sessions_comments_since_the_ask()
    {
        var (board, id) = Waiting();
        board = board.Comment(id, "s1", "details of the ask", At(11), Guid.NewGuid()).Value;
        board = board.SetQuestionAside(id, Question, "note", At(20), Guid.NewGuid());

        var task = Get(board.Answered("s1", At(30)), id);

        Assert.True(task.QuestionSetAside.HasNoValue);
        Assert.True(task.Question.HasNoValue);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.Assignee.IsClaude);
        Assert.Equal(At(30), task.Comments[0].Settled);
    }

    [Fact]
    public void Answered_on_an_In_progress_task_without_a_set_aside_question_changes_nothing()
    {
        var made = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session("s1"), T0, Guid.NewGuid()).Value;

        Assert.Same(made.Board, made.Board.Answered("s1", At(5)));
    }

    [Fact]
    public void Asking_again_or_changing_the_status_drops_the_set_aside_question()
    {
        var (board, id) = Aside();
        Assert.True(Get(board, id).QuestionSetAside.HasValue);

        var asked = Get(board.Ask(id, "Which option?", At(30)).Value, id);
        Assert.True(asked.QuestionSetAside.HasNoValue);
        Assert.Equal("Which option?", asked.Question.Value);

        var done = Get(board.SetStatus(id, TaskStatus.Done, At(30)).Value, id);
        Assert.True(done.QuestionSetAside.HasNoValue);
    }

    // --- TaskBoard.HandBack -----------------------------------------------------------------------------------------

    [Fact]
    public void Handing_back_a_set_aside_task_makes_it_wait_again_on_its_question()
    {
        var (board, id) = Aside();

        var task = Get(board.HandBack(id, At(40)), id);

        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal(Question, task.Question.Value);
        Assert.True(task.QuestionSetAside.HasNoValue);
        Assert.Equal(Assignee.Me, task.Assignee);
        // Restored as it stood: the ask's WaitingSince and place in the block are kept, Raised moves (RepliedTasks counts after it).
        Assert.Equal(At(10), task.WaitingSince.Value);
        Assert.Equal(Get(board, id).Pin, task.Pin);
        Assert.Equal(At(40), task.Raised);
        Assert.True(task.HandedBack.HasNoValue);
    }

    [Fact]
    public void Handing_back_without_a_set_aside_question_is_the_old_hand_back()
    {
        var made = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session("s1"), T0, Guid.NewGuid()).Value;

        var task = Get(made.Board.HandBack(made.Task.Id, At(40)), made.Task.Id);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(Assignee.Me, task.Assignee);
        Assert.Equal(At(40), task.HandedBack.Value);
    }

    // --- Restored: Ask with the same text, RestoreQuestion ---------------------------------------------------------

    // Two waiting tasks, so that "a new ask goes on top" differs from "keeps its place": A (set aside at minute 20) was
    // asked first, B asked at minute 15 and sits above it.
    static (TaskBoard Board, Guid A) AsideBelowAnother()
    {
        var a = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session("s1"), T0, Guid.NewGuid()).Value;
        var asked = a.Board.Ask(a.Task.Id, Question, At(10)).Value;
        var b = asked.Create("Other", Maybe<Guid>.None, Session("s2"), T0, Guid.NewGuid()).Value;
        var both = b.Board.Ask(b.Task.Id, "Other question?", At(15)).Value;
        return (both.SetQuestionAside(a.Task.Id, Question, "note", At(20), Guid.NewGuid()), a.Task.Id);
    }

    [Fact]
    public void Asking_the_same_question_again_restores_it_in_place()
    {
        var (board, id) = AsideBelowAnother();
        var before = Get(board, id);

        var task = Get(board.Ask(id, "  " + Question + " ", At(30)).Value, id);

        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal(Question, task.Question.Value);
        Assert.True(task.QuestionSetAside.HasNoValue);
        Assert.Equal(Assignee.Me, task.Assignee);
        Assert.Equal(At(30), task.Raised);
        Assert.Equal(At(10), task.WaitingSince.Value);
        Assert.Equal(before.Pin, task.Pin);
    }

    [Fact]
    public void Asking_a_different_question_is_a_normal_ask()
    {
        var (board, id) = AsideBelowAnother();
        var before = Get(board, id);

        var task = Get(board.Ask(id, "Which option?", At(30)).Value, id);

        Assert.Equal("Which option?", task.Question.Value);
        Assert.True(task.QuestionSetAside.HasNoValue);
        Assert.Equal(At(30), task.WaitingSince.Value);
        Assert.NotEqual(before.Pin, task.Pin);
    }

    [Fact]
    public void RestoreQuestion_restores_a_set_aside_task_and_leaves_others_alone()
    {
        var (board, id) = Aside();

        var task = Get(board.RestoreQuestion(id, At(30)), id);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal(Question, task.Question.Value);
        Assert.True(task.QuestionSetAside.HasNoValue);
        Assert.Equal(At(10), task.WaitingSince.Value);

        var made = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session("s1"), T0, Guid.NewGuid()).Value;
        Assert.Same(made.Board, made.Board.RestoreQuestion(made.Task.Id, At(30)));
    }

    // --- Assign ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Handing_the_task_to_someone_else_drops_the_set_aside_question_but_Claude_keeps_it()
    {
        var (board, id) = Aside();

        Assert.True(Get(board.Assign(id, Assignee.Me, At(30)).Value, id).QuestionSetAside.HasNoValue);
        Assert.Equal(Question, Get(board.Assign(id, Assignee.Claude, At(30)).Value, id).QuestionSetAside.Value);
    }

    // --- RepliedTasks: after a restore, snoozed, Restore ------------------------------------------------------------

    [Fact]
    public void After_a_restore_the_old_reply_no_longer_counts_but_a_newer_one_does()
    {
        var (board, id) = Waiting();
        board = board.Comment(id, Maybe<string>.None, "remove the footnote", At(12), Guid.NewGuid()).Value;
        board = board.SetQuestionAside(id, Question, "note", At(20), Guid.NewGuid()).RestoreQuestion(id, At(30));

        Assert.Empty(RepliedTasks.SetAside(board, S1Works, NoPrompts.SetItem("s1", At(12))));

        Assert.Equal([(id, Question)], RepliedTasks.SetAside(board, S1Works, NoPrompts.SetItem("s1", At(31))));
    }

    [Fact]
    public void A_snoozed_waiting_task_is_not_set_aside()
    {
        var (board, id) = Waiting();
        board = board.Comment(id, Maybe<string>.None, "remove the footnote", At(12), Guid.NewGuid()).Value;
        Assert.Single(RepliedTasks.SetAside(board, S1Works, NoPrompts));

        var snoozed = board.Snooze(id, At(600), At(13)).Value;

        Assert.Empty(RepliedTasks.SetAside(snoozed, S1Works, NoPrompts));
    }

    [Fact]
    public void Restore_names_set_aside_tasks_whose_live_sessions_are_not_busy()
    {
        var (board, id) = Aside();

        Assert.Equal([id], RepliedTasks.Restore(board, NobodyWorks));
        Assert.Equal([id], RepliedTasks.Restore(board, new HashSet<string> { "s2" }));
        Assert.Empty(RepliedTasks.Restore(board, S1Works));

        // A waiting task, and an In progress one with nothing set aside, are not named.
        var (waiting, _) = Waiting();
        Assert.Empty(RepliedTasks.Restore(waiting, NobodyWorks));
        var plain = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session("s1"), T0, Guid.NewGuid()).Value.Board;
        Assert.Empty(RepliedTasks.Restore(plain, NobodyWorks));
    }

    // --- JsonTaskStore ----------------------------------------------------------------------------------------------

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-setaside-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void The_set_aside_question_round_trips_and_a_file_without_it_reads_none()
    {
        var store = new JsonTaskStore(dir);
        var (board, id) = Aside();

        Assert.True(store.Save(board).IsSuccess);
        Assert.Equal(Question, Get(store.Load().Value, id).QuestionSetAside.Value);

        // Handed back: the question is the waiting one again and no set-aside value is read.
        Assert.True(store.Save(board.HandBack(id, At(40))).IsSuccess);
        Assert.True(Get(store.Load().Value, id).QuestionSetAside.HasNoValue);

        // A file from before the field existed: strip the property from the text and read it again.
        Assert.True(store.Save(board).IsSuccess);
        var path = Path.Combine(dir, "tasks.json");
        var stripped = Regex.Replace(File.ReadAllText(path), "\"questionSetAside\"\\s*:\\s*\"[^\"]*\"\\s*,?", "", RegexOptions.IgnoreCase);
        Assert.DoesNotContain("questionSetAside", stripped, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, stripped);
        Assert.True(Get(store.Load().Value, id).QuestionSetAside.HasNoValue);
    }

    // --- TaskService ------------------------------------------------------------------------------------------------

    sealed class MemoryStore : ITaskStore
    {
        public TaskBoard Stored { get; private set; } = TaskBoard.Empty;
        public int Saves { get; private set; }

        public Result<TaskBoard> Load() => Stored;

        public Result Save(TaskBoard board)
        {
            Saves++;
            Stored = board;
            return Result.Success();
        }
    }

    // A service with a moving clock (`move` sets it to a minute) and a task whose session s1 asked at minute 10.
    static (TaskService Service, MemoryStore Store, Action<int> Move, Guid Id, List<string> Trace) Asked()
    {
        var clock = T0;
        var store = new MemoryStore();
        var trace = new List<string>();
        var service = new TaskService(store, () => clock, trace: trace.Add);
        var task = service.Create("Tasks panel", Maybe<Guid>.None, Session("s1")).Value;
        clock = At(10);
        service.Ask(task.Id, Question);
        return (service, store, minutes => clock = At(minutes), task.Id, trace);
    }

    [Fact]
    public void A_prompt_then_a_working_session_sets_the_question_aside_and_saves_it()
    {
        var (service, store, move, id, trace) = Asked();
        move(12);
        service.Prompted("s1");
        move(13);
        var saves = store.Saves;

        service.Stalled(S1Works, S1Works);

        var task = Get(service.Board, id);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(Question, task.QuestionSetAside.Value);
        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(Question, Get(store.Stored, id).QuestionSetAside.Value);
        Assert.Contains(trace, line => line.Contains("aside"));
    }

    [Fact]
    public void Stalled_restores_a_set_aside_question_when_its_session_is_not_busy_and_leaves_it_while_busy()
    {
        var (service, store, move, id, trace) = Asked();
        move(12);
        service.Prompted("s1");
        move(13);
        service.Stalled(S1Works, S1Works);
        Assert.Equal(TaskStatus.InProgress, Get(service.Board, id).Status);

        move(14);
        service.Stalled(S1Works, S1Works);
        Assert.Equal(TaskStatus.InProgress, Get(service.Board, id).Status);

        move(15);
        var saves = store.Saves;
        service.Stalled(NobodyWorks, NobodyWorks);

        var task = Get(service.Board, id);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal(Question, task.Question.Value);
        Assert.True(task.QuestionSetAside.HasNoValue);
        Assert.True(store.Saves > saves);
        Assert.Contains(trace, line => line.Contains("restored"));

        // The reply that set it aside does not set it aside a second time.
        move(16);
        service.Stalled(S1Works, S1Works);
        Assert.Equal(TaskStatus.WaitingOnYou, Get(service.Board, id).Status);
    }

    [Fact]
    public void Without_a_prompt_a_working_session_changes_nothing()
    {
        var (service, store, move, id, _) = Asked();
        move(13);
        var saves = store.Saves;

        service.Stalled(S1Works, S1Works);

        Assert.Equal(TaskStatus.WaitingOnYou, Get(service.Board, id).Status);
        Assert.Equal(saves, store.Saves);
    }
}
