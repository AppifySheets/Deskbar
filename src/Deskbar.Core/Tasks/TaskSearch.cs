using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// The tasks panel's search box, decided here so the panel only draws it (spec
// docs/superpowers/specs/2026-11-23-tasks-panel-design.md). Case-insensitive, and every typed word must match, anywhere
// in the title, the status line or the question: "store tier" finds "Store submission" asking "Which price tier?".
// Not persisted: a fresh open starts with None, like every other filter. A value record, so the panel's redraw
// signature can compare two searches by their text.
//
// SEARCH BY ID (task 6e4dc116, Petre: "searching for tasks by their id, like 173ef268 should give me that task, from all
// sections, including closed"). A task's id is what every Claude session, comment and status line names it by (the
// first eight characters, "173ef268"), so typing it is how he finds the task one of those talks about. Text that reads
// as the start of an id (IdPrefix: hex digits, dashes and braces allowed, at least MinIdDigits of them) matches every
// task whose id starts with it, and Found hands the panel those tasks as a LOOKUP: shown in whatever section they are
// in, closed and snoozed included, past every other filter (status, workspace, goal, assignee, Claude working). A
// filter that hid the very task asked for by id would make the box look broken. When no task's id starts with the text
// it is an ordinary word search again, so "decade" or "beef" still find titles.
public sealed record TaskSearch(string Text)
{
    public static readonly TaskSearch None = new("");

    // The fewest hex digits read as an id. Four keeps a short hex word ("add", "bad") a word, and a four-digit prefix is
    // already shared by only one task in 65536, so it is unambiguous on any real board. Petre types the eight-digit form.
    const int MinIdDigits = 4;

    // The typed words, split on any whitespace; empty entries dropped, so "  a   b " is two words. Computed, not a
    // field: a record's equality compares its fields, and an array field would compare by reference, so two searches
    // with the same text would no longer be equal.
    string[] words => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Nothing typed (or only spaces): the search admits every task.</summary>
    public bool IsBlank => words.Length == 0;

    /// <summary>
    /// Whether every typed word occurs, ignoring case, in the task's title, status line or question, or the text is the
    /// start of the task's id (see "Search by id" above).
    /// </summary>
    public bool Admits(TaskItem task) =>
        MatchesId(task)
        || words.All(word => new[] { task.Title, task.StatusLine, task.Question.GetValueOrDefault("") }
            .Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The board with only the tasks this search admits; a blank search returns the same board.</summary>
    public TaskBoard Narrow(TaskBoard board) =>
        IsBlank ? board : new TaskBoard(board.Tasks.Where(Admits).ToArray());

    /// <summary>
    /// The text as the start of a task id, in the 32-digit form with no dashes and in lower case: "173ef268",
    /// "6e4dc116-25fd" and "{6E4DC116" all qualify. None when it is anything but hex digits, dashes and braces around
    /// one word, when it has fewer than <see cref="MinIdDigits"/> digits, or more than an id has.
    /// </summary>
    public Maybe<string> IdPrefix
    {
        get
        {
            var digits = Text.Trim().Replace("-", "").Replace("{", "").Replace("}", "").ToLowerInvariant();
            return digits.Length is >= MinIdDigits and <= 32 && digits.All(Uri.IsHexDigit)
                ? Maybe<string>.From(digits)
                : Maybe<string>.None;
        }
    }

    /// <summary>Whether the text is the start of this task's id (see <see cref="IdPrefix"/>).</summary>
    public bool MatchesId(TaskItem task) =>
        IdPrefix.Map(prefix => task.Id.ToString("N").StartsWith(prefix, StringComparison.Ordinal)).GetValueOrDefault(false);

    /// <summary>
    /// The tasks whose id starts with the text, as their own board, when there is at least one: the panel shows them in
    /// every section and past every other filter (see "Search by id" above). None when the text is no id prefix or no
    /// task's id starts with it, and the search is then an ordinary word search.
    /// </summary>
    public Maybe<TaskBoard> Found(TaskBoard board)
    {
        if (IdPrefix.HasNoValue) return Maybe<TaskBoard>.None;
        var found = board.Tasks.Where(MatchesId).ToArray();
        return found.Length > 0 ? Maybe<TaskBoard>.From(new TaskBoard(found)) : Maybe<TaskBoard>.None;
    }
}

// The panel's "Claude working" switch: only tasks with at least one live session (TaskItem.HasLiveSession). Off returns
// the same board, as TaskGoalFilter.Narrow does for All.
public static class ClaudeWorking
{
    public static TaskBoard Narrow(TaskBoard board, bool on) =>
        on ? new TaskBoard(board.Tasks.Where(t => t.HasLiveSession).ToArray()) : board;
}
