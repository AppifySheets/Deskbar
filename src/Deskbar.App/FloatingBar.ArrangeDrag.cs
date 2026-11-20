using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Arrange;

namespace Deskbar.App;

// THE DRAG OF ARRANGE MODE, the half of "drag rows to arrange" that moves things (design:
// docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "What a drop does"; the mode itself is FloatingBar.Arrange.cs).
//
// WHO DECIDES WHAT. The bar answers one question only: which row is under the pointer, and in which part of it (a
// DropZone). What a release there would DO, and what to draw meanwhile, is RowDrop.Plan's answer (Deskbar.Core), and
// performing it is WorkspaceManager.ApplyDrop's. The bar never re-decides a rule, so the line it draws and the change
// the manager makes come from the same function and cannot disagree.
//
// MOUSE CAPTURE, NEVER DoDragDrop. A row never leaves the bar, so OLE's drag loop would buy nothing, and that loop is
// what swallowed the terminating click in #48 (WindowDragSource has the story). The press captures the mouse on the
// bar itself, so the moves and the release keep arriving here even when the pointer strays off the bar, and an ordinary
// WPF release ends the drag.
//
// THE LIFE OF ONE DRAG:
//   press   on a workspace row, a group header or a folded group's line, while arranging: armed (ArmArrangeDrag),
//           with the mouse captured. Nothing is drawn yet; a click that does not move is still nothing.
//   move    past the system drag distance: started. From then on every move hit-tests the rows and repaints the
//           preview (ArrangeMove).
//   release applies what the preview showed (ArrangeRelease, then ArrangeDropAt), once, and serves any rebuild that
//           waited for the drag.
//   Esc, losing the capture, the mode ending, or a move that arrives with the button already up: cancelled, nothing
//           changes (CancelArrangeDrag).
//
// THE BAR DOES NOT REBUILD DURING A DRAG (Rebuild's guard on `arrangeDrag`). A rebuild throws away every row,
// including the one being dragged and every row the preview is measured against. The older guard, "the button is down
// over the bar", is not enough on its own: under capture the pointer may be anywhere. The deferred rebuild is served the
// moment the drag ends, whichever way it ends.
//
// THE PREVIEW lives on an adorner over the bar's Root, never in the rows' own layout. The bar is SizeToContent, so a
// line or a gap that took up room would resize the whole window under the pointer on every move. What it draws (the
// look Petre approved, 3A for "group with"):
//   * the dragged row's slot: covered and outlined with a dashed 1 DIP line at 25% white, the "gap" it leaves;
//   * a copy of the row at 72% opacity, following the pointer up and down;
//   * the answer: a 2 DIP insertion line in InsertMarkerBrush (the bar's full width between top-level rows, short and
//     inside the bracket for a join), or the target in DropHighlight with a 1 DIP ActiveBorder ring, with the hint
//     ("Group with Mail…") under it when the drop makes a group.
public partial class FloatingBar
{
    // A row the drag can pick up or aim at, registered by the rebuild as it builds each one (RegisterArrangeRow,
    // RegisterArrangeHeader) and cleared with the rows. `Drags` is what a press on it picks up; `Drop` what a pointer over
    // it aims at, the same row except on a folded group (below). `Header` gives the header's zones; `Whole` makes the
    // whole element mean "onto".
    sealed record ArrangeTarget(RowRef Drags, RowRef Drop, FrameworkElement Element, bool Header, bool Whole = false);

    readonly List<ArrangeTarget> arrangeTargets = [];

    // Each group's box (FamilyBox), by group: what a dragged header carries (the whole group moves, so the whole box
    // leaves the gap), and where a line beside a whole group is drawn, at the box's own top or bottom edge.
    readonly Dictionary<Guid, FrameworkElement> arrangeBoxes = [];

    // The press or drag in progress, or null. `Element` is what the ghost copies and the gap covers; `PressedAt` is in
    // Root's coordinates, like every point in this file.
    sealed record ArrangeDragState(RowRef Dragged, FrameworkElement Element, Point PressedAt, bool Started);

    ArrangeDragState? arrangeDrag;

    // What the preview draws right now, or null when nothing is. Kept as numbers rather than only painted, so a test can
    // read what would be on screen (the adorner draws exactly this).
    internal sealed record ArrangePaint(DropShow Show, Rect Gap, Rect Ghost, Rect? Line, Rect? Highlight, string? Hint);

