using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE MATRIX VIEW (Task 12, progress.md Ruling 28; Petre: "do the quadrants as in covey's book"). The toolbar's List /
// Matrix switch (remembered: AppState.TasksMatrix, also `tasks-window list|matrix`) shows Covey's time management
// matrix in place of the List:
//
//                    Urgent      Not urgent
//   Important        I           II
//   Not important    III         IV
//
// Each cell lists its quadrant's open tasks in their order (TaskViews.Matrix), with the same line as the List (the
// click, the thread, the toggles, the chip), drawn compact: the sessions, the age and the close button are left out
// for the width (Close is still in "…"). A task waiting on Petre sits on top of its own quadrant's cell, orange as
// everywhere. A cell scrolls on its own when long. Quadrant II is drawn a little stronger, a brighter edge and caption
// and a lighter fill, because it is Covey's quadrant to put first (important, not urgent).
//
// Dragging a line between cells sets the flags of the cell it lands in, within a cell it reorders, into an empty cell
// it goes to the top (TasksWindow.Drag.cs, TaskDropTarget). A waiting line is pinned there as on the bar: its toggles
// move it.
public partial class TasksWindow
{
    // The cells drawn, by quadrant: the cell's box (its caption and its scrolling lines), and the panel of its lines.
    readonly Dictionary<Quadrant, (Border Box, StackPanel Lines)> cells = [];
    bool matrixShown;

    // While the Matrix draws its lines, they use the compact columns (Line, LineColumns).
    bool compactLines;

    static readonly Brush CellEdge = Frozen(0xFF, 0x33, 0x35, 0x3C);
    static readonly Brush StrongCellEdge = Frozen(0xFF, 0x6B, 0x70, 0x7C);
    static readonly Brush CellFill = Frozen(0xFF, 0x1B, 0x1C, 0x21);
    static readonly Brush StrongCellFill = Frozen(0xFF, 0x22, 0x24, 0x2A);
    const string StrongTag = "matrix-strong";

    // The compact columns of a Matrix line: tick, title, flags, (no status chip, no sessions, no assignee, no age), chip, comments, (no action), "…".
    // 178 DIP of fixed columns, so a line in a cell keeps a title of 100 DIP or more at 780 DIP (TasksPanelLayout.MatrixFrom).
    static readonly IReadOnlyList<GridLength> CompactColumns =
        [new(22), new(1, GridUnitType.Star), new(34), new(0), new(0), new(0), new(0), new(64), new(34), new(0), new(24)];

    // THE WINDOW USED TO GROW FOR THE MATRIX HERE (FitWidthToView, GrowForMatrix, GiveBackListWidth, listNormal,
    // grownNormal, ListMinWidth and MatrixMinWidth as the window's MinWidth): two cells side by side need about 780 DIP,
    // so the floating window widened itself while the Matrix showed and gave the List its width back afterwards. The
    // docked panel never widens anything (spec 2026-11-23-tasks-panel-design.md, "The Matrix"), so all of it was removed
    // by the tasks panel plan (docs/superpowers/plans/2026-11-23-tasks-panel.md, Task 6).
    //
    // ONLY WHEN WIDE (spec "The Matrix", Petre's pick "only when wide"; the plan's Task 8). Two cells side by side need
    // TasksPanelLayout.MatrixFrom (780 DIP), so the List / Matrix switch shows only while the panel is at least that wide
    // (the view's MatrixAllowed, from the panel's saved width). Narrower, the List shows, which already groups the tasks
    // under the quadrants I to IV, and the switch is hidden. AppState.TasksMatrix is never changed by the width: the
    // controller shows the Matrix only when the setting asks AND the width allows (TasksBoard.View), so a panel narrowed
    // on the Matrix and widened again comes back to it, and one opened narrow opens on the List.
    void PaintViewSwitch(TasksView view)
    {
        var shown = view.MatrixAllowed ? Visibility.Visible : Visibility.Collapsed;
        // Outlined like a filter chip, the one on show.
        if (listSwitch is not null)
        {
            listSwitch.Visibility = shown;
            listSwitch.BorderBrush = view.Matrix ? Brushes.Transparent : PickedEdge;
        }
        if (matrixSwitch is not null)
        {
            matrixSwitch.Visibility = shown;
            matrixSwitch.BorderBrush = view.Matrix ? PickedEdge : Brushes.Transparent;
        }
    }

