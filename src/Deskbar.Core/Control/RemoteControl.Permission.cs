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
// hook that prints nothing leaves Claude Code's own terminal dialog (the dialog is drawn while the hook waits, and stays
// when it gives up: the spike's run B5 and the probe for Q1 for a background session, the probe of 2026-11-27 for an
// interactive one, TranscriptMark). So every case that is not a question Deskbar can show on a page answers nothing, in
// the same order the plan lists them:
//
//   * no desk or no task service in this copy, the setting off (AppState.ClaudePermissionsInEffect, which needs the
//     task hooks too), or the Inbox look off (TasksPanelState.Inbox: no page to put the card on);
//   * a headless run (`claude -p`, the SDK: nobody to ask, and -p's own rule applies). A session in a terminal or in
//     VS Code IS shown since task 6306d261 (Petre, 2026-11-27: "Yes, all sessions"). That overturns the PM's ruling Q3,
//     which kept the card to background sessions because the others have their own dialog in front of the user; the
//     point is not having to leave the chat. A terminal session's dialog stays drawn beside the card the whole time
//     (measured 2026-11-27). For VS Code that is the hooks reference's word, not a measurement: a call that reaches the
//     SDK's canUseTool runs the hooks "alongside your host, and whichever decides first applies" (manual step PA3). As
//     `claude agents` lists only background sessions, such a card is watched through the session's transcript instead
//     (TranscriptMark, PermissionDesk.Settle);
//   * an AskUserQuestion or an ExitPlanMode its card cannot draw (ruling Q8 kept both off the Allow / Deny card: their
//     answer needs the user's choice). A well-formed AskUserQuestion goes on a card of its own since task c047d0a0
//     (Petre: "model this in the task so i don't have to open claude code"), which sends the user's picks back
//     (DialogAsk); a plan goes on the plan card since task 8dbbaa0b (PlanAsk); the reply says "dialog" for both;
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
        ControlArguments.ParseClaudeHook(rest).Match(request => request.Fetch ? Fetched(request) : Asked(request), _ => Quiet);

    // A DIALOG'S SCRIPT FETCHING ITS ANSWER (task c047d0a0): `claude-permission --fetch --slot <name>`, the code it was
    // handed on stdin. The reply is the answer line the user's Send left for that slot (PermissionDesk.AnswerDialog), once,
    // and the script lets it straight through as the hook's output; anything else is nothing, and the terminal asks. The
    // client checks this verb's server is Deskbar's own executable (PipeTrust), which is why the line comes this way and
    // not as a file in the slot.
    ControlReply Fetched(ClaudeHookRequest request) =>
        permissions is { } desk && request.Slot is { Length: > 0 } slot
            ? desk.Fetch(slot, request.Json!.Trim()).Match(line => Said(line + "\r\n"), _ => Quiet)
            : Quiet;

    ControlReply Asked(ClaudeHookRequest request) =>
        ClaudeHookEvent.Parse(request.Json!, request.Entrypoint, request.Attended, request.Job)
            .Bind(hook => PermissionRequest.Parse(request.Json!).Map(asked => (Hook: hook, Asked: asked)))
            .Map(found => Registered(request, found.Hook, found.Asked))
            .GetValueOrDefault(Quiet);

    ControlReply Registered(ClaudeHookRequest request, ClaudeHookEvent hook, PermissionRequest asked)
    {
        // What the dialog is about is noted whatever happens to the card: the task question the dialog's Notification makes
        // six seconds later names the tool and command from it (task b310a649, RemoteControl.SessionPrompts.cs).
        NotePermission(asked, hook.Transcript);
        if (permissions is not { } desk || tasks is not { } service) return Quiet;
        // Housekeeping first, so a question held past its time is not counted as on show.
        desk.Expire();
        // A newer question from the session settles its older ones (the review's L5: answered in the terminal, or given
        // up), whether or not this one goes on a card: an AskUserQuestion dialog or a refused question means the session
        // no longer waits on the old one either. Their slots go, so a click can never reach a hook that stopped waiting.
        desk.Forget(asked.SessionId);
        return manager.State is { ClaudePermissionsInEffect: true, TasksInbox: true }
            && hook is { Headless: false }
            // AskUserQuestion and ExitPlanMode each go on a card of their own when the card can draw them (DialogAsk,
            // task c047d0a0; PlanAsk, task 8dbbaa0b); one it cannot draw stays in the terminal.
            && (!asked.IsDialog || DialogAsk.Of(asked).HasValue || PlanAsk.Of(asked).HasValue)
            && request.Slot is { Length: > 0 } slot
            && !IsManagerFolder(Folder(request.From) ?? Folder(hook.Folder))
            && service.Board.TaskOf(asked.SessionId).HasValue
                // A background session is watched through `claude agents` (listed); any other through where its
                // transcript stands now, which grows only once the question is answered (TranscriptMark).
                ? desk.Register(slot, asked, listed: hook.Background,
                        mark: hook.Background ? null : TranscriptMark.Take(TranscriptMark.For(hook.Transcript, asked.AgentId)).GetValueOrDefault())
                    // A dialog's slot is told so ("<code> dialog"): its script then fetches and prints the line Deskbar
                    // holds for the dialog word and nothing else (PermissionAnswer.LineFor).
                    .Match(code => Said(asked.IsDialog ? $"{code} {PermissionAnswer.DialogKind}\r\n" : code + "\r\n"), _ => Quiet)
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
        inbox = manager.State.TasksInbox,
    };
}
