using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE OVERVIEW `task overview` PRINTS: one line per open task, for the manager session (a Claude session that
// coordinates the board, docs/superpowers/specs/2026-11-25-manager-session-design.md) to read cheaply. `task list` is
// JSON with every thread's text and runs to about 13,000 tokens on a real board; this is a few thousand characters, and
// the manager asks `task show <id>` for the one task it wants in full.
//
// A line reads, left to right:
//
//   bbf9a6ed  waiting      !*  Deskbar: Manager session | Design written | asks: Approve the design? | claude: idle
//   id8       status       flags  workspace: title       | status line   | the open question         | sessions
//
// The flags are "!" for important and "*" for urgent, blank when neither. The workspace is left off for a task on none.
// The status line and the question are left off while empty. The sessions read "none" (no session ever joined),
// "working" (a live session the latest `claude agents` list calls busy), "idle" (live, not busy, or busy unknown) or
// "ended" (every session over).
//
// The order is the bar's (TaskBoard.Pile). A subtask sits right under its parent, indented two spaces a level; one whose
// parent is not listed (closed, or snoozed) stands at the top level, so it is not lost. Snoozed tasks are left out of the
// lines, since the bar hides them too, and counted on one closing line, "+ 3 snoozed".
public static class TaskOverview
{
    // A title is cut to this many characters, the ellipsis included, so a long one cannot take a line over.
    const int TitleLimit = 60;

    // The status line and the question are cut shorter than the page would let them: the manager reads the full text
    // with `task show`.
    const int NoteLimit = 80;

    const string Ellipsis = "...";

    // The pipe's names for the statuses (RemoteControl.StatusNames), padded in the line to the longest, "in-progress".
    static string StatusName(TaskStatus status) => status switch
    {
        TaskStatus.Backlog => "backlog",
        TaskStatus.InProgress => "in-progress",
        TaskStatus.WaitingOnYou => "waiting",
        TaskStatus.Done => "done",
        _ => "closed",
    };

    /// <param name="board">The board to summarize.</param>
    /// <param name="now">Reserved for the caller's clock; the board's own snooze mark decides what is snoozed.</param>
    /// <param name="workspaceName">A workspace's name by id, or none when it is gone.</param>
    /// <param name="busy">Whether a session is busy in the latest `claude agents` list, or none when it is not listed.</param>
    public static string Text(TaskBoard board, DateTimeOffset now, Func<Guid, string> workspaceName, Func<string, Maybe<bool>> busy)
    {
        var pile = board.Pile();
        var shown = pile.Where(t => !t.IsSnoozed).ToArray();
        var shownIds = shown.Select(t => t.Id).ToHashSet();
        var children = shown.Where(t => t.ParentId.HasValue && shownIds.Contains(t.ParentId.Value))
            .ToLookup(t => t.ParentId.Value);
        var roots = shown.Where(t => !(t.ParentId.HasValue && shownIds.Contains(t.ParentId.Value)));

        IEnumerable<string> LinesOf(TaskItem task, int depth) =>
            new[] { Line(task, depth, workspaceName, busy) }.Concat(children[task.Id].SelectMany(c => LinesOf(c, depth + 1)));

        var snoozed = pile.Count(t => t.IsSnoozed);
        var closing = snoozed > 0 ? new[] { $"+ {snoozed} snoozed" } : [];
        var lines = roots.SelectMany(t => LinesOf(t, 0)).Concat(closing).ToArray();
        return lines.Length == 0 ? "No open tasks." : string.Join("\n", lines);
    }

    static string Line(TaskItem task, int depth, Func<Guid, string> workspaceName, Func<string, Maybe<bool>> busy)
    {
        var flags = (task.Important ? "!" : "") + (task.Urgent ? "*" : "");
        var title = Cut(OneLine(task.Title), TitleLimit);
        var named = task.WorkspaceId.Map(workspaceName).Where(n => !string.IsNullOrWhiteSpace(n));
        var heading = named.HasValue ? $"{named.Value}: {title}" : title;
        var parts = new[]
            {
                heading,
                task.StatusLine,
                task.Question.Map(q => $"asks: {Cut(OneLine(q), NoteLimit)}").GetValueOrDefault(""),
                $"claude: {SessionsOf(task, busy)}",
            }
            .Select((p, i) => i == 1 ? Cut(OneLine(p), NoteLimit) : p)
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var id8 = task.Id.ToString()[..8];
        return $"{new string(' ', depth * 2)}{id8}  {StatusName(task.Status),-11}  {flags,-2}  {string.Join(" | ", parts)}";
    }

    static string SessionsOf(TaskItem task, Func<string, Maybe<bool>> busy) => task.Sessions switch
    {
        [] => "none",
        var all when all.Any(s => s.IsLive) =>
            all.Where(s => s.IsLive).Any(s => busy(s.Id).GetValueOrDefault(false)) ? "working" : "idle",
        _ => "ended",
    };

    // A cut that never leaves the ellipsis alone: the result is at most `limit` characters.
    static string Cut(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - Ellipsis.Length)].TrimEnd() + Ellipsis;

    // Titles and questions can hold line breaks; one line per task is the format's whole point.
    static string OneLine(string text) => string.Join(" ", (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
