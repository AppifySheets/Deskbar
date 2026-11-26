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
        SettingsSwitch.Plan(new TaskBoard([task]), task, listed, _ => true, _ => false);

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
        var step = SettingsSwitch.Plan(Board(Task(wanted: SonnetLow)), Task(wanted: SonnetLow), [Idle()], _ => true, id => id == Original);
        Assert.Equal(SettingsSwitch.JustWoken, Assert.IsType<SwitchStep.Wait>(step).Why);
    }

    [Fact]
    public void A_switch_under_way_waits_for_itself()
    {
        var issued = Task(wanted: SonnetLow) with { Switching = new PendingSwitch(Original, SonnetLow, T0) { Issued = T0 } };
        Assert.Equal(SettingsSwitch.UnderWay, Assert.IsType<SwitchStep.Wait>(Plan(issued, Idle())).Why);
        // A request still waiting for a pause is decided again, so a later pick can replace it.
        var requested = Task(wanted: SonnetLow) with { Switching = new PendingSwitch(Original, Opus, T0) };
        Assert.IsType<SwitchStep.CopyNow>(Plan(requested, Idle()));
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

    // Review H2: an ended entry is not the task's session. The pick waits for the next start; nothing is resumed.
    [Fact]
    public void An_ended_session_is_not_switched_and_the_pick_waits_for_the_next_start()
    {
        var ended = Task(wanted: SonnetLow, sessions: Session(Original, 0, live: false));

        Assert.Equal(SonnetLow, Assert.IsType<SwitchStep.NextStart>(SettingsSwitch.Plan(Board(ended), ended, [], _ => true, _ => false)).To);
        Assert.IsType<SwitchStep.NextStart>(Plan(ended, Idle()));
        Assert.Equal(SonnetLow, Assert.IsType<SwitchStep.NextStart>(Plan(Task(wanted: SonnetLow) with { Sessions = [] })).To);
        // Live on the task but not listed yet (its process went a moment ago): resumed on the pick, nothing to stop.
        Assert.IsType<SwitchStep.AtNextWake>(Plan(Task(wanted: SonnetLow)));
        Assert.IsType<SwitchStep.NextStart>(SettingsSwitch.Plan(Board(Task(wanted: SonnetLow)), Task(wanted: SonnetLow), [], _ => false, _ => false));
    }

    // Review H2: session S moved from task A to task B. A pick on A touches nothing of S, now and at every later step.
    [Fact]
    public void A_session_that_moved_to_another_task_is_never_switched_for_the_old_one()
    {
        var b = new TaskItem(Guid.NewGuid(), "Task B", Maybe<Guid>.None, TaskStatus.InProgress, "", Maybe<string>.None, T0, T0, T0,
            [Session(Original, 10)], []);
        var a = Task(wanted: SonnetLow, sessions: Session(Original, 0, live: false));
        var board = new TaskBoard([a, b]);

        Assert.IsType<SwitchStep.NextStart>(SettingsSwitch.Plan(board, a, [Idle()], _ => true, _ => false));
        Assert.True(SettingsSwitch.SessionOf(board, a).HasNoValue);
        // Even an entry still marked live on A: the board says S works on B now.
        var stale = a with { Sessions = [Session(Original, 0)] };
        Assert.True(SettingsSwitch.SessionOf(new TaskBoard([stale, b]), stale).HasNoValue);
        // A request A had before S moved is given up, nothing issued.
        var requested = a with { Switching = Requested };
        var moved = Assert.IsType<SwitchAction.Abandon>(SettingsSwitch.Next(new TaskBoard([requested, b]), requested, [Idle()], T0, _ => true, _ => false, _ => false));
        Assert.Equal(SettingsSwitch.SessionMoved, moved.Why);
        // A joined switch whose original then joined B: the original is B's, never stopped for A.
        var joined = Switched();
        var bWithOriginal = b with { Sessions = [Session(Original, 10)] };
        var confirm = Assert.IsType<SwitchAction.Confirm>(SettingsSwitch.Next(new TaskBoard([joined, bWithOriginal]), joined,
            [Idle(), Idle(CopyId, "8c42135c")], T0.AddMinutes(20), _ => true, _ => false, _ => true));
        Assert.True(confirm.StopOriginal.HasNoValue);
    }

    // Review M3: only a background session Deskbar started is ever copied, so only Deskbar's own original is ever stopped.
    [Fact]
    public void A_background_session_Deskbar_did_not_start_is_read_only()
    {
        var notOurs = Task(wanted: SonnetLow, sessions: Session(Original, 0, launched: SessionSettings.None));

        Assert.Equal(SettingsSwitch.NotStartedByDeskbar, Assert.IsType<SwitchStep.ReadOnly>(Plan(notOurs, Idle())).Why);
    }

    // The session a comment goes to: never a replaced one; live first, then the latest joined.
    [Fact]
    public void Only_the_current_session_of_several_is_switched()
    {
        var older = Session("11111111-0000-0000-0000-000000000001", 0);
        var ended = Session("22222222-0000-0000-0000-000000000002", 5, live: false);
        var replaced = Session("33333333-0000-0000-0000-000000000003", 9) with { ReplacedAt = T0, ReplacedBy = "x" };
        var task = Task(wanted: SonnetLow, sessions: [older, ended, replaced]);

        Assert.Equal(older.Id, SettingsSwitch.SessionOf(new TaskBoard([task]), task).Value.Id);
        var step = Assert.IsType<SwitchStep.CopyNow>(Plan(task, Idle(older.Id, "11111111")));
        Assert.Equal(older.Id, step.Session.Id);
    }

    // --- the board --------------------------------------------------------------------------------------------------

    static TaskBoard Board(TaskItem task) => new([task]);

    static PendingSwitch Requested => new(Original, SonnetLow, T0);
    static PendingSwitch Issued => Requested with { Issued = T0.AddSeconds(1), CopyShortId = "8c42135c" };

    static TaskSession CopySession(int minute = 5) =>
        new(CopyId, Folder, T0.AddMinutes(minute), Maybe<DateTimeOffset>.None) { LaunchedWith = SonnetLow };

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
        Assert.True(Board(Task(TaskStatus.Closed)).Want(TaskId, SonnetLow, T0).IsFailure);
    }

    // Cancel while waiting for a pause: the pick and its request go; an issued switch is the app's to finish.
    [Fact]
    public void Unwant_drops_the_pick_and_a_request_but_not_an_issued_switch()
    {
        var waiting = Board(Task(wanted: SonnetLow) with { Switching = Requested }).Unwant(TaskId, T0).Value.Tasks.Single();
        Assert.True(waiting.Wanted.HasNoValue);
        Assert.True(waiting.Switching.HasNoValue);

        var issued = Board(Task(wanted: SonnetLow) with { Switching = Issued }).Unwant(TaskId, T0).Value.Tasks.Single();
        Assert.True(issued.Wanted.HasNoValue);
        Assert.Equal(Issued, issued.Switching.Value);
    }

    [Fact]
    public void A_request_is_replaced_by_a_later_pick_but_an_issued_switch_refuses_it()
    {
        var board = Board(Task(wanted: SonnetLow));

        var started = board.SwitchStarted(TaskId, Requested, T0).Value;
        Assert.Equal(Requested, started.Tasks.Single().Switching.Value);
        var later = Requested with { To = Opus };
        Assert.Equal(later, started.SwitchStarted(TaskId, later, T0).Value.Tasks.Single().Switching.Value);
        Assert.True(board.SwitchStarted(TaskId, Requested with { FromSession = "someone-else" }, T0).IsFailure);

        var filled = new SessionSettings("claude-opus-5-5", "low", Maybe<string>.None);
        var issued = started.SwitchIssued(TaskId, filled, T0.AddSeconds(1)).Value;
        Assert.Equal(Requested with { Issued = T0.AddSeconds(1), To = filled }, issued.Tasks.Single().Switching.Value);
        Assert.True(issued.SwitchStarted(TaskId, later, T0).IsFailure);
        Assert.True(issued.SwitchIssued(TaskId, filled, T0).IsFailure);

        Assert.Equal("8c42135c", issued.SwitchCopyNamed(TaskId, "8c42135c", T0).Value.Tasks.Single().Switching.Value.CopyShortId.Value);
        Assert.True(started.SwitchCopyNamed(TaskId, "8c42135c", T0).IsFailure);
    }

    // The task keeps its status: changing the model is not more work, so a Done task stays Done.
    [Fact]
    public void The_copy_joins_with_what_it_was_launched_with_and_the_original_is_replaced_by_it()
    {
        var now = T0.AddMinutes(5);
        var board = Board(Task(TaskStatus.Done, SonnetLow) with { Switching = Issued });

        var joined = board.SessionSwitched(TaskId, Original, CopySession(), now).Value;
        var task = joined.Tasks.Single();

        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.Equal(CopyId, task.Switching.Value.CopyId.Value);
        var original = task.Sessions.Single(s => s.Id == Original);
        Assert.Equal(now, original.Ended.Value);
        Assert.Equal(now, original.ReplacedAt.Value);
        Assert.Equal(CopyId, original.ReplacedBy.Value);
        Assert.Equal(SonnetLow, task.Sessions.Single(s => s.Id == CopyId).LaunchedWith);
        Assert.Equal(CopyId, SettingsSwitch.SessionOf(new TaskBoard([task]), task).Value.Id);
        // Joined twice (the app's look and the copy's own hook): the second changes nothing.
        Assert.Same(joined, joined.SessionSwitched(TaskId, Original, CopySession(), now).Value);
        Assert.True(board.SessionSwitched(TaskId, "not-on-it", CopySession(), now).IsFailure);
        Assert.True(Board(Task(wanted: SonnetLow) with { Switching = Requested }).SessionSwitched(TaskId, Original, CopySession(), now).IsFailure);

        var done = joined.SwitchConfirmed(TaskId, now).Value.Tasks.Single();
        Assert.True(done.Switching.HasNoValue);
        Assert.Equal(SonnetLow, done.Wanted.Value);
        Assert.IsType<SwitchStep.Nothing>(Plan(done, Idle(CopyId, "8c42135c")));
    }

    // If the copy had been adopted meanwhile (Deskbar found it before joining it), the empty adopted task goes.
    [Fact]
    public void A_copy_adopted_meanwhile_leaves_its_empty_adopted_task()
    {
        var now = T0.AddMinutes(5);
        var adopted = new TaskItem(Guid.NewGuid(), "proj", Maybe<Guid>.None, TaskStatus.InProgress, "", Maybe<string>.None, now, now, now,
            [new TaskSession(CopyId, Folder, now, Maybe<DateTimeOffset>.None)], []) { Adopted = true };
        var board = new TaskBoard([Task(wanted: SonnetLow) with { Switching = Issued }, adopted]);

        Assert.Equal(TaskId, board.SessionSwitched(TaskId, Original, CopySession(), now).Value.Tasks.Single().Id);
    }

    // The copy failed: back to the original, which never stopped; the copy's entry is the replaced one now.
    [Fact]
    public void A_revert_puts_the_task_back_on_the_original_and_drops_the_pick()
    {
        var now = T0.AddMinutes(6);
        var task = new TaskBoard([Switched()]).SwitchReverted(TaskId, now).Value.Tasks.Single();

        Assert.True(task.Switching.HasNoValue);
        Assert.True(task.Wanted.HasNoValue);
        var original = task.Sessions.Single(s => s.Id == Original);
        Assert.False(original.Replaced);
        Assert.True(original.ReplacedBy.HasNoValue);
        var copy = task.Sessions.Single(s => s.Id == CopyId);
        Assert.Equal(Original, copy.ReplacedBy.Value);
        Assert.Equal(now, copy.Ended.Value);
        // The list makes the original live again, and comments reach it: it is the task's session once more.
        var listed = new TaskBoard([task]).Reconcile([Idle()], now);
        Assert.Equal(Original, SettingsSwitch.SessionOf(listed, listed.Tasks.Single()).Value.Id);
        Assert.Equal(TaskId, listed.TaskOf(Original).Value.Id);
        Assert.True(Board(Task(wanted: SonnetLow) with { Switching = Issued }).SwitchReverted(TaskId, now).IsFailure);
    }

    [Fact]
    public void A_resume_that_kept_the_session_s_id_stores_the_new_settings_on_it()
    {
        var task = Board(Task(wanted: SonnetLow) with { Switching = Issued }).SessionRelaunched(TaskId, Original, SonnetLow, T0).Value.Tasks.Single();

        Assert.Equal(SonnetLow, Assert.Single(task.Sessions).LaunchedWith);
        Assert.True(task.Switching.HasNoValue);
    }

    // Given up for the pick's own sake (a bad pick, no copy): the pick goes. For a reason that says nothing against it
    // (a session in VS Code): the pick stays, for the next start. A closed task can still be cleared.
    [Fact]
    public void A_given_up_switch_clears_itself_and_the_pick_unless_told_to_keep_it()
    {
        var board = Board(Task(wanted: SonnetLow) with { Switching = Requested });

        var dropped = board.SwitchAbandoned(TaskId, T0.AddMinutes(2)).Value.Tasks.Single();
        Assert.True(dropped.Switching.HasNoValue);
        Assert.True(dropped.Wanted.HasNoValue);
        var kept = board.SwitchAbandoned(TaskId, T0.AddMinutes(2), keepWanted: true).Value.Tasks.Single();
        Assert.True(kept.Switching.HasNoValue);
        Assert.Equal(SonnetLow, kept.Wanted.Value);
        Assert.True(Board(Task(TaskStatus.Closed, SonnetLow) with { Switching = Requested }).SwitchAbandoned(TaskId, T0).Value.Tasks.Single().Switching.HasNoValue);
        var cleared = Board(Task());
        Assert.Same(cleared, cleared.SwitchAbandoned(TaskId, T0).Value);
    }

    // A session's own `task join` (its first message) or a SessionStart after a wake knows nothing of its flags.
    [Fact]
    public void A_session_joining_again_keeps_what_it_was_launched_with()
    {
        var board = Board(Task(sessions: Session(Original, 0, launched: SonnetLow)));

        var joined = board.Join(TaskId, new TaskSession(Original, Folder, T0.AddMinutes(1), Maybe<DateTimeOffset>.None), T0.AddMinutes(1)).Value;

        Assert.Equal(SonnetLow, joined.Tasks.Single().Sessions.Single().LaunchedWith);
    }

    // --- the switched-away session is left alone --------------------------------------------------------------------

    static TaskItem Switched(TaskStatus status = TaskStatus.InProgress) =>
        Board(Task(status, SonnetLow) with { Switching = Issued }).SessionSwitched(TaskId, Original, CopySession(), T0.AddMinutes(5))
            .Value.Tasks.Single();

    // Between the copy's join and the original's stop, both are listed; the original must not come back to life, and its
    // own SessionStart (a compact) must not put it back on the task beside the copy.
    [Fact]
    public void The_list_does_not_revive_the_original_while_it_waits_for_its_stop()
    {
        var board = new TaskBoard([Switched()]);

        var reconciled = board.Reconcile([Idle(), Idle(CopyId, "8c42135c")], T0.AddMinutes(6));

        var task = reconciled.Tasks.Single();
        Assert.False(task.Sessions.Single(s => s.Id == Original).IsLive);
        Assert.True(task.Sessions.Single(s => s.Id == CopyId).IsLive);
        Assert.True(reconciled.TaskOf(Original).HasNoValue);
        Assert.True(reconciled.SwitchingAway(Original));
        Assert.False(reconciled.SwitchingAway(CopyId));
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

    // --- Next: the switch's steps after the pick ----------------------------------------------------------------------

    static TaskItem WithSwitch(PendingSwitch pending) => Task(wanted: SonnetLow) with { Switching = pending };

    static ClaudeAgent Copy(string id = CopyId, string shortId = "8c42135c", int startedSecond = 2, string folder = Folder) =>
        Idle(id, shortId) with { Folder = folder, StartedAt = T0.AddSeconds(startedSecond) };

    static SwitchAction Next(TaskItem task, IReadOnlyCollection<ClaudeAgent> listed, DateTimeOffset? now = null,
        Func<TaskSession, bool>? resumable = null, Func<TaskSession, bool>? replied = null, TaskBoard? board = null) =>
        SettingsSwitch.Next(board ?? new TaskBoard([task]), task, listed, now ?? T0.AddSeconds(5), resumable ?? (_ => true), _ => false, replied ?? (_ => false));

    [Fact]
    public void Next_does_nothing_without_a_switch_and_gives_a_request_up_on_a_closed_task_keeping_the_pick()
    {
        Assert.IsType<SwitchAction.Nothing>(Next(Task(wanted: SonnetLow), [Idle()]));
        var closed = Assert.IsType<SwitchAction.Abandon>(Next(Task(TaskStatus.Closed, SonnetLow) with { Switching = Requested }, [Idle()]));
        Assert.True(closed.KeepWanted);
    }

    // REQUESTED: the pause is waited for, then the copy's resume is issued; a live session not listed is resumed at once.
    [Fact]
    public void A_request_is_issued_at_the_first_pause()
    {
        var task = WithSwitch(Requested);

        Assert.IsType<SwitchAction.Wait>(Next(task, [Idle() with { Busy = true, Status = "busy", State = "working" }]));
        Assert.IsType<SwitchAction.Wait>(Next(task, [Idle() with { Status = "waiting", State = "blocked" }]));
        var issue = Assert.IsType<SwitchAction.Issue>(Next(task, [Idle()]));
        Assert.Equal(Original, issue.From.Id);
        Assert.Equal(SonnetLow, issue.To);

        Assert.IsType<SwitchAction.Issue>(Next(task, []));
        Assert.Equal(SettingsSwitch.CannotResume, Assert.IsType<SwitchAction.Abandon>(Next(task, [], resumable: _ => false)).Why);
    }

    [Fact]
    public void A_request_for_a_session_that_moved_on_or_runs_in_its_own_window_is_given_up_keeping_the_pick()
    {
        var newer = Session("44444444-0000-0000-0000-000000000004", 3);
        var moved = Assert.IsType<SwitchAction.Abandon>(Next(WithSwitch(Requested) with { Sessions = [Session(Original, 0), newer] }, [Idle()]));
        Assert.Equal(SettingsSwitch.SessionMoved, moved.Why);
        Assert.True(moved.KeepWanted);
        // Ended meanwhile: not the task's session any more either.
        Assert.Equal(SettingsSwitch.SessionMoved,
            Assert.IsType<SwitchAction.Abandon>(Next(WithSwitch(Requested) with { Sessions = [Session(Original, 0, live: false)] }, [Idle()])).Why);

        var interactive = new ClaudeAgent(Original, Maybe<string>.None, Folder, false, false, "");
        var readOnly = Assert.IsType<SwitchAction.Abandon>(Next(WithSwitch(Requested), [interactive]));
        Assert.Equal(SettingsSwitch.NotDeskbars, readOnly.Why);
        Assert.True(readOnly.KeepWanted);
    }

    // ISSUED: the copy joins once listed, found by the short id Claude printed.
    [Fact]
    public void An_issued_switch_joins_the_copy_by_its_short_id()
    {
        var join = Assert.IsType<SwitchAction.Join>(Next(WithSwitch(Issued), [Idle(), Copy()]));
        Assert.Equal(CopyId, join.Copy.SessionId);
        // Another session in the same folder, started meanwhile (a VS Code panel), is never taken for it.
        Assert.IsType<SwitchAction.Wait>(Next(WithSwitch(Issued), [Idle(), Copy("99999999-0000-0000-0000-000000000009", "99999999")]));
    }

    // Review M3: no printed short id, no claim. The switch is given up at once, whatever runs in the folder; the original
    // is untouched and nothing is stopped.
    [Fact]
    public void Without_a_printed_short_id_nothing_is_claimed_and_the_switch_is_given_up_at_once()
    {
        var task = WithSwitch(Issued with { CopyShortId = Maybe<string>.None });

        var given = Assert.IsType<SwitchAction.Abandon>(Next(task, [Idle(), Copy()]));
        Assert.Equal(SettingsSwitch.NoShortId, given.Why);
        Assert.False(SettingsSwitch.MayClaim(task, Copy()));
        Assert.True(new TaskBoard([task]).SwitchingTaskFor(CopyId, _ => true, T0.AddSeconds(10)).HasNoValue);
    }

    // A session Claude Code held no saved options for is resumed under its own id with the flags (measured): no copy.
    [Fact]
    public void A_resume_that_kept_the_session_s_own_id_is_a_relaunch()
    {
        var sameId = Copy(Original, "eea5466c", startedSecond: 3);

        Assert.IsType<SwitchAction.Relaunched>(Next(WithSwitch(Issued with { CopyShortId = "eea5466c" }), [sameId]));
        // Claude printed another id: the original listed again is no relaunch of ours.
        Assert.IsType<SwitchAction.Wait>(Next(WithSwitch(Issued), [sameId]));
        // Listed since before the resume: the original itself, still running, not a relaunch.
        Assert.IsType<SwitchAction.Wait>(Next(WithSwitch(Issued with { CopyShortId = "eea5466c" }), [Copy(Original, "eea5466c", startedSecond: -10)]));
    }

    // The original was never stopped, so giving up loses nothing.
    [Fact]
    public void A_copy_that_never_appears_is_given_up_after_a_minute_and_the_pick_with_it()
    {
        var task = WithSwitch(Issued);
        var issued = Issued.Issued.Value;

        Assert.IsType<SwitchAction.Wait>(Next(task, [Idle()], issued + SettingsSwitch.RecoverWithin));
        var given = Assert.IsType<SwitchAction.Abandon>(Next(task, [Idle()], issued + SettingsSwitch.RecoverWithin + TimeSpan.FromSeconds(1)));
        Assert.Equal(SettingsSwitch.NoCopy, given.Why);
        Assert.False(given.KeepWanted);
    }

    // JOINED: the original runs on until the copy has answered once; then it is stopped at a read that shows it at a pause.
    [Fact]
    public void A_joined_switch_waits_for_the_copy_s_first_reply_then_stops_the_original()
    {
        var task = Switched();
        var both = new[] { Idle(), Idle(CopyId, "8c42135c") };

        Assert.Equal(SettingsSwitch.Confirming, Assert.IsType<SwitchAction.Wait>(Next(task, both)).Why);
        var confirm = Assert.IsType<SwitchAction.Confirm>(Next(task, both, replied: s => s.Id == CopyId));
        Assert.Equal("eea5466c", confirm.StopOriginal.Value);
        // The original no longer runs: nothing to stop.
        Assert.True(Assert.IsType<SwitchAction.Confirm>(Next(task, [Idle(CopyId, "8c42135c")], replied: _ => true)).StopOriginal.HasNoValue);
        // Neither runs any more (a finished task's sessions were stopped): the copy stays the task's session.
        Assert.IsType<SwitchAction.Confirm>(Next(task, []));
    }

    // Review H1: the copy has answered, but the original is busy (it woke on its own job, or someone types in its window),
    // waits on a question, or is still in its turn: it is not stopped, at any later read either, until a read shows a pause.
    [Fact]
    public void The_original_is_stopped_only_at_a_pause_never_while_busy()
    {
        var task = Switched();
        var copy = Idle(CopyId, "8c42135c");
        foreach (var original in new[]
                 {
                     Idle() with { Busy = true, Status = "busy", State = "working" },
                     Idle() with { Busy = true, Status = "busy", State = "done" },
                     Idle() with { Status = "waiting", State = "blocked" },
                     Idle() with { State = "working" },
                 })
            Assert.Equal(SettingsSwitch.OriginalStopsAtPause,
                Assert.IsType<SwitchAction.Wait>(Next(task, [original, copy], T0.AddDays(3), replied: _ => true)).Why);

        Assert.Equal("eea5466c", Assert.IsType<SwitchAction.Confirm>(Next(task, [Idle(), copy], replied: _ => true)).StopOriginal.Value);
    }

    // A model the account cannot use fails the copy's first turn (measured: idle, state failed). Once the copy has
    // answered, the switch is final, so a later failed turn is no reason to go back.
    [Fact]
    public void A_copy_that_failed_before_answering_reverts_to_the_original()
    {
        var failed = Idle(CopyId, "8c42135c") with { State = "failed" };

        var revert = Assert.IsType<SwitchAction.Revert>(Next(Switched(), [Idle(), failed]));
        Assert.Equal("8c42135c", revert.StopCopy);
        Assert.Equal(SettingsSwitch.CopyFailed, revert.Why);

        Assert.IsType<SwitchAction.Confirm>(Next(Switched(), [Idle(), failed], replied: _ => true));
    }

    // Review L3: a task closed during a switch. Before the copy answered: the copy goes, the original stays. After: the
    // copy stays and the original is stopped at its next pause. A copy that never joined is stopped and the switch given up.
    [Fact]
    public void A_task_closed_during_a_switch_never_leaves_a_replaced_session_running()
    {
        var closed = Switched() with { Status = TaskStatus.Closed };
        var both = new[] { Idle(), Idle(CopyId, "8c42135c") };

        Assert.Equal("8c42135c", Assert.IsType<SwitchAction.Revert>(Next(closed, both)).StopCopy);
        Assert.Equal("eea5466c", Assert.IsType<SwitchAction.Confirm>(Next(closed, both, replied: _ => true)).StopOriginal.Value);
        Assert.IsType<SwitchAction.Wait>(Next(closed, [Idle() with { Busy = true, Status = "busy", State = "working" }, both[1]], replied: _ => true));

        var issued = WithSwitch(Issued) with { Status = TaskStatus.Closed };
        Assert.Equal("8c42135c", Assert.IsType<SwitchAction.Discard>(Next(issued, [Idle(), Copy()])).StopCopy);
    }

    // Review M3: a switch stops only the copy Claude printed for our resume, or an original Deskbar itself started.
    [Fact]
    public void A_switch_may_stop_only_its_printed_copy_or_an_original_Deskbar_started()
    {
        var pending = Issued;
        var ours = Task(wanted: SonnetLow) with { Switching = pending };
        var notOurs = Task(wanted: SonnetLow, sessions: Session(Original, 0, launched: SessionSettings.None)) with { Switching = pending };

        Assert.True(SettingsSwitch.MayStop(ours, pending, "8c42135c"));
        Assert.True(SettingsSwitch.MayStop(ours, pending, "eea5466c"));
        Assert.False(SettingsSwitch.MayStop(notOurs, pending, "eea5466c"));
        Assert.False(SettingsSwitch.MayStop(ours, pending, "99999999"));
        Assert.False(SettingsSwitch.MayStop(ours, pending with { CopyShortId = Maybe<string>.None }, "8c42135c"));
        Assert.False(SettingsSwitch.MayStop(ours, pending, ""));
    }

    // --- the copy's own SessionStart hook, and the keeper's adoption ------------------------------------------------

    [Fact]
    public void The_copy_s_hook_finds_its_task_by_folder_short_id_and_time()
    {
        var board = new TaskBoard([WithSwitch(Issued)]);
        var now = T0.AddSeconds(10);

        Assert.Equal(TaskId, board.SwitchingTaskFor(CopyId, _ => true, now).Value.Id);
        Assert.True(board.SwitchingTaskFor("99999999-0000", _ => true, now).HasNoValue);
        Assert.True(board.SwitchingTaskFor(CopyId, _ => false, now).HasNoValue);
        Assert.True(board.SwitchingTaskFor(CopyId, _ => true, now + SettingsSwitch.RecoverWithin).HasNoValue);
        // A request not yet issued has no copy to wait for; a session a task holds is no copy.
        Assert.True(new TaskBoard([WithSwitch(Requested)]).SwitchingTaskFor(CopyId, _ => true, now).HasNoValue);
        Assert.True(board.SwitchingTaskFor(Original, _ => true, now).HasNoValue);
    }

    [Fact]
    public void The_keeper_does_not_adopt_a_copy_a_switch_can_still_claim()
    {
        var steps = SessionKeeper.Plan(new TaskBoard([WithSwitch(Issued)]), [Idle(), Copy()], ResumeLedger.Empty, _ => true, _ => false, T0.AddMinutes(1));
        Assert.DoesNotContain(steps, step => step is KeeperStep.Adopt);

        var stranger = Copy("99999999-0000-0000-0000-000000000009", "99999999");
        var adopted = SessionKeeper.Plan(new TaskBoard([WithSwitch(Issued)]), [Idle(), stranger], ResumeLedger.Empty, _ => true, _ => false, T0.AddMinutes(1));
        Assert.Contains(adopted, step => step is KeeperStep.Adopt adopt && adopt.Agent.SessionId == stranger.SessionId);
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
