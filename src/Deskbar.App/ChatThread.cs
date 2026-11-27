using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Deskbar.App;

// A TASK'S THREAD DRAWN AS A CHAT (task 39bc9c4d, Petre: "make it prettier, like a chat", option A of the mockup
// https://claude.ai/artifact/WLQssE276smfMTkQVP3A2n). Until then every comment was an author line over its text with a
// hairline between comments (task aa42794f), which read like a log. Now:
//   - Petre's comments are bubbles on the RIGHT, in blue; a session's and the manager's are bubbles on the LEFT, warm
//     for Claude and violet for the manager;
//   - every bubble's author is named on the small line under it, beside its time ("you · 14:28", "Claude · 16:50").
//     Petre, on the first build: "doesn't say it's me; it's too much vertical separation". His bubbles carried no name
//     and Claude's had it on a line of its own inside the bubble; on the line under it the name costs no height.
//     A run of one author's bubbles names the author once, under the last (task 85218923), and the time goes with it
//     (task da1e1188, "you and time, both can be merged"): a bubble the next one follows within CloseGap has no line
//     under it at all, its time and actions shown beside it on hover; one followed later shows its time alone;
//   - Deskbar's own lines ("Resumed.", "Stopped, ...") are events, not messages: a small dim line centred in the thread;
//   - a day line ("Today", "25 Nov") stands above the first comment of each day, so the line under a bubble carries only the time of day, not the date;
//   - Edit and Settle sit beside the time under the bubble and show only while the mouse (or the keyboard) is on that
//     comment, so the thread reads clean at rest. Hidden, not collapsed, so nothing moves when they appear. A bare
//     bubble (no line under it) shows them with its time beside the bubble instead, on its inner side, drawn outside
//     the layout (a zero-width Canvas) so that nothing moves either.
// Both thread hosts use it: the tasks panel (TasksWindow.Thread) and the task's own window (TaskDetailWindow.DrawThread).
// They build a comment's CONTENT themselves (their markdown boxes, Show more, pictures) and hand it here to be placed.
static class ChatThread
{
    // Petre's bubble: the panel's selected blue (#2E5BAA) darkened so its light text stays easy to read.
    static readonly Brush MineFill = Frozen(0xFF, 0x23, 0x40, 0x6E);
    // A session's bubble: a dark shade of the Claude ink (#D9B98A) that names it.
    static readonly Brush ClaudeFill = Frozen(0xFF, 0x2B, 0x26, 0x20);
    // The manager's bubble: a dark shade of its violet (#B48CFF, IconCache.InManagerViolet).
    static readonly Brush ManagerFill = Frozen(0xFF, 0x29, 0x24, 0x36);
    static readonly Brush ClaudeName = Frozen(0xFF, 0xD9, 0xB9, 0x8A);
    static readonly Brush ManagerName = Frozen(0xFF, 0xB4, 0x8C, 0xFF);
    // "you" under Petre's bubble: a light shade of his bubble's blue.
    static readonly Brush MineName = Frozen(0xFF, 0x9D, 0xBB, 0xEB);
    static readonly Brush Dim = Frozen(0xFF, 0x8B, 0x8F, 0x99);

    // A thread's colours. Today's are Default, for the List and the task window; the tasks panel's Inbox page draws its
    // thread inside Using(InboxLook), blue-black on the Inbox's background (task 740a2231), so no other host changes.
    internal sealed record Look(Brush MineFill, Brush ClaudeFill, Brush ManagerFill, Brush MineName, Brush ClaudeName, Brush ManagerName, Brush Dim);
    static readonly Look Default = new(MineFill, ClaudeFill, ManagerFill, MineName, ClaudeName, ManagerName, Dim);
    internal static readonly Look InboxLook = new(Frozen(0xFF, 0x2A, 0x52, 0xB0), Frozen(0xFF, 0x1F, 0x25, 0x31), Frozen(0xFF, 0x1F, 0x25, 0x31),
        Frozen(0xFF, 0xB6, 0xBE, 0xCC), Frozen(0xFF, 0xB6, 0xBE, 0xCC), Frozen(0xFF, 0xB6, 0xBE, 0xCC), Frozen(0xFF, 0x8A, 0x94, 0xA6));
    [ThreadStatic] static Look? using_;
    static Look L => using_ ?? Default;

