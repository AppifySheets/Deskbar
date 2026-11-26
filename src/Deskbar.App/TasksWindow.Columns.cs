using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE LIST AS A GRID WITH RESIZABLE COLUMNS (task 3df2d28f, "use space more wisely"). Petre's screenshot: at a 740 DIP
// panel the title wrapped at about 140 DIP while the fixed columns beside it (560 DIP of them) were mostly air. His
// answers to the mockup: "let's make it a grid, resizeable", then "buttons shouldn't take so much space" and "arrange
// them so that we're conserving space and putting as much text as we can". So, on a full line of the List:
//
//   tick | title (the rest) | Status | Sessions | Who | Age | Workspace | flags, comments, Open Claude, "⋯"
//
//   - the five text columns have the widths in TaskColumnWidths (Core), which start narrow and are set by dragging the
//     dividers in the header row above the list (ColumnHeader in the XAML), and are saved with the panel;
//   - the icon buttons, the flag toggles included, sit together at the right end, each column as narrow as its glyph,
//     and a button column no line on show uses (Reopen on a list with nothing closed) is zero wide. The toggles used to
//     sit right after the title in a 56 DIP column of their own, invisible until hover most of the time.
//
// The slim line (a narrow panel) takes the same order and the same button widths but has no header: it has two text
// columns only, the squares and the age, sized as before. The Matrix's compact line keeps its own order and widths
// (CompactColumns): its cells are narrow and it has none of the text columns.
//
// The header is built once (BuildColumnHeader) and never rebuilt by a redraw, so a redraw cannot pull a divider from
// under a drag. A drag resizes every full line on show live, by setting their column widths in place (ApplyColumns),
// and only its end hands the widths to the controller to save (TasksWindowActions.ResizeColumns).
public partial class TasksWindow
{
    // What a line is made of, one column each. The List (full and slim lines) and the Matrix put them in different orders
    // (ListOrder below, and this enum's own order for the Matrix), so a line asks At for a part's column.
    enum Part { Tick, Title, Flags, Status, Sessions, Assignee, Age, Place, Comments, Action, More }

    // The List's order: the text columns first, then every button together at the right end.
    static readonly IReadOnlyList<Part> ListOrder =
        [Part.Tick, Part.Title, Part.Status, Part.Sessions, Part.Assignee, Part.Age, Part.Place, Part.Flags, Part.Comments, Part.Action, Part.More];

    // A part's column on the line being drawn: the Matrix's compact line keeps the enum's order, the List the one above.
    int At(Part part) => compactLines ? (int)part : ListOrder.ToList().IndexOf(part);

    // The resizable text columns, by their column on a List line (2 to 6, after the tick and the title).
    static int ColumnIndexOf(TaskColumn column) => ListOrder.ToList().IndexOf(column switch
    {
        TaskColumn.Status => Part.Status,
        TaskColumn.Sessions => Part.Sessions,
        TaskColumn.Assignee => Part.Assignee,
        TaskColumn.Age => Part.Age,
        _ => Part.Place,
    });

