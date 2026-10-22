namespace Deskbar.Core.Persistence;

// The items of the bar's bottom strip, the taskbar's right end redrawn on the bar, which can each be shown or
// hidden from Manage > Settings. Petre, on a laptop strip too narrow for them all: "i don't want wifi there",
// "and i don't think i want notifications either", "make it a setting, what to show".
//
// Saved by NAME (the store writes enums as strings), so reordering these members never changes what a saved
// state.json means. AppState keeps the HIDDEN ones rather than the shown ones, so an item added in a later
// build appears for everyone, instead of being hidden because no saved list mentioned it.
public enum StripItem
{
    Tray,           // the chevron that opens the tray's overflow
    QuickSettings,  // wifi and, on a machine with one, the battery
    Volume,         // the volume dial
    Notifications,  // the bell (the clock opens the same panel)
    Language,       // the keyboard language switcher, itself shown only with two or more languages
    Clock,          // time over date
    ShowDesktop,    // the sliver at the very edge
}
