using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Abstractions;

// Source of truth for "what windows exist". Start() MUST be called on a thread that
// pumps messages (the WPF dispatcher thread) -- WinEvent callbacks arrive there.
public interface IWindowMonitor
{
    Result Start();
    IObservable<WindowEvent> Events { get; }
    IReadOnlyList<WindowInfo> Snapshot();

    // The listed windows that `wanted` accepts, each looked up on its own, with no batch query of every
    // process on the machine. For the 5s repair (WorkspaceManager.RepairWindowList), which only needs the
    // windows it has never seen and is almost always handed none.
    //
    // Snapshot's batch is one WMI query of every process, and the repair used to run it on the UI thread
    // every five seconds to learn about windows it already knew. Petre: "see why the bar had a delay just
    // now". A trace of 2.2.2 had RepairWindowList holding the UI thread for 4.3s of every 25s, about 850ms
    // a sweep, all of it waiting on that query; the bar's 1s stats line stalled 28 times in 9 minutes.
    IReadOnlyList<WindowInfo> SnapshotOf(Func<WindowHandle, bool> wanted);

    // Does this handle still name a real window? The one question that separates a window minimised
    // to the tray from a window that has been destroyed, and the reason it is on the interface at all
    // is that WorkspaceManager.RepairWindowList must never answer it any other way.
    //
    // "Absent from Snapshot" is NOT the same question and using it would be the bug this exists to
    // prevent: a tray-minimised window is absent from the taskbar-candidate list while its hwnd stays
    // perfectly valid, and dropping it would forget its rename ledger entry (see OnHidden).
    bool IsAlive(WindowHandle window);

    // Is it LISTED right now -- a taskbar candidate, rather than merely alive? The second half of
    // reconciling the window list, and a different question from IsAlive above: a window minimised to
    // the tray or a dialog its app has hidden is perfectly alive and holds no taskbar button.
    //
    // Petre: "what's the bell icon in the gepha workspace?" It was Outlook's hidden reminder dialog,
    // holding a row it could not answer for -- no taskbar button, and clicking it reported no desktop,
    // because Windows gives a hidden window none. Then: "yes, hide what's hidden."
    //
    // Cheap enough to ask per event (a handful of window-style reads, no enumeration), which matters:
    // the title path asks it for every title change on the machine.
    bool IsListed(WindowHandle window);

    // Has the shell itself cloaked it (DWM_CLOAKED_SHELL)? Windows does that to hide a window it still
    // holds: one on another virtual desktop, and also the empty frame a closed Store app leaves behind.
    // Alone the answer cannot tell those two apart -- the first has a desktop and the second has none --
    // so the manager asks it only of a window the virtual-desktop service could not place.
    bool IsCloakedByShell(WindowHandle window);

    // Whichever window has focus RIGHT NOW. Needed because EVENT_SYSTEM_FOREGROUND only
    // fires on a CHANGE: without seeding, the active-window highlight would stay blank from
    // launch until the user next switched windows, which reads as the feature being broken.
    // None when the foreground window is not one we track (or there is none).
    Maybe<WindowHandle> Foreground();
}
