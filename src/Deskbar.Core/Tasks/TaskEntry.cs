using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// One row of the task list. Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("The model").

/// <summary>Whether a row is a task or a workspace asking for attention (an "ask", not stored as a task).</summary>
public enum TaskEntryKind { Ask, Task }

/// <summary>
/// A row for the bar and the lists. For an Ask, Task is none, WorkspaceId is the asking workspace and State is
/// Due, so an ask sorts and colours like a timer that has just come due.
/// </summary>
public sealed record TaskEntry(TaskEntryKind Kind, Maybe<TaskItem> Task, Maybe<Guid> WorkspaceId, TaskState State);
