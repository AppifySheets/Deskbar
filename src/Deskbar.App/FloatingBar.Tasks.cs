using System.Windows.Media;
using CSharpFunctionalExtensions;

namespace Deskbar.App;

// TASKS IN THE BAR: what the bar keeps for the task list (docs/superpowers/specs/2026-11-22-tasks-board-design.md).
//
// The timer version of tasks (docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md) drew a stopwatch in the
// bottom strip, a pie in the caption of a workspace row with a running timer, and a menu behind the stopwatch that
// parked a workspace for a few minutes. The tasks board has no timers, so all of that is gone. What stays here is
// what the task list at the bottom of the bar needs from the rows:
//
//   a workspace's name     WorkspaceName, so a line can say which workspace a task belongs to.
//   a workspace's colour   NoteWorkspaceAccent, called by every rebuild with each workspace row's rail colour, so a
//                          line wears the same colour as the row it points at. The colours belong to the rows of one
//                          rebuild, so ForgetWorkspaceAccents empties them with those rows.
public partial class FloatingBar
{
    // Each workspace row's rail colour, from the last rebuild. Null for a workspace that opted out of a colour.
    readonly Dictionary<Guid, Brush?> workspaceAccents = [];

    void ForgetWorkspaceAccents() => workspaceAccents.Clear();

    void NoteWorkspaceAccent(Guid workspace, Brush? accent) => workspaceAccents[workspace] = accent;

    Maybe<string> WorkspaceName(Guid id) => manager.State.Workspaces.TryFirst(w => w.Id == id).Map(w => w.Name);
}
