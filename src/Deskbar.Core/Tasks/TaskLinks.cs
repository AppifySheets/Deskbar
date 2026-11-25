using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// A task with the tasks linked to it (task links, docs/superpowers/specs/2026-11-25-task-links-design.md): the task it
/// is a duplicate of (<see cref="DuplicateOf"/>, none when it is not one or the original no longer exists), the closed
/// tasks that duplicate it (<see cref="Duplicates"/>, latest first) and the tasks related to it either way
/// (<see cref="Related"/>, open ones first in the pile's order, then closed ones latest first). Made by TaskBoard.Links;
/// SessionBriefing.Links turns it into the text a session reads, and the tasks panel lists it in the open task's thread.
/// </summary>
public sealed record TaskLinks(TaskItem Task, Maybe<TaskItem> DuplicateOf, IReadOnlyList<TaskItem> Duplicates, IReadOnlyList<TaskItem> Related)
{
    /// <summary>No link at all: callers print nothing for such a task.</summary>
    public bool IsEmpty => DuplicateOf.HasNoValue && Duplicates.Count == 0 && Related.Count == 0;
}
