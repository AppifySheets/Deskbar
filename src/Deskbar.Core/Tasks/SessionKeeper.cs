using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// DECIDING WHICH CLAUDE SESSIONS TO WAKE, AND WHICH FINISHED ONES TO STOP (Stops, below). Design: docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md,
// "Keeping open tasks running"; measurements: docs/superpowers/notes/2026-11-23-claude-background-probes.md.
//
// THE RULE. An open task (Backlog, In progress or Waiting on you) whose latest session `claude agents --json` does not
// list is resumed. Done and Closed tasks never are. Petre's words: "if a session has ended and done its work, i will
// mark the task as done". So a finished session on a task that is still open is not a finished task: until he says
// Done, the task is meant to be running, and a session that has gone missing is woken.
//
// WHY ONLY SESSIONS THE LIST DOES NOT SHOW. Claude Code's own daemon restarts a session that crashed mid-turn by itself
// and sends it a continue (Task 1 note), so a session that is listed is either working, or idle and attachable, and
// either way not ours to touch. Resuming a listed session would start a COPY with a new id (measured). What the daemon
// cannot cover is the daemon being gone: a reboot, a sign-out, a killed daemon or a closed VS Code session. Those leave
// sessions missing from the list, and after the grace below that is the only case left for the keeper.
//
// THIS IS A PURE FUNCTION. It takes the board, the list, a ledger of resumes already made, and two questions it cannot
// answer without touching files (does the transcript still exist, was the last turn cut off), and returns steps.
// Nothing here starts a process or reads a clock ("now" comes in); Task 8 executes the steps, one at a time,
// <see cref="Spacing"/> apart, and records each resume in the ledger.
//
// THE STEPS, per task, for its latest session (the one with the greatest Joined):
//   * any of the task's sessions listed: nothing, ever (final review, item 9).
//   * replaced by /clear (Ruling 19): nothing, ever.
//   * missing from the list for less than <see cref="Settle"/> (Ruling 18): nothing yet.
//   * resumed within <see cref="Grace"/>: nothing. The list may not show the woken session yet, or shows it busy while
//     it is idle (measured, up to 30 s), and a second resume would start a copy.
//   * no folder: a give-up, since `claude --bg --resume` runs in a folder and there is none to run it in.
//   * transcript gone: a give-up; Claude Code keeps sessions 30 days.
//   * already resumed <see cref="MaxResumesInWindow"/> times within <see cref="LoopWindow"/>: a give-up. A session that
//     ends again straight after waking is crashing, and waking it forever helps nobody.
//   * last turn died on a usage limit that has not reset yet (task daeaa26c): nothing yet, see LimitMargin.
//   * otherwise a resume, with a continue message when the last turn was cut off.
//
// THE ONE CASE WHERE A LISTED SESSION IS TOUCHED (task daeaa26c, 2026-11-26). A session whose turn died on the usage
// limit stays in the list, idle, and nothing ever wakes it: the daemon does not, since nothing crashed, and Deskbar's
// comment waiter is not running, since a turn that ends on an API error runs no Stop hook (ClaudeTranscript.LastTurnError).
// Petre: "it was dead and waiting for limits". So once the limit has reset, the latest session of an open task that is
// listed as an idle BACKGROUND session and whose transcript still ends on that limit is restarted: `claude stop`, then
// the same resume with the continue message (KeeperStep.Resume.StopFirst). Resuming it without the stop would start a
// copy (measured 2026-11-23); the stop keeps the conversation. An interactive one (a VS Code panel, a terminal) is
// Petre's window and cannot be restarted from outside, so it is only shown as stopped (ClaudeSessions.NoteLimits).
// A give-up is said ONCE and is STICKY: the board shows it as the task waiting on Petre with the reason as its question,
// and a task in that state gets no step at all (no repeat, and no resume either, even after the loop window has passed
// and the ledger's count has fallen). It stays that way until Petre acts on it. The caller (Task 8) must call
// ResumeLedger.Forget(sessionId) when a give-up question is answered or the task otherwise leaves
// WaitingOnYou-with-that-question; otherwise, inside the loop window, the ledger would still hold the old resumes and
// the next poll would give up again at once and overwrite his action.
//
// A RECURRING TASK BETWEEN RUNS IS NEVER WOKEN (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md,
// "A run ends"). Its `task done` puts it back in Backlog, snoozed until the next run, so to the rule above it looks like
// an open task whose session has gone missing. Resuming it would bring every finished run back two minutes later as an
// idle session, and the next occurrence, which skips while a session of the task is listed, would then skip for good.
// So TaskItem.IsBetweenRuns (a repeat, open, Backlog or Done) gets no step at all: no resume, no restart, no give-up.
// The next run is a FRESH session that the schedule starts. A run that died without marking done is InProgress (or
// waits on Petre), so it is not between runs and is resumed like any open task's session.
//
// NOR IS ONE WAITING ON ITS OWN REPEAT QUESTION (task 0b78269e, final review, item 1). When a run cannot start, the task
// waits on Petre with RecurringRuns.FolderGoneQuestion or StartFailedQuestion. That waiting is about the run that never
// began, not about the last run's session, which had finished; resuming (or restarting) that session would revive an
// old run behind his back. So such a task is treated like a give-up (RepeatAsked below): no resume, no restart, no
// give-up, until setting the repeat again (or turning it off) answers the question. A recurring run waiting on a question
// of its OWN session is still resumed like any open task's.
//
// A session id on two tasks is resumed once, for the task that comes first in the ordering. Listed sessions that no
// task knows (open or closed, any entry) are adopted: one step each.
//
// THE ORDER. Resume and give-up steps first, Waiting on you tasks before In progress before Backlog, the most recently
// updated first within a status, so the session a person is waiting on to answer comes back first. Adopts follow, in
// the list's order.
public static class SessionKeeper
{
    /// <summary>
    /// The message a session whose last turn was cut off is resumed with, exactly (Global Constraints). It mirrors what
    /// Claude Code's own daemon sends a session it restarts after a crash (Task 1 note). Here in Core, not in the app's
    /// ClaudeSessions that sends it, so the prompt hook can tell it from Petre's own message (RepliedTasks, task 8bd18246).
    /// </summary>
    public const string ContinueMessage = "Continue from where you left off. Note: this session was resumed by Deskbar after it stopped.";

