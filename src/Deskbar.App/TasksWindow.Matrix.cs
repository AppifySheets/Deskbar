using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

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

    // The compact columns of a Matrix line: tick, title, flags, (no sessions, no age), chip, comments, (no action), "…".
    // 178 DIP of fixed columns, so a line in a cell keeps a title of 100 DIP or more at MatrixMinWidth.
    static readonly IReadOnlyList<GridLength> CompactColumns =
        [new(22), new(1, GridUnitType.Star), new(34), new(0), new(0), new(64), new(34), new(0), new(24)];

    // THE WINDOW'S WIDTH FOLLOWS THE VIEW (review of 4fc4441..a1acd60, item 1, and of the fix round, I-1). Two cells side
    // by side, each with its own scroll bar, leave a line half the List's width: at the List's 560 DIP the title got
    // 9.8 DIP, and 80 at 720. So while the Matrix shows, the window is at least MatrixMinWidth wide, which keeps the
    // title about 100 DIP with the chip and the toggles whole.
    //
    // THE LIST'S WIDTH IS PETRE'S, AND IT COMES BACK. The first version only raised MinWidth: the window grew to the
    // right, off a screen it sat flush against (measured 147 DIP off), stayed 780 wide back on the List, and Dismiss
    // saved 780, so one look at the Matrix lost the List's width for good. Now growing for the Matrix notes the List's
    // normal rectangle first (listNormal), grows the window in device pixels and moves it left as far as needed to stay
    // inside its screen's work area, and notes what it grew to (grownNormal). Back on the List, a window nobody resized
    // meanwhile gets the List's rectangle back (only its size, when it was moved meanwhile); one Petre resized keeps his
    // size. While grown and not resized, the place remembered (CurrentPlace, hence Dismiss and the settle timer) is the
    // List's, so state.json never learns the Matrix's width.
    const double ListMinWidth = 560;
    const double MatrixMinWidth = 780;

    Rect? listNormal;
    Rect grownNormal = Rect.Empty;

    void FitWidthToView(bool matrix)
    {
        if (!matrix)
        {
            // Lowered first, or WPF would hold the window at the Matrix's minimum while the List's width comes back.
            MinWidth = ListMinWidth;
            if (listNormal is not null) GiveBackListWidth();
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        // Only a window on screen in its normal state is grown: a hidden one is placed by Open first (which calls this
        // again), and a maximized one already has the width.
        var live = IsVisible && handle != 0 && WindowState == WindowState.Normal;
        if (live && listNormal is null) GrowForMatrix(handle);
        MinWidth = live ? MatrixMinWidth : ListMinWidth;
    }

    void GrowForMatrix(nint handle)
    {
        NoteNormal();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var want = (int)Math.Ceiling(MatrixMinWidth * scale);
        if (lastNormal.IsEmpty || lastNormal.Width >= want) return;
        listNormal = lastNormal;
        var work = WorkAreaOf(handle);
        var left = work.IsEmpty ? lastNormal.Left : Math.Max(work.Left, Math.Min(lastNormal.Left, work.Right - want));
        SetDeviceRect(new Rect(left, lastNormal.Top, want, lastNormal.Height), scale);
        grownNormal = lastNormal;
    }

    void GiveBackListWidth()
    {
        NoteNormal();
        var back = ListRect();
        listNormal = null;
        if (back != lastNormal) SetDeviceRect(back, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    // The rectangle that is the List's while the Matrix has grown the window (see above): the List's own when the
    // window is as it was grown, its size at the window's new place when it was only moved, and the window's own once
    // it was resized.
    Rect ListRect() =>
        listNormal is not { } list || lastNormal.Size != grownNormal.Size ? lastNormal
        : lastNormal.Location == grownNormal.Location ? list
        : new Rect(lastNormal.Left, lastNormal.Top, list.Width, list.Height);

    // Puts the window at a device rectangle and keeps WPF's own Width in step, so a later layout does not size it back.
    void SetDeviceRect(Rect rect, double scale)
    {
        PlaceDevice(new TasksWindowState((int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height, Maximized: false));
        Width = rect.Width / scale;
        NoteNormal();
    }

    static Rect WorkAreaOf(nint handle)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        return NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)
            ? new Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top)
            : Rect.Empty;
    }

    // For the tests: the window's rectangle as Windows has it, in device pixels, and the primary screen's work area.
    internal Rect DeviceRect =>
        NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)
            ? new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : Rect.Empty;

    internal static double PrimaryScale()
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _);
        return dpiX / 96.0;
    }

    internal static Rect PrimaryWorkArea()
    {
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return NativeMethods.GetMonitorInfo(monitor, ref info)
            ? new Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top)
            : Rect.Empty;
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
        return box;
    }
}
