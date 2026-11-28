using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// WHAT ONE CLICK ON THE MANAGER BUTTON DOES (and the pipe's `task manager`, its twin). Design:
// docs/superpowers/specs/2026-11-25-manager-session-design.md, sections "The button" and "Where it runs and what it is
// told"; the claude flags were measured in docs/superpowers/notes/2026-11-25-manager-session-probes.md.
//
// The manager is a Fable background Claude Code session, one per local calendar day, running in its own folder
// (<state dir>\manager) and coordinating the user's task board. Every rule about it lives in Core (ManagerSession) and is
// tested there; this class joins those rules to the pieces that act:
//
//   1. The list is read FRESH (ClaudeSessions.ListNow), never taken from the last poll: a manager started a moment ago is
//      not in a poll up to fifteen seconds old, and ManagerSession.Next would then resume a session that is running,
//      which starts a forked COPY (probe notes, "Resume"). A list that cannot be read is "cannot tell": nothing starts.
//   2. ManagerSession.Next decides, from the stored ManagerRecord (AppState.Manager), today and that list:
//        * Attach: today's manager (or yesterday's, while it is busy: a new day never stops a turn in flight) is listed;
//        * Resume: today's manager is not listed and its transcript is there: a BARE `claude --bg --resume <full id>`,
//          which keeps every option it was started with (measured), then attach;
//        * StartNew: nothing stored, yesterday's, or its transcript gone: `claude stop` on the old one first when it is
//          still listed (idle), then the start (ManagerSession.StartArguments), its id stored with today, then attach;
//        * Claim: the stored manager is not listed (or nothing is stored) but a background session runs in the manager
//          folder, as one does when a start was not listed within the search and so stored nothing. The folder is the
//          manager's identity, so that session is stored as today's and attached; a second manager is never started
//          beside it, since nothing would ever stop it (the keeper leaves the manager folder alone).
//   3. Before every start AND every resume, the folder is created and the briefing (ManagerSession.Briefing) written to
//      manager-briefing.md in it. A resume keeps the saved --append-system-prompt-file, which names this same path, so a
//      Deskbar rebuilt with a new briefing reaches the manager at its next resume, not only at the next day's start.
//   4. The window is ClaudeTaskActions.AttachTo, the one a task's session gets, titled
//      ClaudeStart.AttachTitle(ManagerSession.Name) and opened on the workspace in front (the manager has none of its
//      own). An open one is brought forward. Once it appears, the App pins it to every desktop (App.PinManagerWindows,
//      task f171bca6), and the bar draws it with a violet icon and ring (ManagerSession.IsWindow says which window).
//
// A PENDING CLICK. From the click until the window is attached (or the open failed) a second click does nothing: the
// first start is still finding its session, and a second would start another manager.
//
// FAILURES. Open answers at once; what `claude` says later is told to the user in a message (ClaudeTaskActions.Tell,
// the seam every Claude click uses), since the manager has no task thread to say it in. An untrusted manager folder
// (`claude --bg` fails with "Workspace not trusted", the case on every machine's first start) goes through the same
// offer a task's start makes: a terminal running `claude` in the folder, where the user accepts trust himself. Nothing is
// stored and nothing retried; his next click starts the manager.
//
// THREADS. Open runs on the UI thread (`scheduler` in the app); ClaudeSessions runs `claude` off it and every answer is
// brought back onto `scheduler`, the only thread that touches the state or `pending`. So nothing here locks.
//
// Step 1 ran nothing on a timer: the manager was started, resumed or stopped only by a click. Step 2 adds the EVENT
// WAKES (design docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md, plan
// docs/superpowers/plans/2026-11-25-manager-event-wakes.md), and still no timer of its own: Tick runs on every emission
// of the sessions poll (ClaudeSessions.Live, every 15 seconds), the App wires it.
//
//   * ManagerWakes.Plan (Core, pure) decides from the stored record, the board, the list and the time whether to wake the
//     manager and with what message: a hand-back only (task 6a87df67 dropped the question wakes), at most once per 30
//     minutes, only today's manager and never while it is busy.
//   * The message goes by the roads a comment takes to a task's session (CommentWake.PlanFor): into the manager's inbox
//     when it runs (its waiter wakes it), a BARE resume with the message when it does not run, stop then resume when its
//     waiter lapsed. A resume that fails leaves the message in the inbox, where its next waiter finds it.
//   * The record after the wake is stored (Since moved, Unread set when a task was handed back again, the day's
//     hand-backs when the wake was delivered), but only onto the SAME session:
//     a click that started a new manager meanwhile is not overwritten.
//   * A wake and a click share `pending`, because both may resume the manager and two resumes fork a copy (probe notes).
//     A click during a wake is not dropped: it runs once the wake is done (openAfterWake).
//   * A click clears Unread, which takes the dot off the Manager button.
public sealed class ManagerActions : IDisposable
{
    /// <summary>The manager's window title, by which an open window is found again (ClaudeTaskActions.AttachTo).</summary>
    public static string Title => ManagerSession.WindowTitle;

