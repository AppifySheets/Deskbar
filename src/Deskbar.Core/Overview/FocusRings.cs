using Deskbar.Core.Domain;

namespace Deskbar.Core.Overview;

// THE FOCUS RING: which windows get a coloured outline on the screen itself, and in what colour. Petre: "can you give
// each active window a ring, same in color as in the bar?", then "one that has focus, yes; and another on another
// screen, with a dimmer white", and "go with proposed" on the mockup:
//
//   Focused    the window you are typing in        its workspace's colour from the bar (the lane's colour, group
//                                                  shades included), or white where it has none
//   Elsewhere  the window in front on another      white at 45%, the bar's "fronted" marker on the screen itself
//              screen of the desktop you are on
//   Trail      the window you were in before the     the focused ring's colour, dashed for the one before and dotted
//              focused one, and the one before that  for the one before that (see Trail)
//
// Only the desktop you are on. The bar's fronted marker is also worked out for the collapsed rows of other
// workspaces (FrontsElsewhere.Of), but those windows are cloaked, so an outline on them would show nothing until a
// switch, and the switch rebuilds the bar, which works the rings out again for the desktop arrived on.
//
// "White" is used where there is no workspace colour to show: a window pinned to every desktop, any window on a
// desktop that is not a workspace, and a workspace whose colour is set to none. The colour itself comes from the
// caller (`colourOf`), because the bar's colour rules (a group's colour, a member's shade, the palette slot by
// position) read the app's state and live with the bar (FloatingBar.RowColour); this decides only who gets which.
//
// Pure, so the whole rule is tested without a window. HOW an outline is drawn (Windows' own border, or a frame for a
// maximized or full-screen window, which Windows draws no border on) is the app's: see WindowRings.
public static class FocusRings
{
    // Hex, #RRGGBB, as the palette writes its colours.
    public const string White = "#FFFFFF";

    // White at 45%. The border Windows draws is opaque, so "45% white" is the grey it would be over black: 0.45 x 255
    // = 115 = 0x73. On a dark window edge it reads as dim white, which is what the mockup showed.
    public const string DimWhite = "#737373";

    // Trail: a window you were in before the focused one, on the desktop you are on (see Trail).
    public enum Kind { Focused, Elsewhere, Asking, Trail }

    // How the ring's line is drawn. Solid for every kind but the trail, whose two steps are told apart by pattern the
    // way the bar tells the workspaces you came from apart (FloatingBar.HistoryRing): dashed, then dotted.
    public enum Line { Solid, Dashed, Dotted }

    // Line last and defaulted, so every ring that predates the trail is a solid one without saying so.
    public sealed record Ring(WindowHandle Window, Kind Kind, string Colour, Line Line = Line.Solid);

    // THE TRAIL. Petre: "workspaces as inspiration, can you ring windows similarly?", after the bar's workspace trail,
    // "current - solid - previous - dashed; one before that - dotted". So the focused window keeps its solid ring, the
    // window you were in before it is outlined dashed, and the one before that dotted.
    //
    // Two steps, because the workspace trail has two and for the reason it gives: one pattern per step, and a third
    // pattern nobody could name at a pixel or two.
    public const int TrailDepth = 2;

    // The trail's windows, nearest first: the windows of the desktop you are on (and the pinned ones, which are on
    // every desktop) by when they were last focused (WindowRow.Activation), leaving out the focused window itself.
    //
    // Per desktop and not across the machine, because a ring is drawn on the screen: the window you were in on the
    // workspace you just left is cloaked, and an outline on it would show nothing. So on arriving somewhere, the trail
    // is where you were THERE, which is also what Alt+Tab offers first on that desktop.
    //
    // A minimized window is passed over rather than kept as a step nobody can see: its rectangle is nonsense (CLAUDE.md,
    // Traps) and there is nothing to outline. The next window moves up into its step. So does a window never focused
    // since the app started (Activation 0): it is not somewhere you have been.
    public static IReadOnlyList<WindowRow> Trail(IReadOnlyList<WindowRow> rows, IReadOnlyList<WindowRow> pinned) =>
        rows.Concat(pinned)
            .Where(r => !r.IsActive && !r.IsMinimized && r.Activation > 0)
            .OrderByDescending(r => r.Activation)
            .Take(TrailDepth)
            .ToList();