    /// <summary>Draws the threads built until the result is disposed in <paramref name="look"/>.</summary>
    internal static IDisposable Using(Look look)
    {
        var before = using_;
        using_ = look;
        return System.Reactive.Disposables.Disposable.Create(() => using_ = before);
    }

    // The share of the thread's width a bubble may take: the rest stays empty on the other side, which is what makes
    // the two sides read as two people. Five sixths, set as a 5:1 star split.
    const double BubbleShare = 5;

    /// <summary>
    /// The comments in order, each drawn by <paramref name="draw"/>, with a day line before the first comment of each day.
    /// A run of bubbles by one author names that author once, under its last bubble (see <see cref="Grouped"/>).
    /// </summary>
    public static IReadOnlyList<UIElement> WithDays(IReadOnlyList<TaskCommentView> comments, Func<TaskCommentView, UIElement> draw) =>
        Grouped(comments).Select((comment, index) => (comment, before: index == 0 ? null : comments[index - 1]))
            .SelectMany(x => x.comment.Day.Length > 0 && (x.before is null || x.before.Day != x.comment.Day)
                ? new[] { DayLine(x.comment.Day), draw(x.comment) }
                : new[] { draw(x.comment) })
            .ToArray();

    /// <summary>
    /// The comments with <see cref="TaskCommentView.Sign"/> set. A bubble whose next comment is a bubble by the same
    /// author on the same day is inside a run: Bare (no line under it) when that next one came within
    /// <see cref="CloseGap"/>, Timed (its time alone) when later. Every other bubble is Named, so a run of one author's
    /// messages says "you · 19:03" once, under its last bubble. Petre: "if both are mine, write 'you' once" (task
    /// 85218923), then "you and time, both can be merged" (task da1e1188). An event line, a day line or another author
    /// ends a run.
    /// </summary>
    internal static IReadOnlyList<TaskCommentView> Grouped(IReadOnlyList<TaskCommentView> comments)
    {
        var signs = SignsOf(comments.Select((comment, index) =>
            (JoinsNext: index < comments.Count - 1 && SameRun(comment, comments[index + 1]), comment.At)).ToArray());
        return comments.Select((comment, index) => comment with { Sign = signs[index] }).ToArray();
    }