    // For the tests: whether the Matrix is on show, where a quadrant's cell sits (row, column), which cell holds a
    // task, whether a cell is drawn the stronger way, and a cell's centre in the drag's coordinates.
    internal bool MatrixShown => matrixShown;
    internal (int Row, int Column) CellPlace(Quadrant quadrant) => (Grid.GetRow(cells[quadrant].Box) - 1, Grid.GetColumn(cells[quadrant].Box) - 1);
    internal Quadrant CellOf(Guid id) =>
        cells.First(cell => cell.Value.Lines.Children.OfType<Button>().Any(b => b.Tag is Guid tag && tag == id)).Key;
    internal ScrollViewer CellScroller(Quadrant quadrant) => (ScrollViewer)cells[quadrant].Lines.Parent;
    internal bool CellStronger(Quadrant quadrant) => Equals(cells[quadrant].Box.Tag, StrongTag);
    internal Point CellCentre(Quadrant quadrant) =>
        cells[quadrant].Box.TranslatePoint(new Point(cells[quadrant].Box.ActualWidth / 2, cells[quadrant].Box.ActualHeight / 2), DragSurface);

    // The grid: a row of axis words over the two columns, a narrow column of axis words beside the two rows, and the
    // four cells. Every cell is drawn, empty or not, so each quadrant has a place to drop into.
    void DrawMatrix(TasksView view)
    {
        MatrixGrid.Children.Clear();
        MatrixGrid.RowDefinitions.Clear();
        MatrixGrid.ColumnDefinitions.Clear();
        // EACH CELL KEEPS ITS SCROLL ACROSS A REDRAW (whole-PR review, I-1). A redraw builds the cells anew, so every cell
        // came back scrolled to the top on any change: a comment on another task, a status line. The offsets the last
        // cells had are kept by quadrant and given to the new cells' scrollers, which clamp them to what is there.
        var offsets = cells.ToDictionary(c => c.Key, c => ((ScrollViewer)c.Value.Lines.Parent).VerticalOffset);
        cells.Clear();
        new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) }.ToList()
            .ForEach(height => MatrixGrid.RowDefinitions.Add(new RowDefinition { Height = height }));
        new[] { new GridLength(20), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) }.ToList()
            .ForEach(width => MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }));
        Place(Axis("Urgent", rotated: false), 0, 1);
        Place(Axis("Not urgent", rotated: false), 0, 2);
        Place(Axis("Important", rotated: true), 1, 0);
        Place(Axis("Not important", rotated: true), 2, 0);

        compactLines = true;
        try
        {
            view.Cells.ToList().ForEach(cell => Place(Cell(cell, view), 1 + (cell.Quadrant.IsImportant() ? 0 : 1), 1 + (cell.Quadrant.IsUrgent() ? 0 : 1)));
            offsets.Where(o => cells.ContainsKey(o.Key)).ToList().ForEach(o => CellScroller(o.Key).ScrollToVerticalOffset(o.Value));
        }
        finally
        {
            compactLines = false;
        }
    }

    void Place(UIElement part, int row, int column)
    {
        Grid.SetRow(part, row);
        Grid.SetColumn(part, column);
        MatrixGrid.Children.Add(part);
    }

    static TextBlock Axis(string text, bool rotated) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        Foreground = HeaderInk,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = rotated ? new Thickness(0) : new Thickness(0, 2, 0, 4),
        LayoutTransform = rotated ? new RotateTransform(-90) : Transform.Identity,
    };

    // One cell: its caption, then its lines in a scroller of their own.
    Border Cell(TaskCellView cell, TasksView view)
    {
        var strong = cell.Quadrant == Quadrant.II;
        var lines = new StackPanel { Background = Brushes.Transparent, MinHeight = 24 };
        cell.Rows.ToList().ForEach(row =>
        {
            closing[row.Id] = row.Closing;
            lines.Children.Add(Line(row, view));
            if (row.Expanded) lines.Children.Add(Thread(row));
        });
        var body = new DockPanel();
        var caption = new TextBlock
        {
            Tag = "task-group",
            Text = cell.Header.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = strong ? Ink : HeaderInk,
            Margin = new Thickness(8, 6, 8, 3),
        };
        DockPanel.SetDock(caption, Dock.Top);
        body.Children.Add(caption);
        body.Children.Add(new ScrollViewer
        {
            Content = lines,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });
        var box = new Border
        {
            Tag = strong ? StrongTag : null,
            BorderBrush = strong ? StrongCellEdge : CellEdge,
            BorderThickness = new Thickness(1),
            Background = strong ? StrongCellFill : CellFill,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(3),
            Child = body,
        };
        cells[cell.Quadrant] = (box, lines);
        // A press in the cell makes it the active one, whose flags a new task takes (TasksWindow.AddFlags.cs).
        WireActiveCell(box, cell.Quadrant);
        return box;
    }
}