    // THE BUTTON COLUMNS, each as narrow as what it holds. Every small button is a Flat button, which has a 1 DIP border
    // on each side (Flat's BorderThickness) around its padding and its glyph (12 DIP in the icon font). So a small button
    // is 12 + 4 + 4 of padding (Small) + 2 of border = 22, and a flag toggle 12 + 2 + 2 of padding (FlagsOf, SnoozeButton)
    // + 2 of border = 18, three of them with the snooze moon.
    //
    // THE BORDER WAS LEFT OUT once (task 735abbf5, Petre's third report of the toggles, after "snooze and others not
    // showing?" and "lots of horizontal space for buttons, still hidden"). The columns were 16 a toggle and 20 a button,
    // so the three toggles needed 54 DIP in a 48 DIP column and the grid cut the last 6 off: the right half of the snooze
    // moon, its outer arc, leaving a hook the eye read as a broken glyph. A small button lost 2 DIP of padding, no ink.
    // The widths are worked out from the parts now, and a window test (A_line_s_buttons_fit_their_columns) fails if a
    // button outgrows its column again.
    //
    // The comments column is the glyph alone, or the glyph and its count (3 DIP apart, about 7 DIP a digit at 11 DIP),
    // as wide as the widest count on show. The action column is Reopen's width while a closed task is on show (full
    // lines only), else Open Claude's glyph while a Claude task is (two glyphs on a full line while one has Reevaluate
    // beside it, ClaudeColumnFor), else nothing.
    const double ButtonBorder = 1;
    const double ButtonGlyph = 12;
    const double ButtonColumn = ButtonGlyph + 2 * 4 + 2 * ButtonBorder;
    const double ToggleWidth = ButtonGlyph + 2 * 2 + 2 * ButtonBorder;
    const double FlagsColumn = 3 * ToggleWidth;
    // A Matrix line has the two flags only (no snooze there, SnoozeButton). It was 34, cutting 2 DIP off the clock.
    const double CompactFlagsColumn = 2 * ToggleWidth;
    const double ReopenColumn = 46;
    // Open Claude is a small button like the others (OpenClaudeButton).
    const double OpenClaudeColumn = ButtonColumn;

    // The Claude buttons' width: Open Claude alone, or Open Claude and Reevaluate side by side while a line on show has both.
    double ClaudeColumnFor(IEnumerable<TaskRowView> rows) =>
        rows.Any(ReevaluatesOnLine) ? 2 * OpenClaudeColumn : OpenClaudeColumn;

    static double CommentsColumnFor(IEnumerable<TaskRowView> rows) =>
        rows.Select(r => r.CommentCount > 0 ? ButtonColumn + 3 + 7 * $"{r.CommentCount}".Length : ButtonColumn).DefaultIfEmpty(ButtonColumn).Max();

    // The widths of this draw's button columns, worked out from the lines on show (DrawRows), and the text columns'
    // widths in force: the saved ones, or the ones a divider drag is setting.
    GridLength commentsWidth = new(ButtonColumn);
    GridLength actionWidth = new(0);

    // The flags column: the two toggles and the snooze moon, and a fourth toggle's width while a line on show repeats (task
    // 0b78269e, Task 9: the repeat mark, RepeatMark, takes a toggle's width). Worked out per draw like the comments column,
    // so the title keeps its room on every list with no recurring task, which is nearly every one.
    GridLength flagsWidth = new(FlagsColumn);

    // Whether this draw's slim lines lead their strip with a Reevaluate slot (SizeColumns; task 3dd07539).
    bool reevaluateLeads;
    TaskColumnWidths columnWidths = TaskColumnWidths.Default;

    // Works out the button columns for the lines about to be drawn; the text columns follow the view unless a divider is
    // being dragged, whose widths win until it is let go.
    void SizeColumns(TasksView view, IReadOnlyList<TaskRowView> shownRows)
    {
        commentsWidth = new(CommentsColumnFor(shownRows));
        flagsWidth = new(FlagsColumn + (shownRows.Any(r => r.RepeatLabel is not null) ? ToggleWidth : 0));
        var full = !view.Slim && !view.Matrix;
        // Start Claude, on a task no session has joined, takes the same column as Open Claude (task 59b8f613).
        // Reevaluate beside Open Claude (task 3dd07539): on a full line it makes this column two buttons wide while any
        // line on show has it. A slim line has no room for that at the narrowest panel (its controls are fixed columns, and
        // "⋯" was pushed past the line's end), so there Reevaluate leads the strip of the menu's actions instead, which
        // starts right after this column and fits itself to the room left (reevaluateLeads, LineButtons).
        var claudeColumn = full ? ClaudeColumnFor(shownRows) : OpenClaudeColumn;
        reevaluateLeads = view is { Slim: true, Matrix: false } && shownRows.Any(ReevaluatesOnLine);
        actionWidth = new(full && shownRows.Any(r => r.Status == TaskStatus.Closed && !r.Closing) ? Math.Max(ReopenColumn, claudeColumn)
            : shownRows.Any(r => OpensClaude(r) || StartsClaude(r)) ? claudeColumn
            : 0);
        if (columnDrag is null) columnWidths = view.Columns;
    }

