using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Geometry;

/// <summary>An arrow key's step through a menu of chips.</summary>
public enum ChipStep
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>Where a chip sits in its menu, in DIPs, the menu's own coordinates.</summary>
public readonly record struct ChipBox(double Left, double Top, double Width, double Height)
{
    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
}

// WHERE AN ARROW KEY GOES in a menu of chips that flow and wrap (the task list's workspace menu, WorkspaceChipMenu).
// Task f94524ce, Petre: "can't go to other grouped workspace, can't go to deskbar from here with the keyboard". The menu
// is one wrap of chips per block (the ungrouped workspaces, then one block per group under its caption), and WPF's own
// arrow handling stopped at a block's edge: the context menu takes Up and Down for its item rows, and its items here are
// whole blocks, which cannot take the keyboard. So the menu answers the arrows itself, from where the chips are drawn:
//
//  - Left and Right step to the previous or next chip in the menu's reading order, across lines and blocks, and wrap
//    from the last chip to the first (the menu's Tab order, which already cycles).
//  - Up and Down go to the line above or below, whichever block it belongs to (a group's caption is not a line: it
//    holds no chip), and on it to the chip whose middle is nearest the current chip's middle, so a column of chips is
//    walked straight up. Past the top or bottom line they wrap to the other end, as the Tab order does.
//
// Pure, beside the other geometry here, so the rules are tested without a window.
public static class ChipGrid
{
    /// <summary>
    /// The index of the chip <paramref name="step"/> moves to from the chip at <paramref name="from"/>;
    /// none when <paramref name="from"/> is not a chip of <paramref name="chips"/>. The chips are in the menu's reading
    /// order, as drawn: each line left to right, the lines top to bottom.
    /// </summary>
    public static Maybe<int> Next(IReadOnlyList<ChipBox> chips, int from, ChipStep step) =>
        from < 0 || from >= chips.Count
            ? Maybe<int>.None
            : step switch
            {
                ChipStep.Right => (from + 1) % chips.Count,
                ChipStep.Left => (from - 1 + chips.Count) % chips.Count,
                ChipStep.Down => AcrossLines(chips, from, 1),
                _ => AcrossLines(chips, from, -1),
            };

    // The chip nearest the current one's middle on the line `by` lines away, wrapping past either end. One line only
    // (or a line holding the chip alone) gives the chip itself back, so the key does nothing visible.
    static int AcrossLines(IReadOnlyList<ChipBox> chips, int from, int by)
    {
        var lines = Lines(chips);
        var at = lines.FindIndex(line => line.Contains(from));
        var target = lines[((at + by) % lines.Count + lines.Count) % lines.Count];
        var x = chips[from].CenterX;
        // MinBy keeps the first of a tie, the left one.
        return target.MinBy(i => Math.Abs(chips[i].CenterX - x));
    }

    // The chips grouped into the lines they are drawn on. A wrap panel puts a line's chips at one top, and the reading
    // order keeps a line's chips together, so a line is a run of chips whose middles sit within half a chip's height of
    // the run's first; the next block's first line starts lower by at least its caption, so it is a line of its own.
    internal static ImmutableList<ImmutableList<int>> Lines(IReadOnlyList<ChipBox> chips) =>
        Enumerable.Range(0, chips.Count).Aggregate(ImmutableList<ImmutableList<int>>.Empty, (lines, i) =>
            lines.Count > 0 && SameLine(chips[lines[^1][0]], chips[i])
                ? lines.SetItem(lines.Count - 1, lines[^1].Add(i))
                : lines.Add([i]));

    static bool SameLine(ChipBox first, ChipBox chip) =>
        Math.Abs(chip.CenterY - first.CenterY) < Math.Min(first.Height, chip.Height) / 2;
}
