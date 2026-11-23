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

// ClaudeSessions: the App side of "tasks open Claude sessions" (design
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md, "Which sessions are live", "Every live session
// has a task", "Keeping open tasks running", "Starting a session from a task", "The setting").
//
// Everything runs on virtual time (TestScheduler) against a fake `claude` that answers from a list the test controls,
// so no test starts Claude, and the assertions are on the exact moments and arguments of every call. The fake
// behaves the way the probes measured: a resumed session shows up in `claude agents --json` afterwards, and
// `claude --bg` prints "backgrounded · <short id> · <name>".
public class ClaudeSessionsTests
{
    // A fixed moment so the ledger's arithmetic (now minus ten minutes) has room: a TestScheduler starts at year 1.
    static readonly DateTimeOffset Origin = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    const string ContinueText = "Continue from where you left off. Note: this session was resumed by Deskbar after it stopped.";

    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed record Call(DateTimeOffset At, IReadOnlyList<string> Arguments, string? Folder);

    sealed record Row(string SessionId, string Folder, string Name);

    // `claude` as the probes found it, driven by the test.
    sealed class FakeCli(TestScheduler clock) : IClaudeCli
    {
        public List<Call> Calls { get; } = [];

        // What `claude agents --json` lists right now.
        public List<Row> Listed { get; } = [];

        // Overrides, for the failures.
        public Func<Result<string>>? Agents { get; set; }
        public Func<string, Result<string>>? Resume { get; set; }
        public Func<string, string?, Result<string>>? Start { get; set; }

        public IReadOnlyList<Call> Resumes => Calls.Where(c => c.Arguments is ["--bg", "--resume", ..]).ToList();
        public IReadOnlyList<Call> Reads => Calls.Where(c => c.Arguments is ["agents", "--json"]).ToList();

        public Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
        {
            Calls.Add(new Call(clock.Now, arguments.ToList(), workingDirectory));
            return Task.FromResult(arguments switch
            {
                ["agents", "--json"] => Agents?.Invoke() ?? Result.Success(Json()),
                ["--bg", "--resume", var id, ..] => Resume?.Invoke(id) ?? Woken(id, workingDirectory),
                ["--bg", var message] => Start?.Invoke(message, workingDirectory) ?? Result.Failure<string>("no start scripted"),
                _ => Result.Failure<string>($"unexpected claude call: {string.Join(" ", arguments)}"),
            });
        }

        // A resumed session is running again, so the next list shows it.
        Result<string> Woken(string id, string? folder)
        {
            Listed.Add(new Row(id, folder ?? "", "resumed"));
            return Result.Success($"backgrounded · {id[..8]} · resumed");
        }

        string Json() => JsonSerializer.Serialize(Listed.Select(row => new
        {
            pid = 1,
            id = row.SessionId[..8],
            cwd = row.Folder,
            kind = "background",
            sessionId = row.SessionId,
            name = row.Name,
            status = "idle",
            state = "done",
        }));
    }

    sealed class Rig : IDisposable
    {
        public TestScheduler Clock { get; } = new();
        public WorkspaceManager Manager { get; }
        public TaskService Tasks { get; }
        public FakeCli Cli { get; }
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "deskbar-claude-sessions-" + Guid.NewGuid().ToString("N"));
        public Func<string, Maybe<Guid>> WorkspaceOfFolder { get; set; } = _ => Maybe<Guid>.None;
        public ClaudeSessions? Sessions { get; private set; }

        // When Begin ran: the first read happens then.
        public DateTimeOffset T0 { get; private set; }

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

        // Builds ClaudeSessions after the board is seeded, so the seeding is not raced by the first poll.
        public ClaudeSessions Begin()
        {
            T0 = Clock.Now;
            Sessions = new ClaudeSessions(Tasks, Manager, Cli, Clock, WorkspaceOfFolder, Home, background: Scheduler.Immediate);
            return Sessions;
        }

