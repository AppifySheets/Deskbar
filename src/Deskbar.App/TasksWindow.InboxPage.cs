using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// AN INBOX TASK'S PAGE (Deskbar task 740a2231, slice 1; spec "The page"; the mockup's chat view). A click on an Inbox line
// opens the task as a page over the list, the way a messenger opens a chat, instead of unfolding its thread inside the
// list. On the board it is the same as the List's click: the task is picked and its thread is open (TasksBoard.OpenPage),
// so the page draws exactly the row the List would draw open, and every part of it is the List's.
//
//   the top     the back arrow (Esc does the same), the state mark, the title (the List's editor in its place while it is
//               edited, F2), and "⋯", the line's whole menu; under them the state in words, the workspace chip (the move
//               menu) and the pull requests as links;
//   the body    the refusal reason, the description in full with its pictures, the question in a bubble with an amber
//               edge while the task waits (else the status line in full, its links clickable), then today's thread whole
//               (Thread): its row of buttons (every one: no List line stands above it here to take some away), sessions,
//               subtasks, links, the comments as a chat (ChatThread) and the comment box. It opens scrolled to its end,
//               where the newest comments and the box are, and keeps its place through every redraw;
//   the bottom  for a finished task, "Close task" and "Not done yet…" (the keyboard to the comment box).
//
// THE PAGE STAYS UNTIL IT IS LEFT (the PM's ruling on 2026-11-26): the arrow or Esc, opening another task, closing or
// deleting its task (the close folds its thread, TasksBoard.Close), or another view. A terminal window coming to the front
// moves the selection and never the page (InboxFollowsSelection). While a page is open, F2 and Esc act on the page's task
// whatever is selected. A subtask in the page opens a page of its own; back returns to its parent's page. That is the
// only way pages stack (the tester's L2): a task shown from outside (the bar, Stats, a task link, Open parent, the
// pipe) replaces the page and clears the stack, so back goes to the list, except when it is the page right under this
// one on the stack ("Open parent" from a subtask's page), which goes back to it. The list's scroll place is the one
// taken when the first page opened.
public partial class TasksWindow
{
    // The task whose page is open, none for the list; the pages it was opened from, for back; where the list was scrolled
    // when the page opened, to go back to the same place; and the page's own scroller with the task it last showed.
    Guid? inboxPage;
    readonly Stack<Guid> inboxPageStack = [];
    double inboxListOffset;
    ScrollViewer? pageScroller;
    Guid? pageScrolledFor;
    // WHERE THE OWNER WANTS THE PAGE (see KeepPlace): kept across redraws, cleared only by his own input or by leaving the page.
    PagePlace? pageHold;

    /// <summary>The place a page was left at: its end (a chat that follows its newest) or an offset from its top.</summary>
    sealed record PagePlace(Guid Task, bool AtEnd, double Offset);

    // The same margin ApplyTranscript uses for "at its end".
    const double PageEndMargin = 24;

    // Whether a scroller at `offset` counts as at its end. Within PageEndMargin of the end, but never more than half of what
    // the page can scroll: a page that scrolls by only a few DIP (a chat that went missing leaves little more than the
    // comments) would otherwise count as at its end even at its very top, so the owner who had scrolled to the top was
    // sent to the bottom once the chat came back (main went red on the test of that case when the comment box's hint line
    // went and gave the page 17 DIP more room, task bc27a308). A page that cannot scroll at all is at its end, so a
    // short chat keeps following its newest.
    internal static bool AtPageEnd(double offset, double scrollable) =>
        offset >= scrollable - Math.Min(PageEndMargin, scrollable / 2);

    internal const string InboxPageBackText = "Back";
    internal const string NotDoneYetText = "Not done yet…";
    internal const string CloseTaskPageText = "Close task";
    internal const string QuestionBubbleTag = "inbox-question";

    /// <summary>For the tests: the task whose page is open, if any.</summary>
    internal Guid? InboxPageShown => inboxPage;

    /// <summary>For the tests: the page as drawn, null while the list shows.</summary>
    internal UIElement? InboxPageContent => InboxPage.Visibility == Visibility.Visible ? InboxPage.Child : null;

    /// <summary>For the tests: how far the page is scrolled, and how far it could be.</summary>
    internal ScrollViewer? InboxPageScroller => pageScroller;

