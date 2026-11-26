using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Control;

// `claude-hook`: WHAT CLAUDE CODE'S HOOKS SAY TO DESKBAR, and the `task` forms a session runs for its own task.
// Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md ("Claude sessions", "What a session is offered",
// "Asking and the blink", "After Done or Closed", "Non-interactive sessions", "Who gets it"); plan Tasks 7 and 8;
// measurements in docs/superpowers/notes/2026-11-22-claude-hook-probes.md.
//
//   claude-hook [--entry <value>] [--attended <value>] [--job <value>] [--from <folder>] --json <text>
//
// The hook scripts (written by ClaudeSkill in the app: claude-hook.cmd for Notification and Stop, claude-task-hook.cmd
// for SessionStart, UserPromptSubmit and SessionEnd) run this on every event they are registered for, with the
// hook's JSON on the client's stdin; the client sends it on as --json (ControlArguments.WithInput). --entry and
// --attended are CLAUDE_CODE_ENTRYPOINT and CLAUDE_CODE_SESSION_ATTENDED as the script passed them, and --job is
// CLAUDE_JOB_DIR (set only for a background session, which counts as attended though ATTENDED says 0); ClaudeHookEvent
// turns them into Headless. --from is the session's folder: CLAUDE_PROJECT_DIR, or the client's own folder when Claude
// Code did not set it (ControlArguments.WithCallerFolder). It is normalised (FolderPath: the long form of an 8.3 path,
// one kind of slash, no trailing separator) before it finds a VS Code window or is stored on the task's session.
//
// THE REPLY'S OUTPUT IS WHAT THE SESSION READS: Claude Code adds a SessionStart or UserPromptSubmit hook's stdout to the
// session's context. So it is plain text (SessionBriefing), not the JSON every other command answers, and it is EMPTY
// unless there is something for the session to know. Exit 0 always, except for JSON that is not a hook's (exit 1,
// nothing on stdout): the script turns every exit into 0 anyway, because a Stop hook that exits 2 tells Claude to keep
// going.
//
// The events:
//   Notification, Stop   ask for the user, exactly as the script's `attention` did before this verb existed: the
//                        session's workspace blinks unless the user is on it. Nothing comes back for the session. A
//                        permission prompt never changes a task's status (spec: only an explicit ask does), and a
//                        headless session's asks blink as before (no ruling changes that).
//   SessionStart         the briefing (SessionBriefing.Start). A session that already has a task (a resumed one keeps its
//                        id; a compacted one never ended) is named its task, live on it again; any other is offered the
//                        tasks of its workspace (TaskBoard.Offered; the workspace found as `task new` finds it,
//                        SessionWorkspace) and told how to join one or create its own.
//   UserPromptSubmit     the user's next message changes no status. A task waiting on the user stays waiting (Ruling 49):
//                        the session resolves it with `task answered`. A Done task stays Done (2026-11-23): the session
//                        runs `task join <id>` if the message gives it more work. Then one line reminding the session of
//                        its task, its open question, that it is done, or that the user closed it (SessionBriefing.Prompt).
//   SessionEnd           the session is marked ended; its task stays open ("no live session"). Nothing to read.
//   PostToolUse          only for a Bash or PowerShell command matching `gh pr create*` (the entries' `if` filter,
//                        ClaudeHooks): the pull request it opened goes on the session's own task (task e7aa49b1,
//                        TaskPullRequests). Nothing to read. Also for AskUserQuestion (DialogAnswered, below).
//   PreToolUse           only for AskUserQuestion, Claude Code's multiple-choice dialog: its questions become the task's
//                        question, as `task ask` would make them (task 493b7ba9, DialogQuestion). Nothing to read.
//
// The three task events do nothing at all, and print nothing, for a HEADLESS session (`claude -p`, SDK runs, scheduled
// agents; ClaudeHookEvent's rule), when sessions do not keep tasks (AppState.ClaudeTasksInEffect: a hook entry left
// behind by a crash must not act on a setting that is off), and in a copy with no task service.
//
// FORMER LIMITATION (Ruling 33, gone with Ruling 49): a /loop check-in inside an attended session arrives as a
// UserPromptSubmit that cannot be told from the user's own message, so it used to count as the user answering. Now a
// prompt answers nothing: the session runs `task answered` when the question is settled, and a loop's prompt only
// repeats the reminder that the question is open.
public sealed partial class RemoteControl
{
    ControlReply ClaudeHook(IReadOnlyList<string> rest) =>
        ControlArguments.ParseClaudeHook(rest)
            .Bind(request => ClaudeHookEvent.Parse(request.Json!, request.Entrypoint, request.Attended, request.Job).Map(hook => (request, hook)))
            .Tap(found => QuestionsSettledBy(found.hook))
            .Match(found => Answer(found.hook, found.request.From), ControlReply.Fail);

    // CLAUDE-WAIT: a session's comment waiter registering (plan docs/superpowers/plans/2026-11-24-comment-wakes-session.md,
    // Task 3). The waiting Stop hook (claude-wait-hook.cmd, ClaudeSkill) calls this once, its hook JSON on stdin like any
    // hook, and reads ONE line of ASCII back: the session id and its own generation (CommentInbox). It builds the inbox and
    // gen file paths itself from its own folder, claude-inbox under the shim's (the state folder): a path sent back
    // through the client would reach cmd garbled when it holds a letter outside ASCII, and the client's LF line endings
    // broke `set /p` (final review of the branch, C1 and I2, both measured). Nothing at all for a headless run (nobody would read a comment there), with the task events off, or
    // without an inbox: the script then exits 0 and the session simply has no waiter this turn.
    ControlReply ClaudeWait(IReadOnlyList<string> rest) =>
        ControlArguments.ParseClaudeHook(rest)
            .Bind(request => ClaudeHookEvent.Parse(request.Json!, request.Entrypoint, request.Attended, request.Job).Map(hook => (request, hook)))
            // The manager gets a waiter like any session: it has no task, so no comment is ever addressed to it, but
            // Deskbar's event wakes reach a running, idle manager through its inbox (ManagerWaker; design
            // docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md). Step 1 left it out; step 2 needs it.
            .Match(found => inbox is { } box && !found.hook.Headless && manager.State.ClaudeTasksInEffect
                    ? box.Register(found.hook.SessionId).Match(
                        waiter => Said($"{found.hook.SessionId} {waiter.Generation}\r\n"),
                        _ => Quiet)
                    : Quiet,
                ControlReply.Fail);