    ArrangePaint? arrangePaint;
    Adorner? arrangeAdorner;
    VisualBrush? arrangeGhost;

    internal bool ArrangeDragging => arrangeDrag is { Started: true };
    internal ArrangePaint? ArrangePainting => arrangePaint;

    // How the new-group drop asks for a name, prefilled with the target's. The dialog by default; a test swaps it for an
    // answer so no dialog opens.
    Func<string, Maybe<string>>? askGroupName;

    internal Func<string, Maybe<string>> AskGroupName
    {
        get => askGroupName ?? (name => PromptDialog.Ask("New group", "Name:", name, owner: this));
        set => askGroupName = value;
    }

    // The zones (spec, "Which of those the pointer means"): a row's top quarter is Before and its bottom quarter After,
    // the middle half Onto; a group header's top quarter is Before and the rest Onto. A dragged HEADER uses halves, since
    // a whole group can only go above or below a row.
    const double ArrangeEdgeShare = 0.25;

    // How far inside the member row a join's line starts: past the member's own rail, so the short line sits inside the
    // bracket and cannot be mistaken for the full-width one. A number to judge on screen.
    const double ArrangeJoinInset = 10;

    // The copy of the dragged row, and the dashed gap it leaves (the constraints block of the plan).
    const double ArrangeGhostOpacity = 0.72;
    static readonly Pen ArrangeGapPen = FrozenPen(new Pen(Frozen(0x40, 0xFF, 0xFF, 0xFF), 1) { DashStyle = DashStyles.Dash });

    // Made on first use rather than in a static initializer: ActiveBorder is a static of another part of this class, and
    // the order in which the parts' static initializers run is not defined, so an initializer here could read it null.
    static Pen? arrangeTargetPen;
    static Pen ArrangeTargetPen => arrangeTargetPen ??= FrozenPen(new Pen(ActiveBorder, 1));

    static Pen FrozenPen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    // --- registration, called by the rebuild ---------------------------------------------------------------------

    // Forgets the last build's targets; RebuildCore calls it beside headerDropTargets.Clear(), before the new rows.
    void ForgetArrangeTargets()
    {
        arrangeTargets.Clear();
        arrangeBoxes.Clear();
    }

    // A workspace row. A member of a FOLDED group is aimed at as its group's header, onto the whole of it: the spec makes
    // a folded group "a target only through its header", and it does not unfold during a drag. It still drags as itself.
    void RegisterArrangeRow(Guid workspace, Core.Domain.Group? group, UIElement row)
    {
        if (row is not FrameworkElement element) return;
        arrangeTargets.Add(group is { Folded: true }
            ? new ArrangeTarget(new RowRef.Workspace(workspace), new RowRef.Group(group.Id), element, Header: false, Whole: true)
            : new ArrangeTarget(new RowRef.Workspace(workspace), new RowRef.Workspace(workspace), element, Header: false));
    }

    // A group's header, or a folded group's one line.
    void RegisterArrangeHeader(Guid group, FrameworkElement header) =>
        arrangeTargets.Add(new ArrangeTarget(new RowRef.Group(group), new RowRef.Group(group), header, Header: true));

    // A group's box; returns it, so the rebuild can wrap the FamilyBox call in place.
    UIElement ArrangeBox(Guid group, UIElement box)
    {
        if (box is FrameworkElement element) arrangeBoxes[group] = element;
        return box;
    }

    // --- hit testing and the plan -----------------------------------------------------------------------------------