    /// <summary>Within this long of a resume the session is left alone, whatever the list says.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>The window in which resumes are counted for the loop guard.</summary>
    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(10);

    /// <summary>Resumes within <see cref="LoopWindow"/> after which the next is a give-up.</summary>
    public const int MaxResumesInWindow = 3;

    /// <summary>The pause Task 8 leaves between two resumes, so a first poll after a reboot does not wake all at once.</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(5);

    public const string LoopGaveUp =
        "Deskbar stopped resuming this session: it ended again straight after 3 resumes in 10 minutes.";

    public const string TranscriptGone =
        "Claude Code no longer has this session's transcript (it keeps sessions for 30 days), so it cannot be resumed.";

    public const string NoFolder =
        "Deskbar cannot resume this session: Claude Code never said which folder it ran in.";

    // Asked by Task 8 (ClaudeSessions), not by Plan: `claude --bg --resume` itself failed, for example in a folder Claude
    // Code was never told to trust or with `claude` missing. Claude's own message goes into the thread as a comment and
    // this is the question. Sticky like the three above, so a resume that cannot work is tried once, never on a timer
    // (Review Focus 4: one clear message, no retry loop), until Petre acts on the task.
    public const string ResumeFailed =
        "Deskbar could not resume this session. Claude Code's message is in this task's thread.";