    ControlReply Answer(ClaudeHookEvent hook, string? from) => hook.Event switch
    {
        // The reply of `attention` is JSON for a person running it by hand; a hook's session must not read it.
        // An empty --from (cmd passes an unset CLAUDE_PROJECT_DIR as "") is no folder at all (Folder).
        // The session's name, read from its transcript (SessionTitle), tells its window apart from others on the
        // same folder, so the right icon glows and not none of them.
        "Notification" => Quietly(Attention(Folder(from), SessionTitle.Read(hook.Transcript.GetValueOrDefault()))),
        // Stop blinks exactly as before, then may ask a parked session to say so (Parked, below).
        // A question Deskbar set aside while the session worked (SetAside, below) is asked about first, since it names the
        // question; otherwise a parked session is asked to say so (Parked).
        "Stop" => Quietly(Attention(Folder(from), SessionTitle.Read(hook.Transcript.GetValueOrDefault()))) is var blinked
            && SetAside(hook) is { HasValue: true } setAside ? Nudge(hook, SessionBriefing.SetAsideNudge(setAside.Value), blinked)
            : Parked(hook) ? Nudge(hook, SessionBriefing.ParkedNudge, blinked) : blinked,
        _ => tasks is { } service && !hook.Headless && manager.State.ClaudeTasksInEffect
            ? TaskEvent(service, hook, Folder(from) ?? Folder(hook.Folder))
            : Quiet,
    };

    // SessionStart and UserPromptSubmit reach here only when the hook is not headless, so they also tell the task
    // service a person is at the session (Ruling 17, TaskService.HeardAttended): only such sessions are adopted.
    //
    // THE MANAGER SESSION IS LEFT OUT OF TASKS (manager session design, Task 6): a hook from the manager's own folder is
    // answered before Attended runs, so the keeper never adopts it (HeardAttended is never called), SessionStart gets the
    // two-line SessionBriefing.Manager instead of the offer to join or create a task, and a prompt gets nothing at all, so
    // it costs no tokens per message. SessionEnd stays as for any session (it marks nothing for a session with no task).
    // A prompt there is still read for one thing (task 61e4d143, ManagerQuestion.AfterPrompt): the user's reply to the
    // manager clears the manager's open question, so its line leaves "Waiting on you". Still silent.
    ControlReply TaskEvent(TaskService service, ClaudeHookEvent hook, string? folder) => hook.Event switch
    {
        "SessionStart" when IsManagerFolder(folder) => Said(SessionBriefing.Manager(hook.SessionId)),
        "UserPromptSubmit" when IsManagerFolder(folder) => ManagerPrompted(hook),
        "SessionStart" => Said(SessionStarted(Attended(service, hook), hook, folder)),
        "UserPromptSubmit" => Said(Prompted(Attended(service, hook), hook)),
        "SessionEnd" => Ended(service, hook),
        "PreToolUse" => DialogAsked(service, hook, folder),
        "PostToolUse" when hook.ToolName == DialogQuestion.Tool => DialogAnswered(service, hook),
        "PostToolUse" => PullRequestsOpened(service, hook),
        _ => Quiet,
    };

    // PRETOOLUSE, only for AskUserQuestion (the entry's matcher, ClaudeHooks; task 493b7ba9). The session is about to open
    // Claude Code's multiple-choice dialog, which blocks it on Petre exactly as `task ask` would, so its own open task
    // takes the dialog's questions as its question (DialogQuestion): Waiting on you, Petre's, at the top, and the
    // workspace asks for him as `task ask` makes it do. A session with no task, or a tool_input with no question,
    // changes nothing. Silent either way: whatever a PreToolUse hook prints could be read as a decision on the tool.
    //
    // Two tasks are left as they are (review of the branch): one ALREADY waiting on Petre keeps its question, since the
    // dialog's would replace a `task ask` (say, a pull request to merge, which PullRequestWatch settles by that very
    // text) and the dialog's answer would then settle it unseen; and a Done task stays Done, since a dialog in a finished
    // session is not the work resuming, the same reasoning as a prompt not reopening it. The workspace still blinks.
    ControlReply DialogAsked(TaskService service, ClaudeHookEvent hook, string? folder)
    {
        if (hook.ToolName != DialogQuestion.Tool || hook.DialogQuestion.HasNoValue) return Quiet;
        service.Board.TaskOf(hook.SessionId)
            .Where(t => t.Status is not (TaskStatus.WaitingOnYou or TaskStatus.Done))
            .Tap(t => service.Ask(t.Id, hook.DialogQuestion.Value, hook.SessionId));
        Attention(folder);
        return Quiet;
    }

    // POSTTOOLUSE for AskUserQuestion: the dialog returned, so Petre answered it in the terminal, and the task goes back
    // to In progress as `task answered` takes it (Resolve). ONLY when the task still waits on the very question the
    // dialog put there: a question the session asked some other way is the session's to settle. A dialog dismissed with
    // Esc may not run this hook at all (Claude Code's docs do not say; PostToolUseFailure is documented not to fire for
    // an interruption), and then the task keeps waiting until the session runs `task answered`, which the next prompt's
    // reminder line asks it to do (SessionBriefing.Prompt), as for any open question.
    ControlReply DialogAnswered(TaskService service, ClaudeHookEvent hook)
    {
        service.Board.TaskOf(hook.SessionId)
            .Where(t => t.Status == TaskStatus.WaitingOnYou && hook.DialogQuestion.HasValue && t.Question == hook.DialogQuestion)
            .Tap(t => Resolve(service, t, hook.SessionId));
        return Quiet;
    }

