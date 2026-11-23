using System.Collections.Immutable;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// KEEPING THE TASK BOARD IN STEP WITH CLAUDE CODE'S OWN LIST OF LIVE SESSIONS. Design:
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md, sections "Which sessions are live", "Every live
// session has a task", "Keeping open tasks running", "Starting a session from a task" and "The setting". Measurements:
// docs/superpowers/notes/2026-11-23-claude-background-probes.md.
//
// Every rule lives in Core and is tested there: ClaudeAgents reads the list, TaskService.Reconcile makes the board agree
// with it, SessionKeeper.Plan decides what to wake, give up on or adopt, ClaudeStart writes the first message. This class
// is the one place that does those things at the right moment, and it has three jobs, all about the same list:
//
//   1. THE POLL. While sessions keep tasks (AppState.ClaudeTasksInEffect) it reads `claude agents --json` every
//      Poll (15 s), the first time as soon as the feature is on. Each good read is reconciled into the board, published
//      on Live, and handed to the keeper. A read that fails or that cannot be parsed means "cannot tell" (Global
//      Constraints): nothing is ended, nothing resumed, nothing adopted, and any resume waiting in the queue is dropped,
//      because the list it was decided on can no longer be vouched for. The task board then shows what the hooks last
//      said, which is the spec's fallback; the trace says so once per run of failures (Ruling 12), not every 15 s.
//
//   2. THE KEEPER'S STEPS. An Adopt makes a task for a session no task knows, on the workspace of the editor window
//      showing its folder (workspaceOfFolder, the rule `task new` uses), but only once the session has been listed for
//      AdoptAfter (2 minutes, Ruling 11): a session started by hand joins or creates its own task on its first turn,
//      through its SessionStart hook, and adopting it within the first poll left an empty duplicate task beside the
//      real one. A GiveUp asks Petre through the task. A Resume
//      goes into a queue that is drained ONE AT A TIME, SessionKeeper.Spacing (5 s) apart, because after a reboot there
//      can be twenty at once and each `claude.exe` takes hundreds of MB (Review Focus 2). Before each queued resume is
//      issued, the list is read AGAIN (final review, item 2; see Issue) and the resume checked against the CURRENT board
//      and that fresh list: the task must still be open, the session must still be its latest, and the session must NOT
//      be listed, because resuming a listed session starts a COPY with a new id (Task 1 note). Anything else drops it
//      (Review Focus 5: Petre closed the task meanwhile), and a fresh read that fails drops the whole queue.
//      With the automatic resume off (AppState.ClaudeResume) only the adoption runs, and the reconcile, as the spec asks.
//
//      THE WAITS (Rulings 17 and 18). Nothing is resumed in the first SessionKeeper.Settle (2 minutes) after this class
//      starts watching (built at Deskbar's start, or the feature turned on), and a session must have been missing from
//      the good reads for that long before it is: VS Code brings sessions back by itself after a restart or a reload.
//      A stranger is adopted only when its hooks have said since Deskbar started that a person is at it
//      (TaskService.WasAttended), since the list shows a `claude -p` run as "interactive". When the list cannot be read
//      at all, ListProblem says why, for Manage (Ruling 20).
//
//   3. STARTING A SESSION FROM A TASK (Start). `claude --bg "<first message>"` in the folder, the short id read from what
//      it prints, then the full id found in the list and the task JOINED HERE, not left to the session's SessionStart
//      hook: a background session's hooks can arrive late (Task 1 note), and a join that waits on them leaves the task
//      without its session for that long. The two-minute wait before adoption also covers the few seconds in which the
//      new session is listed but Deskbar does not yet know its short id.
//
//   A RESUME THAT FAILS (an untrusted folder, `claude` missing) is not tried again on a timer (Review Focus 4, Ruling 10):
//   Claude's own message goes into the task's thread, and the task waits on Petre with SessionKeeper.ResumeFailed as
//   its question, which the keeper treats as a sticky give-up until he acts on the task.
//
// THREADS. Every `claude` call runs on `background` (the thread pool in the app), so a slow or hung CLI never stalls the
// bar; its answer comes back on `scheduler`, which in the app is the UI thread, and that is the only thread that touches
// TaskService or this class's fields. So nothing here locks. Tests pass a virtual-time scheduler and an immediate
// `background`, which makes every call and every timer exact.
//
//   4. RESUMING ONE SESSION NOW, AT A CLICK (ResumeNow, Task 9): going to a task whose session has ended. It is the
//      keeper's resume under the same guards, not a second way round them: the list is read FRESH first, and a session
//      it shows is handed back as it is, never resumed (that would start a COPY); a session already being woken (the
//      queue's in flight, or another click's) or woken within SessionKeeper.Grace is not resumed again either, only
//      looked for in the list; one waiting in the queue is taken out of it and woken now; and the resume goes into the
//      ledger like any other, so the keeper's grace and loop guard count it.
//
// NOTHING HERE OPENS A WINDOW OR A TERMINAL. Putting a window on a session is the caller's (ClaudeTaskActions, Task 9).
public sealed class ClaudeSessions : IDisposable
{
    /// <summary>How often `claude agents --json` is read while the feature is on.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The message a session whose last turn was cut off is resumed with, exactly (Global Constraints). It mirrors what
    /// Claude Code's own daemon sends a session it restarts after a crash (Task 1 note).
    /// </summary>
    public const string ContinueMessage = "Continue from where you left off. Note: this session was resumed by Deskbar after it stopped.";

