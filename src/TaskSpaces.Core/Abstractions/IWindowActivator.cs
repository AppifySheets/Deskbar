using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Bring a window to the foreground (un-minimizing if needed). Windows-layer concern
// (SetForegroundWindow); abstracted so JumpTo is testable with a fake.
public interface IWindowActivator
{
    Result Activate(WindowHandle window);

    // ...and put it away again. The other half of the bar's click toggle (Petre: "i want to be
    // able to minimize windows from the floating bar"), and the exact inverse of what Activate
    // does to a minimized window, so the two belong on the same interface rather than in a
    // second one that would inevitably be handed around alongside it.
    Result Minimize(WindowHandle window);

    // Is it down right now? A plain bool with no Result, because IsIconic cannot fail and cannot
    // be refused: it reads a window's own style bits, unlike the two calls above, which ask the
    // OS to DO something and can be denied.
    //
    // Here rather than on IScreenLayout (which also reports minimised state) because those facts
    // are a SNAPSHOT taken during a rebuild, and the whole reason this exists is that a snapshot
    // is exactly what cannot be trusted at the moment a click is decided (see
    // WorkspaceManager.ToggleWindow).
    bool IsMinimized(WindowHandle window);

    // "Close these, then tell me which ones are still here." The close half of deleting a workspace
    // with windows still in it (Petre: "delete workspace in context menu, close all windows in it").
    //
    // Here rather than on an interface of its own, for the reason Minimize is: it is one more thing
    // asked of a window by handle, and a second interface would only ever be handed around beside
    // this one.
    //
    // A LIST rather than one window at a time, because the wait is what makes this slow and the
    // windows can do it at the same time: asking twenty windows to close and then waiting once is
    // twenty times cheaper than waiting after each. The caller has all of them anyway.
    //
    // The return is the SURVIVORS -- the windows that were still there when the wait ran out. Not a
    // Result: nothing here can fail in a way a caller could act on. A handle that has already died
    // is a success, and a window that refuses to go is the one interesting outcome, which is what
    // the list carries.
    IReadOnlyList<WindowHandle> CloseAll(IReadOnlyList<WindowHandle> windows);

    // The window icon menu's Close (Petre: "right clicking an app should have close and force close").
    // The title bar X's request, posted, with no wait: one window, and the menu has nothing to do after it.
    // An app with unsaved work asks its question and stays, exactly as with the X.
    Result Close(WindowHandle window);

    // ...and Force close: END the process that owns the window, without asking it. With `everyInstance`,
    // every process running the same exe, the menu's "Close all" (Petre: "also, close all", "so i can kill
    // all vscode or edge processes"): an app like VS Code or Edge runs as a dozen processes, most with no
    // window, and ending only the windowed ones leaves the rest. Never this app's own process. A Result,
    // because a process running as administrator refuses a normal-level caller, and that is worth saying.
    Result EndProcess(WindowHandle window, bool everyInstance);
}
