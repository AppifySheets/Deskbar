using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The manager's own question to the user (task 61e4d143, ManagerQuestion): the pure rules. RemoteControl stores what these
// return; its tests cover the pipe forms and the hook.
public class ManagerQuestionTests
{
    const string Id = "aaaaaaaa-1111-2222-3333-444444444444";
    static readonly DateOnly today = new(2026, 11, 25);
    static readonly DateTimeOffset at = new(2026, 11, 25, 9, 30, 0, TimeSpan.Zero);

    static ManagerRecord Record(string? question = null) => new(Id, today, Question: question, AskedAt: question is null ? null : at);

    static Maybe<ManagerRecord> Stored(string? question = null) => Record(question);

    [Fact]
    public void Ask_stores_the_trimmed_question_and_when_it_was_asked()
    {
        var kept = ManagerQuestion.Ask(Stored(), "  Which comes first?\n", at).Value;

        Assert.Equal("Which comes first?", kept.Question);
        Assert.Equal(at, kept.AskedAt);
        Assert.Equal(Id, kept.SessionId);
    }

    [Fact]
    public void A_second_ask_replaces_the_first()
    {
        var kept = ManagerQuestion.Ask(Stored("old"), "new", at.AddMinutes(5)).Value;

        Assert.Equal("new", kept.Question);
        Assert.Equal(at.AddMinutes(5), kept.AskedAt);
    }

    [Fact]
    public void A_question_over_the_limit_is_cut_with_an_ellipsis()
    {
        var kept = ManagerQuestion.Ask(Stored(), new string('x', ManagerQuestion.MaxLength + 500), at).Value;

        Assert.Equal(ManagerQuestion.MaxLength, kept.Question!.Length);
        Assert.EndsWith("…", kept.Question);
    }

    [Fact]
    public void A_question_at_the_limit_is_kept_whole() =>
        Assert.Equal(ManagerQuestion.MaxLength, ManagerQuestion.Ask(Stored(), new string('x', ManagerQuestion.MaxLength), at).Value.Question!.Length);

    [Fact]
    public void Ask_without_a_manager_on_record_fails() =>
        Assert.Equal(ManagerQuestion.NoManager, ManagerQuestion.Ask(Maybe<ManagerRecord>.None, "x", at).Error);

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public void A_blank_question_fails(string text) =>
        Assert.Equal(ManagerQuestion.Empty, ManagerQuestion.Ask(Stored("kept"), text, at).Error);

    [Fact]
    public void Answered_clears_the_question_and_its_time()
    {
        var kept = ManagerQuestion.Answered(Stored("open")).Value;

        Assert.Null(kept.Question);
        Assert.Null(kept.AskedAt);
        Assert.Equal(Id, kept.SessionId);
    }

    [Fact]
    public void Answered_without_a_manager_on_record_fails() =>
        Assert.Equal(ManagerQuestion.NoManager, ManagerQuestion.Answered(Maybe<ManagerRecord>.None).Error);

    [Fact]
    public void A_users_prompt_to_the_stored_session_clears_the_question()
    {
        var kept = ManagerQuestion.AfterPrompt(Stored("open"), Id, "yes, close it");

        Assert.True(kept.HasValue);
        Assert.Null(kept.Value.Question);
        Assert.Null(kept.Value.AskedAt);
    }

    [Theory]
    [InlineData("Deskbar wake. Since your last look:")]
    [InlineData("  \n Deskbar wake. Since your last look:")]
    // A wake wrapped in other text (the inbox road through the Stop hook was not measured; pre-PR review, finding 4).
    [InlineData("Stop hook feedback:\nDeskbar wake. Since your last look:")]
    public void A_deskbar_wake_leaves_the_question_open(string prompt) =>
        Assert.True(ManagerQuestion.AfterPrompt(Stored("open"), Id, prompt).HasNoValue);

    [Fact]
    public void A_prompt_to_another_session_leaves_the_question_open() =>
        Assert.True(ManagerQuestion.AfterPrompt(Stored("open"), "bbbbbbbb", "yes").HasNoValue);

    // The user's message marks the turn as their reply even with no question open (task 2c5de076): its end is an answer.
    [Fact]
    public void A_users_prompt_with_no_question_open_marks_the_turn_as_a_reply()
    {
        var kept = ManagerQuestion.AfterPrompt(Stored(), Id, "yes");

        Assert.True(kept.HasValue);
        Assert.True(kept.Value.Replying);
    }

    [Fact]
    public void A_second_users_prompt_while_replying_stores_nothing_new() =>
        Assert.True(ManagerQuestion.AfterPrompt(Record() with { Replying = true }, Id, "and also").HasNoValue);