    readonly WorkspaceManager manager;
    readonly ClaudeSessions sessions;
    readonly ClaudeTaskActions actions;
    readonly IScheduler scheduler;
    readonly Func<DateOnly> today;
    readonly Action<string>? trace;
    readonly Func<string, string> longPath;
    readonly SerialDisposable opening = new();
    readonly SerialDisposable wakingUp = new();
    readonly SerialDisposable ticks = new();

    // The event wakes' inputs (Tick): the board and the inbox. Null in a rig that tests only the click: Tick then does nothing.
    readonly TaskService? tasks;
    readonly CommentInbox? inbox;

    // The manager's folder (ManagerSession.FolderIn the state dir), and the shim's full path with forward slashes: the
    // allow rule and the briefing must name exactly what the manager runs, and Git Bash wants forward slashes (probes).
    // A path with a space is its short form, or quoted (ShimCommand).
    readonly string folder;
    readonly string shim;

    // From a click until its window is attached or its failure told (see the header), and from a wake until it is
    // delivered. Touched only on `scheduler`.
    bool pending;

    // A wake holds `pending`; a click that came meanwhile is run when the wake is done, never dropped (with the attach it
    // asked for: the terminal, or only the page).
    bool waking;
    bool openAfterWake;
    bool openAfterWakeAttaches;

    // PAGE MESSAGES WAITING THEIR TURN (task 6c4c067f). A message sent from the manager's page while a click, a wake or an
    // earlier message holds `pending` waits here and goes out, in order, once that ends (Settled). Never dropped: the user
    // already sees its bubble. Each entry is the text as delivered and its message as stored.
    readonly Queue<(string Delivered, ManagerMessage Message)> queuedSends = new();
    readonly SerialDisposable sending = new();

    /// <param name="stateDir">Deskbar's state folder (AppPaths.NewStateDir), so DESKBAR_APPDATA moves the manager too.</param>
    /// <param name="shimPath">The `deskbar.cmd` ControlCommandLine.WriteShim writes in the state folder.</param>
    /// <param name="scheduler">Where answers land: the UI thread in the app.</param>
    /// <param name="today">The local calendar day; the system's unless a test says otherwise.</param>
    /// <param name="longPath">Expands 8.3 short forms when a listed session's folder is compared with the manager's.</param>
    /// <param name="shortPath">The 8.3 short form of a path (ShortPath.Of), for a shim path with a space (ShimCommand).</param>
    public ManagerActions(WorkspaceManager manager, ClaudeSessions sessions, ClaudeTaskActions actions, string stateDir, string shimPath,
        IScheduler scheduler, Func<DateOnly>? today = null, Action<string>? trace = null, Func<string, string>? longPath = null,
        Func<string, string>? shortPath = null, TaskService? tasks = null, CommentInbox? inbox = null)
    {
        this.tasks = tasks;
        this.inbox = inbox;
        // The event wakes' tick: every emission of the sessions poll, brought onto `scheduler` (Tick touches the state).
        // Only with the board and the inbox; without them there is nothing to wake for or with.
        if (tasks is not null && inbox is not null)
            ticks.Disposable = sessions.Live.ObserveOn(scheduler).Subscribe(Tick,
                error => trace?.Invoke($"manager: wakes stopped by {error.Message}"));
        this.longPath = longPath ?? Deskbar.Windows.Control.LongPath.Of;
        this.manager = manager;
        this.sessions = sessions;
        this.actions = actions;
        this.scheduler = scheduler;
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        this.trace = trace;
        folder = ManagerSession.FolderIn(stateDir);
        shim = ShimCommand(shimPath, shortPath ?? Deskbar.Windows.Control.ShortPath.Of);
    }