    // PostToolUse (task e7aa49b1): the pull requests a `gh pr create` printed go on the session's own open task, or on the
    // task it was last on when that is closed (a session can open a pull request after Petre closed its task, and the
    // search must still find it there). A session with no task at all, or a command that is no `gh pr create`
    // (TaskPullRequests.Created checks again, since the hook's filter is best effort), changes nothing. Silent either way:
    // the session needs to hear nothing, and a hook's output would only clutter its transcript.
    static ControlReply PullRequestsOpened(TaskService service, ClaudeHookEvent hook)
    {
        var links = TaskPullRequests.Created(hook.ToolCommand, hook.ToolOutput);
        if (links.Count > 0)
            service.Board.TaskOf(hook.SessionId).Or(() => service.Board.LastTaskOf(hook.SessionId))
                .Tap(task => links.ToList().ForEach(link => service.AddPullRequest(task.Id, link)));
        return Quiet;
    }

    // A prompt reached the manager (see TaskEvent): when it is the user's reply to the stored manager while its question is
    // open, the question is cleared; a Deskbar wake, another session in the folder or no question changes nothing.
    ControlReply ManagerPrompted(ClaudeHookEvent hook)
    {
        ManagerQuestion.AfterPrompt(Maybe.From(manager.State.Manager), hook.SessionId, hook.Prompt)
            .Tap(manager.SetManagerSession);
        return Quiet;
    }

    // The folder is already normalised (Folder), the same way the task forms read it (RemoteControl.Tasks).
    bool IsManagerFolder(string? folder) => ManagerSession.IsManager(folder, taskSessions?.ManagerFolder ?? "", LongPath);

    static TaskService Attended(TaskService service, ClaudeHookEvent hook)
    {
        service.HeardAttended(hook.SessionId);
        return service;
    }

    // STOP ON A PARKED TASK (stalled tasks, spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md, part 3).
    // Sessions often end a turn waiting on Petre without running `task ask`, so their task stays Claude's and off the bar.
    // Claude Code lets a Stop hook print {"decision":"block","reason":...}: the stop is refused and the reason becomes
    // Claude's next instruction. This does that when ALL hold:
    //   * sessions keep their tasks (ClaudeTasksInEffect) and there is a task service, as for the other task events;
    //   * the session is attended (a headless `claude -p` or SDK run has nobody to ask);
    //   * stop_hook_active is false, so a turn is nudged at most once (Claude Code also caps the loop at 8);
    //   * the session has no background work or scheduled wake-up of its own (it is waiting on that, not on Petre);
    //   * the session's task is open, In progress and Claude's (an ask or done already moved it, and a task Petre took
    //     is his).
    //   * the turn did not itself begin with a nudge (TaskService.TurnFromNudge, see Nudge below).
    // The cost: one short extra turn on most replies of a session with a task, delivered quietly where it can be (Nudge,
    // task 8710ac09). What this misses, StalledTasks catches after 15 quiet minutes: it asks the session again, and hands
    // the task back to Petre only if the session does not answer.
    bool Parked(ClaudeHookEvent hook) =>
        tasks is { } service
        && manager.State.ClaudeTasksInEffect
        && !hook.Headless
        && !hook.StopHookActive
        && !service.TurnFromNudge(hook.SessionId)
        && !hook.BackgroundWork
        && service.Board.TaskOf(hook.SessionId)
            .Map(t => t is { IsOpen: true, Status: TaskStatus.InProgress } && t.Assignee.IsClaude)
            .GetValueOrDefault(false);

    // STOP WITH A QUESTION SET ASIDE (task 8bd18246, RepliedTasks): Deskbar took the task out of "Waiting on you" while the
    // session worked after Petre's reply, and only the session can say whether the question still holds (Ruling 49). So
    // its turn ends with that question, once (stop_hook_active), when the session is attended, sessions keep their tasks,
    // and its task is open, In progress and carries a set-aside question. UNLIKE Parked, background work does not excuse
    // it: a session watching its pull request is exactly the one whose "merge it" question may still hold, and if nothing
    // asked it, the task would sit as Claude's until StalledTasks hands it back. Returns the question, or none.
    Maybe<string> SetAside(ClaudeHookEvent hook) =>
        tasks is { } service
        && manager.State.ClaudeTasksInEffect
        && !hook.Headless
        && !hook.StopHookActive
        && !service.TurnFromNudge(hook.SessionId)
            ? service.Board.TaskOf(hook.SessionId)
                .Where(t => t is { IsOpen: true, Status: TaskStatus.InProgress })
                .Bind(t => t.QuestionSetAside)
            : Maybe<string>.None;

