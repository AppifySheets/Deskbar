namespace Deskbar.Core.Migration;

// What the Store copy found at its start, as much of it as the autostart takeover decides on. Gathered by
// the App layer (App.OnStartup) and nothing else, so every rule below is settled in a unit test.
//
// Packaged: this process runs from the MSIX package (PackageIdentity.IsPackaged). MarkerExists: the
// marker file PackagedTakeover.MarkerName is already in the state folder, meaning a packaged start has
// done the takeover before. RootOverridden: DESKBAR_APPDATA is set, a rehearsal. DirectRunValueExists:
// the HKCU Run value `Deskbar` the direct build writes. DirectShortcutExists: the direct build's
// Deskbar.lnk in the per-user Start menu.
public sealed record TakeoverFacts(bool Packaged, bool MarkerExists, bool RootOverridden, bool DirectRunValueExists, bool DirectShortcutExists);

// One thing the takeover does. The App layer (PackagedTakeoverApplier) applies them in the order listed.
public abstract record TakeoverStep;

// Delete the HKCU Run value `Deskbar`, the direct build's start with Windows.
public sealed record DeleteDirectRunValue : TakeoverStep;

// Delete %APPDATA%\Microsoft\Windows\Start Menu\Programs\Deskbar.lnk, the direct build's Start entry.
public sealed record DeleteDirectShortcut : TakeoverStep;

// Write the marker file into the state folder, so the takeover never runs again.
public sealed record WriteMarker : TakeoverStep;

// The first start of the Microsoft Store copy on a machine that may have the direct build set up already.
// Store packaging design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// The package's autostart is its startup task. A Run value or a Start shortcut pointing at the direct exe
// would start a second bar at login (an anti-goal of the design), so the Store copy deletes both, ONCE:
//
// - Only packaged. The direct build never takes anything over; it warns in Manage instead.
// - Once, behind a marker file in the state folder. After that, a Run value the person sets again from the
//   direct build's Manage (which warns that both will start) is their choice and stays.
// - Never under DESKBAR_APPDATA, not even the marker. A rehearsal must leave the real autostart alone, as
//   the 1.x rename migration already rules, and writing the marker there would only mark the scratch
//   folder, so leaving it out costs nothing and keeps the rule simple: an overridden root does nothing.
// - Each deletion only when there is something to delete, then the marker, so the trace says exactly
//   what was there.
public static class PackagedTakeover
{
    public const string MarkerName = "packaged-first-start-done";

    public static IReadOnlyList<TakeoverStep> Plan(TakeoverFacts facts) =>
        facts.Packaged && !facts.MarkerExists && !facts.RootOverridden
            ? [
                .. facts.DirectRunValueExists ? [new DeleteDirectRunValue()] : Array.Empty<TakeoverStep>(),
                .. facts.DirectShortcutExists ? [new DeleteDirectShortcut()] : Array.Empty<TakeoverStep>(),
                new WriteMarker(),
            ]
            : [];
}
