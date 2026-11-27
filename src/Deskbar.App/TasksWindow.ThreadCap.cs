using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// AN OPEN THREAD NEVER PUSHES ITS OWN TASK OUT OF SIGHT (task ee9bd3fc; Petre, on a screenshot of the tasks panel filled
// top to bottom by one task's comments, its line scrolled off above: "expanding task comments should limit the task
// detail height and always show the task itself").
//
// A thread used to be a plain block in the list, as tall as everything in it. With a long conversation it was taller
// than the panel, so reading it meant scrolling the task's own line away, and nothing on screen said which task the
// comments belonged to. Now a top-level thread (one drawn under its task's line, in the List or a Matrix cell) is
// capped: the line and the thread together fit in the list's view. Inside the cap:
//   - the head (the subtask's "Subtask of" entry, the row of buttons, the subtask box) stays at the top, and the
//     comment box stays at the bottom, so the task's actions and the place to reply never scroll away;
//   - everything between (sessions, subtasks, links, the folds and the comments) scrolls on its own, and starts at its
//     end, the latest comment, as a chat does;
//   - the scroll is kept across redraws (the panel redraws on every status pulse), and a thread scrolled to its end
//     stays at its end when a comment arrives, so a new message shows without a touch;
//   - the mouse wheel at either end of the thread's scroll moves the list instead, so the pointer resting on a long
//     thread never traps the wheel.
// When a thread OPENS (its task was drawn closed the time before), the list scrolls as little as it takes to show the
// line and the whole capped thread together. A thread that shows because a filter changed, or the panel opened, moves
// nothing: the scroll then is the one the user or ScrollToSelected chose.
//
// A subtask's thread opened in place inside its parent's thread is not capped (it is inside the parent's scroll), and
// the task window (TaskDetailWindow) has the task for itself, so neither changes.
public partial class TasksWindow
{
    // The Tag on every thread's frame (Thread), capped or in place.
    internal const string ThreadTag = "task-thread";

    // The smallest a capped thread gets, in DIP, so a very short panel still shows a comment and the box under it.
    const double ThreadMinCap = 120;

    // A wheel step's scroll is the list's own; this is only how close to an end counts as "at the end", in DIP.
    const double ThreadEndSlack = 0.5;

    // The capped threads drawn this time, by task: the thread's scroller, so the next redraw can read where it was
    // scrolled to, and its frame and line, so ScrollToSelected can show the two together.
    readonly Dictionary<Guid, (ScrollViewer Scroller, Border Frame, FrameworkElement Line)> cappedThreads = [];

    // Where each open thread was scrolled to, by task, kept across redraws, with whether that was its end and which
    // comment was its latest then. A thread at its end goes to the end again only when a NEW latest comment has come,
    // so a message arriving shows without a touch, while unfolding earlier comments, opening a subtask in place or
    // opening a long comment keeps the place the user was reading (review of this change: with "at the end" alone, a
    // thread that fitted counted as at its end, and the earlier comments just unfolded were scrolled away above).
    // A thread that closes is forgotten, so opening it again starts at the latest comment.
    readonly Dictionary<Guid, (double Offset, bool AtEnd, Guid? Latest)> threadScrolls = [];

    // Whether each task's thread was open the last time its line was drawn, so a thread that has just opened can be told
    // from one that was already open, or one that only came into the view through a filter.
    readonly Dictionary<Guid, bool> drawnExpanded = [];

    // The threads that opened and have not yet been revealed (RevealOnLoad). Kept until the reveal has run, not only for
    // the next draw: a second draw before any layout would otherwise build the thread anew, its first frame never loaded,
    // and the reveal would be lost (review of this change).
    readonly HashSet<Guid> threadsToReveal = [];