    /// <summary>
    /// How long a session must have been missing from the list before the keeper does anything about it (Ruling 18),
    /// and so also how long after Deskbar starts before the first resume: a session never seen since then counts as
    /// missing since then. VS Code restores its Claude panels after a restart and reloads them after an update or a
    /// window reload, and each brings its session back BY ITSELF a little later; resuming in that gap would start a
    /// background copy beside the one VS Code is about to open. Two minutes covers that and costs a reboot's resumes
    /// two minutes.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after a usage limit's reset moment a session stopped on it is woken (task daeaa26c). The moment comes
    /// from the API's own answer, but the clock that answered is not this one, and a wake a few seconds early only hits
    /// the limit again and costs a loop-guard resume.
    /// </summary>
    public static readonly TimeSpan LimitMargin = TimeSpan.FromMinutes(2);

    /// <summary>When a session stopped on a usage limit may be woken: its reset moment plus <see cref="LimitMargin"/>.</summary>
    public static DateTimeOffset WakeAfterLimit(DateTimeOffset resetsAt) => resetsAt + LimitMargin;

    /// <param name="absentSince">
    /// Since when a session has been missing from every list that could be read (Ruling 18; ClaudeSessions keeps it).
    /// A session gets no step at all until <see cref="Settle"/> has passed since then. Left out, every session counts as
    /// long gone, which is what the tests of the other rules want.
    /// </param>
    /// <param name="limitedUntil">
    /// When the session's last turn died on a usage limit, that error (ClaudeTranscript.LastTurnError, with its reset
    /// moment); None otherwise, and None for an error that names no reset. Left out, no session is limited.
    /// </param>
    public static IReadOnlyList<KeeperStep> Plan(
        TaskBoard board,
        IReadOnlyCollection<ClaudeAgent> live,
        ResumeLedger ledger,
        Func<TaskSession, bool> transcriptExists,
        Func<TaskSession, bool> cutOff,
        DateTimeOffset now,
        Func<string, DateTimeOffset>? absentSince = null,
        Func<TaskSession, Maybe<TurnError>>? limitedUntil = null)
    {
        var listed = live.Select(agent => agent.SessionId).ToHashSet();
        var missingSince = absentSince ?? (_ => DateTimeOffset.MinValue);
        var limited = limitedUntil ?? (_ => Maybe<TurnError>.None);

        var resumes = board.Tasks
            .Where(task => task.IsClaudes && task.Status is TaskStatus.Backlog or TaskStatus.InProgress or TaskStatus.WaitingOnYou)
            // A recurring task between runs is never woken (task 0b78269e, see the header): its last run is over. Nor is one
            // waiting on its own repeat question: the run it asks about never started (final review, item 1).
            .Where(task => !task.IsBetweenRuns && !RepeatAsked(task))
            // A task with ANY of its sessions listed is running (final review, item 9). The latest by Joined is not
            // always the one alive: a resumed or rejoined older entry, or a second session started on the task, can be
            // listed while the latest has ended, and waking the ended one would put two sessions on one task.
            .Where(task => !task.Sessions.Any(session => listed.Contains(session.Id)))
            .OrderBy(task => StatusRank(task.Status))
            .ThenByDescending(task => task.UpdatedAt)
            .Select(task => (Task: task, Session: task.Sessions.MaxBy(session => session.Joined)!))
            // A session on two tasks belongs to the first in the ordering; the later task is not looked at.
            .DistinctBy(pair => pair.Session.Id)
            // /clear ended that conversation for good (Ruling 19): never resumed, and no give-up either, since nothing
            // went wrong. The fresh session that followed joins the task by its own hook.
            .Where(pair => !pair.Session.Replaced)
            // Ruling 18: missing for less than Settle, it may still come back by itself.
            .Where(pair => now - missingSince(pair.Session.Id) >= Settle)
            .Select(pair => StepFor(pair.Task, pair.Session, ledger, transcriptExists, cutOff, limited, now))
            .Where(step => step.HasValue)
            .Select(step => step.Value);

        // The listed session stopped on a usage limit that has since reset (see the header): stopped, then resumed. Only
        // the task's LATEST session, and only when it is the one listed: an older session listed beside an ended latest
        // one is left alone, as the rule above leaves it.
        var restarts = board.Tasks
            .Where(task => task.IsClaudes && task.Status is TaskStatus.Backlog or TaskStatus.InProgress or TaskStatus.WaitingOnYou)
            // Nor restarted: a restart is a resume with a stop before it (task 0b78269e).
            .Where(task => !task.IsBetweenRuns && !RepeatAsked(task))
            .Where(task => task.Sessions.Count > 0)
            .OrderBy(task => StatusRank(task.Status))
            .ThenByDescending(task => task.UpdatedAt)
            .Select(task => (Task: task, Session: task.Sessions.MaxBy(session => session.Joined)!))
            .Where(pair => !pair.Session.Replaced)
            .Select(pair => (pair.Task, pair.Session, Agent: live.FirstOrDefault(agent => agent.SessionId == pair.Session.Id)))
            .Where(triple => triple.Agent is { Background: true, Busy: false, ShortId.HasValue: true })
            .DistinctBy(triple => triple.Session.Id)
            .Select(triple => RestartFor(triple.Task, triple.Session, triple.Agent!, ledger, limited, now))
            .Where(step => step.HasValue)
            .Select(step => step.Value);

        // Every session id any task holds, closed tasks included: a closed task's session is not a stranger.
        var known = board.Tasks.SelectMany(task => task.Sessions).Select(session => session.Id).ToHashSet();

        var adopts = live
            .DistinctBy(agent => agent.SessionId)
            .Where(agent => !known.Contains(agent.SessionId))
            .Select(agent => (KeeperStep)new KeeperStep.Adopt(agent));

        return resumes.Concat(restarts).Concat(adopts).ToList();
    }

