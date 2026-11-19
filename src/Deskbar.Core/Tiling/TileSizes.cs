namespace Deskbar.Core.Tiling;

// LEGACY, READ ONLY BY MIGRATION. How older builds stored one screen's tiling: the columns-of-rows grid, before
// the split tree (TileLayout). JsonPersistenceStore.MigrateTileSizes turns each stored entry into the tree
// TileLayout.FromGrid builds and empties the list, so nothing else in the app reads or writes one of these; the
// grid arithmetic that used to work on them (GridLayout, TileArrange, SplitMove, TileArrival) is gone. The type
// stays because state.json written by an older build still carries it, under Workspace.TileSizes.
//
//   Columns[c]  is column c's share of the width (column 0 is leftmost); the list sums to 1.
//   Rows[c][r]  is row r's share of column c's height (row 0 is top); each inner list sums to 1.
//   Opening     the cell (by its slot, counted column by column) kept empty for the next window, or null.
//
// The file can hold anything a hand edit put there, so the migration checks the shape before it reads one
// (JsonPersistenceStore.IsGrid) and drops what is not a grid.
//
// A record whose members are lists compares the list REFERENCES by default, so Equals and GetHashCode are
// overridden to compare CONTENTS: Workspace is a record holding these (through ScreenTileSizes), and its generated
// equality must not tell two identical loads apart.
public sealed record TileSizes(IReadOnlyList<double> Columns, IReadOnlyList<IReadOnlyList<double>> Rows)
{
    public int? Opening { get; init; }

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
}