    /// <summary>
    /// Before a redraw takes the lines away: reads where each capped thread is scrolled to, and notes the threads that
    /// open in this draw (drawn closed last time, open now). `rows` are the top-level rows this draw will put lines for.
    /// </summary>
    void BeforeThreadsRedraw(IReadOnlyList<TaskRowView> rows)
    {
        // A scroller never laid out (two draws with no layout between) reads 0 for everything: its entry is left as it
        // was rather than overwritten with a made-up "at the top".
        cappedThreads.Where(t => t.Value.Scroller.ViewportHeight > 0).ToList().ForEach(t =>
        {
            var scroller = t.Value.Scroller;
            threadScrolls[t.Key] = (scroller.VerticalOffset, scroller.VerticalOffset >= scroller.ScrollableHeight - ThreadEndSlack,
                shownComments.GetValueOrDefault(t.Key) is [.., var latest] ? (Guid?)latest : null);
        });
        cappedThreads.Clear();
        rows.Where(r => r.Expanded && drawnExpanded.TryGetValue(r.Id, out var was) && !was).ToList().ForEach(r => threadsToReveal.Add(r.Id));
    }

    /// <summary>
    /// After the lines are drawn: remembers which threads were open, and forgets the scroll (and any reveal still to
    /// come) of a thread drawn closed.
    /// </summary>
    void AfterThreadsRedraw(IReadOnlyList<TaskRowView> rows)
    {
        drawnExpanded.Clear();
        rows.ToList().ForEach(r => drawnExpanded[r.Id] = r.Expanded);
        rows.Where(r => !r.Expanded).ToList().ForEach(r =>
        {
            threadScrolls.Remove(r.Id);
            threadsToReveal.Remove(r.Id);
        });
    }

