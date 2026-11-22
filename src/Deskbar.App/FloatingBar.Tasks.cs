using System.Windows;
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

    // A workspace's rail colour as the last rebuild drew it, for the tasks window's chips, so a chip there wears the
    // colour of the row it points at, as the list's chips here do. None before the first rebuild or for a row without
    // a colour.
    internal Brush? WorkspaceAccent(Guid id) => workspaceAccents.GetValueOrDefault(id);

    // The bar's whole rectangle on screen in device pixels, which the tasks window keeps clear of (TasksWindow.PlaceNear;
    // the bar re-asserts itself at the top of the topmost band every second, so a window over it would be covered).
    // Empty while the bar is not on screen (minimized to its taskbar button): the window then opens where Quick find
    // does.
    internal Rect BarScreenBounds() =>
        IsVisible && PresentationSource.FromVisual(this) is not null
            ? new Rect(PointToScreen(new Point(0, 0)), PointToScreen(new Point(ActualWidth, ActualHeight)))
            : Rect.Empty;
}
