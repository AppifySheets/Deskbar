using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Deskbar.App;

// A TASK'S THREAD DRAWN AS A CHAT (task 39bc9c4d, Petre: "make it prettier, like a chat", option A of the mockup
// https://claude.ai/artifact/WLQssE276smfMTkQVP3A2n). Until then every comment was an author line over its text with a
// hairline between comments (task aa42794f), which read like a log. Now:
//   - Petre's comments are bubbles on the RIGHT, in blue, with no name (they are his);
//   - a session's and the manager's are bubbles on the LEFT, warm for Claude and violet-named for the manager, named
//     at the top of the bubble unless the comment above is by the same author on the same day;
//   - Deskbar's own lines ("Resumed.", "Stopped, ...") are events, not messages: a small dim line centred in the thread;
//   - a day line ("Today", "25 Nov") stands above the first comment of each day, so a bubble carries only its time;
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
    static readonly Brush Dim = Frozen(0xFF, 0x8B, 0x8F, 0x99);

    // The share of the thread's width a bubble may take: the rest stays empty on the other side, which is what makes
    // the two sides read as two people. Five sixths, set as a 5:1 star split.
    const double BubbleShare = 5;

    /// <summary>
    /// The comments in order, each drawn by <paramref name="draw"/>, with a day line before the first comment of each day.
    /// <paramref name="draw"/> is told whether the bubble should carry its author's name: not when the comment right above
    /// is by the same author on the same day, the way a chat groups a run of messages.
    /// </summary>
    public static IReadOnlyList<UIElement> WithDays(IReadOnlyList<TaskCommentView> comments, Func<TaskCommentView, bool, UIElement> draw) =>
        comments.Select((comment, index) => (comment, before: index == 0 ? null : comments[index - 1]))
            .SelectMany(x =>
            {
                var newDay = x.before is null || x.before.Day != x.comment.Day;
                var named = newDay || x.before!.Who != x.comment.Who;
                var drawn = draw(x.comment, named);
                return newDay && x.comment.Day.Length > 0 ? new[] { DayLine(x.comment.Day), drawn } : new[] { drawn };
            })
            .ToArray();

    /// <summary>The Tag of a day line, for the tests.</summary>
    internal const string DayTag = "chat-day";

    // "Today", centred, small and dim.
    static UIElement DayLine(string day) => new TextBlock
    {
        Tag = DayTag,
        Text = day,
        FontSize = 10,
        Foreground = Dim,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 6),
    };

    /// <summary>
    /// One comment as a bubble: <paramref name="content"/> (the caller's body, Show more and pictures) inside, its name on
    /// top for a session or the manager when <paramref name="named"/>, and under it the time with
    /// <paramref name="actions"/> (Edit, Settle) beside it, shown on hover.
    /// </summary>
    public static FrameworkElement Message(TaskCommentView comment, bool named, UIElement content, IReadOnlyList<Button> actions)
    {
        var mine = comment.Who == CommentAuthor.You;
        var inside = new StackPanel();
        if (named && !mine)
            inside.Children.Add(new TextBlock
            {
                Text = comment.Author,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = comment.Who == CommentAuthor.Manager ? ManagerName : ClaudeName,
                Margin = new Thickness(0, 0, 0, 1),
            });
        inside.Children.Add(content);
        var bubble = new Border
        {
            Background = mine ? MineFill : comment.Who == CommentAuthor.Manager ? ManagerFill : ClaudeFill,
            // Rounded all round but for the corner nearest its author's side, the "tail" of a chat bubble.
            CornerRadius = mine ? new CornerRadius(12, 12, 3, 12) : new CornerRadius(12, 12, 12, 3),
            Padding = new Thickness(10, 5, 10, 6),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = inside,
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
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
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
        var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 8) };
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

    // The line under a bubble: the time, and the actions next to it on the bubble's inner side (left of the time under
    // Petre's bubble, right of it under the others), hidden until hover.
    static (StackPanel Meta, UIElement Actions) Meta(TaskCommentView comment, IReadOnlyList<Button> actions, bool mine)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden };
        actions.ToList().ForEach(button => buttons.Children.Add(button));
        var time = Time(comment, HorizontalAlignment.Left);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(4, 1, 4, 0) };
        (mine ? new UIElement[] { buttons, time } : [time, buttons]).ToList().ForEach(part => meta.Children.Add(part));
        return (meta, buttons);
    }

    static TextBlock Time(TaskCommentView comment, HorizontalAlignment alignment) => new()
    {
        Text = comment.Time.Length > 0 ? comment.Time : comment.When,
        ToolTip = comment.FullWhen.Length > 0 ? comment.FullWhen : null,
        FontSize = 10,
        Foreground = Dim,
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
    public static void FitWidth(RichTextBox box)
    {
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
