using System.Windows.Interop;

namespace Deskbar.App;

// BRINGING A JUST-SHOWN PANEL FORWARD, AND WAITING FOR THE SHELL TO PLACE IT (Deskbar task b62c4472, Petre: "should
// automatically focus the field for me to enter text", with a screenshot of the manager's docked panel and its empty
// "Message the manager…" box).
//
// The box was meant to get the keyboard already (task 64744611, TasksWindow.PageFocus.cs): ShowManager opens the panel,
// the App's seam raises and activates it (Arrive), and FocusBoard puts the keyboard in the box. The trace of the two opens
// Petre made that morning showed why it did not:
//
//   12:55:07.714 manager panel: docked (shown) ...
//   12:55:07.817 unplaced 216107C: Window is not on any desktop (closed or pinned) ...
//   12:55:07.817 tasks panel 0x216107C: not on the desktop in front (its pin failed?), left there rather than taking you to it
//   12:55:07.826 manager panel pin failed: ... Element not found. (0x8002802B (TYPE_E_ELEMENTNOTFOUND))
//
// A window the shell has only just been asked to show is on no desktop yet: the virtual-desktop service places it a
// moment later. The seam asks "is it on the desktop in front?" (App.OnDesktopInFront) in that moment, gets no, and
// refuses the activation (rightly: activating a window on another desktop takes Windows there). The keyboard was then
// left alone, as it must be for an inactive window. The pin queued for the first show failed for the same reason. So the
// first open after every start (and the next one, since the pin is only retried on a show) came up without the keyboard.
//
// So a refused arrival is tried again a few times, ArriveRetry apart, for about two seconds in all. Once the shell has
// placed the window (pinned, or simply on the desktop in front), the seam says yes and the keyboard goes in as before.
// A window that really is on another desktop (a pin that keeps failing) is still never activated: each try asks the same
// question and the tries run out. A later arrival of the same window replaces a retry in flight, and a window hidden or
// put behind a full-screen window meanwhile ends it. The App pins with retries of its own now too (App.PinWhenShown).
public sealed partial class TasksBoard
{
    // Ten tries 200 ms apart: the trace above shows the shell placing the window within a second of the show, and two
    // seconds still reads as "it opened with the caret in the box" while the user has not moved on to something else.
    internal const int ArriveTries = 10;
    internal static readonly TimeSpan ArriveRetry = TimeSpan.FromMilliseconds(200);

    // The retry waiting for each window (the tasks panel's and the docked manager's can both be arriving at once).
    readonly Dictionary<TasksWindow, IDisposable> arrivals = new();

    // The window, shown without activation, comes to Petre: through the App's seam (raise, then activate), or, with no seam,
    // activated where it is, and then the keyboard into it (FocusBoard). Never over a full-screen window, never while it is
    // not on screen (see Arrived for both). A refusal is tried again later while tries are left (the header above).
    //
    // A RETRY NEVER TAKES THE KEYBOARD FROM WHERE THE USER HAS JUST PUT IT (review of task b62c4472). In the two seconds of
    // retries the user may click into the panel themselves (it is shown, only not active), or go on to another program.
    // So a retry ends when the window already has the keyboard, and when the foreground window has changed since the
    // first refusal to one that is not Deskbar's own: that is the user's choice, and activating the panel over it would
    // take their typing.
    void ComeForward(TasksWindow shown, TasksPanel strip, int triesLeft = ArriveTries, nint foregroundAtFirst = 0)
    {
        // A retry still waiting is replaced by this arrival, so one window never has two in flight.
        if (arrivals.Remove(shown, out var pending)) pending.Dispose();
        if (shutDown || !shown.IsVisible) return;
        var retry = triesLeft < ArriveTries;
        if (retry && (shown.IsActive || shown.IsKeyboardFocusWithin)) return;
        var foreground = Windows.Monitoring.NativeMethods.GetForegroundWindow();
        if (retry && foreground != foregroundAtFirst && !FloatingBar.IsOwnWindow(foreground))
        {
            ClickTrace.Write($"{strip.Name}: you moved on to 0x{foreground:X}, no longer brought forward");
            return;
        }
        if (strip.BehindFullScreen)
        {
            ClickTrace.Write($"{strip.Name}: behind a full-screen window, not brought forward");
            return;
        }
        var activated = bringForward is { } seam ? seam(new WindowInteropHelper(shown).EnsureHandle()) : ActivatedHere(shown);
        if (activated)
        {
            // One line when it took a retry, so a live check reads how long the shell took to place the window.
            if (retry) ClickTrace.Write($"{strip.Name}: brought forward on try {ArriveTries - triesLeft + 1}");
            shown.FocusBoard();
            return;
        }
        if (triesLeft <= 1)
        {
            ClickTrace.Write($"{strip.Name}: still not on the desktop in front after {ArriveTries} tries, left unfocused");
            return;
        }
        // The seam's COM calls pump messages, so another arrival of this window may have run inside it and left a retry of
        // its own: that one goes, keeping one in flight (review of task b62c4472). The first refusal's foreground is
        // carried along for the check above.
        if (arrivals.Remove(shown, out var stale)) stale.Dispose();
        var first = retry ? foregroundAtFirst : foreground;
        // The timer is removed from the ledger before the try, so the try does not dispose the timer that is running it.
        arrivals[shown] = undoTimer(ArriveRetry, () =>
        {
            arrivals.Remove(shown);
            ComeForward(shown, strip, triesLeft - 1, first);
        });
    }

    // The board's Dispose: a retry still waiting ends with it.
    void EndArrivals()
    {
        arrivals.Values.ToList().ForEach(arrival => arrival.Dispose());
        arrivals.Clear();
    }

    static bool ActivatedHere(TasksWindow shown)
    {
        shown.ActivateHere();
        return true;
    }
}
