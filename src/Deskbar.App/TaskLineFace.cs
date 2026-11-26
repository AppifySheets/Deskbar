using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

// ONE TASK LINE ON THE BAR, LAID OUT (2026-11-23, a released-build bug: at 198 px and 125% the titles read "Ca…", "M…",
// "S." with a wide empty gap after them). The line used to be a DockPanel: the parts docked left, the parts docked
// right, and the title filling what was left. Two of the right-docked parts were the OFF flag toggles, which stay
// transparent until the line is hovered (Opacity 0) but were still laid out, so they took about thirty DIP between the
// title and the age: at the default docked width the title was left 18 DIP (measured,
// FloatingBarTaskListTests.A_title_takes_the_free_width_of_its_line).
//
// This panel lays the line out instead, left to right:
//   Left     Undo on a line just closed (the tick box went in task acd76fbe), at its own width;
//   Title    everything the other parts do not take, so a title that does not fit ends right where the next part
//            starts, with nothing empty in between;
//   Right    the parts that show (a flag that is on, the close button, Undo), each at its own width;
//   End      the workspace chip at the line's right edge, or, when the chip would leave the title less than
//            TitleShare of the line, a dot in the workspace's colour instead (its name in the tooltip), so a title keeps a
//            fair share of a narrow bar;
//   Overlay  the quiet controls (the flags that are off), laid over the right end of the title instead of beside it,
//            drawn on top. They show while the line is hovered or selected, over the title's tail, and take no room of
//            their own, so the title never moves when they come and go.
// Since 2026-11-24 the bar hands it no Right parts and no Overlay (FloatingBar.TaskLineButton: the flags show in the
// title's weight and slant, and the age moved to the tasks panel); both slots stay for any later use, and the tests still
// pin how they would be laid out.
// Every part but the title is measured at its own width; the title is measured at the room left, and is arranged at all
// of it. Each part sits in the line's first row (FirstRow), so a title that wraps to a second line leaves them beside its
// first line.
//
// THE SECOND ROW RUNS THE FULL WIDTH (task acd76fbe, Petre: "utilize the second row fully"). A wrapping TextBlock title
// is given the whole line from its left edge to the line's right edge, under the chip too, and its first row is ended
// early by a LineBreak where its words stop fitting the room beside the chip and the Right parts (TitleRows). So the
// first row stops where the chip starts, as before, and the second row goes on to the end of the line instead of stopping
// short of the chip. A title that is not a wrapping TextBlock (the inline editor) keeps the old layout: it ends where the
// chip starts on every row.
internal sealed class TaskLineFace : Panel
{
    /// <summary>How much of the line the title keeps before the chip gives way to a dot.</summary>
    internal const double TitleShare = 0.55;

    readonly IReadOnlyList<UIElement> left;
    readonly UIElement title;
    readonly IReadOnlyList<UIElement> right;
    readonly UIElement chip;
    readonly UIElement? dot;
    readonly UIElement? overlay;

    // The width the title was given by the last measure.
    double titleRoom;

    // A wrapping TextBlock title spans the full line (see the header): the text it was built with, so each measure can
    // place its first row's break afresh, and the break placed last, so a measure that lands on the same one changes no
    // inline (changing them would measure the title again and again).
    readonly TextBlock? spanning;
    readonly IReadOnlyList<System.Windows.Documents.Inline> spanningSource = [];
    int? placedBreak;

    public TaskLineFace(IReadOnlyList<UIElement> left, UIElement title, IReadOnlyList<UIElement> right, UIElement chip, UIElement? dot,
        UIElement? overlay)
    {
        this.left = left;
        this.title = title;
        this.right = right;
        this.chip = chip;
        this.dot = dot;
        this.overlay = overlay;
        if (title is TextBlock { TextWrapping: TextWrapping.Wrap } text)
        {
            spanning = text;
            spanningSource = text.Inlines.ToList();
        }
        // The overlay last, so it is drawn over the title.
        left.Concat([title]).Concat(right).Concat([chip]).Concat(dot is null ? [] : [dot]).Concat(overlay is null ? [] : [overlay]).ToList()
            .ForEach(part => Children.Add(part));
    }

    /// <summary>Whether the last measure gave the chip's place to the dot.</summary>
    internal bool ShowsDot { get; private set; }

    /// <summary>
    /// The height of the line's first row: what the parts beside the title are centred on. The title's own single-line
    /// height when it is a TextBlock (a two-line title leaves its second line below the row), else the tallest part.
    /// </summary>
    double FirstRow =>
        Math.Max(Others().Select(p => p.DesiredSize.Height).DefaultIfEmpty(0).Max(),
            title is TextBlock text ? Math.Min(title.DesiredSize.Height, SingleLine(text)) : title.DesiredSize.Height);

