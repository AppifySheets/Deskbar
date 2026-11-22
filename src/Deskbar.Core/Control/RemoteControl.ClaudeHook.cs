using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Control;

// `claude-hook`: WHAT CLAUDE CODE'S HOOKS SAY TO DESKBAR. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md
// ("Claude sessions", "Who gets it"); plan Task 7; measurements in docs/superpowers/notes/2026-11-22-claude-hook-probes.md.
//
//   claude-hook [--entry <value>] [--attended <value>] [--from <folder>] --json <text>
//
// claude-hook.cmd (written by ClaudeSkill in the app) runs this on every hook event it is registered for, with the
// hook's JSON on the client's stdin; the client sends it on as --json (ControlArguments.WithHookInput). --entry and
// --attended are CLAUDE_CODE_ENTRYPOINT and CLAUDE_CODE_SESSION_ATTENDED as the script passed them; ClaudeHookEvent
// turns them into Headless. --from is the session's folder: CLAUDE_PROJECT_DIR, or the client's own folder when Claude
// Code did not set it (ControlArguments.WithCallerFolder).
//
// THE REPLY'S OUTPUT IS WHAT THE SESSION READS: Claude Code adds a SessionStart or UserPromptSubmit hook's stdout to the
// session's context. So it is plain text, not the JSON every other command answers, and it is EMPTY unless there is
// something for the session to know. Exit 0 always, except for JSON that is not a hook's (exit 1, nothing on stdout):
// the script turns every exit into 0 anyway, because a Stop hook that exits 2 tells Claude to keep going.
//
// The events:
//   Notification, Stop   ask for the user, exactly as the script's `attention` did before this verb existed: the
//                        session's workspace blinks unless the user is on it. Nothing comes back for the session.
//   SessionStart, UserPromptSubmit, SessionEnd
//                        registered only while sessions keep tasks (AppState.ClaudeTasksInEffect, ClaudeHooks in the
//                        app). Answered with nothing for now; Task 8 joins or creates the session's task here.
public sealed partial class RemoteControl
{
    ControlReply ClaudeHook(IReadOnlyList<string> rest) =>
        ControlArguments.ParseClaudeHook(rest)
            .Bind(request => ClaudeHookEvent.Parse(request.Json!, request.Entrypoint, request.Attended).Map(hook => (request, hook)))
            .Match(found => Answer(found.hook, found.request.From), ControlReply.Fail);

    ControlReply Answer(ClaudeHookEvent hook, string? from) => hook.Event switch
    {
        // The reply of `attention` is JSON for a person running it by hand; a hook's session must not read it.
        "Notification" or "Stop" => Quietly(Attention(NamedFolder(from))),
        _ => Quiet,
    };

    static readonly ControlReply Quiet = new(ControlReply.Ok, "");

    // Whatever the ask answered, the session hears nothing. A refusal from RequestAttention is the app's concern (it
    // means a COM call failed), not the session's, and the hook would print it into the conversation for nothing.
    static ControlReply Quietly(ControlReply _) => Quiet;

    // An empty --from (cmd passes an unset CLAUDE_PROJECT_DIR as "") is no folder at all.
    static string? NamedFolder(string? from) => string.IsNullOrWhiteSpace(from) ? null : from;

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