    // A list read normally answers in well under a second; ten covers a cold start of the CLI without letting a hung
    // one hold the next poll back for long (a read in flight makes the next tick skip, see Read).
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // `claude --bg` and `claude --bg --resume` hand the session to the daemon and return within a few seconds (measured).
    static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(30);

    // After `claude --bg` returns, the daemon lists the new session within a second or two (measured); Start looks for
    // it this many times, this far apart, about ten seconds in all, before it says it could not find it.
    const int FindAttempts = 6;
    static readonly TimeSpan FindPause = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a session no task knows must have been listed before it gets a task of its own (Ruling 11). A session
    /// started by hand joins its own task on its first turn; two minutes leaves room for that turn.
    /// </summary>
    public static readonly TimeSpan AdoptAfter = TimeSpan.FromMinutes(2);

    static readonly IReadOnlyList<string> AgentsArguments = ["agents", "--json"];

    readonly TaskService tasks;
    readonly WorkspaceManager manager;
    readonly IClaudeCli cli;
    readonly IScheduler scheduler;
    readonly IScheduler background;
    readonly Func<string, Maybe<Guid>> workspaceOfFolder;
    readonly string claudeHome;
    readonly Action<string>? trace;

    readonly ReplaySubject<IReadOnlyCollection<ClaudeAgent>> live = new(1);
    readonly Subject<System.Reactive.Unit> stopping = new();
    readonly SerialDisposable readCall = new();
    readonly SerialDisposable resumeCall = new();
    readonly SerialDisposable drainTimer = new();
    readonly CompositeDisposable subscriptions = new();

    // --- state, touched only on `scheduler` ---

    // Resumes made, for the keeper's grace and loop guard. A resume is recorded the moment it is ISSUED, failed or not,
    // so a session that cannot be woken still counts toward the guard.
    ResumeLedger ledger = ResumeLedger.Empty;

    // Resumes waiting their turn, in the keeper's order, and whether one is being issued or waited for.
    ImmutableQueue<KeeperStep.Resume> queue = ImmutableQueue<KeeperStep.Resume>.Empty;
    bool draining;
    DateTimeOffset lastIssued;

    // The session whose resume has been issued and not yet answered: it counts as queued, so a poll in the meantime
    // cannot queue it again (Ruling 13).
    Maybe<string> inFlight = Maybe<string>.None;

    // When each recent resume ANSWERED; a session is not resumed again within SessionKeeper.Grace of that (Settled).
    ImmutableDictionary<string, DateTimeOffset> answered = ImmutableDictionary<string, DateTimeOffset>.Empty;

    // The latest list that could be read. Empty until the first good read; only used to re-check a queued resume, and
    // the queue is empty until a good read has filled it.
    IReadOnlyCollection<ClaudeAgent> lastLive = [];

    // Whether a list read is in flight: a tick that comes while one is skips, so a hung CLI never piles reads up.
    bool reading;

    // Whether the last read failed: the trace line is written on the first failure of a run, not on every poll.
    bool unreadable;

    // When each listed session was first seen in an unbroken run of good reads; a session that leaves the list is
    // forgotten, so one that comes back starts its two minutes again (AdoptAfter).
    ImmutableDictionary<string, DateTimeOffset> firstSeen = ImmutableDictionary<string, DateTimeOffset>.Empty;

    // Sessions given up on (by the keeper, or here after a failed resume), so the ledger can forget them once Petre has
    // acted (ForgetAnswered).
    ImmutableHashSet<string> givenUp = ImmutableHashSet<string>.Empty;

    // Sessions Start launched whose full id is not yet known, by short id, with the task and folder they belong to. A
    // list that shows one (Start's own search or a poll) joins it to its task.
    ImmutableDictionary<string, StartClaim> claims = ImmutableDictionary<string, StartClaim>.Empty;

    sealed record StartClaim(Guid TaskId, string Folder);

    // Sessions a click is resuming right now (ResumeNow), apart from the queue's one in flight so neither ends the
    // other's turn: both count as queued, so no poll queues them meanwhile.
    ImmutableHashSet<string> wakingNow = ImmutableHashSet<string>.Empty;

