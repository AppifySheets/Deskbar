namespace TaskSpaces.Core.Domain;

// Petre: "i also want different colors for different workspaces in the lanes", "configurable".
//
// Configurable, but never blank: a workspace with no colour set gets one from this palette by
// its position, so lanes are distinguishable the moment they exist rather than only after
// someone goes and picks colours. Workspace.Color overrides it.
//
// Pure and in Core (not in the WPF layer) so the choice is unit-testable and so any future
// surface tints a workspace the same way the bar does.
public static class WorkspacePalette
{
    // Muted rather than saturated: these sit behind app icons on a translucent bar, so they have to
    // separate the lanes without competing with the icons, the active-window highlight or the amber
    // candidate ring. Ordered so adjacent lanes are far apart in hue.
    //
    // #68 tried four replacements for this list -- brighter hues, lighter still, an evenly
    // constructed wheel, deep jewel tones at high alpha -- and Petre rejected every one, at the end
    // in as many words: "old colors were better, they were darker and better. let's just go back
    // and avoid that green." So this IS the original set, restored, with exactly one change.
    //
    // Worth recording why the constructed ones lost, because the arithmetic was sound and the
    // result still looked wrong. Even hue spacing is a property of the WHEEL, not of the colours
    // you get from it: nine hues 40 degrees apart must put one or two in the band between yellow
    // and green, and nothing in that band is attractive as a dark lane -- it is olive, mustard and
    // moss, which is where "terrible colors" and "still bad colors" came from. A hand-picked set
    // can simply decline to visit it. Measuring hue separation was the right check and the wrong
    // objective.
    //
    // THE ONE CHANGE: moss green is gone, replaced by orchid. Petre, twice: "i still don't like the
    // green for sparrow", then "avoid that green". It sat at slot 6, and slot 6 is Sparrow.
    //
    // Orchid rather than another green, and its hue is not a taste call: with moss removed, the
    // widest gap left between the remaining eight is 271-340 (violet to plum), so ~305 is the one
    // place a ninth colour can go without crowding a neighbour. It is 34 degrees from violet and 35
    // from plum -- the most room available, in a palette this muted.
    //
    // Names are what the picker shows, so they have to be the word someone would reach for rather
    // than the hue number.
    public sealed record Swatch(string Name, string Hex);

    // ORDER IS THE OTHER HALF, and it is not the order these were written in. Petre, pointing at two
    // rows that touched: "can you avoid similar colors next to each other? eurocredit and sparrow."
    //
    // He was pointing at plum and orchid, and replacing moss with orchid is what put them a slot
    // apart -- the new colour had to go somewhere, and appending it dropped it next to the one
    // existing colour it was closest to.
    //
    // So the sequence is now SOLVED rather than chosen: of every arrangement of these nine, this is
    // the one whose most-similar adjacent pair is as different as it can be. It triples the worst
    // neighbouring pair on the bar (0.0157 -> 0.0475) with no colour changed.
    //
    // Two things the measurement had to get right, and hue alone gets both wrong:
    //
    //   * It compares the colours AS SHOWN -- each hex composited at the lane's alpha over the
    //     bar's dark background -- not as stored. At 22% alpha two hexes that look distinct in a
    //     file arrive nearly identical, which is exactly how plum and orchid got past a hue check.
    //   * It measures in OKLab, where distance matches what the eye does. Two colours can be far
    //     apart in hue and still look alike if their lightness and chroma agree.
    //
    // The list CYCLES: a tenth workspace wears slot 0 again, directly under the ninth, so the last
    // and first are neighbours too and the arrangement is solved as a ring rather than a line.
    //
    // Indigo stays at slot 0 -- the one tie worth keeping, since it matches the app icon.
    //
    // ---- VIVID (2.1.1) -------------------------------------------------------------------------
    //
    // Everything above argued for MUTED colours, and it was right for what the colour was then: a tint
    // painted behind a whole row of icons. The accent rail style ("try b") changed what the colour is.
    // A row is no longer tinted (only the current one is, faintly); its colour is a 3 DIP rail down its
    // left edge, and a line cannot drown an icon the way a lane could. Petre, seeing that: "now that we
    // have just a line for the colors, make it more colorful and vivid and youthful", and from the
    // mockup's three options (muted as now, Vivid, Candy) he picked Vivid.
    //
    // So: saturated, mid-light hues that hold up on the bar's near-black, with the two lessons of #68
    // kept. Nothing from the olive and mustard band between yellow and green, which is where "terrible
    // colors" came from; sunflower is a clean yellow and there is no green at all. And the ORDER is
    // solved, not chosen: the same measurement as before (OKLab distance between neighbours, around the
    // ring, as shown), except that rails are now drawn opaque, so the colours are compared as they are.
    // Of every order with Indigo first, this one makes the most similar neighbouring pair as different
    // as it can be: 0.327, against 0.221 for the order the mockup showed. The close pairs it keeps apart
    // are hot pink and coral, violet and orchid, and indigo and violet (WorkspacePaletteTests pins it).
    public static readonly IReadOnlyList<Swatch> Swatches =
    [
        new("Indigo", "#5B6CFF"), // matching the app icon
        new("Tangerine", "#FF8A2B"),
        new("Violet", "#A66BFF"),
        new("Sunflower", "#FFC935"),
        new("Orchid", "#E45CFF"),
        new("Aqua", "#19D3B5"),
        new("Hot pink", "#FF4D9D"),
        new("Sky", "#2FB4FF"),
        new("Coral", "#FF5A5F"),
    ];

