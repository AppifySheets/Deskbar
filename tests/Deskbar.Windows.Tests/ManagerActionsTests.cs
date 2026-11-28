using System.IO;
using System.Reactive.Concurrency;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;

namespace Deskbar.Windows.Tests;

// One click opens the manager (ManagerActions; design docs/superpowers/specs/2026-11-25-manager-session-design.md, "The
// button" and "Where it runs and what it is told"). Over the shared two-workspace harness (GEPHA in front), a real
// ClaudeSessions and ClaudeTaskActions, a fake `claude` that answers the way the probes measured, and seams that RECORD
// the windows instead of opening them: no test starts Claude, wt or a terminal. Everything runs on virtual time.
public class ManagerActionsTests
{
    static readonly DateTimeOffset Origin = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Today = new(2026, 11, 25);

    const string Untrusted = "claude exited with code 1.\nWorkspace not trusted. Run `claude` in the folder once and accept the trust prompt, then retry.";

    // The board the service loads: empty, or the tasks a wake test starts from.
    sealed class MemoryStore(TaskBoard? board = null) : ITaskStore
    {
        public Result<TaskBoard> Load() => board ?? TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed record Call(IReadOnlyList<string> Arguments, string? Folder);

    sealed record Row(string SessionId, string Folder, bool Busy);

    // `claude` as the probes found it: a start prints "backgrounded · <short id> · <name>" and the daemon lists the new
    // session; a bare resume wakes the session under its own id; a stop takes it off the list.
    sealed class FakeCli : IClaudeCli
    {
        public List<Call> Calls { get; } = [];
        public List<Row> Listed { get; } = [];

        // The id the next start gets.
        public string NextId { get; set; } = "5a1e0c2b-1111-4222-8333-944455556666";

        // An override for a start that fails (an untrusted folder).
        public Func<Result<string>>? Start { get; set; }

        // Sessions the daemon has not listed yet, so a search for one waits out its pauses (a slow start).
        public HashSet<string> NotListedYet { get; } = [];

        // A `claude stop` that fails: as a failed Result, or as a fault the CLI did not turn into one.
        public Func<Result<string>>? Stop { get; set; }
        public bool StopThrows { get; set; }

        // Every call but the list reads, in order.
        public IReadOnlyList<Call> Acts => Calls.Where(c => c.Arguments is not ["agents", "--json"]).ToList();

        public Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
        {
            Calls.Add(new Call(arguments.ToList(), workingDirectory));
            if (StopThrows && arguments is ["stop", _])
                return Task.FromException<Result<string>>(new InvalidOperationException("the tree could not be killed"));
            return Task.FromResult(arguments switch
            {
                ["agents", "--json"] => Result.Success(Json()),
                ["--bg", "--resume", var id] => Woken(id, workingDirectory),
                // A wake's resume: bare, then the message after `--` (ManagerActions.ResumedWith).
                ["--bg", "--resume", var id, "--", _] => Woken(id, workingDirectory),
                ["--bg", "--model", ..] => Start?.Invoke() ?? Started(workingDirectory),
                ["stop", var shortId] => Stop?.Invoke() ?? Stopped(shortId),
                _ => Result.Failure<string>($"unexpected claude call: {string.Join(" ", arguments)}"),
            });
        }

        Result<string> Started(string? folder)
        {
            Listed.Add(new Row(NextId, folder ?? "", Busy: false));
            return Result.Success($"backgrounded · {NextId[..8]} · {ManagerSession.Name}");
        }

        Result<string> Woken(string id, string? folder)
        {
            Listed.Add(new Row(id, folder ?? "", Busy: false));
            return Result.Success($"note: woke session {id[..8]} with its saved options (-n, --model).");
        }

        Result<string> Stopped(string shortId)
        {
            Listed.RemoveAll(row => row.SessionId.StartsWith(shortId, StringComparison.Ordinal));
            return Result.Success($"stopped {shortId}");
        }

        string Json() => JsonSerializer.Serialize(Listed.Where(row => !NotListedYet.Contains(row.SessionId)).Select(row => new
        {
            pid = 1,
            id = row.SessionId[..8],
            cwd = row.Folder,
            kind = "background",
            sessionId = row.SessionId,
            name = ManagerSession.Name,
            status = row.Busy ? "busy" : "idle",
        }));
    }