    // RULING 18: when this class started watching (built, or the feature turned on), and since when each session that
    // a good read listed has been missing from every good read after it. The keeper acts on a session only once it has
    // been missing for SessionKeeper.Settle; one never listed since watching began counts as missing since then, which
    // is what keeps the first two minutes after a start free of resumes. A failed read changes neither: it says
    // nothing about who is listed.
    DateTimeOffset watchingSince;
    ImmutableDictionary<string, DateTimeOffset> goneSince = ImmutableDictionary<string, DateTimeOffset>.Empty;

    // RULING 20: why the list cannot be read, in one line, for Manage to show under the resume box; none after a good
    // read and while the feature is off.
    readonly BehaviorSubject<Maybe<string>> listProblem = new(Maybe<string>.None);

    /// <param name="scheduler">Where answers land and timers run: the UI thread in the app.</param>
    /// <param name="workspaceOfFolder">The workspace of the editor window showing a folder, or none, for adopted sessions.</param>
    /// <param name="claudeHome">The folder that holds Claude Code's `projects`, normally %USERPROFILE%\.claude.</param>
    /// <param name="background">Where `claude` runs and transcripts are read: the thread pool unless a test says otherwise.</param>
    public ClaudeSessions(TaskService tasks, WorkspaceManager manager, IClaudeCli cli, IScheduler scheduler,
        Func<string, Maybe<Guid>> workspaceOfFolder, string claudeHome, IScheduler? background = null, Action<string>? trace = null)
    {
        this.tasks = tasks;
        this.manager = manager;
        this.cli = cli;
        this.scheduler = scheduler;
        this.background = background ?? TaskPoolScheduler.Default;
        this.workspaceOfFolder = workspaceOfFolder;
        this.claudeHome = claudeHome;
        this.trace = trace;
        lastIssued = scheduler.Now - SessionKeeper.Spacing;
        watchingSince = scheduler.Now;

        // The poll, alive only while the feature is in effect: turning it on reads at once (StartWith), off stops the
        // timer (Switch to Never). StateChanged can pulse from the virtual-desktop thread, so the setting is read there
        // and only its CHANGES are moved onto the scheduler. Turning it on starts the watch afresh (Ruling 18: nothing
        // was read while it was off, so nobody's absence is known); turning it off clears Manage's line (Ruling 20).
        subscriptions.Add(manager.StateChanged
            .Select(_ => manager.State.ClaudeTasksInEffect)
            .StartWith(manager.State.ClaudeTasksInEffect)
            .DistinctUntilChanged()
            .ObserveOn(scheduler)
            .Do(on =>
            {
                if (on) StartWatching();
                else listProblem.OnNext(Maybe<string>.None);
            })
            .Select(on => on ? Observable.Interval(Poll, scheduler).StartWith(scheduler, 0L) : Observable.Never<long>())
            .Switch()
            .Subscribe(_ => Read()));
        subscriptions.Add(readCall);
        subscriptions.Add(resumeCall);
        subscriptions.Add(drainTimer);
    }

    /// <summary>Every list `claude agents --json` gave that could be read, latest first to a new subscriber.</summary>
    public IObservable<IReadOnlyCollection<ClaudeAgent>> Live => live.AsObservable();

    /// <summary>
    /// Why `claude agents --json` cannot be read right now, in one line (Ruling 20: `claude` missing from the PATH, a
    /// failed or unparseable read), or none once a read works again and while the feature is off. Manage shows it under
    /// the resume box. Pulses on `scheduler` (the UI thread in the app), the current value first to a new subscriber.
    /// </summary>
    public IObservable<Maybe<string>> ListProblem => listProblem.DistinctUntilChanged();

    /// <summary>
    /// Petre acted on a session the keeper gave up on (ClaudeTaskActions: a double-click that brought it back): its
    /// resumes no longer count, so the loop guard judges it afresh.
    /// </summary>
    public void Forget(string sessionId)
    {
        ledger = ledger.Forget(sessionId);
        givenUp = givenUp.Remove(sessionId);
    }

    /// <summary>
    /// Starts a background session on the task in <paramref name="folder"/> with the task as its first message, joins
    /// the task to it and returns its full session id. A failure carries Claude's own words: an untrusted folder makes
    /// `claude --bg` exit 1 with the reason on stderr (measured), and that reason is what Petre needs to see. Called on
    /// the UI thread; the returned task completes there.
    /// </summary>
    public Task<Result<string>> Start(TaskItem task, string folder) =>
        OffThread(() => cli.Run(["--bg", ClaudeStart.FirstMessage(task)], folder, LaunchTimeout))
            .SelectMany(launched => Launched(task, folder, launched))
            .TakeUntil(stopping)
            .DefaultIfEmpty(Result.Failure<string>("Deskbar closed before the session was found."))
            .ToTask();

