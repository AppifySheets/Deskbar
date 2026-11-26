using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests.Bookmarks;

// How a row's first screen lane is shared between its windows and its bookmark chips: the chips follow the
// windows in one sequence, so they wrap with them by the bar's own arithmetic, and the lines come back split
// into the two kinds again, aligned by index.
public sealed class ChipLayoutTests
{
    static Bookmark Chip(string name) => new(Guid.NewGuid(), name, BookmarkKind.Link, $"https://{name}.org");

    // Four cells a line, as a stand-in for IconRowLimit's width arithmetic.
    static IReadOnlyList<IReadOnlyList<RowCell<string>>> FourPerLine(IReadOnlyList<RowCell<string>> cells) =>
        cells.Chunk(4).Select(line => (IReadOnlyList<RowCell<string>>)line).ToList();

    [Fact]
    public void Chips_follow_the_windows_on_the_last_line_and_wrap_with_them()
    {
        var b1 = Chip("b1");
        var b2 = Chip("b2");

        var (windows, chips) = ChipLayout.Split(["w1", "w2", "w3"], [b1, b2], FourPerLine);

        Assert.Equal(2, windows.Count);
        Assert.Equal(["w1", "w2", "w3"], windows[0]);
        Assert.Empty(windows[1]);
        Assert.Equal(2, chips.Count);
        Assert.Equal([b1], chips[0]);
        Assert.Equal([b2], chips[1]);
    }

    [Fact]
    public void A_row_with_no_windows_holds_only_chips()
    {
        var b1 = Chip("b1");
        var b2 = Chip("b2");

        var (windows, chips) = ChipLayout.Split<string>([], [b1, b2], FourPerLine);

        Assert.Single(windows);
        Assert.Empty(windows[0]);
        Assert.Single(chips);
        Assert.Equal([b1, b2], chips[0]);
    }

    [Fact]
    public void No_chips_changes_nothing()
    {
        var titles = new[] { "a", "b", "c", "d", "e", "f", "g" };

        var (windows, chips) = ChipLayout.Split(titles, [], cells => IconRowLimit.Lines(cells));

        Assert.Equal(IconRowLimit.Lines(titles).Select(l => l.ToList()), windows.Select(l => l.ToList()));
        Assert.Equal(windows.Count, chips.Count);
        Assert.All(chips, Assert.Empty);
    }
}
