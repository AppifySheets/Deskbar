using System.IO;
using System.Reactive.Concurrency;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;

namespace Deskbar.Windows.Tests;

// ClaudeSessions' switch of a task session's model, effort and permission mode (ClaudeSessions.Switch.cs; the rules are
// SettingsSwitch's, in Core). Everything runs on virtual time against a fake `claude` that behaves as measured on
// 2026-11-26: a resume with flags of a background session starts a COPY under a new id and prints it on the usual
// "backgrounded" line; a session with no saved options is resumed under its own id instead; a model the account cannot
// use leaves the copy listed "failed"; `claude stop` takes a session off the list. No test starts Claude.
public class ClaudeSessionsSwitchTests
{
    static readonly DateTimeOffset Origin = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    const string Folder = @"E:\repos\Work";

    static readonly SessionSettings SonnetLow = new("sonnet", "low", Maybe<string>.None);
    static readonly SessionSettings HaikuPlan = new("haiku", Maybe<string>.None, "plan");
    static readonly SessionSettings Opus = new("opus", Maybe<string>.None, Maybe<string>.None);

    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed record Call(DateTimeOffset At, IReadOnlyList<string> Arguments, string? Folder);

    sealed class Row(string sessionId, string folder, DateTimeOffset startedAt)
    {
        public string SessionId { get; } = sessionId;
        public string Folder { get; } = folder;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public bool Background { get; set; } = true;
        public string Status { get; set; } = "idle";
        public string State { get; set; } = "done";
    }

    sealed class FakeCli(TestScheduler clock) : IClaudeCli
    {
        public List<Call> Calls { get; } = [];
        public List<Row> Listed { get; } = [];
        public bool AgentsFail { get; set; }
        public Func<string, Result<string>>? Resume { get; set; }
        public Func<string, bool> StopFails { get; set; } = _ => false;
        // Heard while a stop is out, before it answers (what Cancel meets then).
        public Action<string>? OnStop { get; set; }
        // A session Claude Code holds no saved options for: the resume keeps its id (measured with an ended `-p` one).
        public bool KeepsId { get; set; }
        int made;

        public IReadOnlyList<Call> Resumes => Calls.Where(c => c.Arguments is ["--bg", "--resume", ..]).ToList();
        public IReadOnlyList<Call> Stops => Calls.Where(c => c.Arguments is ["stop", _]).ToList();
        public IReadOnlyList<Call> Starts => Calls.Where(c => c.Arguments is ["--bg", "--model", ..]).ToList();
        public IReadOnlyList<Call> Changes => Calls.Where(c => c.Arguments is not ["agents", "--json"]).ToList();

        public Row? Last { get; private set; }

        public Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
        {
            Calls.Add(new Call(clock.Now, arguments.ToList(), workingDirectory));
            if (arguments is ["stop", var stopping]) OnStop?.Invoke(stopping);
            return Task.FromResult(arguments switch
            {
                ["agents", "--json"] => AgentsFail ? Result.Failure<string>("claude exited with code 1.") : Result.Success(Json()),
                ["--bg", "--resume", var id, ..] => Resume?.Invoke(id) ?? Launched(KeepsId ? id : NewId(), workingDirectory),
                ["--bg", "--model", ..] => Launched(NewId(), workingDirectory),
                ["stop", var shortId] => StopFails(shortId) ? Result.Failure<string>("claude exited with code 1.") : Stopped(shortId),
                _ => Result.Failure<string>($"unexpected claude call: {string.Join(" ", arguments)}"),
            });
        }

        string NewId() => $"{0xc0000000 + ++made:x8}-0000-4000-8000-000000000000";

        Result<string> Launched(string id, string? folder)
        {
            Listed.RemoveAll(row => row.SessionId == id);
            Last = new Row(id, folder ?? "", clock.Now);
            Listed.Add(Last);
            return Result.Success($"backgrounded · {id[..8]}");
        }

        Result<string> Stopped(string shortId)
        {
            Listed.RemoveAll(row => row.SessionId.StartsWith(shortId, StringComparison.Ordinal));
            return Result.Success($"stopped {shortId}");
        }

