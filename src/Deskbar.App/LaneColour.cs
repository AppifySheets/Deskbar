using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.App;

// A workspace's lane colour and the name a list shows it by, resolved exactly as the bar resolves them. Shared
// by the workspace switcher and quick find, so a workspace looks the same in every list that names it.
public static class LaneColour
{
    // A group has one colour (#90), so every member shows the group's; an ungrouped workspace shows its own,
    // by its DEFINED position rather than its position in any recent-use list, so it never changes colour
    // because it was visited.
    public static string Of(AppState state, Workspace workspace) =>
        state.GroupOf(workspace.Id) is { } group
            ? WorkspacePalette.For(group.Color, Math.Max(0, state.ColourSlotOf(group)))
            : WorkspacePalette.For(workspace, Math.Max(0, state.Workspaces.ToList().FindIndex(w => w.Id == workspace.Id)));

    // The group's name for a member of a group, null for one that stands alone and for the group's anchor,
    // whose row would otherwise read "Sparrow › Sparrow" (#93). Both group kinds present the same way.
    public static string? GroupNameOf(AppState state, Workspace workspace) =>
        state.GroupOf(workspace.Id) is { } group && !state.IsAnchor(workspace.Id) ? group.Name : null;

    // "Sparrow › dice-rolls" for a member, the bare name otherwise: Petre names children by what they are and
    // lets the group say where they sit.
    public static string FullName(AppState state, Workspace workspace) =>
        GroupNameOf(state, workspace) is { } group ? $"{group} › {workspace.Name}" : workspace.Name;
}
