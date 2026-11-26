using System.Collections.Immutable;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The answer options of a question (the inbox's answer buttons): a session's `task ask` can name the few answers it
// expects, Deskbar keeps them with the open question and drops them with it. AnswerOptions holds the limits and the text
// form, TaskBoard.Ask the storing, TaskBoard.OptionText the check behind a button, JsonTaskStore the file. The pipe form
// is in RemoteControlClaudeHookTests, the row view and the button's click in Deskbar.Windows.Tests' TasksBoardTests.
public sealed class AnswerOptionsTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    const string Question = "Merge the pull request?";
    static readonly string[] Two = ["Merge it", "Not yet"];

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-answer-options-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    static TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", T0, Maybe<DateTimeOffset>.None);

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    // A Claude task whose session s1 has joined and asked nothing yet.
    static (TaskBoard Board, Guid Id) Working()
    {
        var made = TaskBoard.Empty.Create("Tasks panel", Maybe<Guid>.None, Session("s1"), T0, Guid.NewGuid()).Value;
        return (made.Board, made.Task.Id);
    }

    // The same task waiting on Question with the two options.
    static (TaskBoard Board, Guid Id) Waiting(params string[] options)
    {
        var (board, id) = Working();
        return (board.Ask(id, Question, At(10), options: options.Length == 0 ? Two : options).Value, id);
    }

    // --- the text form -----------------------------------------------------------------------------------------------

    [Fact]
    public void Text_with_no_marker_is_the_question_and_has_no_options()
    {
        var split = AnswerOptions.Split("Merge it?\nThe CI is green.").Value;

        Assert.Equal("Merge it?\nThe CI is green.", split.Question);
        Assert.Empty(split.Options);
    }

    [Fact]
    public void A_marker_line_ends_the_question_and_each_line_after_it_is_an_option()
    {
        var split = AnswerOptions.Split("Merge it?\nThe CI is green.\n--options\nMerge it\nNot yet").Value;

        Assert.Equal("Merge it?\nThe CI is green.", split.Question);
        Assert.Equal(Two, split.Options);
    }

    [Fact]
    public void Windows_line_ends_blank_lines_and_padding_are_tolerated_among_the_options()
    {
        var split = AnswerOptions.Split("Merge it?\r\n  --options  \r\n\r\n  Merge it  \r\n\r\nNot yet\r\n").Value;

        Assert.Equal("Merge it?", split.Question);
        Assert.Equal(Two, split.Options);
    }

    [Fact]
    public void A_marker_inside_a_line_is_prose_and_splits_nothing()
    {
        var split = AnswerOptions.Split("Use --options on the command line?").Value;

        Assert.Equal("Use --options on the command line?", split.Question);
        Assert.Empty(split.Options);
    }

    [Fact]
    public void A_marker_with_no_option_after_it_is_refused_not_read_as_no_options()
    {
        var split = AnswerOptions.Split("Merge it?\n--options\n\n");

        Assert.True(split.IsFailure);
        Assert.Contains("no option", split.Error);
        Assert.Contains("at most 4", split.Error);
    }

    // --- the limits --------------------------------------------------------------------------------------------------

    [Fact]
    public void Four_options_of_forty_characters_are_the_limit_and_are_kept_trimmed()
    {
        var forty = new string('x', 40);

        var checkedList = AnswerOptions.Check(["  a ", "b", "c", forty]).Value;

        Assert.Equal(["a", "b", "c", forty], checkedList);
    }

    [Fact]
    public void A_fifth_option_refuses_the_whole_list_and_the_message_says_the_limit()
    {
        var refused = AnswerOptions.Check(["a", "b", "c", "d", "e"]);

        Assert.True(refused.IsFailure);
        Assert.Contains("5 options is too many", refused.Error);
        Assert.Contains("at most 4 options", refused.Error);
        Assert.Contains("40 characters", refused.Error);
    }

    [Fact]
    public void An_option_of_forty_one_characters_refuses_the_list()
    {
        var refused = AnswerOptions.Check(["fine", new string('x', 41)]);

        Assert.True(refused.IsFailure);
        Assert.Contains("41 characters", refused.Error);
        Assert.Contains("at most 40 characters", refused.Error);
    }

    [Fact]
    public void The_forty_characters_are_counted_after_trimming()
    {
        Assert.True(AnswerOptions.Check(["  " + new string('x', 40) + "   "]).IsSuccess);
    }

    [Theory]
    [InlineData("Merge\nit")]
    [InlineData("Merge\rit")]
    [InlineData("Merge\u2028it")]
    public void An_option_with_a_line_break_inside_refuses_the_list(string option)
    {
        var refused = AnswerOptions.Check(["fine", option]);

        Assert.True(refused.IsFailure);
        Assert.Contains("line break", refused.Error);
    }

    [Fact]
    public void An_empty_option_refuses_the_list()
    {
        Assert.True(AnswerOptions.Check(["fine", "   "]).IsFailure);
    }

    [Fact]
    public void Two_options_alike_in_any_case_refuse_the_list()
    {
        var refused = AnswerOptions.Check(["Merge it", "fine", " MERGE IT "]);

        Assert.True(refused.IsFailure);
        Assert.Contains("given twice", refused.Error);
    }

    [Fact]
    public void A_bad_option_in_the_text_refuses_the_whole_ask()
    {
        var split = AnswerOptions.Split("Merge it?\n--options\nMerge it\n" + new string('x', 41));

        Assert.True(split.IsFailure);
        Assert.Contains("nothing was asked", split.Error);
    }

    // --- the board stores them with the question ---------------------------------------------------------------------

    [Fact]
    public void An_ask_keeps_its_options_with_the_question()
    {
        var (board, id) = Waiting();

        var task = Get(board, id);

        Assert.Equal(Two, task.QuestionOptions);
        Assert.Equal(Question, task.Question.Value);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
    }

    [Fact]
    public void An_ask_without_options_leaves_none()
    {
        var (board, id) = Working();

        Assert.Empty(Get(board.Ask(id, Question, At(10)).Value, id).QuestionOptions);
    }

    [Fact]
    public void A_bad_list_refuses_the_ask_and_stores_nothing_not_even_the_question()
    {
        var (board, id) = Working();

        var refused = board.Ask(id, Question, At(10), options: ["a", "b", "c", "d", "e"]);

        Assert.True(refused.IsFailure);
        Assert.Contains("at most 4", refused.Error);
        Assert.Equal(TaskStatus.InProgress, Get(board, id).Status);
        Assert.True(Get(board, id).Question.HasNoValue);
        Assert.Empty(Get(board, id).Comments);
    }

    [Fact]
    public void A_bad_list_in_a_second_ask_leaves_the_first_ask_as_it_was()
    {
        var (board, id) = Waiting();

        Assert.True(board.Ask(id, "Another question?", At(12), options: ["same", "SAME"]).IsFailure);

        Assert.Equal(Question, Get(board, id).Question.Value);
        Assert.Equal(Two, Get(board, id).QuestionOptions);
    }

    [Fact]
    public void A_new_ask_brings_its_own_options()
    {
        var (board, id) = Waiting();

        board = board.Ask(id, "Delete the branch too?", At(12), options: ["Delete", "Keep"]).Value;

        Assert.Equal(["Delete", "Keep"], Get(board, id).QuestionOptions);
    }

    [Fact]
    public void A_new_ask_without_options_clears_the_old_ones_even_for_the_same_question()
    {
        var (board, id) = Waiting();

        board = board.Ask(id, Question, At(12)).Value;

        Assert.Equal(Question, Get(board, id).Question.Value);
        Assert.Empty(Get(board, id).QuestionOptions);
    }

    [Fact]
    public void The_question_in_the_thread_is_the_question_alone()
    {
        var (board, id) = Working();

        var task = Get(board.Ask(id, Question, At(10), ("s1", Guid.NewGuid()), Two).Value, id);

        Assert.Equal(Question, Assert.Single(task.Comments).Markdown);
        Assert.Equal(Two, task.QuestionOptions);
    }

    // --- they go whenever the question does --------------------------------------------------------------------------

    [Fact]
    public void Answered_clears_them()
    {
        var (board, id) = Waiting();

        Assert.Empty(Get(board.Answered("s1", At(12)), id).QuestionOptions);
    }

    [Fact]
    public void Done_clears_them()
    {
        var (board, id) = Waiting();

        Assert.Empty(Get(board.MarkDone(id, At(12)).Value, id).QuestionOptions);
    }

    [Fact]
    public void A_close_clears_them_and_undoing_it_brings_the_question_back_without_them()
    {
        var (board, id) = Waiting();
        board = board.Close(id, At(12), stoppingClaude: true).Value;
        Assert.Empty(Get(board, id).QuestionOptions);

        board = board.UndoClose(id, TaskStatus.WaitingOnYou, Question, At(13)).Value;

        Assert.Equal(Question, Get(board, id).Question.Value);
        Assert.Empty(Get(board, id).QuestionOptions);
    }

    [Fact]
    public void A_status_set_by_hand_away_from_waiting_clears_them()
    {
        var (board, id) = Waiting();

        Assert.Empty(Get(board.SetStatus(id, TaskStatus.InProgress, At(12)).Value, id).QuestionOptions);
    }

    [Fact]
    public void Waiting_set_by_hand_on_a_task_already_waiting_keeps_them()
    {
        var (board, id) = Waiting();

        Assert.Equal(Two, Get(board.SetStatus(id, TaskStatus.WaitingOnYou, At(12)).Value, id).QuestionOptions);
    }

    [Fact]
    public void Settling_by_a_merge_clears_them()
    {
        var (board, id) = Waiting();

        Assert.Empty(Get(board.SettleQuestion(id, Question, "merged", At(12), Guid.NewGuid()), id).QuestionOptions);
    }

    [Fact]
    public void The_users_reply_setting_the_question_aside_clears_them()
    {
        var (board, id) = Waiting();

        board = board.SetQuestionAside(id, Question, "note", At(12), Guid.NewGuid());

        var task = Get(board, id);
        Assert.Equal(Question, task.QuestionSetAside.Value);
        Assert.Empty(task.QuestionOptions);
    }

    [Fact]
    public void A_set_aside_question_asked_again_comes_back_with_the_new_asks_options()
    {
        var (board, id) = Waiting();
        board = board.SetQuestionAside(id, Question, "note", At(12), Guid.NewGuid());

        board = board.Ask(id, Question, At(14), options: ["Yes", "No"]).Value;

        var task = Get(board, id);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal(["Yes", "No"], task.QuestionOptions);
    }

    [Fact]
    public void A_set_aside_question_restored_by_deskbar_comes_back_without_them()
    {
        var (board, id) = Waiting();
        board = board.SetQuestionAside(id, Question, "note", At(12), Guid.NewGuid());

        board = board.RestoreQuestion(id, At(14));

        Assert.Equal(TaskStatus.WaitingOnYou, Get(board, id).Status);
        Assert.Equal(Question, Get(board, id).Question.Value);
        Assert.Empty(Get(board, id).QuestionOptions);
    }

    [Fact]
    public void The_service_clears_them_when_a_reply_sets_the_question_aside_and_saves_that()
    {
        var clock = T0;
        var store = new MemoryStore();
        var service = new TaskService(store, () => clock);
        var id = service.Create("Tasks panel", Maybe<Guid>.None, Session("s1")).Value.Id;
        clock = At(10);
        Assert.True(service.Ask(id, Question, options: Two).IsSuccess);
        Assert.Equal(Two, Get(store.Stored, id).QuestionOptions);
        clock = At(12);
        service.Prompted("s1");
        clock = At(13);

        service.Stalled(new HashSet<string> { "s1" }, new HashSet<string> { "s1" });

        Assert.Equal(TaskStatus.InProgress, Get(service.Board, id).Status);
        Assert.Empty(Get(service.Board, id).QuestionOptions);
        Assert.Empty(Get(store.Stored, id).QuestionOptions);
    }

    sealed class MemoryStore : ITaskStore
    {
        public TaskBoard Stored { get; private set; } = TaskBoard.Empty;

        public Result<TaskBoard> Load() => Stored;

        public Result Save(TaskBoard board)
        {
            Stored = board;
            return Result.Success();
        }
    }

    // --- the check behind an answer button ---------------------------------------------------------------------------

    [Fact]
    public void An_option_is_found_by_its_index_with_its_exact_words()
    {
        var (board, id) = Waiting();

        Assert.Equal("Merge it", board.OptionText(id, 0, Two).Value);
        Assert.Equal("Not yet", board.OptionText(id, 1, Two).Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void An_index_outside_the_options_is_refused(int index)
    {
        var (board, id) = Waiting();

        var refused = board.OptionText(id, index, Two);

        Assert.True(refused.IsFailure);
        Assert.Contains("no answer number", refused.Error);
    }

    [Fact]
    public void Options_that_changed_since_the_button_was_drawn_are_refused()
    {
        var (board, id) = Waiting();
        board = board.Ask(id, Question, At(12), options: ["Merge it", "Later"]).Value;

        Assert.Equal(TaskBoard.OptionsChanged, board.OptionText(id, 0, Two).Error);
    }

    [Fact]
    public void The_same_words_in_another_order_or_case_are_not_the_drawn_list()
    {
        var (board, id) = Waiting();

        Assert.True(board.OptionText(id, 0, ["Not yet", "Merge it"]).IsFailure);
        Assert.True(board.OptionText(id, 0, ["merge it", "Not yet"]).IsFailure);
        Assert.True(board.OptionText(id, 0, []).IsFailure);
    }

    [Fact]
    public void Once_the_question_is_answered_the_buttons_do_nothing()
    {
        var (board, id) = Waiting();
        board = board.Answered("s1", At(12));

        Assert.Equal(TaskBoard.OptionsChanged, board.OptionText(id, 0, Two).Error);
    }

    [Fact]
    public void A_closed_task_and_an_unknown_task_refuse()
    {
        var (board, id) = Waiting();
        var closed = board.Close(id, At(12), stoppingClaude: true).Value;

        Assert.True(closed.OptionText(id, 0, Two).IsFailure);
        Assert.True(board.OptionText(Guid.NewGuid(), 0, Two).IsFailure);
    }

    [Fact]
    public void Asking_the_same_options_again_keeps_the_drawn_buttons_working()
    {
        var (board, id) = Waiting();
        board = board.Ask(id, "Merge the pull request now?", At(12), options: Two).Value;

        Assert.Equal("Merge it", board.OptionText(id, 0, Two).Value);
    }

    // --- the help text and the briefing ------------------------------------------------------------------------------

    [Fact]
    public void The_help_text_and_the_briefing_tell_a_session_about_the_options_line()
    {
        var ask = Deskbar.Core.Control.ControlUsage.All.Single(c => c.Verb == "task" && c.Arguments.StartsWith("ask <question>|- --session"));
        Assert.Contains("--options", ask.Description);
        Assert.Contains("up to 4", ask.Description);

        var briefing = SessionBriefing.Start(new ClaudeHookEvent("SessionStart", "92943450", @"C:\repos\TaskSpaces", "startup", Headless: false),
            Maybe<TaskItem>.None, []);
        Assert.Contains("--options", briefing);
        Assert.Contains("40 characters", briefing);
    }

    // --- tasks.json --------------------------------------------------------------------------------------------------

    string TasksPath => Path.Combine(dir, "tasks.json");

    // One waiting task as the store writes it, then the file's text.
    string SavedWaiting(out Guid id)
    {
        var (board, made) = Waiting("Merge it", "Not yet", "Ask Maria");
        id = made;
        Assert.True(new JsonTaskStore(dir).Save(board).IsSuccess);
        return File.ReadAllText(TasksPath);
    }

    [Fact]
    public void The_options_survive_a_save_and_a_load()
    {
        SavedWaiting(out var id);

        var task = Get(new JsonTaskStore(dir).Load().Value, id);

        Assert.Equal(["Merge it", "Not yet", "Ask Maria"], task.QuestionOptions);
        Assert.Equal(Question, task.Question.Value);
    }

    [Fact]
    public void A_task_without_options_writes_no_field_so_its_file_is_as_before()
    {
        var (board, _) = Working();
        Assert.True(new JsonTaskStore(dir).Save(board).IsSuccess);

        Assert.DoesNotContain("questionOptions", File.ReadAllText(TasksPath));
    }

    [Fact]
    public void An_old_file_without_the_field_loads_with_no_options()
    {
        var text = SavedWaiting(out var id);
        var old = JsonSerializer.Serialize(RemovedProperty(text, "questionOptions"), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(TasksPath, old);

        var task = Get(new JsonTaskStore(dir).Load().Value, id);

        Assert.Equal(Question, task.Question.Value);
        Assert.Empty(task.QuestionOptions);
    }

    // The file's text with one property taken off every task.
    static JsonElement RemovedProperty(string text, string name)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
        var tasks = doc["tasks"].EnumerateArray()
            .Select(t => t.EnumerateObject().Where(p => p.Name != name).ToDictionary(p => p.Name, p => p.Value)).ToArray();
        return JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["version"] = 2, ["tasks"] = tasks });
    }

    // THE CLAIM THAT A BUILD WITHOUT THE FIELD STILL LOADS A FILE WITH IT cannot be run against the old build here, so
    // this holds its mechanism: the reader skips a property no DTO field names (the Web defaults do not disallow unmapped
    // members). A file carrying the options next to a property nobody knows, written the way a newer build would, loads,
    // and the unknown property is simply not read, so the same reader would skip `questionOptions` in a build without it.
    [Fact]
    public void A_file_with_an_unknown_field_still_loads()
    {
        var text = SavedWaiting(out var id);
        File.WriteAllText(TasksPath, text.Replace("\"questionOptions\"", "\"someFieldFromANewerBuild\": {\"a\": [1, 2]},\n      \"questionOptions\""));

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(["Merge it", "Not yet", "Ask Maria"], Get(loaded.Value, id).QuestionOptions);
    }

    [Fact]
    public void A_file_whose_field_is_not_an_array_reads_as_no_options_and_does_not_fail_the_load()
    {
        var text = SavedWaiting(out var id);
        var broken = System.Text.RegularExpressions.Regex.Replace(text, "\"questionOptions\": \\[[^\\]]*\\]", "\"questionOptions\": \"yes\"");
        Assert.NotEqual(text, broken);
        File.WriteAllText(TasksPath, broken);

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Empty(Get(loaded.Value, id).QuestionOptions);
        Assert.Equal(Question, Get(loaded.Value, id).Question.Value);
    }

    [Fact]
    public void A_hand_edited_list_that_breaks_the_limits_reads_as_none()
    {
        var text = SavedWaiting(out var id);
        File.WriteAllText(TasksPath, text.Replace("\"Ask Maria\"", "\"Merge it\""));

        Assert.Empty(Get(new JsonTaskStore(dir).Load().Value, id).QuestionOptions);
    }

    [Fact]
    public void Options_in_a_file_beside_no_question_are_dropped_on_load()
    {
        var text = SavedWaiting(out var id);
        File.WriteAllText(TasksPath, text.Replace($"\"question\": \"{Question}\"", "\"question\": null"));

        var task = Get(new JsonTaskStore(dir).Load().Value, id);

        Assert.True(task.Question.HasNoValue);
        Assert.Empty(task.QuestionOptions);
    }
}
