using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// The task data model. Design and reasoning: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md,
// section "The model". Everything here is an immutable record; TaskBoard holds the rules that change them.

/// <summary>What a task looks like right now. Derived from the fields and the clock, never stored.</summary>
public enum TaskState { Running, Due, Open, Done }

/// <summary>How a timer ended, kept in a task's history for later totals.</summary>
public enum TimerEnd { CameBack, Done, Cleared, Replaced }

/// <summary>A timer that is running or has run out and not yet been answered.</summary>
public sealed record TaskTimer(DateTimeOffset Started, TimeSpan Length)
{
    /// <summary>When the timer runs out. At this instant the task is already Due.</summary>
    public DateTimeOffset Ends => Started + Length;
}

/// <summary>A timer that has ended, with when and how.</summary>
public sealed record TimerRecord(DateTimeOffset Started, TimeSpan Length, DateTimeOffset Ended, TimerEnd How);

/// <summary>
/// One task. <see cref="WorkspaceId"/> is none for a free task. <see cref="UpdatedAt"/> moves on every change
/// so a later sync can tell which copy is newer. <see cref="History"/> is every timer the task has finished.
/// </summary>
public sealed record TaskItem(
    Guid Id,
    string Title,
    Maybe<Guid> WorkspaceId,
    DateTimeOffset Created,
    DateTimeOffset UpdatedAt,
    Maybe<TaskTimer> Timer,
    Maybe<DateTimeOffset> DoneAt,
    IReadOnlyList<TimerRecord> History)
{
    /// <summary>Done wins; then a timer whose end has come is Due, a timer still ahead is Running, else Open.</summary>
    public TaskState StateAt(DateTimeOffset now) =>
        DoneAt.HasValue ? TaskState.Done
        : Timer.HasNoValue ? TaskState.Open
        : Timer.Value.Ends <= now ? TaskState.Due
        : TaskState.Running;

    /// <summary>A live task is one that is not done: it belongs on the lists and can hold a timer.</summary>
    public bool IsLive => DoneAt.HasNoValue;
}
