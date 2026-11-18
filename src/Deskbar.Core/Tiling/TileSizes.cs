using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tiling;

// How much of one screen's work area each column and each row of the tiling grid takes, as
// fractions. Petre: "i want to be able to resize tiles by dragging the separation" and "keep my
// sizes", so these outlive the window count changing.
//
//   Columns[c]  is column c's share of the width (column 0 is leftmost); the list sums to 1.
//   Rows[c][r]  is row r's share of column c's height (row 0 is top); each inner list sums to 1.
//
// Sizes belong to PLACES, never to windows: when a window closes or joins, the places stay and
// Fit() reshapes the fractions to the new grid shape using the rules below.
//
// A record whose members are lists compares the list REFERENCES by default, so Equals and
// GetHashCode are overridden to compare CONTENTS. Callers compare a freshly fitted size with the
// stored one so an unchanged size is not written again.
public sealed record TileSizes(IReadOnlyList<double> Columns, IReadOnlyList<IReadOnlyList<double>> Rows)
{
    // Two fractions closer than this are the same (also what "even" means for IsEqual).
    const double Tolerance = 1e-9;

    // Every column and every row the same size: what the grid gave before sizes existed.
    // shape[c] is the number of rows in column c.
    public static TileSizes Equal(IReadOnlyList<int> shape) =>
        new(EvenList(shape.Count), shape.Select(EvenList).ToList());

    // How many rows each column holds: the grid SHAPE these sizes describe. Usually the automatic one
    // (GridLayout.Shape), but a drop into a tile's edge zone (TileArrange) can make any other.
    public IReadOnlyList<int> Shape => Rows.Select(r => r.Count).ToList();

    // AN OPENING: the cell (by its slot, counted column by column like every slot) that holds no window. Petre: "if i
    // resize a window in a tiled setting, i want to keep the opening for the new window to jump into". Pulling a
    // tile's OUTER edge in used to snap it back; now it leaves an empty cell there (TileArrange.Open), and the next
    // window to join that screen goes into it (TileArrange.Filled). The sizes then describe one cell more than the
    // screen has windows. Null for none, and stored with the sizes, so the opening outlives a restart.
    public int? Opening { get; init; }

    // These sizes lay out `count` windows around their opening: there is one, and exactly one cell more than windows.
    public bool HasOpeningFor(int count) =>
        Opening is { } open && Shape.Sum() == count + 1 && open >= 0 && open <= count;

    // True when every list is even, within the tolerance.
    public bool IsEqual =>
        IsEven(Columns) && Rows.All(IsEven);

    // Reshapes these sizes to a new grid shape (shape[c] = rows in column c).
    // `leaving` is the (column, row) the departing window occupied in the OLD grid, when known.
    //
    // The rules:
    //   - a leaving window takes its own row's fraction, and the rest share it in proportion;
    //   - a joining window adds a row at the bottom with an equal share (1/n), the rest scale
    //     in proportion;
    //   - when columns drop, the rightmost goes and the rest share its fraction in proportion;
    //   - when columns rise, the new column is added on the right with 1/n, the rest scale in
    //     proportion;
    //   - a row count that drops with no known leaver drops the bottom rows.
    // Fit lays out windows only, so the result never has an opening (TileArrange.Refit closes one before it fits).
    public TileSizes Fit(IReadOnlyList<int> shape, Maybe<(int Column, int Row)> leaving) =>
        new(
            Resize(Columns, shape.Count, Maybe<int>.None),
            shape
                .Select((rows, column) => column < Rows.Count
                    // The leaver only applies to the column it stood in.
                    ? Resize(Rows[column], rows, LeaverRow(leaving, column))
                    // A column that did not exist before starts even.
                    : EvenList(rows))
                .ToList());

    public bool Equals(TileSizes? other) =>
        other is not null
        && Opening == other.Opening
        && Columns.SequenceEqual(other.Columns)
        && Rows.Count == other.Rows.Count
        && Rows.Zip(other.Rows, (a, b) => a.SequenceEqual(b)).All(same => same);

    public override int GetHashCode() =>
        Rows.Aggregate(
            Columns.Aggregate(Seeded(Opening), (hash, c) => { hash.Add(c); return hash; }),
            (hash, row) => row.Aggregate(hash, (h, r) => { h.Add(r); return h; }))
            .ToHashCode();

    static HashCode Seeded(int? opening)
    {
        var hash = new HashCode();
        hash.Add(opening);
        return hash;
    }

    static IReadOnlyList<double> EvenList(int count) =>
        Enumerable.Repeat(1.0 / count, count).ToList();

    static bool IsEven(IReadOnlyList<double> list) =>
        list.All(x => Math.Abs(x - 1.0 / list.Count) < Tolerance);

    static Maybe<int> LeaverRow(Maybe<(int Column, int Row)> leaving, int column) =>
        leaving.HasValue && leaving.Value.Column == column ? leaving.Value.Row : Maybe<int>.None;

    // Brings one list of fractions to `target` entries, one step at a time. Only the first
    // removal can use the known leaver; later ones (several windows gone at once) drop the last.
    static IReadOnlyList<double> Resize(IReadOnlyList<double> list, int target, Maybe<int> leaver) =>
        list.Count == 0 ? EvenList(target)
        : list.Count > target ? Resize(Remove(list, leaver.HasValue && leaver.Value < list.Count ? leaver.Value : list.Count - 1), target, Maybe<int>.None)
        : list.Count < target ? Resize(Add(list), target, Maybe<int>.None)
        : list;

    // Removes entry `index` and gives its share to the rest in proportion (they sum to 1 again).
    static IReadOnlyList<double> Remove(IReadOnlyList<double> list, int index)
    {
        var rest = list.Where((_, i) => i != index).ToList();
        var total = rest.Sum();
        return rest.Count == 0 ? rest : total <= 0 ? EvenList(rest.Count) : rest.Select(x => x / total).ToList();
    }

    // Appends an entry with an equal share (1/n); the others scale down in proportion.
    static IReadOnlyList<double> Add(IReadOnlyList<double> list)
    {
        var n = list.Count + 1;
        return list.Select(x => x * (n - 1) / n).Append(1.0 / n).ToList();
    }
}
