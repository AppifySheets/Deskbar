namespace Deskbar.Core.Tasks;

// THE RECENT ROW OF THE TASK LIST'S WORKSPACE MENUS (task 46eddcb0, Petre: "show recent workspaces at the top and let me
// select from them first, so i don't have to go through unused workspaces with a keyboard"). With twenty-odd workspaces
// in the bar's order, the few he files tasks in this week sat among ones he has not touched in a month, and the arrows
// had to walk past them. WorkspaceChipMenu puts the workspaces this answers in a "Recent" row above everything else, and
// the keyboard starts there.
//
// WHAT "RECENT" MEANS: the workspaces of the newest tasks, by when each task was made (TaskItem.Created), newest first,
// each workspace once. Three other measures were weighed and set aside:
//   - The switcher's desktop MRU (WorkspaceManager.ByRecentUse) is live only, so every restart (and a dev build restarts
//     often) would empty the row, and the desktops visited are not the same thing as the places tasks are filed.
//   - TaskItem.UpdatedAt moves on every status line a session writes, so whichever workspace has the busiest Claude
//     session would float to the front whether Petre used it or not.
//   - A pick history of its own would need new saved state and would start empty; the tasks already carry the answer.
// A free task (no workspace) is left out: "none" keeps its own place at the head of the menu's first block.
public static class RecentTaskWorkspaces
{
    /// <summary>How many workspaces the Recent row holds: one or two lines of chips in the 260 DIP menu.</summary>
    public const int Count = 5;

    /// <summary>
    /// The workspaces of the newest <paramref name="tasks"/>, newest first, each once, at most <paramref name="count"/>
    /// of them. A workspace <paramref name="include"/> turns down (one that no longer exists, or one the menu leaves out)
    /// is skipped without using up a place.
    /// </summary>
    public static IReadOnlyList<Guid> Of(IEnumerable<TaskItem> tasks, Func<Guid, bool> include, int count = Count) =>
        tasks
            .Where(t => t.WorkspaceId.HasValue)
            .OrderByDescending(t => t.Created)
            .Select(t => t.WorkspaceId.Value)
            // Distinct keeps each workspace's first, so newest, appearance, in order (its documented behaviour in .NET).
            .Distinct()
            .Where(include)
            .Take(count)
            .ToArray();
}