        string Json() => JsonSerializer.Serialize(Listed.Select(row => row.Background
            ? (object)new
            {
                pid = 1, id = row.SessionId[..8], cwd = row.Folder, kind = "background", sessionId = row.SessionId, name = "",
                status = row.Status, state = row.State, startedAt = row.StartedAt.ToUnixTimeMilliseconds(),
            }
            : new { pid = 1, cwd = row.Folder, kind = "interactive", sessionId = row.SessionId, status = row.Status }));
    }

    sealed class Rig : IDisposable
    {
        public TestScheduler Clock { get; } = new();
        public WorkspaceManager Manager { get; }
        public TaskService Tasks { get; }
        public FakeCli Cli { get; }
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "deskbar-claude-switch-" + Guid.NewGuid().ToString("N"));
        public ClaudeSessions? Sessions { get; private set; }
        public List<string> Traces { get; } = [];

        public Rig()
        {
            Clock.AdvanceTo(Origin.UtcTicks);
            var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
            desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
            Manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
            Assert.True(Manager.Start().IsSuccess);
            Tasks = new TaskService(new MemoryStore(), () => Clock.Now);
            Cli = new FakeCli(Clock);
        }

        // A restart builds a new ClaudeSessions over the same board, as a Deskbar that closed and started again does.
        public ClaudeSessions Begin(Func<TaskItem, bool>? windowOpen = null)
        {
            Sessions?.Dispose();
            Sessions = new ClaudeSessions(Tasks, Manager, Cli, Clock, _ => Maybe<Guid>.None, Home, background: Scheduler.Immediate,
                trace: Traces.Add, folderExists: _ => true, windowOpen: windowOpen);
            Run(Seconds(1));
            return Sessions;
        }

        public void Run(TimeSpan span) => Clock.AdvanceBy(span.Ticks);

        // A task whose background session runs, idle, with a transcript on disk.
        public (Guid TaskId, string SessionId) Running(string title = "Picker")
        {
            var (taskId, id) = Task(title);
            Cli.Listed.Add(new Row(id, Folder, Clock.Now.AddHours(-1)));
            return (taskId, id);
        }

        // Started by Deskbar (its LaunchedWith set, as Start sets it) unless `ours` is false.
        public (Guid TaskId, string SessionId) Task(string title = "Picker", bool transcript = true, bool ours = true)
        {
            var id = Guid.NewGuid().ToString();
            var session = ours ? Tasks.NewSession(id, Folder) with { LaunchedWith = Opus } : Tasks.NewSession(id, Folder);
            var task = Tasks.Create(title, Maybe<Guid>.None, session).Value;
            if (transcript) Transcript(id, """{"type":"assistant","timestamp":"2026-11-26T08:00:00.000Z","message":{"model":"claude-opus-5-5","stop_reason":"end_turn"}}""");
            return (task.Id, id);
        }

        public void Transcript(string sessionId, params string[] lines)
        {
            var path = ClaudeTranscript.PathOf(Home, Folder, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, lines);
        }

        // The copy's first reply, written now.
        public void Replied(string sessionId) =>
            Transcript(sessionId, """{"type":"assistant","timestamp":""" + "\"" + Clock.Now.AddSeconds(1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
                + "\"" + ""","message":{"model":"claude-sonnet-5-5","stop_reason":"end_turn"}}""");

        public SwitchStep Pick(Guid taskId, SessionSettings settings)
        {
            var picked = Sessions!.Pick(taskId, settings);
            Run(Seconds(1));
            Assert.True(picked.IsCompleted);
            return picked.Result.Value;
        }

        public TaskItem Of(Guid id) => Tasks.Board.Tasks.Single(t => t.Id == id);

        public void Dispose()
        {
            Sessions?.Dispose();
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
    }

    static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    // A task the virtual clock has already run to its end: its answer, read without waiting.
    static T Completed<T>(Task<T> task)
    {
        Assert.True(task.IsCompleted);
        return task.Result;
    }

    static readonly TimeSpan Poll = ClaudeSessions.Poll;

    // --- the switch, as it should go ---------------------------------------------------------------------------------

    // Copy first, join, and stop the original only once the copy has answered: no moment has the conversation stopped.
    [Fact]
    public void An_idle_session_is_copied_first_and_its_original_stopped_only_after_the_copy_answers()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Begin();

        Assert.IsType<SwitchStep.CopyNow>(rig.Pick(taskId, SonnetLow));

        var resume = Assert.Single(rig.Cli.Resumes);
        Assert.Equal(ClaudeStart.SwitchArguments(original, SonnetLow), resume.Arguments);
        Assert.Equal(Folder, resume.Folder);
        var copy = rig.Cli.Last!.SessionId;
        var task = rig.Of(taskId);
        Assert.Equal(SonnetLow, task.Sessions.Single(s => s.Id == copy).LaunchedWith);
        Assert.Equal(copy, task.Sessions.Single(s => s.Id == original).ReplacedBy.Value);
        Assert.Equal(copy, SettingsSwitch.SessionOf(rig.Tasks.Board, task).Value.Id);
        Assert.Contains(task.Comments, c => c.ByDeskbar && c.Markdown == SettingsSwitch.JoinedNote(SonnetLow));
        Assert.Single(rig.Tasks.Board.Tasks);

        // Polls pass; the copy has not answered, so the original runs on.
        rig.Run(Poll * 3);
        Assert.Empty(rig.Cli.Stops);
        Assert.True(rig.Of(taskId).Switching.HasValue);

        rig.Replied(copy);
        rig.Run(Poll);
        var stop = Assert.Single(rig.Cli.Stops);
        Assert.Equal(["stop", original[..8]], stop.Arguments);
        Assert.True(stop.At > resume.At);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
        Assert.Equal(SonnetLow, rig.Of(taskId).Wanted.Value);
    }