    internal (double Offset, double Extent) InboxPageScroll => pageScroller is { } s ? (s.VerticalOffset, s.ScrollableHeight) : (0, 0);

    // A line's click: the task picked and its thread open on the board (TasksBoard.OpenPage), and its page open here.
    void OpenInboxPage(Guid id)
    {
        inboxListOffset = Scroller.VerticalOffset;
        inboxPageStack.Clear();
        inboxPage = id;
        if (Actions?.OpenPage is { } open) open(id);
        else Actions?.Pick(id);
    }

    // The way back: to the parent's page when this one was opened from it, else to the list where it was scrolled. The
    // task's thread is folded on the board, as Esc folds an open line's thread in the List.
    void BackFromPage()
    {
        if (inboxPage is not { } id) return;
        var open = latest is { } view && AllRows(view).Any(r => r.Id == id && r.Expanded);
        if (inboxPageStack.TryPop(out var parent))
        {
            inboxPage = parent;
            if (open) Actions?.ToggleThread(id);
            Actions?.OpenPage?.Invoke(parent);
            RedrawInbox();
            return;
        }
        inboxPage = null;
        if (open) Actions?.ToggleThread(id);
        RedrawInbox();
        RestoreListOffset();
    }

    void RestoreListOffset()
    {
        UpdateLayout();
        Scroller.ScrollToVerticalOffset(inboxListOffset);
        UpdateLayout();
    }

    // A subtask's line in the page's thread (TasksWindow.Line's click): its own page, with back to this one. Outside a
    // page the List's way stands (its thread opens in place).
    bool OpenSubtaskPage(Guid id)
    {
        if (inboxPage is not { } parent || latest?.Inbox is null) return false;
        if (pageInWindow) return OpensInOwnWindow(id);
        inboxPageStack.Push(parent);
        inboxPage = id;
        Actions?.OpenPage?.Invoke(id);
        RedrawInbox();
        return true;
    }

    void LeaveInboxPage()
    {
        inboxPage = null;
        inboxPageStack.Clear();
        pageScroller = null;
        pageScrolledFor = null;
        pageHold = null;
        StopTranscript();
        InboxPage.Child = null;
        InboxPage.Visibility = Visibility.Collapsed;
    }

    // Esc on a page (Escape): back, unless the keyboard is in a comment box, where Esc is the box's (review M5 of the List).
    bool InboxBack()
    {
        if (inboxPage is null || latest?.Inbox is null || pageInWindow) return false;
        if (EscapeIgnoresThreadWhile?.Invoke() ?? commentBoxes.Values.Any(box => box.IsKeyboardFocusWithin)) return false;
        BackFromPage();
        return true;
    }

    // F2 on a page (EditSelected): the page's task, whatever is selected, when nothing is being edited or typed in already.
    bool InboxEditPage()
    {
        if (inboxPage is not { } id || latest?.Inbox is null || renaming is not null) return false;
        if (AddBoxShown && AddBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin || subtaskBox?.IsKeyboardFocusWithin == true
            || commentBoxes.Values.Any(box => box.IsKeyboardFocusWithin)) return false;
        BeginRename(id);
        return true;
    }