    // HOW A STOP NUDGE REACHES THE SESSION (task 8710ac09). Two roads, the quiet one first:
    //   * INBOX: the session has a waiter that can still be alive (CommentInbox.Lapsed is false; this very Stop also
    //     starts a new one, which takes over the inbox). The nudge goes into its inbox, the Stop itself is let through,
    //     and the waiter wakes the session a moment later with the nudge as a new message. Claude Code shows that as ONE
    //     line, the waiter's summary ("Deskbar", ClaudeHooks.WaitEntry), then the session's short reply.
    //   * BLOCK: no waiter (a Claude Code without asyncRewake, a session just resumed whose waiter Deskbar retired, no
    //     inbox at all): {"decision":"block","reason":...}, as before. Claude Code shows every Stop hook that blocks as
    //     "Stop hook reported a problem" in yellow with the whole text under it, whatever the text says.
    // Measured 2026-11-26 on Claude Code 2.1.332, a throwaway session in a pseudo console with one hook of each kind: the
    // block rendered as "Ran 2 stop hooks / Stop hook reported a problem / <reason>", the waiter's wake as
    // "● Deskbar: probe summary" and the reply. Petre saw the yellow line twice and read it as a failure ("what's the
    // problem?"), so the quiet road is the one he asked to try.
    //
    // THE LOOP GUARD MOVES WITH IT. A block's next Stop carries stop_hook_active, which is what lets Parked and SetAside
    // nudge a turn once. A waiter's wake is a NEW turn, whose Stop does not carry it, so without a guard every nudged turn
    // would be nudged again. TaskService.TurnFromNudge says whether the session's current turn began with a nudge
    // (Prompted records it), and Parked and SetAside stand down for such a turn.
    // A nudge put in the inbox is told to the task service (NudgeSent), which counts the session busy until the waiter has
    // woken it, so the poll does not read the second of idleness in between as a quiet session (review of the branch).
    ControlReply Nudge(ClaudeHookEvent hook, string nudge, ControlReply blinked) =>
        inbox is { } box && !box.Lapsed(hook.SessionId) && box.Deliver(hook.SessionId, nudge).IsSuccess
            ? Delivered(hook.SessionId, blinked)
            : Said(System.Text.Json.JsonSerializer.Serialize(new { decision = "block", reason = nudge }));

    ControlReply Delivered(string sessionId, ControlReply blinked)
    {
        tasks?.NudgeSent(sessionId);
        return blinked;
    }

    static readonly ControlReply Quiet = new(ControlReply.Ok, "");

    static ControlReply Said(string text) => new(ControlReply.Ok, text);

    // Whatever the ask answered, the session hears nothing. A refusal from RequestAttention is the app's concern (it
    // means a COM call failed), not the session's, and the hook would print it into the conversation for nothing.
    // The exit code is kept (review of Task 7, M-4), so the app's trace line (`ctl ... -> <code>`) still says an ask
    // failed; the output goes, and the hook still exits 0, because the script ends `@exit /b 0` and the client prints
    // nothing for an empty hook answer (ControlConsole.Silent).
    static ControlReply Quietly(ControlReply reply) => reply with { Output = "" };

    // SessionStart. TaskOf finds the task a still-live session is on (compact, clear, or a second SessionStart);
    // LastTaskOf finds the one a resumed session was on before its SessionEnd, and Rejoin makes it live there again (the
    // same id replaces its old entry) WITHOUT touching the task's status (Ruling 34): resuming a session to look at it
    // is not continuing the work, so a Done or waiting task stays as it is until the session itself moves it (`task join`
    // or `task answered`; a prompt no longer does, see Prompted).
    // Only a session with no open task at all is offered a choice.
    //
    // /clear (Ruling 19): a session /clear marked replaced that starts again under the SAME id (unmeasured whether /clear
    // keeps it) rejoins as a fresh entry, which drops the mark, so the keeper may resume it again later. A session with
    // no task at all that starts with source "clear" takes the place of the session cleared in its folder within the
    // last minute (TaskService.JoinAfterClear), so the task Petre was working on follows him into the fresh conversation.
    //
    // A RESUME ON A CLOSED TASK REOPENS IT (task 327e3cd0, Petre's call, and the one exception to Ruling 34). A closed
    // task has left the list, so a session resumed on it had no task at all: the briefing said there was nothing to join
    // and its first prompt said new work needed a new task, and the session made a duplicate of the task Petre had just
    // resumed it to carry on with. So when a session with no open task resumes (source "resume") and the task it was on
    // LAST is closed (TaskBoard.ClosedLastTaskOf), it joins it, which reopens it (TaskBoard.Join): In progress, Claude's,
    // back at the top of its quadrant, and the briefing says so. Only "resume": a compact or a /clear is the same session
    // carrying on, and a session started fresh has a new id. Deskbar's own resumes never land here by accident: the keeper
    // resumes open tasks only, and a comment that resumes a closed task's session is Petre talking to it.
    string SessionStarted(TaskService service, ClaudeHookEvent hook, string? folder)
    {
        var current = service.Board.TaskOf(hook.SessionId);
        var resumed = hook.Source.Map(s => s == "resume").GetValueOrDefault(false);
        var reopened = current.HasNoValue && resumed
            && service.Board.ClosedLastTaskOf(hook.SessionId)
                .Map(t => service.Join(t.Id, service.NewSession(hook.SessionId, folder ?? "")).IsSuccess)
                .GetValueOrDefault(false);
        if (!reopened && (current.HasNoValue || current.Value.Sessions.Any(s => s.Id == hook.SessionId && s.Replaced)))
            service.Board.LastTaskOf(hook.SessionId).Tap(t => service.Rejoin(t.Id, service.NewSession(hook.SessionId, folder ?? "")));
        if (service.Board.LastTaskOf(hook.SessionId).HasNoValue && hook.Source.Map(s => s == "clear").GetValueOrDefault(false) && folder is not null)
            service.JoinAfterClear(hook.SessionId, folder, stored => FolderPath.Same(stored, folder, LongPath));
        var own = service.Board.TaskOf(hook.SessionId);
        // The workspace whose tasks are offered is found the way `task new` finds one (SessionWorkspace): the VS Code
        // window showing the folder, else the workspace of the latest task worked on in the session's repo. The window
        // alone left a session started in a terminal (no VS Code window on its folder) offered only the free Claude
        // tasks (task d8c152cf). No session is passed: a session offered a choice has no own task to ask.
        var offered = own.HasValue ? [] : service.Board.Offered(SessionWorkspace(service, folder, Maybe<string>.None));
        // A session coming back to a subtask (or a parent) reads its family again: siblings moved on while it was away.
        // Its linked tasks too (task links spec): what it duplicates and what is related, with their sessions.
        var family = own.Bind(t => AroundOf(service, t)).GetValueOrDefault("");
        // The own task's description images are listed with absolute paths (images in tasks, TaskService.ExpandImages).
        return SessionBriefing.Start(hook, own, offered, service.ExpandImages, family, reopened);
    }

