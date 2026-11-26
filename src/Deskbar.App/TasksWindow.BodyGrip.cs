using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Deskbar.App;

// A LONG TEXT'S HEIGHT IS PETRE'S TO SET (task 2088e1ed; Petre, of a session's question cut at three lines in its bubble:
// "i should be able to increase or decrease issue or comment body"). Three things in the tasks panel stop at a few lines:
// a long comment (about three lines, then "Show more", Ruling 63) and an open line's description and question (four
// lines, then "Show all in window", task e91f8aa5). Each was all or nothing: the cut, or the whole text. A grip now sits
// under each of them, a thin band with a short bar in its middle and the up-down cursor, and dragging it shows more or
// less, from one line down to the whole text, live under the pointer as a text box's corner grip does.
//
// WHAT IS KEPT. The height a drag ends on is kept per text (bodyHeights: per comment, and per task for its description
// and its question) for as long as the app runs, through every redraw and through the panel closing and opening, since
// a size dragged on purpose is not undone by folding a line. A drag to the text's foot is "the whole text" (infinity),
// which keeps showing whole if the text grows; a comment dragged whole reads as one Show more opened ("Show less" under
// it). "Show more" and "Show less" still do what they did, and either one forgets a dragged size: Show less is three
// lines again.
//
// WHERE IT SHOWS. Only under a text long enough to be cut, or whose size was dragged (so one dragged to its whole height
// keeps a grip to shrink it again). Not in the task window (TaskDetailWindow), which shows every text whole.
//
// THE PRESS IS THE GRIP'S ALONE. A description and a question sit inside their line, which is a button that picks the
// task on a click and arms a drag of the line on a press (TasksWindow.Drag.cs). The line's own press handler leaves a
// press on a grip alone (IsOnBodyGrip), and the grip marks its press handled, so the line neither folds, picks nor moves.
//
// THE POINTER, NOT DELTAS. The new height is the height at the press plus how far the pointer has moved since, both in
// the window's coordinates, so it does not matter that the grip moves with the text's foot (or, on a description, snaps
// to a line edge, HeightCap.EdgeOf) while it is dragged. The panel's width grip measures the same way (TasksWindow.Grip.cs).
//
// NO REDRAW MID-DRAG. A redraw rebuilds every line, the grip under the pointer included, so MustWait holds one back while
// a grip is dragged (bodyGripDragging); the release draws whatever waited. A drag the window loses (another window takes
// the mouse) puts the text back as it was.
public partial class TasksWindow
{
    // The heights Petre has dragged texts to, in DIP, keyed by CommentKey, DescriptionKey or DetailKey; positive infinity
    // is "the whole text". Part of the redraw signature, so a redraw after a drag builds the text at its new height.
    readonly Dictionary<string, double> bodyHeights = [];

    // Whether a grip is being dragged right now (see "No redraw mid-drag" above).
    bool bodyGripDragging;

    /// <summary>Every body grip's tag, so the line's press handler, and the tests, can tell one.</summary>
    internal const string BodyGripTag = "task-body-grip";

    /// <summary>The grip band's height, DIP: tall enough to hit without hunting for it, short enough to add little.</summary>
    internal const double BodyGripHeight = 7;

    // How strong the grip's bar is at rest: there to be found, quieter than the text. Full strength under the pointer.
    const double BodyGripRest = 0.35;

    internal static string CommentKey(Guid comment) => $"comment:{comment}";
    internal static string DescriptionKey(Guid task) => $"description:{task}";
    internal static string DetailKey(Guid task) => $"detail:{task}";

    /// <summary>For the tests: the height a text was dragged to, if any.</summary>
    internal double? BodyHeight(string key) => bodyHeights.TryGetValue(key, out var height) ? height : null;

    // The dragged heights in the redraw signature, whole DIPs, in a fixed order.
    string BodyHeightsSignature =>
        string.Join(",", bodyHeights.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value:0}"));

    // Whether a press landed on a grip (its band; the bar in it takes no hits).
    static bool IsOnBodyGrip(object? source) => source is FrameworkElement { Tag: BodyGripTag };

