using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// FLAGS WHEN ADDING (Ruling 37; Petre: "i should be able to say urgent or important or both when adding") and THE
// MATRIX'S ACTIVE CELL (Petre: "actually use the box which is active when adding through the matrix").
//
// The add row carries the two toggles a task line has, important and urgent, beside the box and before the workspace
// chip. They say what the new task will be, so Petre picks neither, either or both before pressing Enter, and the new
// task lands at the top of the quadrant its flags give (TaskBoard.AddNote).
//
//   In the List   the toggles are the row's own: off when the row opens, flipped by a click, off again after an add
//                 (and after Esc, which closes the row).
//   In the Matrix the toggles ARE the active cell: the quadrant Petre last clicked in, or the one holding the task he
//                 selected, quadrant II until either has happened (Covey's quadrant to put first). The active cell
//                 wears a blue edge. Flipping a toggle moves the active cell to the quadrant the flags now give, and
//                 clicking in a cell turns the toggles to its flags, so the two always agree. A new task lands at the
//                 top of the active cell.
//
// Nothing here redraws the lines: the toggles are rebuilt with the add row's chip (PaintAddPlace) and the cells' edges
// are repainted in place (PaintActiveCell), so a click in a cell never moves a line under the pointer.
public partial class TasksWindow
{
    // The List's toggles.
    bool addImportant;
    bool addUrgent;

    // The Matrix's active cell, none until Petre clicks in a cell or selects a task there.
    Quadrant? activeCell;

    static readonly Brush ActiveCellEdge = Frozen(0xFF, 0x4F, 0x8C, 0xFF);

    /// <summary>The Matrix's active cell: the one last clicked in or holding the selected task, quadrant II at first.</summary>
    internal Quadrant ActiveCell => activeCell ?? Quadrant.II;

    /// <summary>The flags a note added now gets, as the quadrant they make.</summary>
    internal Quadrant AddQuadrant => matrixShown ? ActiveCell : QuadrantOf.Flags(addImportant, addUrgent);

    /// <summary>For the tests: the cell drawn with the active edge, if any.</summary>
    internal Quadrant? HighlightedCell =>
        cells.Where(c => ReferenceEquals(c.Value.Box.BorderBrush, ActiveCellEdge)).Select(c => (Quadrant?)c.Key).FirstOrDefault();

    // The two toggles, as on a task line: the glyph in its colour when on, dim when off; the automation status says
    // which. Unfocusable (Flat), so the box keeps the keyboard.
    IEnumerable<Button> AddFlagToggles()
    {
        var quadrant = AddQuadrant;
        return new[]
        {
            (Flag: TaskFlag.Important, Name: "Add as important", Glyph: "\uE8C9", On: quadrant.IsImportant(), Ink: ImportantInk),
            (Flag: TaskFlag.Urgent, Name: "Add as urgent", Glyph: "\uE823", On: quadrant.IsUrgent(), Ink: UrgentInk),
        }.Select(flag =>
        {
            var toggle = Flat(new TextBlock { Text = flag.Glyph, FontFamily = IconFont, FontSize = 12, Foreground = flag.On ? flag.Ink : DimInk },
                flag.Name, () => ToggleAddFlag(flag.Flag), new Thickness(5, 2, 5, 2));
            toggle.Background = Brushes.Transparent;
            toggle.ToolTip = flag.Name;
            toggle.Margin = new Thickness(4, 0, 0, 0);
            AutomationProperties.SetItemStatus(toggle, flag.On ? "on" : "off");
            return toggle;
        }).ToArray();
    }

    // A toggle flipped: the List's own flags, or in the Matrix the active cell, which moves to the quadrant the flags
    // now make.
    void ToggleAddFlag(TaskFlag flag)
    {
        var now = AddQuadrant;
        var next = now.With(flag, !(flag == TaskFlag.Important ? now.IsImportant() : now.IsUrgent()));
        if (matrixShown) activeCell = next;
        else (addImportant, addUrgent) = (next.IsImportant(), next.IsUrgent());
        PaintActiveCell();
        PaintAddPlace();
        if (AddBoxShown) AddBox.Focus();
    }

    // A press anywhere in a cell makes it the active one (its own lines, buttons and empty space alike: a preview
    // handler, so a line's button that handles the click still counts).
    void WireActiveCell(Border box, Quadrant quadrant) =>
        box.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => MakeActiveCell(quadrant)), handledEventsToo: true);

    /// <summary>For the tests: a left-button press routed to a cell's box, as a click anywhere in the cell makes.</summary>
    internal void PressInCell(Quadrant quadrant) =>
        cells[quadrant].Box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent });

    void MakeActiveCell(Quadrant quadrant)
    {
        if (activeCell == quadrant) return;
        activeCell = quadrant;
        PaintActiveCell();
        PaintAddPlace();
    }

    // The selected task's cell becomes the active one (the selection is a click on a line, or a pick in the bar).
    void FollowSelectionToCell(Guid selected)
    {
        if (!matrixShown) return;
        var cell = latest?.Cells.FirstOrDefault(c => c.Rows.Any(r => r.Id == selected));
        if (cell is not null) MakeActiveCell(cell.Quadrant);
    }

    // Every cell's edge as it should be: the active one blue, the others their own (quadrant II a little stronger).
    void PaintActiveCell() =>
        cells.ToList().ForEach(c => c.Value.Box.BorderBrush = matrixShown && c.Key == ActiveCell ? ActiveCellEdge
            : c.Key == Quadrant.II ? StrongCellEdge : CellEdge);

    // After an add, and when the row closes, the List's toggles are off again. The Matrix keeps its active cell.
    void ResetAddFlags() => (addImportant, addUrgent) = (false, false);
}