    /// <summary>
    /// Resumes one session now, for a click on its task (Task 9; see the header, job 4), and answers it as the list
    /// shows it once it runs, so the caller can attach a window to it. A session the fresh list already shows comes back
    /// at once, untouched. A failure carries the reason, and has been said in the task's thread too (a failed resume as
    /// the keeper's own is), so a click that opened nothing has its reason where Petre looks. Called on the UI thread;
    /// the returned task completes there.
    /// </summary>
    public Task<Result<ClaudeAgent>> ResumeNow(Guid taskId, TaskSession session) =>
        OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .SelectMany(read => read.Bind(ClaudeAgents.Parse).Match(
                list => list.TryFirst(agent => agent.SessionId == session.Id) is { HasValue: true } listed
                    ? Observable.Return(Result.Success(listed.Value))
                    : WakeNow(taskId, session),
                // Cannot tell (Global Constraints): nothing is resumed on a list that could not be read.
                error => Observable.Return(Said(taskId, Result.Failure<ClaudeAgent>(
                    $"Claude Code's list of sessions could not be read, so the session was not resumed:\n\n{error}")))))
            .TakeUntil(stopping)
            .DefaultIfEmpty(Result.Failure<ClaudeAgent>("Deskbar closed before the session was resumed."))
            .ToTask();

    // Runs on `scheduler` (inside ResumeNow's SelectMany), so it may touch the state.
    IObservable<Result<ClaudeAgent>> WakeNow(Guid taskId, TaskSession session)
    {
        // Already being woken, or woken a moment ago and not listed yet: a second resume would start a copy.
        if (Waking(session.Id) || AnsweredWithinGrace(session.Id))
            return Listed(taskId, session.Id, FindAttempts);

        // Waiting its turn in the queue: woken now instead, so the queue does not wake it a second time.
        queue = ImmutableQueue.CreateRange(queue.Where(r => r.Session.Id != session.Id));
        var at = scheduler.Now;
        ledger = ledger.Record(session.Id, at);
        wakingNow = wakingNow.Add(session.Id);
        return OffThread(async () =>
            {
                // Read here, on `background`, for the reason given in Heard.
                var cutOff = CutOff(session);
                return (CutOff: cutOff, Result: await cli.Run(ResumeArguments(session.Id, cutOff), session.Folder, LaunchTimeout));
            })
            // A fault the CLI did not turn into a Result is a failure like any other (Ruling 13), not a dispatcher crash.
            .Catch((Exception e) => Observable.Return((CutOff: false, Result: Result.Failure<string>(e.Message))))
            .SelectMany(answer =>
            {
                wakingNow = wakingNow.Remove(session.Id);
                // From this answer on, the session is left alone for Grace, as after the queue's own resume (Settled).
                var now = scheduler.Now;
                answered = answered.Where(pair => pair.Value + SessionKeeper.Grace > now).ToImmutableDictionary().SetItem(session.Id, now);
                Answered(taskId, session.Id, answer.CutOff, answer.Result, at);
                return answer.Result.IsSuccess
                    ? Listed(taskId, session.Id, FindAttempts)
                    : Observable.Return(Result.Failure<ClaudeAgent>(answer.Result.Error));
            });
    }

    // Being resumed right now: the queue's one in flight, or a click's.
    bool Waking(string sessionId) => inFlight.Map(id => id == sessionId).GetValueOrDefault(false) || wakingNow.Contains(sessionId);

    // Reads the list until it shows the session, a pause between reads (about ten seconds in all, as Start's Find).
    // Not found in time is said in the task's thread, so a click that opened nothing has its reason where Petre looks.
    IObservable<Result<ClaudeAgent>> Listed(Guid taskId, string sessionId, int attemptsLeft) =>
        OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .SelectMany(read => read.Bind(ClaudeAgents.Parse)
                    .Map(list => list.TryFirst(agent => agent.SessionId == sessionId))
                    .GetValueOrDefault(Maybe<ClaudeAgent>.None) is { HasValue: true } found
                ? Observable.Return(Result.Success(found.Value))
                : attemptsLeft > 1
                    ? Observable.Timer(FindPause, scheduler).SelectMany(_ => Listed(taskId, sessionId, attemptsLeft - 1))
                    : Observable.Return(Said(taskId, Result.Failure<ClaudeAgent>(
                        $"Session {sessionId} was resumed, but Claude Code did not list it within {FindAttempts * FindPause.TotalSeconds:0} seconds, so no window was opened on it."))));

    // A click's failure, said in the task's thread and handed back.
    Result<ClaudeAgent> Said(Guid taskId, Result<ClaudeAgent> failure)
    {
        Say(taskId, failure.Error);
        return failure;
    }

