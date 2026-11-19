using CSharpFunctionalExtensions;

namespace Deskbar.Core.Domain;

// A single instant's answer to "where is each window on the physical screen, and what state is
// it in". Gathered in one pass (see IScreenLayout) because a z-order index only means anything
// relative to the enumeration that produced it.
//
// Empty is the honest default everywhere this is unavailable -- compatibility mode, and every
// test that predates it. It reads as "no monitor known, nothing minimised, no z-order", which
// leaves the bar sorting and rendering exactly as it did before any of this existed.
public sealed record ScreenFacts(
    // Windows' own display number -- the 1, 2, ... you see under Display Settings > Identify.
    // Absent for a window whose monitor could not be resolved; those sort last rather than
    // disappearing, on the same principle as the overview's "Unplaced" group.
    IReadOnlyDictionary<WindowHandle, int> MonitorOf,
    IReadOnlySet<WindowHandle> Minimized,
    // Position in the OS's front-to-back order: 0 is the front-most window. Only ever populated
    // for the CURRENT virtual desktop, because EnumWindows skips cloaked windows and every
    // window on another desktop is cloaked. That is a limit worth keeping rather than working
    // around: "which one is on top" is a question about the screen you are looking at.
    IReadOnlyDictionary<WindowHandle, int> ZOrder,
    // Which display Windows calls primary -- "the main monitor". None only if no display
    // reported itself as primary, which Windows does not do, but which is cheaper to tolerate
    // than to assume away.
    Maybe<int> PrimaryMonitor = default,
    // Where each display physically sits, keyed by the same display number MonitorOf uses. Drives
    // both the order icons are grouped in and how many strokes each group's hairline draws (see
    // MonitorArrangement). Optional and last for the same reason PrimaryMonitor is: null means
    // "no geometry available", which is compatibility mode and every test written before this,
    // and leaves ordering exactly as it was -- ascending by display number.
    IReadOnlyDictionary<int, MonitorBounds>? MonitorPlacement = null,
    // Each display's WORK AREA (rcWork), keyed like MonitorPlacement: the display minus what the
    // taskbar and any docked app bar have reserved, Deskbar's own docked strip included. Kept apart
    // from MonitorPlacement because the two answer different questions. Placement says where a screen
    // IS (ordering, the layout key, which screen a window is on) and must not change when a taskbar
    // appears; the work area says where a window may be PUT, which is what a monitor move needs (see
    // WorkspaceManager.MoveWindowToMonitor). Null, or a display missing from it, means "not known",
    // and the caller falls back to the display's whole rectangle.
    IReadOnlyDictionary<int, MonitorBounds>? MonitorWorkAreas = null)
{
    public static readonly ScreenFacts Empty = new(
        new Dictionary<WindowHandle, int>(),
        new HashSet<WindowHandle>(),
        new Dictionary<WindowHandle, int>());

    // The given windows that are on the main monitor, front to back.
    //
    // A LIST rather than just the front one, because the caller has to be able to keep looking:
    // this snapshot cannot be trusted to describe only the desktop we are on (see
    // WorkspaceManager.FrontmostOnMainMonitor), so the first candidate may have to be rejected.
    //
    // Restricted to a caller-supplied set because ZOrder comes from a raw EnumWindows that has
    // never heard of our own chrome: the floating bar is a taskbar candidate AND permanently
    // topmost, so it would otherwise head this list every time.
    public IReadOnlyList<WindowHandle> OnPrimaryFrontToBack(IEnumerable<WindowHandle> candidates) =>
        PrimaryMonitor
            .Map(primary => (IReadOnlyList<WindowHandle>)candidates
                .Where(w => ZOrder.ContainsKey(w) && MonitorOf.TryGetValue(w, out var m) && m == primary)
                .OrderBy(w => ZOrder[w])
                .ToList())
            .GetValueOrDefault([]);
}