    // Stopping a busy session would lose its step: the switch waits, stored, for the first pause.
    [Fact]
    public void A_busy_session_is_switched_at_its_first_pause_and_never_stopped_mid_step()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        var row = rig.Cli.Listed.Single();
        row.Status = "busy";
        row.State = "working";
        rig.Begin();

        Assert.Equal(SettingsSwitch.Busy, Assert.IsType<SwitchStep.Wait>(rig.Pick(taskId, SonnetLow)).Why);
        rig.Run(Poll * 2);
        Assert.Empty(rig.Cli.Changes);
        Assert.Equal(original, rig.Of(taskId).Switching.Value.FromSession);

        // Waiting on a permission prompt is no pause either.
        row.Status = "waiting";
        row.State = "blocked";
        rig.Run(Poll);
        Assert.Empty(rig.Cli.Changes);

        row.Status = "idle";
        row.State = "done";
        rig.Run(Poll);
        Assert.Single(rig.Cli.Resumes);
        Assert.Empty(rig.Cli.Stops);
    }

    // A session never resumed or stopped by anything but a pick: a pick stored by other means, and every poll after it,
    // touch nothing.
    [Fact]
    public void Nothing_but_a_pick_starts_a_switch()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running();
        Assert.True(rig.Tasks.Want(taskId, SonnetLow).IsSuccess);
        rig.Begin();

        rig.Run(TimeSpan.FromMinutes(5));

        Assert.Empty(rig.Cli.Changes);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
    }

    // --- the failure cases --------------------------------------------------------------------------------------------

    // Claude refused the resume (an untrusted folder, `claude` missing): nothing started, so the original goes on.
    [Fact]
    public void A_refused_resume_leaves_the_original_running_and_says_why()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Cli.Resume = _ => Result.Failure<string>("claude exited with code 1. Workspace not trusted.");
        rig.Begin();

        rig.Pick(taskId, SonnetLow);

        var task = rig.Of(taskId);
        Assert.Empty(rig.Cli.Stops);
        Assert.True(task.Switching.HasNoValue);
        Assert.True(task.Wanted.HasNoValue);
        Assert.Equal(original, SettingsSwitch.SessionOf(rig.Tasks.Board, task).Value.Id);
        Assert.False(task.Sessions.Single().Replaced);
        Assert.Contains(task.Comments, c => c.ByDeskbar && c.Markdown.Contains("Workspace not trusted", StringComparison.Ordinal));
    }

    // A model the account cannot use: the copy starts, then fails its first turn (measured). The task goes back to the
    // original, which never stopped, and the failed copy is stopped.
    [Fact]
    public void A_copy_that_fails_its_first_turn_hands_the_task_back_to_the_original()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, new SessionSettings("claude-bogus-model-9", Maybe<string>.None, Maybe<string>.None));
        var copy = rig.Cli.Last!;

        copy.State = "failed";
        rig.Run(Poll);

        Assert.Equal(["stop", copy.SessionId[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        var task = rig.Of(taskId);
        Assert.Equal(original, SettingsSwitch.SessionOf(rig.Tasks.Board, task).Value.Id);
        Assert.False(task.Sessions.Single(s => s.Id == original).Replaced);
        Assert.True(task.Switching.HasNoValue);
        Assert.True(task.Wanted.HasNoValue);
        Assert.Contains(task.Comments, c => c.ByDeskbar && c.Markdown == SettingsSwitch.CopyFailed);
        Assert.Contains(rig.Cli.Listed, row => row.SessionId == original);
    }

    // Deskbar closed after the resume went out and before it joined the copy: the next Deskbar joins it, adopts nothing,
    // and resumes nothing again.
    [Fact]
    public void A_restarted_Deskbar_joins_the_copy_of_a_switch_it_had_issued()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        var copyId = "c00000aa-0000-4000-8000-000000000000";
        Assert.True(rig.Tasks.SwitchStarted(taskId, new PendingSwitch(original, SonnetLow, rig.Clock.Now)).IsSuccess);
        Assert.True(rig.Tasks.SwitchIssued(taskId, SonnetLow).IsSuccess);
        Assert.True(rig.Tasks.SwitchCopyNamed(taskId, copyId[..8]).IsSuccess);
        rig.Cli.Listed.Add(new Row(copyId, Folder, rig.Clock.Now));
        rig.Run(Seconds(5));

        rig.Begin();
        rig.Run(TimeSpan.FromMinutes(3));

        var task = rig.Of(taskId);
        Assert.Equal(copyId, task.Switching.Value.CopyId.Value);
        Assert.Equal(copyId, SettingsSwitch.SessionOf(rig.Tasks.Board, task).Value.Id);
        Assert.Single(rig.Tasks.Board.Tasks);
        Assert.Empty(rig.Cli.Resumes);
        Assert.Empty(rig.Cli.Stops);
    }

    // Review M3: Deskbar closed before Claude named the copy. A session in the original's folder is never claimed by its
    // folder: the switch is given up at once, the original untouched, nothing stopped, and the thread says so.
    [Fact]
    public void A_restarted_Deskbar_never_claims_an_unnamed_copy_by_its_folder()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        Assert.True(rig.Tasks.SwitchStarted(taskId, new PendingSwitch(original, SonnetLow, rig.Clock.Now)).IsSuccess);
        Assert.True(rig.Tasks.SwitchIssued(taskId, SonnetLow).IsSuccess);
        rig.Run(Seconds(1));
        var stranger = "c00000bb-0000-4000-8000-000000000000";
        rig.Cli.Listed.Add(new Row(stranger, Folder, rig.Clock.Now));

        rig.Begin();

        var task = rig.Of(taskId);
        Assert.True(task.Switching.HasNoValue);
        Assert.DoesNotContain(task.Sessions, s => s.Id == stranger);
        Assert.Equal(original, SettingsSwitch.SessionOf(rig.Tasks.Board, task).Value.Id);
        Assert.Contains(task.Comments, c => c.ByDeskbar && c.Markdown == SettingsSwitch.NoShortId);
        Assert.Empty(rig.Cli.Changes);
    }

    // Review M3: Claude printed no short id for the resume: the switch is given up at once, nothing claimed or stopped.
    [Fact]
    public void A_resume_that_prints_no_short_id_gives_the_switch_up_at_once()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Cli.Resume = _ => Result.Success("started");
        rig.Begin();

        rig.Pick(taskId, SonnetLow);
        rig.Run(Poll * 2);

        Assert.True(rig.Of(taskId).Switching.HasNoValue);
        Assert.Empty(rig.Cli.Stops);
        Assert.Equal(original, SettingsSwitch.SessionOf(rig.Tasks.Board, rig.Of(taskId)).Value.Id);
        Assert.Contains(rig.Of(taskId).Comments, c => c.ByDeskbar && c.Markdown == SettingsSwitch.NoShortId);
    }

    // No copy appeared within a minute: given up. The original was never stopped, so it goes on as it was.
    [Fact]
    public void A_copy_that_never_appears_is_given_up_and_the_original_never_stopped()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        Assert.True(rig.Tasks.Want(taskId, SonnetLow).IsSuccess);
        Assert.True(rig.Tasks.SwitchStarted(taskId, new PendingSwitch(original, SonnetLow, rig.Clock.Now)).IsSuccess);
        Assert.True(rig.Tasks.SwitchIssued(taskId, SonnetLow).IsSuccess);
        Assert.True(rig.Tasks.SwitchCopyNamed(taskId, "c00000cc").IsSuccess);

        rig.Begin();
        rig.Run(SettingsSwitch.RecoverWithin + Poll);

        var task = rig.Of(taskId);
        Assert.True(task.Switching.HasNoValue);
        Assert.True(task.Wanted.HasNoValue);
        Assert.Equal(original, SettingsSwitch.SessionOf(rig.Tasks.Board, task).Value.Id);
        Assert.Contains(task.Comments, c => c.ByDeskbar && c.Markdown == SettingsSwitch.NoCopy);
        Assert.Empty(rig.Cli.Changes);
    }

    // Deskbar closed while the joined copy waited for its first reply: the next Deskbar confirms it once it has answered.
    [Fact]
    public void A_restarted_Deskbar_stops_the_original_once_the_joined_copy_has_answered()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        var copy = rig.Cli.Last!.SessionId;
        rig.Sessions!.Dispose();
        rig.Replied(copy);

        rig.Begin();

        Assert.Equal(["stop", original[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
    }

    // The original's stop failed: tried again after a while, never on every poll, and the switch stays until it works.
    [Fact]
    public void A_failed_stop_of_the_original_is_tried_again_later_not_on_every_poll()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        rig.Replied(rig.Cli.Last!.SessionId);
        var failing = true;
        rig.Cli.StopFails = _ => failing;

        rig.Run(Poll * 4);
        Assert.Single(rig.Cli.Stops);
        Assert.True(rig.Of(taskId).Switching.HasValue);

        failing = false;
        rig.Run(TimeSpan.FromMinutes(10));
        Assert.Equal(2, rig.Cli.Stops.Count);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
    }

    // A second pick while the first switch runs: no second resume meanwhile; once the first is done, the second begins on
    // the copy and goes out at the next read.
    [Fact]
    public void A_second_pick_while_a_switch_runs_follows_it_on_the_copy()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        var copy = rig.Cli.Last!.SessionId;

        Assert.Equal(SettingsSwitch.UnderWay, Assert.IsType<SwitchStep.Wait>(rig.Pick(taskId, HaikuPlan)).Why);
        Assert.Single(rig.Cli.Resumes);

        // The next read stops the original and begins the second switch; the one after it sends it out.
        rig.Replied(copy);
        rig.Run(Poll * 3);

        Assert.Equal(2, rig.Cli.Resumes.Count);
        Assert.Equal(ClaudeStart.SwitchArguments(copy, HaikuPlan), rig.Cli.Resumes[1].Arguments);
        Assert.Equal(HaikuPlan, SettingsSwitch.SessionOf(rig.Tasks.Board, rig.Of(taskId)).Value.LaunchedWith);
    }

    // VS Code or a terminal: Deskbar cannot restart it, so nothing runs; the pick is kept for the next start.
    [Fact]
    public void A_session_in_its_own_window_is_left_alone_and_the_pick_kept()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running();
        rig.Cli.Listed.Single().Background = false;
        rig.Begin();

        Assert.IsType<SwitchStep.ReadOnly>(rig.Pick(taskId, SonnetLow));
        rig.Run(Poll * 2);

        Assert.Empty(rig.Cli.Changes);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
        Assert.Equal(SonnetLow, rig.Of(taskId).Wanted.Value);
    }

    // Review H2: a session that has ended is not the task's session: nothing is resumed or stopped, the pick waits for the
    // next start.
    [Fact]
    public void An_ended_session_is_not_resumed_on_a_pick()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Task();
        rig.Begin();

        Assert.IsType<SwitchStep.NextStart>(rig.Pick(taskId, SonnetLow));
        rig.Run(Poll * 2);

        Assert.Empty(rig.Cli.Changes);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
        Assert.Equal(SonnetLow, rig.Of(taskId).Wanted.Value);
    }

    // Review H2: session S moved from task A to task B. A pick on A touches nothing of S: no copy, no stop.
    [Fact]
    public void A_pick_on_a_task_whose_session_moved_to_another_task_touches_nothing_of_it()
    {
        using var rig = new Rig();
        var (a, s) = rig.Running("Task A");
        var b = rig.Tasks.AddNote("Task B", Maybe<Guid>.None).Value.Id;
        // Later than its join to A, as a real move is.
        rig.Run(Seconds(1));
        Assert.True(rig.Tasks.Join(b, rig.Tasks.NewSession(s, Folder) with { LaunchedWith = Opus }).IsSuccess);
        rig.Begin();

        Assert.IsType<SwitchStep.NextStart>(rig.Pick(a, SonnetLow));
        rig.Run(Poll * 3);

        Assert.Empty(rig.Cli.Changes);
        Assert.True(rig.Of(a).Switching.HasNoValue);
        Assert.Equal(s, SettingsSwitch.SessionOf(rig.Tasks.Board, rig.Of(b)).Value.Id);
    }

    // A session Claude Code holds no saved options for comes back under the id Claude printed, its own, with the flags
    // (measured): the same session, now on the pick. Seen through by a restarted Deskbar.
    [Fact]
    public void A_resume_that_keeps_the_session_s_id_is_the_same_session_on_the_pick()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Task();
        Assert.True(rig.Tasks.SwitchStarted(taskId, new PendingSwitch(original, SonnetLow, rig.Clock.Now)).IsSuccess);
        Assert.True(rig.Tasks.SwitchIssued(taskId, SonnetLow).IsSuccess);
        Assert.True(rig.Tasks.SwitchCopyNamed(taskId, original[..8]).IsSuccess);
        rig.Cli.Listed.Add(new Row(original, Folder, rig.Clock.Now));

        rig.Begin();

        var task = rig.Of(taskId);
        Assert.Equal(SonnetLow, Assert.Single(task.Sessions).LaunchedWith);
        Assert.True(task.Switching.HasNoValue);
        Assert.Contains(task.Comments, c => c.ByDeskbar && c.Markdown == SettingsSwitch.RelaunchedNote(SonnetLow));
    }

    // Review H1: the copy has answered, but the original is busy (it woke on its own between the reply and the stop): it
    // is not stopped, at any read while it stays busy, however long; it is stopped at the first read that shows a pause.
    [Fact]
    public void The_original_is_stopped_only_at_a_read_that_shows_it_at_a_pause()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        rig.Replied(rig.Cli.Last!.SessionId);
        var row = rig.Cli.Listed.Single(r => r.SessionId == original);
        row.Status = "busy";
        row.State = "working";

        rig.Run(TimeSpan.FromHours(2));
        Assert.Empty(rig.Cli.Stops);
        Assert.Equal(SettingsSwitch.OriginalStopsAtPause, rig.Sessions!.PickRow(taskId)!.PhaseWords);

        row.Status = "waiting";
        row.State = "blocked";
        rig.Run(Poll * 2);
        Assert.Empty(rig.Cli.Stops);

        row.Status = "idle";
        row.State = "done";
        rig.Run(Poll);
        Assert.Equal(["stop", original[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
    }

    // Review M3: every `claude stop` of a switch is checked in one place. A joined switch whose original Deskbar did not
    // start (it could only get there by a hand-edited file) never has the original stopped: refused, and traced.
    [Fact]
    public void A_switch_never_stops_a_session_Deskbar_did_not_start()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Task(ours: false);
        rig.Cli.Listed.Add(new Row(original, Folder, rig.Clock.Now.AddHours(-1)));
        var copy = "c00000dd-0000-4000-8000-000000000000";
        Assert.True(rig.Tasks.SwitchStarted(taskId, new PendingSwitch(original, SonnetLow, rig.Clock.Now)).IsSuccess);
        Assert.True(rig.Tasks.SwitchIssued(taskId, SonnetLow).IsSuccess);
        Assert.True(rig.Tasks.SwitchCopyNamed(taskId, copy[..8]).IsSuccess);
        Assert.True(rig.Tasks.SessionSwitched(taskId, original, rig.Tasks.NewSession(copy, Folder) with { LaunchedWith = SonnetLow }).IsSuccess);
        rig.Cli.Listed.Add(new Row(copy, Folder, rig.Clock.Now));
        rig.Run(Seconds(1));
        rig.Replied(copy);

        rig.Begin();
        rig.Run(Poll * 2);

        Assert.Empty(rig.Cli.Stops);
        Assert.Contains(rig.Traces, line => line.Contains($"stop of {original[..8]} refused", StringComparison.Ordinal));
    }

    // Review L3: a task closed during a switch, before the copy answered: the copy is stopped, the original stays.
    [Fact]
    public void A_task_closed_before_the_copy_answered_stops_the_copy_and_keeps_the_original()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        var copy = rig.Cli.Last!.SessionId;

        // Closed as "Stop Claude and close" does: a live session keeps a task from closing otherwise.
        Assert.True(rig.Tasks.Close(taskId, stoppingClaude: true).IsSuccess);
        rig.Run(Poll);

        Assert.Equal(["stop", copy[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        Assert.Contains(rig.Cli.Listed, r => r.SessionId == original);
        Assert.False(rig.Of(taskId).Sessions.Single(s => s.Id == original).Replaced);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
    }

    // Review L3: closed after the copy answered: the copy stays the task's session, and the original is stopped at its next
    // pause even though the task is closed.
    [Fact]
    public void A_task_closed_after_the_copy_answered_still_stops_the_original_at_its_pause()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        var copy = rig.Cli.Last!.SessionId;
        rig.Replied(copy);
        var row = rig.Cli.Listed.Single(r => r.SessionId == original);
        row.Status = "busy";

        // Closed as "Stop Claude and close" does: a live session keeps a task from closing otherwise.
        Assert.True(rig.Tasks.Close(taskId, stoppingClaude: true).IsSuccess);
        rig.Run(Poll * 2);
        Assert.Empty(rig.Cli.Stops);

        row.Status = "idle";
        rig.Run(Poll);
        Assert.Equal(["stop", original[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
        Assert.Equal(copy, rig.Of(taskId).Sessions.Single(s => s.Id == original).ReplacedBy.Value);
    }

    // Review L3: a task deleted during a switch, before the copy answered: the copy is still stopped.
    [Fact]
    public void A_task_deleted_before_the_copy_answered_still_stops_the_copy()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        var copy = rig.Cli.Last!.SessionId;
        rig.Run(Poll);

        Assert.True(rig.Tasks.Delete(taskId).IsSuccess);
        rig.Run(Poll);

        Assert.Equal(["stop", copy[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        rig.Run(Poll * 2);
        Assert.Single(rig.Cli.Stops);
    }

    // Expired: nothing to resume. The pick waits for the next start, which passes it and records it on the session.
    [Fact]
    public void A_pick_on_an_expired_session_is_used_by_the_next_start()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Task(transcript: false);
        rig.Begin();

        Assert.IsType<SwitchStep.NextStart>(rig.Pick(taskId, SonnetLow));
        Assert.Empty(rig.Cli.Changes);

        var started = rig.Sessions!.Start(rig.Of(taskId), Folder);
        rig.Run(Seconds(1));

        var start = Assert.Single(rig.Cli.Starts);
        Assert.Equal(ClaudeStart.Arguments(rig.Of(taskId), SonnetLow), start.Arguments);
        Assert.Equal(SonnetLow, rig.Of(taskId).Sessions.Single(s => s.Id == Completed(started).Value).LaunchedWith);
    }

    // A list that cannot be read: nothing is switched; the pick is kept.
    [Fact]
    public void An_unreadable_list_switches_nothing()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running();
        rig.Begin();
        rig.Cli.AgentsFail = true;

        var picked = rig.Sessions!.Pick(taskId, SonnetLow);
        rig.Run(Seconds(1));

        Assert.True(Completed(picked).IsFailure);
        Assert.Empty(rig.Cli.Changes);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
        Assert.Equal(SonnetLow, rig.Of(taskId).Wanted.Value);
    }

    // --- the picker row's side: its words, Cancel, and the window ----------------------------------------------------

    [Fact]
    public void The_row_follows_the_switch_through_its_phases()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running();
        var row = rig.Cli.Listed.Single();
        row.Status = "busy";
        row.State = "working";
        rig.Begin();

        rig.Pick(taskId, SonnetLow);
        var waiting = rig.Sessions!.PickRow(taskId)!;
        Assert.Equal(PickPhase.Waiting, waiting.Phase);
        Assert.Equal("Sonnet · low", waiting.ModelWords);
        Assert.True(waiting.CanCancel);

        row.Status = "idle";
        row.State = "done";
        rig.Run(Poll);
        var joined = rig.Sessions.PickRow(taskId)!;
        Assert.Equal(PickPhase.Confirming, joined.Phase);
        Assert.Equal(SessionPicker.ConfirmingWords, joined.PhaseWords);

        rig.Replied(rig.Cli.Last!.SessionId);
        rig.Run(Poll);
        var done = rig.Sessions.PickRow(taskId)!;
        Assert.Equal(PickPhase.Settled, done.Phase);
        Assert.Equal("Sonnet · low", done.ModelWords);
    }

    // Review M2: once the copy has answered the switch is final. Cancel is refused then, whatever the row showed, and
    // while the original's stop is out; nothing is stopped by it, and the copy stays the task's session.
    [Fact]
    public void Cancel_is_refused_once_the_copy_has_answered_or_while_the_original_stops()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        var row = rig.Cli.Listed.Single();
        rig.Begin();
        rig.Pick(taskId, SonnetLow);
        var copy = rig.Cli.Last!.SessionId;
        rig.Replied(copy);
        row.Status = "busy";

        var refused = rig.Sessions!.CancelPick(taskId);
        Assert.Equal(ClaudeSessions.SwitchIsFinal, refused.Error);
        Assert.Empty(rig.Cli.Stops);
        Assert.Equal(copy, SettingsSwitch.SessionOf(rig.Tasks.Board, rig.Of(taskId)).Value.Id);
        Assert.False(rig.Sessions.PickRow(taskId)!.CanCancel);

        // While the original's stop is out, a Cancel is refused too.
        Result? duringStop = null;
        rig.Cli.OnStop = shortId => { if (shortId == original[..8]) duringStop = rig.Sessions.CancelPick(taskId); };
        row.Status = "idle";
        rig.Run(Poll);
        Assert.Equal(ClaudeSessions.SwitchIsFinal, duringStop!.Value.Error);
        Assert.Equal(["stop", original[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        Assert.Equal(copy, SettingsSwitch.SessionOf(rig.Tasks.Board, rig.Of(taskId)).Value.Id);
    }

    // Cancel while waiting drops the pick; Cancel once joined goes back to the original, which never stopped, and stops
    // the copy; while the copy is being made there is nothing to cancel yet.
    [Fact]
    public void Cancel_drops_a_waiting_pick_or_goes_back_from_a_joined_one()
    {
        using var rig = new Rig();
        var (taskId, original) = rig.Running();
        var row = rig.Cli.Listed.Single();
        row.Status = "busy";
        rig.Begin();
        rig.Pick(taskId, SonnetLow);

        Assert.True(rig.Sessions!.CancelPick(taskId).IsSuccess);
        Assert.True(rig.Of(taskId).Switching.HasNoValue);
        Assert.True(rig.Of(taskId).Wanted.HasNoValue);

        row.Status = "idle";
        rig.Pick(taskId, SonnetLow);
        var copy = rig.Cli.Last!.SessionId;
        Assert.True(rig.Sessions.CancelPick(taskId).IsSuccess);
        rig.Run(Seconds(1));

        Assert.Equal(["stop", copy[..8]], Assert.Single(rig.Cli.Stops).Arguments);
        Assert.Equal(original, SettingsSwitch.SessionOf(rig.Tasks.Board, rig.Of(taskId)).Value.Id);
        Assert.Contains(rig.Cli.Listed, r => r.SessionId == original);

        Assert.True(rig.Tasks.SwitchStarted(taskId, new PendingSwitch(original, SonnetLow, rig.Clock.Now)).IsSuccess);
        Assert.True(rig.Tasks.SwitchIssued(taskId, SonnetLow).IsSuccess);
        Assert.True(rig.Sessions.CancelPick(taskId).IsFailure);
    }

    // The original's stop closes a window attached to it: one is opened on the copy a moment later, only when one was open.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_window_on_the_original_is_reopened_on_the_copy(bool windowWasOpen)
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running();
        var reopened = new List<Guid>();
        rig.Begin(windowOpen: _ => windowWasOpen).ReopenWindow = reopened.Add;
        rig.Pick(taskId, SonnetLow);
        rig.Replied(rig.Cli.Last!.SessionId);

        rig.Run(Poll + Seconds(3));
        Assert.Single(rig.Cli.Stops);
        Assert.Equal(windowWasOpen ? [taskId] : [], reopened);
    }
}
