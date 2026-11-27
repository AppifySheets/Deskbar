using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// ONE LINE OF THE INBOX (Deskbar task 740a2231, slice 1; spec "A line"; the mockup's .cv). From the left: the state mark in
// a 36 DIP rounded square, then the title (two rows at most, whole in the tooltip) with the workspace chip at the end of
// its first row, then one line of text, and at the right the time with the small marks under it.
//
//   the mark     one shape per state (InboxMarks). A note's mark IS its tick box, and so is a finished task's tick: a
//                click closes it, with the six seconds of Undo the List gives (TickOf's Undo sits in its place
//                meanwhile). Every other mark is a picture.
//   the title    13.5, bold when the task needs you or is important, italic when urgent (Ruling 59), struck through in
//                its undo window. A subtask drawn under its parent is indented and starts with "↳".
//   the chip     the workspace's full name with a dot in its colour; a click opens the same move menu as the List's chip
//                (OpenMoveMenu).
//   the text     the question while it waits, else the status line (three dots in front while Claude works), else the
//                description's first line; "no live session" as the List says it (the row's Detail).
//   the time     how long the state has lasted, in the panel's short form ("34m", "2h3m"; TaskAge), amber when the task
//                needs you, rewritten once a minute (RefreshInboxTimes).
//   small marks  important, urgent, a pull request, the subtasks' count, a related or duplicate link, the comments' count.
//   buttons      while the pointer is on the line, and while it is the selected one: Open Claude or Start Claude on this,
//                Reevaluate, Open in window, Snooze, Close, and "⋯" for the line's whole menu (OpenMenuFor), which a
//                right-click anywhere on the line opens too.
//
// The line's own wiring is the List line's (TasksWindow.Line), calling the same methods: a click opens the task's page
// (Actions.OpenPage), a double-click goes to its workspace and session (Actions.Go), a press arms the drag (DragPress,
// dragLines; the lines are in the List's own panel, so the drag code sees them as List lines), and every button takes the
// click guard. Only the drawing is the Inbox's.
public partial class TasksWindow
{
    // The lines on show: the line, its mark's square and its buttons, so the selection is painted in place
    // (PaintInboxSelection); and each line's time, rewritten once a minute.
    readonly Dictionary<Guid, (Button Line, Border Tile, FrameworkElement Buttons)> inboxLines = [];
    readonly Dictionary<Guid, (TextBlock Text, DateTimeOffset Since)> inboxTimes = [];

    internal const string InboxMarkTag = "inbox-mark";
    internal const string InboxTimeTag = "inbox-time";
    internal const string InboxTextTag = "inbox-text";
    internal const string InboxButtonsTag = "inbox-buttons";

    /// <summary>The automation name of the Close button among an Inbox line's buttons.</summary>
    internal const string CloseTaskText = "Close task";

