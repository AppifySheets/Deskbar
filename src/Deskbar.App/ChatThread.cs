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
//     A run of one author's bubbles names the author once, under the last (task 85218923); the others show their time alone;
//   - Deskbar's own lines ("Resumed.", "Stopped, ...") are events, not messages: a small dim line centred in the thread;
//   - a day line ("Today", "25 Nov") stands above the first comment of each day, so the line under a bubble carries only the time of day, not the date;
//   - Edit and Settle sit beside the time under the bubble and show only while the mouse (or the keyboard) is on that
//     comment, so the thread reads clean at rest. Hidden, not collapsed, so nothing moves when they appear.
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
    /// The comments with <see cref="TaskCommentView.NamesAuthor"/> set: false on a bubble whose next comment is a bubble
    /// by the same author on the same day, so a run of one author's messages says "you" once, under its last bubble, and
    /// the bubbles above it carry their time alone. Petre (task 85218923): "if both are mine, write 'you' once".
    /// An event line, a day line or another author ends a run.
    /// </summary>
    internal static IReadOnlyList<TaskCommentView> Grouped(IReadOnlyList<TaskCommentView> comments) =>
        comments.Select((comment, index) => comment with
            {
                NamesAuthor = index == comments.Count - 1 || !SameRun(comment, comments[index + 1]),
            })
            .ToArray();

    // Two neighbouring comments are one run when both are bubbles by the same author on the same day. A settled bubble
    // (drawn dim once the settled fold is open) and an unsettled one do not share a run, so the name never sits only
    // under a dim bubble for a run of bright ones (review of this change).
    static bool SameRun(TaskCommentView comment, TaskCommentView next) =>
        IsBubble(comment) && IsBubble(next) && comment.Who == next.Who && comment.Author == next.Author
        && comment.Day == next.Day && comment.Settled == next.Settled;

    // A bubble, not an event line: everything but Deskbar's own lines, and a question Deskbar asked is a bubble too
    // (task 968c2216), as both thread hosts decide it.
    static bool IsBubble(TaskCommentView comment) => comment.Who != CommentAuthor.Deskbar || comment.Asks;

    /// <summary>The Tag of a day line, for the tests.</summary>
    internal const string DayTag = "chat-day";

    // "Today", centred, small and dim.
    static UIElement DayLine(string day) => new TextBlock
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
        var (meta, shown) = Meta(comment, actions, mine);
        var column = new StackPanel
        {
            // Transparent rather than none, so the whole bubble-and-time block takes the mouse and the actions do not
            // vanish on the way from the bubble down to them.
            Background = Brushes.Transparent,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Children = { bubble, meta },
        };
        // A bubble inside a run (its author named further down) sits a little closer to the next one, so the run reads as one.
        var row = new Grid { Margin = new Thickness(0, 0, 0, comment.NamesAuthor ? 4 : 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mine ? 1 : BubbleShare, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mine ? BubbleShare : 1, GridUnitType.Star) });
        Grid.SetColumn(column, mine ? 1 : 0);
        row.Children.Add(column);
        ShowOnHover(column, shown);
        return row;
    }

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

    // The line under a bubble: its author and time ("you · 14:28"), and the actions next to them on the bubble's inner
    // side (left under Petre's bubble, right under the others), hidden until hover.
    static (StackPanel Meta, UIElement Actions) Meta(TaskCommentView comment, IReadOnlyList<Button> actions, bool mine)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden };
        actions.ToList().ForEach(button => buttons.Children.Add(button));
        // "you · 21:21" under the last bubble of a run, "21:15" alone under the ones above it (task 85218923).
        var signed = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (comment.NamesAuthor) signed.Children.Add(AuthorName(comment));
        signed.Children.Add(Time(comment, HorizontalAlignment.Left, signed: comment.NamesAuthor));
        var meta = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(4, 0, 4, 0) };
        (mine ? new UIElement[] { buttons, signed } : [signed, buttons]).ToList().ForEach(part => meta.Children.Add(part));
        return (meta, buttons);
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

    // The time, "· 14:28" after an author's name, plain under an event.
    static TextBlock Time(TaskCommentView comment, HorizontalAlignment alignment, bool signed = false) => new()
    {
        Text = (signed ? "· " : "") + (comment.Time.Length > 0 ? comment.Time : comment.When),
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