    public void Dispose()
    {
        stopping.OnNext(System.Reactive.Unit.Default);
        subscriptions.Dispose();
        live.OnCompleted();
        listProblem.OnCompleted();
    }

    // --- the poll ---------------------------------------------------------------------------------------------------

    void Read()
    {
        if (reading) return;
        reading = true;
        // The error path matters (Ruling 13): a fault the CLI did not turn into a Result would otherwise be an unhandled
        // exception on the dispatcher, and `reading` would stay set, so no list would ever be read again.
        readCall.Disposable = OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout)).Subscribe(
            read =>
            {
                reading = false;
                Heard(read);
            },
            error =>
            {
                reading = false;
                trace?.Invoke($"claude sessions: list read threw: {error.Message}");
                if (manager.State.ClaudeTasksInEffect) listProblem.OnNext(Problem(error.Message));
            });
    }

    // Ruling 18: a fresh watch. Nobody's absence is known yet, so every session counts as missing from now.
    void StartWatching()
    {
        watchingSince = scheduler.Now;
        goneSince = ImmutableDictionary<string, DateTimeOffset>.Empty;
    }

    // Since when a session has been missing from the good reads (Ruling 18): the read that first missed it after it was
    // listed, and never before the watch began.
    DateTimeOffset AbsentSince(string sessionId) =>
        goneSince.TryGetValue(sessionId, out var gone) && gone > watchingSince ? gone : watchingSince;

    // Ruling 20: Manage's line. One line, since Claude's message can run to several.
    static Maybe<string> Problem(string error) =>
        $"Claude Code's list of sessions cannot be read, so nothing is resumed: {string.Join(" ", error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}";

    void Heard(Result<string> read)
    {
        // Turned off while the read was out: its answer is not wanted.
        if (!manager.State.ClaudeTasksInEffect) return;

        var parsed = read.Bind(ClaudeAgents.Parse);
        if (parsed.IsFailure)
        {
            // Cannot tell. Not even the queue is trusted: it was decided on a list that is now unconfirmed. Said once
            // per run of failures (Ruling 12): `claude` missing fails every 15 s, and one line says all there is to say.
            queue = ImmutableQueue<KeeperStep.Resume>.Empty;
            if (!unreadable) trace?.Invoke($"claude sessions: list not read, tasks keep what the hooks said: {parsed.Error}");
            unreadable = true;
            listProblem.OnNext(Problem(parsed.Error));
            return;
        }

        if (unreadable) trace?.Invoke("claude sessions: list read again");
        unreadable = false;
        listProblem.OnNext(Maybe<string>.None);
        var listed = parsed.Value;
        var now = scheduler.Now;
        // Ruling 18: a session the last good read listed and this one does not has been missing since now; one listed
        // again is no longer missing. Entries older than Settle say no more than the watch's start does, so they go.
        var listedIds = listed.Select(agent => agent.SessionId).ToHashSet();
        goneSince = goneSince
            .Where(pair => !listedIds.Contains(pair.Key) && pair.Value > now - SessionKeeper.Settle)
            .Concat(lastLive.Select(agent => agent.SessionId).Distinct()
                .Where(id => !listedIds.Contains(id) && !goneSince.ContainsKey(id))
                .Select(id => KeyValuePair.Create(id, now)))
            .ToImmutableDictionary();
        lastLive = listed;
        // Each listed session keeps the moment it was first seen; one that left the list is forgotten.
        firstSeen = listed.Select(agent => agent.SessionId).Distinct()
            .ToImmutableDictionary(id => id, id => firstSeen.TryGetValue(id, out var seen) ? seen : now);
        // Sessions Start launched join their tasks first, so the keeper sees them known and does not adopt them.
        Claim(listed);
        tasks.Reconcile(listed);
        // Stalled tasks (spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md): a Claude task no session
        // has worked on for 15 minutes goes back to Petre, and back to Claude once its session works again. A session
        // counts as working only when the list says busy AND it has been listed for BusyGrace, because a session just
        // resumed reports busy for up to 30 seconds while idle. An unreadable list never gets here (see above), so
        // "cannot tell" moves nothing.
        tasks.Stalled(listed
            .Where(agent => agent.Busy && now - firstSeen[agent.SessionId] >= StalledTasks.BusyGrace)
            .Select(agent => agent.SessionId)
            .ToHashSet());
        ForgetAnswered();

        // With the resume off, the keeper is still asked (for its adoptions), but told every transcript exists, so it
        // reads no files for steps nobody will take.
        var resuming = manager.State.ClaudeResumeInEffect;
        SessionKeeper.Plan(tasks.Board, listed, ledger,
                resuming ? TranscriptExists : _ => true,
                // Whether a turn was cut off is read when the resume is ISSUED, off the UI thread (Issue), not here: a
                // transcript can run to tens of MB and this runs on every poll for every session waiting in the queue.
                _ => false,
                scheduler.Now,
                // Ruling 18: no step for a session missing less than SessionKeeper.Settle.
                AbsentSince)
            .ToList()
            .ForEach(step => Take(step, resuming));

        live.OnNext(listed);
        Drain();
    }

    void Take(KeeperStep step, bool resuming)
    {
        switch (step)
        {
            // Only a session listed for AdoptAfter: before that it may still join its own task (Ruling 11). And only one
            // whose hooks have said, since Deskbar started, that a person is at it (Ruling 17): `claude agents` lists a
            // `claude -p` run as "interactive" (measured 2026-11-23), so the list alone would adopt every scripted call.
            case KeeperStep.Adopt adopt when ListedFor(adopt.Agent.SessionId) >= AdoptAfter && tasks.WasAttended(adopt.Agent.SessionId):
                tasks.Adopt(adopt.Agent, workspaceOfFolder(adopt.Agent.Folder))
                    .TapError(e => trace?.Invoke($"claude sessions: {adopt.Agent.SessionId} not adopted: {e}"));
                break;
            case KeeperStep.GiveUp giveUp when resuming:
                tasks.Ask(giveUp.TaskId, giveUp.Reason)
                    .Tap(() => LatestSessionOf(giveUp.TaskId).Tap(id => givenUp = givenUp.Add(id)))
                    .TapError(e => trace?.Invoke($"claude sessions: give-up not asked on {giveUp.TaskId}: {e}"));
                break;
            case KeeperStep.Resume resume when resuming && !Queued(resume.Session.Id):
                queue = queue.Enqueue(resume);
                break;
        }
    }

    TimeSpan ListedFor(string sessionId) =>
        firstSeen.TryGetValue(sessionId, out var seen) ? scheduler.Now - seen : TimeSpan.Zero;

    // A give-up stays until Petre acts on its task (answers, sets a status, closes it). Once its task no longer waits on
    // him with the keeper's question, the session's resumes are forgotten so the keeper judges it afresh; without this,
    // the old resumes inside the loop window would make the next poll give up again at once and undo what he did.
    void ForgetAnswered()
    {
        var answered = givenUp.Where(id => !StillGivenUp(id)).ToList();
        answered.ForEach(id => ledger = ledger.Forget(id));
        givenUp = givenUp.Except(answered);
    }

    bool StillGivenUp(string sessionId) =>
        tasks.Board.Tasks.Any(t => LatestSession(t).Map(s => s.Id == sessionId).GetValueOrDefault(false) && SessionKeeper.GivenUp(t));

    // --- the resume queue -------------------------------------------------------------------------------------------

    // Issues the next resume once SessionKeeper.Spacing has passed since the last one AND that one has answered, so two
    // are never in flight at once.
    void Drain()
    {
        if (draining || queue.IsEmpty) return;
        draining = true;
        var wait = lastIssued + SessionKeeper.Spacing - scheduler.Now;
        drainTimer.Disposable = scheduler.Schedule(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Issue);
    }

    // THE FRESH LIST (final review, item 2). The queue was decided on a poll up to minutes old (five seconds a step, after
    // a reboot twenty steps), and in that time VS Code may have brought the session back by itself, or Petre opened it.
    // Resuming a listed session starts a COPY (Global Constraints), so right before each resume the list is read again,
    // off the UI thread (about 230 ms, measured 2026-11-23, and it writes nothing to ~/.claude.json), and the resume
    // goes only if that list does not show the session. A read that fails is "cannot tell": the resume and the rest of
    // the queue are dropped, as a failed poll drops them; the next good poll queues them again.
    void Issue()
    {
        var next = NextWanted();
        if (next.HasNoValue)
        {
            draining = false;
            return;
        }

        var resume = next.Value;
        // Counts as queued while the list is read, so a poll meanwhile cannot queue it a second time.
        inFlight = resume.Session.Id;
        resumeCall.Disposable = OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .Subscribe(
                read => Rechecked(resume, read.Bind(ClaudeAgents.Parse)),
                error => Dropped(resume, $"the list read threw: {error.Message}", wholeQueue: true));
    }

    void Rechecked(KeeperStep.Resume resume, Result<IReadOnlyList<ClaudeAgent>> fresh)
    {
        if (fresh.IsFailure) Dropped(resume, $"the list could not be read: {fresh.Error}", wholeQueue: true);
        else if (fresh.Value.Any(agent => agent.SessionId == resume.Session.Id)) Dropped(resume, "the list shows it again", wholeQueue: false);
        else if (!StillWanted(resume)) Dropped(resume, "no longer wanted", wholeQueue: false);
        else Wake(resume);
    }

    // A queued resume that does not go: the queue moves on (or is emptied, when the list cannot be read).
    void Dropped(KeeperStep.Resume resume, string why, bool wholeQueue)
    {
        inFlight = Maybe<string>.None;
        draining = false;
        if (wholeQueue) queue = ImmutableQueue<KeeperStep.Resume>.Empty;
        trace?.Invoke($"claude sessions: queued resume of {resume.Session.Id} dropped, {why}");
        Drain();
    }

    void Wake(KeeperStep.Resume resume)
    {
        var session = resume.Session;
        var at = scheduler.Now;
        ledger = ledger.Record(session.Id, at);
        lastIssued = at;
        inFlight = session.Id;
        resumeCall.Disposable = OffThread(async () =>
            {
                // Read here, on `background`, for the reason given in Heard.
                var cutOff = CutOff(session);
                return (CutOff: cutOff, Result: await cli.Run(ResumeArguments(session.Id, cutOff), session.Folder, LaunchTimeout));
            })
            .Subscribe(
                answer =>
                {
                    // The queue is freed BEFORE the answer is acted on, so nothing Answered does can leave it stuck.
                    Settled(session.Id);
                    Answered(resume.TaskId, session.Id, answer.CutOff, answer.Result, at);
                    Drain();
                },
                error =>
                {
                    // Ruling 13: a fault is traced, not thrown onto the dispatcher, and the queue moves on. The ledger
                    // already holds this resume, so the keeper's grace and loop guard still apply to the session.
                    Settled(session.Id);
                    trace?.Invoke($"claude sessions: resume of {session.Id} threw: {error.Message}");
                    Drain();
                });
    }

    // The resume in flight has answered (or failed): the next may go, and for Grace from NOW the session is left alone.
    // `claude --bg --resume` can take up to its timeout, longer than the keeper's grace counted from the ISSUE, and a
    // list read before the answer does not show the woken session yet; resuming it again would start a copy (Ruling 13).
    void Settled(string sessionId)
    {
        draining = false;
        inFlight = Maybe<string>.None;
        var now = scheduler.Now;
        answered = answered.Where(pair => pair.Value + SessionKeeper.Grace > now).ToImmutableDictionary().SetItem(sessionId, now);
    }

    // The next queued resume that is still wanted; the ones that are not are dropped on the way.
    Maybe<KeeperStep.Resume> NextWanted()
    {
        while (!queue.IsEmpty)
        {
            queue = queue.Dequeue(out var head);
            if (StillWanted(head)) return head;
            trace?.Invoke($"claude sessions: queued resume of {head.Session.Id} dropped");
        }
        return Maybe<KeeperStep.Resume>.None;
    }

    // The re-check before a queued resume goes out (Review Focus 5, Global Constraints): the setting is still on, the
    // task is still open and not given up, the session is still its latest, the latest list does not show it, and its
    // last resume did not answer within the grace (Ruling 13, see Settled).
    bool StillWanted(KeeperStep.Resume resume) =>
        manager.State.ClaudeResumeInEffect
        && !AnsweredWithinGrace(resume.Session.Id)
        && tasks.Board.Tasks.TryFirst(t => t.Id == resume.TaskId)
            .Map(t => t.Status is TaskStatus.Backlog or TaskStatus.InProgress or TaskStatus.WaitingOnYou
                && !SessionKeeper.GivenUp(t)
                && LatestSession(t).Map(s => s.Id == resume.Session.Id).GetValueOrDefault(false))
            .GetValueOrDefault(false)
        && lastLive.All(agent => agent.SessionId != resume.Session.Id);

    void Answered(Guid taskId, string sessionId, bool cutOff, Result<string> result, DateTimeOffset at)
    {
        if (result.IsSuccess)
        {
            Say(taskId, cutOff
                ? $"Resumed by Deskbar at {Clock(at)}, its last turn was cut off, so it was told to continue."
                : $"Resumed by Deskbar at {Clock(at)}.");
            return;
        }

        // Review Focus 4, Ruling 10: an untrusted folder or a missing `claude`. Claude's own words go to the thread, and
        // the task waits on Petre with the sticky ResumeFailed question, so the keeper tries nothing more until he acts;
        // remembered as given up, so the ledger forgets the session once he has (ForgetAnswered).
        Say(taskId, $"Deskbar could not resume this session at {Clock(at)}:\n\n{result.Error}");
        tasks.Ask(taskId, SessionKeeper.ResumeFailed)
            .Tap(() => givenUp = givenUp.Add(sessionId))
            .TapError(e => trace?.Invoke($"claude sessions: failed resume not asked on {taskId}: {e}"));
    }

    void Say(Guid taskId, string markdown) =>
        tasks.Comment(taskId, Maybe<string>.None, markdown)
            .TapError(e => trace?.Invoke($"claude sessions: no comment on {taskId}: {e}"));

    static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm");

    static IReadOnlyList<string> ResumeArguments(string sessionId, bool cutOff) =>
        cutOff ? ["--bg", "--resume", sessionId, ContinueMessage] : ["--bg", "--resume", sessionId];

    // Waiting in the queue, or being resumed right now.
    bool Queued(string sessionId) =>
        queue.Any(r => r.Session.Id == sessionId) || inFlight.Map(id => id == sessionId).GetValueOrDefault(false) || wakingNow.Contains(sessionId);

    bool AnsweredWithinGrace(string sessionId) =>
        answered.TryGetValue(sessionId, out var at) && at + SessionKeeper.Grace > scheduler.Now;

    // --- transcripts ------------------------------------------------------------------------------------------------

    bool TranscriptExists(TaskSession session) => File.Exists(ClaudeTranscript.PathOf(claudeHome, session.Folder, session.Id));

    // Shared for reading and writing, since nothing promises Claude Code has let go of the file. An unreadable file is
    // "not cut off": the session is resumed without the continue message rather than not at all.
    bool CutOff(TaskSession session)
    {
        try
        {
            using var stream = new FileStream(ClaudeTranscript.PathOf(claudeHome, session.Folder, session.Id),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ClaudeTranscript.WasCutOff(reader.ReadToEnd().Split('\n'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // --- starting ---------------------------------------------------------------------------------------------------

    IObservable<Result<string>> Launched(TaskItem task, string folder, Result<string> launched) =>
        launched
            .Bind(output => ClaudeAgents.ShortIdFromStart(output).ToResult($"Claude did not say which session it started: {output}"))
            .Tap(shortId => claims = claims.SetItem(shortId, new StartClaim(task.Id, folder)))
            .Match(shortId => Find(shortId, FindAttempts), error => Observable.Return(Result.Failure<string>(error)));

    // Reads the list until it shows the session, a pause between reads, and joins it to its task.
    IObservable<Result<string>> Find(string shortId, int attemptsLeft) =>
        OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .SelectMany(read => read.Bind(ClaudeAgents.Parse)
                    .Map(list => list.TryFirst(agent => Matches(agent, shortId)))
                    .GetValueOrDefault(Maybe<ClaudeAgent>.None) is { HasValue: true } found
                ? Observable.Return(JoinStarted(shortId, found.Value))
                : attemptsLeft > 1
                    ? Observable.Timer(FindPause, scheduler).SelectMany(_ => Find(shortId, attemptsLeft - 1))
                    // Still claimed, so the poll joins it when it does appear (and the session's own hook may too).
                    : Observable.Return(Result.Failure<string>(
                        $"Claude started session {shortId}, but did not list it within {FindAttempts * FindPause.TotalSeconds:0} seconds. It joins the task when it appears.")));

    static bool Matches(ClaudeAgent agent, string shortId) =>
        agent.ShortId.Map(id => id == shortId).GetValueOrDefault(false) || agent.SessionId.StartsWith(shortId, StringComparison.OrdinalIgnoreCase);

    // Every claimed session the list shows joins its task (the poll's half of Start).
    void Claim(IReadOnlyCollection<ClaudeAgent> listed) =>
        claims.Keys.ToList().ForEach(shortId => listed.TryFirst(agent => Matches(agent, shortId)).Tap(agent => JoinStarted(shortId, agent)));

    // Joins a started session to its task, once: a poll may have joined it already, and then the id is all that is left.
    Result<string> JoinStarted(string shortId, ClaudeAgent agent)
    {
        if (!claims.TryGetValue(shortId, out var claim)) return agent.SessionId;
        claims = claims.Remove(shortId);
        return tasks.Join(claim.TaskId, tasks.NewSession(agent.SessionId, claim.Folder))
            .Map(() => agent.SessionId)
            .TapError(e => trace?.Invoke($"claude sessions: started session {agent.SessionId} not joined: {e}"));
    }

    // --- helpers ----------------------------------------------------------------------------------------------------

    static Maybe<TaskSession> LatestSession(TaskItem task) => task.Sessions.OrderByDescending(s => s.Joined).TryFirst();

    Maybe<string> LatestSessionOf(Guid taskId) =>
        tasks.Board.Tasks.TryFirst(t => t.Id == taskId).Bind(LatestSession).Map(s => s.Id);

    // Runs `work` on `background` and brings its answer back onto `scheduler`, where the state lives.
    IObservable<T> OffThread<T>(Func<Task<T>> work) =>
        Observable.FromAsync(work).SubscribeOn(background).ObserveOn(scheduler);
}