    sealed class Rig : IDisposable
    {
        public Harness Harness { get; } = Harness.Build();
        public TestScheduler Clock { get; } = new();
        public FakeCli Cli { get; } = new();
        public TaskService Tasks { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "deskbar-manager-actions-" + Guid.NewGuid().ToString("N"));
        public string StateDir => Path.Combine(Root, "state");
        public string Home => Path.Combine(Root, "home");
        public string Folder => ManagerSession.FolderIn(StateDir);
        public string Shim => Path.Combine(StateDir, "deskbar.cmd");
        public string ShimForward => Shim.Replace('\\', '/');
        public string BriefingPath => Path.Combine(Folder, ManagerSession.BriefingFile);

        public List<ProgramLaunch> Launches { get; } = [];
        public List<string> Told { get; } = [];
        public List<string> Asked { get; } = [];
        public bool TrustsTerminal { get; set; }

        public ClaudeSessions Sessions { get; }
        public ClaudeTaskActions Actions { get; }
        public ManagerActions Manager { get; }

        // The comment inbox the event wakes deliver into (only a rig built with `wakes`).
        public CommentInbox Inbox { get; }
        public string InboxFolder => Path.Combine(Root, "inbox");

        /// <param name="wakes">Wire the event wakes (the board and the inbox), as the App does.</param>
        /// <param name="board">The tasks the board starts with.</param>
        public Rig(bool wakes = false, TaskBoard? board = null)
        {
            Clock.AdvanceTo(Origin.UtcTicks);
            Tasks = new TaskService(new MemoryStore(board), () => Clock.Now);
            Tasks.Load();
            Inbox = new CommentInbox(InboxFolder, () => Clock.Now);
            Sessions = new ClaudeSessions(Tasks, Harness.Manager, Cli, Clock, _ => Maybe<Guid>.None, Home,
                background: Scheduler.Immediate, managerFolder: Folder, folderExists: _ => true);
            Actions = new ClaudeTaskActions(Tasks, Harness.Manager, Sessions.Live, new ClaudeTaskSeams(
                Start: (_, _, _) => Task.FromResult(Result.Failure<string>("no task starts here")),
                Resume: (_, _) => Task.FromResult(Result.Failure<ClaudeAgent>("no task resumes here")),
                Launch: launch =>
                {
                    Launches.Add(launch);
                    return Result.Success<int?>(4321);
                },
                EditorWindow: _ => Maybe<nint>.None,
                BringForward: _ => Result.Success(),
                TranscriptExists: _ => true,
                EditorFolderNames: _ => [])
            {
                OfferTrustTerminal = message =>
                {
                    Asked.Add(message);
                    return TrustsTerminal;
                },
                Tell = Told.Add,
            }, Clock);
            Manager = wakes
                ? new ManagerActions(Harness.Manager, Sessions, Actions, StateDir, Shim, Clock, () => Today, tasks: Tasks, inbox: Inbox)
                : new ManagerActions(Harness.Manager, Sessions, Actions, StateDir, Shim, Clock, () => Today);
        }

        public Guid Gepha => Harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;

        // The answers of `claude` arrive on the scheduler; a second covers every hop, and the list shows a started
        // session at once, so no search waits out its pause.
        public void Settle() => Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        public void Stored(string sessionId, DateOnly day) => Harness.Manager.SetManagerSession(new ManagerRecord(sessionId, day));

        public void WriteTranscript(string sessionId)
        {
            var path = ClaudeTranscript.PathOf(Home, Folder, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, ["""{"type":"user","message":{"role":"user","content":"start"}}"""]);
        }

