using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Control;

// EVERY DIALOG A SESSION WAITS ON IS ITS TASK'S QUESTION (task b310a649; design
// docs/superpowers/specs/2026-11-27-claude-session-prompts-design.md; the dialog types and their texts are in
// SessionPrompt's header). Four roads in, all through `claude-hook`:
//
//   Notification     six seconds after a dialog opens, Claude Code's text and type for it. A question type puts the
//                    question on the session's open task (PromptAsked); elicitation_response settles it.
//   PermissionRequest, Elicitation
//                    fired the moment a dialog opens: what it is about (a tool and its command, a plan, an MCP server's
//                    request) is noted (PromptDetailNoted), so the question six seconds later can name it. Silent: a
//                    PermissionRequest or Elicitation hook that prints nothing leaves Claude Code's own dialog to decide.
//                    `claude-permission` notes the same detail while the Allow / Deny card is on (Registered).
//   PreToolUse       AskUserQuestion's own question (DialogAsked) is placed the same way, with the same watch.
//   Stop, the user's prompt, SessionEnd
//                    the session went on, so whatever dialog question it left is settled (PromptsSettledBy).
//
// And one road out that is not a hook: SettlePrompts, which the app calls every five seconds, settles a question once the
// session's transcript has moved past it (SessionPrompts.Moved, TranscriptMark): the dialog was answered, refused or
// escaped in the session, which writes the conversation's next line.
//
// THE RULES ARE ASKUSERQUESTION'S (task 493b7ba9, DialogAsked): a task already Waiting on you keeps its question (a
// `task ask` about a pull request to merge must not be replaced and then settled unseen), unless that question is the
// session's own earlier dialog question; a Done task stays Done; settling touches a task only while it still shows the
// very question that was placed, so a question the session asked since is the session's to settle.
public sealed partial class RemoteControl
{
    // The dialogs sessions wait on: details noted and questions placed. Its clock is the task service's, so tests can
    // move it; a copy without a task service never places anything, and UTC now is good enough to note with.
    readonly SessionPrompts prompts = new(() => tasks?.Now ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// The five-second settle (the app's timer, on the UI thread): every dialog question whose session's transcript has
    /// moved on since it was placed is settled, as `task answered` would settle it.
    /// </summary>
    public void SettlePrompts()
    {
        if (tasks is { } service) Settle(service, prompts.Moved());
    }

    // A Notification: blinked already (the reply passed in), then, for a question type, the question on the task.
    ControlReply PromptAsked(ClaudeHookEvent hook, string? from, ControlReply blinked)
    {
        if (tasks is not { } service || hook.Headless || !manager.State.ClaudeTasksInEffect) return blinked;
        if (IsManagerFolder(Folder(from) ?? Folder(hook.Folder))) return blinked;

        var type = hook.NotificationType.GetValueOrDefault("");
        // The Allow / Deny card already shows this session's tool permission on its task's page, with the command; a
        // second question for the same prompt would only say it again.
        if (type == "permission_prompt" && permissions?.Asks(hook.SessionId) == true) return blinked;

        var detail = prompts.Detail(hook.SessionId);
        SessionPrompt.Text(type, hook.Message.GetValueOrDefault(""), detail)
            .Bind(text => service.Board.TaskOf(hook.SessionId)
                .Where(t => t.Status is not TaskStatus.Done && (t.Status is not TaskStatus.WaitingOnYou || PlacedOn(t)))
                .Map(t => (Task: t, Text: text)))
            .Tap(found => PlaceQuestion(service, found.Task, found.Text, hook, detail.Bind(d => d.AgentId)));
        return blinked;
    }

    // Puts the question on the task as `task ask` would (Waiting on you, at the top, asked by the session) and remembers it
    // with where its transcript stands: the subagent's own when a subagent asked (TranscriptMark.For), none when that file
    // is not there, so then only the session's events settle it (a question that stays too long, never one that goes
    // too soon). The text stored is the task's own after the ask, so the later comparison is exact.
    void PlaceQuestion(TaskService service, TaskItem task, string text, ClaudeHookEvent hook, Maybe<string> detailAgent = default) =>
        service.Ask(task.Id, text, hook.SessionId)
            .Tap(() => prompts.Placed(hook.SessionId, task.Id,
                service.Board.TaskOf(hook.SessionId).Bind(t => t.Question).GetValueOrDefault(text),
                TranscriptMark.Take(TranscriptMark.For(hook.Transcript, hook.AgentId.Or(detailAgent)))));

    // Whether the task's waiting question is one this session's dialog put there and has not been settled yet.
    bool PlacedOn(TaskItem task) =>
        prompts.All.Any(p => p.Task == task.Id && task.Question.HasValue && p.Question == task.Question.Value);

    // A PermissionRequest or Elicitation hook from the task script: what the dialog that is opening is about.
    void PromptDetailNoted(string json, ClaudeHookEvent hook)
    {
        if (hook.Event == PermissionRequest.Event)
            PermissionRequest.Parse(json).Tap(NotePermission);
        else if (hook.Event == "Elicitation" && hook.McpServer.HasValue)
            prompts.Note(hook.SessionId, PromptDetail.Elicitation(hook.McpServer.Value, hook.Message.GetValueOrDefault(""),
                hook.AgentId, tasks?.Now ?? DateTimeOffset.UtcNow));
    }

    // Also called by `claude-permission` (the card's hook), so the detail is there whether or not the card is on.
    void NotePermission(PermissionRequest asked) =>
        prompts.Note(asked.SessionId, PromptDetail.Of(asked, tasks?.Now ?? DateTimeOffset.UtcNow));

    // The session went on: its turn ended (Stop, which cannot come while a dialog waits), the user typed to it (a prompt
    // Claude Code or Deskbar delivered does not count, SessionBriefing.FromTheUser: it may arrive while the dialog still
    // waits), it ended, or its MCP server's request was answered. Whatever dialog question it left goes.
    void PromptsSettledBy(ClaudeHookEvent hook)
    {
        if (tasks is not { } service) return;
        var wentOn = hook.Event switch
        {
            "Stop" or "SessionEnd" => true,
            "UserPromptSubmit" => hook.Prompt.Map(SessionBriefing.FromTheUser).GetValueOrDefault(true),
            "Notification" => SessionPrompt.Settles(hook.NotificationType.GetValueOrDefault("")),
            _ => false,
        };
        if (wentOn) Settle(service, prompts.Take(hook.SessionId));
    }

    // Each placement's task goes back to In progress, as `task answered` takes it, only while it still waits on that very
    // question.
    void Settle(TaskService service, IReadOnlyList<SessionPrompts.Placement> placements) =>
        placements.ToList().ForEach(p => service.Board.TaskOf(p.Session)
            .Where(t => t.Id == p.Task && t.Status == TaskStatus.WaitingOnYou && t.Question == p.Question)
            .Tap(t => Resolve(service, t, p.Session)));
}