        public void Run(TimeSpan span) => Clock.AdvanceBy(span.Ticks);

        // The TestScheduler puts work scheduled for "now" one tick later, so every hop between threads (the call goes out
        // on `background`, its answer comes back on the scheduler) adds a tick. RunTo runs half a second past the moment
        // named so whatever was due then has run, and Since rounds a call's time to the millisecond after Begin.
        public void RunTo(TimeSpan sinceBegin) => Clock.AdvanceTo((T0 + sinceBegin + TimeSpan.FromSeconds(0.5)).UtcTicks);

        public TimeSpan Since(Call call) => TimeSpan.FromMilliseconds(Math.Round((call.At - T0).TotalMilliseconds));

        // A task whose session `claude agents` does not list: dead, with a transcript on disk (unless told otherwise).
        // A second passes on the clock, so tasks made one after another have different times.
        public (Guid TaskId, string SessionId) Dead(string title, string folder = @"E:\repos\Work", bool cutOff = false, bool transcript = true)
        {
            var id = Guid.NewGuid().ToString();
            var task = Tasks.Create(title, Maybe<Guid>.None, Tasks.NewSession(id, folder)).Value;
            if (transcript) WriteTranscript(id, folder, cutOff);
            Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
            return (task.Id, id);
        }

        public void WriteTranscript(string sessionId, string folder, bool cutOff)
        {
            var path = ClaudeTranscript.PathOf(Home, folder, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // A finished turn, and for a cut-off one a prompt nothing answered (the measured crash).
            string[] lines =
            [
                """{"type":"user","message":{"role":"user","content":"start"}}""",
                """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn"}}""",
                .. cutOff ? ["""{"type":"user","message":{"role":"user","content":"and then"}}"""] : Array.Empty<string>(),
            ];
            File.WriteAllLines(path, lines);
        }

        public TaskItem Task(Guid id) => Tasks.Board.Tasks.Single(t => t.Id == id);

        public void Dispose()
        {
            Sessions?.Dispose();
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
    }

    static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    // --- polling ---------------------------------------------------------------------------------

    // Spec, "Which sessions are live": the list is read on a timer while the feature is on. Off stops the timer; on again
    // reads at once rather than waiting out an interval.
    [Fact]
    public void It_polls_every_15_seconds_only_while_Claude_tasks_are_on()
    {
        using var rig = new Rig();
        rig.Begin();

        rig.RunTo(Seconds(45));
        Assert.Equal([Seconds(0), Seconds(15), Seconds(30), Seconds(45)], rig.Cli.Reads.Select(c => rig.Since(c)));

        Assert.True(rig.Manager.SetClaudeTasks(false).IsSuccess);
        rig.RunTo(Seconds(120));
        Assert.Equal(4, rig.Cli.Reads.Count);

        Assert.True(rig.Manager.SetClaudeTasks(true).IsSuccess);
        rig.Run(Seconds(0.1));
        Assert.Equal(5, rig.Cli.Reads.Count);
        rig.Run(Seconds(15.1));
        Assert.Equal(6, rig.Cli.Reads.Count);
    }

    // Global Constraints: an unreadable or unknown `claude agents --json` means "cannot tell". Nothing is ended,
    // nothing resumed, nothing adopted; the board is the very same object. The first good read then acts.
    [Fact]
    public void A_failed_or_unknown_read_changes_nothing()
    {
        using var rig = new Rig();
        var (taskId, sessionId) = rig.Dead("Dead one");
        var answers = new Queue<Result<string>>(
        [
            Result.Failure<string>("claude exited with code 1."),
            Result.Success("""{"agents":[]}"""),
            Result.Success("""[{"id":"12345678"}]"""),
        ]);
        rig.Cli.Agents = () => answers.Count > 0 ? answers.Dequeue() : Result.Success("[]");
        var board = rig.Tasks.Board;
        rig.Begin();

        rig.RunTo(Seconds(30));

        Assert.Equal(3, rig.Cli.Reads.Count);
        Assert.Same(board, rig.Tasks.Board);
        Assert.Empty(rig.Cli.Resumes);

        // The fourth read is a real, empty list: now the session is known dead and is woken.
        rig.RunTo(Seconds(45));
        Assert.True(rig.Task(taskId).Sessions.Single().Ended.HasValue);
        Assert.Equal(sessionId, rig.Cli.Resumes.Single().Arguments[2]);
    }

    // --- resuming --------------------------------------------------------------------------------

    // Review Focus 2: after a reboot every open task's session is dead at once. They come back one at a time, five
    // seconds apart, each exactly once.
    [Fact]
    public void Resumes_run_one_at_a_time_five_seconds_apart()
    {
        using var rig = new Rig();
        var dead = Enumerable.Range(1, 20).Select(i => rig.Dead($"Task {i}")).ToList();
        rig.Begin();

        rig.RunTo(Seconds(150));

        var resumes = rig.Cli.Resumes;
        Assert.Equal(Enumerable.Range(0, 20).Select(i => Seconds(5 * i)), resumes.Select(c => rig.Since(c)));
        Assert.Equal(dead.Select(d => d.SessionId).Order(), resumes.Select(c => c.Arguments[2]).Order());
    }

    // The continue message goes only to a session whose last turn was cut off; the arguments are exact, and the resume
    // runs in the session's own folder (transcripts are filed per folder). Each resume says so in its task's thread.
    [Fact]
    public void A_resume_passes_the_continue_message_only_for_a_cut_off_turn()
    {
        using var rig = new Rig();
        var cut = rig.Dead("Cut off", folder: @"E:\repos\Cut", cutOff: true);
        var done = rig.Dead("Finished", folder: @"E:\repos\Done", cutOff: false);
        rig.Begin();

        rig.RunTo(Seconds(10));

        var byId = rig.Cli.Resumes.ToDictionary(c => c.Arguments[2]);
        Assert.Equal(["--bg", "--resume", cut.SessionId, ContinueText], byId[cut.SessionId].Arguments);
        Assert.Equal(@"E:\repos\Cut", byId[cut.SessionId].Folder);
        Assert.Equal(["--bg", "--resume", done.SessionId], byId[done.SessionId].Arguments);
        Assert.Equal(@"E:\repos\Done", byId[done.SessionId].Folder);

        var cutAt = byId[cut.SessionId].At.ToLocalTime().ToString("HH:mm");
        var doneAt = byId[done.SessionId].At.ToLocalTime().ToString("HH:mm");
        Assert.Contains(rig.Task(cut.TaskId).Comments, c => c.SessionId.HasNoValue
            && c.Markdown == $"Resumed by Deskbar at {cutAt}, its last turn was cut off, so it was told to continue.");
        Assert.Contains(rig.Task(done.TaskId).Comments, c => c.SessionId.HasNoValue
            && c.Markdown == $"Resumed by Deskbar at {doneAt}.");
    }

    // Global Constraints: `claude --bg --resume` on a listed session starts a COPY. A listed session is never resumed,
    // even when the board had it ended, and a queued resume whose session turns up in the list before its turn is dropped.
    [Fact]
    public void A_listed_session_is_never_resumed_even_when_its_task_wants_it()
    {
        using var rig = new Rig();
        var listedId = Guid.NewGuid().ToString();
        var listedTask = rig.Tasks.Create("Listed", Maybe<Guid>.None, rig.Tasks.NewSession(listedId, @"E:\repos\Work")).Value;
        rig.WriteTranscript(listedId, @"E:\repos\Work", cutOff: true);
        rig.Tasks.SessionEnded(listedId);
        rig.Cli.Listed.Add(new Row(listedId, @"E:\repos\Work", "Listed"));

        // Seven dead ones: the queue runs at 0, 5, 10, ... 30 s. Just after the third, every session not yet woken
        // shows up in the list (woken by somebody else), so the read at 15 s drops what is left in the queue.
        var dead = Enumerable.Range(1, 7).Select(i => rig.Dead($"Dead {i}")).ToList();
        rig.Begin();
        rig.Clock.Schedule(Seconds(12), () =>
            dead.Where(d => rig.Cli.Resumes.All(c => c.Arguments[2] != d.SessionId)).ToList()
                .ForEach(d => rig.Cli.Listed.Add(new Row(d.SessionId, @"E:\repos\Work", "woken elsewhere"))));

        rig.RunTo(Seconds(120));

        Assert.DoesNotContain(rig.Cli.Resumes, c => c.Arguments[2] == listedId);
        Assert.True(rig.Task(listedTask.Id).Sessions.Single().IsLive);
        // 0, 5 and 10 went; the one due at 15 may go if it runs before that read; nothing after.
        Assert.InRange(rig.Cli.Resumes.Count, 3, 4);
        Assert.All(rig.Cli.Resumes, c => Assert.True(rig.Since(c) <= Seconds(15)));
    }

    // Review Focus 5: Petre closes a task while its resume waits in the queue. The resume is dropped.
    [Fact]
    public void A_queued_resume_for_a_task_closed_meanwhile_is_dropped()
    {
        using var rig = new Rig();
        var dead = Enumerable.Range(1, 3).Select(i => rig.Dead($"Dead {i}")).ToList();
        rig.Begin();

        rig.Run(Seconds(1));
        Assert.Single(rig.Cli.Resumes);
        var waiting = dead.Where(d => rig.Cli.Resumes.All(c => c.Arguments[2] != d.SessionId)).ToList();
        Assert.True(rig.Tasks.Close(waiting[0].TaskId).IsSuccess);

        rig.RunTo(Seconds(120));

        Assert.DoesNotContain(rig.Cli.Resumes, c => c.Arguments[2] == waiting[0].SessionId);
        Assert.Contains(rig.Cli.Resumes, c => c.Arguments[2] == waiting[1].SessionId);
        Assert.Equal(2, rig.Cli.Resumes.Count);
    }

    // Review Focus 4: a folder that was never trusted makes the resume fail. One clear message in the task's thread,
    // the CLI's own words, and no retry loop: the session is left alone for the loop window, and a retry that fails
    // the same way says nothing new.
    [Fact]
    public void A_failed_resume_comments_once_on_the_task_and_is_not_retried_until_the_next_window()
    {
        using var rig = new Rig();
        const string untrusted = "claude exited with code 1.\nWorkspace not trusted. Run `claude` in E:\\repos\\Work once and accept the trust prompt, then retry.";
        rig.Cli.Resume = _ => Result.Failure<string>(untrusted);
        var dead = rig.Dead("Untrusted");
        rig.Begin();

        rig.RunTo(TimeSpan.FromMinutes(9));
        Assert.Equal([Seconds(0)], rig.Cli.Resumes.Select(c => rig.Since(c)));
        Assert.Single(rig.Task(dead.TaskId).Comments, c => c.Markdown.Contains("Workspace not trusted"));
        Assert.DoesNotContain(rig.Task(dead.TaskId).Comments, c => c.Markdown.StartsWith("Resumed by Deskbar"));

        rig.RunTo(TimeSpan.FromMinutes(10) + Seconds(1));
        Assert.Equal([Seconds(0), TimeSpan.FromMinutes(10)], rig.Cli.Resumes.Select(c => rig.Since(c)));
        Assert.Single(rig.Task(dead.TaskId).Comments, c => c.Markdown.Contains("Workspace not trusted"));
    }

    // The setting: with automatic resume off, the board still follows the list (a dead session shows ended) and an
    // unknown live session still gets a task, on the workspace of the editor showing its folder.
    [Fact]
    public void With_resume_off_nothing_is_resumed_but_adoption_and_liveness_still_run()
    {
        using var rig = new Rig();
        Assert.True(rig.Manager.SetClaudeResume(false).IsSuccess);
        var workspace = Guid.NewGuid();
        rig.WorkspaceOfFolder = folder => folder == @"E:\repos\Other" ? workspace : Maybe<Guid>.None;
        var dead = rig.Dead("Dead");
        var strangerId = Guid.NewGuid().ToString();
        rig.Cli.Listed.Add(new Row(strangerId, @"E:\repos\Other", "Started in a terminal"));
        rig.Begin();

        rig.RunTo(Seconds(120));

        Assert.Empty(rig.Cli.Resumes);
        Assert.True(rig.Task(dead.TaskId).Sessions.Single().Ended.HasValue);
        var adopted = Assert.Single(rig.Tasks.Board.Tasks, t => t.Sessions.Any(s => s.Id == strangerId));
        Assert.Equal("Started in a terminal", adopted.Title);
        Assert.Equal(workspace, adopted.WorkspaceId.GetValueOrDefault());
    }

    // --- starting --------------------------------------------------------------------------------

    // Spec, "Starting a session from a task": `claude --bg "<first message>"` in the folder, the short id read from its
    // output, then the full id found in the list, and the task joined by ClaudeSessions itself. A background session's
    // hooks may come late (Task 1 note), so the join must not wait for them. The session is not adopted as a second task.
    [Fact]
    public async Task Start_runs_claude_bg_with_the_first_message_in_the_folder_and_joins_the_task_itself()
    {
        using var rig = new Rig();
        const string fullId = "6785e6b5-0d8f-4a51-9a3e-1c2b3d4e5f60";
        var note = rig.Tasks.AddNote("Fix the login page", Maybe<Guid>.None).Value;
        rig.Cli.Start = (_, folder) =>
        {
            // The daemon lists the new session a few seconds later.
            rig.Clock.Schedule(Seconds(3), () => rig.Cli.Listed.Add(new Row(fullId, folder ?? "", "Fix the login page")));
            return Result.Success("backgrounded · 6785e6b5 · Fix the login page");
        };
        var sessions = rig.Begin();

        var started = sessions.Start(note, @"E:\repos\Work");
        rig.RunTo(Seconds(30));

        Assert.True(started.IsCompleted);
        Assert.Equal(fullId, (await started).Value);
        var start = Assert.Single(rig.Cli.Calls, c => c.Arguments is ["--bg", _]);
        Assert.Equal(["--bg", ClaudeStart.FirstMessage(note)], start.Arguments);
        Assert.Equal(@"E:\repos\Work", start.Folder);
        var joined = rig.Task(note.Id).Sessions.Single();
        Assert.Equal((fullId, @"E:\repos\Work"), (joined.Id, joined.Folder));
        Assert.Single(rig.Tasks.Board.Tasks);
    }

    // An untrusted folder: `claude --bg` exits 1 with its reason on stderr, and that reason is what Start returns.
    [Fact]
    public async Task Start_in_an_untrusted_folder_fails_with_Claude_s_message()
    {
        using var rig = new Rig();
        var note = rig.Tasks.AddNote("Fix the login page", Maybe<Guid>.None).Value;
        rig.Cli.Start = (_, _) => Result.Failure<string>(
            "claude exited with code 1.\nWorkspace not trusted. Run `claude` in E:\\repos\\New once and accept the trust prompt, then retry.");
        var sessions = rig.Begin();

        var started = sessions.Start(note, @"E:\repos\New");
        rig.RunTo(Seconds(30));

        Assert.True(started.IsCompleted);
        var result = await started;
        Assert.True(result.IsFailure);
        Assert.Contains("Workspace not trusted", result.Error);
        Assert.Empty(rig.Task(note.Id).Sessions);
    }
}
