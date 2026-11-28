using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Task e948b383: what the transcripts say about the usage limit, and the probe's verdict. The lines below are cut down
// from what Claude Code 2.1.332 wrote on 2026-11-28 (sessions a8c3dd64, 2c94ab8f): only the fields read are kept.
public class UsageLimitSignsTests
{
    static readonly DateTimeOffset T = new(2026, 11, 28, 7, 25, 0, TimeSpan.Zero);
    static readonly DateTimeOffset ResetsAt = new(2026, 11, 28, 10, 10, 0, TimeSpan.Zero);

    static string Stamp(DateTimeOffset at) => at.ToString("O");

    static string Prompt(DateTimeOffset at, string text = "go") =>
        "{\"type\":\"user\",\"timestamp\":\"" + Stamp(at) + "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    static string Reply(DateTimeOffset at, string stop = "end_turn", string text = "ok") =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + Stamp(at)
        + "\",\"message\":{\"model\":\"claude-opus-5-5\",\"role\":\"assistant\",\"stop_reason\":\"" + stop + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    // What session a8c3dd64 answered each time, cut down.
    const string Waiting = "It's waiting for the usage limit to lift at 14:10.";

    static string Note(DateTimeOffset at, string kind, bool sidechain = false) =>
        "{\"type\":\"user\",\"isSidechain\":" + (sidechain ? "true" : "false") + ",\"isMeta\":true,\"usageLimitNote\":\"" + kind + "\",\"timestamp\":\"" + Stamp(at)
        + "\",\"message\":{\"role\":\"user\",\"content\":\"[note]\"}}";

    static string LimitError(DateTimeOffset at) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + Stamp(at) + "\",\"isApiErrorMessage\":true,\"apiErrorParams\":{\"rate_limit_info\":{\"resetsAt\":"
        + ResetsAt.ToUnixTimeSeconds() + ",\"rateLimitType\":\"five_hour\"}},\"message\":{\"model\":\"<synthetic>\",\"role\":\"assistant\",\"stop_reason\":\"stop_sequence\"}}";

    static Maybe<DateTimeOffset> Lifted(params string[] lines) => UsageLimitSigns.Read(lines).Lifted;

    [Fact]
    public void A_release_note_is_proof_the_limit_is_gone() =>
        Assert.Equal(T.AddMinutes(3), Lifted(Prompt(T), Note(T.AddMinutes(1), "release"), Note(T.AddMinutes(3), "release")));

    // Replies inside the grace allowance are answered while the limit holds, and the note that marks them can lie
    // outside the tail that is read (review finding 1), so no reply is proof; the probe covers what they would.
    [Fact]
    public void An_ordinary_reply_is_not_proof() =>
        Assert.Equal(Maybe<DateTimeOffset>.None, Lifted(Prompt(T), Reply(T.AddMinutes(20)), Reply(T.AddMinutes(30))));

    [Fact]
    public void A_subagent_release_is_not_read() =>
        Assert.Equal(Maybe<DateTimeOffset>.None, Lifted(Note(T, "release", sidechain: true)));

    [Fact]
    public void The_latest_limit_error_names_its_reset()
    {
        var signs = UsageLimitSigns.Read([LimitError(T.AddMinutes(2)), Reply(T.AddHours(-1)), LimitError(T)]);
        Assert.Equal(new LimitErrorSign(T.AddMinutes(2), ResetsAt), signs.LatestError.Value);
    }

    [Fact]
    public void All_transcripts_together_take_the_latest_proof_and_the_latest_error()
    {
        var one = UsageLimitSigns.Read([LimitError(T), Note(T.AddMinutes(3), "release")]);
        var two = UsageLimitSigns.Read([Note(T.AddMinutes(4), "release"), LimitError(T.AddMinutes(1))]);
        var all = LimitSigns.Of([one, two]);
        Assert.Equal(T.AddMinutes(4), all.Lifted);
        Assert.Equal(T.AddMinutes(1), all.LatestError.Value.At);
        Assert.True(all.LiftedSince(T.AddMinutes(2)));
        Assert.False(all.LiftedSince(T.AddMinutes(4)));
    }

    // Session 2c94ab8f on 2026-11-28: warned at 07:25:12, it finished its turn properly at 07:25:28 and said it stopped
    // on the limit. No API error, so LastTurnError sees a finished turn.
    [Fact]
    public void A_turn_finished_after_a_wrap_up_note_is_a_stop_on_the_limit() =>
        Assert.Equal(T.AddSeconds(12),
            UsageLimitSigns.StoppedOnNote([Prompt(T), Reply(T.AddSeconds(11), "tool_use"), Note(T.AddSeconds(12), "wrap_up"), Reply(T.AddSeconds(28))]));

    [Fact]
    public void A_release_after_the_note_ends_the_stop() =>
        Assert.Equal(Maybe<DateTimeOffset>.None,
            UsageLimitSigns.StoppedOnNote([Note(T, "wrap_up"), Reply(T.AddSeconds(16)), Prompt(T.AddMinutes(14)), Note(T.AddMinutes(14), "release"), Reply(T.AddMinutes(15))]));

    [Fact]
    public void A_turn_still_running_after_the_note_is_not_a_stop_yet() =>
        Assert.Equal(Maybe<DateTimeOffset>.None, UsageLimitSigns.StoppedOnNote([Prompt(T), Note(T.AddSeconds(1), "wrap_up"), Reply(T.AddSeconds(5), "tool_use")]));