    // UserPromptSubmit (Ruling 49): a prompt NO LONGER settles a question. A task Waiting on the user stays waiting, and
    // its blink stays, until the session itself runs `task answered` (see SessionTask): the session knows best whether
    // its question is settled, and a prompt is not proof. A /loop check-in or a scheduled prompt arrives as a
    // UserPromptSubmit from the attended session, indistinguishable from the user's own message (Ruling 33's known
    // limitation, which this removes: such a prompt can no longer answer anything). Instead the reminder line says the
    // question is still open (SessionBriefing.Prompt).
    //
    // A DONE task stays Done on a prompt too (2026-11-23, Petre agreed): a message to a finished session is as often a
    // question about the work ("is it done?", a screenshot of the board) as more work, and the hook cannot tell the two
    // apart, so every such question used to reopen the task and leave it In progress on Petre's board. The session can
    // tell: the reminder for a Done task (SessionBriefing.Prompt) tells it to run `task join <id>` when the message does
    // give it more work, and Join already moves a Done task back to In progress. Same reasoning as Ruling 49 for a
    // waiting task: the session decides, a prompt proves nothing.
    //
    // A PROMPT IS REMEMBERED (task 8bd18246, RepliedTasks): a prompt after a task began waiting is Petre replying in the
    // terminal, and once the session then works, Deskbar sets the question aside. It settles nothing here (Ruling 49
    // stands). A prompt Deskbar itself put there (a delivered comment, the keeper's resume message) is not a reply, and
    // neither is one Claude Code put there (a subagent's hand-back, another session's message, a background job's notice,
    // task b846f5ad), so both are left out (SessionBriefing.FromTheUser). A hook with no prompt field still counts, as before.
    string Prompted(TaskService service, ClaudeHookEvent hook)
    {
        var sessionId = hook.SessionId;
        // EVERY prompt, whoever put it there, starts a turn: the tasks panel's working part takes it as proof the session
        // is working, past the resume grace (ClaudeActivity, task 10b49554).
        service.TurnStarted(sessionId);
        // Whether this turn began with a Stop nudge the waiter delivered (Nudge): such a turn is not nudged again.
        service.NudgedTurn(sessionId, hook.Prompt.Map(SessionBriefing.OnlyStopNudge).GetValueOrDefault(false));
        if (hook.Prompt.Map(SessionBriefing.FromTheUser).GetValueOrDefault(true)) service.Prompted(sessionId);
        var own = service.Board.TaskOf(sessionId);
        return SessionBriefing.Prompt(sessionId, own, own.HasValue ? Maybe<TaskItem>.None : service.Board.ClosedTaskOf(sessionId), service.ExpandImages,
            // Deskbar task c8e6967d: the session hears that Deskbar watches its pull request (and stops its own loop).
            own.HasValue && service.IsWatched(own.Value));
    }

    // SessionEnd. Three cases:
    //   * reason "clear" (Ruling 19): the session is marked replaced and ended, whatever kind it is, so the keeper never
    //     resumes the conversation Petre cleared, and the fresh session that follows can take its place (SessionStarted).
    //   * a BACKGROUND session's own hook (final review, item 13): it sends SessionEnd at the end of every turn while
    //     `claude agents --json` still lists it (Review Focus 1). This branch runs only while sessions keep their tasks
    //     (Answer checks ClaudeTasksInEffect), which is exactly when ClaudeSessions reads that list every 15 seconds,
    //     and the list is the truth: it ends the session when it really goes (TaskBoard.Reconcile). Ending it here only
    //     made the board flicker "ended" after every turn until the next poll revived it, so it is skipped.
    //   * anything else: the session is marked ended; its task stays open.
    ControlReply Ended(TaskService service, ClaudeHookEvent hook)
    {
        if (hook.Reason.Map(r => r == "clear").GetValueOrDefault(false)) service.SessionCleared(hook.SessionId);
        else if (!hook.Background) service.SessionEnded(hook.SessionId);
        // A comment wakes its session: an ended session's inbox and gen file go with it (its waiter, if any, then finds
        // no gen file of its number and exits). A comment for it later resumes it instead (CommentWake). NOT a background
        // session's, unless /clear ended it: it sends SessionEnd at the end of every turn while it lives on (the reason the
        // board ignores it above), and forgetting then would stop its waiter and delete a comment waiting for it (final
        // review of the branch, C2). Its files stay until it really goes; a comment for it then resumes it all the same.
        if (!hook.Background || hook.Reason.Map(r => r == "clear").GetValueOrDefault(false)) inbox?.Forget(hook.SessionId);
        return Quiet;
    }