    Button InboxLineOf(TaskRowView row, InboxLine line)
    {
        var needsYou = line.Mark is InboxMark.Asks or InboxMark.WentQuiet;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = MarkTile(row, line);
        tile.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(tile);

        var middle = new StackPanel { Margin = new Thickness(11, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
        var first = new DockPanel { LastChildFill = true };
        var chip = InboxChip(row);
        DockPanel.SetDock(chip, Dock.Right);
        first.Children.Add(chip);
        first.Children.Add(renaming == row.Id ? TextOf(row).Title : InboxTitle(row, needsYou));
        middle.Children.Add(first);
        if (InboxText(row, line) is { } text) middle.Children.Add(text);
        if (row.Problem is { Length: > 0 } problem) middle.Children.Add(ProblemOf(problem));
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var time = new TextBlock
        {
            Tag = InboxTimeTag,
            Text = TaskAge.Format(Now() - line.Since),
            FontSize = 12,
            Foreground = needsYou ? InboxLook.Amber : InboxLook.Ink3,
            FontWeight = needsYou ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = $"{InboxMarks.WordsOf(line.Mark)} since {line.Since.ToLocalTime():d MMM, HH:mm}",
        };
        inboxTimes[row.Id] = (time, line.Since);
        right.Children.Add(time);
        right.Children.Add(SmallMarks(row, line));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var buttons = LineButtonsOf(row);
        Grid.SetColumn(buttons, 1);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        // A line in its undo window is quiet, all but its Undo.
        if (row.Closing) grid.Children.OfType<UIElement>().Where(part => !ReferenceEquals(part, tile)).ToList().ForEach(part => part.Opacity = 0.45);

        var button = new Button
        {
            Tag = row.Id,
            Content = new Border { Padding = new Thickness(8), Child = grid },
            Template = (ControlTemplate)FindResource("InboxLineButton"),
            Background = Brushes.Transparent,
            Margin = new Thickness(row.Nested ? 30 : 6, 0, 6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = row.Title,
        };
        AutomationProperties.SetName(button, row.Title);
        AutomationProperties.SetItemStatus(button, InboxMarks.WordsOf(line.Mark));
        inboxLines[row.Id] = (button, tile, buttons);
        button.MouseEnter += (_, _) => buttons.Visibility = Visibility.Visible;
        button.MouseLeave += (_, _) => buttons.Visibility = Picked(row.Id) ? Visibility.Visible : Visibility.Hidden;
        // The List line's press (TasksWindow.Line): a double-click goes to the task's workspace and session, a single press
        // arms the drag, and the click guard counts the press.
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && !row.Closing && row.Status != TaskStatus.Closed && renaming != row.Id && IsTheLinesOwn(button, e.OriginalSource))
            {
                e.Handled = true;
                Actions?.Go(row.Id);
                return;
            }
            pressClicks = e.ClickCount;
            DragPress(row.Id, DragMouse.Position(e, DragSurface));
        };
        dragLines[row.Id] = (row.Quadrant, row.Status.IsPinned() && !row.Closing, row.Closing || row.Status == TaskStatus.Closed, row.Nested,
            row.Closing ? TaskDropTarget.Part.None : TaskDropTarget.PartOf(row.Status, row.Delivery.Shipping));
        button.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (renaming == row.Id) return;
            e.Handled = true;
            OpenMenuFor(row.Id, atPointer: true);
        };
        button.Click += (_, _) =>
        {
            if (ClickIgnored() || row.Closing || renaming == row.Id) return;
            OpenInboxPage(row.Id);
        };
        return button;
    }

    // The state mark in its rounded square. A note's square is its tick box (Close, with Undo); a line in its undo window
    // has the Undo sign there, as the List's tick column has.
    Border MarkTile(TaskRowView row, InboxLine line)
    {
        var tile = new Border
        {
            Tag = InboxMarkTag,
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(11),
            Background = InboxLook.Raised,
            ToolTip = InboxMarks.WordsOf(line.Mark),
        };
        var icon = InboxMarks.Icon(InboxMarks.IconOf(line.Mark), 18, InboxMarks.InkOf(line.Mark), InboxLook.Raised, turning: line.Mark == InboxMark.Working);
        if (row.Closing)
            tile.Child = Centred(TickOf(row.Id, row.Status, row.Closing, ""));
        else if (line.Mark is InboxMark.Note or InboxMark.Finished or InboxMark.FinishedNotRunning)
        {
            // The note's box closes it (TickOf's Close: Actions.Close, the click guard, the board's refusal for a parent with
            // open subtasks), drawn as the mockup's empty box. A finished task's tick closes it the same way: it is drawn
            // as a tick in a circle, which reads as a tick box, and a click on it used to open the page instead (Luka on
            // build 4: "I had impression that if task is finished I could click on this tick and task would close").
            var close = Small(icon, "Close", () => Actions?.Close(row.Id), TaskViews.TickTip(row.Claudes, row.SubtaskCount, row.ClaudeWorking));
            close.Padding = new Thickness(0);
            close.Width = 36;
            close.Height = 36;
            close.HorizontalAlignment = HorizontalAlignment.Stretch;
            tile.Child = close;
        }
        else tile.Child = Centred(icon);
        return tile;
    }