    /// <summary>
    /// How long a finished task's background session must have sat idle, in every list read meanwhile, before
    /// <see cref="Stops"/> names it. One idle read is not enough: a session that has just called `task done` is still
    /// finishing its turn, and between two tool calls a list can catch it idle for a moment.
    /// </summary>
    public static readonly TimeSpan StopAfter = TimeSpan.FromMinutes(1);

    // STOPPING FINISHED SESSIONS (task 87f899ad, 2026-11-23). Petre: "i want to not have a stagnant session", "i don't
    // want to stop sessions that are asking for my feedback, only those that have been finished". A background session
    // stays in Claude Code's daemon until a reboot, about 350 MB a session (measured: 14 sessions, 4.8 GB), and before
    // this nothing ever ended one. "Finished" is the task's word, not the CLI's: the task is Done (the session said
    // `task done`, or Petre marked it) or Closed. A task waiting on Petre is not finished, so a session asking for his
    // feedback is never stopped, and neither is any session of an open task.
    //
    // `claude stop <id>` keeps the conversation (measured 2026-11-23): the session leaves `claude agents --json`, a second
    // stop is harmless, and `claude --bg --resume <id>` brings it back under the same id, idle, without calling the
    // model. So nothing is lost: going to the task resumes it (ClaudeTaskActions), and moving the task back to an open
    // status lets the keeper's Plan resume it like any other open task's session.
    //
    // A session is named only when ALL of these hold:
    //   * one of a Done or Closed task's sessions, and no open task holds the same session id;
    //   * listed as a BACKGROUND session: an interactive one (VS Code, a terminal) is Petre's window, and `claude stop`
    //     only knows background ones anyway;
    //   * idle, and idle in every read for <see cref="StopAfter"/> (<paramref name="idleSince"/>);
    //   * for a DONE task, no window of Deskbar's is attached to it (<paramref name="windowOpen"/>): stopping it would
    //     close the window under Petre while he reads the answer of a Done task he opened (a session marks its own task
    //     Done, often with its report still on screen). Once he closes the window it is stopped.
    //     A CLOSED task's session is stopped window or not, and its window goes with it (task 8556944b, Petre: "can a
    //     claude session window close on its own when a task has been closed?"). Only Petre closes a task, so the close
    //     is his "finished with this", unlike Done. Nothing here closes the window: `claude stop` does, by ending the
    //     `claude attach` the window runs (measured 2026-11-25: a window opened as ClaudeStart.AttachWindow opens one,
    //     on a session stopped while idle, was gone 2 to 4 seconds after the stop). The conversation is kept, and opening
    //     the task again resumes it, so the stop loses nothing. Still only once idle for StopAfter: a turn in flight
    //     finishes first.
    //     "Idle" for a CLOSED task means its turn is over (ClaudeAgent.TurnOver, <paramref name="turnOverSince"/>), not
    //     that the list says idle (task 607af1db). A session that ended its turn with a background job still running,
    //     a Monitor watching a pull request or a Bash left in the background, is listed busy until the job ends, and a
    //     job that never ends kept a Closed task's session alive for good. The close is Petre's "finished with this",
    //     so the job goes with the session. A DONE task's session still waits for the list to say idle: its job may be
    //     the last of its work (a watch it means to see through), and when the job ends the session's next turn ends
    //     idle and is stopped then.
    // A session on two finished tasks is named once, for the first.
    //
    // A RECURRING TASK BETWEEN RUNS counts as finished here too (task 0b78269e, spec
    // docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, "A run ends": "a finished run leaves nothing running").
    // Its run said `task done`, which put the task back in Backlog to sleep until the next run, so without this its
    // session would sit in the daemon until the reboot and the next occurrence would skip while it is listed. It takes
    // the DONE road, not the Closed one: idle in every read for StopAfter, and never while a Deskbar window is attached
    // (Petre has the run open; it stays until he closes it, and the next occurrence skips meanwhile, his rule). Two
    // things keep a run that has just started safe. It is Backlog until its `task join`, so for a few seconds it can look
    // between runs, but its session is busy on its first turn, and an idle read only counts once idle for StopAfter.
    // And "between runs" is exactly TaskItem.IsBetweenRuns, so a run waiting on Petre (WaitingOnYou) is never stopped:
    // it is asking for his feedback. The step says BetweenRuns, so the App can stop it without a line in the thread.
    public static IReadOnlyList<KeeperStep.Stop> Stops(
        TaskBoard board,
        IReadOnlyCollection<ClaudeAgent> live,
        Func<string, DateTimeOffset> idleSince,
        Func<string, DateTimeOffset> turnOverSince,
        Func<TaskItem, bool> windowOpen,
        DateTimeOffset now)
    {
        // Held by a task whose sessions are not ours to stop: an open task, a recurring one between runs excepted.
        var heldOpen = board.Tasks.Where(task => !Stoppable(task)).SelectMany(task => task.Sessions).Select(s => s.Id).ToHashSet();
        return board.Tasks
            .Where(Stoppable)
            .SelectMany(task => live
                .Where(agent => task.Sessions.Any(session => session.Id == agent.SessionId))
                .Select(agent => (Task: task, Agent: agent)))
            .Where(pair => pair.Agent.Background)
            .Where(pair => !heldOpen.Contains(pair.Agent.SessionId))
            // Closed: its turn over for StopAfter, a background job of its own or not. Done: idle for StopAfter.
            .Where(pair => pair.Task.Status == TaskStatus.Closed
                ? pair.Agent.TurnOver && now - turnOverSince(pair.Agent.SessionId) >= StopAfter
                : !pair.Agent.Busy && now - idleSince(pair.Agent.SessionId) >= StopAfter)
            .Where(pair => pair.Task.Status == TaskStatus.Closed || !windowOpen(pair.Task))
            .DistinctBy(pair => pair.Agent.SessionId)
            .Select(pair => new KeeperStep.Stop(pair.Task.Id, pair.Agent) { BetweenRuns = pair.Task.IsBetweenRuns })
            .ToList();
    }

