using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// Which window holds which slot, per screen. The list order IS the slot order, and it matches
// GridLayout.Arrange: column by column, top to bottom inside a column, so the n-th window of a screen
// gets the n-th rectangle.
//
// Immutable: every method returns a new board, so the Tiler can compare before and after and a test
// can keep the old board around. A window is on at most one screen at a time; Join enforces it.
public sealed record TileBoard(IReadOnlyDictionary<int, IReadOnlyList<WindowHandle>> Screens)
{
    public static TileBoard Empty { get; } = new(new Dictionary<int, IReadOnlyList<WindowHandle>>());

    // The very first arrangement of a workspace, from where the windows already sit. Per screen they
    // are ordered by Left, then Top, which is column-major like the automatic layout's slot order, so tiling a tidy
    // layout does not shuffle it. The same as the overload below with no stored cells.
    public static TileBoard FirstFill(IEnumerable<(WindowHandle Window, int Screen, WindowRect Frame)> windows) =>
        FirstFill(windows, (_, _) => Maybe<IReadOnlyList<WindowRect>>.None);

    // The first arrangement when a screen may have a STORED shape (spec,
    // docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md, "Arrival and minimize", the first fill bullet).
    // `cells(screen, count)` answers the window rectangles of the layout stored for that screen, in slot order, when it
    // lays out exactly `count` windows; None otherwise (nothing stored, or a count that does not match), and then the
    // screen is sorted by Left then Top as before.
    //
    // Why: the sort is column-major, which is the automatic grid's slot order but not a hand-made tree's. In
    // Down[Across[A, C], B] the slot order is A, C, B, while the sort gives A, B, C (B's left edge is 0, C's is half
    // way), so B and C traded places on every restart. With the cells, each window is matched to the cell its frame
    // overlaps most, greedily: the (window, cell) pair with the largest overlap is taken first, then the next largest
    // among the windows and cells still free, and so on; ties go to the window first in Left-then-Top order, then to
    // the cell first in slot order. Windows that overlap no free cell take the cells left over, the windows in
    // Left-then-Top order, the cells in slot order. A layout that survived a restart therefore lays every window out
    // where it already is.
    public static TileBoard FirstFill(IEnumerable<(WindowHandle Window, int Screen, WindowRect Frame)> windows,
        Func<int, int, Maybe<IReadOnlyList<WindowRect>>> cells) =>
        new(windows
            .GroupBy(w => w.Screen)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var sorted = g.OrderBy(w => w.Frame.Left).ThenBy(w => w.Frame.Top).ToList();
                    return cells(g.Key, sorted.Count)
                        .Where(c => c.Count == sorted.Count)
                        .Map(c => Matched(sorted.Select(w => (w.Window, w.Frame)).ToList(), c))
                        .GetValueOrDefault(() => sorted.Select(w => w.Window).ToList());
                }));

    // The greedy match of FirstFill: `sorted` in Left-then-Top order, `cells` in slot order, one cell per window. The
    // pairs sorted by overlap (largest first, then window, then cell) and taken while both ends are still free are
    // exactly "repeatedly take the largest remaining pair".
    static IReadOnlyList<WindowHandle> Matched(IReadOnlyList<(WindowHandle Window, WindowRect Frame)> sorted, IReadOnlyList<WindowRect> cells)
    {
        var byCell = sorted
            .SelectMany((w, wi) => cells.Select((c, ci) => (Window: wi, Cell: ci, Area: Overlap(w.Frame, c))))
            .Where(p => p.Area > 0)
            .OrderByDescending(p => p.Area)
            .ThenBy(p => p.Window)
            .ThenBy(p => p.Cell)
            .Aggregate(ImmutableDictionary<int, int>.Empty, (taken, p) =>
                taken.ContainsKey(p.Cell) || taken.ContainsValue(p.Window) ? taken : taken.Add(p.Cell, p.Window));
        // The leftovers paired in order: the n-th free cell takes the n-th unmatched window.
        var unmatched = Enumerable.Range(0, sorted.Count).Where(wi => !byCell.ContainsValue(wi));
        var free = Enumerable.Range(0, cells.Count).Where(ci => !byCell.ContainsKey(ci));
        var all = byCell.AddRange(free.Zip(unmatched, (ci, wi) => new KeyValuePair<int, int>(ci, wi)));
        return Enumerable.Range(0, cells.Count).Select(ci => sorted[all[ci]].Window).ToList();
    }

    // The area two rectangles share, 0 when they do not meet. Long, so two large screens' worth cannot overflow.
    static long Overlap(WindowRect a, WindowRect b) =>
        (long)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left))
        * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    // Adds the window to the end of `screen`'s order. It leaves wherever it was first, another screen
    // or an earlier place on this one, so a window never holds two slots.
    //
    // The screen's list is read from the board AFTER the leave, not from this one: reading it before
    // kept the window's old entry, so rejoining its own screen listed it twice and the Tiler's slot
    // dictionary threw on every evaluate.
    public TileBoard Join(int screen, WindowHandle w) =>
        Leave(w) is var left ? left.With(screen, left.On(screen).Append(w).ToList()) : this;

    // Removes the window wherever it is; the windows after it move up one slot. A screen left empty
    // is dropped, so Screens never carries empty lists.
    public TileBoard Leave(WindowHandle w) =>
        new(Screens
            .Select(s => (s.Key, Windows: s.Value.Where(x => x != w).ToList()))
            .Where(s => s.Windows.Count > 0)
            .ToDictionary(s => s.Key, s => (IReadOnlyList<WindowHandle>)s.Windows));

    // Exchanges two windows' slots. Only within one screen: a slot is a place on a screen, so across
    // screens (or for a window that is not on the board) the board is returned unchanged.
    public TileBoard Swap(WindowHandle a, WindowHandle b) =>
        ScreenOf(a).TryGetValue(out var screen) && ScreenOf(b).TryGetValue(out var other) && screen == other
            ? With(screen, On(screen).Select(x => x == a ? b : x == b ? a : x).ToList())
            : this;

    // Two windows trade places, on the same screen or across two: each takes the other's screen and position.
    // Unchanged when either is not on the board.
    public TileBoard Exchange(WindowHandle a, WindowHandle b) =>
        ScreenOf(a).TryGetValue(out var sa) && ScreenOf(b).TryGetValue(out var sb)
            ? sa == sb
                ? Swap(a, b)
                : With(sa, On(sa).Select(x => x == a ? b : x).ToList()) is var first
                    ? first.With(sb, first.On(sb).Select(x => x == b ? a : x).ToList())
                    : this
            : this;

    // One screen takes `order`, which is the windows it has plus any brought over from another screen (each leaves
    // where it was). A drop beside a tile, on its own screen or dragged across (TileArrange.Drop). Unchanged unless
    // `order` holds every window the screen has, so an arrangement can never lose one.
    public TileBoard Into(int screen, IReadOnlyList<WindowHandle> order)
    {
        var arriving = order.Where(w => !On(screen).Contains(w)).ToList();
        var left = arriving.Aggregate(this, (board, w) => board.Leave(w));
        return order.Distinct().Count() == order.Count && left.On(screen).All(order.Contains)
            && order.Count == left.On(screen).Count + arriving.Count
            ? left.With(screen, order)
            : this;
    }

    // The same windows of one screen in a new order: a drop beside another tile (TileArrange.Drop). Refused, the
    // board returned unchanged, unless `order` holds exactly the windows the screen has, so a rearrangement can
    // never add or lose a window.
    public TileBoard Reordered(int screen, IReadOnlyList<WindowHandle> order) =>
        order.Count == On(screen).Count && order.ToHashSet().SetEquals(On(screen)) ? With(screen, order) : this;

    public Maybe<int> ScreenOf(WindowHandle w) =>
        Screens.Where(s => s.Value.Contains(w)).Select(s => Maybe.From(s.Key)).FirstOrDefault();

    public IReadOnlyList<WindowHandle> On(int screen) =>
        Screens.TryGetValue(screen, out var windows) ? windows : [];

    // A copy with one screen's order replaced.
    TileBoard With(int screen, IReadOnlyList<WindowHandle> windows) =>
        new(Screens
            .Where(s => s.Key != screen)
            .Append(new KeyValuePair<int, IReadOnlyList<WindowHandle>>(screen, windows))
            .ToDictionary(s => s.Key, s => s.Value));
}