    /// <summary>
    /// The shim as the manager's allow rule and briefing name it (final review, item 3): the full path with forward
    /// slashes, as measured. A path with a space (a user folder like C:\Users\John Doe) never matches an unquoted rule,
    /// because the model double-quotes it and under dontAsk every call is then denied silently; so such a path becomes
    /// its 8.3 short form, which has no space and was measured to run unquoted. On a volume with short names turned off
    /// the short form is the long one, still spaced; then the path goes in double quotes, the form the model writes,
    /// which a rule carrying the same quotes was measured to match. Both measured in
    /// docs/superpowers/notes/2026-11-25-manager-session-probes.md, "A shim path with a space".
    /// </summary>
    public static string ShimCommand(string shimPath, Func<string, string> shortPath) =>
        (shimPath.Contains(' ') ? shortPath(shimPath) : shimPath).Replace('\\', '/') is var command && command.Contains(' ')
            ? $"\"{command}\""
            : command;

    /// <summary>
    /// The Manager button and `task manager`: attaches to today's manager, resumes it or starts it (see the header).
    /// Answers at once; refused while Claude sessions are off, as "Start Claude on this" is (the terminal profile the
    /// window opens with and the hooks exist only while they are on). A click while one is pending does nothing.
    /// </summary>
    /// <param name="attach">
    /// Whether the click ends in the manager's terminal window (ClaudeTaskActions.AttachTo). Since task 6c4c067f the Manager
    /// button and the manager line open the manager's PAGE in the tasks panel and call this with false: the same steps make
    /// sure today's manager exists (attach to it, resume, start or claim it), and no window opens. True, the default, is
    /// the page's "Open in terminal" and the pipe's `task manager`, which has no panel to look at.
    /// </param>
    public Result Open(bool attach = true)
    {
        if (!manager.State.ClaudeTasksInEffect) return Result.Failure(ClaudeTaskActions.FeatureOff);
        if (pending)
        {
            // A wake is being delivered: the click runs right after it, so it never resumes the manager alongside it. A
            // terminal asked for wins over a page-only open asked for in the same wait.
            if (waking)
            {
                openAfterWake = true;
                openAfterWakeAttaches |= attach;
            }
            return Result.Success();
        }
        pending = true;
        // The click is the user looking: whatever a wake brought is read from here on (the dot goes), and so is the
        // manager's answer to their message (task 2c5de076, ManagerQuestion.Seen: the line goes; a question stays).
        if (manager.State.Manager is { Unread: true } unread) manager.SetManagerSession(unread with { Unread = false });
        ManagerQuestion.Seen(Maybe.From(manager.State.Manager)).Tap(manager.SetManagerSession);
        // The day is read once, at the click, so a click just before midnight stores the day it was decided on.
        var day = today();
        opening.Disposable = OnScheduler(sessions.ListNow)
            .SelectMany(listed => listed.Match(
                list => Take(ManagerSession.Next(Maybe.From(manager.State.Manager), day, list, TranscriptExists,
                    agent => ManagerSession.IsManager(agent.Folder, folder, longPath)), day),
                error => Observable.Return(Result.Failure<ClaudeAgent>(
                    $"Claude Code's list of sessions could not be read, so the manager was not opened:\n\n{error}"))))
            .Subscribe(
                opened =>
                {
                    pending = false;
                    opened.Match(agent => { if (attach) actions.AttachTo(Title, Maybe<Guid>.None, agent, actions.Tell); }, Failed);
                    Settled();
                },
                error =>
                {
                    pending = false;
                    Failed(error.Message);
                    Settled();
                });
        return Result.Success();
    }