    /// <summary>
    /// A selection from outside the panel's lines (ScrollToSelected and RevealSelected call this): while the Inbox shows it
    /// answers for them. <paramref name="open"/>: an explicit "show this task" (a click on the bar, a Stats line, a task
    /// link, Open parent: TasksBoard.ShowTask), which switches to the task's tab and opens its page. Without it
    /// (RevealSelected: a Claude task's terminal window coming to the front) the task's line is lit and brought into view,
    /// switching to the tab that holds it when need be, and an open page stays as it is (see TERMINAL IN FRONT below).
    /// The panel opening (ScrollToSelected with no thread open) brings the line into view only if it is in the tab on show.
    /// </summary>
    bool InboxFollowsSelection(bool open)
    {
        if (latest?.Inbox is not { } inbox) return false;
        // RevealSelected is the one caller that asks with open false: a terminal in front (TasksBoard.Reveal).
        var terminalInFront = !open;
        // A task just added from the panel (InboxGlow) is no explicit "show": its line is already brought into view.
        var justAdded = inboxJustAdded;
        inboxJustAdded = null;
        if (selectedId.HasNoValue) return true;
        var id = selectedId.Value;
        if (justAdded == id) open = false;
        var row = AllRows(latest).FirstOrDefault(r => r.Id == id);
        if (open && row is { Expanded: true })
        {
            // A SHOW FROM OUTSIDE REPLACES THE PAGE (the tester's L2): back goes to the list, so the stack is cleared. Only
            // a subtask opened from its parent's page stacks (OpenSubtaskPage). The one exception keeps "Open parent"
            // natural: showing the page right under this one on the stack goes back to it, popping it. The list's scroll
            // place stays the one taken when the first page opened.
            if (inboxPage is { } current && current != id)
            {
                if (inboxPageStack.TryPeek(out var under) && under == id) inboxPageStack.Pop();
                else inboxPageStack.Clear();
            }
            else if (inboxPage is null) inboxListOffset = Scroller.VerticalOffset;
            inboxPage = id;
            if (inbox.Tabbed && inbox.TabToShow(id, inboxTab) is { HasValue: true } tab) inboxTab = tab.Value;
            RedrawInbox();
            return true;
        }
        // TERMINAL IN FRONT (the owner: "let's highlight those inbox issues when selecting claude session / terminal
        // windows, as it's done with the list"). The List lights the task's line (PaintSelection, already done by the
        // selection's change) and brings it into view as little as it takes (RevealSelected). In the Inbox the line is
        // often not drawn at all: it sits in another tab than the one on show (a session's task is in Claude while the user
        // reads Needs you), so lighting it alone showed nothing. So the tab that holds it comes on show first, chosen by
        // InboxView.TabToShow: the tab on show stays while it holds the line, All included (All holds every line), else the
        // line's own tab. The line's own tab rather than All, because it is the switch a click on the bar makes
        // (ShowTask), so the two ways of picking a task from outside land in the same place, and the tab says what state the
        // task is in. The redraw that switches paints the selection on the new lines (DrawInbox's PaintInboxSelection).
        // NOT WITH A PAGE OPEN, mirroring the List: there a terminal in front moves the selection frame and never folds or
        // replaces an open thread, and a page is the Inbox's open thread (the PM's ruling, see the header), so the page and
        // the tab under it stay. NOT FOR A TASK THE INBOX HAS NO LINE FOR (a filter or the search hides it): the List moves
        // nothing then either. Under a search the tabs do not apply and the one list is already on show.
        if (inboxPage is not null) return true;
        if (terminalInFront && inbox.Tabbed && inbox.TabToShow(id, inboxTab) is { HasValue: true } holder && holder.Value != inboxTab)
        {
            inboxTab = holder.Value;
            // Brought into view once its line is drawn: a redraw held by a press in flight (MustWait) draws it later.
            inboxRevealPending = id;
            RedrawInbox();
            ApplyInboxReveal();
            return true;
        }
        if (inboxLines.TryGetValue(id, out var line)) line.Line.BringIntoView();
        return true;
    }

    // The line a terminal in front switched tabs for, brought into view once it is drawn (DrawInbox calls this too).
    Guid? inboxRevealPending;

    // Dropped unshown once the selection has moved to another task: a later draw must not scroll to a line nobody asked for.
    void ApplyInboxReveal()
    {
        if (inboxRevealPending is not { } id) return;
        if (!Picked(id))
        {
            inboxRevealPending = null;
            return;
        }
        if (!inboxLines.TryGetValue(id, out var line)) return;
        inboxRevealPending = null;
        line.Line.BringIntoView();
    }

    // A SUBTASK ADDED FROM A PAGE'S THREAD (the tester's L1). The board does what it does for the List: the thread the box
    // was in folds and the new subtask opens on its own line (TasksBoard.AddSubtaskInThread). On a page that fold would
    // close the page, so once the add has worked the page and the tab are put back as they were, and the page's task is
    // opened again (TasksBoard.OpenPage). Outside a page, or on a refused add, nothing changes.
    bool WithInboxPageKept(Func<bool> add)
    {
        var (page, stack, tab, offset) = (inboxPage, inboxPageStack.Reverse().ToArray(), inboxTab, inboxListOffset);
        var added = add();
        if (!added || page is not { } id || latest?.Inbox is null) return added;
        inboxPage = id;
        inboxPageStack.Clear();
        stack.ToList().ForEach(inboxPageStack.Push);
        inboxTab = tab;
        inboxListOffset = offset;
        inboxGlowPending = null;
        if (Actions?.OpenPage is { } open) open(id);
        RedrawInbox();
        return added;
    }

