using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// A task with the tasks around it (subtasks spec, docs/superpowers/specs/2026-11-24-subtasks-design.md): its
/// <see cref="Parent"/> when it is a subtask (none when the parent no longer exists), its <see cref="Siblings"/> (the
/// parent's other subtasks) and its own <see cref="Subtasks"/>. Siblings and subtasks are listed open ones first in the
/// pile's order, then closed ones latest closed first. Made by TaskBoard.Family; SessionBriefing.Family turns it into the
/// text a session reads. One level deep, so a task has a parent or subtasks, and Siblings is empty for a parent.
/// </summary>
/// <summary>
/// What a line's "Subtask of" menu offers (TaskViews.ParentChoices): whether the task may not be put under another (it has
/// subtasks of its own), the parent it has now, and the tasks it could go under.
/// </summary>
public sealed record ParentChoices(bool Blocked, Maybe<Guid> Current, IReadOnlyList<(Guid Id, string Title)> Candidates);

public sealed record TaskFamily(TaskItem Task, Maybe<TaskItem> Parent, IReadOnlyList<TaskItem> Siblings, IReadOnlyList<TaskItem> Subtasks);