        public void Dispose()
        {
            Manager.Dispose();
            Actions.Dispose();
            Sessions.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    const string Earlier = "0a1d0a1d-aaaa-4bbb-8ccc-dddddddddddd";

    // A started or claimed manager is stored with today and a Since (event wakes count from its start).
    static void AssertStoredToday(Rig rig, string sessionId)
    {
        var stored = rig.Harness.Manager.State.Manager;
        Assert.NotNull(stored);
        Assert.Equal(sessionId, stored.SessionId);
        Assert.Equal(Today, stored.Day);
        Assert.NotNull(stored.Since);
        Assert.False(stored.Unread);
    }

    // EVENT WAKES (docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md). The manager started 31 minutes before
    // the clock's Origin and a Claude task was handed back to the user 11 minutes after that, so a tick at Origin wakes it.
    // The clock is NOT advanced before a test's own Tick: the sessions poll first reads at Origin, during the test's
    // Settle, by which time the test's Tick (or the poll's, in The_sessions_poll_ticks_the_wakes) has decided.
    static readonly Guid Stalled = Guid.Parse("51a11ed0-0000-0000-0000-000000000001");
    static readonly DateTimeOffset ManagerStart = Origin.AddMinutes(-31);

    static TaskBoard HandedBackBoard() => new(
    [
        new TaskItem(Stalled, "Tasks redesign", Maybe<Guid>.None, Deskbar.Core.Tasks.TaskStatus.InProgress, "PR 512 green, ready to merge",
            Maybe<string>.None, Origin.AddDays(-1), Origin, Origin.AddDays(-1), [], [])
        {
            HandedBack = ManagerStart.AddMinutes(11),
        },
    ]);

    static Rig WakeRig(bool unread = false, bool handedBack = true)
    {
        var rig = new Rig(wakes: true, board: handedBack ? HandedBackBoard() : null);
        rig.Harness.Manager.SetManagerSession(new ManagerRecord(Earlier, Today, ManagerStart, unread));
        return rig;
    }

    static ClaudeAgent ListedManager(Rig rig, bool busy = false) => new(Earlier, Earlier[..8], rig.Folder, true, busy, ManagerSession.Name);

    // A running, idle manager with a live waiter: the wake goes into its inbox, where the waiter wakes it; nothing is
    // started or resumed, and the record moves Since. A first hand-back sets no dot (task 6a87df67: the nudge is the
    // manager's own work); ManagerWakesTests covers the repeat hand-back that does.
    [Fact]
    public void A_hand_back_wakes_a_running_manager_through_its_inbox()
    {
        using var rig = WakeRig();
        Assert.True(rig.Inbox.Register(Earlier).IsSuccess);
        rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: false));
        // A running session has its transcript; the comment roads skip one without (it could not be resumed either).
        rig.WriteTranscript(Earlier);

        rig.Manager.Tick([ListedManager(rig)]);
        rig.Settle();

        var message = File.ReadAllText(Path.Combine(rig.InboxFolder, Earlier + ".md"));
        Assert.StartsWith("Deskbar wake.", message);
        Assert.Contains("51a11ed0 \"Tasks redesign\"", message);
        Assert.Empty(rig.Cli.Acts);
        var stored = rig.Harness.Manager.State.Manager!;
        Assert.False(stored.Unread);
        Assert.Equal(Origin, stored.Since);
        Assert.Equal([Stalled], stored.HandedBack);
    }

    // A manager that is not running (ended, or Deskbar rebooted with it) is woken by a BARE resume with the message after
    // `--`, in its folder: flags would fork a copy.
    [Fact]
    public void A_hand_back_resumes_a_manager_that_is_not_running()
    {
        using var rig = WakeRig();
        rig.WriteTranscript(Earlier);

        rig.Manager.Tick([]);
        rig.Settle();

        var resume = Assert.Single(rig.Cli.Acts);
        Assert.Equal(["--bg", "--resume", Earlier, "--"], resume.Arguments.Take(4));
        Assert.StartsWith("Deskbar wake.", resume.Arguments[4]);
        Assert.Equal(rig.Folder, resume.Folder);
        // A first hand-back: the record moved on (Since), with no dot (task 6a87df67).
        Assert.Equal(Origin, rig.Harness.Manager.State.Manager!.Since);
    }

    // Post-merge review of #557: the poll's list can be stale (a click resumed the manager after it was read). A fresh read
    // still lists the manager, so it is NOT resumed (that would fork a copy): after ResumeChecks reads the wake waits in
    // its inbox instead, and the record still moves on.
    [Fact]
    public void A_stale_list_never_resumes_a_manager_that_is_running()
    {
        using var rig = WakeRig();
        rig.WriteTranscript(Earlier);
        rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: false));

        rig.Manager.Tick([]);
        rig.Clock.AdvanceBy((ManagerActions.ResumePause * (ManagerActions.ResumeChecks + 1)).Ticks);

