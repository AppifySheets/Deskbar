using Deskbar.Core.Tasks;

namespace Deskbar.App;

// ALLOW / DENY FROM A TASK'S PAGE, ON THE BAR. In the Inbox look a task line whose session waits on a permission
// question wears the panel's mark for it (the shield with a keyhole, InboxMarks) and, as its quiet second line, what is
// asked: "Asks permission: Bash". The answer itself is only ever given on the task's page or in the terminal; the bar
// shows, it does not answer. It repaints when a question comes or goes (PermissionDesk.Changed): the list's shape carries
// the questions (PermissionShape), so the paint is not skipped.
public partial class FloatingBar
{
    PermissionDesk? permissionDesk;

    /// <summary>The app's permission desk (App.PermissionDesk); none: no line ever shows a permission question.</summary>
    internal PermissionDesk? Permissions
    {
        get => permissionDesk;
        set
        {
            if (permissionDesk is not null) permissionDesk.Changed -= RepaintForPermissions;
            permissionDesk = value;
            if (value is not null) value.Changed += RepaintForPermissions;
        }
    }

    void RepaintForPermissions() => PaintTaskList();

    IReadOnlyList<PermissionDesk.Pending> BarPermissionsOf(Guid id) =>
        permissionDesk is { } desk && taskService is { } service ? desk.OfTask(service.Board, id) : [];

    bool AsksPermission(TaskItem task) => BarPermissionsOf(task.Id).Count > 0;

    // The quiet second line of a line whose session waits on a permission question, else null.
    string? PermissionWords(Guid id) =>
        BarPermissionsOf(id) is [var first, ..] ? $"Asks permission: {PermissionDisplay.Visible(first.Request.Tool)}" : null;

    // The second line a line needing the user shows: the permission question when there is one, else its detail.
    string BarDetail(TaskLine line, InboxMark mark) =>
        mark == InboxMark.AsksPermission ? PermissionWords(line.Id) ?? line.Detail : line.Detail;

    // Part of the list's shape: which tasks have a question on show.
    string PermissionShape() =>
        permissionDesk is { } desk ? string.Join(',', desk.All.Select(p => $"{p.Slot}:{p.Request.SessionId}")) : "";
}
