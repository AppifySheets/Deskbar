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
//   claude-hook [--entry <value>] [--attended <value>] [--from <folder>] --json <text>
//
// claude-hook.cmd (written by ClaudeSkill in the app) runs this on every hook event it is registered for, with the
// hook's JSON on the client's stdin; the client sends it on as --json (ControlArguments.WithHookInput). --entry and
// --attended are CLAUDE_CODE_ENTRYPOINT and CLAUDE_CODE_SESSION_ATTENDED as the script passed them; ClaudeHookEvent
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
//                        tasks of its workspace (TaskBoard.Offered) and told how to join one or create its own.
//   UserPromptSubmit     the user's next message: the session's task, when waiting on the user or Done, goes back to In
//                        progress (TaskService.Answered) and its workspace stops blinking for it; then one line
//                        reminding the session of its task (SessionBriefing.Prompt), or that the user closed it.
//   SessionEnd           the session is marked ended; its task stays open ("no live session"). Nothing to read.
//
// The three task events do nothing at all, and print nothing, for a HEADLESS session (`claude -p`, SDK runs, scheduled
// agents; ClaudeHookEvent's rule), when sessions do not keep tasks (AppState.ClaudeTasksInEffect: a hook entry left
// behind by a crash must not act on a setting that is off), and in a copy with no task service.
//
// KNOWN LIMITATION (Ruling 33): a /loop check-in inside an attended session arrives as a UserPromptSubmit from that
// attended session, with nothing in the JSON or the environment to tell it from the user's own message (unmeasured;
// the probes found no such field). So a loop's prompt counts as the user answering: it moves the session's task from
// Waiting on the user back to In progress. The live check is to send one /loop prompt and look at the hook's JSON.
public sealed partial class RemoteControl
{
    ControlReply ClaudeHook(IReadOnlyList<string> rest) =>
        ControlArguments.ParseClaudeHook(rest)
            .Bind(request => ClaudeHookEvent.Parse(request.Json!, request.Entrypoint, request.Attended).Map(hook => (request, hook)))
            .Match(found => Answer(found.hook, found.request.From), ControlReply.Fail);

    ControlReply Answer(ClaudeHookEvent hook, string? from) => hook.Event switch
    {
        // The reply of `attention` is JSON for a person running it by hand; a hook's session must not read it.
        // An empty --from (cmd passes an unset CLAUDE_PROJECT_DIR as "") is no folder at all (Folder).
        "Notification" or "Stop" => Quietly(Attention(Folder(from))),
        _ => tasks is { } service && !hook.Headless && manager.State.ClaudeTasksInEffect
            ? TaskEvent(service, hook, Folder(from) ?? Folder(hook.Folder))
            : Quiet,
    };

    ControlReply TaskEvent(TaskService service, ClaudeHookEvent hook, string? folder) => hook.Event switch
    {
        "SessionStart" => Said(SessionStarted(service, hook, folder)),
        "UserPromptSubmit" => Said(Prompted(service, hook.SessionId)),
        "SessionEnd" => Ended(service, hook.SessionId),
        _ => Quiet,
    };

    static readonly ControlReply Quiet = new(ControlReply.Ok, "");

    static ControlReply Said(string text) => new(ControlReply.Ok, text);

    // Whatever the ask answered, the session hears nothing. A refusal from RequestAttention is the app's concern (it
    // means a COM call failed), not the session's, and the hook would print it into the conversation for nothing.
    static ControlReply Quietly(ControlReply _) => Quiet;

    // SessionStart. TaskOf finds the task a still-live session is on (compact, clear, or a second SessionStart);
    // LastTaskOf finds the one a resumed session was on before its SessionEnd, and Join makes it live there again (the
    // same id replaces its old entry, TaskBoard.Join, which also brings a Done task back to In progress: resuming is
    // taking the work up again). Only a session with no open task at all is offered a choice.
    string SessionStarted(TaskService service, ClaudeHookEvent hook, string? folder)
    {
        if (service.Board.TaskOf(hook.SessionId).HasNoValue)
            service.Board.LastTaskOf(hook.SessionId).Tap(t => service.Join(t.Id, service.NewSession(hook.SessionId, folder ?? "")));
        var own = service.Board.TaskOf(hook.SessionId);
        var offered = own.HasValue ? [] : service.Board.Offered(CallerOf(folder).Workspace is { } w ? w.Id : Maybe<Guid>.None);
        return SessionBriefing.Start(hook, own, offered);
    }