    // `colourOf` answers a workspace row's colour as #RRGGBB, or null where the workspace has none.
    public static IReadOnlyList<Ring> Of(Overview overview, Func<WorkspaceGroup, string?> colourOf)
    {
        // The active window may be pinned to every desktop: the overview keeps those in Pinned, not in any row (see
        // FrontsElsewhere for the time that mattered).
        var activePinned = overview.Pinned.FirstOrDefault(r => r.IsActive);

        // The desktop you are on is either a workspace or an unnamed desktop, never both.
        var workspace = overview.Workspaces.FirstOrDefault(w => w.IsCurrent);
        var rows = workspace?.Running
                   ?? overview.OtherDesktops.FirstOrDefault(d => d.IsCurrent)?.Windows
                   ?? [];

        var activeHere = rows.FirstOrDefault(r => r.IsActive);
        var focused = activeHere ?? activePinned;

        // The workspace's colour only for a window that belongs to it; a pinned window belongs to every desktop and so
        // to none in particular.
        var focusColour = activeHere is not null && workspace is not null ? colourOf(workspace) ?? White : White;

        var focusedRing = focused is null || focused.IsMinimized
            ? []
            : new[] { new Ring(focused.Window.Handle, Kind.Focused, focusColour) };

        // The trail, in the focused ring's colour: the workspace trail on the bar draws its dashed and dotted rings in the
        // same white as the current one, so the pattern alone says "before". White for a pinned window, as above.
        var trail = Trail(rows, overview.Pinned)
            .Select((row, step) => new Ring(row.Window.Handle, Kind.Trail,
                workspace is not null && rows.Contains(row) ? colourOf(workspace) ?? White : White,
                step == 0 ? Line.Dashed : Line.Dotted))
            .ToList();
        var inTrail = trail.Select(r => r.Window).ToHashSet();

        // The same windows the bar's fronted marker picks in this row, so the screen and the bar cannot disagree. One
        // that is also in the trail wears the trail's ring instead: with two screens the window in front on the other
        // one is very often the one you were just in, and "you were here" is the more specific of the two. It is still
        // outlined, so the other screen's front window keeps a mark either way.
        var elsewhere = FrontsElsewhere.InRow(rows, activeHere is null ? activePinned : null)
            .Where(h => (focused is null || h != focused.Window.Handle) && !inTrail.Contains(h))
            .Select(h => new Ring(h, Kind.Elsewhere, DimWhite));

        return focusedRing.Concat(trail).Concat(elsewhere).ToList();
    }

    // The attention orange, the bar's AttentionDot and the asking row's blink, so the screen and the bar say "this one
    // wants you" in one colour.
    public const string Asking = "#FFA726";

    // THE BLINKING BORDER of a window asking for you (WorkspaceManager.seenAt; Petre picked "a" from the mockup at
    // https://claude.ai/artifact/PPhYsVsC8x8647K3MAjF4m). `rings` are the focus rings worked out by Of, or none when
    // the focus ring setting is off, and `blinking` the windows to blink. On the lit beat each blinking window on the
    // desktop in front gets the orange; on the dark beat it gets back the ring it would have had, or `unlit` where it
    // has none (the dim white with the setting on, so the line flips between two colours rather than coming and going;
    // null with the setting off, so the border goes back to Windows' own between beats).
    //
    // Only windows the overview has on the desktop in front: a window that asked on a workspace you have since left is
    // cloaked, and an outline on it would show nothing. A minimized one is left out too, as Of leaves it out.
    public static IReadOnlyList<Ring> WithAsking(IReadOnlyList<Ring> rings, Overview overview, IReadOnlyCollection<WindowHandle> blinking,
        bool lit, string? unlit)
    {
        var here = overview.Workspaces.FirstOrDefault(w => w.IsCurrent)?.Running
                   ?? overview.OtherDesktops.FirstOrDefault(d => d.IsCurrent)?.Windows
                   ?? [];
        var visible = here.Concat(overview.Pinned).Where(r => !r.IsMinimized).Select(r => r.Window.Handle).ToHashSet();
        var asking = blinking.Where(visible.Contains).ToHashSet();

        var beats = asking.Select(w => lit
                ? new Ring(w, Kind.Asking, Asking)
                : rings.FirstOrDefault(r => r.Window == w) ?? (unlit is { } colour ? new Ring(w, Kind.Asking, colour) : null))
            .OfType<Ring>();
        return rings.Where(r => !asking.Contains(r.Window)).Concat(beats).ToList();
    }
}
