using Deskbar.Core.Domain;

namespace Deskbar.Core.Geometry;

// A name for a monitor ARRANGEMENT, so the bar can remember where it sat on each one (#150).
// Petre: "when changing screen resolution, when connecting from a laptop, floating window may not
// show... it needs to redraw, readjust, maybe have its own place for each layout, as windows already
// does i think."
//
// This is the "as windows already does" half. Windows 11's own "remember window locations based on
// monitor connection" keys a window's position by the set of displays attached, and that is exactly
// the shape wanted here: docked at the desk the bar sits where it was at the desk, and the
// RDP-from-laptop layout gets its own remembered spot rather than a clamped guess derived from the
// desk position.
//
// Pure, and in Core rather than in the bar, for the usual reason: the awkward cases are about the
// STRING (does enumeration order change it? does a monitor moving in the arrangement change it?) and
// every one of them is testable without a second monitor to plug in.
public static class MonitorLayoutKey
{
    // The empty string means "no layout known", and it is a real answer rather than an error. It
    // happens in compatibility mode and whenever the monitor query fails, and it must never be used
    // as a dictionary key, because two different unknown layouts are not the same layout. Callers
    // read it as "do not remember, and do not look up".
    public const string Unknown = "";

    // Physical rectangles, one per display, sorted so the key describes the ARRANGEMENT and nothing
    // else. Sorting is the whole substance of this method: EnumDisplayMonitors makes no promise
    // about order, and an unsorted join would give the same three screens two different names
    // depending on which one Windows happened to hand back first -- turning "I am back at my desk"
    // into "I have never seen this layout".
    //
    // Rectangles rather than display NUMBERS or device names. A number is an index into whatever is
    // attached at the time (unplug the middle screen and everything after it renumbers), and a
    // device name is stable for a port rather than for a picture. What actually decides whether a
    // remembered position is still meaningful is the geometry: same rectangles, same desk.
    //
    // Resolution is part of the key by the same argument, and that is deliberate rather than
    // accidental: dropping a 4K screen to 1080p moves every edge the bar could be parked against,
    // so the position it had at 4K is not the position it wants at 1080p.
    public static string Of(IEnumerable<MonitorBounds>? monitors)
    {
        if (monitors is null) return Unknown;

        var parts = monitors
            .Where(m => m is not null)
            .Select(m => $"{m.Left},{m.Top},{m.Right},{m.Bottom}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        return parts.Count == 0 ? Unknown : string.Join("|", parts);
    }

    public static bool IsKnown(string? key) => !string.IsNullOrEmpty(key);

    // Whether two keys describe the same DESK, which is looser than the same key. Petre: "after i
    // reconnected with my main desktop, deskbar ended up being stuck to the wrong monitor edge". His
    // state.json held four keys for one desk, a 4K screen left of a 4K primary, because the left screen
    // came back 229, 232, 246 and 248 px down on different reconnects. Each return minted a key the
    // exact lookup had never seen, so the spot he had parked the bar in at that desk was never found.
    //
    // So the offsets are left out, and what is compared is each screen's SIZE and the SIDE of the primary
    // screen it sits on. That keeps everything the key exists to tell apart: another resolution, another
    // number of screens, a screen moved to the other side or above. What it gives up is a screen slid along
    // the edge it shares with the primary, and that never moves an edge the bar docks against.
    //
    // A key that cannot be read (only a hand-edited state.json could hold one) matches only itself.
    public static bool SameDesk(string? first, string? second) =>
        IsKnown(first) && IsKnown(second)
        && (first == second
            || (Arrangement(first!) is { } a && Arrangement(second!) is { } b && a.SequenceEqual(b, StringComparer.Ordinal)));

    // The desk with the offsets taken out: one "side WxH" per screen, sorted so enumeration order cannot
    // matter (the same reason Of sorts). The primary screen is the one at the origin, which Windows
    // guarantees; a key with no screen there is unreadable as a desk and answers null.
    static IReadOnlyList<string>? Arrangement(string key)
    {
        var screens = key.Split('|').Select(Parse).ToList();
        if (screens.Any(s => s is null)) return null;
        var primary = screens.FirstOrDefault(s => s!.Left == 0 && s.Top == 0);
        return primary is null
            ? null
            : screens
                .Select(s => $"{SideOf(s!, primary)} {s!.Right - s.Left}x{s.Bottom - s.Top}")
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();
    }

    // Left and right are asked first, so a screen touching the primary only at a corner counts as beside it.
    static string SideOf(MonitorBounds screen, MonitorBounds primary) =>
        screen.Right <= primary.Left ? "left"
        : screen.Left >= primary.Right ? "right"
        : screen.Bottom <= primary.Top ? "above"
        : screen.Top >= primary.Bottom ? "below"
        : "primary";

    // One "left,top,right,bottom" back into a rectangle, or null. Invariant culture on the way back in,
    // because a culture with its own minus sign has broken this app before (the Georgian control shim).
    static MonitorBounds? Parse(string part)
    {
        var numbers = part.Split(',')
            .Select(text => int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : (int?)null)
            .ToList();
        return numbers.Count == 4 && numbers.All(n => n is not null)
            ? new MonitorBounds(numbers[0]!.Value, numbers[1]!.Value, numbers[2]!.Value, numbers[3]!.Value)
            : null;
    }
}