    // A full line's columns, in ListOrder: the tick, the title (the star, which wraps), the five text columns, the buttons.
    // The last holds the menu's actions as icon buttons and "⋯" after them (task 59b8f613, TasksWindow.LineButtons.cs). It
    // is a star too, with a weight that gives it all the room first, but capped at what the seven and "⋯" need, and the
    // title keeps FullTitleFloor before it gets any (ConstrainFullColumns).
    //
    // THE TITLE OUTRANKS THE MENU BUTTONS (task 39d58e5b, Petre's pick of option B). The floor was 112 DIP, so at the
    // narrowest full line (then 710 DIP) the strip kept its buttons and the title got about 100 DIP: four or five lines a
    // title, while a slim line one DIP narrower read it in one ("wider bar renders worse than narrow"). The floor is now
    // the readable width, so on a narrow full line the strip gives up its buttons, down to "⋯" alone, before the title
    // goes under it, and the full line starts only where the title and "⋯" both fit (TasksPanelLayout.SlimBelow). Wider,
    // the strip takes its buttons back first, all seven from about 940 to 970 DIP (by the comment counts and the action
    // column on show), and then the title grows.
    IReadOnlyList<GridLength> FullColumns() =>
        [new(22), new(1, GridUnitType.Star), .. columnWidths.InOrder.Select(w => new GridLength(w)), flagsWidth, commentsWidth, actionWidth,
            new(1000, GridUnitType.Star)];

    // The title's floor on a full line, and the strip's cap: the two limits FullColumns' two stars are drawn within. Set on
    // every full line's grid and on the header's, so the header's columns stay over the lines'. The readable width for
    // the title's text (TasksPanelLayout.FullTitleReadable, 320 DIP, task 39d58e5b; it was 100), plus the 10 DIP of
    // margin its block has inside the column (TextOf: 2 left, 8 right), plus 2 to spare.
    const double FullTitleFloor = TasksPanelLayout.FullTitleReadable + 12;

    //
    // THE FLOOR GIVES WAY TO "⋯" (task 39d58e5b review). A hard 332 DIP floor pushed the line's right end off it whenever
    // the fixed columns left less than 332 + 22: with the scroll bar, a closed task's Reopen and two-digit comment counts
    // on show at 850 DIP; on a subtask's full line inside its parent's thread, 18 DIP narrower than the header; and with
    // text columns saved wide by a drag before the floor rose. WPF keeps a star column's MinWidth even when the grid has
    // no room for it, so the columns after it ran past the line's edge and "⋯" was clipped. So each grid works out its own
    // floor from its own width (FitTitleFloor): FullTitleFloor where the room allows, less where it does not, never so
    // much that "⋯" leaves the line. It is worked out again when the grid's width changes and when a divider drag or a
    // reset changes the text columns (ApplyColumns). The header's grid hooks its size once in BuildColumnHeader.
    void ConstrainFullColumns(Grid grid)
    {
        grid.ColumnDefinitions[At(Part.More)].MaxWidth = ButtonColumn * (LineButtonSlots + 1);
        FitTitleFloor(grid);
        if (!ReferenceEquals(grid, ColumnHeader)) grid.SizeChanged += (_, _) => FitTitleFloor(grid);
    }

    // The title's floor for one full-line grid: what its width leaves after the fixed columns and one button for "⋯",
    // capped at FullTitleFloor. Before the grid is laid out (width 0) the floor is FullTitleFloor; the first layout's
    // SizeChanged sets the real one.
    void FitTitleFloor(Grid grid)
    {
        var fixedColumns = grid.ColumnDefinitions.Where(c => c.Width.IsAbsolute).Sum(c => c.Width.Value);
        var room = grid.ActualWidth - fixedColumns - ButtonColumn;
        // By ListOrder, not At: a SizeChanged can arrive on a line grid after a later draw switched to the Matrix.
        grid.ColumnDefinitions[ListOrder.ToList().IndexOf(Part.Title)].MinWidth =
            grid.ActualWidth > 0 ? Math.Clamp(room, 0, FullTitleFloor) : FullTitleFloor;
    }

