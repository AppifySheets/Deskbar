using Deskbar.Core.Migration;

namespace Deskbar.Core.Tests.Migration;

// The first start of the Microsoft Store copy on a machine that may already have the direct build set to
// start with Windows. The package's own startup task is its autostart, so the direct build's Run value and
// Start menu shortcut would start a second bar at login; the Store copy deletes them once, behind a marker.
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Every rule is decided by the pure plan below; the App layer (PackagedTakeoverApplier) only does what it
// lists, and is deliberately not exercised here against real paths.
public class PackagedTakeoverTests
{
    [Fact]
    public void First_packaged_start_deletes_direct_autostart_and_writes_marker() =>
        Assert.Equal([new DeleteDirectRunValue(), new DeleteDirectShortcut(), new WriteMarker()],
            PackagedTakeover.Plan(new(true, false, false, true, true)));

    // A start under DESKBAR_APPDATA is a rehearsal and must leave the real autostart alone. No marker
    // either, so the real first packaged start still does the takeover.
    [Fact]
    public void Nothing_under_a_root_override_not_even_the_marker() =>
        Assert.Empty(PackagedTakeover.Plan(new(true, false, true, true, true)));

    // Once only: after the first packaged start, a Run value the person sets again from the direct build
    // (which warns them) is theirs to keep.
    [Fact]
    public void Nothing_when_the_marker_exists() =>
        Assert.Empty(PackagedTakeover.Plan(new(true, true, false, true, true)));

    [Fact]
    public void Nothing_unpackaged() =>
        Assert.Empty(PackagedTakeover.Plan(new(false, false, false, true, true)));

    [Fact]
    public void Only_the_marker_when_there_is_nothing_to_delete() =>
        Assert.Equal([new WriteMarker()], PackagedTakeover.Plan(new(true, false, false, false, false)));

    [Fact]
    public void Only_the_run_value_when_there_is_no_shortcut() =>
        Assert.Equal([new DeleteDirectRunValue(), new WriteMarker()],
            PackagedTakeover.Plan(new(true, false, false, true, false)));
}