    /// <summary>
    /// A top-level thread's inside, capped: the head fixed at the top, the comment box at the bottom and the body between
    /// them in a scroller of its own. The frame (the thread's Border) takes the cap as its MaxHeight, bound to the height
    /// of the list's view less the line's, so the two always fit together and the cap follows a resize of the panel or a
    /// line that wraps to two lines. `latest` is the last comment the body shows.
    /// </summary>
    UIElement CappedThread(Guid id, Border frame, FrameworkElement line, UIElement head, UIElement body, UIElement box, Guid? latest)
    {
        var scroller = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        // Where it was, or the end: for a thread just drawn, and for one that was at its end when a new comment came.
        // Both are queued and run at the scroller's first layout, which clamps them to what is there.
        if (threadScrolls.TryGetValue(id, out var kept) && !(kept.AtEnd && kept.Latest != latest)) scroller.ScrollToVerticalOffset(kept.Offset);
        else scroller.ScrollToEnd();
        scroller.PreviewMouseWheel += (_, e) => PassWheelAtEnd(scroller, e);
        cappedThreads[id] = (scroller, frame, line);

        // The cap: the view of the list the thread is in (the List's Scroller, or its Matrix cell's), less the line with
        // its margins, less the thread's own margins. FindAncestor from the frame finds that list, the nearest scroller
        // above it.
        var around = line.Margin.Top + line.Margin.Bottom + frame.Margin.Top + frame.Margin.Bottom;
        var cap = new MultiBinding { Converter = new ThreadCap(around) };
        cap.Bindings.Add(new Binding(nameof(ScrollViewer.ViewportHeight)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ScrollViewer), 1) });
        cap.Bindings.Add(new Binding(nameof(ActualHeight)) { Source = line });
        frame.SetBinding(MaxHeightProperty, cap);

        if (threadsToReveal.Contains(id)) RevealOnLoad(id, frame, line);

        var inside = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(box, Dock.Bottom);
        inside.Children.Add(head);
        inside.Children.Add(box);
        // Last, so it fills what the head and the box leave.
        inside.Children.Add(scroller);
        return inside;
    }

    // Once the thread just opened has its size, the list shows the line and the thread together (ShowWithLine). One
    // time only: the handler takes itself off, and the thread leaves threadsToReveal once it has been shown.
    void RevealOnLoad(Guid id, Border frame, FrameworkElement line)
    {
        void Reveal(object sender, RoutedEventArgs e)
        {
            frame.Loaded -= Reveal;
            threadsToReveal.Remove(id);
            ShowWithLine(frame, line);
        }
        frame.Loaded += Reveal;
    }

    // The list scrolls as little as it takes to show a thread's line and the thread together, from the top of the line's
    // margin to the bottom of the thread's. The cap makes that fit the view, so the line is never the part left out.
    static void ShowWithLine(Border frame, FrameworkElement line)
    {
        frame.UpdateLayout();
        if (!frame.IsVisible || !line.IsVisible) return;
        var bottom = frame.TranslatePoint(new Point(0, frame.ActualHeight + frame.Margin.Bottom), line).Y;
        line.BringIntoView(new Rect(0, -line.Margin.Top, line.ActualWidth, bottom + line.Margin.Top));
    }

    // ScrollToSelected's last step: a selected line inside a capped thread (a subtask's, with Subtasks unticked) is
    // brought out of the thread's own scroll too, and a selected task whose thread is open shows with all of it, not
    // just its line a third of the way down (review of this change: a bar click on a task the panel had not drawn left
    // the thread's bottom, comment box included, below the view).
    void ShowSelectedThread(Guid id, FrameworkElement selectedFrame)
    {
        selectedFrame.BringIntoView();
        if (cappedThreads.TryGetValue(id, out var thread)) ShowWithLine(thread.Frame, thread.Line);
    }

    // The wheel over a capped thread scrolls the thread, until the thread is at the end the wheel turns toward; from
    // there it scrolls the list the thread is in, as if the thread were not there. Without this the thread's scroller
    // takes every wheel step even when it has nowhere to go, and a thread as tall as the view would hold the list still.
    static void PassWheelAtEnd(ScrollViewer thread, MouseWheelEventArgs e)
    {
        var up = e.Delta > 0;
        var canMove = up ? thread.VerticalOffset > ThreadEndSlack : thread.VerticalOffset < thread.ScrollableHeight - ThreadEndSlack;
        if (canMove || ListAround(thread) is not { } list) return;
        e.Handled = true;
        list.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = thread });
    }

    // THE SUBTASK SHELF (task 452487b4): a capped thread's subtasks, in its head so they never scroll away under the
    // comments. It scrolls on its own past half the thread's cap, so one subtask opened in place (its own thread drawn
    // under its line, uncapped) leaves the parent's comments and comment box room; the wheel at its end moves the list.
    static ScrollViewer SubtaskShelf(UIElement subtasks, Border frame)
    {
        var shelf = new ScrollViewer
        {
            Content = subtasks,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        shelf.SetBinding(MaxHeightProperty, new Binding(nameof(MaxHeight)) { Source = frame, Converter = new HalfOf() });
        shelf.PreviewMouseWheel += (_, e) => PassWheelAtEnd(shelf, e);
        return shelf;
    }

    // Half a height, no cap staying no cap (the frame's MaxHeight is infinite until the list has a size).
    sealed class HalfOf : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double height && !double.IsInfinity(height) ? height / 2 : double.PositiveInfinity;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException("The shelf's cap is only ever read from the thread's.");
    }

    // The nearest scroller above a thread's own: the List's Scroller or the Matrix cell's.
    static ScrollViewer? ListAround(DependencyObject from) =>
        VisualTreeHelper.GetParent(from) is not { } parent ? null
        : parent as ScrollViewer ?? ListAround(parent);

    // The cap from the list's view height and the line's height. Before the list has a size (the first measure, or a
    // value the binding cannot read yet) there is no cap, so the thread does not flash at the minimum.
    sealed class ThreadCap(double around) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [double view, double line] && view > 0
                ? Math.Max(ThreadMinCap, view - line - around)
                : double.PositiveInfinity;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException("The thread's cap is only ever read from the list's size.");
    }
}