    // A slim line's columns, in ListOrder: no status, no assignee; the squares, the age and the workspace chip; the buttons.
    IReadOnlyList<GridLength> SlimColumns() =>
        [new(22), new(1, GridUnitType.Star), new(0), slimSquaresWidth, new(0), new(SlimAgeColumn), new(columnWidths.Workspace),
            flagsWidth, commentsWidth, actionWidth, new(ButtonColumn)];

    // Every full line's grid on show, with the header's, so a divider drag can resize them in place. Emptied by each draw.
    readonly List<Grid> fullGrids = [];

    // A divider drag under way: which column, the pointer's x over the header at the press, the widths then and the room
    // the line could give up then (TaskColumnWidths.Dragged caps the growth with it, RoomToGive). Null while none is.
    (TaskColumn Column, double X, TaskColumnWidths Widths, double Room)? columnDrag;

    // What a column may grow by, read off the header (whose columns are the lines'): the title's width over its floor
    // plus the menu strip's over "⋯" alone. Both stars give it up as the column grows (the strip first, its weight being
    // the larger), so the drag stops where the title is at its floor and "⋯" still on the line. Before task 39d58e5b it
    // was the title's width less 80 DIP, which let a drag squeeze the title to 80.
    double RoomToGive() =>
        Math.Max(0, ColumnHeader.ColumnDefinitions[At(Part.Title)].ActualWidth - FullTitleFloor)
        + Math.Max(0, ColumnHeader.ColumnDefinitions[At(Part.More)].ActualWidth - ButtonColumn);

    // The left inset of a line's grid from the list's edge, and its right one, so the header's columns sit exactly over
    // the lines': the tile's inset (TileInset), the selection frame's 2 DIP and Claude's 3 DIP edge on the left, then the
    // edge's padding (4 left, 8 right; Line). The scroll bar's width is added on the right while it shows (FollowScrollBar).
    const double GridLeftInset = TileInset + 2 + 3 + 4;
    const double GridRightInset = TileInset + 8;

    // The header's labels, by column on a List line. None over the tick and the buttons.
    static readonly IReadOnlyList<(Part Part, string Text)> HeaderLabels =
        [(Part.Title, "Task"), (Part.Status, "Status"), (Part.Sessions, "Sessions"), (Part.Assignee, "Who"), (Part.Age, "Age"), (Part.Place, "Workspace")];

    // Builds the header once: its labels and a divider on the left edge of each resizable column. Called by the constructor.
    void BuildColumnHeader()
    {
        ListOrder.ToList().ForEach(_ => ColumnHeader.ColumnDefinitions.Add(new ColumnDefinition()));
        HeaderLabels.ToList().ForEach(label =>
        {
            var text = new TextBlock
            {
                Text = label.Text,
                FontSize = 10,
                Foreground = HeaderInk,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                // Over the title, where its text starts (TextOf's 2 DIP); over the age, where AgeOf puts it (6 DIP); else
                // 3 DIP clear of the divider's hairline, about where the column's chip text starts.
                Margin = new Thickness(label.Part == Part.Title ? 2 : label.Part == Part.Age ? 6 : 3, 0, 4, 0),
            };
            Grid.SetColumn(text, ListOrder.ToList().IndexOf(label.Part));
            ColumnHeader.Children.Add(text);
        });
        Enum.GetValues<TaskColumn>().ToList().ForEach(column => ColumnHeader.Children.Add(Divider(column)));
        fullGrids.Add(ColumnHeader);
        // The header's title floor follows its width, as each full line's does (ConstrainFullColumns, FitTitleFloor).
        ColumnHeader.SizeChanged += (_, _) => FitTitleFloor(ColumnHeader);
        // The lines narrow by the scroll bar's width while it shows; the header, outside the scroller, follows.
        Scroller.ScrollChanged += (_, _) => FollowScrollBar();
        Scroller.SizeChanged += (_, _) => FollowScrollBar();
        FollowScrollBar();
    }

