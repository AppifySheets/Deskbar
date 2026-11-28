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
// THE RULES ARE ASKUSERQUESTION'S (task 493b7ba9, DialogAsked), one predicate for both (TakesDialogQuestion): a task
// already Waiting on you keeps its question (a `task ask` about a pull request to merge must not be replaced and then
// settled unseen), unless that question is the session's own earlier dialog question; a Done task stays Done. Two more
// from the review of this branch: a task whose question Deskbar SET ASIDE while Claude works on Petre's reply (task
// 8bd18246) is left alone, since asking would drop that question and the Stop nudge that brings it back; and so is a
// task Petre took (Assignee Me), since settling would hand it back to Claude (`task answered` does). Settling touches a
// task only while it still shows the very question that was placed, so a question the session asked since is the
// session's to settle.
//
// KNOWN LIMIT: while the Allow / Deny card shows a tool permission, its Notification places nothing (the card is the
// question). If the card is held past PermissionDesk.HeldFor (about ten minutes) and taken off while the terminal still
// waits, nothing on the task says so: the Notification fired once, six seconds in. The workspace blinked then.
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
        if (tasks is not { } service) return;
        Settle(service, prompts.Moved());
        CardsGone(service);
    }

    /// <summary>How long a page-led dialog question waits for its card to register before the terminal's lead is put back.</summary>
    public static readonly TimeSpan CardGrace = TimeSpan.FromSeconds(30);

    // A PAGE-LED QUESTION WHOSE CARD IS GONE (task 28fd8550, DialogQuestion's header). The card left the page while the
    // dialog still waits in the terminal: held past PermissionDesk.HeldFor, handed back with "Answer in the terminal", or
    // it never registered (the hook missing, a refused slot). The question then says the terminal's lead instead, in place
    // (TaskService.RewordQuestion: no new bubble, no move to the top), and its placement follows, so the settle rules,
    // which compare the exact text, still find it. CardGrace covers the moment between PreToolUse, which places the
    // question, and the PermissionRequest that registers the card. A dialog answered on the card is settled by its
    // PostToolUse a moment after the card goes; a tick in between rewords it first, which the settle also accepts
    // (IsDialogQuestion).
    //
    // Two exceptions found in the branch's review. A dialog just answered ON the card (PermissionDesk.AnsweredOnCardSince)
    // is left alone for CardGrace: the card leaves the desk on Send, seconds before PostToolUse settles the question. And a
    // page-led question with NO placement is an orphan of a Deskbar restart (placements and cards live in memory only, and
    // PermissionDesk.Sweep deletes every slot at start, so the terminal keeps the dialog): it is reworded too, once no
    // session of its task has a card. Only a dialog question can start with PageLead, since a session's `task ask` loses a
    // copied lead (DialogQuestion.WithoutLead).
    void CardsGone(TaskService service)
    {
        PlacedCardsGone(service);
        OrphanedCardsGone(service);
    }

    // Whether the session's dialog still has its card, or was answered on it a moment ago.
    bool HasCard(TaskService service, string session) =>
        permissions is { } desk
        && (desk.Of(session).Any(q => q.Request.IsAskUserQuestion) || desk.AnsweredOnCardSince(session, service.Now - CardGrace));

    void OrphanedCardsGone(TaskService service) =>
        service.Board.Tasks
            .Where(t => t is { IsOpen: true, Status: TaskStatus.WaitingOnYou, Question.HasValue: true })
            .Where(t => prompts.All.All(p => p.Task != t.Id))
            .Where(t => !t.Sessions.Any(s => HasCard(service, s.Id)))
            .Select(t => (Task: t, Terminal: DialogQuestion.ToTerminal(t.Question.Value)))
            .Where(r => r.Terminal.HasValue)
            .ToList()
            .ForEach(r => service.RewordQuestion(r.Task.Id, r.Task.Question.Value, r.Terminal.Value));

    void PlacedCardsGone(TaskService service) =>
        prompts.All
            .Where(p => service.Now - p.At >= CardGrace)
            // Only while the task still shows the very question placed (the settle rules' condition too).
            .Where(p => service.Board.Tasks.Any(t => t.Id == p.Task && t.Status == TaskStatus.WaitingOnYou && t.Question.HasValue && t.Question.Value == p.Question))
            .Where(p => !HasCard(service, p.Session))
            .Select(p => (Placement: p, Terminal: DialogQuestion.ToTerminal(p.Question)))
            .Where(r => r.Terminal.HasValue)
            .ToList()
            .ForEach(r => service.RewordQuestion(r.Placement.Task, r.Placement.Question, r.Terminal.Value)
                .Tap(() => prompts.Reworded(r.Placement, r.Terminal.Value)));

    // A Notification: blinked already (the reply passed in), then, for a question type, the question on the task.
    ControlReply PromptAsked(ClaudeHookEvent hook, string? from, ControlReply blinked)
    {
        if (tasks is not { } service || hook.Headless || !manager.State.ClaudeTasksInEffect) return blinked;
        if (IsManagerFolder(Folder(from) ?? Folder(hook.Folder))) return blinked;

        var type = hook.NotificationType.GetValueOrDefault("");
        // The Allow / Deny card already shows this session's tool permission on its task's page, with the command; a
        // second question for the same prompt would only say it again. A plan on its card (task 8dbbaa0b) is not one of
        // those: approving a plan is a decision, so its task waits on the user with the plan's question, as it did
        // before the card, and the card is where it is answered.
        if (type == "permission_prompt" && permissions?.Of(hook.SessionId).Any(p => !p.Request.IsPlan) == true) return blinked;

        var detail = prompts.Detail(hook.SessionId);
        // Claude's multiple-choice dialog is a permission dialog in Claude Code's registry too ("Claude needs your
        // permission", measured in the 2.1.332 binary by the branch's review): its question is DialogAsked's, worded from
        // the dialog itself and settled by PostToolUse, so its Notification must not replace it (finding 1).
        if (detail.Map(d => d.Tool == DialogQuestion.Tool).GetValueOrDefault(false)) return blinked;

        var message = hook.Message.GetValueOrDefault("");
        // The detail's subagent picks the transcript to watch only when the detail named the question.
        var agent = SessionPrompt.Named(type, message, detail).HasValue ? detail.Bind(d => d.AgentId) : Maybe<string>.None;
        // A plan whose card is on the task's page is answered there, and its question says so (SessionPrompt.PlanOnCardLead).
        var planOnCard = permissions?.Of(hook.SessionId).Any(q => q.Request.IsPlan) == true;
        SessionPrompt.Text(type, message, detail, planOnCard)
            .Bind(text => service.Board.TaskOf(hook.SessionId).Where(TakesDialogQuestion).Map(t => (Task: t, Text: text)))
            .Tap(found => PlaceQuestion(service, found.Task, found.Text, hook, agent));
        return blinked;
    }

    // Whether a dialog's question may go on the task (the header's rules).
    bool TakesDialogQuestion(TaskItem task) =>
        task.Status is not TaskStatus.Done
        && task.QuestionSetAside.HasNoValue
        // The session's own earlier dialog question may be replaced whatever the ask did to the task's assignee.
        && (PlacedOn(task) || (task.Status is not TaskStatus.WaitingOnYou && task.Assignee.Kind == AssigneeKind.Claude));

    // Puts the question on the task as `task ask` would (Waiting on you, at the top, asked by the session) and remembers it
    // with where its transcript stands: the subagent's own when a subagent asked (TranscriptMark.For), none when that file
    // is not there, so then only the session's events settle it (a question that stays too long, never one that goes
    // too soon). The text stored is the task's own after the ask, so the later comparison is exact. AskUserQuestion's
    // PreToolUse fires before Claude Code writes the asking line, so its mark names the tool and that line does not
    // settle it (TranscriptMark's header, task 0978f3f4); a Notification's hook names no tool and its mark none.
    void PlaceQuestion(TaskService service, TaskItem task, string text, ClaudeHookEvent hook, Maybe<string> detailAgent = default) =>
        service.Ask(task.Id, text, hook.SessionId)
            .Tap(() => prompts.Placed(hook.SessionId, task.Id,
                service.Board.TaskOf(hook.SessionId).Bind(t => t.Question).GetValueOrDefault(text),
                TranscriptMark.Take(TranscriptMark.For(hook.Transcript, hook.AgentId.Or(detailAgent)),
                    hook.ToolName.Where(tool => tool == DialogQuestion.Tool))));

    // Whether the task's waiting question is one this session's dialog put there and has not been settled yet.
    bool PlacedOn(TaskItem task) =>
        prompts.All.Any(p => p.Task == task.Id && task.Question.HasValue && p.Question == task.Question.Value);

    // A PermissionRequest or Elicitation hook from the task script: what the dialog that is opening is about.
    void PromptDetailNoted(string json, ClaudeHookEvent hook)
    {
        if (hook.Event == PermissionRequest.Event)
            PermissionRequest.Parse(json).Tap(asked => NotePermission(asked, hook.Transcript));
        else if (hook.Event == "Elicitation" && hook.McpServer.HasValue)
            prompts.Note(hook.SessionId, PromptDetail.Elicitation(hook.McpServer.Value, hook.Message.GetValueOrDefault(""),
                hook.AgentId, tasks?.Now ?? DateTimeOffset.UtcNow), TranscriptMark.Take(TranscriptMark.For(hook.Transcript, hook.AgentId)));
    }

    // Also called by `claude-permission` (the card's hook), so the detail is there whether or not the card is on. The
    // mark is where the transcript the dialog is decided in stands now: once it moves, the detail is spent. A dialog's
    // mark names its tool (task 0978f3f4): its asking line landing late must not spend the detail, or the Notification
    // six seconds on would no longer know the dialog and would put a permission question over the dialog's own.
    void NotePermission(PermissionRequest asked, Maybe<string> transcript) =>
        prompts.Note(asked.SessionId, PromptDetail.Of(asked, tasks?.Now ?? DateTimeOffset.UtcNow),
            TranscriptMark.Take(TranscriptMark.For(transcript, asked.AgentId), asked.IsDialog ? Maybe.From(asked.Tool) : Maybe<string>.None));

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