    // --- the session's own `task` forms ------------------------------------------------------------
    //
    //   task join <id> --session <sid>          take up an offered task (one of the user's notes becomes Claude's work)
    //   task new <title> --session <sid>        create the session's task, on the workspace SessionWorkspace finds (the
    //                                           VS Code window showing the folder, else the repo's latest task's), or none
    //   task status-line <text> --session <sid> the short line under the title ("waiting for CI on #398")
    //   task ask <question> --session <sid>     waiting on the user with the question, and asks for them as `attention`
    //   task answered --session <sid>           the session's question is settled: back to In progress, the blink stops
    //   task done --session <sid>               the session's clear finish; only the user closes
    //   task comment <markdown> --session <sid> a comment on the session's own task, signed by the session
    //   task comment-settle <id> --session <sid> settle one of the session's own comments (folds away; answered settles them since its ask)
    //   task later <title> --session <sid>      a Backlog note on the caller's workspace, a follow-up for later, related to
    //                                           the session's own task when it has one (task fa1e6a74)
    //   task add <title> [flags] --session <sid> `task add` with its flags, related to the session's own task the same way
    //   task subtask <title> [--no-start] [--model <model>] --session <sid>
    //                                           a subtask of the session's own task (a sibling when that is a subtask), with
    //                                           a Claude session started on it at once unless --no-start (task fa056b2f)
    //   task repeat <schedule>|off [--model <m>] [--folder <p>] --session <sid>
    //                                           the session's own task repeats (recurring tasks spec, task 0b78269e)
    //   task family --session <sid>             the family and linked tasks of the session's own task (subtasks spec)
//   task pr <url> --session <sid>           a pull request the session opened some other way than `gh pr create`, on its
//                                           own task (task e7aa49b1; the hook records `gh pr create` by itself)
    //   task deployed <note> --session <sid>    the session's own task's change was seen running where its app is deployed,
    //                                           the note the evidence (task 8470d176, phase 2; TaskBoard.MarkDeployed)
    //   task duplicate-of <id> --session <sid>  the session's own task closes as a duplicate of <id> (task links spec)
    //   task relate <id> --session <sid>        links the session's own task to <id> as related
    //   task unrelate <id> --session <sid>      removes that link
    //
    // Everything but join, new, later and add acts on the session's OWN task (TaskBoard.TaskOf), which is the spec's rule that
    // a session only settles tasks it is working on: there is no id to point at someone else's. A session with no open
    // task is refused, and nothing changes. `--session` is the hook's session_id, which the briefing hands the session.
    ControlReply SessionTask(TaskService service, string[] args, string session, string? from)
    {
        var folder = Folder(from);
        Result<TaskItem> Own() => service.Board.TaskOf(session)
            .ToResult($"Session {session} has no open task. Join one (task join <id> --session {session}) or create one (task new \"<title>\" --session {session}).");
        Maybe<Guid> Here() => SessionWorkspace(service, folder, session);
        ControlReply Changed(Result<TaskItem> task) =>
            task.Map(t => service.Board.Tasks.First(x => x.Id == t.Id)).Match(t => Success(Describe(t)), ControlReply.Fail);

        return args switch
        {
            // The join reply carries the task's family (subtasks spec): a session started on a subtask reads it here,
            // since its SessionStart briefing ran before it had a task.
            ["join", var id] => service.Board.Find(id).Bind(t => service.Join(t.Id, service.NewSession(session, folder ?? "")).Map(() => t))
                .Map(t => service.Board.Tasks.First(x => x.Id == t.Id))
                .Match(t => Success(WithFamily(service, t)), ControlReply.Fail),
            ["family"] => Own().Match(t => Said(AroundOf(service, t).GetValueOrDefault(NoFamily)), ControlReply.Fail),
            // TASK LINKS (docs/superpowers/specs/2026-11-25-task-links-design.md). Petre, 2026-11-25: a session that finds
            // its task is work already done or under way elsewhere closes it as a duplicate itself (he reopens it when it
            // is wrong). It is the one way a session closes a task, and narrow: only its own, only as a duplicate of a
            // task that exists. Past Claude-still-working (stoppingClaude), since the session asking is the one working.
            // The reply is the task read back, now closed; the target is looked up first, so a bad id changes nothing.
            ["duplicate-of", var of] => Changed(Own().Bind(t => service.Board.Find(of)
                .Bind(o => service.MarkDuplicate(t.Id, o.Id, stoppingClaude: true)).Map(() => t))),
            ["relate", var other] => Changed(Own().Bind(t => service.Board.Find(other)
                .Bind(o => service.Relate(t.Id, o.Id)).Map(() => t))),
            ["unrelate", var other] => Changed(Own().Bind(t => service.Board.Find(other)
                .Bind(o => service.Unrelate(t.Id, o.Id)).Map(() => t))),
            // A session splits its work: a subtask of its own task, or a sibling when its task is already a subtask, and
            // a Claude session starts on it at once unless --no-start says not to (SessionSubtask, task fa056b2f).
            ["subtask", .. var rest] => SessionSubtask(service, session, folder, rest),
            // A session makes its own task recurring (recurring tasks spec, task 0b78269e): `task repeat <schedule>|off
            // [--model <m>] [--folder <p>]`. Own task only, there is no id to point at another. The folder defaults to the
            // REPO of the folder the session was started in (CallerFolder, as `subtask` reads it), else it is refused.
            ["repeat", .. var rest] => TaskRepeatReply(service, Own(), rest,
                t => CallerFolder(service, session, folder) is { } here ? Result.Success(here) : Result.Failure<string>("No folder is known for this session; pass --folder <path>"),
                taskSessions?.FolderExists ?? (_ => true)),
            ["new", var title] => Changed(service.Create(title, Here(), service.NewSession(session, folder ?? ""))),
            ["status-line", var text] => Changed(Own().Bind(t => service.SetStatusLine(t.Id, text).Map(() => t))),
            ["ask", var question] => Changed(Own().Bind(t => service.Ask(t.Id, question, session).Map(() => t))
                .Tap(_ => Attention(folder))),
            ["done"] => Changed(Own().Bind(t => service.MarkDone(t.Id).Map(() => t))),
            // Ruling 49: the session says its question is settled (the user's reply answered it, or the session settled it
            // itself). Waiting on you goes back to In progress, the question goes and the workspace's blink and glow stop.
            // A task that is not waiting is left alone and the command still succeeds: a session may run it needlessly.
            ["answered"] => Changed(Own().Tap(t => Resolve(service, t, session))),
            ["comment", var markdown] => Changed(Own().Bind(t => service.Comment(t.Id, session, markdown).Map(() => t))),
            // Ruling 62: the session settles one of its OWN comments by id (answered settles the ones since its ask); the board
            // refuses another session's or Petre's.
            ["comment-settle", var comment] => Changed(service.Board.FindComment(comment)
                .Bind(c => service.SettleComment(c.Id, true, session).Map(() => service.Board.Tasks.First(t => t.Comments.Any(x => x.Id == c.Id))))),
            // The follow-up is linked as related to the session's own task, when it has one (task fa1e6a74), so the note
            // says where it came from and the task lists what it left behind.
            ["later", var title] => Changed(service.AddNoteFrom(session, title, Here())),
            // `task add` with --session: the script form with its flags, linked the same way as `later` (TaskAdd).
            ["add", .. var more] => TaskAdd(service, more, from, session),
            // A pull request by its URL (task e7aa49b1). Anything that is not one GitHub pull request link is refused
            // before the task is touched.
            ["pr", var url] => AskedPullRequests.In(url) is [var link]
                ? Changed(Own().Bind(t => service.AddPullRequest(t.Id, link).Map(() => t)))
                : ControlReply.Fail($"'{url}' is not a GitHub pull request link (https://github.com/<owner>/<repo>/pull/<n>)"),
            // A DEPLOY SEEN RUNNING (task 8470d176, phase 2; RunningChanges' header). The session checked the deployed
            // app's logs, health page or deploy output for the version carrying its merge, and the note is what it saw
            // ("2026.11.25.39-3749d21 in the portal logs"), usually on stdin. Every recorded pull request of its own task
            // not reported yet is recorded; a blank note, or a task with no recorded pull request, is refused.
            ["deployed", var note] => Changed(Own().Bind(t => service.MarkDeployed(t.Id, note).Map(() => t))),
            _ => ControlReply.Fail($"Wrong arguments for a session's task form.\n{TaskUsage()}"),
        };
    }

