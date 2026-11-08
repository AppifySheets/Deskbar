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
    // are ordered by Left, then Top, which is column-major like the slot order, so tiling a tidy
    // layout does not shuffle it.
    public static TileBoard FirstFill(IEnumerable<(WindowHandle Window, int Screen, WindowRect Frame)> windows) =>
        new(windows
            .GroupBy(w => w.Screen)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<WindowHandle>)g
                    .OrderBy(w => w.Frame.Left)
                    .ThenBy(w => w.Frame.Top)
                    .Select(w => w.Window)
                    .ToList()));

    // Adds the window to the end of `screen`'s order. It leaves wherever it was first, another screen
    // or an earlier place on this one, so a window never holds two slots.
    public TileBoard Join(int screen, WindowHandle w) =>
        Leave(w).With(screen, On(screen).Append(w).ToList());

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
