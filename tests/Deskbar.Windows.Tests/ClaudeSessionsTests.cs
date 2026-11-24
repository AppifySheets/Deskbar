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
        public Func<string, Result<string>>? Stop { get; set; }

        // Sessions the list shows busy; every other row is idle.
        public HashSet<string> Busy { get; } = [];

        // A slow `claude --bg --resume`: the virtual clock moves on this far while the call runs, without running what
        // was due meanwhile, which then runs late, the way a poll that fires during a slow call is handled after it.
        public TimeSpan ResumeTakes { get; set; }

        // How long after the resume the daemon lists the woken session; none means at once.
        public TimeSpan? ListedAfter { get; set; }

        // Calls that fault instead of answering, as an exception the CLI did not turn into a Result would.
        public Func<IReadOnlyList<string>, bool>? Throws { get; set; }

        public IReadOnlyList<Call> Resumes => Calls.Where(c => c.Arguments is ["--bg", "--resume", ..]).ToList();
        public IReadOnlyList<Call> Reads => Calls.Where(c => c.Arguments is ["agents", "--json"]).ToList();
        public IReadOnlyList<Call> Stops => Calls.Where(c => c.Arguments is ["stop", _]).ToList();

        public Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
        {
            Calls.Add(new Call(clock.Now, arguments.ToList(), workingDirectory));
            if (Throws?.Invoke(arguments) == true)
                return Task.FromException<Result<string>>(new AggregateException(new InvalidOperationException("the tree could not be killed")));
            if (arguments is ["--bg", "--resume", ..] && ResumeTakes > TimeSpan.Zero) clock.Sleep(ResumeTakes.Ticks);
            return Task.FromResult(arguments switch
            {
                ["agents", "--json"] => Agents?.Invoke() ?? Result.Success(Json()),
                ["--bg", "--resume", var id, ..] => Resume?.Invoke(id) ?? Woken(id, workingDirectory),
                ["--bg", var message] => Start?.Invoke(message, workingDirectory) ?? Result.Failure<string>("no start scripted"),
                ["stop", var shortId] => Stop?.Invoke(shortId) ?? Stopped(shortId),
                _ => Result.Failure<string>($"unexpected claude call: {string.Join(" ", arguments)}"),
            });
        }

        // A resumed session is running again, so the next list shows it.
        Result<string> Woken(string id, string? folder)
        {
            var row = new Row(id, folder ?? "", "resumed");
            if (ListedAfter is { } after) clock.Schedule(after, () => Listed.Add(row));
            else Listed.Add(row);
            return Result.Success($"backgrounded · {id[..8]} · resumed");
        }

        // `claude stop` as measured: the session leaves the list at once and the CLI prints "stopped <short id>".
        Result<string> Stopped(string shortId)
        {
            Listed.RemoveAll(row => row.SessionId.StartsWith(shortId, StringComparison.Ordinal));
            return Result.Success($"stopped {shortId}");
        }

        string Json() => JsonSerializer.Serialize(Listed.Select(row => new
        {
            pid = 1,
            id = row.SessionId[..8],
            cwd = row.Folder,
            kind = "background",
            sessionId = row.SessionId,
            name = row.Name,
            status = Busy.Contains(row.SessionId) ? "busy" : "idle",
            state = Busy.Contains(row.SessionId) ? "working" : "done",
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
        public Func<TaskItem, bool> WindowOpen { get; set; } = _ => false;
        public ClaudeSessions? Sessions { get; private set; }
        public List<string> Traces { get; } = [];

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
            Sessions = new ClaudeSessions(Tasks, Manager, Cli, Clock, WorkspaceOfFolder, Home, background: Scheduler.Immediate, trace: Traces.Add,
                windowOpen: task => WindowOpen(task));
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

        // A task whose background session `claude agents` lists, idle unless the test marks it busy.
        public (Guid TaskId, string SessionId) Running(string title, string folder = @"E:\repos\Work")
        {
            var id = Guid.NewGuid().ToString();
            var task = Tasks.Create(title, Maybe<Guid>.None, Tasks.NewSession(id, folder)).Value;
            Cli.Listed.Add(new Row(id, folder, title));
            Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
            return (task.Id, id);
        }

        public void Dispose()
        {
            Sessions?.Dispose();
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
    }

    static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    // Ruling 18: nothing is resumed in the first two minutes after ClaudeSessions starts watching, so the first resume
    // of a session dead from the start comes with the poll at two minutes.
    static readonly TimeSpan Settle = SessionKeeper.Settle;

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

        // The fourth read is a real, empty list: now the session is known dead, and is woken once the two minutes since
        // the start have passed (Ruling 18).
        rig.RunTo(Seconds(45));
        Assert.True(rig.Task(taskId).Sessions.Single().Ended.HasValue);
        Assert.Empty(rig.Cli.Resumes);
        rig.RunTo(Settle);
        Assert.Equal(sessionId, rig.Cli.Resumes.Single().Arguments[2]);
    }

    // Ruling 12: with `claude` missing every read fails. The tasks keep what the hooks said, and the trace says so once
    // per run of failures, from the start or after a good read, rather than every 15 seconds.
    [Fact]
    public void An_unreadable_list_is_traced_once_per_run_of_failures()
    {
        using var rig = new Rig();
        var answers = new Queue<Result<string>>(
        [
            Result.Failure<string>("Could not run claude: not found."),
            Result.Failure<string>("Could not run claude: not found."),
            Result.Success("[]"),
            Result.Failure<string>("Could not run claude: not found."),
            Result.Failure<string>("Could not run claude: not found."),
        ]);
        rig.Cli.Agents = () => answers.Count > 0 ? answers.Dequeue() : Result.Success("[]");
        rig.Begin();

        rig.RunTo(Seconds(60));

        Assert.Equal(5, rig.Cli.Reads.Count);
        Assert.Equal(2, rig.Traces.Count(t => t.Contains("list not read")));
        Assert.Single(rig.Traces, t => t.Contains("list read again"));
    }

    // Ruling 11: a session started by hand joins or creates its own task on its first turn, through its hook, so a
    // stranger in the list is adopted only after it has been listed for two minutes. One that leaves the list and comes
    // back starts its two minutes again. Both were heard from as attended (Ruling 17, the next test).
    [Fact]
    public void A_session_is_adopted_only_after_two_minutes_listed()
    {
        using var rig = new Rig();
        var steady = Guid.NewGuid().ToString();
        var flaky = Guid.NewGuid().ToString();
        rig.Cli.Listed.Add(new Row(steady, @"E:\repos\Work", "Steady"));
        rig.Cli.Listed.Add(new Row(flaky, @"E:\repos\Work", "Flaky"));
        rig.Tasks.HeardAttended(steady);
        rig.Tasks.HeardAttended(flaky);
        rig.Begin();
        // The flaky one is gone for the reads at 60 s, then back from 75 s on.
        rig.Clock.Schedule(Seconds(50), () => rig.Cli.Listed.RemoveAll(r => r.SessionId == flaky));
        rig.Clock.Schedule(Seconds(70), () => rig.Cli.Listed.Add(new Row(flaky, @"E:\repos\Work", "Flaky")));

        bool Adopted(string id) => rig.Tasks.Board.Tasks.Any(t => t.Sessions.Any(s => s.Id == id));

        rig.RunTo(Seconds(105));
        Assert.False(Adopted(steady));
        Assert.False(Adopted(flaky));

        rig.RunTo(Seconds(120));
        Assert.True(Adopted(steady));
        Assert.True(rig.Tasks.Board.Tasks.Single(t => t.Sessions.Any(s => s.Id == steady)).Adopted);
        Assert.False(Adopted(flaky));

        rig.RunTo(Seconds(180));
        Assert.False(Adopted(flaky));
        rig.RunTo(Seconds(195));
        Assert.True(Adopted(flaky));
    }

    // Ruling 17: `claude agents` lists a `claude -p` run as "interactive", so the list cannot tell it from a person's
    // session. Only a session whose hooks said a person is at it (TaskService.HeardAttended, from SessionStart or
    // UserPromptSubmit) is adopted, still after its two minutes; one never heard from that way never is.
    [Fact]
    public void Only_a_session_heard_from_as_attended_is_adopted()
    {
        using var rig = new Rig();
        var person = Guid.NewGuid().ToString();
        var script = Guid.NewGuid().ToString();
        rig.Cli.Listed.Add(new Row(person, @"E:\repos\Work", "Person"));
        rig.Cli.Listed.Add(new Row(script, @"E:\repos\Work", "claude -p"));
        rig.Begin();
        // The person's next prompt comes a minute in, after Deskbar started.
        rig.Clock.Schedule(Seconds(60), () => rig.Tasks.HeardAttended(person));

        bool Adopted(string id) => rig.Tasks.Board.Tasks.Any(t => t.Sessions.Any(s => s.Id == id));

        rig.RunTo(Seconds(105));
        Assert.False(Adopted(person));
        rig.RunTo(Settle);
        Assert.True(Adopted(person));
        rig.RunTo(TimeSpan.FromMinutes(10));
        Assert.False(Adopted(script));
    }

    // --- resuming --------------------------------------------------------------------------------

    // Review Focus 2: after a reboot every open task's session is dead at once. They come back one at a time, five
    // seconds apart, each exactly once, from two minutes after the start (Ruling 18).
    [Fact]
    public void Resumes_run_one_at_a_time_five_seconds_apart()
    {
        using var rig = new Rig();
        var dead = Enumerable.Range(1, 20).Select(i => rig.Dead($"Task {i}")).ToList();
        rig.Begin();

        rig.RunTo(Settle + Seconds(150));

        var resumes = rig.Cli.Resumes;
        Assert.Equal(Enumerable.Range(0, 20).Select(i => Settle + Seconds(5 * i)), resumes.Select(c => rig.Since(c)));
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

        rig.RunTo(Settle + Seconds(10));

        var byId = rig.Cli.Resumes.ToDictionary(c => c.Arguments[2]);
        Assert.Equal(["--bg", "--resume", cut.SessionId, ContinueText], byId[cut.SessionId].Arguments);
        Assert.Equal(@"E:\repos\Cut", byId[cut.SessionId].Folder);
        Assert.Equal(["--bg", "--resume", done.SessionId], byId[done.SessionId].Arguments);
        Assert.Equal(@"E:\repos\Done", byId[done.SessionId].Folder);

        var cutAt = byId[cut.SessionId].At.ToLocalTime().ToString("HH:mm");
        var doneAt = byId[done.SessionId].At.ToLocalTime().ToString("HH:mm");
        Assert.Contains(rig.Task(cut.TaskId).Comments, c => c.ByDeskbar
            && c.Markdown == $"Resumed by Deskbar at {cutAt}, its last turn was cut off, so it was told to continue.");
        Assert.Contains(rig.Task(done.TaskId).Comments, c => c.ByDeskbar
            && c.Markdown == $"Resumed by Deskbar at {doneAt}.");
    }

    // Task 87f899ad (2026-11-24): a session that joined from the repo and then entered a worktree has its transcript
    // under the worktree's project folder, not the joined folder's. The keeper still finds it, resumes the session (in
    // the joined folder, from which `claude --bg --resume` found such a session when measured), reads its cut-off turn
    // from that file, and the thread's list calls it ended, not expired.
    [Fact]
    public void A_transcript_filed_under_a_worktree_still_counts()
    {
        using var rig = new Rig();
        var moved = rig.Dead("Went into a worktree", folder: @"E:\repos\Work", transcript: false);
        rig.WriteTranscript(moved.SessionId, @"E:\repos\Work\.claude\worktrees\fix", cutOff: true);
        rig.Begin();

        rig.RunTo(Settle + Seconds(10));

        var resume = Assert.Single(rig.Cli.Resumes);
        Assert.Equal(["--bg", "--resume", moved.SessionId, ContinueText], resume.Arguments);
        Assert.Equal(@"E:\repos\Work", resume.Folder);
        Assert.True(rig.Sessions!.TranscriptExists(rig.Task(moved.TaskId).Sessions.Single()));
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

        // Seven dead ones: the queue runs at 2:00, 2:05, 2:10, ... Just after the third, every session not yet woken
        // shows up in the list (woken by somebody else), so the fresh read before the one due at 2:15 drops it, and the
        // poll at 2:15 finds the rest listed.
        var dead = Enumerable.Range(1, 7).Select(i => rig.Dead($"Dead {i}")).ToList();
        rig.Begin();
        rig.Clock.Schedule(Settle + Seconds(12), () =>
            dead.Where(d => rig.Cli.Resumes.All(c => c.Arguments[2] != d.SessionId)).ToList()
                .ForEach(d => rig.Cli.Listed.Add(new Row(d.SessionId, @"E:\repos\Work", "woken elsewhere"))));

        rig.RunTo(Settle + Seconds(120));

        Assert.DoesNotContain(rig.Cli.Resumes, c => c.Arguments[2] == listedId);
        Assert.True(rig.Task(listedTask.Id).Sessions.Single().IsLive);
        Assert.Equal([Settle, Settle + Seconds(5), Settle + Seconds(10)], rig.Cli.Resumes.Select(c => rig.Since(c)));
    }

    // Review Focus 5: Petre closes a task while its resume waits in the queue. The resume is dropped.
    [Fact]
    public void A_queued_resume_for_a_task_closed_meanwhile_is_dropped()
    {
        using var rig = new Rig();
        var dead = Enumerable.Range(1, 3).Select(i => rig.Dead($"Dead {i}")).ToList();
        rig.Begin();

        rig.RunTo(Settle + Seconds(1));
        Assert.Single(rig.Cli.Resumes);
        var waiting = dead.Where(d => rig.Cli.Resumes.All(c => c.Arguments[2] != d.SessionId)).ToList();
        Assert.True(rig.Tasks.Close(waiting[0].TaskId).IsSuccess);

        rig.RunTo(Settle + Seconds(120));

        Assert.DoesNotContain(rig.Cli.Resumes, c => c.Arguments[2] == waiting[0].SessionId);
        Assert.Contains(rig.Cli.Resumes, c => c.Arguments[2] == waiting[1].SessionId);
        Assert.Equal(2, rig.Cli.Resumes.Count);
    }

    // Ruling 13: `claude --bg --resume` can take longer than the keeper's 30 s grace, which counts from the issue. The polls
    // that come meanwhile see the session neither listed nor in grace; they must not queue it again, and once it answers
    // it is left alone for the grace from the ANSWER, since the list read before then does not show it yet. One resume.
    [Fact]
    public void A_session_is_not_resumed_twice_when_a_slow_resume_answers_after_the_grace()
    {
        using var rig = new Rig();
        rig.Cli.ResumeTakes = Seconds(35);
        // The daemon lists it 20 s after the answer, 55 s after the resume went out.
        rig.Cli.ListedAfter = Seconds(20);
        var dead = rig.Dead("Slow");
        rig.Begin();

        rig.RunTo(Settle + Seconds(120));

        Assert.Equal([dead.SessionId], rig.Cli.Resumes.Select(c => c.Arguments[2]));
        // The polls kept going through it.
        Assert.True(rig.Cli.Reads.Count >= 8);
    }

    // Ruling 13: a call that faults instead of answering (ClaudeCli's kill can throw an AggregateException) is traced,
    // never thrown onto the dispatcher, and neither the polling nor the resume queue stops behind it.
    [Fact]
    public void A_throwing_cli_neither_stops_resuming_nor_escapes()
    {
        using var rig = new Rig();
        var first = rig.Dead("First");
        var second = rig.Dead("Second");
        var readsThrown = 0;
        var resumesThrown = 0;
        rig.Cli.Throws = arguments => arguments switch
        {
            ["agents", "--json"] when rig.Cli.Reads.Count == 1 && readsThrown++ == 0 => true,
            ["--bg", "--resume", ..] when resumesThrown++ == 0 => true,
            _ => false,
        };
        rig.Begin();

        // Not thrown out of the scheduler: AdvanceTo would rethrow it here.
        rig.RunTo(Settle + Seconds(60));

        Assert.Equal(1, readsThrown);
        Assert.True(rig.Cli.Reads.Count >= 4);
        // The read at 0 threw; the one at 2:00 queued both; the first resume threw, and the queue still went on.
        var resumed = rig.Cli.Resumes.Select(c => c.Arguments[2]).ToList();
        Assert.True(resumed.Count >= 2);
        Assert.Contains(first.SessionId, resumed);
        Assert.Contains(second.SessionId, resumed);
        Assert.Contains(rig.Traces, t => t.Contains("list read threw"));
        Assert.Contains(rig.Traces, t => t.Contains("threw") && t.Contains("resume of"));
    }

    // Review Focus 4, Ruling 10: a folder that was never trusted makes the resume fail. One clear message in the task's
    // thread, the CLI's own words, the task waiting on Petre with the sticky ResumeFailed question, and no retry at all,
    // however long passes. Once he acts on the task (here: answers, which puts it back in progress), it is judged afresh.
    [Fact]
    public void A_failed_resume_asks_once_and_is_not_retried()
    {
        using var rig = new Rig();
        const string untrusted = "claude exited with code 1.\nWorkspace not trusted. Run `claude` in E:\\repos\\Work once and accept the trust prompt, then retry.";
        rig.Cli.Resume = _ => Result.Failure<string>(untrusted);
        var dead = rig.Dead("Untrusted");
        rig.Begin();

        rig.RunTo(TimeSpan.FromMinutes(45));

        Assert.Equal([Settle], rig.Cli.Resumes.Select(c => rig.Since(c)));
        var task = rig.Task(dead.TaskId);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal(SessionKeeper.ResumeFailed, task.Question.GetValueOrDefault(""));
        var comment = Assert.Single(task.Comments);
        Assert.Contains("Workspace not trusted", comment.Markdown);

        // Petre trusts the folder and puts the task back in progress: the next poll tries again.
        rig.Cli.Resume = null;
        Assert.True(rig.Tasks.SetStatus(dead.TaskId, TaskStatus.InProgress).IsSuccess);
        rig.Run(Seconds(15));
        Assert.Equal(2, rig.Cli.Resumes.Count);
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
        rig.Tasks.HeardAttended(strangerId);
        rig.Begin();

        rig.RunTo(Seconds(120));

        Assert.Empty(rig.Cli.Resumes);
        Assert.True(rig.Task(dead.TaskId).Sessions.Single().Ended.HasValue);
        var adopted = Assert.Single(rig.Tasks.Board.Tasks, t => t.Sessions.Any(s => s.Id == strangerId));
        Assert.Equal("Started in a terminal", adopted.Title);
        Assert.Equal(workspace, adopted.WorkspaceId.GetValueOrDefault());
    }

    // Final review, item 2: the queue was decided on a poll that can be minutes old by a resume's turn. Right before each
    // resume the list is read again, and a session it shows (VS Code brought it back, Petre opened it) is not resumed:
    // that would start a copy.
    [Fact]
    public void A_session_listed_between_the_poll_and_its_turn_is_not_resumed()
    {
        using var rig = new Rig();
        var first = rig.Dead("First");
        var second = rig.Dead("Second");
        rig.Begin();
        // The poll at 2:00 queues both; the first goes at once, the second is due at 2:05. At 2:02 the second is back.
        rig.RunTo(Settle + Seconds(1));
        var woken = Assert.Single(rig.Cli.Resumes).Arguments[2];
        var other = woken == first.SessionId ? second.SessionId : first.SessionId;
        rig.Cli.Listed.Add(new Row(other, @"E:\repos\Work", "back by itself"));

        rig.RunTo(Settle + Seconds(60));

        Assert.Equal([woken], rig.Cli.Resumes.Select(c => c.Arguments[2]));
        // The fresh read happened at 2:05, before any poll could have seen it (the next is at 2:15).
        Assert.Contains(rig.Cli.Reads, c => rig.Since(c) == Settle + Seconds(5));
    }

    // Item 2: a fresh read that fails is "cannot tell", so the resume does not go, and neither does the rest of the queue;
    // the next good poll queues them again.
    [Fact]
    public void A_failed_fresh_read_drops_the_queue()
    {
        using var rig = new Rig();
        var dead = Enumerable.Range(1, 3).Select(i => rig.Dead($"Dead {i}")).ToList();
        rig.Begin();
        rig.RunTo(Settle + Seconds(1));
        Assert.Single(rig.Cli.Resumes);
        // Every read fails from now (2:01) to 2:20: the fresh read at 2:05 and the poll at 2:15.
        rig.Cli.Agents = () => Result.Failure<string>("claude exited with code 1.");
        rig.Clock.Schedule(Seconds(19), () => rig.Cli.Agents = null);

        rig.RunTo(Settle + Seconds(29));
        Assert.Single(rig.Cli.Resumes);

        // The poll at 2:30 reads again and the two left are queued and woken.
        rig.RunTo(Settle + Seconds(60));
        Assert.Equal(dead.Select(d => d.SessionId).Order(), rig.Cli.Resumes.Select(c => c.Arguments[2]).Order());
    }

    // Ruling 18: VS Code restores and reloads its Claude panels by itself a little after a restart, so nothing is
    // resumed in the first two minutes after ClaudeSessions starts, and a session that was listed and then went missing
    // waits two minutes from the first read that missed it.
    [Fact]
    public void A_session_is_resumed_only_after_two_minutes_missing()
    {
        using var rig = new Rig();
        var (taskId, sessionId) = rig.Dead("Comes and goes");
        rig.Cli.Listed.Add(new Row(sessionId, @"E:\repos\Work", "running"));
        rig.Begin();
        // It goes at 3:20, so the read at 3:30 is the first to miss it.
        rig.Clock.Schedule(Seconds(200), () => rig.Cli.Listed.RemoveAll(r => r.SessionId == sessionId));

        rig.RunTo(Seconds(325));
        Assert.Empty(rig.Cli.Resumes);
        Assert.False(rig.Task(taskId).HasLiveSession);

        rig.RunTo(Seconds(330));
        Assert.Equal([Seconds(330)], rig.Cli.Resumes.Select(c => rig.Since(c)));
    }

    // Ruling 18 again: one that comes back within the two minutes is never resumed, and its next absence starts the
    // two minutes afresh.
    [Fact]
    public void A_session_back_within_two_minutes_is_not_resumed()
    {
        using var rig = new Rig();
        var (_, sessionId) = rig.Dead("Reloaded");
        var row = new Row(sessionId, @"E:\repos\Work", "running");
        rig.Cli.Listed.Add(row);
        rig.Begin();
        // A window reload: gone from 2:30 to 3:30.
        rig.Clock.Schedule(Seconds(150), () => rig.Cli.Listed.Remove(row));
        rig.Clock.Schedule(Seconds(210), () => rig.Cli.Listed.Add(row));

        rig.RunTo(TimeSpan.FromMinutes(10));

        Assert.Empty(rig.Cli.Resumes);
    }

    // Ruling 20: a list that cannot be read is said on Manage, in one line, until a read works again.
    [Fact]
    public void An_unreadable_list_is_published_for_Manage_and_cleared_by_a_good_read()
    {
        using var rig = new Rig();
        var answers = new Queue<Result<string>>(
        [
            Result.Failure<string>("Could not run claude:\nThe system cannot find the file specified."),
            Result.Success("not json"),
            Result.Success("[]"),
        ]);
        rig.Cli.Agents = () => answers.Count > 0 ? answers.Dequeue() : Result.Success("[]");
        var sessions = rig.Begin();
        var seen = new List<Maybe<string>>();
        using var watch = sessions.ListProblem.Subscribe(seen.Add);

        rig.RunTo(Seconds(30));

        Assert.True(seen.Last().HasNoValue);
        var problems = seen.Where(p => p.HasValue).Select(p => p.Value).ToList();
        Assert.Contains(problems, p => p.Contains("The system cannot find the file specified.") && !p.Contains('\n'));
        Assert.True(problems.Count >= 1);

        // Turning the feature off clears it too.
        rig.Cli.Agents = () => Result.Failure<string>("gone");
        rig.RunTo(Seconds(45));
        Assert.True(seen.Last().HasValue);
        Assert.True(rig.Manager.SetClaudeTasks(false).IsSuccess);
        rig.Run(Seconds(1));
        Assert.True(seen.Last().HasNoValue);
    }

    // --- resuming at a click (Task 9) -------------------------------------------------------------

    // Going to a task whose session ended resumes it now (ClaudeSessions.ResumeNow), under the keeper's guards: a fresh
    // read first, one `claude --bg --resume` however many clicks come while it wakes, and once the list shows it, the
    // session as the list has it, so the caller can attach. A click on a session already listed resumes nothing (that
    // would start a copy). The click works with the automatic resume off: it is Petre asking, not the keeper.
    [Fact]
    public async Task Resuming_now_wakes_an_ended_session_once_and_hands_back_the_listed_one()
    {
        using var rig = new Rig();
        Assert.True(rig.Manager.SetClaudeResume(false).IsSuccess);
        var (taskId, sessionId) = rig.Dead("Dead one");
        rig.Cli.ListedAfter = Seconds(3);
        rig.Begin();
        rig.RunTo(Seconds(1));
        var session = rig.Task(taskId).Sessions.Single();

        var first = rig.Sessions!.ResumeNow(taskId, session);
        rig.Run(Seconds(1));
        // A second click while it wakes: no second resume.
        var second = rig.Sessions.ResumeNow(taskId, session);
        rig.Run(Seconds(10));

        Assert.True(first.IsCompleted && second.IsCompleted);
        Assert.Equal(sessionId, Assert.Single(rig.Cli.Resumes).Arguments[2]);
        Assert.Equal(sessionId, (await first).Value.SessionId);
        Assert.Equal(sessionId, (await second).Value.SessionId);
        Assert.Contains(rig.Task(taskId).Comments, c => c.Markdown.StartsWith("Resumed by Deskbar", StringComparison.Ordinal));

        // Listed now: a third click hands it back without resuming.
        var third = rig.Sessions.ResumeNow(taskId, session);
        rig.Run(Seconds(1));
        Assert.True(third.IsCompleted);
        Assert.True((await third).IsSuccess);
        Assert.Single(rig.Cli.Resumes);
    }

    // A list that cannot be read is "cannot tell" (Global Constraints): a click resumes nothing on it. A resume that
    // fails says so in the task's thread with Claude's words, as the keeper's own failed resume does.
    [Fact]
    public async Task Resuming_now_on_an_unreadable_list_or_a_failed_resume_says_why()
    {
        using var rig = new Rig();
        Assert.True(rig.Manager.SetClaudeResume(false).IsSuccess);
        var (taskId, _) = rig.Dead("Dead one");
        rig.Begin();
        rig.RunTo(Seconds(1));
        var session = rig.Task(taskId).Sessions.Single();

        rig.Cli.Agents = () => Result.Failure<string>("Could not run claude: not found.");
        var unreadable = rig.Sessions!.ResumeNow(taskId, session);
        rig.Run(Seconds(1));
        Assert.True(unreadable.IsCompleted);
        Assert.True((await unreadable).IsFailure);
        Assert.Empty(rig.Cli.Resumes);

        rig.Cli.Agents = null;
        rig.Cli.Resume = _ => Result.Failure<string>("Workspace not trusted. Run `claude` in E:\\repos\\Work once and accept the trust prompt, then retry.");
        var failed = rig.Sessions.ResumeNow(taskId, session);
        rig.Run(Seconds(1));
        Assert.True(failed.IsCompleted);
        Assert.Contains("Workspace not trusted", (await failed).Error);
        Assert.Contains(rig.Task(taskId).Comments, c => c.Markdown.Contains("Workspace not trusted"));
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
    // --- stopping finished sessions (task 87f899ad) ----------------------------------------------------------------

    // Petre: "i want to not have a stagnant session". A Done task's background session is stopped once it has been idle
    // for a minute of reads, with its short id, and the thread says so. The keeper never wakes it again on its own.
    [Fact]
    public void A_done_tasks_idle_session_is_stopped_after_a_minute_and_the_thread_says_so()
    {
        using var rig = new Rig();
        var (taskId, sessionId) = rig.Running("Finished work");
        Assert.True(rig.Tasks.SetStatus(taskId, TaskStatus.Done).IsSuccess);
        rig.Begin();

        rig.RunTo(Seconds(45));
        Assert.Empty(rig.Cli.Stops);

        rig.RunTo(SessionKeeper.StopAfter);
        var stop = Assert.Single(rig.Cli.Stops);
        Assert.Equal(["stop", sessionId[..8]], stop.Arguments);
        Assert.Equal(SessionKeeper.StopAfter, rig.Since(stop));
        Assert.Contains(rig.Task(taskId).Comments, c => c.Markdown.StartsWith("Stopped by Deskbar at"));

        // Gone from the list, and the task is Done: nothing resumes it, nothing stops it again.
        rig.RunTo(TimeSpan.FromMinutes(10));
        Assert.Single(rig.Cli.Stops);
        Assert.Empty(rig.Cli.Resumes);
        Assert.Equal(TaskStatus.Done, rig.Task(taskId).Status);
    }

    // "only those that have been finished": a session waiting on Petre, or working on any open task, is left running.
    [Theory]
    [InlineData(TaskStatus.Backlog)]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.WaitingOnYou)]
    public void An_open_tasks_idle_session_is_never_stopped(TaskStatus status)
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running("Still open");
        if (status != TaskStatus.InProgress) Assert.True(rig.Tasks.SetStatus(taskId, status).IsSuccess);
        rig.Begin();

        rig.RunTo(TimeSpan.FromMinutes(10));

        Assert.Empty(rig.Cli.Stops);
    }

    // The minute counts from the last read that saw it busy: a session that said `task done` is still finishing its turn.
    [Fact]
    public void A_busy_session_is_stopped_a_minute_after_it_goes_idle()
    {
        using var rig = new Rig();
        var (taskId, sessionId) = rig.Running("Wrapping up");
        Assert.True(rig.Tasks.SetStatus(taskId, TaskStatus.Done).IsSuccess);
        rig.Cli.Busy.Add(sessionId);
        rig.Begin();

        rig.RunTo(Seconds(90));
        Assert.Empty(rig.Cli.Stops);

        // Idle from the read at 105 s on: stopped by the read a minute after that.
        rig.Clock.Schedule(Seconds(10), () => rig.Cli.Busy.Remove(sessionId));
        rig.RunTo(Seconds(150));
        Assert.Empty(rig.Cli.Stops);
        rig.RunTo(Seconds(165));
        Assert.Equal(Seconds(165), rig.Since(Assert.Single(rig.Cli.Stops)));
    }

    // A window Deskbar attached to the session (Petre reading the answer of a Done task he opened) keeps it running;
    // the first read after the window closes stops it.
    [Fact]
    public void A_session_with_a_window_open_is_stopped_once_the_window_closes()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running("Being read");
        Assert.True(rig.Tasks.SetStatus(taskId, TaskStatus.Done).IsSuccess);
        var open = true;
        rig.WindowOpen = task => open && task.Id == taskId;
        rig.Begin();

        rig.RunTo(TimeSpan.FromMinutes(5));
        Assert.Empty(rig.Cli.Stops);

        open = false;
        rig.RunTo(TimeSpan.FromMinutes(5) + Seconds(15));
        Assert.Single(rig.Cli.Stops);
    }

    // A stop that fails leaves the session listed. It is traced, and sent again only after ten minutes, not every poll.
    [Fact]
    public void A_failed_stop_is_retried_after_ten_minutes_not_every_poll()
    {
        using var rig = new Rig();
        var (taskId, _) = rig.Running("Stubborn");
        Assert.True(rig.Tasks.SetStatus(taskId, TaskStatus.Done).IsSuccess);
        rig.Cli.Stop = _ => Result.Failure<string>("claude exited with code 1.");
        rig.Begin();

        rig.RunTo(TimeSpan.FromMinutes(10));
        Assert.Single(rig.Cli.Stops);
        Assert.Contains(rig.Traces, t => t.Contains("failed"));
        Assert.DoesNotContain(rig.Task(taskId).Comments, c => c.Markdown.StartsWith("Stopped by Deskbar"));

        rig.RunTo(TimeSpan.FromMinutes(11) + Seconds(15));
        Assert.Equal(2, rig.Cli.Stops.Count);
    }
}
