using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// What the picker row says (SessionPicker.RowOf): the buttons' words, the phase, Cancel and read-only.
public class SessionPickerTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    const string Original = "eea5466c-0000-0000-0000-000000000001";
    static readonly SessionSettings SonnetLowEdits = new("sonnet", "low", "acceptEdits");

    static TaskItem Task(SessionSettings? launched = null, Maybe<SessionSettings> wanted = default, bool session = true) =>
        new TaskItem(Guid.NewGuid(), "Picker", Maybe<Guid>.None, TaskStatus.InProgress, "", Maybe<string>.None, T0, T0, T0,
            session ? [new TaskSession(Original, @"C:\repos\proj", T0, Maybe<DateTimeOffset>.None) { LaunchedWith = launched ?? SessionSettings.None }] : [], [])
        { Wanted = wanted };

    static ClaudeAgent Background(string status = "idle") => new(Original, "eea5466c", @"C:\repos\proj", true, status == "busy", "") { Status = status, State = "done" };

    static SessionPickRow Row(TaskItem task, IReadOnlyCollection<ClaudeAgent>? listed = null, string? lastModel = null, bool replied = false) =>
        SessionPicker.RowOf(new TaskBoard([task]), task, listed ?? [Background()], _ => Maybe.From(lastModel), _ => replied);

    [Fact]
    public void A_session_Deskbar_started_shows_what_it_was_started_with()
    {
        var row = Row(Task(SonnetLowEdits));

        Assert.Equal("Sonnet · low", row.ModelWords);
        Assert.Equal("Edit by itself", row.ModeWords);
        Assert.Equal(PickPhase.Settled, row.Phase);
        Assert.True(row.Pickable);
        Assert.False(row.CanCancel);
    }

    [Fact]
    public void A_session_Deskbar_did_not_set_shows_its_last_reply_s_model_or_defaults()
    {
        // Listed in the background but not started by Deskbar: shown, and read-only, with why.
        var seen = Row(Task(), lastModel: "claude-opus-5-5");
        Assert.Equal("claude-opus-5-5 (last reply) · default effort", seen.ModelWords);
        Assert.Equal(SettingsSwitch.NotStartedByDeskbar, seen.ReadOnlyWhy.Value);
        Assert.Equal("Claude Code's setting", seen.ModeWords);
        // Not listed yet: the last reply's model, and the tip says where it comes from.
        Assert.Equal(SessionPicker.LastReplyTip, Row(Task(), [], lastModel: "claude-opus-5-5").Tip);

        Assert.Equal("Default model · default effort", Row(Task()).ModelWords);
    }

    // Review H2: an ended entry, or a session that moved to another task, is not this task's session.
    [Fact]
    public void A_task_whose_session_ended_or_moved_shows_its_pick_for_the_next_start()
    {
        var ended = Task(SonnetLowEdits) with { Sessions = [new TaskSession(Original, @"C:\repos\proj", T0, T0.AddMinutes(1)) { LaunchedWith = SonnetLowEdits }] };
        var row = Row(ended with { Wanted = new SessionSettings("haiku", Maybe<string>.None, Maybe<string>.None) });

        Assert.Equal("Haiku · default effort", row.ModelWords);
        Assert.Equal(SessionPicker.NoLiveSessionTip, row.Tip);
        Assert.True(row.Pickable);
    }

    [Fact]
    public void A_task_without_a_session_shows_its_pick_for_the_first_start()
    {
        var row = Row(Task(session: false, wanted: SonnetLowEdits), []);

        Assert.Equal("Sonnet · low", row.ModelWords);
        Assert.Equal(SessionPicker.FirstStartTip, row.Tip);
        Assert.True(row.Pickable);
    }

    // VS Code or a terminal: read-only, and why.
    [Fact]
    public void A_session_in_its_own_window_is_read_only_and_says_why()
    {
        var interactive = new ClaudeAgent(Original, Maybe<string>.None, @"C:\repos\proj", false, false, "");

        var row = Row(Task(SonnetLowEdits), [interactive]);

        Assert.True(row.ReadOnly);
        Assert.False(row.Pickable);
        Assert.Equal(SettingsSwitch.NotDeskbars, row.ReadOnlyWhy.Value);
    }

    [Fact]
    public void A_switch_shows_where_it_goes_and_what_it_waits_for()
    {
        var pending = new PendingSwitch(Original, SonnetLowEdits, T0);

        var waiting = Row(Task() with { Switching = pending }, [Background("busy")]);
        Assert.Equal((PickPhase.Waiting, SettingsSwitch.Busy, true), (waiting.Phase, waiting.PhaseWords, waiting.CanCancel));
        Assert.Equal("Sonnet · low", waiting.ModelWords);
        Assert.Equal(SettingsSwitch.WaitingOnPermission, Row(Task() with { Switching = pending }, [Background("waiting")]).PhaseWords);

        var issued = Row(Task() with { Switching = pending with { Issued = T0 } });
        Assert.Equal((PickPhase.Switching, false, false), (issued.Phase, issued.CanCancel, issued.Pickable));

        var copy = new TaskSession("c0000001-0000", @"C:\repos\proj", T0.AddMinutes(2), Maybe<DateTimeOffset>.None) { LaunchedWith = SonnetLowEdits };
        var joinedTask = Task() with { Sessions = [.. Task().Sessions, copy], Switching = pending with { Issued = T0, CopyId = copy.Id } };
        var joined = Row(joinedTask);
        Assert.Equal((PickPhase.Confirming, SessionPicker.ConfirmingWords, true), (joined.Phase, joined.PhaseWords, joined.CanCancel));
        Assert.NotEqual(waiting.Signature, joined.Signature);

        // Review M2: once the copy has answered the switch is final: no Cancel, and the row says the earlier session
        // stops at its next pause.
        var final = Row(joinedTask, replied: true);
        Assert.Equal((PickPhase.Confirming, SettingsSwitch.OriginalStopsAtPause, false), (final.Phase, final.PhaseWords, final.CanCancel));
    }
}