    // A task whose listed sessions Stops may end: a finished one (Done or Closed), or a recurring one between runs (a
    // Backlog task here goes the Done task's way above, since only Closed takes the other road).
    static bool Stoppable(TaskItem task) => Finished(task) || task.IsBetweenRuns;

    static bool Finished(TaskItem task) => task.Status is TaskStatus.Done or TaskStatus.Closed;

    // A recurring task waiting on one of the two questions a run that cannot start asks (RecurringRuns.IsRepeatQuestion;
    // task 0b78269e, final review, item 1). It waits for Petre to set the repeat again, like a give-up waits for him, so
    // the keeper leaves its last run's session down. Only with a repeat: a question is the repeat's only while it repeats.
    static bool RepeatAsked(TaskItem task) =>
        task.Repeat.HasValue && task.Status == TaskStatus.WaitingOnYou && RecurringRuns.IsRepeatQuestion(task.Question);

    static int StatusRank(TaskStatus status) => status switch
    {
        TaskStatus.WaitingOnYou => 0,
        TaskStatus.InProgress => 1,
        _ => 2
    };

    static Maybe<KeeperStep> StepFor(
        TaskItem task, TaskSession session, ResumeLedger ledger,
        Func<TaskSession, bool> transcriptExists, Func<TaskSession, bool> cutOff,
        Func<TaskSession, Maybe<TurnError>> limited, DateTimeOffset now)
    {
        // Sticky: a task the keeper has given up on gets no step at all, not even a resume once the loop window has
        // passed and the ledger's count has fallen. It waits for Petre; see the comment on Forget.
        if (GivenUp(task) || ledger.Within(session.Id, now, Grace) > 0) return Maybe<KeeperStep>.None;
        // Asked only past the cheap checks above, since it reads the transcript's tail.
        var limit = limited(session);
        // Woken before its usage limit resets, it only hits the limit again (and three of those in ten minutes would end
        // in a loop give-up). It waits, and the first poll after the reset wakes it.
        if (limit.Map(error => !MayWake(error, session, ledger, now)).GetValueOrDefault(false)) return Maybe<KeeperStep>.None;
        return string.IsNullOrEmpty(session.Folder) ? GiveUp(task, NoFolder)
            : !transcriptExists(session) ? GiveUp(task, TranscriptGone)
            : ledger.Within(session.Id, now, LoopWindow) >= MaxResumesInWindow ? GiveUp(task, LoopGaveUp)
            : new KeeperStep.Resume(task.Id, session, cutOff(session)) { AfterLimit = limit.HasValue };
    }

