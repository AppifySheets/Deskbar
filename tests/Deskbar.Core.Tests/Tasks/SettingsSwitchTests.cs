using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Changing a task session's model, effort or permission mode (SettingsSwitch): when it may happen and to which session
// (Plan), how a Deskbar that closed halfway finishes it (Recover), and what the board stores (TaskBoard.Want,
// SwitchStarted, SessionSwitched, SwitchAbandoned), including that the switched-away session is left alone by the
// keeper, the comment wake, the list's reconcile and the /clear hand-over.
public class SettingsSwitchTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    static readonly Guid TaskId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    const string Original = "eea5466c-0000-0000-0000-000000000001";
    const string CopyId = "8c42135c-0000-0000-0000-000000000002";
    const string Folder = @"C:\repos\proj";

    static readonly SessionSettings Opus = new("opus", Maybe<string>.None, Maybe<string>.None);
    static readonly SessionSettings SonnetLow = new("sonnet", "low", Maybe<string>.None);

    static TaskSession Session(string id, int minute, bool live = true, SessionSettings? launched = null) =>
        new(id, Folder, T0.AddMinutes(minute), live ? Maybe<DateTimeOffset>.None : T0.AddMinutes(minute + 1))
        { LaunchedWith = launched ?? Opus };

    static TaskItem Task(TaskStatus status = TaskStatus.InProgress, Maybe<SessionSettings> wanted = default, params TaskSession[] sessions) =>
        new TaskItem(TaskId, "Picker", Maybe<Guid>.None, status, "", Maybe<string>.None, T0, T0, T0,
            sessions.Length > 0 ? sessions : [Session(Original, 0)], []) { Wanted = wanted };

    // An idle background session as `claude agents --json` lists it once its turn is over (measured: idle, done).
    static ClaudeAgent Idle(string id = Original, string shortId = "eea5466c") =>
        new(id, shortId, Folder, true, false, "") { Status = "idle", State = "done" };

    static SwitchStep Plan(TaskItem task, params ClaudeAgent[] listed) =>
        SettingsSwitch.Plan(task, listed, _ => true, _ => false);

    // --- Plan -------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_pick_a_closed_task_or_a_pick_the_session_already_runs_is_nothing()
    {
        Assert.IsType<SwitchStep.Nothing>(Plan(Task(), Idle()));
        Assert.IsType<SwitchStep.Nothing>(Plan(Task(TaskStatus.Closed, SonnetLow), Idle()));
        Assert.IsType<SwitchStep.Nothing>(Plan(Task(wanted: Opus), Idle()));
    }

    [Fact]
    public void An_idle_background_session_is_copied_now_with_the_pick()
    {
        var step = Assert.IsType<SwitchStep.CopyNow>(Plan(Task(wanted: SonnetLow), Idle()));

        Assert.Equal(Original, step.Session.Id);
        Assert.Equal("eea5466c", step.ShortId);
        Assert.Equal(SonnetLow, step.To);
    }

    // "I changed only the effort" keeps the model the session was started on.
    [Fact]
    public void A_pick_without_a_model_keeps_the_model_the_session_was_started_on()
    {
        var step = Assert.IsType<SwitchStep.CopyNow>(Plan(Task(wanted: new SessionSettings(Maybe<string>.None, "high", "plan")), Idle()));

        Assert.Equal(new SessionSettings("opus", "high", "plan"), step.To);
    }

    [Fact]
    public void A_busy_session_waits_for_the_end_of_its_step()
    {
        var working = Idle() with { Busy = true, Status = "busy", State = "working" };
        Assert.Equal(SettingsSwitch.Busy, Assert.IsType<SwitchStep.Wait>(Plan(Task(wanted: SonnetLow), working)).Why);
    }

    // Busy only on a background job of its own: the job belongs to the original's process, and a stop would kill it.
    [Fact]
    public void A_session_whose_own_background_job_still_runs_waits_too()
    {
        var jobRunning = Idle() with { Busy = true, Status = "busy", State = "done" };
        Assert.True(jobRunning.TurnOver);
        Assert.IsType<SwitchStep.Wait>(Plan(Task(wanted: SonnetLow), jobRunning));
    }

    // ClaudeAgent.Busy reads only "busy", so a permission prompt must be told by its status.
    [Fact]
    public void A_session_held_by_a_permission_prompt_waits_and_says_why()
    {
        var asking = Idle() with { Status = "waiting", State = "blocked" };
        Assert.False(asking.Busy);
        Assert.Equal(SettingsSwitch.WaitingOnPermission, Assert.IsType<SwitchStep.Wait>(Plan(Task(wanted: SonnetLow), asking)).Why);
    }

    [Fact]
    public void A_session_woken_a_moment_ago_waits()
    {
        var step = SettingsSwitch.Plan(Task(wanted: SonnetLow), [Idle()], _ => true, id => id == Original);
        Assert.Equal(SettingsSwitch.JustWoken, Assert.IsType<SwitchStep.Wait>(step).Why);
    }

    [Fact]
    public void A_switch_under_way_waits_for_itself()
    {
        var task = Task(wanted: SonnetLow) with { Switching = new PendingSwitch(Original, SonnetLow, T0) };
        Assert.Equal(SettingsSwitch.UnderWay, Assert.IsType<SwitchStep.Wait>(Plan(task, Idle())).Why);
    }

    // A listed row with no process is not running: there is no step to lose.
    [Fact]
    public void A_listed_session_without_a_process_is_copied()
    {
        var dead = Idle() with { HasProcess = false, Status = Maybe<string>.None, State = "blocked" };
        Assert.IsType<SwitchStep.CopyNow>(Plan(Task(wanted: SonnetLow), dead));
    }

    [Fact]
    public void A_session_in_vs_code_or_a_terminal_is_read_only()
    {
        var interactive = new ClaudeAgent(Original, Maybe<string>.None, Folder, false, false, "");
        Assert.Equal(SettingsSwitch.NotDeskbars, Assert.IsType<SwitchStep.ReadOnly>(Plan(Task(wanted: SonnetLow), interactive)).Why);
    }

    [Fact]
    public void A_session_that_does_not_run_takes_the_pick_at_its_next_wake_or_the_next_start_when_it_cannot_be_woken()
    {
        var task = Task(wanted: SonnetLow, sessions: Session(Original, 0, live: false));

        var wake = Assert.IsType<SwitchStep.AtNextWake>(SettingsSwitch.Plan(task, [], _ => true, _ => false));
        Assert.Equal(SonnetLow, wake.To);
        Assert.Equal(SonnetLow, Assert.IsType<SwitchStep.NextStart>(SettingsSwitch.Plan(task, [], _ => false, _ => false)).To);
        Assert.Equal(SonnetLow, Assert.IsType<SwitchStep.NextStart>(Plan(Task(wanted: SonnetLow) with { Sessions = [] })).To);
    }

    // The session a comment goes to: never a replaced one; live first, then the latest joined.
    [Fact]
    public void Only_the_current_session_of_several_is_switched()
    {
        var older = Session("11111111-0000-0000-0000-000000000001", 0);
        var ended = Session("22222222-0000-0000-0000-000000000002", 5, live: false);
        var replaced = Session("33333333-0000-0000-0000-000000000003", 9) with { ReplacedAt = T0, ReplacedBy = "x" };
        var task = Task(wanted: SonnetLow, sessions: [older, ended, replaced]);

        Assert.Equal(older.Id, SettingsSwitch.Current(task).Value.Id);
        var step = Assert.IsType<SwitchStep.CopyNow>(Plan(task, Idle(older.Id, "11111111")));
        Assert.Equal(older.Id, step.Session.Id);
    }

    // --- the board --------------------------------------------------------------------------------------------------

    static TaskBoard Board(TaskItem task) => new([task]);

    [Fact]
    public void Want_stores_the_pick_keeps_an_adopted_task_and_the_same_pick_again_changes_nothing()
    {
        var board = Board(Task() with { Adopted = true });

        var picked = board.Want(TaskId, SonnetLow, T0.AddMinutes(1)).Value;
        var task = picked.Tasks.Single();
        Assert.Equal(SonnetLow, task.Wanted.Value);
        Assert.False(task.Adopted);
        Assert.Equal(T0.AddMinutes(1), task.UpdatedAt);

        Assert.Same(picked, picked.Want(TaskId, SonnetLow, T0.AddMinutes(2)).Value);
        Assert.True(picked.Unwant(TaskId, T0.AddMinutes(3)).Value.Tasks.Single().Wanted.HasNoValue);
        Assert.True(Board(Task(TaskStatus.Closed)).Want(TaskId, SonnetLow, T0).IsFailure);
    }

    [Fact]
    public void A_switch_is_stored_once_and_only_for_the_task_s_own_session()
    {
        var board = Board(Task(wanted: SonnetLow));
        var pending = new PendingSwitch(Original, SonnetLow, T0);

        var started = board.SwitchStarted(TaskId, pending, T0).Value;
        Assert.Equal(pending, started.Tasks.Single().Switching.Value);
        Assert.True(started.SwitchStarted(TaskId, pending, T0).IsFailure);
        Assert.True(board.SwitchStarted(TaskId, pending with { FromSession = "someone-else" }, T0).IsFailure);

        var named = started.SwitchCopyNamed(TaskId, "8c42135c", T0).Value;
        Assert.Equal("8c42135c", named.Tasks.Single().Switching.Value.CopyShortId.Value);
        Assert.True(board.SwitchCopyNamed(TaskId, "8c42135c", T0).IsFailure);
    }

    // The task keeps its status: changing the model is not more work, so a Done task stays Done.
    [Fact]
    public void The_copy_joins_with_what_it_was_launched_with_and_the_original_is_replaced_by_it()
    {
        var now = T0.AddMinutes(5);
        var board = Board(Task(TaskStatus.Done, SonnetLow) with { Switching = new PendingSwitch(Original, SonnetLow, T0) });
        var copy = new TaskSession(CopyId, Folder, now, Maybe<DateTimeOffset>.None) { LaunchedWith = SonnetLow };

        var task = board.SessionSwitched(TaskId, Original, copy, now).Value.Tasks.Single();

        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.True(task.Switching.HasNoValue);
        Assert.Equal(SonnetLow, task.Wanted.Value);
        var original = task.Sessions.Single(s => s.Id == Original);
        Assert.Equal(now, original.Ended.Value);
        Assert.Equal(now, original.ReplacedAt.Value);
        Assert.Equal(CopyId, original.ReplacedBy.Value);
        Assert.Equal(SonnetLow, task.Sessions.Single(s => s.Id == CopyId).LaunchedWith);
        Assert.Equal(CopyId, SettingsSwitch.Current(task).Value.Id);
        Assert.IsType<SwitchStep.Nothing>(Plan(task, Idle(CopyId, "8c42135c")));
        Assert.True(board.SessionSwitched(TaskId, "not-on-it", copy, now).IsFailure);
    }

    // If the copy had been adopted meanwhile (Deskbar found it before joining it), the empty adopted task goes.
    [Fact]
    public void A_copy_adopted_meanwhile_leaves_its_empty_adopted_task()
    {
        var now = T0.AddMinutes(5);
        var adopted = new TaskItem(Guid.NewGuid(), "proj", Maybe<Guid>.None, TaskStatus.InProgress, "", Maybe<string>.None, now, now, now,
            [new TaskSession(CopyId, Folder, now, Maybe<DateTimeOffset>.None)], []) { Adopted = true };
        var board = new TaskBoard([Task(wanted: SonnetLow), adopted]);

        var switched = board.SessionSwitched(TaskId, Original, new TaskSession(CopyId, Folder, now, Maybe<DateTimeOffset>.None), now).Value;

        Assert.Equal(TaskId, switched.Tasks.Single().Id);
    }

    [Fact]
    public void A_given_up_switch_clears_itself_and_the_pick()
    {
        var board = Board(Task(wanted: SonnetLow) with { Switching = new PendingSwitch(Original, SonnetLow, T0) });

        var task = board.SwitchAbandoned(TaskId, T0.AddMinutes(2)).Value.Tasks.Single();

        Assert.True(task.Switching.HasNoValue);
        Assert.True(task.Wanted.HasNoValue);
        Assert.Equal(Original, SettingsSwitch.Current(task).Value.Id);
        var cleared = Board(Task());
        Assert.Same(cleared, cleared.SwitchAbandoned(TaskId, T0).Value);
    }

    // --- the switched-away session is left alone --------------------------------------------------------------------

    static TaskItem Switched(TaskStatus status = TaskStatus.InProgress) =>
        Board(Task(status, SonnetLow)).SessionSwitched(TaskId, Original,
            new TaskSession(CopyId, Folder, T0.AddMinutes(5), Maybe<DateTimeOffset>.None) { LaunchedWith = SonnetLow }, T0.AddMinutes(5))
            .Value.Tasks.Single();

    // Between the copy's join and the original's stop, both are listed; the original must not come back to life.
    [Fact]
    public void The_list_does_not_revive_the_original_while_it_waits_for_its_stop()
    {
        var board = new TaskBoard([Switched()]);

        var reconciled = board.Reconcile([Idle(), Idle(CopyId, "8c42135c")], T0.AddMinutes(6));

        var task = reconciled.Tasks.Single();
        Assert.False(task.Sessions.Single(s => s.Id == Original).IsLive);
        Assert.True(task.Sessions.Single(s => s.Id == CopyId).IsLive);
        Assert.True(reconciled.TaskOf(Original).HasNoValue);
    }

    [Fact]
    public void The_keeper_never_wakes_the_original_and_comments_go_to_the_copy()
    {
        var task = Switched();
        var board = new TaskBoard([task with { Sessions = task.Sessions.Select(s => s with { Ended = T0.AddMinutes(7) }).ToArray() }]);

        var steps = SessionKeeper.Plan(board, [], ResumeLedger.Empty, _ => true, _ => false, T0.AddHours(1));
        var resume = Assert.IsType<KeeperStep.Resume>(Assert.Single(steps));
        Assert.Equal(CopyId, resume.Session.Id);

        var wake = Assert.IsType<WakePlan.Resume>(CommentWake.Plan(board.Tasks.Single(), [], _ => true, _ => false));
        Assert.Equal(CopyId, wake.Session.Id);
    }

    // Only a /clear hands its task to the next fresh session in its folder.
    [Fact]
    public void A_clear_elsewhere_in_the_folder_does_not_take_the_switched_task()
    {
        var board = new TaskBoard([Switched()]);
        Assert.True(board.ClearedTaskIn(_ => true, T0.AddMinutes(5).AddSeconds(10)).HasNoValue);

        var cleared = new TaskBoard([Task()]).SessionCleared(Original, T0);
        Assert.True(cleared.ClearedTaskIn(_ => true, T0.AddSeconds(10)).HasValue);
    }

    // --- Recover, and the keeper's adoption ---------------------------------------------------------------------------

    static TaskItem Pending(Maybe<string> copyShortId = default) =>
        Task(wanted: SonnetLow) with { Switching = new PendingSwitch(Original, SonnetLow, T0) { CopyShortId = copyShortId } };

    static ClaudeAgent Copy(string id = CopyId, string shortId = "8c42135c", int startedSecond = 2, string folder = Folder) =>
        Idle(id, shortId) with { Folder = folder, StartedAt = T0.AddSeconds(startedSecond) };

    [Fact]
    public void Recover_finds_the_copy_by_its_short_id()
    {
        var found = Assert.IsType<SwitchRecovery.Finish>(SettingsSwitch.Recover(Pending("8c42135c"), [Idle(), Copy()], T0.AddSeconds(5), _ => false));
        Assert.Equal(CopyId, found.Copy.SessionId);
    }

    // Deskbar closed before Claude named the copy: the one new unknown session in the original's folder is it.
    [Fact]
    public void Without_a_short_id_the_one_new_unknown_session_in_the_folder_is_the_copy()
    {
        var task = Pending();

        Assert.IsType<SwitchRecovery.Finish>(SettingsSwitch.Recover(task, [Idle(), Copy(folder: @"c:/repos/PROJ/")], T0.AddSeconds(5), _ => false));
        // Older than the switch, in another folder, or already a task's: not the copy.
        Assert.IsType<SwitchRecovery.Wait>(SettingsSwitch.Recover(task, [Copy(startedSecond: -60)], T0.AddSeconds(5), _ => false));
        Assert.IsType<SwitchRecovery.Wait>(SettingsSwitch.Recover(task, [Copy(folder: @"C:\repos\other")], T0.AddSeconds(5), _ => false));
        Assert.IsType<SwitchRecovery.Wait>(SettingsSwitch.Recover(task, [Copy()], T0.AddSeconds(5), id => id == CopyId));
        // Two candidates cannot be told apart.
        Assert.IsType<SwitchRecovery.Wait>(SettingsSwitch.Recover(task,
            [Copy(), Copy("99999999-0000-0000-0000-000000000009", "99999999")], T0.AddSeconds(5), _ => false));
    }

    // The original was never stopped, so giving up loses nothing.
    [Fact]
    public void A_copy_that_never_appears_is_given_up_after_a_minute()
    {
        Assert.IsType<SwitchRecovery.Wait>(SettingsSwitch.Recover(Pending("8c42135c"), [Idle()], T0 + SettingsSwitch.RecoverWithin, _ => false));
        Assert.IsType<SwitchRecovery.Abandon>(SettingsSwitch.Recover(Pending("8c42135c"), [Idle()], T0 + SettingsSwitch.RecoverWithin + TimeSpan.FromSeconds(1), _ => false));
        Assert.IsType<SwitchRecovery.Nothing>(SettingsSwitch.Recover(Task(), [Idle()], T0, _ => false));
    }

    [Fact]
    public void The_keeper_does_not_adopt_a_copy_a_switch_can_still_claim()
    {
        var steps = SessionKeeper.Plan(new TaskBoard([Pending()]), [Idle(), Copy()], ResumeLedger.Empty, _ => true, _ => false, T0.AddMinutes(1));
        Assert.DoesNotContain(steps, step => step is KeeperStep.Adopt);

        var stranger = Copy(folder: @"C:\repos\other");
        var adopted = SessionKeeper.Plan(new TaskBoard([Pending()]), [Idle(), stranger], ResumeLedger.Empty, _ => true, _ => false, T0.AddMinutes(1));
        Assert.Contains(adopted, step => step is KeeperStep.Adopt adopt && adopt.Agent.SessionId == CopyId);
    }

    // --- Drifted ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_wake_that_lost_the_effort_or_mode_Deskbar_started_with_has_drifted()
    {
        var session = Session(Original, 0, launched: new SessionSettings("sonnet", "low", "plan"));

        Assert.False(SettingsSwitch.Drifted(session, new SessionSettings("claude-opus-5-5", "LOW", "plan")));
        Assert.True(SettingsSwitch.Drifted(session, new SessionSettings(Maybe<string>.None, "medium", "plan")));
        Assert.True(SettingsSwitch.Drifted(session, new SessionSettings(Maybe<string>.None, "low", "auto")));
        // What either side does not know is no evidence.
        Assert.False(SettingsSwitch.Drifted(session, SessionSettings.None));
        Assert.False(SettingsSwitch.Drifted(Session(Original, 0), new SessionSettings(Maybe<string>.None, "max", "auto")));
        // manual is Claude Code's other name for default.
        Assert.False(SettingsSwitch.Drifted(Session(Original, 0, launched: new SessionSettings("opus", Maybe<string>.None, "default")),
            new SessionSettings(Maybe<string>.None, Maybe<string>.None, "manual")));
    }
}