    /// <summary>
    /// A message the user wrote on the manager's page (task 6c4c067f; spec
    /// docs/superpowers/specs/2026-11-28-manager-inbox-page-design.md, "Sending"). It is stored on today's record at once,
    /// so the page draws the user's bubble straight away, the record is marked as the user having written (the question
    /// and any unread answer cleared, Replying set: ManagerQuestion.AfterPrompt, since the waiter road never fires the
    /// prompt hook that does it for a typed line), and the text goes to the manager as ManagerSession.PageMessage by the
    /// first road that fits (Deliver). Answers at once; a failure later is told as Open's are. Refused while Claude sessions
    /// are off, and in a copy built without the comment inbox (the tests of the click alone); blank text does nothing.
    /// </summary>
    /// <param name="openTask">The task whose page was open when the manager's page was opened, named to the manager as
    /// "the current task" (ManagerSession.OpenTaskLine), or none.</param>
    public Result Send(string text, Maybe<TaskItem> openTask)
    {
        if (!manager.State.ClaudeTasksInEffect) return Result.Failure(ClaudeTaskActions.FeatureOff);
        if (inbox is null) return Result.Failure("The manager cannot be written to in this copy.");
        if (string.IsNullOrWhiteSpace(text)) return Result.Success();
        var words = text.Trim();
        var delivered = ManagerSession.PageMessage(words, ManagerSession.OpenTaskLine(Maybe.From(words), openTask));
        var message = new ManagerMessage(Guid.NewGuid(), scheduler.Now, words);
        // Today's record takes it now. With none (the first message of the day, the page opened before midnight) the
        // record the start makes takes it (Started).
        if (manager.State.Manager is { } record && record.Day == today())
            manager.SetManagerSession(Written(ManagerSession.WithMessage(record, message), delivered));
        queuedSends.Enqueue((delivered, message));
        if (!pending) Settled();
        return Result.Success();
    }

    // The record once the user's message reached it: AfterPrompt's change, or the record as it was when nothing changed.
    static ManagerRecord Written(ManagerRecord record, string delivered) =>
        ManagerQuestion.AfterPrompt(Maybe.From(record), record.SessionId, Maybe.From(delivered)).GetValueOrDefault(record);

    // `pending` has just been released (or was free): a click that waited on a wake runs first, then the next page message.
    // Each runs under `pending` and comes back here when it ends, so waiting work goes out one at a time, in order.
    void Settled()
    {
        if (pending) return;
        if (openAfterWake)
        {
            var attach = openAfterWakeAttaches;
            openAfterWake = openAfterWakeAttaches = false;
            Open(attach);
            return;
        }
        if (queuedSends.Count == 0) return;
        var (delivered, message) = queuedSends.Dequeue();
        Deliver(delivered, message);
    }

    // One page message to the manager. The list is read fresh (a poll can be fifteen seconds old, and resuming a manager
    // that runs forks a copy) and ManagerSession.Next says what today's manager is:
    //   * none, or not resumable (StartNew): a new manager started with the message as its FIRST prompt, in place of the
    //     overview (the user asked something of their own), and the message stored on the record the start makes;
    //   * not running (Resume): a bare resume with the message, only once a fresh list says it is gone (ResumedWith);
    //   * running (Attach, Claim): the comment roads over its one session, as a wake (CommentWake.PlanFor): its inbox while
    //     it is busy (never a stop that would kill the turn; its turn's Stop starts a waiter that finds the message at once)
    //     or has a live waiter, else a stop and a resume with the message.
    void Deliver(string delivered, ManagerMessage message)
    {
        pending = true;
        var day = today();
        sending.Disposable = OnScheduler(sessions.ListNow)
            .SelectMany(listed => listed.Match(
                list => Road(ManagerSession.Next(Maybe.From(manager.State.Manager), day, list, TranscriptExists,
                    agent => ManagerSession.IsManager(agent.Folder, folder, longPath)), list, day, delivered, message),
                error => Observable.Return(Result.Failure(
                    $"Claude Code's list of sessions could not be read, so your message did not reach the manager:\n\n{error}"))))
            .Catch((Exception e) => Observable.Return(Result.Failure(e.Message)))
            .Subscribe(sent =>
            {
                pending = false;
                sent.TapError(Failed);
                Settled();
            });
    }