    /// <summary>
    /// How long a stretch of one run's bubbles may span and still share one time, the time of its last bubble. Five
    /// minutes, the window Slack groups one person's messages by, so "19:03" under bubbles written from 19:02 is not a
    /// lie. Measured from the stretch's FIRST bubble, not bubble to bubble (review of this change): bubbles four minutes
    /// apart from 19:02 to 19:30 would otherwise all hang on "19:30", and a run that spans half an hour keeps a time
    /// for each stretch of it.
    /// </summary>
    internal static readonly TimeSpan CloseGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The sign of each bubble, in order, from whether it is in one run with the next (<c>JoinsNext</c>, false for the
    /// last) and when it was written. A bubble that does not join the next one is Named (the last of its run). One that
    /// does is Bare when the next falls inside its stretch (within <see cref="CloseGap"/> of the stretch's first bubble),
    /// and Timed when the next starts a new stretch. Shared by the threads (Grouped) and the Inbox's stream
    /// (ChatStreamPanel.Signs), so both split a run the same way.
    /// </summary>
    internal static IReadOnlyList<BubbleSign> SignsOf(IReadOnlyList<(bool JoinsNext, DateTimeOffset At)> bubbles)
    {
        // When each bubble's stretch began: the stretch of the bubble before it, when that one joins it and it falls
        // within CloseGap of that stretch's start; its own time otherwise. Built in order, each from the one before.
        var starts = bubbles.Aggregate(new List<DateTimeOffset>(), (list, bubble) =>
        {
            var index = list.Count;
            list.Add(index > 0 && bubbles[index - 1].JoinsNext && bubble.At - list[index - 1] <= CloseGap ? list[index - 1] : bubble.At);
            return list;
        });
        return bubbles.Select((bubble, index) =>
                !bubble.JoinsNext || index == bubbles.Count - 1 ? BubbleSign.Named
                : bubbles[index + 1].At - starts[index] <= CloseGap ? BubbleSign.Bare
                : BubbleSign.Timed)
            .ToArray();
    }

    // Two neighbouring comments are one run when both are bubbles by the same author on the same day. A settled bubble
    // (drawn dim once the settled fold is open) and an unsettled one do not share a run, so the name never sits only
    // under a dim bubble for a run of bright ones (review of this change).
    static bool SameRun(TaskCommentView comment, TaskCommentView next) =>
        IsBubble(comment) && IsBubble(next) && comment.Who == next.Who && comment.Author == next.Author
        && comment.Day == next.Day && comment.Settled == next.Settled;

    // A bubble, not an event line: everything but Deskbar's own lines, and a question Deskbar asked is a bubble too
    // (task 968c2216), as both thread hosts decide it.
    static bool IsBubble(TaskCommentView comment) => comment.Who != CommentAuthor.Deskbar || comment.Asks;

    /// <summary>
    /// A bubble that <see cref="Message"/> drew, signed again in place as a run decides now: its author's name shown or
    /// collapsed, its time with or without the "· ", the line under it shown or taken away (its time then beside the
    /// bubble on hover), and the gap under it, exactly as a bubble drawn with that <see cref="TaskCommentView.Sign"/>
    /// would look. For the Inbox's stream (ChatStreamPanel), which never re-creates a message it has drawn, so a message
    /// that a new one joins loses its line where it is. Anything <see cref="Message"/> did not draw is left as it is.
    /// </summary>
    internal static void Sign(FrameworkElement row, BubbleSign sign)
    {
        if (Drawn.TryGetValue(row, out var parts)) parts.Apply(sign);
    }

    // The parts of a drawn bubble that its sign changes, kept beside the row rather than found again by walking its tree
    // (the row's Tag stays free for its host). Weak, so a row the thread drops takes its parts with it.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, Signing> Drawn = new();

    // One bubble's signing parts: the row (its bottom gap), the line under the bubble (meta) with the author's name and
    // time on it, the time beside the bubble (side, for a bare bubble), and the actions, which sit with whichever shows.
    sealed class Signing(Grid row, StackPanel column, StackPanel meta, TextBlock author, TextBlock time, Border side,
        StackPanel sideTimes, StackPanel buttons, bool mine)
    {
        BubbleSign sign = BubbleSign.Named;

        public void Apply(BubbleSign now)
        {
            sign = now;
            author.Visibility = sign == BubbleSign.Named ? Visibility.Visible : Visibility.Collapsed;
            var plain = time.Text.StartsWith("· ", StringComparison.Ordinal) ? time.Text[2..] : time.Text;
            time.Text = sign == BubbleSign.Named ? "· " + plain : plain;
            // The actions go where the time shows: on the line under the bubble, or beside a bare bubble. Always on the
            // inner side (left of Petre's bubble, right of the others), next to the time.
            Move(buttons, meta, mine);
            // A bubble inside a run sits a little closer to the next one, so the run reads as one.
            row.Margin = new Thickness(0, 0, 0, sign == BubbleSign.Named ? 4 : 2);
            Refresh();
        }

        /// <summary>
        /// Shows or hides the actions (and a bare bubble's time) for where the mouse and the keyboard are now. A bare
        /// bubble shows its time and actions BESIDE it when the empty side of the thread has room for them. A long
        /// bubble fills its five sixths and leaves the sixth beyond it, which in a narrow panel is narrower than the
        /// time with Edit and Settle; drawn there they were cut off at the thread's edge, out of the mouse's reach
        /// (review of this change). Then the line under the bubble shows instead while the mouse is on it, as under a
        /// timed bubble, and goes again when the mouse leaves.
        /// </summary>
        public void Refresh()
        {
            var over = column.IsMouseOver || column.IsKeyboardFocusWithin;
            var beside = sign == BubbleSign.Bare && over && FitsBeside();
            if (sign == BubbleSign.Bare && over) Move(buttons, beside ? sideTimes : meta, mine);
            meta.Visibility = sign != BubbleSign.Bare || (over && !beside) ? Visibility.Visible : Visibility.Collapsed;
            side.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
            buttons.Visibility = over ? Visibility.Visible : Visibility.Hidden;
        }

        // Whether the time and actions fit between the bubble's inner edge and the row's edge on that side.
        bool FitsBeside()
        {
            if (row.ActualWidth <= 0 || PresentationSource.FromVisual(column) is null) return false;
            // Measured where they are, without moving the actions (moving a focused button would take the keyboard from
            // it): the panel inside the side's Border, which the Border's collapse would measure as nothing, plus the
            // actions when they are not in it yet, plus the Border's padding.
            var any = new Size(double.PositiveInfinity, double.PositiveInfinity);
            sideTimes.Measure(any);
            buttons.Measure(any);
            var width = sideTimes.DesiredSize.Width + (buttons.Parent == sideTimes ? 0 : buttons.DesiredSize.Width)
                + side.Padding.Left + side.Padding.Right;
            var left = column.TranslatePoint(new Point(0, 0), row).X;
            var room = mine ? left : row.ActualWidth - left - column.ActualWidth;
            return width <= room;
        }

        // Takes the actions from where they are and puts them on the inner end of the target: first in Petre's (its
        // left), last in the others' (its right). The time is the target's other child, so it stays beside the bubble.
        static void Move(StackPanel buttons, StackPanel target, bool mine)
        {
            if (buttons.Parent == target) return;
            (buttons.Parent as Panel)?.Children.Remove(buttons);
            if (mine) target.Children.Insert(0, buttons);
            else target.Children.Add(buttons);
        }
    }

    /// <summary>The Tag of a day line, for the tests.</summary>
    internal const string DayTag = "chat-day";

    // "Today", centred, small and dim.
    internal static TextBlock DayLine(string day) => new TextBlock
    {
        Tag = DayTag,
        Text = day,
        FontSize = 10,
        Foreground = L.Dim,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 1, 0, 3),
    };