    static double SingleLine(TextBlock text) =>
        double.IsNaN(text.LineHeight) ? text.FontFamily.LineSpacing * text.FontSize + text.Padding.Top + text.Padding.Bottom + text.Margin.Top + text.Margin.Bottom
            : text.LineHeight + text.Margin.Top + text.Margin.Bottom;

    IEnumerable<UIElement> Others() => left.Concat(right).Concat([End]);

    UIElement End => ShowsDot && dot is not null ? dot : chip;

    static double WidthOf(IEnumerable<UIElement> parts) => parts.Sum(p => p.DesiredSize.Width);

    protected override Size MeasureOverride(Size available)
    {
        var free = new Size(double.PositiveInfinity, available.Height);
        left.Concat(right).Concat([chip]).Concat(dot is null ? [] : [dot]).ToList().ForEach(p => p.Measure(free));
        var fixedWidth = WidthOf(left) + WidthOf(right);
        if (double.IsInfinity(available.Width))
        {
            ShowsDot = false;
            PlaceBreak(null);
            title.Measure(free);
            titleRoom = title.DesiredSize.Width;
            overlay?.Measure(free);
            return new Size(fixedWidth + titleRoom + chip.DesiredSize.Width, Math.Max(title.DesiredSize.Height, FirstRow));
        }
        // The chip gives way to the dot when it would leave the title less than its share of the line.
        ShowsDot = dot is not null && dot.DesiredSize.Width < chip.DesiredSize.Width
            && available.Width - fixedWidth - chip.DesiredSize.Width < TitleShare * available.Width;
        titleRoom = Math.Max(0, available.Width - fixedWidth - End.DesiredSize.Width);
        overlay?.Measure(new Size(titleRoom, available.Height));
        if (spanning is null)
        {
            title.Measure(new Size(titleRoom, available.Height));
            return new Size(available.Width, Math.Max(title.DesiredSize.Height, FirstRow));
        }
        // The first row's words fit the room beside the chip (less the title's own margins and one DIP, so a rounding
        // in the measure cannot push the last word under the chip); the second row has the line to its end.
        PlaceBreak(TitleRows.BreakAt(spanning, spanningSource, titleRoom - spanning.Margin.Left - spanning.Margin.Right - 1));
        title.Measure(new Size(Math.Max(0, available.Width - WidthOf(left)), available.Height));
        return new Size(available.Width, Math.Max(title.DesiredSize.Height, FirstRow));
    }

    // The spanning title's first row ended at `at` (TitleRows.WithBreak), changed only when the break moves.
    void PlaceBreak(int? at)
    {
        if (spanning is null || at == placedBreak) return;
        placedBreak = at;
        var inlines = TitleRows.WithBreak(spanningSource, at);
        spanning.Inlines.Clear();
        spanning.Inlines.AddRange(inlines);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var row = FirstRow;
        // A part beside the title, centred on the first row.
        Rect InRow(double x, UIElement part) =>
            new(x, Math.Max(0, (row - part.DesiredSize.Height) / 2), part.DesiredSize.Width, part.DesiredSize.Height);

        var x = 0.0;
        left.ToList().ForEach(part =>
        {
            part.Arrange(InRow(x, part));
            x += part.DesiredSize.Width;
        });
        var room = Math.Max(0, final.Width - x - WidthOf(right) - End.DesiredSize.Width);
        // A spanning title is arranged to the line's end: its first row's break keeps the words clear of the chip.
        title.Arrange(new Rect(x, title.DesiredSize.Height < row ? (row - title.DesiredSize.Height) / 2 : 0,
            spanning is null ? room : Math.Max(0, final.Width - x), Math.Max(title.DesiredSize.Height, 0)));
        // The quiet controls over the right end of the title, in its first row.
        overlay?.Arrange(InRow(x + room - Math.Min(room, overlay.DesiredSize.Width), overlay) with { Width = Math.Min(room, overlay.DesiredSize.Width) });
        x += room;
        right.ToList().ForEach(part =>
        {
            part.Arrange(InRow(x, part));
            x += part.DesiredSize.Width;
        });
        End.Arrange(InRow(x, End));
        // The one of chip and dot not in use takes no room and shows nothing. Hidden rather than Collapsed: a hidden
        // element is still measured at its own size, so the next measure compares the same two widths and settles on
        // the same choice (Collapsed would measure it as nothing and flip the choice back and forth).
        End.Visibility = Visibility.Visible;
        if ((ShowsDot ? chip : dot) is { } unused)
        {
            unused.Visibility = Visibility.Hidden;
            unused.Arrange(new Rect(0, 0, 0, 0));
        }
        return final;
    }
}