    // A SESSION'S SUBTASK STARTS ITS OWN SESSION (task fa056b2f, Petre: "when agents are creating tasks for claude, start
    // them immediately unless asked not to explicitly"). `task subtask` is the one form that makes a task FOR Claude
    // (TaskBoard.AddSubtask assigns it to Claude; `later` and `add` make the user's notes). It used to sit in the
    // Backlog until a session on the workspace was offered it, or the user clicked "Start Claude on this", so a split
    // piece of work waited on someone noticing it. Now the subtask is made, and then a background session starts on it
    // through the same path as `task start` and the menu's "Start Claude on this" (TaskSessionCommands.Start: the
    // `claude --bg` whose first message joins the task, so the new session reads its family and this session's work).
    //
    //   --no-start      the old behaviour: the subtask waits in the Backlog. The explicit "not now" the task asks for.
    //   --model <name>  the new session's model (ClaudeStart.Model), so a session can hand a mechanical piece to a cheaper
    //                   one; without it the start uses `claude-model`, as every start does. Refused with --no-start,
    //                   since nothing would use it.
    //
    // The folder is the REPO of the folder the calling session was started in (ClaudeStart.RepoOf), as its own task
    // records it (the SessionStart hook's folder), else of the shell folder the command came from: the piece is split
    // off the work in that repo. The recorded folder comes first because the shell's is wherever the session last
    // `cd`'d to (a subfolder, another repo). A worktree folder is left out because worktrees are deleted after their
    // merge (ClaudeStart.Folders says the same). With neither, the likeliest of the subtask's own candidates
    // (ClaudeStart.Folders leads with the parent's repo).
    //
    // QUIET (TaskSessionCommands.StartQuietly): nobody clicked for this start, so the app opens no window on the new
    // session and shows no message; it runs in the background and shows in the tasks panel's Claude block, where Open
    // Claude attaches to it.
    //
    // Parsing, the model's name and the subtask itself come first: a refusal there changes nothing. A start that cannot
    // happen (Claude sessions off, no folder, no session commands in this copy) does NOT undo the subtask, which is still
    // real work split off; the reply says it was not started and why, so the session can tell the user or run
    // `task start` itself. A failure after the start is under way (an untrusted folder) lands in the subtask's thread.
    //
    // The reply is the subtask as every task form answers it (Describe), plus `started` (true or false) and, when false
    // for a reason other than --no-start, `notStarted` with that reason.
    ControlReply SessionSubtask(TaskService service, string session, string? folder, string[] words) =>
        SubtaskWords.Parse(words)
            .Ensure(w => w.Start || w.Model is null, "--model names the started session's model; with --no-start no session starts")
            .Bind(w => (w.Model is { } given ? ClaudeStart.Model(given).Map(Maybe.From) : Result.Success(Maybe<string>.None))
                .Bind(model => service.AddSubtask(session, w.Title!)
                    .Map(made => (Task: made, Started: w.Start ? StartSubtask(service, made, session, folder, model) : Maybe<Result>.None))))
            .Match(made => SubtaskReply(service.Board.Tasks.First(t => t.Id == made.Task.Id), made.Started), ControlReply.Fail);

    // The start itself (see above): None when --no-start asked for none, else whether it got under way.
    Maybe<Result> StartSubtask(TaskService service, TaskItem subtask, string session, string? folder, Maybe<string> model) =>
        taskSessions is not { } commands
            ? Result.Failure("Claude sessions are not available in this copy of Deskbar")
            : (CallerFolder(service, session, folder) is { } here ? ClaudeStart.RepoOf(here) : commands.Folders(subtask).FirstOrDefault()) is { } where
                ? (commands.StartQuietly ?? commands.Start)(service.Board.Tasks.First(t => t.Id == subtask.Id), where, model)
                : Result.Failure($"No folder is known for it; run task start {subtask.Id} --folder <path>");

    // The folder the calling session was started in, as its own task records it, else the shell folder (see above).
    static string? CallerFolder(TaskService service, string session, string? shell) =>
        service.Board.TaskOf(session)
            .Bind(own => own.Sessions.TryFirst(s => s.Id == session && s.Folder != ""))
            .Map(s => (string?)s.Folder)
            .GetValueOrDefault(shell);

    // Describe's JSON with `started` and, for a start that was wanted and refused, `notStarted` (see above).
    ControlReply SubtaskReply(TaskItem subtask, Maybe<Result> started)
    {
        var reply = System.Text.Json.JsonSerializer.SerializeToNode(Describe(subtask), Json)!.AsObject();
        reply["started"] = started.Map(r => r.IsSuccess).GetValueOrDefault(false);
        started.Where(r => r.IsFailure).Tap(r => reply["notStarted"] = r.Error);
        return Success(reply);
    }

