using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// ALLOW / DENY FROM A TASK'S PAGE, THE BOARD'S HALF. The board hands the window two things and decides nothing else:
// the questions on show for a task (PermissionDesk.OfTask), which the page draws as cards and the Inbox's split reads for
// the "Asks permission" mark and section, and the click (PermissionDesk.Answer), the only way an answer is ever written.
// The desk raises Changed when a question comes or goes, and the board then redraws, so a card appears and leaves on its
// own (the page's redraw signature carries the questions' slots, TasksWindow.PermissionSignature).
public sealed partial class TasksBoard
{
    PermissionDesk? permissions;

    /// <summary>The app's permission desk (App.PermissionDesk); none in a copy without it, which shows no questions.</summary>
    internal PermissionDesk? Permissions
    {
        get => permissions;
        set
        {
            if (permissions is not null) permissions.Changed -= Refresh;
            permissions = value;
            if (value is not null) value.Changed += Refresh;
        }
    }

    // Whether a session of the task has a TOOL permission question on show: the Inbox's mark and section (InboxTabs.Split).
    // A multiple-choice dialog is not one (task c047d0a0), nor is a plan to approve (task 8dbbaa0b): its task already
    // waits on the dialog's own question (DialogQuestion, SessionPrompt), so it stays in Waiting on you rather than under
    // "Asks permission, a yes or a no".
    bool AsksPermission(TaskItem task) => permissions is { } desk && desk.OfTask(tasks.Board, task.Id).Any(p => !p.Request.IsDialog);

    IReadOnlyList<PermissionDesk.Pending> PermissionsOf(Guid id) => permissions?.OfTask(tasks.Board, id) ?? [];

    Result AnswerPermission(string slot, PermissionDecision decision) =>
        permissions?.Answer(slot, decision) ?? Result.Failure("This question has ended. Answer it in the terminal.");

    // A card's "Yes, and ..." built from one of Claude Code's suggestions (task bd55bd54, PermissionChoices).
    Result AnswerSuggestion(string slot, int index) =>
        permissions?.AnswerSuggestion(slot, index) ?? Result.Failure("This question has ended. Answer it in the terminal.");

    Result AnswerDialog(string slot, IReadOnlyDictionary<string, string> answers) =>
        permissions?.AnswerDialog(slot, answers) ?? Result.Failure("This question has ended. Answer it in the terminal.");

    // A plan card's click (task 8dbbaa0b). What was decided goes into the task's thread as Deskbar's line, so the chat
    // keeps it after the card has gone: nothing else would say a plan was approved from here, or what the note was.
    Result AnswerPlan(string slot, PlanDecision decision, string note)
    {
        if (permissions is not { } desk) return Result.Failure("This question has ended. Answer it in the terminal.");
        var task = desk.All.FirstOrDefault(p => p.Slot == slot) is { } pending ? tasks.Board.TaskOf(pending.Request.SessionId) : default;
        return desk.AnswerPlan(slot, decision, note)
            .Tap(() => task.Tap(t => tasks.Note(t.Id, PlanAsk.ThreadLine(decision, note))));
    }
}

internal sealed partial record TasksWindowActions
{
    /// <summary>The questions on show for a task, the oldest first (TasksBoard.PermissionsOf). None: no cards.</summary>
    public Func<Guid, IReadOnlyList<PermissionDesk.Pending>>? PermissionsOf { get; init; }

    /// <summary>A card's click (TasksBoard.AnswerPermission): the one way an answer is written. No pipe command does it.</summary>
    public Func<string, PermissionDecision, Result>? AnswerPermission { get; init; }

    /// <summary>
    /// A card's click on a "Yes, and ..." built from Claude Code's suggestion at that index (TasksBoard.AnswerSuggestion,
    /// task bd55bd54). No pipe command does it.
    /// </summary>
    public Func<string, int, Result>? AnswerSuggestion { get; init; }

    /// <summary>A dialog card's Send (TasksBoard.AnswerDialog, task c047d0a0): the picks by question text. No pipe command does it.</summary>
    public Func<string, IReadOnlyDictionary<string, string>, Result>? AnswerDialog { get; init; }

    /// <summary>A plan card's click (TasksBoard.AnswerPlan, task 8dbbaa0b): the decision and the note. No pipe command does it.</summary>
    public Func<string, PlanDecision, string, Result>? AnswerPlan { get; init; }
}
