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

    // Whether a session of the task has a question on show: the Inbox's mark and section (InboxTabs.Split).
    bool AsksPermission(TaskItem task) => permissions is { } desk && desk.OfTask(tasks.Board, task.Id).Count > 0;

    IReadOnlyList<PermissionDesk.Pending> PermissionsOf(Guid id) => permissions?.OfTask(tasks.Board, id) ?? [];

    Result AnswerPermission(string slot, PermissionDecision decision) =>
        permissions?.Answer(slot, decision) ?? Result.Failure("This question has ended. Answer it in the terminal.");
}

internal sealed partial record TasksWindowActions
{
    /// <summary>The questions on show for a task, the oldest first (TasksBoard.PermissionsOf). None: no cards.</summary>
    public Func<Guid, IReadOnlyList<PermissionDesk.Pending>>? PermissionsOf { get; init; }

    /// <summary>A card's click (TasksBoard.AnswerPermission): the one way an answer is written. No pipe command does it.</summary>
    public Func<string, PermissionDecision, Result>? AnswerPermission { get; init; }
}