    IObservable<Result> Road(ManagerStep step, IReadOnlyCollection<ClaudeAgent> listed, DateOnly day, string delivered, ManagerMessage message) =>
        step switch
        {
            ManagerStep.StartNew start => StopFirst(start.StopFirst)
                .SelectMany(_ => Started(day, delivered))
                .Select(found => found.Tap(_ => StoreOnNewRecord(delivered, message)))
                .Select(found => found.IsSuccess ? Result.Success() : Result.Failure(found.Error)),
            ManagerStep.Resume resume => ResumedWith(resume.SessionId, delivered),
            ManagerStep.Attach attach => ToRunning(attach.Agent.SessionId, listed, delivered),
            ManagerStep.Claim claim => Observable.Defer(() =>
            {
                manager.SetManagerSession(new ManagerRecord(claim.Agent.SessionId, day, scheduler.Now));
                StoreOnNewRecord(delivered, message);
                return ToRunning(claim.Agent.SessionId, listed, delivered);
            }),
            _ => Observable.Return(Result.Failure($"Unknown manager step {step}")),
        };

    // The page message on a record the send itself made (a start or a claim): stored as Send stores it on today's.
    void StoreOnNewRecord(string delivered, ManagerMessage message)
    {
        if (manager.State.Manager is not { } record || record.Messages?.Any(m => m.Id == message.Id) == true) return;
        manager.SetManagerSession(Written(ManagerSession.WithMessage(record, message), delivered));
    }

    // A running manager: the road a comment takes to a task's running session (CommentWake.PlanFor over its one session).
    IObservable<Result> ToRunning(string sessionId, IReadOnlyCollection<ClaudeAgent> listed, string delivered)
    {
        var session = new TaskSession(sessionId, folder, DateTimeOffset.MinValue, Maybe<DateTimeOffset>.None);
        return CommentWake.PlanFor([session], listed.ToList(), sessions.TranscriptExists, inbox!.Lapsed) switch
        {
            WakePlan.Inbox => Observable.Return(inbox.Deliver(sessionId, delivered)),
            WakePlan.Resume => ResumedWith(sessionId, delivered),
            WakePlan.StopThenResume => StopFirst(listed.TryFirst(a => a.SessionId == sessionId)).SelectMany(_ => ResumedWith(sessionId, delivered)),
            WakePlan.Nothing nothing => Observable.Return(Result.Failure(nothing.Why)),
            var road => Observable.Return(Result.Failure($"Unknown wake road {road}")),
        };
    }

    // The step Next chose, ending in the session to attach to, or why there is none.
    IObservable<Result<ClaudeAgent>> Take(ManagerStep step, DateOnly day) => step switch
    {
        ManagerStep.Attach attach => Observable.Return(Result.Success(attach.Agent)),
        // A manager in the folder whose id was never stored (its start was not listed in time): today's from now on.
        ManagerStep.Claim claim => Observable.Return(Result.Success(claim.Agent))
            .Do(_ => manager.SetManagerSession(new ManagerRecord(claim.Agent.SessionId, day, scheduler.Now))),
        ManagerStep.Resume resume => Briefed(_ => OnScheduler(() =>
            sessions.RunAndFind(ManagerSession.ResumeArguments(resume.SessionId), folder, resume.SessionId))),
        ManagerStep.StartNew start => StopFirst(start.StopFirst).SelectMany(_ => Started(day)),
        _ => Observable.Return(Result.Failure<ClaudeAgent>($"Unknown manager step {step}")),
    };

    // A new manager: the briefing written, `claude --bg` with the measured flags, and once it is listed, its full id stored
    // with the day of the click, so the next click that day attaches or resumes it rather than starting another.
    // `firstMessage`: a page message that made the start (task 6c4c067f), its first prompt in place of the overview.
    // The page messages of a record of the SAME day are carried onto the new one (its manager's transcript was gone, so a
    // new one started): the user's side of the day's chat stays on the page.
    IObservable<Result<ClaudeAgent>> Started(DateOnly day, string? firstMessage = null) =>
        Briefed(briefing => OnScheduler(() => sessions.RunAndFind(ManagerSession.StartArguments(briefing, shim, firstMessage), folder))
            // Since = now: the manager's first turn reads the board, so events are counted from its start (ManagerWakes).
            .Select(found => found.Tap(agent => manager.SetManagerSession(new ManagerRecord(agent.SessionId, day, scheduler.Now)
            {
                Messages = manager.State.Manager is { } before && before.Day == day ? before.Messages : null,
            }))));

