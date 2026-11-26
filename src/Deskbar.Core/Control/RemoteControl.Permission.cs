using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Control;

// `claude-permission`: A PERMISSION HOOK REGISTERING ITS QUESTION (Allow / Deny from a task's page; plan
// notes/tasks-inbox-permission-plan.md in the PM's workspace, built on the live spike of 2026-11-26).
//
//   claude-permission [--entry <value>] [--attended <value>] [--job <value>] [--from <folder>] --slot <req-n-n> --json <text>
//
// The hook script (claude-permission-hook.cmd, written by the app) makes its slot folder, calls this ONCE with the
// hook's JSON on stdin, and reads one line back: the code its answer must carry (PermissionDesk). Then it polls its slot
// and never calls Deskbar again, so the one-client pipe is held for a moment only.
//
// THE REPLY IS THE CODE OR NOTHING. Nothing is the safe answer: the script then prints nothing, and a PermissionRequest
// hook that prints nothing leaves Claude Code's own terminal dialog (in a background session the dialog is drawn while
// the hook waits, and stays when it gives up: the spike's run B5 and the probe for Q1). So every case that is not a
// question Deskbar can show on a page answers nothing, in the same order the plan lists them:
//
//   * no desk or no task service in this copy, the setting off (AppState.ClaudePermissionsInEffect, which needs the
//     task hooks too), or the Inbox look off (TasksPanelState.Inbox: no page to put the card on);
//   * a session that is not a BACKGROUND one (the PM's ruling Q3: a terminal or VS Code session has its own dialog in
//     front of the user), or a headless run (`claude -p`, the SDK: nobody to ask, and -p's own rule applies);
//   * Claude Code's own dialogs, AskUserQuestion and ExitPlanMode (ruling Q8: their answer needs the user's choice);
//   * the manager session (it has no task), a session with no open task (unknown, or its task closed);
//   * JSON that is not a PermissionRequest, a missing or refused slot (PermissionDesk.Register).
//
// THERE IS NO VERB THAT ANSWERS. An answer is a click in Deskbar's own window and nothing else, a deliberate exception
// to "every menu command has a remote twin": a twin on the pipe would let any process of the user, another Claude
// session included, approve what Claude runs. `claude-permissions on|off` is the setting's twin, which is allowed.
//
// NOTHING OF THE QUESTION IS TRACED: the verb is a hook verb (ControlArguments.IsClaudeHook), so the app's `ctl` trace
// line holds the event, the session and the JSON's length only (ControlArguments.ForTrace).
public sealed partial class RemoteControl
{
    ControlReply ClaudePermission(IReadOnlyList<string> rest) =>
        ControlArguments.ParseClaudeHook(rest)
            .Bind(request => ClaudeHookEvent.Parse(request.Json!, request.Entrypoint, request.Attended, request.Job)
                .Bind(hook => PermissionRequest.Parse(request.Json!).Map(asked => (Request: request, Hook: hook, Asked: asked))))
            .Map(found => Registered(found.Request, found.Hook, found.Asked))
            .GetValueOrDefault(Quiet);

    ControlReply Registered(ClaudeHookRequest request, ClaudeHookEvent hook, PermissionRequest asked)
    {
        if (permissions is not { } desk || tasks is not { } service) return Quiet;
        // Housekeeping first, so a question held past its time is not counted as on show.
        desk.Expire();
        // A newer question from the session settles its older ones (the review's L5: answered in the terminal, or given
        // up), whether or not this one goes on a card: an AskUserQuestion dialog or a refused question means the session
        // no longer waits on the old one either. Their slots go, so a click can never reach a hook that stopped waiting.
        desk.Forget(asked.SessionId);
        return manager.State is { ClaudePermissionsInEffect: true, TasksPanel.Inbox: true }
            && hook is { Background: true, Headless: false }
            && !asked.IsDialog
            && request.Slot is { Length: > 0 } slot
            && !IsManagerFolder(Folder(request.From) ?? Folder(hook.Folder))
            && service.Board.TaskOf(asked.SessionId).HasValue
                ? desk.Register(slot, asked).Match(code => Said(code + "\r\n"), _ => Quiet)
                : Quiet;
    }

    // A SESSION THAT MOVED ON SETTLED ITS QUESTIONS. Its turn ended (Stop, and a background session's SessionEnd at the end
    // of every turn), a new prompt started one (UserPromptSubmit), or it ended: no permission prompt of it can still be
    // waiting, so a question still on its page was answered in the terminal. It is taken off and its slot deleted, which
    // ends the hook script without a word (PermissionDesk). Notification is NOT one of these: Claude Code sends the
    // permission prompt's own Notification about six seconds into the wait (the hooks reference).
    void QuestionsSettledBy(ClaudeHookEvent hook)
    {
        if (hook.Event is "Stop" or "UserPromptSubmit" or "SessionEnd") permissions?.Forget(hook.SessionId);
    }

    // The setting (AppState.ClaudePermissions), the remote twin of Manage's box. Like claude-tasks, the reply says
    // whether it is in effect, since it is off while sessions do not keep tasks, and whether the Inbox look is on, since
    // only that look draws the card.
    ControlReply ClaudePermissions(string onOff) =>
        onOff.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" => Reply(manager.SetClaudePermissions(true), DescribeClaudePermissions),
            "off" or "false" or "no" => Reply(manager.SetClaudePermissions(false), DescribeClaudePermissions),
            _ => ControlReply.Fail(ControlUsage.LineFor("claude-permissions")),
        };

    object DescribeClaudePermissions() => new
    {
        claudePermissions = manager.State.ClaudePermissions ? "on" : "off",
        inEffect = manager.State.ClaudePermissionsInEffect,
        inbox = manager.State.TasksPanel?.Inbox ?? false,
    };
}