        Assert.DoesNotContain(rig.Cli.Acts, call => call.Arguments is ["--bg", "--resume", ..]);
        Assert.StartsWith("Deskbar wake.", File.ReadAllText(Path.Combine(rig.InboxFolder, Earlier + ".md")));
        Assert.Equal(Origin, rig.Harness.Manager.State.Manager!.Since);
    }

    // A busy manager is not woken: the batch waits, and nothing about the record changes.
    [Fact]
    public void A_busy_manager_is_not_woken()
    {
        using var rig = WakeRig();
        Assert.True(rig.Inbox.Register(Earlier).IsSuccess);
        rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: true));

        rig.Manager.Tick([ListedManager(rig, busy: true)]);
        rig.Settle();

        Assert.False(File.Exists(Path.Combine(rig.InboxFolder, Earlier + ".md")));
        Assert.Equal(new ManagerRecord(Earlier, Today, ManagerStart), rig.Harness.Manager.State.Manager);
    }

    // The sessions poll drives it: no Tick by hand, only the poll's own first read on the clock.
    [Fact]
    public void The_sessions_poll_ticks_the_wakes()
    {
        using var rig = WakeRig();
        rig.WriteTranscript(Earlier);

        rig.Settle();

        Assert.Contains(rig.Cli.Acts, call => call.Arguments is ["--bg", "--resume", Earlier, "--", _]);
    }

    // Opening the manager is the user reading what the wake brought: the dot goes.
    [Fact]
    public void Opening_the_manager_clears_the_dot()
    {
        using var rig = WakeRig(unread: true, handedBack: false);
        rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: false));

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        Assert.False(rig.Harness.Manager.State.Manager!.Unread);
        Assert.Single(rig.Launches);
    }

    // A click while a wake's resume is under way is not dropped and does not resume alongside it (two resumes fork a
    // copy): it runs once the wake is done, and finds the manager listed, so it attaches.
    [Fact]
    public void A_click_during_a_wake_runs_after_it()
    {
        using var rig = WakeRig();
        rig.WriteTranscript(Earlier);

        rig.Manager.Tick([]);
        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        Assert.Single(rig.Cli.Acts.Where(c => c.Arguments is ["--bg", "--resume", ..]));
        AssertAttachedOnTheWorkspaceInFront(rig, Earlier);
        Assert.False(rig.Harness.Manager.State.Manager!.Unread);
    }

    static void AssertAttachedOnTheWorkspaceInFront(Rig rig, string sessionId)
    {
        var launch = Assert.Single(rig.Launches);
        var expected = ClaudeStart.AttachWindow(sessionId[..8], rig.Folder, "Deskbar manager · Claude");
        Assert.Equal(expected.Program, launch.Program);
        Assert.Equal(expected.Arguments, launch.Arguments);
        Assert.Equal(expected.WorkingDirectory, launch.WorkingDirectory);
        Assert.Equal(rig.Gepha, rig.Harness.Manager.CurrentWorkspaceId.Value);
        Assert.True(rig.Harness.Manager.AwaitsAny("WindowsTerminal"));
    }

    // Spec, "Where it runs": nothing stored, so the click starts Fable in the manager's own folder with the measured flags
    // (the briefing by its path in that folder, the shim by its full path with forward slashes), writes the briefing
    // there first, stores the new session's full id with today, and opens its window on the workspace in front.
    [Fact]
    public void First_click_starts_fable_in_the_manager_folder_and_stores_the_day()
    {
        using var rig = new Rig();

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        var start = Assert.Single(rig.Cli.Acts);
        Assert.Equal(ManagerSession.StartArguments(rig.BriefingPath, rig.ShimForward), start.Arguments);
        Assert.Contains("fable", start.Arguments);
        Assert.Contains($"Bash({rig.ShimForward} task:*)", start.Arguments);
        Assert.Equal(rig.Folder, start.Folder);
        Assert.True(Directory.Exists(rig.Folder));
        Assert.Equal(ManagerSession.Briefing(rig.ShimForward), File.ReadAllText(rig.BriefingPath));
        AssertStoredToday(rig, rig.Cli.NextId);
        AssertAttachedOnTheWorkspaceInFront(rig, rig.Cli.NextId);
        Assert.Empty(rig.Told);
    }

    // Design, "The button", 1, and the Deskbar-restarted case: today's manager is listed, so the click opens a window on
    // it and runs no `claude` but the list read.
    [Fact]
    public void Click_on_todays_listed_manager_attaches_without_starting()
    {
        using var rig = new Rig();
        rig.Stored(Earlier, Today);
        rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: false));

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        Assert.Empty(rig.Cli.Acts);
        AssertAttachedOnTheWorkspaceInFront(rig, Earlier);
        Assert.Equal(new ManagerRecord(Earlier, Today), rig.Harness.Manager.State.Manager);
    }

    // Design, "The button", 2: today's manager ended but its transcript is there, so it is woken by a BARE resume under
    // its full id, in its folder (flags on a resume fork a copy), and attached once listed. The record is unchanged.
    [Fact]
    public void Click_on_todays_ended_manager_resumes_it()
    {
        using var rig = new Rig();
        rig.Stored(Earlier, Today);
        rig.WriteTranscript(Earlier);

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        var resume = Assert.Single(rig.Cli.Acts);
        Assert.Equal(ManagerSession.ResumeArguments(Earlier), resume.Arguments);
        Assert.Equal(rig.Folder, resume.Folder);
        AssertAttachedOnTheWorkspaceInFront(rig, Earlier);
        Assert.Equal(new ManagerRecord(Earlier, Today), rig.Harness.Manager.State.Manager);
    }

    // Design, "The button", 3: yesterday's manager is still listed and idle, so it is stopped (by its short id) BEFORE
    // today's starts, and today's id and day replace it.
    [Fact]
    public void Next_day_click_stops_the_idle_old_manager_then_starts()
    {
        using var rig = new Rig();
        rig.Stored(Earlier, Today.AddDays(-1));
        rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: false));

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        Assert.Equal(2, rig.Cli.Acts.Count);
        Assert.Equal(["stop", Earlier[..8]], rig.Cli.Acts[0].Arguments);
        Assert.Equal(ManagerSession.StartArguments(rig.BriefingPath, rig.ShimForward), rig.Cli.Acts[1].Arguments);
        AssertStoredToday(rig, rig.Cli.NextId);
        AssertAttachedOnTheWorkspaceInFront(rig, rig.Cli.NextId);
    }

    // Design, "Where it runs", the trust prompt: the manager folder is new on every machine, so the first start fails
    // "Workspace not trusted". The offer a task's start makes is made here too, as a message; yes opens a terminal running
    // `claude` in the manager folder on the workspace in front. Nothing is stored, and nothing retries on its own.
    [Fact]
    public void First_start_untrusted_offers_trust_and_stores_nothing()
    {
        using var rig = new Rig { TrustsTerminal = true };
        rig.Cli.Start = () => Result.Failure<string>(Untrusted);

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        var asked = Assert.Single(rig.Asked);
        Assert.Contains("Workspace not trusted", asked);
        Assert.Contains(ClaudeTaskActions.TrustQuestion, asked);
        var trust = Assert.Single(rig.Launches);
        var expected = ClaudeStart.TrustWindow(rig.Folder);
        Assert.Equal(expected.Program, trust.Program);
        Assert.Equal(expected.Arguments, trust.Arguments);
        Assert.Equal(expected.WorkingDirectory, trust.WorkingDirectory);
        Assert.Null(rig.Harness.Manager.State.Manager);
        Assert.Empty(rig.Told);

        // Nothing retries: minutes later there is still the one start.
        rig.Clock.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
        Assert.Single(rig.Cli.Acts);
    }

    // Plan, risk 2: a second click while the first start is still finding its session (the daemon has not listed it
    // yet, so the search waits between reads) starts nothing more. Once the first has attached, a click goes through
    // again and finds today's manager listed.
    [Fact]
    public void Second_click_while_starting_starts_nothing()
    {
        using var rig = new Rig();
        rig.Cli.NotListedYet.Add(rig.Cli.NextId);

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();
        Assert.Single(rig.Cli.Acts);

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();
        Assert.Single(rig.Cli.Acts);
        Assert.Empty(rig.Launches);

        // The daemon lists it; the first click's search finds it at its next read and attaches.
        rig.Cli.NotListedYet.Clear();
        rig.Clock.AdvanceBy(TimeSpan.FromSeconds(3).Ticks);
        AssertStoredToday(rig, rig.Cli.NextId);
        Assert.Single(rig.Launches);

        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();
        Assert.Single(rig.Cli.Acts);
        Assert.Single(rig.Launches);
    }

    // Design, "Where it runs": the briefing follows the build. It is written before every start AND every resume, so the
    // saved --append-system-prompt-file a resume keeps reads the current text.
    [Fact]
    public void Briefing_file_is_rewritten_on_every_start_and_resume()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(rig.Folder);
        File.WriteAllText(rig.BriefingPath, "an older build's briefing");

        rig.Manager.Open();
        rig.Settle();
        Assert.Equal(ManagerSession.Briefing(rig.ShimForward), File.ReadAllText(rig.BriefingPath));

        // The session ends; its transcript stays, so the next click resumes it.
        rig.Cli.Listed.Clear();
        rig.WriteTranscript(rig.Cli.NextId);
        File.WriteAllText(rig.BriefingPath, "an older build's briefing");

        rig.Manager.Open();
        rig.Settle();
        Assert.Equal(ManagerSession.ResumeArguments(rig.Cli.NextId), rig.Cli.Acts[^1].Arguments);
        Assert.Equal(ManagerSession.Briefing(rig.ShimForward), File.ReadAllText(rig.BriefingPath));
    }

    // Task review, Important: a start Claude Code does not list within the search stores nothing, but the session runs on
    // in the manager folder. The click says why no window came; the next click finds that session by its folder, stores
    // it as today's and attaches, and starts no second manager beside it.
    [Fact]
    public void A_start_not_listed_in_time_is_claimed_by_the_next_click_not_started_again()
    {
        using var rig = new Rig();
        rig.Cli.NotListedYet.Add(rig.Cli.NextId);

        rig.Manager.Open();
        rig.Clock.AdvanceBy(TimeSpan.FromSeconds(15).Ticks);
        Assert.Contains(rig.Told, told => told.Contains("did not list it"));
        Assert.Null(rig.Harness.Manager.State.Manager);
        Assert.Empty(rig.Launches);

        rig.Cli.NotListedYet.Clear();
        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        Assert.Single(rig.Cli.Acts);
        AssertStoredToday(rig, rig.Cli.NextId);
        AssertAttachedOnTheWorkspaceInFront(rig, rig.Cli.NextId);
    }

    // A start that fails for another reason than trust is told as it is, and the click is no longer pending: the next
    // click starts again.
    [Fact]
    public void A_failed_start_is_told_and_the_next_click_starts_again()
    {
        using var rig = new Rig();
        rig.Cli.Start = () => Result.Failure<string>("claude exited with code 1.\nsomething else went wrong");

        rig.Manager.Open();
        rig.Settle();
        var told = Assert.Single(rig.Told);
        Assert.Contains("something else went wrong", told);
        Assert.Empty(rig.Asked);
        Assert.Null(rig.Harness.Manager.State.Manager);

        rig.Cli.Start = null;
        Assert.True(rig.Manager.Open().IsSuccess);
        rig.Settle();

        Assert.Equal(2, rig.Cli.Acts.Count);
        AssertStoredToday(rig, rig.Cli.NextId);
        AssertAttachedOnTheWorkspaceInFront(rig, rig.Cli.NextId);
    }

    // Yesterday's idle manager cannot be stopped (a failed answer, or a fault the CLI did not turn into one): today's
    // manager still starts, and the click is not lost.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_stop_still_starts_todays_manager(bool throws)
    {
        using var rig = new Rig();
        rig.Stored(Earlier, Today.AddDays(-1));
        rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: false));
        rig.Cli.Stop = () => Result.Failure<string>("claude exited with code 1.\nno such session");
        rig.Cli.StopThrows = throws;

        rig.Manager.Open();
        rig.Settle();

        Assert.Equal(["stop", Earlier[..8]], rig.Cli.Acts[0].Arguments);
        Assert.Equal(ManagerSession.StartArguments(rig.BriefingPath, rig.ShimForward), rig.Cli.Acts[1].Arguments);
        AssertStoredToday(rig, rig.Cli.NextId);
        Assert.Single(rig.Launches);
        Assert.Empty(rig.Told);
    }

    // Final review, item 3: the shim as the allow rule and the briefing name it. A path with no space is the full path
    // with forward slashes, its short form never asked for.
    [Fact]
    public void ShimCommand_is_the_forward_slash_path_when_it_has_no_space() =>
        Assert.Equal("C:/Users/me/AppData/Roaming/Deskbar/deskbar.cmd",
            ManagerActions.ShimCommand(@"C:\Users\me\AppData\Roaming\Deskbar\deskbar.cmd",
                _ => throw new InvalidOperationException("no short form is needed")));

    // A space never matches the unquoted rule (the model quotes the path; measured denied), so the 8.3 short form is used,
    // which has none and was measured to run.
    [Fact]
    public void ShimCommand_takes_the_short_form_of_a_path_with_a_space() =>
        Assert.Equal("C:/Users/JOHNDO~1/AppData/Roaming/Deskbar/deskbar.cmd",
            ManagerActions.ShimCommand(@"C:\Users\John Doe\AppData\Roaming\Deskbar\deskbar.cmd",
                path => path.Replace("John Doe", "JOHNDO~1")));

    // A volume with 8.3 names off hands the long form back: the path is double-quoted, the form the model writes, and the
    // allow rule built from it carries the same quotes (measured to match).
    [Fact]
    public void ShimCommand_quotes_a_spaced_path_that_has_no_short_form()
    {
        var shim = ManagerActions.ShimCommand(@"C:\Users\John Doe\AppData\Roaming\Deskbar\deskbar.cmd", path => path);

        Assert.Equal("\"C:/Users/John Doe/AppData/Roaming/Deskbar/deskbar.cmd\"", shim);
        Assert.Contains("Bash(\"C:/Users/John Doe/AppData/Roaming/Deskbar/deskbar.cmd\" task:*)", ManagerSession.StartArguments("b.md", shim));
    }

    // As "Start Claude on this": while Claude sessions are off, the click is refused with the reason and runs nothing.
    [Fact]
    public void Click_while_Claude_sessions_are_off_is_refused()
    {
        using var rig = new Rig();
        Assert.True(rig.Harness.Manager.SetClaudeTasks(false).IsSuccess);

        var refused = rig.Manager.Open();
        rig.Settle();

        Assert.True(refused.IsFailure);
        Assert.Equal(ClaudeTaskActions.FeatureOff, refused.Error);
        Assert.Empty(rig.Cli.Calls.Where(c => c.Arguments is not ["agents", "--json"]));
        Assert.Null(rig.Harness.Manager.State.Manager);
    }

    // THE MANAGER'S PAGE (task 6c4c067f; spec docs/superpowers/specs/2026-11-28-manager-inbox-page-design.md, "Sending"):
    // a message written on the page reaches the manager by the comment roads, is stored for the page's bubble, and marks
    // the record as the user having written. Open(attach: false) makes sure the manager exists without a window.
    static Rig PageRig(bool listed = true, bool busy = false, bool waiter = true)
    {
        var rig = new Rig(wakes: true);
        rig.Harness.Manager.SetManagerSession(new ManagerRecord(Earlier, Today, ManagerStart));
        rig.WriteTranscript(Earlier);
        if (waiter) Assert.True(rig.Inbox.Register(Earlier).IsSuccess);
        if (listed) rig.Cli.Listed.Add(new Row(Earlier, rig.Folder, Busy: busy));
        return rig;
    }

    static string InboxText(Rig rig) => File.ReadAllText(Path.Combine(rig.InboxFolder, Earlier + ".md"));

    [Fact]
    public void Send_while_the_first_turn_runs_goes_to_the_inbox()
    {
        using var rig = PageRig(busy: true, waiter: false);

        Assert.True(rig.Manager.Send("what is stuck?", Maybe<TaskItem>.None).IsSuccess);
        rig.Settle();

        Assert.Equal(ManagerSession.PageMessage("what is stuck?", Maybe<string>.None) + "\n", InboxText(rig));
        Assert.Empty(rig.Cli.Acts);
    }

    [Fact]
    public void Send_stores_the_message_and_marks_the_record_replying()
    {
        using var rig = PageRig();
        rig.Harness.Manager.SetManagerSession(rig.Harness.Manager.State.Manager! with { Question = "Which one?", AskedAt = Origin });

        rig.Manager.Send("  the first  ", Maybe<TaskItem>.None);

        var stored = rig.Harness.Manager.State.Manager!;
        Assert.Equal("the first", Assert.Single(stored.Messages!).Text);
        Assert.True(stored.Replying);
        Assert.Null(stored.Question);
    }

    [Fact]
    public void Send_to_a_listed_idle_manager_with_a_live_waiter_goes_to_the_inbox()
    {
        using var rig = PageRig();

        rig.Manager.Send("hello", Maybe<TaskItem>.None);
        rig.Settle();

        Assert.StartsWith(ManagerSession.PageMessageOpening, InboxText(rig));
        Assert.Empty(rig.Cli.Acts);
    }

    [Fact]
    public void Send_to_an_idle_manager_whose_waiter_lapsed_stops_and_resumes_it_with_the_message()
    {
        using var rig = PageRig(waiter: false);

        rig.Manager.Send("hello", Maybe<TaskItem>.None);
        rig.Clock.AdvanceBy((ManagerActions.ResumePause * (ManagerActions.ResumeChecks + 1)).Ticks);

        Assert.Equal(["stop", Earlier[..8]], rig.Cli.Acts[0].Arguments);
        Assert.Equal(["--bg", "--resume", Earlier, "--", ManagerSession.PageMessage("hello", Maybe<string>.None)], rig.Cli.Acts[1].Arguments);
    }

    [Fact]
    public void Send_with_no_manager_starts_one_with_the_message()
    {
        using var rig = new Rig(wakes: true);

        rig.Manager.Send("start with this", Maybe<TaskItem>.None);
        rig.Settle();

        var start = Assert.Single(rig.Cli.Acts);
        Assert.Equal(ManagerSession.StartArguments(rig.BriefingPath, rig.ShimForward, ManagerSession.PageMessage("start with this", Maybe<string>.None)),
            start.Arguments);
        var stored = rig.Harness.Manager.State.Manager!;
        Assert.Equal(rig.Cli.NextId, stored.SessionId);
        Assert.Equal("start with this", Assert.Single(stored.Messages!).Text);
        Assert.True(stored.Replying);
    }

    [Fact]
    public void Two_sends_during_a_pending_click_go_in_order()
    {
        using var rig = PageRig();

        Assert.True(rig.Manager.Open(attach: false).IsSuccess);
        rig.Manager.Send("one", Maybe<TaskItem>.None);
        rig.Manager.Send("two", Maybe<TaskItem>.None);
        rig.Settle();
        rig.Settle();

        var text = InboxText(rig);
        Assert.True(text.IndexOf("\n\none", StringComparison.Ordinal) < text.IndexOf("\n\ntwo", StringComparison.Ordinal));
        Assert.Equal(["one", "two"], rig.Harness.Manager.State.Manager!.Messages!.Select(m => m.Text));
    }

    // Review of task 6c4c067f: the first click of the day starts the manager from the page (no record yet), and a message
    // typed before it is listed is stored on the record the start made once it exists, and delivered.
    [Fact]
    public void A_send_while_the_page_starts_the_first_manager_is_stored_on_its_record()
    {
        using var rig = new Rig(wakes: true);
        // A started manager has its transcript at once; the comment roads skip a session without one.
        rig.WriteTranscript(rig.Cli.NextId);

        Assert.True(rig.Manager.Open(attach: false).IsSuccess);
        rig.Manager.Send("hello", Maybe<TaskItem>.None);
        Assert.Null(rig.Harness.Manager.State.Manager);
        rig.Clock.AdvanceBy((ManagerActions.ResumePause * (ManagerActions.ResumeChecks + 2)).Ticks);

        var stored = rig.Harness.Manager.State.Manager!;
        Assert.Equal(rig.Cli.NextId, stored.SessionId);
        Assert.Equal("hello", Assert.Single(stored.Messages!).Text);
        Assert.True(stored.Replying);
        var delivered = ManagerSession.PageMessage("hello", Maybe<string>.None);
        var inbox = Path.Combine(rig.InboxFolder, rig.Cli.NextId + ".md");
        Assert.True(rig.Cli.Acts.Any(a => a.Arguments.Contains(delivered)) || File.Exists(inbox) && File.ReadAllText(inbox).Contains(delivered));
    }

    // Review of task 6c4c067f: "Open in terminal" while the page's own open is still under way is another intent, so it
    // runs after it instead of being dropped.
    [Fact]
    public void Open_in_terminal_during_a_page_open_runs_after_it()
    {
        using var rig = PageRig();

        rig.Manager.Open(attach: false);
        rig.Manager.Open(attach: true);
        rig.Settle();
        rig.Settle();

        Assert.Single(rig.Launches);
    }

    [Fact]
    public void Open_without_attach_attaches_no_window()
    {
        using var rig = PageRig();

        Assert.True(rig.Manager.Open(attach: false).IsSuccess);
        rig.Settle();

        Assert.Empty(rig.Launches);
    }

    [Fact]
    public void Send_names_the_task_that_was_open()
    {
        using var rig = PageRig();
        var task = new TaskItem(Stalled, "Tasks redesign", Maybe<Guid>.None, Deskbar.Core.Tasks.TaskStatus.InProgress, "",
            Maybe<string>.None, Origin, Origin, Origin, [], []);

        rig.Manager.Send("is this one stuck?", Maybe.From(task));
        rig.Settle();

        Assert.Contains($"{ManagerSession.OpenTaskOpening} 51a11ed0 \"Tasks redesign\"", InboxText(rig));
    }

    [Fact]
    public void Send_with_Claude_sessions_off_is_refused_and_stores_nothing()
    {
        using var rig = PageRig();
        Assert.True(rig.Harness.Manager.SetClaudeTasks(false).IsSuccess);

        var refused = rig.Manager.Send("hello", Maybe<TaskItem>.None);
        rig.Settle();

        Assert.Equal(ClaudeTaskActions.FeatureOff, refused.Error);
        Assert.Null(rig.Harness.Manager.State.Manager!.Messages);
        Assert.False(File.Exists(Path.Combine(rig.InboxFolder, Earlier + ".md")));
    }
}