    void FollowScrollBar() =>
        ColumnHeader.Margin = new Thickness(GridLeftInset, 4, GridRightInset + Math.Max(0, Scroller.ActualWidth - Scroller.ViewportWidth), 1);

    // ONE DIVIDER: an 8 DIP band centred on the column's left edge, drawn as the panel grip's hairline (GripTemplate), blue
    // under the pointer and while dragged. Dragging it left widens the column, the title giving up the room; right
    // narrows it (TaskColumnWidths.Dragged). The pointer's move is read against the header, which never moves, rather
    // than from the thumb's own deltas, measured from a thumb that moves with the edge (the grip's lesson, Grip.cs).
    // A double-click puts the column back to its default width.
    Thumb Divider(TaskColumn column)
    {
        var divider = new Thumb
        {
            Tag = $"task-column-divider-{column}",
            Width = 8,
            Margin = new Thickness(-4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.SizeWE,
            Template = (ControlTemplate)FindResource("GripTemplate"),
            ToolTip = "Drag to change the column's width; double-click for its usual width",
        };
        Grid.SetColumn(divider, ColumnIndexOf(column));
        Panel.SetZIndex(divider, 1);
        divider.DragStarted += (_, _) =>
            columnDrag = (column, Mouse.GetPosition(ColumnHeader).X, columnWidths, RoomToGive());
        divider.DragDelta += (_, _) =>
        {
            if (columnDrag is { } drag)
                ApplyColumns(TaskColumnWidths.Dragged(drag.Widths, drag.Column, Mouse.GetPosition(ColumnHeader).X - drag.X, drag.Room));
        };
        divider.DragCompleted += (_, e) => EndColumnDrag(e.Canceled);
        divider.MouseDoubleClick += (_, e) =>
        {
            e.Handled = true;
            ResizeColumn(column, TaskColumnWidths.Default[column]);
        };
        return divider;
    }

    void EndColumnDrag(bool cancelled)
    {
        if (columnDrag is not { } drag) return;
        columnDrag = null;
        if (cancelled) ApplyColumns(drag.Widths);
        // A press without a move changes nothing, and so saves nothing.
        else if (columnWidths != drag.Widths) Actions?.ResizeColumns?.Invoke(columnWidths);
    }

    /// <summary>
    /// Sets one column's width on every full line and saves it, as a divider's drag end does; the double-click's reset,
    /// and the tests' way to resize without a mouse.
    /// </summary>
    internal void ResizeColumn(TaskColumn column, double width)
    {
        ApplyColumns(columnWidths.With(column, Math.Max(TaskColumnWidths.MinWidth, width)));
        Actions?.ResizeColumns?.Invoke(columnWidths);
    }

    // The header and every full line on show take these widths, in place: nothing is redrawn.
    void ApplyColumns(TaskColumnWidths widths)
    {
        columnWidths = widths;
        fullGrids.ForEach(grid => Enum.GetValues<TaskColumn>().ToList()
            .ForEach(column => grid.ColumnDefinitions[ColumnIndexOf(column)].Width = new GridLength(widths[column])));
        // New text column widths change what the title may keep beside them (FitTitleFloor).
        fullGrids.ForEach(FitTitleFloor);
    }

    // The header's columns for this draw, the full line's, and whether it shows: only over full lines in the List, and
    // only while there are lines to head.
    void PaintColumnHeader(bool shown)
    {
        ColumnHeader.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        FullColumns().Select((width, i) => (width, i)).ToList().ForEach(c => ColumnHeader.ColumnDefinitions[c.i].Width = c.width);
        ConstrainFullColumns(ColumnHeader);
    }

    // For the tests: the width a column has on the lines, and whether the header shows.
    internal double ColumnWidth(TaskColumn column) => columnWidths[column];

    internal bool ColumnHeaderShown => ColumnHeader.Visibility == Visibility.Visible;
}
