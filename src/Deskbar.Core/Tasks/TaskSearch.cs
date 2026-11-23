namespace Deskbar.Core.Tasks;

// The tasks panel's search box, decided here so the panel only draws it (spec
// docs/superpowers/specs/2026-11-23-tasks-panel-design.md). Case-insensitive, and every typed word must match, anywhere
// in the title, the status line or the question: "store tier" finds "Store submission" asking "Which price tier?".
// Not persisted: a fresh open starts with None, like every other filter. A value record, so the panel's redraw
// signature can compare two searches by their text.
public sealed record TaskSearch(string Text)
{
    public static readonly TaskSearch None = new("");

    // The typed words, split on any whitespace; empty entries dropped, so "  a   b " is two words. Computed, not a
    // field: a record's equality compares its fields, and an array field would compare by reference, so two searches
    // with the same text would no longer be equal.
    string[] words => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Nothing typed (or only spaces): the search admits every task.</summary>
    public bool IsBlank => words.Length == 0;

    /// <summary>Whether every typed word occurs, ignoring case, in the task's title, status line or question.</summary>
    public bool Admits(TaskItem task) =>
        words.All(word => new[] { task.Title, task.StatusLine, task.Question.GetValueOrDefault("") }
            .Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The board with only the tasks this search admits; a blank search returns the same board.</summary>
    public TaskBoard Narrow(TaskBoard board) =>
        IsBlank ? board : new TaskBoard(board.Tasks.Where(Admits).ToArray());
}

// The panel's "Claude working" switch: only tasks with at least one live session (TaskItem.HasLiveSession). Off returns
// the same board, as TaskGoalFilter.Narrow does for All.
public static class ClaudeWorking
{
    public static TaskBoard Narrow(TaskBoard board, bool on) =>
        on ? new TaskBoard(board.Tasks.Where(t => t.HasLiveSession).ToArray()) : board;
}