    // Which row is under the pointer and in which zone of it. Below the last row is End, with no row. Between rows (the
    // separators are a few pixels) or above the first, the nearest row answers: the line it draws sits on that gap anyway.
    internal (RowRef? Target, DropZone Zone) ArrangeTargetAt(RowRef dragged, Point inRoot)
    {
        var placed = PlacedArrangeTargets();
        if (placed.Count == 0 || inRoot.Y > placed.Max(p => p.Bounds.Bottom)) return (null, DropZone.End);

        // Neighbouring rows overlap by a pixel (each row's negative margin), so the nearest CENTRE settles a tie.
        var (target, bounds) = placed
            .OrderBy(p => VerticalDistance(p.Bounds, inRoot.Y))
            .ThenBy(p => Math.Abs(p.Bounds.Top + p.Bounds.Height / 2 - inRoot.Y))
            .First();
        var share = (inRoot.Y - bounds.Top) / Math.Max(1, bounds.Height);

        var zone = dragged is RowRef.Group
            ? share < 0.5 ? DropZone.Before : DropZone.After
            : target.Whole ? DropZone.Onto
            : share < ArrangeEdgeShare ? DropZone.Before
            : target.Header ? DropZone.Onto
            : share > 1 - ArrangeEdgeShare ? DropZone.After
            : DropZone.Onto;
        return (target.Drop, zone);
    }

    // The hit test, then RowDrop.Plan. What the drag draws and what the release applies both come from here.
    internal (DropAction Action, DropShow Show) ArrangePreviewAt(RowRef dragged, Point inRoot) =>
        PlanAt(dragged, ArrangeTargetAt(dragged, inRoot));

    (DropAction Action, DropShow Show) PlanAt(RowRef dragged, (RowRef? Target, DropZone Zone) aim) =>
        RowDrop.Plan(manager.State, dragged, aim.Target, aim.Zone);

    // The drop itself: what the preview at this point shows, applied once. The name prompt runs only for a new group,
    // inside ApplyDrop, and a Cancel there is a success that changes nothing.
    internal Result ArrangeDropAt(RowRef dragged, Point inRoot)
    {
        var (action, _) = ArrangePreviewAt(dragged, inRoot);
        var result = manager.ApplyDrop(action, AskGroupName);
        if (ClickTrace.On)
            ClickTrace.Write($"arrange drop {DescribeDrop(action)} ok={result.IsSuccess}{(result.IsFailure ? $" error={result.Error}" : "")}");
        return result;
    }

    // Every registered row that is on screen, with where it is in Root. A rebuild leaves new rows with no layout until a
    // pass runs, so one is forced first when needed (AimHeaderDrag does the same for the same reason).
    IReadOnlyList<(ArrangeTarget Target, Rect Bounds)> PlacedArrangeTargets()
    {
        if (arrangeTargets.Any(t => !t.Element.IsArrangeValid)) UpdateLayout();
        return arrangeTargets
            .Where(t => t.Element.IsVisible)
            .Select(t => (Target: t, Bounds: BoundsIn(t.Element, Root)))
            .Where(p => p.Bounds is not null)
            .Select(p => (p.Target, p.Bounds!.Value))
            .ToList();
    }

    // The registered row a press landed in, walking up from what was hit. A member row is met before its group's box,
    // which is not a target, so a press on a member picks up the member.
    ArrangeTarget? ArrangeSourceOf(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
            if (arrangeTargets.FirstOrDefault(t => ReferenceEquals(t.Element, node)) is { } hit) return hit;
        return null;
    }

    // Whether the pointer is on something a press would pick up: the move cursor while arranging (CursorAt).
    bool OnArrangeSource(Point inRoot) =>
        arranging && PlacedArrangeTargets().Any(p => p.Bounds.Contains(inRoot));

    // --- the drag ---------------------------------------------------------------------------------------------------

    // Called by the press handler where the mode swallows a press (after the #48 bookkeeping, as Task 3 left it). A
    // press on the Done strip is left to its button; a press on anything that is not a row or a header arms nothing.
    void ArmArrangeDrag(MouseButtonEventArgs e)
    {
        if (PressIsOnArrangeStrip(e.OriginalSource) || ArrangeSourceOf(e.OriginalSource) is not { } target) return;

        // A dragged header carries its whole box.
        var element = target.Drags is RowRef.Group g && arrangeBoxes.TryGetValue(g.Id, out var box) ? box : target.Element;
        arrangeDrag = new ArrangeDragState(target.Drags, element, e.GetPosition(Root), Started: false);
        CaptureMouse();
        if (ClickTrace.On) ClickTrace.Write($"arrange press {DescribeRow(target.Drags)} captured={IsMouseCaptured}");
    }

