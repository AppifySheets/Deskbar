namespace Deskbar.Core.Tasks;

// WHO A TASK IS ON (Ruling 57). Petre: "tasks that don't need my attention are assigned to claude; others are assigned
// to me", and "in the future i'll have my colleagues in this task management thing". So the assignee is a PERSON, not a
// yes/no about Claude: a kind plus an id and a display name, and a colleague is one more kind. Only Me and Claude exist
// in the UI today; a colleague is storable and comparable now so nothing needs reshaping when they arrive.
//
// The rules that move it live in TaskBoard (created by Petre: Me; a session's task: Claude while it works; `ask`: Me;
// `answered`: Claude; `done`: Me; and Petre's own Assign from the tasks window). The bar lists only tasks assigned to Me.

/// <summary>What kind of person an assignee is. Me is Petre, the owner of this Deskbar.</summary>
public enum AssigneeKind { Me, Claude, Colleague }

/// <summary>
/// A task's assignee. Equality is by kind, id and name, so a colleague renamed is a different value until the store
/// says otherwise; Me and Claude are fixed.
/// </summary>
public sealed record Assignee(AssigneeKind Kind, string Id, string Name)
{
    /// <summary>Petre himself.</summary>
    public static readonly Assignee Me = new(AssigneeKind.Me, "me", "Me");

    /// <summary>Claude Code sessions, as one assignee: tasks that do not need Petre's attention.</summary>
    public static readonly Assignee Claude = new(AssigneeKind.Claude, "claude", "Claude");

    /// <summary>A colleague, by a stable id and the name shown. Not offered by any menu yet.</summary>
    public static Assignee Colleague(string id, string name) => new(AssigneeKind.Colleague, id, name);

    public bool IsMe => Kind == AssigneeKind.Me;

    public bool IsClaude => Kind == AssigneeKind.Claude;

    /// <summary>
    /// The assignee a file's fields name; none when they name nothing usable. A kind this build does not know (a newer
    /// build's) reads as none, so the caller derives one rather than failing the file.
    /// </summary>
    public static CSharpFunctionalExtensions.Maybe<Assignee> From(string? kind, string? id, string? name) =>
        (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "me" => Me,
            "claude" => Claude,
            "colleague" when !string.IsNullOrWhiteSpace(id) => Colleague(id.Trim(), string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim()),
            _ => CSharpFunctionalExtensions.Maybe<Assignee>.None,
        };

    /// <summary>
    /// The assignee for a task written before the field existed: a Claude task (one with sessions) that is not waiting
    /// on Petre, not Done and not Closed is Claude's; everything else is his.
    /// </summary>
    public static Assignee DerivedFor(bool hasSessions, TaskStatus status) =>
        hasSessions && status is TaskStatus.InProgress or TaskStatus.Backlog ? Claude : Me;
}