    // --- drawing -------------------------------------------------------------------------------------------------------

    // Part of the redraw signature: the page's stack and the task's PR links (which no other signature reads).
    string InboxPageSignature(TasksView view) =>
        inboxPage is not { } id ? ""
        : $"|page{id}|{string.Join(',', inboxPageStack)}|" + string.Join(',', AllRows(view).Where(r => r.Id == id).SelectMany(r => r.PullRequests).Select(p => p.Key)) + PermissionSignature(id) + AnswersSignature(view, id);

    // The page over the list while one is open, or the list; a page whose task no longer has its thread open (closed,
    // deleted, folded by a filter or from elsewhere) is left, back to the list.
    void DrawInboxPage(TasksView view)
    {
        var row = inboxPage is { } id ? AllRows(view).FirstOrDefault(r => r.Id == id) : null;
        if (inboxPage is not null && row is not { Expanded: true })
        {
            inboxPage = null;
            inboxPageStack.Clear();
            Dispatcher.BeginInvoke(new Action(RestoreListOffset), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        if (inboxPage is null || row is null)
        {
            InboxPage.Child = null;
            InboxPage.Visibility = Visibility.Collapsed;
            Scroller.Visibility = Visibility.Visible;
            pageScroller = null;
            pageScrolledFor = null;
            StopTranscript();
            return;
        }
        var keep = pageScrolledFor == row.Id && pageScroller is { } old ? old.VerticalOffset : (double?)null;
        // The place to come back to: the one held from before (a redraw that came while the content was short must not
        // forget it), else where the old scroller is now. A page just opened has none and goes to its end.
        PagePlace? hold = null;
        if (pageScrolledFor == row.Id)
            hold = pageHold is { } held && held.Task == row.Id ? held
                : pageScroller is { } was ? new PagePlace(row.Id, AtPageEnd(was.VerticalOffset, was.ScrollableHeight), was.VerticalOffset)
                : null;
        var line = view.Inbox!.Lines.FirstOrDefault(l => l.Id == row.Id);
        var mark = line?.Mark ?? MarkOfRow(row);
        Scroller.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        InboxHeader.Visibility = Visibility.Collapsed;
        InboxFooter.Visibility = Visibility.Collapsed;
        InboxPage.Visibility = Visibility.Visible;
        // The thread's comment box is drawn by Thread below, and authors[id] with it, which is what Esc's fold reads.
        var dock = new DockPanel { LastChildFill = true };
        var top = PageTop(row, mark);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        if (mark is InboxMark.Finished or InboxMark.FinishedNotRunning && !row.Closing)
        {
            var bottom = FinishedRow(row);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);
        }
        // A CHAT READS FROM THE BOTTOM UP (the PM's fix 1 after checkpoint 1). A body shorter than the page sits at its
        // bottom, so the comment box is at the bottom edge and the empty room is on top; a longer one scrolls. The
        // scroller arranges its content at least as tall as the view, so the body's own bottom alignment does it.
        FollowTranscript(row.Id);
        var body = PageBody(row);
        body.VerticalAlignment = VerticalAlignment.Bottom;
        // A FastScrollViewer like every other scroller in the panel (FastScrollViewer.cs, task 85df5e3f), so a page's chat
        // scrolls as far per wheel notch as the list it was opened from.
        pageScroller = new FastScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = body,
        };
        dock.Children.Add(pageScroller);
        DockBottom(dock, row, pageScroller);
        InboxPage.Child = dock;
        // Opened: at its end, where the newest comments and the comment box are. Redrawn: where it was.
        var scroller = pageScroller;
        pageScrolledFor = row.Id;
        pageHold = hold;
        scroller.ScrollChanged += (_, e) => KeepPlace(scroller, e);
        // His own input ends the hold, so it can never fight his scrolling (a wheel, a click or drag on the bar, a key, a touch).
        void Release() { if (ReferenceEquals(scroller, pageScroller)) pageHold = null; }
        scroller.PreviewMouseWheel += (_, _) => Release();
        scroller.PreviewMouseDown += (_, _) => Release();
        scroller.PreviewKeyDown += (_, _) => Release();
        scroller.PreviewTouchDown += (_, _) => Release();
        InboxPage.UpdateLayout();
        if (hold is null) scroller.ScrollToEnd();
        else scroller.ScrollToVerticalOffset(hold.AtEnd ? scroller.ScrollableHeight : hold.Offset);
        InboxPage.UpdateLayout();
    }