    // The move handler's hook: true when an arrange press owns this move, which is then nobody else's. A move with the
    // button already up means its release never reached the bar, and a drag that waited for it would hold every rebuild
    // back for good, so it is called off.
    bool ArrangeDragMoved(MouseEventArgs e)
    {
        if (arrangeDrag is null) return false;
        if (e.LeftButton != MouseButtonState.Pressed) CancelArrangeDrag("the button came up unseen");
        else ArrangeMove(e.GetPosition(Root));
        return true;
    }

    // One move of the pointer. Starts the drag once it has gone the system drag distance, then repaints the preview.
    internal void ArrangeMove(Point inRoot)
    {
        if (arrangeDrag is not { } drag) return;

        if (!drag.Started)
        {
            if (Math.Abs(inRoot.X - drag.PressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(inRoot.Y - drag.PressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            arrangeDrag = drag = drag with { Started = true };
            StartArrangePreview(drag);
            if (ClickTrace.On) ClickTrace.Write($"arrange drag-start {DescribeRow(drag.Dragged)}");
        }

        var (_, show) = ArrangePreviewAt(drag.Dragged, inRoot);
        arrangePaint = PaintFor(drag, inRoot, show);
        arrangeAdorner?.InvalidateVisual();
    }

    // The release. A press that never became a drag is only let go; a drag drops where it was released. The drag is
    // ended BEFORE the drop is applied: the new-group drop opens a modal prompt, which must not open under a capture the
    // bar still holds, and the drop's own rebuild must not be deferred by the drag it ends.
    internal void ArrangeRelease(Point inRoot)
    {
        if (arrangeDrag is not { } drag) return;
        EndArrangeDrag();
        if (drag.Started) Report(ArrangeDropAt(drag.Dragged, inRoot));
        else if (ClickTrace.On) ClickTrace.Write($"arrange release without a drag {DescribeRow(drag.Dragged)}");
        FlushDeferredRebuild();
    }

    // Esc, a lost capture, the mode ending: nothing is dropped, and a rebuild that waited is served.
    internal void CancelArrangeDrag(string why)
    {
        if (arrangeDrag is not { } drag) return;
        EndArrangeDrag();
        if (ClickTrace.On) ClickTrace.Write($"arrange cancel {DescribeRow(drag.Dragged)} ({why})");
        FlushDeferredRebuild();
    }

    // The state first, then the capture: releasing the capture raises LostMouseCapture, which must find no drag left
    // to cancel.
    void EndArrangeDrag()
    {
        arrangeDrag = null;
        arrangePaint = null;
        arrangeGhost = null;
        if (arrangeAdorner is { } adorner) AdornerLayer.GetAdornerLayer(Root)?.Remove(adorner);
        arrangeAdorner = null;
        ClearValue(CursorProperty);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // Wired once in the constructor. Another window or the system taking the mouse mid-drag (a dialog, Alt+Tab, a
    // locked screen) ends the drag as a cancel: a release that will never come cannot be waited for.
    void OnArrangeLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this)) CancelArrangeDrag("capture lost");
    }

    // --- the preview ------------------------------------------------------------------------------------------------

    void StartArrangePreview(ArrangeDragState drag)
    {
        Cursor = Cursors.SizeAll;
        // The copy is a live picture of the row, cut to its own box: the bounds of what it draws can differ from its
        // layout box (its ring overhangs by a pixel), and the default viewbox would scale that difference in.
        arrangeGhost = new VisualBrush(drag.Element)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(drag.Element.RenderSize),
        };
        if (AdornerLayer.GetAdornerLayer(Root) is not { } layer) return;
        arrangeAdorner = new ArrangePreview(Root, PaintArrange) { IsHitTestVisible = false };
        layer.Add(arrangeAdorner);
    }

    // What to draw for this pointer and this answer, in Root's coordinates.
    ArrangePaint? PaintFor(ArrangeDragState drag, Point inRoot, DropShow show)
    {
        if (BoundsIn(drag.Element, Root) is not { } gap) return null;
        var ghost = gap with { Y = gap.Y + inRoot.Y - drag.PressedAt.Y };
        var rows = BoundsIn(Rows, Root) ?? gap;

        return show switch
        {
            DropShow.Line line when LineFor(line, rows) is { } at => new ArrangePaint(show, gap, ghost, at, null, null),
            DropShow.Highlight lit when ArrangeBoundsOf(lit.Target) is { } target => new ArrangePaint(show, gap, ghost, null, target, lit.Hint),
            _ => new ArrangePaint(show, gap, ghost, null, null, null),
        };
    }