    /// <summary>
    /// One comment as a bubble: <paramref name="content"/> (the caller's body, Show more and pictures) inside, and under it
    /// its author and time with <paramref name="actions"/> (Edit, Settle) beside them, shown on hover.
    /// </summary>
    public static FrameworkElement Message(TaskCommentView comment, UIElement content, IReadOnlyList<Button> actions)
    {
        var mine = comment.Who == CommentAuthor.You;
        var bubble = new Border
        {
            Background = mine ? L.MineFill : comment.Who == CommentAuthor.Manager ? L.ManagerFill : L.ClaudeFill,
            // Rounded all round but for the corner nearest its author's side, the "tail" of a chat bubble.
            CornerRadius = mine ? new CornerRadius(12, 12, 3, 12) : new CornerRadius(12, 12, 12, 3),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = content,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        actions.ToList().ForEach(button => buttons.Children.Add(button));
        var (meta, author, time) = Meta(comment, mine);
        // The time beside a bare bubble (one with no line under it, task da1e1188), on its inner side and level with its
        // foot, where the line under it would have started. It stands in a zero-width Canvas at the bubble's inner edge,
        // so it is drawn over the empty side of the thread and takes no room: the bubble is laid out exactly as without
        // it, and nothing moves when it shows on hover. Transparent padding bridges the gap to the bubble, so the mouse
        // can travel from the bubble to Edit without the hover ending on the way.
        var sideTimes = new StackPanel { Orientation = Orientation.Horizontal, Children = { Time(comment, HorizontalAlignment.Left) } };
        var side = new Border
        {
            Tag = SideTag,
            Background = Brushes.Transparent,
            Padding = mine ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0),
            Child = sideTimes,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetBottom(side, 0);
        if (mine) Canvas.SetRight(side, 0);
        else Canvas.SetLeft(side, 0);
        var line = new Grid
        {
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Children =
            {
                bubble,
                new Canvas { Width = 0, HorizontalAlignment = mine ? HorizontalAlignment.Left : HorizontalAlignment.Right, Children = { side } },
            },
        };
        var column = new StackPanel
        {
            // Transparent rather than none, so the whole bubble-and-time block takes the mouse and the actions do not
            // vanish on the way from the bubble down to them.
            Background = Brushes.Transparent,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Children = { line, meta },
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mine ? 1 : BubbleShare, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mine ? BubbleShare : 1, GridUnitType.Star) });
        Grid.SetColumn(column, mine ? 1 : 0);
        row.Children.Add(column);
        // The actions show while the mouse (or the keyboard) is on the comment; a bare bubble's time shows with them
        // (Signing.Refresh).
        var signing = new Signing(row, column, meta, author, time, side, sideTimes, buttons, mine);
        column.MouseEnter += (_, _) => signing.Refresh();
        column.MouseLeave += (_, _) => signing.Refresh();
        column.IsKeyboardFocusWithinChanged += (_, _) => signing.Refresh();
        Drawn.AddOrUpdate(row, signing);
        signing.Apply(comment.Sign);
        return row;
    }

    /// <summary>The Tag of the time beside a bare bubble (shown on hover), for the tests.</summary>
    internal const string SideTag = "chat-side-time";

    /// <summary>
    /// One of Deskbar's own lines as an event: <paramref name="body"/> centred and dim, with the time under it unless the
    /// caller put it in the text (<paramref name="timeInBody"/>, a one-line event reads "Resumed. · 09:08"), and Settle at
    /// the right end, shown on hover.
    /// </summary>
    public static FrameworkElement Event(TaskCommentView comment, UIElement body, bool timeInBody, IReadOnlyList<Button> actions)
    {
        var middle = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { body } };
        if (!timeInBody) middle.Children.Add(Time(comment, HorizontalAlignment.Center));
        var end = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
        actions.ToList().ForEach(button => end.Children.Add(button));
        // Three columns, the outer two of one width, so the event stays centred on the thread with Settle at its end.
        var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(end, 2);
        row.Children.Add(middle);
        row.Children.Add(end);
        ShowOnHover(row, end);
        return row;
    }

    /// <summary>Whether a Deskbar line is short enough to carry its time at its end: one line of text, no pictures.</summary>
    public static bool TimeFitsInline(string text) => !text.Trim().Contains('\n') && text.Trim().Length is > 0 and <= 80;

    // The line under a bubble: its author and time ("you · 14:28"). The sign decides what of it shows (Signing.Apply):
    // "you · 21:21" under the last bubble of a run, "21:15" alone under one followed later (task 85218923), and no line
    // at all under one followed within CloseGap (task da1e1188). Signing.Apply also puts the actions on its inner side
    // (left under Petre's bubble, right under the others).
    static (StackPanel Meta, TextBlock Author, TextBlock Time) Meta(TaskCommentView comment, bool mine)
    {
        var author = AuthorName(comment);
        var time = Time(comment, HorizontalAlignment.Left);
        var signed = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { author, time },
        };
        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 0),
            Children = { signed },
        };
        return (meta, author, time);
    }

    /// <summary>The Tag of the author's name under a bubble, for the tests.</summary>
    internal const string AuthorTag = "comment-author";

    // "you", "Claude" or "Manager", in the colour of its side.
    static TextBlock AuthorName(TaskCommentView comment) => new()
    {
        Tag = AuthorTag,
        Text = comment.Author,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = comment.Who switch
        {
            CommentAuthor.You => L.MineName,
            CommentAuthor.Manager => L.ManagerName,
            _ => L.ClaudeName,
        },
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 0, 0),
    };

    // The time, plain; Signing.Apply puts "· " before it when it follows the author's name.
    static TextBlock Time(TaskCommentView comment, HorizontalAlignment alignment) => new()
    {
        Text = comment.Time.Length > 0 ? comment.Time : comment.When,
        ToolTip = comment.FullWhen.Length > 0 ? comment.FullWhen : null,
        FontSize = 10,
        Foreground = L.Dim,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = alignment,
        Margin = new Thickness(4, 0, 4, 0),
    };

    // The actions show while the mouse is on the comment or the keyboard is inside it, and hide again after.
    static void ShowOnHover(FrameworkElement area, UIElement actions)
    {
        void Refresh() => actions.Visibility = area.IsMouseOver || area.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
        area.MouseEnter += (_, _) => Refresh();
        area.MouseLeave += (_, _) => Refresh();
        area.IsKeyboardFocusWithinChanged += (_, _) => Refresh();
    }

    /// <summary>
    /// Makes a markdown box as wide as its text and no wider, so a short comment gets a short bubble. A RichTextBox
    /// always takes all the width it is given (a FlowDocument does not size to its content), so the width is measured
    /// here and set as the box's MaxWidth; the bubble around it then follows. A document with anything but plain
    /// paragraphs (a list, a table, a picture inline) keeps the full width, where wrapping matters more than a snug fit.
    /// </summary>
    /// <remarks>
    /// It also takes away the box's minimum height. The app's theme gives every RichTextBox a MinHeight of 32 DIP, sized
    /// for an editing box, so a one-line comment (15 DIP of text) stood in a bubble twice its height: the "too much
    /// vertical separation" of Petre's first look at the chat (measured in the panel: 32 DIP tall, 15.3 of extent).
    /// </remarks>
    public static void FitWidth(RichTextBox box)
    {
        box.MinHeight = 0;
        var width = NaturalWidth(box.Document);
        if (!double.IsInfinity(width)) box.MaxWidth = width;
    }

    internal static double NaturalWidth(FlowDocument document) =>
        document.Blocks.All(block => block is Paragraph paragraph && !paragraph.Inlines.OfType<InlineUIContainer>().Any())
            // Measured in the paragraph's regular weight; bold or code runs are a little wider, so a share and a few DIP
            // of slack keep the last word from wrapping, and the RichTextBox's own page padding is in the slack too.
            ? document.Blocks.Cast<Paragraph>().Select(Widest).DefaultIfEmpty(0).Max() * 1.08 + 14
            : double.PositiveInfinity;

    // The widest line of a paragraph, its line breaks taken as the ends of lines. Each run is measured in its OWN font
    // (review of this change): inline code is Cascadia Mono or Consolas and bold is heavier, both wider than the
    // paragraph's regular Segoe UI, and measuring the whole text in that one font left a comment with code in it a
    // word short, wrapping its last word.
    static double Widest(Paragraph paragraph) =>
        LineWidths(paragraph.Inlines).DefaultIfEmpty(0).Max();

    // The widths of the lines the inlines make, a LineBreak (or a newline inside a run) ending each.
    static IEnumerable<double> LineWidths(IEnumerable<Inline> inlines) =>
        Pieces(inlines).Aggregate(new List<double> { 0 }, (lines, piece) =>
        {
            if (piece is null) lines.Add(0);
            else lines[^1] += piece.Value;
            return lines;
        });

    // Each run's width, with null marking the end of a line. Spans (bold, italic, links) are walked into.
    static IEnumerable<double?> Pieces(IEnumerable<Inline> inlines) =>
        inlines.SelectMany(inline => inline switch
        {
            Run run => run.Text.Replace("\r", "").Split('\n')
                .SelectMany((part, index) => index == 0 ? new double?[] { Measure(run, part) } : [null, Measure(run, part)]),
            LineBreak => new double?[] { null },
            Span span => Pieces(span.Inlines),
            _ => [],
        });

    static double Measure(Run run, string text) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch), run.FontSize, Brushes.Black, 1.0)
            .WidthIncludingTrailingWhitespace;

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