    // --- the manager's answer (task 2c5de076) ---

    static ManagerRecord Replying() => Record() with { Replying = true };

    static ManagerRecord Answered(string answer = "Done.") => Record() with { Answer = answer, AnsweredAt = at };

    [Fact]
    public void A_users_prompt_reads_the_unread_answer()
    {
        var kept = ManagerQuestion.AfterPrompt(Answered(), Id, "thanks, next").Value;

        Assert.Null(kept.Answer);
        Assert.Null(kept.AnsweredAt);
        Assert.True(kept.Replying);
    }

    [Fact]
    public void A_deskbar_wake_leaves_the_unread_answer_and_is_no_reply()
    {
        var kept = ManagerQuestion.AfterPrompt(Answered() with { Replying = true }, Id, "Deskbar wake. Since your last look:").Value;

        Assert.Equal("Done.", kept.Answer);
        Assert.False(kept.Replying);
    }

    [Fact]
    public void The_end_of_a_reply_stores_its_first_line_and_when()
    {
        var kept = ManagerQuestion.AfterStop(Replying(), Id, "\n  Yes, it shows now.\n\nThe details: ...", false, at).Value;

        Assert.Equal("Yes, it shows now.", kept.Answer);
        Assert.Equal(at, kept.AnsweredAt);
        Assert.False(kept.Replying);
    }

    [Theory]
    [InlineData("## Where it stands", "Where it stands")]
    [InlineData("**No.** The Inbox shows nothing.", "No. The Inbox shows nothing.")]
    [InlineData("- first point", "first point")]
    [InlineData("> quoted", "quoted")]
    public void The_answer_drops_the_marks_the_line_cannot_draw(string reply, string shown) =>
        Assert.Equal(shown, ManagerQuestion.AfterStop(Replying(), Id, reply, false, at).Value.Answer);

    [Fact]
    public void A_long_first_line_is_cut()
    {
        var answer = ManagerQuestion.AfterStop(Replying(), Id, new string('a', 500), false, at).Value.Answer!;

        Assert.Equal(ManagerQuestion.AnswerLength, answer.Length);
        Assert.EndsWith("…", answer);
    }

    [Fact]
    public void A_reply_without_its_text_still_asks_to_be_read() =>
        Assert.Equal(ManagerQuestion.NoReplyText, ManagerQuestion.AfterStop(Replying(), Id, Maybe<string>.None, false, at).Value.Answer);

    [Fact]
    public void A_reply_written_with_the_window_in_front_stores_no_answer()
    {
        var kept = ManagerQuestion.AfterStop(Replying(), Id, "Yes.", true, at).Value;

        Assert.Null(kept.Answer);
        Assert.False(kept.Replying);
    }

    [Fact]
    public void The_end_of_a_turn_that_was_no_reply_stores_nothing() =>
        Assert.True(ManagerQuestion.AfterStop(Stored(), Id, "Nothing needs the user.", false, at).HasNoValue);

    [Fact]
    public void The_end_of_another_sessions_turn_stores_nothing() =>
        Assert.True(ManagerQuestion.AfterStop(Replying(), "bbbbbbbb", "Yes.", false, at).HasNoValue);

    [Fact]
    public void Seeing_the_manager_reads_the_answer_and_keeps_the_question()
    {
        var kept = ManagerQuestion.Seen(Answered() with { Question = "Which first?", AskedAt = at }).Value;

        Assert.Null(kept.Answer);
        Assert.Equal("Which first?", kept.Question);
    }

    [Fact]
    public void Seeing_the_manager_with_nothing_unread_stores_nothing() =>
        Assert.True(ManagerQuestion.Seen(Stored("open")).HasNoValue);

    [Fact]
    public void A_prompt_with_no_text_still_counts_as_the_users_reply() =>
        Assert.True(ManagerQuestion.AfterPrompt(Stored("open"), Id, Maybe<string>.None).HasValue);

    [Fact]
    public void Record_equality_covers_the_question_and_when_it_was_asked()
    {
        Assert.Equal(Record("q"), Record("q"));
        Assert.NotEqual(Record("q"), Record("other"));
        Assert.NotEqual(Record("q"), Record("q") with { AskedAt = at.AddHours(1) });
        Assert.NotEqual(Record("q"), Record());
        Assert.NotEqual(Record(), Record() with { Replying = true });
        Assert.NotEqual(Record(), Answered());
        Assert.NotEqual(Answered(), Answered() with { AnsweredAt = at.AddHours(1) });
    }
}
