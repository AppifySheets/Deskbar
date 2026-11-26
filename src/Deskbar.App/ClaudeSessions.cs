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
//   5. STOPPING FINISHED SESSIONS (task 87f899ad; SessionKeeper.Stops has the rule and its reasons). On every good read,
//      a Done or Closed task's background session that has been idle for SessionKeeper.StopAfter, for a Done task only
//      with no window of Deskbar's attached to it, and for a Closed task idle or busy only on a background job of its
//      own (task 607af1db, ClaudeAgent.TurnOver), is sent `claude stop <short id>`, which keeps its conversation
//      (measured) and closes a Closed task's attached window with it (task 8556944b, measured). The task's
//      thread says so. A stop is sent once; while the session is still listed it is not sent again for StopRetry, so a
//      stop that fails is traced and retried now and then, never every poll. Going to the task resumes it (job 4).
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
    // Kept in Core (SessionKeeper) so the prompt hook can tell it from Petre's own message (RepliedTasks, task 8bd18246).
    public const string ContinueMessage = SessionKeeper.ContinueMessage;

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

    // A stop sent to a session that the list still shows after this long is sent again (job 5). `claude stop` answers
    // in about a second and the session leaves the list at once (measured), so a session still listed this long after
    // has not stopped; ten minutes keeps a stop that keeps failing to a trace line now and then.
    static readonly TimeSpan StopRetry = TimeSpan.FromMinutes(10);

    readonly TaskService tasks;
    readonly WorkspaceManager manager;
    readonly IClaudeCli cli;
    readonly IScheduler scheduler;
    readonly IScheduler background;
    readonly Func<string, Maybe<Guid>> workspaceOfFolder;
    readonly Func<TaskItem, bool> windowOpen;

    // CommentInbox.Retire in the app: called right before every resume, so a comment waiter orphaned by the session's
    // ended process exits instead of claiming the next comment meant for the woken session (task daeaa26c).
    readonly Action<string> retireWaiter;
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

    // What the latest resume of each session failed with, while it is the latest answer (a success removes it). A click
    // within the grace of a FAILED resume hands this back instead of looking for the session in the list: nothing was
    // started, so the look could only end in "was resumed, but Claude Code did not list it", which is untrue (task
    // 0bdf74d0: five clicks on a session whose worktree was gone put five of those under the real reason).
    ImmutableDictionary<string, string> failedWith = ImmutableDictionary<string, string>.Empty;

    // The wake a click has under way for each session, resume and look together (WakeNow), for the clicks after it to share.
    ImmutableDictionary<string, IObservable<Result<ClaudeAgent>>> clickWakes =
        ImmutableDictionary<string, IObservable<Result<ClaudeAgent>>>.Empty;

    // The list wait under way for each session (LookFor). A click that comes while one runs shares it, so however many
    // clicks there are, one answer comes back and the thread gets at most one "did not list it".
    ImmutableDictionary<string, IObservable<Result<ClaudeAgent>>> looking =
        ImmutableDictionary<string, IObservable<Result<ClaudeAgent>>>.Empty;

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

    // The usage-limit stops already said in their task's thread (NoteLimits), by session and the limit's reset moment, so
    // each is said once a run; a limit hit again after a wake has a new reset moment and is said again.
    ImmutableHashSet<(string SessionId, DateTimeOffset ResetsAt)> limitNoted = ImmutableHashSet<(string, DateTimeOffset)>.Empty;

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

    // Job 5: since when each listed session has been idle in every good read; one seen busy, or gone from the list, is
    // dropped, so its minute starts again. And when a stop was sent to each session the list still shows.
    ImmutableDictionary<string, DateTimeOffset> idleSince = ImmutableDictionary<string, DateTimeOffset>.Empty;
    // The same for a turn being over (ClaudeAgent.TurnOver: idle, or busy only on a background job of its own), the
    // clock a Closed task's session is stopped by (task 607af1db, SessionKeeper.Stops).
    ImmutableDictionary<string, DateTimeOffset> turnOverSince = ImmutableDictionary<string, DateTimeOffset>.Empty;
    ImmutableDictionary<string, DateTimeOffset> stopSent = ImmutableDictionary<string, DateTimeOffset>.Empty;

    // RULING 20: why the list cannot be read, in one line, for Manage to show under the resume box; none after a good
    // read and while the feature is off.
    readonly BehaviorSubject<Maybe<string>> listProblem = new(Maybe<string>.None);

    // The manager session's folder, or none; see the constructor.
    readonly string? managerFolder;
    readonly Func<string, string> longPath;

    // The listed sessions the keeper may adopt, resume or stop: everyone but the manager. `live` and the tasks'
    // reconcile keep the full list, since the manager's click reads it.
    IReadOnlyList<ClaudeAgent> KeeperView(IReadOnlyList<ClaudeAgent> listed) =>
        managerFolder is null ? listed : ManagerSession.Others(listed, managerFolder, longPath);

    /// <param name="scheduler">Where answers land and timers run: the UI thread in the app.</param>
    /// <param name="workspaceOfFolder">The workspace of the editor window showing a folder, or none, for adopted sessions.</param>
    /// <param name="claudeHome">The folder that holds Claude Code's `projects`, normally %USERPROFILE%\.claude.</param>
    /// <param name="background">Where `claude` runs and transcripts are read: the thread pool unless a test says otherwise.</param>
    /// <param name="windowOpen">
    /// Whether a window of Deskbar's is attached to the task's session (ClaudeTaskActions.HasWindow); a finished task's
    /// session is not stopped while one is (job 5). Left out, none ever is.
    /// </param>
    /// <param name="managerFolder">
    /// The manager session's folder (ManagerSession.FolderIn). A session listed there is Petre's manager, started,
    /// resumed and stopped only by his click, so the keeper neither adopts nor stops it. None, nothing is left out.
    /// </param>
    /// <param name="longPath">Expands 8.3 short forms when comparing folders; the file system's answer unless a test says otherwise.</param>
    public ClaudeSessions(TaskService tasks, WorkspaceManager manager, IClaudeCli cli, IScheduler scheduler,
        Func<string, Maybe<Guid>> workspaceOfFolder, string claudeHome, IScheduler? background = null, Action<string>? trace = null,
        Func<TaskItem, bool>? windowOpen = null, string? managerFolder = null, Func<string, string>? longPath = null,
        Func<string, bool>? folderExists = null, Action<string>? retireWaiter = null)
    {
        this.retireWaiter = retireWaiter ?? (_ => { });
        this.folderExists = folderExists ?? Directory.Exists;
        this.managerFolder = managerFolder;
        this.longPath = longPath ?? Deskbar.Windows.Control.LongPath.Of;
        this.windowOpen = windowOpen ?? (_ => false);
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
        failedWith = failedWith.Remove(sessionId);
    }

    /// <summary>
    /// Starts a background session on the task in <paramref name="folder"/> with the task as its first message, joins
    /// the task to it and returns its full session id. A failure carries Claude's own words: an untrusted folder makes
    /// `claude --bg` exit 1 with the reason on stderr (measured), and that reason is what Petre needs to see. Called on
    /// the UI thread; the returned task completes there.
    /// </summary>
    //
    // The start always names a model (ClaudeStart.Arguments): the one given for this session (`task start --model`),
    // otherwise the setting (AppState.ClaudeTaskModelInEffect, read here on the UI thread), never Claude Code's own
    // default. The resumes below pass none, because a session wakes with the model it was started on (measured).
    public Task<Result<string>> Start(TaskItem task, string folder, Maybe<string> model = default) =>
        Observable.Return(ClaudeStart.Arguments(task, model.GetValueOrDefault(manager.State.ClaudeTaskModelInEffect)))
            .SelectMany(arguments => OffThread(() => cli.Run(arguments, folder, LaunchTimeout)))
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

    /// <summary>
    /// A comment wakes its session (plan docs/superpowers/plans/2026-11-24-comment-wakes-session.md, Task 5): resumes a
    /// session that does not run, with <paramref name="message"/> (Petre's comment, SessionBriefing.Commented) as the
    /// message it wakes to, from its own folder. A session the fresh list shows, or one being resumed already or a moment
    /// ago, is NOT resumed (a copy would start, or the message would be lost to the resume in flight): that is a failure,
    /// and the caller (CommentWaker) puts the comment in its inbox instead. Called on the UI thread.
    /// </summary>
    public Task<Result> ResumeWith(Guid taskId, TaskSession session, string message) =>
        OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .SelectMany(read => read.Bind(ClaudeAgents.Parse).Match(
                list => list.Any(agent => agent.SessionId == session.Id) || Waking(session.Id) || AnsweredWithinGrace(session.Id)
                    ? Observable.Return(Result.Failure("The session is running or being resumed already."))
                    : WakeNow(taskId, session, message).Select(woken => woken.IsSuccess ? Result.Success() : Result.Failure(woken.Error)),
                error => Observable.Return(Result.Failure($"Claude Code's list of sessions could not be read: {error}"))))
            // A fault the CLI did not turn into a Result is a failure (the caller then uses the inbox), never a crash.
            .Catch((Exception e) => Observable.Return(Result.Failure(e.Message)))
            .TakeUntil(stopping)
            .DefaultIfEmpty(Result.Failure("Deskbar closed before the session was resumed."))
            .ToTask();

    /// <summary>
    /// `claude stop <short id>` now, for a background session whose comment waiter lapsed (CommentWake.StopThenResume):
    /// the stop returns once the process is gone (measured 2026-11-23), so a resume right after it does not start a copy.
    /// </summary>
    public Task<Result> StopNow(string shortId) =>
        // Not while Deskbar is waking that session itself or has just woken it (task daeaa26c review, item 1): a keeper
        // restart at a limit's reset is when the manager tends to comment, and the comment's stop would kill the session
        // the restart has just started. Refused, the caller's resume is refused too (Waking) and the comment goes to the
        // inbox, which the restarted session's first Stop hook reads.
        BeingWoken(shortId)
            ? Task.FromResult(Result.Failure("Deskbar is waking this session already."))
            : OffThread(() => cli.Run(["stop", shortId], null, LaunchTimeout))
            .Select(stopped => stopped.IsSuccess ? Result.Success() : Result.Failure(stopped.Error))
            .TakeUntil(stopping)
            .DefaultIfEmpty(Result.Failure("Deskbar closed before the session was stopped."))
            .ToTask();

    // Runs on `scheduler` (inside ResumeNow's SelectMany), so it may touch the state. `message`, when given, is what the
    // session wakes to instead of the usual (a comment, ResumeWith).
    IObservable<Result<ClaudeAgent>> WakeNow(Guid taskId, TaskSession session, string? message = null)
    {
        // Another click is waking it right now: this one shares that wake and its answer, so it neither looks for the
        // session before the resume has answered (a look begun then could end first and call a good resume a failure)
        // nor says anything of its own (review of task 0bdf74d0).
        if (message is null && clickWakes.TryGetValue(session.Id, out var underWay))
            return underWay;

        // Woken a moment ago, but that resume FAILED: nothing runs, so there is nothing to look for. Its reason is in the
        // thread already and the task asks about it, so the failure is handed back and nothing more is said. Once the
        // grace has passed, a click tries again.
        if (!Waking(session.Id) && AnsweredWithinGrace(session.Id) && failedWith.TryGetValue(session.Id, out var failure))
            return Observable.Return(Result.Failure<ClaudeAgent>(failure));

        // Already being woken, or woken a moment ago and not listed yet: a second resume would start a copy.
        if (Waking(session.Id) || AnsweredWithinGrace(session.Id))
            return LookFor(taskId, session.Id);

        // Waiting its turn in the queue: woken now instead, so the queue does not wake it a second time.
        queue = ImmutableQueue.CreateRange(queue.Where(r => r.Session.Id != session.Id));
        var at = scheduler.Now;
        ledger = ledger.Record(session.Id, at);
        wakingNow = wakingNow.Add(session.Id);
        var wake = OffThread(async () =>
            {
                // Read here, on `background`, for the reason given in Heard.
                var cutOff = CutOff(session);
                retireWaiter(session.Id);
                return (CutOff: cutOff, Result: await cli.Run(message is null ? ResumeArguments(session.Id, cutOff) : ["--bg", "--resume", session.Id, message], session.Folder, LaunchTimeout));
            })
            // A fault the CLI did not turn into a Result is a failure like any other (Ruling 13), not a dispatcher crash.
            .Catch((Exception e) => Observable.Return((CutOff: false, Result: Result.Failure<string>(e.Message))))
            .SelectMany(answer =>
            {
                wakingNow = wakingNow.Remove(session.Id);
                // From this answer on, the session is left alone for Grace, as after the queue's own resume (Settled).
                var now = scheduler.Now;
                answered = answered.Where(pair => pair.Value + SessionKeeper.Grace > now).ToImmutableDictionary().SetItem(session.Id, now);
                Answered(taskId, session.Id, answer.CutOff, answer.Result);
                return answer.Result.IsSuccess
                    // A comment's resume (ResumeWith) is done once the command ran: the comment went with it, and waiting for
                    // the list would call a late listing a failure (final review, I4).
                    ? message is not null
                        ? Observable.Return(Result.Success(new ClaudeAgent(session.Id, Maybe<string>.None, session.Folder, true, true, "")))
                        : LookFor(taskId, session.Id)
                    : Observable.Return(Result.Failure<ClaudeAgent>(answer.Result.Error));
            });
        if (message is not null) return wake;

        // A click's wake is shared with the clicks that come while it runs (above), and leaves `clickWakes` once answered.
        var shared = wake
            .Finally(() => clickWakes = clickWakes.Remove(session.Id))
            .Replay(1)
            .RefCount();
        clickWakes = clickWakes.SetItem(session.Id, shared);
        return shared;
    }

    // Looks for the session in the list (Listed), or joins the look already under way for it. The shared look leaves
    // `looking` once it ends, on `scheduler` like every other field change here, so the next click after it starts afresh.
    IObservable<Result<ClaudeAgent>> LookFor(Guid taskId, string sessionId)
    {
        if (looking.TryGetValue(sessionId, out var underWay)) return underWay;
        var look = Listed(taskId, sessionId, FindAttempts)
            .Finally(() => looking = looking.Remove(sessionId))
            .Replay(1)
            .RefCount();
        looking = looking.SetItem(sessionId, look);
        return look;
    }

    // Being resumed right now: the queue's one in flight, or a click's.
    bool Waking(string sessionId) => inFlight.Map(id => id == sessionId).GetValueOrDefault(false) || wakingNow.Contains(sessionId);

    // Reads the list until it shows the session, a pause between reads (about ten seconds in all, as Start's Find).
    // Not found in time is said in the task's thread, so a click that opened nothing has its reason where Petre looks.
    IObservable<Result<ClaudeAgent>> Listed(Guid taskId, string sessionId, int attemptsLeft) =>
        OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .SelectMany(read => FailedMeanwhile(sessionId) is { HasValue: true } failed
                // The resume this look waited on (another click's, or the queue's) failed while it looked: nothing will
                // be listed, its reason is in the thread already, and "was resumed" would be untrue (review of 0bdf74d0).
                ? Observable.Return(Result.Failure<ClaudeAgent>(failed.Value))
                : read.Bind(ClaudeAgents.Parse)
                    .Map(list => list.TryFirst(agent => agent.SessionId == sessionId))
                    .GetValueOrDefault(Maybe<ClaudeAgent>.None) is { HasValue: true } found
                ? Observable.Return(Result.Success(found.Value))
                : attemptsLeft > 1
                    ? Observable.Timer(FindPause, scheduler).SelectMany(_ => Listed(taskId, sessionId, attemptsLeft - 1))
                    : Observable.Return(Said(taskId, Result.Failure<ClaudeAgent>(
                        $"Session {sessionId} was resumed, but Claude Code did not list it within {FindAttempts * FindPause.TotalSeconds:0} seconds, so no window was opened on it."))));

    // The session's latest resume answered with a failure and none is under way now. Runs on `scheduler`.
    Maybe<string> FailedMeanwhile(string sessionId) =>
        !Waking(sessionId) && failedWith.TryGetValue(sessionId, out var error) ? error : Maybe<string>.None;

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
        // has worked on for 15 minutes goes back to Petre, and back to Claude once its session works again. Any session
        // listed busy keeps its task with Claude; only one listed for BusyGrace takes a task back, because a session just
        // resumed reports busy for up to 30 seconds while idle (and right after a Deskbar restart every session is inside
        // the grace, which must not hand a busy one's task back). An unreadable list never gets here (see above), so
        // "cannot tell" moves nothing.
        var busy = listed.Where(agent => agent.Busy).ToArray();
        tasks.Stalled(
            busy.Select(agent => agent.SessionId).ToHashSet(),
            busy.Where(agent => now - firstSeen[agent.SessionId] >= StalledTasks.BusyGrace).Select(agent => agent.SessionId).ToHashSet());
        ForgetAnswered();

        // With the resume off, the keeper is still asked (for its adoptions), but told every transcript exists, so it
        // reads no files for steps nobody will take.
        var resuming = manager.State.ClaudeResumeInEffect;
        // The manager is left out of what the keeper sees: it is never adopted as a task nor stopped as a finished one.
        var keeperView = KeeperView(listed);
        SessionKeeper.Plan(tasks.Board, keeperView, ledger,
                resuming ? Resumable : _ => true,
                // Whether a turn was cut off is read when the resume is ISSUED, off the UI thread (Issue), not here: a
                // transcript can run to tens of MB and this runs on every poll for every session waiting in the queue.
                _ => false,
                scheduler.Now,
                // Ruling 18: no step for a session missing less than SessionKeeper.Settle.
                AbsentSince,
                // Task daeaa26c: a session stopped on a usage limit waits for the reset, then is woken (a listed one is
                // stopped first). Read from the transcript's tail, cached until the file changes (LastTurnError).
                resuming ? LimitError : null)
            .ToList()
            .ForEach(step => Take(step, resuming));
        NoteLimits(listed, now, resuming);

        // Job 5: finished tasks' idle sessions are stopped. Asked after the keeper's steps, on the same board and list.
        idleSince = listed.Where(agent => !agent.Busy).Select(agent => agent.SessionId).Distinct()
            .ToImmutableDictionary(id => id, id => idleSince.TryGetValue(id, out var since) ? since : now);
        turnOverSince = listed.Where(agent => agent.TurnOver).Select(agent => agent.SessionId).Distinct()
            .ToImmutableDictionary(id => id, id => turnOverSince.TryGetValue(id, out var since) ? since : now);
        stopSent = stopSent.Where(pair => listedIds.Contains(pair.Key)).ToImmutableDictionary();
        SessionKeeper.Stops(tasks.Board, keeperView,
                id => idleSince.TryGetValue(id, out var since) ? since : now,
                id => turnOverSince.TryGetValue(id, out var since) ? since : now,
                windowOpen, now)
            // Not while it is being resumed (a click on the Done task), and not again while a stop sent is on its way.
            .Where(stop => !Queued(stop.Agent.SessionId) && !AnsweredWithinGrace(stop.Agent.SessionId))
            .Where(stop => !stopSent.TryGetValue(stop.Agent.SessionId, out var sent) || now - sent >= StopRetry)
            .ToList()
            .ForEach(Stop);

        live.OnNext(listed);
        Drain();
    }

    // `claude stop <short id>` off the UI thread. The short id is what the stop takes (`claude --bg` prints it, and it is
    // the first eight characters of the session id, also after a resume: measured). Success is said in the task's
    // thread, so the session shown as ended there has its reason; a failure is traced only, and retried after StopRetry.
    void Stop(KeeperStep.Stop stop)
    {
        var sessionId = stop.Agent.SessionId;
        var shortId = stop.Agent.ShortId.GetValueOrDefault(sessionId[..Math.Min(8, sessionId.Length)]);
        var at = scheduler.Now;
        stopSent = stopSent.SetItem(sessionId, at);
        trace?.Invoke($"claude sessions: stopping {sessionId}, its task {stop.TaskId} is finished");
        OffThread(() => cli.Run(["stop", shortId], null, LaunchTimeout))
            .TakeUntil(stopping)
            .Subscribe(
                result => result
                    .Tap(() => Say(stop.TaskId, "Stopped, since the task is finished. Open the task to resume it."))
                    .TapError(e => trace?.Invoke($"claude sessions: stop of {sessionId} failed: {e}")),
                error => trace?.Invoke($"claude sessions: stop of {sessionId} threw: {error.Message}"));
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
            // A restart (StopFirst) never while a window of Deskbar's is attached to the task: `claude stop` would close it
            // under Petre (measured 2026-11-25), and with the window open he sees the limit's message and can type himself.
            case KeeperStep.Resume resume when resuming && !Queued(resume.Session.Id) && !(resume.StopFirst.HasValue && WindowOpenOn(resume.TaskId)):
                queue = queue.Enqueue(resume);
                break;
        }
    }

    bool BeingWoken(string shortId) =>
        queue.Select(r => r.Session.Id).Concat(inFlight.ToList()).Concat(wakingNow).Concat(answered.Keys)
            .Any(id => id.StartsWith(shortId, StringComparison.OrdinalIgnoreCase) && (Queued(id) || AnsweredWithinGrace(id)));

    bool WindowOpenOn(Guid taskId) =>
        tasks.Board.Tasks.TryFirst(t => t.Id == taskId).Map(windowOpen).GetValueOrDefault(false);

    // SHOWN AS STOPPED (task daeaa26c). A session whose turn died on the usage limit is still listed, idle, and its hooks
    // never said it ended, so the board went on showing it live with its last status line ("PR 574 open, CI running")
    // for the eight hours until the limit reset. Once per stop, the open task's thread gets a line saying it stopped and
    // when the limit resets, and its status line, which the bar shows, says the same until the session writes its own
    // again after it wakes. A status line already saying exactly that is the same stop said before a Deskbar restart,
    // so nothing is repeated.
    //
    // The line is Deskbar's, so Deskbar takes it back (review, item 4): once the session is no longer stopped on the
    // limit (it was woken, or Petre typed in it) or the reset has passed, a status line still saying exactly this is
    // cleared, and the session writes its own again.
    void NoteLimits(IReadOnlyCollection<ClaudeAgent> listed, DateTimeOffset now, bool resuming)
    {
        ClearLimitLines(now);
        tasks.Board.Tasks
            .Where(task => task.IsClaudes && task.Status is TaskStatus.Backlog or TaskStatus.InProgress or TaskStatus.WaitingOnYou)
            .Where(task => !SessionKeeper.GivenUp(task))
            .Select(task => (Task: task, Session: LatestSession(task)))
            .Where(pair => pair.Session is { HasValue: true, Value.Replaced: false })
            .Select(pair => (pair.Task, Session: pair.Session.Value, ResetsAt: LimitedUntil(pair.Session.Value)))
            .Where(item => item.ResetsAt.HasValue && now < SessionKeeper.WakeAfterLimit(item.ResetsAt.Value))
            .Where(item => !limitNoted.Contains((item.Session.Id, item.ResetsAt.Value)))
            .ToList()
            .ForEach(item =>
            {
                limitNoted = limitNoted.Add((item.Session.Id, item.ResetsAt.Value));
                var when = Clock(item.ResetsAt.Value);
                var status = LimitLine + when;
                if (item.Task.StatusLine == status) return;
                // An interactive session (a VS Code panel, a terminal) cannot be restarted from outside (SessionKeeper's
                // header), and a background one with a Deskbar window open on it is not (Take), so their line says what
                // Petre can do instead of promising a wake.
                var interactive = listed.Any(agent => agent.SessionId == item.Session.Id && !agent.Background);
                Say(item.Task.Id,
                    interactive ? $"Stopped on the usage limit, which resets at {when}. It runs in a window Deskbar cannot restart, so type in it once the limit has reset."
                    : windowOpen(item.Task) ? $"Stopped on the usage limit, which resets at {when}. Its window is open, so type in it then, or close it and Deskbar resumes it."
                    : resuming ? $"Stopped on the usage limit. Deskbar resumes it once the limit resets at {when}."
                    : $"Stopped on the usage limit, which resets at {when}.");
                tasks.SetStatusLine(item.Task.Id, status)
                    .TapError(e => trace?.Invoke($"claude sessions: no status line on {item.Task.Id}: {e}"));
            });
    }

    // The status line NoteLimits writes, before the time.
    const string LimitLine = "Stopped on the usage limit until ";

    // Clears the line NoteLimits wrote once it no longer holds: the open task's latest session is not stopped on a limit
    // any more, or the reset has passed. Only a status line still exactly Deskbar's is touched, never the session's own.
    void ClearLimitLines(DateTimeOffset now) =>
        tasks.Board.Tasks
            .Where(task => task.StatusLine.StartsWith(LimitLine, StringComparison.Ordinal))
            .Where(task => LatestSession(task)
                .Map(session => LimitedUntil(session).Map(resetsAt => now >= SessionKeeper.WakeAfterLimit(resetsAt)).GetValueOrDefault(true))
                .GetValueOrDefault(true))
            .ToList()
            .ForEach(ClearLimitLine);

    void ClearLimitLine(TaskItem task)
    {
        if (!task.StatusLine.StartsWith(LimitLine, StringComparison.Ordinal)) return;
        tasks.SetStatusLine(task.Id, "")
            .TapError(e => trace?.Invoke($"claude sessions: status line not cleared on {task.Id}: {e}"));
    }

    // A reset moment as "Thu 06:00", in this machine's time zone. Always with the day, so the same reset reads the same
    // whatever the time now is: NoteLimits compares the status line with it to tell a stop it has already said (review,
    // item 4: "06:00" before midnight and "Thu 06:00" after it made a restarted Deskbar say it twice).
    static string Clock(DateTimeOffset at) =>
        at.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

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
        // A restart (StopFirst) expects its session listed; only one that is working again is left alone.
        else if (resume.StopFirst.HasNoValue && fresh.Value.Any(agent => agent.SessionId == resume.Session.Id)) Dropped(resume, "the list shows it again", wholeQueue: false);
        else if (resume.StopFirst.HasValue && fresh.Value.Any(agent => agent.SessionId == resume.Session.Id && agent.Busy)) Dropped(resume, "it is working again", wholeQueue: false);
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
                // A restart (task daeaa26c): the listed session is stopped first, since resuming one that runs starts a
                // copy. A stop that fails is no resume either, for the same reason; the ledger already counts it, so the
                // grace and the loop guard space out the next try.
                if (resume.StopFirst.HasValue && await cli.Run(["stop", resume.StopFirst.Value], null, LaunchTimeout) is { IsFailure: true } stop)
                    return (CutOff: cutOff, Result: Result.Failure<string>(stop.Error), Stopped: false);
                retireWaiter(session.Id);
                return (CutOff: cutOff, Result: await cli.Run(ResumeArguments(session.Id, cutOff), session.Folder, LaunchTimeout), Stopped: true);
            })
            .Subscribe(
                answer =>
                {
                    // The queue is freed BEFORE the answer is acted on, so nothing Answered does can leave it stuck.
                    Settled(session.Id);
                    if (answer.Stopped) Answered(resume.TaskId, session.Id, answer.CutOff, answer.Result, resume.AfterLimit);
                    else trace?.Invoke($"claude sessions: restart of {session.Id} not made, its stop failed: {answer.Result.Error}");
                    Drain();
                },
                error =>
                {
                    // Ruling 13: a fault is traced, not thrown onto the dispatcher, and the queue moves on. The ledger
                    // already holds this resume, so the keeper's grace and loop guard still apply to the session.
                    Settled(session.Id);
                    // A fault says nothing about whether claude started (a kill that failed, say), so an older failure is
                    // forgotten and a click within the grace looks for the session instead of handing that back.
                    failedWith = failedWith.Remove(session.Id);
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
                && LatestSession(t).Map(s => s.Id == resume.Session.Id).GetValueOrDefault(false)
                // A restart: still no window on it, and still stopped on the limit (Petre may have typed in it since).
                && (resume.StopFirst.HasNoValue || (!windowOpen(t) && LimitedUntil(resume.Session).HasValue)))
            .GetValueOrDefault(false)
        && (resume.StopFirst.HasValue
            ? lastLive.All(agent => agent.SessionId != resume.Session.Id || !agent.Busy)
            : lastLive.All(agent => agent.SessionId != resume.Session.Id));

    void Answered(Guid taskId, string sessionId, bool cutOff, Result<string> result, bool afterLimit = false)
    {
        // Kept for a click within the grace (WakeNow), so it hands this back instead of looking for a session that never started.
        failedWith = result.IsSuccess ? failedWith.Remove(sessionId) : failedWith.SetItem(sessionId, result.Error);
        if (result.IsSuccess)
        {
            // The thread's header already names Deskbar and the time (task aa42794f, "too much bla bla"), so the line
            // says only what happened.
            Say(taskId, afterLimit ? "Resumed, since the usage limit it stopped on has reset. It was told to continue."
                : cutOff ? "Resumed. Its last turn was cut off, so it was told to continue." : "Resumed.");
            if (afterLimit) tasks.Board.Tasks.TryFirst(t => t.Id == taskId).Tap(ClearLimitLine);
            return;
        }

        // Review Focus 4, Ruling 10: an untrusted folder or a missing `claude`. Claude's own words go to the thread, and
        // the task waits on Petre with the sticky ResumeFailed question, so the keeper tries nothing more until he acts;
        // remembered as given up, so the ledger forgets the session once he has (ForgetAnswered).
        Say(taskId, $"Could not resume this session:\n\n{result.Error}");
        tasks.Ask(taskId, SessionKeeper.ResumeFailed)
            .Tap(() => givenUp = givenUp.Add(sessionId))
            .TapError(e => trace?.Invoke($"claude sessions: failed resume not asked on {taskId}: {e}"));
    }

    void Say(Guid taskId, string markdown) =>
        tasks.Note(taskId, markdown)
            .TapError(e => trace?.Invoke($"claude sessions: no comment on {taskId}: {e}"));


    static IReadOnlyList<string> ResumeArguments(string sessionId, bool cutOff) =>
        cutOff ? ["--bg", "--resume", sessionId, ContinueMessage] : ["--bg", "--resume", sessionId];

    // Waiting in the queue, or being resumed right now.
    bool Queued(string sessionId) =>
        queue.Any(r => r.Session.Id == sessionId) || inFlight.Map(id => id == sessionId).GetValueOrDefault(false) || wakingNow.Contains(sessionId);

    bool AnsweredWithinGrace(string sessionId) =>
        answered.TryGetValue(sessionId, out var at) && at + SessionKeeper.Grace > scheduler.Now;

    // --- transcripts ------------------------------------------------------------------------------------------------

    // Where a session's transcript is: under the folder it joined from, or, for a session that went on into a git
    // worktree, under that worktree's project folder (ClaudeTranscript.Candidates has the measurement). Used by the
    // keeper (expired means give up), by the thread's session list (expired means no Open, through the App's
    // TranscriptExists seam) and by CutOff, so all three find the same file.
    //
    // A found path is remembered per session, so the search over every project folder runs once per session rather than
    // on every poll. A miss is NOT remembered (CLAUDE.md, "never cache a lookup failure that can succeed later"): a
    // session that has just started has no file yet. A remembered path that has since gone is looked for afresh.
    // Concurrent, because CutOff runs off the UI thread when a resume is issued.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> transcriptFound = new();

    public Maybe<string> TranscriptOf(TaskSession session) =>
        transcriptFound.TryGetValue(session.Id, out var known) && File.Exists(known)
            ? known
            : ClaudeTranscript.Candidates(claudeHome, session.Folder, session.Id, ProjectFolders())
                .TryFirst(File.Exists)
                .Tap(path => transcriptFound[session.Id] = path);

    public bool TranscriptExists(TaskSession session) => TranscriptOf(session).HasValue;

    /// <summary>
    /// Whether a session can be resumed at all: its transcript is there AND the folder it ran in still is (task 0bdf74d0).
    /// `claude --bg --resume` runs in that folder, so a session whose git worktree was removed after its work merged can
    /// never come back, however often it is clicked. Such a session counts as expired everywhere this is the seam (the
    /// thread, the keeper, a comment's wake): the thread then offers Start Claude on this instead of an Open that fails.
    /// A session with no folder recorded ("") is judged on its transcript alone, as before.
    /// </summary>
    public bool Resumable(TaskSession session) =>
        (string.IsNullOrEmpty(session.Folder) || folderExists(session.Folder)) && TranscriptExists(session);

    // Directory.Exists in the app; tests pass their own, since their sessions' folders are made-up paths.
    readonly Func<string, bool> folderExists;

    // The folders under <claudeHome>\projects, or none when it cannot be listed (no Claude Code yet, or no access): then
    // only the joined folder's own path is tried, which is how this behaved before.
    IEnumerable<string> ProjectFolders()
    {
        try
        {
            return Directory.GetDirectories(Path.Combine(claudeHome, "projects"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // WHETHER THE LAST TURN DIED ON AN API ERROR (ClaudeTranscript.LastTurnError, task daeaa26c). Asked on every poll for
    // each open task's latest session (the keeper, NoteLimits) and for each comment (CommentWaker), so it reads only the
    // transcript's last TailBytes, and remembers the answer per file until the file's length or write time changes. The
    // last user or assistant entry always lies in the tail unless it is itself longer than the tail; then the tail holds
    // no whole entry at all and the answer is "no error", the side that changes nothing. Unreadable is "no error" too.
    const int TailBytes = 256 * 1024;

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, Maybe<TurnError> Error)> turnErrors = new();

    public Maybe<TurnError> LastTurnError(TaskSession session)
    {
        try
        {
            if (TranscriptOf(session).GetValueOrDefault() is not { } path) return Maybe<TurnError>.None;
            // Length and write time are taken BEFORE the content (review, item 3): taken after, an entry appended between
            // the two reads would be cached as "no error" under the new length, and never read again while the session
            // sits there.
            var file = new FileInfo(path);
            var (length, written) = (file.Length, file.LastWriteTimeUtc);
            if (turnErrors.TryGetValue(path, out var known) && known.Length == length && known.Written == written) return known.Error;
            var error = ClaudeTranscript.LastTurnError(Tail(path));
            turnErrors[path] = (length, written, error);
            return error;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Maybe<TurnError>.None;
        }
    }

    /// <summary>The moment the usage limit the session's last turn died on resets; None when it did not die on one.</summary>
    public Maybe<DateTimeOffset> LimitedUntil(TaskSession session) => LastTurnError(session).Bind(error => error.ResetsAt);

    // The error itself, for the keeper, only when it is a usage limit (it names a reset).
    Maybe<TurnError> LimitError(TaskSession session) => LastTurnError(session).Where(error => error.ResetsAt.HasValue);

    /// <summary>
    /// The session's last turn ended on an API error, so no Stop hook ran and no comment waiter can be alive for it
    /// (CommentWaker treats its waiter as lapsed).
    /// </summary>
    public bool EndedOnApiError(TaskSession session) => LastTurnError(session).HasValue;

    // The file's last TailBytes as lines. A tail that starts inside the file starts inside a line (maybe inside a
    // character), so that first piece is dropped: it is not a whole entry.
    static IReadOnlyList<string> Tail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n');
        return start > 0 ? lines.Skip(1).ToList() : lines;
    }

    // Shared for reading and writing, since nothing promises Claude Code has let go of the file. An unreadable file is
    // "not cut off": the session is resumed without the continue message rather than not at all.
    bool CutOff(TaskSession session)
    {
        try
        {
            if (TranscriptOf(session).GetValueOrDefault() is not { } path) return false;
            using var stream = new FileStream(path,
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
            .Match(shortId => Find(shortId), error => Observable.Return(Result.Failure<string>(error)));

    // Reads the list until it shows the session, and joins it to its task. Still claimed when it is not found in time,
    // so the poll joins it when it does appear (and the session's own hook may too).
    IObservable<Result<string>> Find(string shortId) =>
        Found(shortId, FindAttempts,
                $"Claude started session {shortId}, but did not list it within {FindAttempts * FindPause.TotalSeconds:0} seconds. It joins the task when it appears.")
            .Select(found => found.Bind(agent => JoinStarted(shortId, agent)));

    // Reads the list until it shows the session named by `id` (its short id, or its full id: Matches takes either), a
    // pause between reads, about ten seconds in all. Shared by a task's start (Find) and the manager's (RunAndFind).
    IObservable<Result<ClaudeAgent>> Found(string id, int attemptsLeft, string notFound) =>
        OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .SelectMany(read => read.Bind(ClaudeAgents.Parse)
                    .Map(list => list.TryFirst(agent => Matches(agent, id)))
                    .GetValueOrDefault(Maybe<ClaudeAgent>.None) is { HasValue: true } found
                ? Observable.Return(Result.Success(found.Value))
                : attemptsLeft > 1
                    ? Observable.Timer(FindPause, scheduler).SelectMany(_ => Found(id, attemptsLeft - 1, notFound))
                    : Observable.Return(Result.Failure<ClaudeAgent>(notFound)));

    /// <summary>
    /// The manager session's start and resume (ManagerActions; docs/superpowers/specs/2026-11-25-manager-session-design.md):
    /// runs `claude` with <paramref name="arguments"/> in <paramref name="folder"/> and answers the session as the list
    /// shows it once it does. A start names its session by the short id `claude --bg` prints; a resume passes the
    /// <paramref name="sessionId"/> it woke, since what a resume prints is not the start's line. No task is claimed and
    /// none joined: the manager has no task of its own. A failure carries Claude's own words (an untrusted folder's
    /// reason, say). Called on the UI thread; the returned task completes there.
    /// </summary>
    public Task<Result<ClaudeAgent>> RunAndFind(IReadOnlyList<string> arguments, string folder, string? sessionId = null) =>
        OffThread(() => cli.Run(arguments, folder, LaunchTimeout))
            .SelectMany(ran => ran
                .Bind(output => sessionId is not null
                    ? Result.Success(sessionId)
                    : ClaudeAgents.ShortIdFromStart(output).ToResult($"Claude did not say which session it started: {output}"))
                .Match(id => Found(id, FindAttempts,
                        $"Claude started session {id}, but did not list it within {FindAttempts * FindPause.TotalSeconds:0} seconds, so no window was opened on it."),
                    error => Observable.Return(Result.Failure<ClaudeAgent>(error))))
            // A fault the CLI did not turn into a Result is a failure like any other, never a dispatcher crash.
            .Catch((Exception e) => Observable.Return(Result.Failure<ClaudeAgent>(e.Message)))
            .TakeUntil(stopping)
            .DefaultIfEmpty(Result.Failure<ClaudeAgent>("Deskbar closed before the session was found."))
            .ToTask();

    /// <summary>
    /// `claude agents --json` read now, for the manager's click, which must not decide on a list up to a poll old: a
    /// manager started a moment ago is not in the last poll yet, and resuming it would start a COPY. Also answers while
    /// the poll is off. A read that fails or cannot be parsed is a failure ("cannot tell"). Called on the UI thread; the
    /// returned task completes there.
    /// </summary>
    public Task<Result<IReadOnlyList<ClaudeAgent>>> ListNow() =>
        OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .Select(read => read.Bind(ClaudeAgents.Parse))
            .Catch((Exception e) => Observable.Return(Result.Failure<IReadOnlyList<ClaudeAgent>>(e.Message)))
            .TakeUntil(stopping)
            .DefaultIfEmpty(Result.Failure<IReadOnlyList<ClaudeAgent>>("Deskbar closed before the sessions were read."))
            .ToTask();

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