    static FrameworkElement Centred(UIElement part) =>
        new Grid { Children = { part }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    // The title: two rows at most, ending in "…" when it needs more (the whole title is the line's tooltip).
    TextBlock InboxTitle(TaskRowView row, bool needsYou)
    {
        var title = new TextBlock
        {
            Tag = TitleTag,
            FontSize = 13.5,
            Foreground = InboxLook.Ink,
            FontWeight = needsYou || row.Important ? FontWeights.Bold : FontWeights.Medium,
            FontStyle = row.Urgent ? FontStyles.Italic : FontStyles.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 36,
            TextDecorations = row.Closing ? TextDecorations.Strikethrough : null,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (row.Nested || row.ParentTitle is not null)
            title.Inlines.Add(new Run(row.Nested ? "↳ " : $"↳ {row.ParentTitle} › ") { Foreground = InboxLook.Ink3, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal });
        title.Inlines.Add(new Run(row.Title.ReplaceLineEndings(" ")));
        return title;
    }

    // The workspace chip: the full name and a dot in its colour, in a quiet pill; a click opens the move menu.
    UIElement InboxChip(TaskRowView row)
    {
        var named = row.Place.Workspace.HasValue;
        var pill = new StackPanel { Orientation = Orientation.Horizontal };
        if (named)
            pill.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = row.Place.Colour ?? InboxLook.Ink3, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        pill.Children.Add(Text(named ? row.Place.Name : "no workspace", 12, named ? InboxLook.Ink2 : InboxLook.Ink3));
        if (row.Closing)
            return new Border { Child = pill, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Button? chip = null;
        chip = InboxButton(pill, PlaceChipName, () => OpenMoveMenu(row.Id, row.Place.Workspace, chip), new Thickness(7, 1, 8, 1),
            InboxLook.Surface, 10, InboxLook.StrongLine, ClickIgnored);
        chip.Margin = new Thickness(8, 0, 0, 0);
        chip.VerticalAlignment = VerticalAlignment.Top;
        chip.ToolTip = $"{row.Place.Name}: move it to another workspace";
        return chip;
    }

    // The one line of text under the title, or nothing when the task has nothing to say.
    FrameworkElement? InboxText(TaskRowView row, InboxLine line)
    {
        var words = row.Detail.Length > 0 ? FloatingBar.FirstLine(TaskViews.PlainInline(row.Detail)) : TaskViews.PlainFirstLine(row.Description);
        if (words.Length == 0) return null;
        var text = new TextBlock
        {
            Tag = InboxTextTag,
            Text = words,
            FontSize = 12.5,
            Foreground = InboxLook.Ink2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (line.Mark != InboxMark.Working) return new Border { Child = text, Margin = new Thickness(0, 1, 0, 0) };
        // Three dots in front while Claude is working (the mockup's .dots), still: a moving thing in a list read all day
        // pulls the eye away from what needs it.
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Enumerable.Range(0, 3).ToList().ForEach(i => dots.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 5, Height = 5, Fill = InboxLook.Blue, Opacity = 0.45 + 0.25 * i, Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
        }));
        var dock = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(dots, Dock.Left);
        dock.Children.Add(dots);
        dock.Children.Add(text);
        return dock;
    }

    // The small marks under the time: important, urgent, a pull request, subtasks, a link, comments.
    static StackPanel SmallMarks(TaskRowView row, InboxLine line)
    {
        var marks = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0), MinHeight = 13 };
        void Add(string icon, string tip, int? count = null)
        {
            var part = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(marks.Children.Count == 0 ? 0 : 6, 0, 0, 0), ToolTip = tip, Background = Brushes.Transparent };
            part.Children.Add(InboxMarks.Icon(icon, 13, InboxLook.Ink3));
            if (count is { } n) part.Children.Add(Text($"{n}", 12, InboxLook.Ink3, FontWeights.Normal, new Thickness(3, -2, 0, -2)));
            AutomationProperties.SetName(part, tip);
            marks.Children.Add(part);
        }
        if (row.Important) Add("flag", "Important");
        if (row.Urgent) Add("bolt", "Urgent");
        if (row.PullRequests.Count > 0) Add("pr", row.PullRequests.Count == 1 ? "A pull request" : $"{row.PullRequests.Count} pull requests");
        if (row.SubtaskCount > 0) Add("list", TaskViews.SubtaskCountText(row.SubtaskCount), row.SubtaskCount);
        if (line.Linked) Add("link", "Linked to another task");
        if (row.CommentCount > 0) Add("comment", row.CommentCount == 1 ? "1 comment" : $"{row.CommentCount} comments", row.CommentCount);
        return marks;
    }

    // The buttons over the line's right end while the pointer is on it or it is selected (spec, "A line"): each calls
    // what the List's line or its menu calls.
    FrameworkElement LineButtonsOf(TaskRowView row)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        var open = row.Status != TaskStatus.Closed && !row.Closing;
        void Add(string icon, string name, Action<Button> press, string tip)
        {
            Button? button = null;
            button = GlyphButton(icon, name, () => press(button!), tip, size: 26, ignoredWhen: ClickIgnored);
            strip.Children.Add(button);
        }
        if (OpensClaude(row)) Add("terminal", OpenClaudeText, _ => Actions?.Go(row.Id), "Go to this task's Claude session, resuming it first if it has ended");
        else if (open && StartsClaude(row) && Actions?.StartClaude is { } start)
            Add("play", StartClaudeText, _ => start(row.Id), "Start a background Claude session on this task");
        if (ReevaluatesOnLine(row)) Add("refresh", ReevaluateText, _ => Actions?.Reevaluate?.Invoke(row.Id), ReevaluateTip);
        if (open && Actions?.OpenWindow is { } openWindow)
            Add("open", OpenInWindowText, _ => openWindow(row.Id), "Read and edit the whole task in a window of its own");
        if (open && Actions?.Snooze is not null) Add("snooze", "Snooze", b => OpenSnoozeMenu(row.Id, b), "Hide it for a while");
        if (open) Add("checked", CloseTaskText, _ => Actions?.Close(row.Id), TaskViews.TickTip(row.Claudes, row.SubtaskCount, row.ClaudeWorking));
        if (!row.Closing)
        {
            Add("more", "More", _ => OpenMenuFor(row.Id), "Edit, status, move, delete");
            menuButtons[row.Id] = (Button)strip.Children[^1];
        }
        return new Border
        {
            Tag = InboxButtonsTag,
            Child = strip,
            Background = InboxLook.RaisedHot,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -4, 0),
            Visibility = Picked(row.Id) ? Visibility.Visible : Visibility.Hidden,
        };
    }

    // A TASK JUST ADDED FROM THE PANEL (the tester's L1; TasksBoard.ShowAdded calls Glow, then ScrollToSelected). In the
    // List its line is picked, glows and is scrolled to; in the Inbox it may be in another tab than the one on show, so:
    //   - no page open: the tab that holds it comes on show, and its line glows (TaskGlow, on the Inbox's selected fill)
    //     and is brought into view, once it is drawn: a redraw held by a press in flight (MustWait) draws it later, and
    //     the glow waits for that draw (inboxGlowPending);
    //   - a page open (a subtask added from the page's thread): the page and the tab stay as they are.
    // The ScrollToSelected that follows is no explicit "show this task", so it opens no page (inboxJustAdded). A task
    // added from the bar's box or by a session does not come through here, and changes nothing in the panel's tab.
    // FOLLOWED INTO ITS NEXT TAB, ONCE (the owner's ask): "Add and start Claude" adds the note, and Claude starts on it a
    // moment later, which moves its line from Notes to Claude; Start Claude pressed later on the same line does the same.
    // So the task just added stays followed (inboxFollowed), and the first draw that finds it in another tab than the one
    // on show switches there, and its line glows again (FollowAddedTask). No timer and no hook in the add box: it is read
    // from the view on each draw. The follow ends, with no switch, as soon as the user takes over: a tab button, a page,
    // a search or a filter (the filters as they were when it was added), the selection moving to another task, the task
    // leaving the view, or another view.
    Guid? inboxGlowPending;
    Guid? inboxJustAdded;
    Guid? inboxFollowed;
    string inboxFollowedFilters = "";

    // What the follow keeps an eye on: a change in any of these is the user taking over.
    static string FollowFilters(TasksView view) =>
        $"{view.FiltersOn}|{view.Where}|{view.Search}|{string.Join(",", view.Filters.Where(f => f.On).Select(f => f.Filter))}|{view.ClaudeFilter}";

    // DrawInbox, before it picks the tab's lines: the followed task's tab, once it is no longer the tab on show.
    void FollowAddedTask(TasksView view, InboxView inbox)
    {
        if (inboxFollowed is not { } id) return;
        if (inboxPage is not null || !inbox.Tabbed || selectedId.HasNoValue || selectedId.Value != id
            || FollowFilters(view) != inboxFollowedFilters || inbox.TabOf(id) is not { HasValue: true } tab)
        {
            inboxFollowed = null;
            return;
        }
        if (tab.Value == inboxTab) return;
        inboxTab = tab.Value;
        inboxGlowPending = id;
        inboxFollowed = null;
    }

    // Glow's Inbox half: whether the Inbox answered for it.
    bool InboxGlow(Guid id)
    {
        if (latest?.Inbox is not { } inbox) return false;
        inboxJustAdded = id;
        if (inboxPage is not null) return true;
        if (inbox.Tabbed && inbox.TabOf(id) is { HasValue: true } tab) inboxTab = tab.Value;
        inboxFollowed = id;
        inboxFollowedFilters = FollowFilters(latest);
        inboxGlowPending = id;
        RedrawInbox();
        ApplyInboxGlow();
        return true;
    }

    // The pending glow, on its line once the line is drawn (DrawInbox calls this too).
    void ApplyInboxGlow()
    {
        if (inboxGlowPending is not { } id || !inboxLines.TryGetValue(id, out var line)) return;
        inboxGlowPending = null;
        line.Line.Background = TaskGlow.Fading(InboxLook.Raised);
        line.Line.BringIntoView();
    }

    /// <summary>For the tests: whether an Inbox line glows.</summary>
    internal bool InboxGlowing(Guid id) => inboxLines.TryGetValue(id, out var line) && TaskGlow.IsGlow(line.Line.Background);

    // The selection drawn in place (PaintSelection calls this): the selected line lighter, its square lighter still, its
    // buttons out.
    void PaintInboxSelection() =>
        inboxLines.ToList().ForEach(pair =>
        {
            var picked = Picked(pair.Key);
            pair.Value.Line.Background = picked ? InboxLook.Raised : Brushes.Transparent;
            pair.Value.Tile.Background = picked ? InboxLook.RaisedHot : InboxLook.Raised;
            pair.Value.Buttons.Visibility = picked || pair.Value.Line.IsMouseOver ? Visibility.Visible : Visibility.Hidden;
        });

    // Each line's time, once a minute (RefreshAges calls this).
    void RefreshInboxTimes(DateTimeOffset now) =>
        inboxTimes.Values.ToList().ForEach(time => time.Text.Text = TaskAge.Format(now - time.Since));

    /// <summary>For the tests: the time an Inbox line shows.</summary>
    internal string InboxTimeOf(Guid id) => inboxTimes.TryGetValue(id, out var time) ? time.Text.Text : "";

    /// <summary>For the tests: an Inbox line on show.</summary>
    internal Button? InboxLineButton(Guid id) => inboxLines.TryGetValue(id, out var line) ? line.Line : null;
}