    // The insertion line: 2 DIP, centred on the edge it marks. Beside a whole group, at its box's edge; between top-level
    // rows, across the bar's full width; for a join, from inside the bracket to the member row's end.
    Rect? LineFor(DropShow.Line line, Rect rows)
    {
        if (ArrangeBoundsOf(line.Beside) is not { } beside) return null;
        var y = line.After ? beside.Bottom : beside.Top;
        return line.InsideGroup
            ? new Rect(beside.Left + ArrangeJoinInset, y - 1, Math.Max(0, beside.Width - ArrangeJoinInset), 2)
            : new Rect(rows.Left, y - 1, rows.Width, 2);
    }

    // Where a row is drawn: a workspace's row, or a group's whole box (its header when the box is missing).
    Rect? ArrangeBoundsOf(RowRef row) =>
        row switch
        {
            RowRef.Group g when arrangeBoxes.TryGetValue(g.Id, out var box) => BoundsIn(box, Root),
            _ => arrangeTargets.FirstOrDefault(t => t.Drags == row) is { } target ? BoundsIn(target.Element, Root) : null,
        };

    // The adorner's drawing, from arrangePaint alone. The gap first, then the copy, then the answer on top, so the line or
    // the outline is never hidden under the copy of the row.
    void PaintArrange(DrawingContext dc)
    {
        if (arrangePaint is not { } paint) return;

        // The row's own slot: covered with the bar's background so the row seems to have left it, and outlined dashed.
        var gap = paint.Gap;
        gap.Inflate(-0.5, -0.5);
        dc.DrawRoundedRectangle(Root.Background, ArrangeGapPen, gap, 3, 3);

        if (arrangeGhost is { } ghost)
        {
            dc.PushOpacity(ArrangeGhostOpacity);
            dc.DrawRectangle(ghost, null, paint.Ghost);
            dc.Pop();
        }

        if (paint.Highlight is { } lit)
        {
            lit.Inflate(-0.5, -0.5);
            dc.DrawRoundedRectangle(DropHighlight, ArrangeTargetPen, lit, 3, 3);
        }

        if (paint.Hint is { } hint && paint.Highlight is { } under)
        {
            var text = new FormattedText(hint, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                GroupHeaderFontSize, CurrentRowForeground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            // Under the target, on a patch of the bar's own background, so it reads over whatever row is below.
            var at = new Point(under.Left + 4, under.Bottom + 1);
            dc.DrawRoundedRectangle(Root.Background, ArrangeTargetPen, new Rect(at.X - 3, at.Y, text.Width + 6, text.Height + 1), 2, 2);
            dc.DrawText(text, at);
        }

        if (paint.Line is { } line) dc.DrawRoundedRectangle(InsertMarkerBrush, null, line, 1, 1);
    }

    // The preview's surface: an adorner that draws whatever the bar hands it and takes no part in layout or hit testing.
    sealed class ArrangePreview(UIElement adorned, Action<DrawingContext> paint) : Adorner(adorned)
    {
        protected override void OnRender(DrawingContext drawingContext) => paint(drawingContext);
    }

    // --- the trace --------------------------------------------------------------------------------------------------

    string DescribeRow(RowRef row) =>
        row switch
        {
            RowRef.Workspace w => manager.State.Workspaces.FirstOrDefault(x => x.Id == w.Id)?.Name ?? "?",
            RowRef.Group g => $"group {manager.State.Groups.FirstOrDefault(x => x.Id == g.Id)?.Name ?? "?"}",
            _ => "?",
        };

    string DescribeDrop(DropAction action) =>
        action switch
        {
            DropAction.Place p => $"place {DescribeRow(new RowRef.Workspace(p.Workspace))} {(p.After ? "after" : "before")} {DescribeRow(p.Beside)}",
            DropAction.PlaceGroup p => $"place {DescribeRow(new RowRef.Group(p.Group))} {(p.After ? "after" : "before")} {DescribeRow(p.Beside)}",
            DropAction.GroupWith g => $"group {DescribeRow(new RowRef.Workspace(g.Dragged))} with {DescribeRow(new RowRef.Workspace(g.Target))}",
            _ => "nothing",
        };
}