    // That is LastTurnError's case, and the keeper's: the error names its own reset.
    [Fact]
    public void A_turn_that_died_on_the_error_after_the_note_is_not_a_note_stop() =>
        Assert.Equal(Maybe<DateTimeOffset>.None, UsageLimitSigns.StoppedOnNote([Prompt(T), Note(T.AddSeconds(1), "wrap_up"), LimitError(T.AddSeconds(20))]));

    // Session a8c3dd64 on 2026-11-28: its turn died on the limit at 07:25 (reset 14:10); Deskbar's asks woke it from 07:40
    // on, every reply went through, and each time it said it was waiting for 14:10 and stopped.
    [Fact]
    public void A_session_whose_own_replies_follow_its_limit_error_holds_itself() =>
        Assert.Equal(new LimitErrorSign(T, ResetsAt),
            UsageLimitSigns.HeldAfterLift([Prompt(T.AddSeconds(-5)), LimitError(T), Prompt(T.AddMinutes(15), "Deskbar: no session has worked"), Reply(T.AddMinutes(15).AddSeconds(5), text: Waiting)]).Value);

    // Second review, finding 1: a session that worked after the lift (the keeper resumed it, Petre typed into it) has
    // replies after the error too; its last words are its work, not the limit.
    [Fact]
    public void A_session_that_worked_after_the_lift_does_not_hold_itself() =>
        Assert.Equal(Maybe<LimitErrorSign>.None,
            UsageLimitSigns.HeldAfterLift([LimitError(T), Prompt(T.AddMinutes(5)), Reply(T.AddMinutes(9), text: "Built it; PR 12 is open and CI is green.")]));

    // Second review, finding 2: once told the limit is over (the held nudge, the keeper's limit resume), its answer is
    // one more reply after the error, and it must not be told again after a Deskbar restart.
    [Theory]
    [InlineData(SessionKeeper.HeldNudge)]
    [InlineData(SessionKeeper.LimitContinueMessage)]
    public void A_session_told_the_limit_is_over_does_not_hold_itself(string told) =>
        Assert.Equal(Maybe<LimitErrorSign>.None,
            UsageLimitSigns.HeldAfterLift([LimitError(T), Prompt(T.AddMinutes(15), "Deskbar's watch: " + told), Reply(T.AddMinutes(16), text: "Still " + Waiting)]));

    [Fact]
    public void A_release_note_after_the_error_means_it_was_told() =>
        Assert.Equal(Maybe<LimitErrorSign>.None,
            UsageLimitSigns.HeldAfterLift([LimitError(T), Prompt(T.AddMinutes(15)), Note(T.AddMinutes(15), "release"), Reply(T.AddMinutes(16), text: Waiting)]));

    [Fact]
    public void A_session_still_refused_does_not_hold_itself() =>
        Assert.Equal(Maybe<LimitErrorSign>.None,
            UsageLimitSigns.HeldAfterLift([Prompt(T.AddSeconds(-5)), LimitError(T), Prompt(T.AddMinutes(15)), LimitError(T.AddMinutes(15).AddSeconds(1))]));

    [Fact]
    public void A_session_working_after_its_limit_error_does_not_hold_itself_yet() =>
        Assert.Equal(Maybe<LimitErrorSign>.None,
            UsageLimitSigns.HeldAfterLift([LimitError(T), Prompt(T.AddMinutes(15)), Reply(T.AddMinutes(15).AddSeconds(5), "tool_use")]));

    [Fact]
    public void Made_up_replies_after_the_error_prove_nothing() =>
        Assert.Equal(Maybe<LimitErrorSign>.None,
            UsageLimitSigns.HeldAfterLift([LimitError(T), Prompt(T.AddMinutes(15)), Reply(T.AddMinutes(15).AddSeconds(1), "end_turn").Replace("claude-opus-5-5", "<synthetic>")]));

    [Fact]
    public void A_session_that_never_hit_the_limit_does_not_hold_itself() =>
        Assert.Equal(Maybe<LimitErrorSign>.None, UsageLimitSigns.HeldAfterLift([Prompt(T), Reply(T.AddSeconds(5))]));

    // --- the probe -------------------------------------------------------------------------------------------------------

    // What `claude -p ... --output-format json` printed on 2026-11-28 with the limit lifted (fields cut down).
    const string Answer = """{"type":"result","subtype":"success","is_error":false,"duration_api_ms":538,"num_turns":1,"result":"OK","total_cost_usd":0.00064}""";

    [Fact]
    public void The_probe_answered_when_claude_printed_a_successful_result() =>
        Assert.True(UsageLimitProbe.Answered(Result.Success(Answer)));

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"You've hit your 5-hour limit"}""")]
    [InlineData("""{"type":"result","subtype":"error_during_execution","is_error":false}""")]
    [InlineData("You've hit your 5-hour limit")]
    [InlineData("")]
    public void Anything_else_means_the_limit_still_holds(string output) =>
        Assert.False(UsageLimitProbe.Answered(Result.Success(output)));

    [Fact]
    public void A_failed_run_means_the_limit_still_holds() =>
        Assert.False(UsageLimitProbe.Answered(Result.Failure<string>("claude exited with 1")));

    // Each flag keeps the probe small and out of Deskbar's way (UsageLimitProbe's header); these are the ones measured.
    [Fact]
    public void The_probe_is_the_measured_command() =>
        Assert.Equal(["-p", "--model", "haiku", "--no-session-persistence", "--setting-sources", "", "--strict-mcp-config",
            "--tools", "", "--disable-slash-commands", "--output-format", "json", "Reply with the single word OK"], UsageLimitProbe.Arguments);
}
