using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.App;

// The focus ring's hook in the bar (FocusRings decides, WindowRings draws). Here because the bar already has both
// things the rings need, worked out once per rebuild: the overview, which says which window has focus and which is in
// front on each screen, and the bar's colour rules (RowColour), so a ring is the exact colour of its workspace's lane.
public partial class FloatingBar
{
    // Set by App. Null in the tests that build a bar on its own, and in compatibility mode.
    internal WindowRings? Rings { get; init; }

    // Called by both rebuilds, the full one and the strips-only one of a minimized bar (FillStripsAlone), so the rings
    // follow focus whether the bar is showing or not. With the setting off, every ring is taken away.
    void ShowFocusRings(Overview overview) =>
        Rings?.Show(manager.State.FocusRing ? FocusRings.Of(overview, w => RingColourOf(overview, w)) : []);

    // A workspace's colour as the ring wants it: #RRGGBB, or null where the workspace's colour is none. The same
    // resolution as the window list's spine and a strip's tag (WorkspaceAccent, ListSpine): the group's colour in the
    // member's shade, or the palette's colour for the row's position.
    string? RingColourOf(Overview overview, WorkspaceGroup workspace)
    {
        var group = workspace.Workspace.GroupId is { } id ? manager.State.Groups.FirstOrDefault(g => g.Id == id) : null;
        var (colour, slot, _) = RowColour(overview, workspace, group);
        return WorkspacePalette.IsNone(colour) ? null : WorkspacePalette.For(colour, slot < 0 ? 0 : slot);
    }
}
