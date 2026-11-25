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
    public void A_deskbar_wake_leaves_the_question_open(string prompt) =>
        Assert.True(ManagerQuestion.AfterPrompt(Stored("open"), Id, prompt).HasNoValue);

    [Fact]
    public void A_prompt_to_another_session_leaves_the_question_open() =>
        Assert.True(ManagerQuestion.AfterPrompt(Stored("open"), "bbbbbbbb", "yes").HasNoValue);

    [Fact]
    public void A_prompt_with_no_question_open_stores_nothing() =>
        Assert.True(ManagerQuestion.AfterPrompt(Stored(), Id, "yes").HasNoValue);

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
    }
}