    // The restart of a listed session stopped on a usage limit (see the header). Nothing for one not limited, or limited
    // until later; the same grace and loop guard as a resume, since a restart is a resume with a stop before it.
    static Maybe<KeeperStep> RestartFor(
        TaskItem task, TaskSession session, ClaudeAgent agent, ResumeLedger ledger,
        Func<TaskSession, Maybe<TurnError>> limited, DateTimeOffset now)
    {
        if (GivenUp(task) || ledger.Within(session.Id, now, Grace) > 0) return Maybe<KeeperStep>.None;
        if (!limited(session).Map(error => MayWake(error, session, ledger, now)).GetValueOrDefault(false)) return Maybe<KeeperStep>.None;
        return string.IsNullOrEmpty(session.Folder) ? GiveUp(task, NoFolder)
            : ledger.Within(session.Id, now, LoopWindow) >= MaxResumesInWindow ? GiveUp(task, LoopGaveUp)
            : new KeeperStep.Resume(task.Id, session, SendContinue: true) { StopFirst = agent.ShortId, AfterLimit = true };
    }

    // A session stopped on a usage limit may be woken once the limit has reset (plus LimitMargin), and only if it has not
    // been woken since the error was written: a wake that worked writes to the transcript, so the same error still last
    // means the wake has not taken effect yet, and a second one would stop a session that is only starting up (the
    // restart's `claude stop` on a resume in progress). The ledger keeps resumes for LoopWindow, so a wake that truly
    // wrote nothing is tried again ten minutes later, not every Grace.
    static bool MayWake(TurnError error, TaskSession session, ResumeLedger ledger, DateTimeOffset now) =>
        error.ResetsAt.Map(resetsAt => now >= WakeAfterLimit(resetsAt)).GetValueOrDefault(false)
        && error.At.Map(at => ledger.Within(session.Id, now, now - at) == 0).GetValueOrDefault(true);