    /// <summary>
    /// What a drag ending at <paramref name="height"/> keeps for one comment, and the redraw that shows it: the whole
    /// comment (infinity, which the comment shows as Show more opens it, "Show less" under it) when the height reaches its
    /// foot, else that height. The drag replaces a Show more: unlike longOpen, a dragged height outlives the panel
    /// closing. Internal for the tests, which cannot drive a mouse drag on a hidden desktop.
    /// </summary>
    internal void KeepCommentHeight(Guid comment, double height, double whole)
    {
        longOpen.Remove(comment);
        bodyHeights[CommentKey(comment)] = height >= whole - 0.5 ? double.PositiveInfinity : height;
        Redraw();
    }

    /// <summary>
    /// What a drag ending at <paramref name="height"/> keeps for an open line's description or question (by its key), and
    /// the redraw: the whole text when the height reaches its foot, else that height. Internal for the tests.
    /// </summary>
    internal void KeepCappedHeight(string key, double height, double whole)
    {
        bodyHeights[key] = height >= whole - 0.5 ? double.PositiveInfinity : height;
        Redraw();
    }

    // Forgets a comment's dragged height (Show more and Show less, which say how much shows themselves).
    void ForgetCommentHeight(Guid comment) => bodyHeights.Remove(CommentKey(comment));

    // The redraw after a drag or a button: the latest view, unless something holds it back (Present says what).
    void Redraw()
    {
        if (latest is not null) Present(latest);
    }

    /// <summary>
    /// The grip under one text: <paramref name="shown"/> is the height showing now, <paramref name="least"/> and
    /// <paramref name="whole"/> the smallest and largest it can be dragged to, <paramref name="preview"/> shows a height
    /// while the drag goes on (and puts the first one back if the drag is lost), and <paramref name="keep"/> keeps the one
    /// a drag ends on. A press released where it started keeps nothing.
    /// </summary>
    Border BodyGrip(string name, Func<double> shown, Func<double> least, Func<double> whole, Action<double> preview, Action<double> keep)
    {
        // The bar: short, rounded, centred in the band. Never hit, so a press anywhere on the band is the band's.
        var bar = new Border
        {
            Width = 28,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = HeaderInk,
            Opacity = BodyGripRest,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var grip = new Border
        {
            Tag = BodyGripTag,
            Height = BodyGripHeight,
            // Transparent, not null, so the whole band takes the press and shows the cursor, not just the bar.
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNS,
            Focusable = false,
            ToolTip = "Drag to show more or less",
            Child = bar,
        };
        AutomationProperties.SetName(grip, name);
        // The press: the pointer's height in the window and the text's height then. Null while no drag is under way.
        (double Y, double Height)? from = null;
        var last = 0.0;
        grip.MouseEnter += (_, _) => bar.Opacity = 1;
        grip.MouseLeave += (_, _) =>
        {
            if (from is null) bar.Opacity = BodyGripRest;
        };
        grip.MouseLeftButtonDown += (_, e) =>
        {
            // Handled: the line's button under it must not take this press as its click (see the top of this file).
            e.Handled = true;
            from = (e.GetPosition(this).Y, shown());
            last = from.Value.Height;
            bodyGripDragging = true;
            grip.CaptureMouse();
        };
        grip.MouseMove += (_, e) =>
        {
            if (from is not { } start || !grip.IsMouseCaptured) return;
            var floor = least();
            last = Math.Clamp(start.Height + e.GetPosition(this).Y - start.Y, floor, Math.Max(floor, whole()));
            preview(last);
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (from is not { } start) return;
            e.Handled = true;
            // Cleared before the capture goes, so LostMouseCapture below does not take it for a lost drag.
            from = null;
            bodyGripDragging = false;
            grip.ReleaseMouseCapture();
            bar.Opacity = grip.IsMouseOver ? 1 : BodyGripRest;
            if (Math.Abs(last - start.Height) >= 0.5) keep(last);
            else Flush();
        };
        grip.LostMouseCapture += (_, _) =>
        {
            if (from is not { } start) return;
            // Another window took the mouse mid-drag: the text goes back to the height it had at the press.
            from = null;
            bodyGripDragging = false;
            bar.Opacity = BodyGripRest;
            preview(start.Height);
            Flush();
        };
        return grip;
    }
}