    // THE PAGE KEEPS ITS PLACE WHILE ITS CONTENT ARRIVES OR GOES (the owner: "the chat scrolls up by itself"). The chat of a
    // task with a session is a stream that the transcript poll fills in. When the poll's source goes missing for a moment
    // the stream empties, the scroller pulls its offset up to what still fits, and the content comes back only at a later
    // poll or redraw, which would then read the pulled-up offset as the owner's. So the place is held in `pageHold`:
    //   * it is made when a redraw finds none (from the old scroller), or here, when a change of the content pulls the
    //     offset up on its own (the offset BEFORE the pull, and whether it was at the end);
    //   * every later layout that gives the content more room puts the offset back toward it, up to what exists;
    //   * it is cleared only by his own input on the scroller (DrawInboxPage) or by leaving the page. Reaching the place
    //     does not clear it: a redraw can make the content short again.
    // A page held at its end stays at its end as the chat grows; one held at an offset is moved only toward that offset, never past it.
    void KeepPlace(ScrollViewer scroller, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, scroller) || !ReferenceEquals(scroller, pageScroller) || pageScrolledFor is not { } task) return;
        if (pageHold is not { } hold)
        {
            if (e.VerticalChange >= 0 || (e.ExtentHeightChange >= 0 && e.ViewportHeightChange <= 0)) return;
            var before = e.VerticalOffset - e.VerticalChange;
            var wasAtEnd = AtPageEnd(before, e.ExtentHeight - e.ExtentHeightChange - (e.ViewportHeight - e.ViewportHeightChange));
            pageHold = new PagePlace(task, wasAtEnd, before);
            return;
        }
        var wanted = hold.AtEnd ? scroller.ScrollableHeight : Math.Min(hold.Offset, scroller.ScrollableHeight);
        if (scroller.VerticalOffset < wanted - 0.5) scroller.ScrollToVerticalOffset(wanted);
    }

    // The mark a task's page shows when the task has no Inbox line of its own (a subtask opened from its parent's page).
    static InboxMark MarkOfRow(TaskRowView row) =>
        row.Status == TaskStatus.Closed ? InboxMark.Closed
        : row.SnoozedUntil is not null ? InboxMark.Snoozed
        : row.Status == TaskStatus.WaitingOnYou ? InboxMark.Asks
        : row.Status == TaskStatus.Done ? row.Delivery.Shipping ? InboxMark.FinishedNotRunning : InboxMark.Finished
        : !row.Claudes ? InboxMark.Note
        : row.LiveCount > 0 && row.WorkingCount > 0 ? InboxMark.Working
        : InboxMark.Idle;

    // The state in words, the page's second row (spec, "The page").
    static string PageStateWords(TaskRowView row, InboxMark mark) => mark switch
    {
        InboxMark.Asks => "Waits for your answer",
        InboxMark.WentQuiet => row.Status == TaskStatus.WaitingOnYou ? "Could not be restarted" : "Went quiet: no update for a while",
        InboxMark.Finished => "Finished. Check it and close",
        InboxMark.FinishedNotRunning => "Finished. Not running yet",
        InboxMark.Working => "Claude is working",
        InboxMark.Idle => row.LiveCount > 0 ? "Claude is idle" : "No live session",
        InboxMark.UsageLimit => "Stopped on the usage limit",
        InboxMark.Closed => "Closed",
        InboxMark.Snoozed => "Snoozed",
        InboxMark.AsksPermission => "Waits for your yes or no",
        _ => "A note. No Claude on it yet",
    };

    UIElement PageTop(TaskRowView row, InboxMark mark)
    {
        var first = new DockPanel { LastChildFill = true };
        var back = GlyphButton("back", InboxPageBackText, BackFromPage, "Back (Esc)");
        DockPanel.SetDock(back, Dock.Left);
        if (!pageInWindow) first.Children.Add(back);
        var tile = new Border
        {
            Tag = InboxMarkTag, Width = 28, Height = 28, CornerRadius = new CornerRadius(9), Background = InboxLook.Raised,
            Margin = new Thickness(4, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = InboxMarks.WordsOf(mark),
            Child = Centred(InboxMarks.Icon(InboxMarks.IconOf(mark), 16, InboxMarks.InkOf(mark), InboxLook.Raised, turning: mark == InboxMark.Working)),
        };
        DockPanel.SetDock(tile, Dock.Left);
        first.Children.Add(tile);
        if (!row.Closing)
        {
            var more = GlyphButton("more", "More", () => OpenMenuFor(row.Id), "Edit, status, move, delete");
            more.VerticalAlignment = VerticalAlignment.Top;
            menuButtons[row.Id] = more;
            DockPanel.SetDock(more, Dock.Right);
            first.Children.Add(more);
        }
        first.Children.Add(renaming == row.Id ? TextOf(row).Title : new TextBlock
        {
            Tag = TitleTag,
            Text = row.Title.ReplaceLineEndings(" "),
            FontSize = 16,
            FontWeight = row.Important ? FontWeights.Bold : FontWeights.SemiBold,
            FontStyle = row.Urgent ? FontStyles.Italic : FontStyles.Normal,
            Foreground = InboxLook.Ink,
            TextWrapping = TextWrapping.Wrap,
            TextDecorations = row.Closing ? TextDecorations.Strikethrough : null,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 4, 0),
        });

        var second = new WrapPanel { Margin = new Thickness(36, 6, 0, 0) };
        var words = Text(PageStateWords(row, mark), 12, mark is InboxMark.Asks or InboxMark.WentQuiet or InboxMark.AsksPermission ? InboxLook.Amber : InboxLook.Ink2,
            FontWeights.SemiBold, new Thickness(0, 0, 10, 4));
        AutomationProperties.SetName(words, "State");
        second.Children.Add(words);
        if (InboxChip(row) is FrameworkElement chip)
        {
            chip.Margin = new Thickness(0, 0, 10, 4);
            second.Children.Add(chip);
        }
        row.PullRequests.ToList().ForEach(pr =>
        {
            var link = InboxButton(Row(InboxMarks.Icon("pr", 13, InboxLook.Blue), Text(pr.Label, 12, InboxLook.Blue)), pr.Label,
                () => OpenPullRequest(row.Id, pr), new Thickness(4, 1, 4, 1), Brushes.Transparent, 6, ignoredWhen: ClickIgnored);
            link.ToolTip = pr.Url;
            link.Margin = new Thickness(0, 0, 6, 4);
            second.Children.Add(link);
        });
        if (pageInWindow) foreach (var extra in WindowStateRow(row)) second.Children.Add(extra);

        var top = new StackPanel();
        top.Children.Add(first);
        top.Children.Add(second);
        return new Border
        {
            Padding = new Thickness(8, 10, 10, 6),
            BorderBrush = InboxLook.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = top,
        };
    }

    // A pull request's link, opened the way a link in the task's text is (TasksBoard.OpenLink: the task's workspace first).
    void OpenPullRequest(Guid task, PullRequestLink pr)
    {
        Actions?.OpenLink?.Invoke(task, new Uri(pr.Url));
    }

    FrameworkElement PageBody(TaskRowView row)
    {
        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        if (row.Problem is { Length: > 0 } problem) body.Children.Add(ProblemOf(problem));
        // The description in full, its pictures under it (TextOf's parts for an open line, uncut: the page has the room).
        if (row.Description.Length > 0)
        {
            var parts = TaskImages.Split(row.Description);
            if (parts.Text.Length > 0)
            {
                var description = DescriptionBlock(row);
                description.Document.FontSize = 13;
                description.Document.Foreground = InboxLook.Ink2;
                description.Margin = new Thickness(0, 0, 0, 8);
                description.IsHitTestVisible = true;
                body.Children.Add(FullText(description, "description:" + row.Id, 13));
            }
            if (TaskImageRow.Of(StateDir, parts.Images, OpenImage) is { } pictures) body.Children.Add(pictures);
        }
        // THE QUESTION IS IN THE CHAT WHEN THE CHAT SHOWS IT (task 968c2216, on main): an ask writes the question into the
        // thread too, and an open line leaves it off while that comment is on show (DetailShown). The page follows the
        // same rule, so the question is never drawn twice: the bubble only for a question the chat does not carry (one
        // asked before questions went into the thread, or folded behind "N earlier comments"), and what is left of the
        // detail ("no live session") as plain text.
        var shownDetail = DetailShown(row);
        if (shownDetail.Length > 0)
        {
            // The List's detail text (its links clickable, task 08cdb052), wrapped in full: an open row is never cut.
            var detail = DetailText(row, shownDetail);
            detail.FontSize = 13;
            detail.FontWeight = FontWeights.Normal;
            detail.Foreground = InboxLook.Ink;
            detail.TextWrapping = TextWrapping.Wrap;
            detail.TextTrimming = TextTrimming.None;
            if (row.Status == TaskStatus.WaitingOnYou && shownDetail == row.Detail)
            {
                // The question in a bubble with an amber edge, as the mockup's "Claude asks".
                var bubble = new StackPanel();
                bubble.Children.Add(Row(InboxMarks.Icon("ask", 13, InboxLook.Amber, InboxLook.AskFill),
                    Text(row.Claudes ? "CLAUDE ASKS" : "WAITS ON YOU", 11, InboxLook.Amber, FontWeights.SemiBold)));
                detail.Margin = new Thickness(0, 5, 0, 0);
                bubble.Children.Add(detail);
                if (AnswerButtons(row.Id) is { } answers) bubble.Children.Add(answers);
                body.Children.Add(new Border
                {
                    Tag = QuestionBubbleTag,
                    Background = InboxLook.AskFill,
                    BorderBrush = InboxLook.AskEdge,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(12, 9, 12, 10),
                    Margin = new Thickness(0, 2, 0, 10),
                    Child = bubble,
                });
            }
            else
            {
                detail.Foreground = InboxLook.Ink2;
                detail.Margin = new Thickness(0, 0, 0, 10);
                body.Children.Add(detail);
            }
        }
        // THE SUBTASKS DRAWN UNDER IT IN THE LIST. A parent's thread leaves out the subtasks the List draws right under the
        // parent (TaskViews.DrawnUnderParents: "it shows up twice"); in the Inbox those lines are behind the page, so the
        // page lists them here, each the List's own subtask line (Line with inThreadOf), whose click opens its page.
        if (latest is { } view)
            view.Groups.SelectMany(g => g.Rows).Where(r => r.Nested && r.ParentId == row.Id && row.Subtasks.All(s => s.Id != r.Id)).ToList()
                .ForEach(sub =>
                {
                    closing[sub.Id] = sub.Closing;
                    body.Children.Add(Line(sub, inThreadOf: row.Id));
                });
        // Today's thread, every part of it, without the frame it has under a List line: the page is its frame.
        // In the Inbox's colours (ChatThread.Using): blue-black bubbles, not the List's warm ones (the PM's fix 3).
        UIElement thread;
        // Its row of buttons as the Inbox list's icon buttons (task 57191c84, ThreadActions), not the List look's words.
        threadOnPage = true;
        try
        {
            using (ChatThread.Using(ChatThread.InboxLook)) thread = Thread(row);
        }
        finally { threadOnPage = false; }
        if (thread is Border frame)
        {
            frame.Background = Brushes.Transparent;
            frame.BorderThickness = new Thickness(0);
            frame.Padding = new Thickness(0);
            frame.Margin = new Thickness(0);
        }
        body.Children.Add(thread);
        return body;
    }

    // A finished task's last row: close it, or say it is not done yet in a comment.
    UIElement FinishedRow(TaskRowView row)
    {
        var close = InboxButton(Row(InboxMarks.Icon("checked", 14, InboxLook.Background), Text(CloseTaskPageText, 12.5, InboxLook.Background, FontWeights.SemiBold)),
            CloseTaskPageText, () => Actions?.Close(row.Id), new Thickness(12, 6, 14, 6), InboxLook.Ink, 15, ignoredWhen: ClickIgnored);
        close.ToolTip = "Close it: six seconds to undo";
        var notYet = InboxButton(Text(NotDoneYetText, 12.5, InboxLook.Ink2), NotDoneYetText, () =>
        {
            if (CommentBoxOf(row.Id) is { } box)
            {
                box.Focus();
                box.BringIntoView();
            }
        }, new Thickness(12, 6, 12, 6), Brushes.Transparent, 15, InboxLook.StrongLine, ClickIgnored);
        notYet.ToolTip = "Say what is missing in a comment: the session reads it";
        notYet.Margin = new Thickness(8, 0, 0, 0);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal };
        row2.Children.Add(close);
        row2.Children.Add(notYet);
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            BorderBrush = InboxLook.Hairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = row2,
        };
    }
}