    static Maybe<KeeperStep> GiveUp(TaskItem task, string reason) => new KeeperStep.GiveUp(task.Id, reason);

    static readonly IReadOnlyList<string> GiveUpReasons = [LoopGaveUp, TranscriptGone, NoFolder, ResumeFailed];

    // A give-up shows on the board as the task waiting on Petre with the reason as its question. While it stands, the
    // keeper says nothing more, so a give-up is said once and the session is never woken behind his back. Public so
    // Task 8 (ClaudeSessions) asks the same question when it re-checks a queued resume and when it decides Petre has
    // acted on a give-up, instead of keeping its own copy of the reasons.
    public static bool GivenUp(TaskItem task) =>
        task.Status == TaskStatus.WaitingOnYou && task.Question.Match(q => GiveUpReasons.Contains(q), () => false);
}

/// <summary>What the keeper wants done. Task 8 executes these; the keeper itself does nothing.</summary>
public abstract record KeeperStep
{
    /// <summary>Wake the task's session; <paramref name="SendContinue"/> when its last turn was cut off.</summary>
    public sealed record Resume(Guid TaskId, TaskSession Session, bool SendContinue) : KeeperStep
    {
        /// <summary>
        /// The short id to `claude stop` before the resume: set only for a LISTED idle background session stopped on a
        /// usage limit that has reset (SessionKeeper's header), which a bare resume would copy.
        /// </summary>
        public Maybe<string> StopFirst { get; init; } = Maybe<string>.None;

        /// <summary>The session's last turn died on a usage limit that has since reset; the thread says so.</summary>
        public bool AfterLimit { get; init; }
    }

    /// <summary>Stop trying for this task and tell Petre why, as the task's question.</summary>
    public sealed record GiveUp(Guid TaskId, string Reason) : KeeperStep;

    /// <summary>A live session no task knows: make it a task.</summary>
    public sealed record Adopt(ClaudeAgent Agent) : KeeperStep;

    /// <summary>A finished task's idle background session: `claude stop` it (SessionKeeper.Stops).</summary>
    public sealed record Stop(Guid TaskId, ClaudeAgent Agent) : KeeperStep
    {
        /// <summary>
        /// The task is a recurring one between runs (TaskItem.IsBetweenRuns; task 0b78269e, spec
        /// docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, "A run ends"): the stop ends a finished RUN, not a
        /// finished task. Only a flag; the App uses it to leave the thread alone (Task 7), since a run that stops every
        /// hour would otherwise write a line there every hour.
        /// </summary>
        public bool BetweenRuns { get; init; }
    }
}

/// <summary>
/// When each session was last resumed, immutable. Only what the keeper's rules need is kept: nothing older than
/// <see cref="SessionKeeper.LoopWindow"/> before the newest record can change a decision, so <see cref="Record"/>
/// prunes it.
/// </summary>
public sealed record ResumeLedger(IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> Resumes)
{
    public static readonly ResumeLedger Empty = new(new Dictionary<string, IReadOnlyList<DateTimeOffset>>());

    public ResumeLedger Record(string sessionId, DateTimeOffset at)
    {
        var kept = Resumes.GetValueOrDefault(sessionId, Array.Empty<DateTimeOffset>())
            .Where(past => past > at - SessionKeeper.LoopWindow)
            .Append(at)
            .ToList();
        var next = Resumes.ToDictionary(pair => pair.Key, pair => pair.Value);
        next[sessionId] = kept;
        return new ResumeLedger(next);
    }

    /// <summary>A new ledger without the session: its resumes no longer count, so it can be resumed afresh.</summary>
    public ResumeLedger Forget(string sessionId) =>
        new(Resumes.Where(pair => pair.Key != sessionId).ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>How many resumes of the session happened in the span ending at <paramref name="now"/>.</summary>
    public int Within(string sessionId, DateTimeOffset now, TimeSpan span) =>
        Resumes.GetValueOrDefault(sessionId, Array.Empty<DateTimeOffset>())
            .Count(at => at > now - span && at <= now);
}