    // The words of `task subtask` after the verb (--session is already off): the title and, in any order, --no-start and
    // --model <name>. The first other word is the title, even one starting with "--" (free text from stdin is taken as
    // it is); a second one, or a missing title, is refused with the form's usage line.
    sealed record SubtaskWords(string? Title, bool Start, string? Model)
    {
        public static Result<SubtaskWords> Parse(string[] words) => Parse(words, new SubtaskWords(null, true, null));

        static Result<SubtaskWords> Parse(string[] words, SubtaskWords so) => words switch
        {
            [] => so.Title is null ? Result.Failure<SubtaskWords>($"A subtask needs a title.\n{Usage}") : Result.Success(so),
            ["--no-start", .. var rest] => Parse(rest, so with { Start = false }),
            ["--model", var model, .. var rest] => Parse(rest, so with { Model = model }),
            [var title, .. var rest] when so.Title is null =>
                Parse(rest, so with { Title = title }),
            [var other, ..] => Result.Failure<SubtaskWords>($"task subtask does not take '{other}'.\n{Usage}"),
        };

        const string Usage = "task subtask <title>|- [--no-start] [--model <model>] --session <session>";
    }

    // The workspace a session's `task new` and `task later` go on (task f905b1a7: "tasks added to the deskbar by claude
    // sessions should be given proper workspaces, not none"). The first of these that answers wins:
    //   1. the workspace of the VS Code window that shows the session's folder (CallerOf), as before;
    //   2. the workspace of the session's own open task: a follow-up note belongs with the work it follows up;
    //   3. the workspace of the latest task worked on in the session's repo (RepoWorkspace.Latest).
    // Only when none answers does the task go on no workspace. The window used to be the only source, and since sessions
    // run in the Windows Terminal windows Deskbar opens there is often no VS Code window at all, so every task a
    // session made was on none.
    Maybe<Guid> SessionWorkspace(TaskService service, string? folder, Maybe<string> session) =>
        CallerOf(folder).Workspace is { } w
            ? w.Id
            : session.Bind(service.Board.TaskOf).Bind(t => t.WorkspaceId).Where(WorkspaceExists)
                .Or(() => folder is null ? Maybe<Guid>.None : RepoWorkspace.Latest(service.Board, folder, WorkspaceExists));

    bool WorkspaceExists(Guid id) => manager.State.Workspaces.Any(w => w.Id == id);

    // `task answered`: only a WAITING task is touched (Answered would also resume a Done one, which is not a question).
    void Resolve(TaskService service, TaskItem task, string session)
    {
        // A question Deskbar SET ASIDE (task 8bd18246: In progress, QuestionSetAside) is settled by `task answered` too, as
        // TaskBoard.Answered allows. Leaving it out made the session's `task answered` a no-op on it, so the Stop hook
        // asked about the same question at the end of every turn after the reply (SetAside, SessionBriefing.SetAsideNudge).
        if (task.Status != TaskStatus.WaitingOnYou && !(task.Status == TaskStatus.InProgress && task.QuestionSetAside.HasValue)) return;
        service.Answered(session);
        task.WorkspaceId.Tap(w => manager.ClearAttention(w));
    }

    // `--session <id>` may stand anywhere among a `task` command's words (the client's --from is already off the end).
    static (string[] Args, string? Session) SplitSession(string[] args) =>
        Array.IndexOf(args, "--session") is var at && at >= 0 && at + 1 < args.Length
            ? ([.. args[..at], .. args[(at + 2)..]], args[at + 1])
            : (args, null);

    // The setting that lets Claude Code sessions keep their tasks (AppState.ClaudeTasks), the remote twin of Manage's
    // checkbox "Claude Code sessions keep their tasks in the task list". The reply says whether it is in effect, since
    // it is off while "Teach Claude Code about Deskbar" (claude-skill) is.
    ControlReply ClaudeTasks(string onOff) =>
        onOff.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" => Reply(manager.SetClaudeTasks(true), DescribeClaudeTasks),
            "off" or "false" or "no" => Reply(manager.SetClaudeTasks(false), DescribeClaudeTasks),
            _ => ControlReply.Fail(ControlUsage.LineFor("claude-tasks")),
        };

    object DescribeClaudeTasks() => new
    {
        claudeTasks = manager.State.ClaudeTasks ? "on" : "off",
        inEffect = manager.State.ClaudeTasksInEffect,
    };

    // Whether the sessions of open tasks are resumed when they stop running (AppState.ClaudeResume), the remote twin of
    // Manage's "Resume the sessions of open tasks after a restart or a crash". Like claude-tasks, the reply says whether it
    // is in effect, since it is off while sessions do not keep tasks at all.
    ControlReply ClaudeResume(string onOff) =>
        onOff.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" => Reply(manager.SetClaudeResume(true), DescribeClaudeResume),
            "off" or "false" or "no" => Reply(manager.SetClaudeResume(false), DescribeClaudeResume),
            _ => ControlReply.Fail(ControlUsage.LineFor("claude-resume")),
        };

    object DescribeClaudeResume() => new
    {
        claudeResume = manager.State.ClaudeResume ? "on" : "off",
        inEffect = manager.State.ClaudeResumeInEffect,
    };

    // The model task sessions start on (AppState.ClaudeTaskModel; design
    // docs/superpowers/specs/2026-11-25-session-model-pick-design.md). With no name it only says what is set; the
    // setting has no box in Manage yet, so this is how it is read and changed.
    ControlReply ClaudeModel(Maybe<string> model) =>
        model.Match(name => Reply(manager.SetClaudeTaskModel(name), DescribeClaudeModel), () => Success(DescribeClaudeModel()));

    object DescribeClaudeModel() => new { claudeModel = manager.State.ClaudeTaskModelInEffect };
}
