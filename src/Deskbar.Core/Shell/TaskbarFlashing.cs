namespace Deskbar.Core.Shell;

// DESKBAR HOLDS WINDOWS' "SHOW FLASHING ON TASKBAR APPS" OFF WHILE IT RUNS, and puts it back when it stops.
// Design: docs/superpowers/specs/2026-11-18-taskbar-flashing-switch-design.md
//
// Why. Petre keeps the taskbar on auto-hide because the bar replaces it, and it "keeps popping up when i don't
// need it". Measured on his machine (2026-11-18): Windows keeps an auto-hidden taskbar UP for as long as any
// taskbar button asks for attention, until that window is clicked. It had been up for 6.5 minutes with the
// pointer on another screen, held by Edge and by VS Code, which flashes whenever a Claude session finishes in
// a window that is not in front. With the Windows setting off, four Teams flashes in the next 13 minutes raised
// nothing, and the shell still sent the flash event, so the bar's amber dot keeps working.
//
// Why only while Deskbar runs. His rule: "set the flashing off, so that without deskbar taskbar still does its
// thing". With the setting off for good, a machine where Deskbar is not running has no flash AND no amber dot,
// and an app asking for attention goes unnoticed.
//
// This file is the decision, pure: from three facts, what to write to the registry and what to remember. The
// App layer (TaskbarFlashingSwitch) gathers the facts and carries the step out.
//
// Wanted: the setting is on (AppState.QuietTaskbar) and Deskbar is staying up. False when the box is unticked
// and on every orderly way out (Exit, Restart, sign-out), which is what "puts it back" means.
// Remembered: what the switch was before Deskbar took hold, read from the marker file; null when Deskbar is
// not holding it. FlashingOn: what the registry says now.
public sealed record TaskbarFlashingFacts(bool Wanted, bool? Remembered, bool FlashingOn);

// Write: the value to put in the registry (true = flashing on), null to leave it. Remember: the value to put
// in the marker, null to leave the marker. Forget: delete the marker.
public sealed record TaskbarFlashingStep(bool? Write, bool? Remember, bool Forget);

public static class TaskbarFlashing
{
    // The marker in the state folder, beside PackagedTakeover's. A file of its own and not a key in state.json:
    // it records a fact about this machine's registry at this moment, and it is written and deleted during
    // shutdown, when nothing should depend on the manager still being able to persist.
    public const string MarkerName = "taskbar-flashing.before";

    // The suggestion under Manage's box. Deskbar suggests the setting and does not switch it on unasked: it
    // changes a Windows setting, and nobody should find one changed without having ticked something.
    public const string NoteText =
        "Your taskbar is on auto-hide. An app that flashes brings it up and keeps it there until you click that app. Ticking this stops that.";

    public static TaskbarFlashingStep Plan(TaskbarFlashingFacts facts) =>
        facts switch
        {
            // Holding, or taking hold. The value is remembered only the FIRST time: a marker that is already
            // there was left by a crash (or by a second Sync), and holds the true original, where the registry
            // now holds the 0 Deskbar wrote. Off is written only when it is not already off.
            { Wanted: true } => new(
                Write: facts.FlashingOn ? false : null,
                Remember: facts.Remembered is null ? facts.FlashingOn : null,
                Forget: false),

            // Letting go: back to what was remembered, written only if the registry does not already say so.
            // Someone who had flashing off before Deskbar keeps it off.
            { Remembered: { } before } => new(
                Write: before != facts.FlashingOn ? before : null,
                Remember: null,
                Forget: true),

            // Not wanted and not holding: the switch is the user's own and stays untouched.
            _ => new(Write: null, Remember: null, Forget: false),
        };

    // Shown only when ticking the box would change something the user can see: the box is unticked, the
    // taskbar auto-hides, and Windows flashes. With a taskbar that is always visible a flash holds nothing up.
    public static string Note(bool ticked, bool autoHide, bool flashingOn) =>
        !ticked && autoHide && flashingOn ? NoteText : "";
}