    // Yesterday's idle manager is stopped before today's starts, so two managers never run side by side. `claude stop`
    // returns once the process is gone (measured). A stop that fails (the session ended in the second since the list was
    // read, say) does not hold today's manager back; it is traced. A fault the CLI did not turn into a Result is such a
    // failure too, not the end of the click.
    IObservable<Unit> StopFirst(Maybe<ClaudeAgent> stale) =>
        stale.HasValue
            ? OnScheduler(() => sessions.StopNow(ShortIdOf(stale.Value)))
                .Catch((Exception e) => Observable.Return(Result.Failure(e.Message)))
                .Do(stopped => stopped.TapError(e => trace?.Invoke($"manager: yesterday's {stale.Value.SessionId} not stopped: {e}")))
                .Select(_ => Unit.Default)
            : Observable.Return(Unit.Default);

    // The briefing written before a start or a resume (see the header), then `next` with its path; a briefing that cannot
    // be written starts nothing, since the manager would run without its rules.
    IObservable<Result<ClaudeAgent>> Briefed(Func<string, IObservable<Result<ClaudeAgent>>> next) =>
        WriteBriefing().Match(next, error => Observable.Return(Result.Failure<ClaudeAgent>(error)));

    Result<string> WriteBriefing()
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, ManagerSession.BriefingFile);
            File.WriteAllText(path, ManagerSession.Briefing(shim));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<string>($"The manager's briefing could not be written in {folder}: {e.Message}");
        }
    }

    /// <summary>
    /// One tick of the sessions poll (see the header): a wake when ManagerWakes.Plan says so, or the record kept current
    /// (Since filled in for a record saved before step 2). Nothing while a click or a wake is under way, while Claude
    /// sessions are off, or with no manager stored. Called on `scheduler`.
    /// </summary>
    public void Tick(IReadOnlyCollection<ClaudeAgent> listed)
    {
        if (pending || tasks is null || inbox is null || !manager.State.ClaudeTasksInEffect || manager.State.Manager is not { } record) return;
        switch (ManagerWakes.Plan(record, tasks.Board, listed, today(), scheduler.Now, tasks.RedRuns))
        {
            case ManagerWake.Nothing(var kept) when kept != record:
                manager.SetManagerSession(kept);
                break;
            case ManagerWake.Wake(var message, var after):
                Wake(record, message, after, listed);
                break;
        }
    }

    // The wake delivered by the comment roads (CommentWake.PlanFor over the manager's one session), then the record stored.
    void Wake(ManagerRecord record, string message, ManagerRecord after, IReadOnlyCollection<ClaudeAgent> listed)
    {
        pending = waking = true;
        var session = new TaskSession(record.SessionId, folder, DateTimeOffset.MinValue, Maybe<DateTimeOffset>.None);
        var road = CommentWake.PlanFor([session], listed.ToList(), sessions.TranscriptExists, inbox!.Lapsed);
        trace?.Invoke($"manager: waking {record.SessionId} by {road.GetType().Name}");
        wakingUp.Disposable = (road switch
            {
                WakePlan.Inbox => Observable.Return(inbox.Deliver(record.SessionId, message)),
                WakePlan.Resume => ResumedWith(record.SessionId, message),
                WakePlan.StopThenResume => StopFirst(listed.TryFirst(a => a.SessionId == record.SessionId))
                    .SelectMany(_ => ResumedWith(record.SessionId, message)),
                WakePlan.Nothing nothing => Observable.Return(Result.Failure(nothing.Why)),
                _ => Observable.Return(Result.Failure($"Unknown wake road {road}")),
            })
            .Catch((Exception e) => Observable.Return(Result.Failure(e.Message)))
            .Subscribe(sent => Woken(record, after, sent));
    }

    // A bare resume with the message (the one form that keeps the manager's saved options, step 1 probes). The briefing is
    // written again first, as before any resume. A resume that did not happen leaves the message in the inbox instead.
    //
    // ONLY ONCE A FRESH LIST SAYS IT IS GONE (post-merge review of #557): resuming a session that is still listed starts a
    // forked COPY, a second Fable manager nobody recorded. The list Tick was given can be stale (a click may have resumed
    // the manager after that poll was read), and right after `claude stop` the session stays listed for about a second
    // (measured, probe notes "dontAsk probes"). So the list is read again here, and while the manager is on it the read
    // is repeated, ResumeChecks times ResumePause apart; still listed after that, the message waits in the inbox, where
    // its next turn's waiter finds it.
    IObservable<Result> ResumedWith(string sessionId, string message) =>
        Gone(sessionId, ResumeChecks)
            .SelectMany(gone => gone
                ? Observable.Defer(() =>
                    {
                        WriteBriefing().TapError(e => trace?.Invoke($"manager: {e}"));
                        return OnScheduler(() => sessions.RunAndFind([.. ManagerSession.ResumeArguments(sessionId), "--", message], folder, sessionId));
                    })
                    .Select(found => found.IsSuccess ? Result.Success() : Waits(sessionId, message, found.Error))
                : Observable.Return(Waits(sessionId, message, "it is still listed as running")));

    /// <summary>How many fresh reads of the list a wake makes, ResumePause apart, before it resumes the manager.</summary>
    public const int ResumeChecks = 5;

    public static readonly TimeSpan ResumePause = TimeSpan.FromSeconds(1);

    // Whether a fresh `claude agents --json` no longer lists the session, read up to `checks` times. A list that cannot be
    // read is "cannot tell", which never resumes.
    IObservable<bool> Gone(string sessionId, int checks) =>
        OnScheduler(sessions.ListNow).SelectMany(listed =>
            listed.IsFailure ? Observable.Return(false)
            : listed.Value.All(agent => agent.SessionId != sessionId) ? Observable.Return(true)
            : checks > 1 ? Observable.Timer(ResumePause, scheduler).SelectMany(_ => Gone(sessionId, checks - 1))
            : Observable.Return(false));

    // The wake left in the inbox when it could not be resumed with it.
    Result Waits(string sessionId, string message, string why) =>
        inbox!.Deliver(sessionId, message).Tap(() => trace?.Invoke($"manager: not resumed ({why}), the wake waits in its inbox"));

    // After a wake: the record stored onto the same session only (a click may have started another meanwhile). A wake
    // that could not be delivered still moves Since, or every tick would try again; it just sets no dot. Nor does it
    // count its tasks as nudged (task 6a87df67 review): the manager never saw them, so their next stop is a first one,
    // nudged again, not an AGAIN that lights the dot.
    void Woken(ManagerRecord record, ManagerRecord after, Result sent)
    {
        pending = waking = false;
        sent.TapError(e => trace?.Invoke($"manager: wake not delivered: {e}"));
        if (manager.State.Manager is { } current && current.SessionId == record.SessionId && current.Day == record.Day)
            manager.SetManagerSession(current with
            {
                Since = after.Since,
                Unread = current.Unread || (sent.IsSuccess && after.Unread),
                HandedBack = sent.IsSuccess ? after.HandedBack : current.HandedBack,
            });
        // A click that came during the wake, then any page message that waited on it (task 6c4c067f).
        Settled();
    }

    // An untrusted folder gets the trust offer (ClaudeTaskActions.OfferTrust); anything else is told as it is.
    void Failed(string error)
    {
        trace?.Invoke($"manager: not opened: {error}");
        if (error.Contains("not trusted", StringComparison.OrdinalIgnoreCase)) actions.OfferTrust(folder, error);
        else actions.Tell($"Deskbar could not open the manager session:\n\n{error}");
    }

    // The manager's transcript, looked for as the keeper looks for a task's (worktree project folders included).
    bool TranscriptExists(string sessionId) =>
        sessions.TranscriptExists(new TaskSession(sessionId, folder, DateTimeOffset.MinValue, Maybe<DateTimeOffset>.None));

    // A background session's short id is the first eight characters of its id when the list does not give it (measured).
    static string ShortIdOf(ClaudeAgent agent) =>
        agent.ShortId.GetValueOrDefault(agent.SessionId[..Math.Min(8, agent.SessionId.Length)]);

    // A ClaudeSessions call, started when subscribed and answered on `scheduler`. The scheduler is given to ToObservable,
    // so the answer is handed on as the task completes rather than from a thread-pool continuation.
    IObservable<T> OnScheduler<T>(Func<Task<T>> call) => Observable.Defer(() => call().ToObservable(scheduler));

    public void Dispose()
    {
        sending.Dispose();
        ticks.Dispose();
        opening.Dispose();
        wakingUp.Dispose();
    }
}
