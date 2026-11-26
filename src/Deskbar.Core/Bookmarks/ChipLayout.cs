using CSharpFunctionalExtensions;

namespace Deskbar.Core.Bookmarks;

// One cell of a row's first screen lane while it is being wrapped: a window, or a bookmark chip, never both.
// Window is a Maybe rather than a nullable so a value-type T (a test's int, say) is not boxed into a question.
public sealed record RowCell<T>(Maybe<T> Window, Bookmark? Chip);

// WHERE A ROW'S BOOKMARK CHIPS GO (docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md, "Showing",
// option A of the mockup): after the row's windows, in the row's FIRST screen lane, wrapped WITH those windows.
//
// The bar wraps a lane by IconRowLimit (a fixed count, or the cells that fit the lane's width), and a chip is the
// same width as a window's icon (IconCellWidth). So the chips are not wrapped on their own: the lane's windows and
// its chips are handed to the bar's own wrap as one sequence of cells, windows first, and the lines that come back
// are split into the two kinds again. A chip therefore takes exactly the room an icon would, a line that is full of
// windows pushes the chips onto the next line, and the arithmetic that keeps a lane from overflowing (#58) covers
// the chips without a second copy of it.
//
// The two lists that come back are ALIGNED BY INDEX: line i of the lane holds Windows[i] and then Chips[i], either
// of which can be empty. The bar draws line i of the row from both.
public static class ChipLayout
{
    public static (IReadOnlyList<IReadOnlyList<T>> Windows, IReadOnlyList<IReadOnlyList<Bookmark>> Chips) Split<T>(
        IReadOnlyList<T> windows,
        IReadOnlyList<Bookmark> chips,
        Func<IReadOnlyList<RowCell<T>>, IReadOnlyList<IReadOnlyList<RowCell<T>>>> wrap)
    {
        var cells = windows.Select(w => new RowCell<T>(Maybe.From(w), null))
            .Concat(chips.Select(c => new RowCell<T>(Maybe<T>.None, c)))
            .ToList();
        var lines = wrap(cells);
        return (
            lines.Select(line => (IReadOnlyList<T>)line.Where(c => c.Window.HasValue).Select(c => c.Window.Value).ToList()).ToList(),
            lines.Select(line => (IReadOnlyList<Bookmark>)line.Select(c => c.Chip).OfType<Bookmark>().ToList()).ToList());
    }
}
