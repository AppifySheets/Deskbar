using Deskbar.Core;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE OTHER SCREEN'S FRONT WINDOW, for its marker (the "fronted" one in FloatingBar.Marker.cs). Petre: "in 2monitor
// setup, can you identify the window that's on top on ANOTHER screen, not the one that's active?", "mark it, too".
//
// Read once per rebuild, at its start, so every icon of that rebuild agrees. A rebuild is what follows every change
// that can move a window to the front: activating a window changes the foreground, and minimizing, closing or opening
// one changes the window list, and both rebuild the bar. A window raised without being activated would not be
// noticed until the next rebuild; nothing on Petre's desk does that.
//
// The z-order walk itself is ScreenFronts, in the Windows layer. Only the windows the bar lists count, read from the
// manager as a set first so the walk asks each window in constant time.
public partial class FloatingBar
{
    IReadOnlySet<WindowHandle> screenFronts = new HashSet<WindowHandle>();

    // Set by the composition root (App) to ScreenFrontsOf. Left at nothing, as every test but the marker ones leaves
    // it, the bar marks no window this way: a test bar must not read the z-order of the machine running it, where a
    // real window could share a fake one's handle.
    internal Func<IReadOnlySet<WindowHandle>> ReadScreenFronts { get; init; } = () => new HashSet<WindowHandle>();

    internal static IReadOnlySet<WindowHandle> ScreenFrontsOf(WorkspaceManager manager)
    {
        var known = manager.KnownWindows.Select(w => w.Handle).ToHashSet();
        return ScreenFronts.AwayFromForeground(known.Contains);
    }

    // The active window keeps its own, stronger marker, so it is never fronted as well.
    bool Fronted(WindowRow row) => !row.IsActive && screenFronts.Contains(row.Window.Handle);
}