    // THE MUTED SET, RETIRED, and the colour each one becomes. A workspace or group whose colour was
    // picked from the old palette stores that swatch's hex (Petre's EC group holds the old indigo), and
    // a stored hex would otherwise keep the muted colour now drawn at full strength, the dullest thing on
    // a vivid bar. Choosing a palette entry meant "that palette colour", so it follows the palette.
    //
    // Mapped where colours are READ (Current), not rewritten in state.json: nothing to migrate, nothing
    // lost, and a Custom… colour that happened to match none of these is left exactly as chosen. Each old
    // name maps to its nearest vivid relative, and the names stay accepted by the `color` command, so a
    // script saying "teal" or "rust" keeps working.
    static readonly IReadOnlyList<(string Name, string Hex, string Becomes)> Retired =
    [
        ("Indigo", "#3C48BE", "Indigo"),
        ("Plum", "#9A3B5A", "Hot pink"),
        ("Steel", "#2F6FA8", "Sky"),
        ("Violet", "#8A4CD6", "Violet"),
        ("Teal", "#2E7D6B", "Aqua"),
        ("Orchid", "#8A3C7E", "Orchid"),
        ("Amber", "#B06A2C", "Tangerine"),
        ("Slate", "#6A5ACD", "Indigo"),
        ("Rust", "#A8562C", "Coral"),
    ];

    // Declared AFTER Swatches on purpose: static fields initialise in declaration order, so
    // reading Swatches from above it would read a null.
    static readonly IReadOnlyList<string> Defaults = Swatches.Select(s => s.Hex).ToList();

    // The vivid swatch with this name, case-insensitive, or the one a retired name became. Null when the
    // name is neither.
    public static Swatch? Named(string name) =>
        Swatches.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Retired.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(r => Swatches.First(s => s.Name == r.Becomes))
            .FirstOrDefault();

    // A stored colour as it is drawn now: a retired swatch's hex becomes its vivid replacement, anything
    // else (a Custom… colour, "none", null) is returned untouched.
    public static string? Current(string? color) =>
        color is null
            ? null
            : Retired.Where(r => r.Hex.Equals(color.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(r => Swatches.First(s => s.Name == r.Becomes).Hex)
                .FirstOrDefault() ?? color;

    // Petre: "add transparent as an option for color" (#68).
    //
    // A SENTINEL rather than a colour, and the distinction is the point: "no lane" is not a colour
    // and cannot be spelled as one. #00000000 would be a transparent BLACK, which the tint path
    // would happily strip the alpha off and paint as black; "Transparent" parses to a
    // transparent WHITE, which would come out as a white wash for the same reason. Both are
    // colours that happen to be invisible, and neither is the absence of one.
    //
    // Distinct from Color being null, which means "follow my position in the list". A workspace
    // that has opted out has made a choice, and a reorder must not undo it.
    public const string None = "none";

    public static bool IsNone(string? color) =>
        string.Equals(color?.Trim(), None, StringComparison.OrdinalIgnoreCase);

    // index is the position in the user's own ordering, so the colour follows the list rather than a
    // hash of the name: renaming a workspace should not recolour it.
    //
    // Takes a bare colour rather than a workspace because a GROUP has one too (#90, Group.Color) and
    // the rule for resolving it is identical: an override if there is one, the position's colour
    // otherwise. Two copies of that rule would be two places for it to drift.
    public static string For(string? color, int index) =>
        string.IsNullOrWhiteSpace(color) ? Defaults[index % Defaults.Count] : Current(color)!;

    public static string For(Workspace workspace, int index) => For(workspace.Color, index);

    // A MEMBER'S SHADE of its group's colour. Petre, on a group whose two rows had identical red rails: "when a
    // group, both subbands are of the same color, it's kinda confusing", then "let them be different shades of
    // that color". The group stays one colour family, which is what says "these belong together", and each row
    // in it gets its own step of that colour, which says "and these are different workspaces".
    //
    // Step 0 is the colour itself, so the first member (the anchor, in an anchored group) wears exactly the
    // group's colour, like the group's caption and outline. After that the steps alternate a shade LIGHTER and a
    // shade DARKER, each pair a further 30% from the base: 1 lighter, 2 darker, 3 lighter still, 4 darker still.
    // Alternating rather than one direction, so neighbours differ in both directions from the base and a group
    // of four does not run out into near-white or near-black. Capped at 60%, past which the shade stops reading
    // as the same colour. Anything that is not #RRGGBB is returned untouched.
    public static string Shade(string hex, int step)
    {
        var colour = hex.Trim();
        if (step <= 0 || colour.Length != 7 || colour[0] != '#'
            || !int.TryParse(colour[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return hex;
        var amount = Math.Min(0.6, 0.3 * ((step + 1) / 2));
        var lighter = step % 2 == 1;
        int Channel(int shift)
        {
            var c = (rgb >> shift) & 0xFF;
            return (int)Math.Round(lighter ? c + (255 - c) * amount : c * (1 - amount));
        }
        return $"#{Channel(16):X2}{Channel(8):X2}{Channel(0):X2}";
    }
}