    // UserPromptSubmit: the user answered. A task that was waiting on the user stops asking for them too: its
    // workspace's blink and its windows' glow go (WorkspaceManager.ClearAttention), since the answer has come.
    string Prompted(TaskService service, string sessionId)
    {
        var waiting = service.Board.TaskOf(sessionId).Where(t => t.Status == TaskStatus.WaitingOnYou);
        service.Answered(sessionId);
        waiting.Bind(t => t.WorkspaceId).Tap(w => manager.ClearAttention(w));
        var own = service.Board.TaskOf(sessionId);
        return SessionBriefing.Prompt(sessionId, own, own.HasValue ? Maybe<TaskItem>.None : service.Board.ClosedTaskOf(sessionId));
    }

    static ControlReply Ended(TaskService service, string sessionId)
    {
        service.SessionEnded(sessionId);
        return Quiet;
    }

    // --- the session's own `task` forms ------------------------------------------------------------
    //
    //   task join <id> --session <sid>          take up an offered task (one of the user's notes becomes Claude's work)
    //   task new <title> --session <sid>        create the session's task, on the workspace of the VS Code window that
    //                                           shows the session's folder, or on none when no window does
    //   task status-line <text> --session <sid> the short line under the title ("waiting for CI on #398")
    //   task ask <question> --session <sid>     waiting on the user with the question, and asks for them as `attention`
    //   task done --session <sid>               the session's clear finish; only the user closes
    //   task comment <markdown> --session <sid> a comment on the session's own task, signed by the session
    //   task later <title> --session <sid>      a Backlog note on the caller's workspace, a follow-up for later
    //
    // Everything but join, new and later acts on the session's OWN task (TaskBoard.TaskOf), which is the spec's rule that
    // a session only settles tasks it is working on: there is no id to point at someone else's. A session with no open
    // task is refused, and nothing changes. `--session` is the hook's session_id, which the briefing hands the session.
    ControlReply SessionTask(TaskService service, string[] args, string session, string? from)
    {
        var folder = Folder(from);
        Result<TaskItem> Own() => service.Board.TaskOf(session)
            .ToResult($"Session {session} has no open task. Join one (task join <id> --session {session}) or create one (task new \"<title>\" --session {session}).");
        Maybe<Guid> Here() => CallerOf(folder).Workspace is { } w ? w.Id : Maybe<Guid>.None;
        ControlReply Changed(Result<TaskItem> task) =>
            task.Map(t => service.Board.Tasks.First(x => x.Id == t.Id)).Match(t => Success(Describe(t)), ControlReply.Fail);

        return args switch
        {
            ["join", var id] => Changed(service.Board.Find(id).Bind(t => service.Join(t.Id, service.NewSession(session, folder ?? "")).Map(() => t))),
            ["new", var title] => Changed(service.Create(title, Here(), service.NewSession(session, folder ?? ""))),
            ["status-line", var text] => Changed(Own().Bind(t => service.SetStatusLine(t.Id, text).Map(() => t))),
            ["ask", var question] => Changed(Own().Bind(t => service.Ask(t.Id, question).Map(() => t))
                .Tap(_ => Attention(folder))),
            ["done"] => Changed(Own().Bind(t => service.MarkDone(t.Id).Map(() => t))),
            ["comment", var markdown] => Changed(Own().Bind(t => service.Comment(t.Id, session, markdown).Map(() => t))),
            ["later", var title] => Changed(service.AddNote(title, Here())),
            _ => ControlReply.Fail($"Wrong arguments for a session's task form.\n{TaskUsage()}"),
        };
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
}
