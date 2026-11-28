using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE INBOX VIEW, THE WINDOW'S HALF (Deskbar task 740a2231, slice 1; design: the workspace's notes\tasks-inbox-design.md,
// section 4; the look is the picked mockup, notes\mockups\2026-11-26-tasks-redesign-round2\pick.html). A fourth view of
// the panel beside List, Matrix and Stats, drawn from the view's Inbox, which the board fills only while the Inbox is the
// view (TasksBoard.Inbox.cs). Display only, like the rest of this window: every click calls what the List calls.
//
//   THE FRAME   while the Inbox shows, the panel takes the mockup's colours (InboxLook), the toolbar's title line and chip
//               rows give way to the Inbox's header (InboxHeader), and the view switch moves to a footer (InboxFooter):
//                 1. "Tasks", then "+ New" (today's add box, under the header) and the panel's ×; the search box (today's,
//                    Ctrl+F too) sits under the header, always on show over the lines (task b14e4203, PaintSearchRow);
//                 2. the tabs, Needs you, Claude and Notes, then All (the three together), each with its mark and its
//                    count;
//                 3. "This workspace | Everywhere" (today's Current workspace checkbox, same memory), and Filter, which
//                    unfolds a sheet under the header holding today's chip rows, every one of them: the very Filters panel
//                    the toolbar draws (DrawFilters), moved into the sheet while the Inbox shows and back when it goes.
//               Then the manager's violet line, the add box and the problem line, all as today, then the lines.
//               Leaving the Inbox puts every one of these back as it was, so List, Matrix and Stats are drawn exactly as
//               before, but for the footer: that one stays, as the panel's foot in every view (PaintInboxChrome).
//   THE LINES   the chosen tab's lines under captions (TasksWindow.InboxLine.cs), drawn into the List's own panel (Rows),
//               so the drag, the click guard and the scroll work on them as they do on the List's. While the search box
//               has text, a search by id found a task, or Closed or Snoozed is the status filter, the tabs do not apply: one
//               list of the matches, and the three tab buttons dimmed.
public partial class TasksWindow
{
    /// <summary>For the tests: the Inbox's lines as last handed over, null while another view shows.</summary>
    internal InboxView? InboxShown => latest?.Inbox;

    /// <summary>For the tests: the tab on show.</summary>
    internal InboxTab InboxTabShown => inboxTab;

    /// <summary>For the tests: whether the Filter sheet is unfolded.</summary>
    internal bool InboxFilterSheetOpen => inboxFilterOpen;

    /// <summary>For the tests: the Inbox's header and footer as drawn (null outside the Inbox).</summary>
    internal UIElement? InboxHeaderShown => InboxHeader.Child;

    internal UIElement? InboxFooterShown => InboxFooter.Child;

    /// <summary>For the tests: whether the foot (the view switch row) is on show, in whatever view.</summary>
    internal bool InboxFooterOnShow => InboxFooter.Visibility == Visibility.Visible && InboxFooter.Child is not null;

    /// <summary>For the tests: whether the chip rows are in the Filter sheet (Inbox) or in the toolbar (every other view).</summary>
    internal bool FiltersInSheet => ReferenceEquals(Filters.Parent, inboxSheet);

    /// <summary>For the tests: whether the toolbar's title line (List / Matrix / Stats, "+ Add", ×) shows.</summary>
    internal bool ToolbarTitleShown => TitleLine.Visibility == Visibility.Visible;

    /// <summary>For the tests: whether the search row shows under the Inbox's header.</summary>
    internal bool InboxSearchShown => SearchRow.Visibility == Visibility.Visible;

    // The tab on show, the search row asked for by Ctrl+F on a page (on the list it always shows, PaintSearchRow), and the
    // Filter sheet unfolded: the window's own state, part of the redraw signature (InboxSignature), kept while the panel
    // is open.
    InboxTab inboxTab = InboxTab.NeedsYou;
    bool inboxSearchOpen;
    bool inboxFilterOpen;

    // Where the Filters panel lives in the toolbar, so it goes back there when the Inbox goes; and the sheet it moves into.
    Panel? filtersHome;
    readonly Border inboxSheet = new()
    {
        Tag = InboxSheetTag,
        Background = InboxLook.Sheet,
        BorderBrush = InboxLook.StrongLine,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(8, 8, 8, 4),
        Margin = new Thickness(0, 8, 0, 0),
    };

    internal const string InboxSheetTag = "inbox-filter-sheet";

    // The board's fill in List, Matrix and Stats (the XAML's), put back when the Inbox goes.
    static readonly Brush ListBoardFill = Frozen(0xFF, 0x1E, 0x1F, 0x24);

    /// <summary>A tab button's click, and for the tests.</summary>
    internal void ChooseInboxTab(InboxTab tab)
    {
        inboxTab = tab;
        // The user picked a tab: a task just added is no longer followed into another (FollowAddedTask).
        inboxFollowed = null;
        RedrawInbox();
    }

    // The window's own Inbox state changed (a tab, the sheet, the search row): drawn again from the latest view, which the
    // signature now tells apart. Present, so a menu or a press in flight holds it as it holds any redraw.
    void RedrawInbox()
    {
        if (latest is not null) Present(latest);
    }

    // The frame for this view, both ways (see the header). Called on every draw, Stats' included, after the draw has set
    // the search row and the chip rows for List, Matrix and Stats, so this has the last word while the Inbox shows.
    void PaintInboxChrome(TasksView view)
    {
        var on = view.Inbox is not null;
        Background = on ? InboxLook.Background : ListBoardFill;
        Board.Background = Background;
        InboxHeader.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // THE FOOT IS THE SAME IN EVERY VIEW (the owner, of the Inbox's "Inbox | List | Stats" row: "put this on the bottom
        // of the list as well"). The one control, InboxFooterOf, at the bottom of List, Matrix and Stats too, with the view
        // on show on its pill; its clicks are the Inbox's own. It is also now the List's one way into the Inbox: the
        // toolbar's Inbox chip this PR had added is gone, so the Inbox has one entry and the toolbar is main's again
        // (List, Matrix, Stats, "+ Add", ×). That is why the foot shows for everyone, the Inbox left for the List included:
        // the Inbox is the default (TasksPanelState.Inbox), and someone who has left it can only find it again here. Only a
        // window with no controller that can show the Inbox (Actions.ShowInbox unset: the tests' bare windows) draws no
        // foot outside the Inbox, as it drew no Inbox chip. An open Inbox page still hides it (DrawInboxPage).
        var foot = on || Actions?.ShowInbox is not null;
        InboxFooter.Visibility = foot ? Visibility.Visible : Visibility.Collapsed;
        TitleLine.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        filtersHome ??= Filters.Parent as Panel;
        if (on && Filters.Parent == filtersHome)
        {
            filtersHome!.Children.Remove(Filters);
            inboxSheet.Child = Filters;
        }
        else if (!on && Filters.Parent != filtersHome && filtersHome is not null)
        {
            inboxSheet.Child = null;
            filtersHome.Children.Add(Filters);
        }
        PaintSearchRow(on);
        if (!on)
        {
            InboxHeader.Child = null;
            // The Inbox draws its own foot in DrawInbox; every other view gets it here.
            InboxFooter.Child = foot ? InboxFooterOf(view) : null;
            EmptyText.Foreground = HeaderInk;
            inboxFollowed = null;
            inboxRevealPending = null;
            LeaveInboxPage();
        }
    }

    // THE SEARCH ROW IS ALWAYS THERE IN THE INBOX'S LIST (task b14e4203, the owner: "keep the search field always
    // showing"). It used to hide behind a search button in the header and come out on a click or Ctrl+F, so every search
    // took a click before the typing; now it sits under the header whenever the list does, and the button is gone.
    // The toolbar it lives in stays in place, focusable (FocusBoard puts the keyboard there), and in the Inbox holds only
    // this row. A task's page and the manager's page hide the row while it is empty, as they hide the header above it:
    // a page has its own top row (Back, the title), and an empty box over it would only take room. With text the row
    // stays over a page too, so the search a page was opened from is still in sight, and Ctrl+F on a page brings it
    // back (inboxSearchOpen, ShowInboxSearch). Called by PaintInboxChrome, and again at the end of DrawInbox, because
    // the page's draw can close the page (its thread folded elsewhere) after the chrome was painted for it.
    // A task's page or the manager's page is over the list.
    bool OnInboxPage => inboxPage is not null || managerPage;

    void PaintSearchRow(bool on)
    {
        var onPage = OnInboxPage;
        var searching = on && (!onPage || inboxSearchOpen || SearchBox.Text.Length > 0);
        if (on) SearchRow.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SearchRow.Margin = on ? new Thickness(0) : new Thickness(0, 6, 0, 0);
        // With the row hidden (on a page) the toolbar takes no room at all: no padding, no hairline.
        Toolbar.Padding = !on ? new Thickness(10, 8, 10, 8) : searching ? new Thickness(12, 8, 12, 8) : new Thickness(0);
        Toolbar.BorderThickness = !on || searching ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        Toolbar.BorderBrush = on ? InboxLook.Hairline : ThreadEdge;
    }

    // Everything the Inbox's own state adds to what is drawn, for the redraw signature: the tab, the search row, the sheet,
    // the page, the filters' count, and each line's tab, mark, time and small marks.
    string InboxSignature(TasksView view) =>
        view.Inbox is not { } inbox ? ""
        : $"inbox|{inboxTab}|{inboxSearchOpen}|{inboxFilterOpen}|{inboxPage}|{view.FiltersOn}|{inbox.Tabbed}|{view.MatrixAllowed}|"
          + string.Join(',', inbox.Lines.Select(l => $"{l.Id}{l.Tab}{l.Section}{l.Mark}{l.Since:O}{l.Nested}{l.Linked}"))
          + "|" + string.Join(',', view.Groups.SelectMany(g => g.Rows).Select(r => $"{r.PullRequests.Count}{r.Description.Length}"))
          + InboxPageSignature(view) + ManagerPageSignature();

    // The Inbox in place of the List (DrawRows has emptied the lines' panel and every per-line table before this): the
    // header, the footer, then the chosen tab's lines under their captions, or the task's page over them.
    void DrawInbox(TasksView view)
    {
        var inbox = view.Inbox!;
        matrixShown = false;
        MatrixGrid.Visibility = Visibility.Collapsed;
        inboxTimes.Clear();
        inboxLines.Clear();
        // A task just added and followed, moved to another tab since (TasksWindow.InboxLine.cs): that tab, before the
        // header and the lines are drawn for the one on show.
        FollowAddedTask(view, inbox);
        InboxHeader.Child = InboxHeaderOf(view, inbox);
        InboxFooter.Child = InboxFooterOf(view);
        var rows = view.Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Id);
        var lines = (inbox.Tabbed ? inbox.In(inboxTab) : inbox.Lines).Where(l => rows.ContainsKey(l.Id)).ToArray();
        var captions = new List<string>();
        InboxSection? above = null;
        foreach (var line in lines)
        {
            if (inbox.Tabbed && line.Section != above)
            {
                captions.Add(InboxTabs.CaptionOf(line.Section).Caption);
                Rows.Children.Add(SectionCaption(line.Section, lines.Count(l => l.Section == line.Section && !l.Nested), above is not null));
                above = line.Section;
            }
            var row = rows[line.Id];
            closing[row.Id] = row.Closing;
            Rows.Children.Add(InboxLineOf(row, line));
        }
        Headers = captions;
        // THE LINES DRAWN, IN THEIR ORDER, as the List's Keys are (the tester's M1): F2 edits the selected line only when
        // it is on show (EditSelected reads Keys), so a selection in another tab is no longer renamed with no editor
        // drawn, which held every redraw (MustWait) until Esc.
        Keys = lines.Select(l => l.Id).ToArray();
        EmptyText.Text = !inbox.Tabbed || view.Groups.All(g => g.Rows.Count == 0) ? view.EmptyText : EmptyTabText(inboxTab);
        EmptyText.Foreground = InboxLook.Ink3;
        EmptyText.Visibility = lines.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaintColumnHeader(false);
        PaintAddPlace();
        PaintInboxSelection();
        ApplyInboxGlow();
        ApplyInboxReveal(); // a terminal in front that switched the tab (TasksWindow.InboxPage.cs, InboxFollowsSelection)
        // The manager's page (task 6c4c067f, TasksWindow.ManagerPage.cs) in a task page's place while it is open.
        if (managerPage) DrawManagerPage();
        else DrawInboxPage(view);
        // The page decided above (opened, kept or closed): the search row follows it (PaintSearchRow).
        PaintSearchRow(true);
    }

    static string EmptyTabText(InboxTab tab) => tab switch
    {
        InboxTab.NeedsYou => "Nothing needs you.",
        InboxTab.Claude => "Claude is not on any of these.",
        InboxTab.Notes => "No notes here. \"+ New\" writes one.",
        // All is empty only when the other three are, which the view's own empty text says first (DrawInbox reads it when
        // no group has a row); this is for a filter that leaves the groups but none of their lines on show.
        _ => "Nothing here. \"+ New\" writes a note.",
    };

    // --- the header --------------------------------------------------------------------------------------------------

    UIElement InboxHeaderOf(TasksView view, InboxView inbox)
    {
        var header = new StackPanel();
        // 1. "Tasks", then search, "+ New" and the ×, right to left in the dock.
        var top = new DockPanel { Height = 30, LastChildFill = true };
        var close = GlyphButton("x", "Close the panel", () => Actions?.ClosePanel(), "Close the tasks panel");
        var add = InboxButton(Row(InboxMarks.Icon("plus", 14, InboxLook.Ink), Text("New", 12.5, InboxLook.Ink, FontWeights.SemiBold)),
            "New", ShowAdd, new Thickness(8, 0, 11, 0), InboxLook.Raised, 9);
        add.Height = 30;
        add.Margin = new Thickness(4, 0, 4, 0);
        add.ToolTip = "Write a note";
        // No search button here any more: the search row under the header is always on show (PaintSearchRow).
        new[] { close, add }.ToList().ForEach(b =>
        {
            DockPanel.SetDock(b, Dock.Right);
            top.Children.Add(b);
        });
        top.Children.Add(new TextBlock { Text = "Tasks", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = InboxLook.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
        header.Children.Add(top);

        // 2. The tabs, equal widths, 6 DIP apart: Needs you, Claude, Notes, then All, the one that holds the other three's
        //    lines together (the owner: "let's add all tasks here as well"). All is last: the first three are where a line
        //    is sorted to, read left to right by urgency, and All is the sum of them, so it stands at the row's end as a
        //    total does. Four cards still fit the panel's narrowest width (360 DIP: about 80 DIP a card).
        var tabs = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        var order = Enum.GetValues<InboxTab>();
        order.Select((tab, i) => (tab, i)).ToList().ForEach(x =>
        {
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = TabButton(x.tab, inbox.Count(x.tab), inbox.Tabbed);
            button.Margin = new Thickness(x.i == 0 ? 0 : 3, 0, x.i == order.Length - 1 ? 0 : 3, 0);
            Grid.SetColumn(button, x.i);
            tabs.Children.Add(button);
        });
        header.Children.Add(tabs);

        // 3. This workspace | Everywhere, then Filter at the right end.
        var scope = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        var filterText = view.FiltersOn > 0 ? $"Filter · {view.FiltersOn}" : "Filter";
        var filter = InboxButton(Row(InboxMarks.Icon("filter", 14, inboxFilterOpen || view.FiltersOn > 0 ? InboxLook.Ink : InboxLook.Ink2),
                Text(filterText, 12, inboxFilterOpen || view.FiltersOn > 0 ? InboxLook.Ink : InboxLook.Ink2)),
            "Filter", ToggleInboxFilters, new Thickness(8, 4, 8, 4), inboxFilterOpen ? InboxLook.Raised : Brushes.Transparent, 7);
        filter.ToolTip = "Status, Claude, subtasks, workspace, assignee and goal filters";
        AutomationProperties.SetItemStatus(filter, inboxFilterOpen ? "expanded" : "collapsed");
        DockPanel.SetDock(filter, Dock.Right);
        scope.Children.Add(filter);
        var two = new StackPanel { Orientation = Orientation.Horizontal };
        two.Children.Add(Segment("This workspace", view.Where.FollowsCurrent, () => Actions?.FilterWorkspace(TaskWorkspaceFilter.Current),
            "Only the tasks of the workspace you are on, following you when you switch desktops"));
        two.Children.Add(Segment("Everywhere", view.Where.Any, () => Actions?.FilterWorkspace(TaskWorkspaceFilter.All),
            "The tasks of every workspace"));
        scope.Children.Add(new Border
        {
            Background = InboxLook.Surface, BorderBrush = InboxLook.Hairline, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = two,
        });
        header.Children.Add(scope);

        // The sheet: today's chip rows, unfolded under the header until Filter folds it again.
        (inboxSheet.Parent as Panel)?.Children.Remove(inboxSheet);
        inboxSheet.Visibility = inboxFilterOpen ? Visibility.Visible : Visibility.Collapsed;
        header.Children.Add(inboxSheet);

        return new Border
        {
            Padding = new Thickness(12, 12, 12, 10),
            BorderBrush = InboxLook.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header,
        };
    }

    // A tab: its mark and count over its name. The chosen one is lighter, edged, with a 3 DIP bar along its bottom.
    Button TabButton(InboxTab tab, int count, bool tabbed)
    {
        var chosen = tab == inboxTab && tabbed;
        var (name, mark, ink) = TabFace(tab);
        var numbers = Row(InboxMarks.Icon(mark, 18, ink, chosen ? InboxLook.Raised : InboxLook.Surface),
            Text($"{count}", 19, InboxLook.Ink, FontWeights.SemiBold));
        var content = new StackPanel();
        content.Children.Add(numbers);
        var caption = Text(name, 12, chosen ? InboxLook.Ink : InboxLook.Ink2, chosen ? FontWeights.SemiBold : FontWeights.Normal, new Thickness(0, 3, 0, 0));
        // Four cards share the row now, so at the narrowest panel a long name ends in "…" rather than spilling out of its card.
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(caption);
        var face = new Grid();
        face.Children.Add(new Border
        {
            Background = chosen ? InboxLook.Raised : InboxLook.Surface,
            BorderBrush = chosen ? InboxLook.Ink3 : InboxLook.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 8, 10, 10),
            Child = content,
        });
        if (chosen)
            face.Children.Add(new Border
            {
                Height = 3, Background = InboxLook.Ink, CornerRadius = new CornerRadius(3, 3, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 0),
            });
        var button = Flat(face, name, () => ChooseInboxTab(tab), new Thickness(0));
        // The three share the row in equal parts, so the face fills its button: the toolbar's FlatButton centres its content.
        button.Template = StretchedFace;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        // Dimmed while the tabs do not apply (a search, Closed or Snoozed): the list is the matches, whatever tab is chosen.
        button.Opacity = tabbed ? 1 : 0.45;
        button.ToolTip = $"{name}: {count}";
        AutomationProperties.SetItemStatus(button, chosen ? "selected" : "");
        return button;
    }

    // A tab card's name, its mark (an InboxMarks icon) and the mark's colour. Each of the first three wears the mark of what
    // it mostly holds, in that state's colour. All holds every state, so it wears none of theirs: a plain stack of cards
    // (InboxMarks' "all") in the quiet ink Notes uses, so it reads as the total and does not compete with Needs you's amber.
    static (string Name, string Mark, Brush Ink) TabFace(InboxTab tab) => tab switch
    {
        InboxTab.NeedsYou => ("Needs you", "ask", InboxLook.Amber),
        InboxTab.Claude => ("Claude", "working", InboxLook.Blue),
        InboxTab.Notes => ("Notes", "note", InboxLook.Ink2),
        _ => ("All", "all", InboxLook.Ink2),
    };

    // A button that is only its content, stretched across it: the tabs' template. Made once per window, since a template
    // belongs to the thread that made it and the tests build windows on several threads.
    ControlTemplate StretchedFace => stretchedFace ??= MakeStretchedFace();
    ControlTemplate? stretchedFace;

    static ControlTemplate MakeStretchedFace()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
        return new ControlTemplate(typeof(Button)) { VisualTree = presenter };
    }

    Button Segment(string name, bool on, Action press, string tip)
    {
        var button = InboxButton(Text(name, 12, on ? InboxLook.Ink : InboxLook.Ink2, on ? FontWeights.SemiBold : FontWeights.Normal), name, press,
            new Thickness(10, 3, 10, 3), on ? InboxLook.Raised : Brushes.Transparent, 7, on ? InboxLook.StrongLine : null);
        button.ToolTip = tip;
        AutomationProperties.SetItemStatus(button, on ? "checked" : "unchecked");
        return button;
    }

    void ToggleInboxFilters()
    {
        inboxFilterOpen = !inboxFilterOpen;
        RedrawInbox();
    }

    // Ctrl+F (FocusSearch): on a task's or the manager's page the row is hidden while empty (PaintSearchRow), so it is
    // shown first, or the keyboard would go to a box nobody can see. On the Inbox's list it is already there.
    void ShowInboxSearch()
    {
        // Asked for on a page even while the box has text, so an Esc that empties it there leaves the row in sight.
        if (latest?.Inbox is null || inboxSearchOpen || !OnInboxPage) return;
        inboxSearchOpen = true;
        // Drawn again, so the signature knows the row is asked for; the row painted at once too, in case a redraw is
        // held. Only the row: PaintInboxChrome would also bring back the header and foot the page has just hidden.
        RedrawInbox();
        PaintSearchRow(true);
        UpdateLayout();
    }

    // --- the footer ----------------------------------------------------------------------------------------------------

    // The view switch, Inbox, List, Matrix (from 780 DIP, as the toolbar's), Stats, and the Manager button at the right end.
    // The panel's foot in every view (PaintInboxChrome), the view on show on its pill: the same buttons calling the same
    // actions wherever it is drawn, so a click on List from the Inbox's foot and from the List's own foot are one thing.
    UIElement InboxFooterOf(TasksView view)
    {
        var dock = new DockPanel { LastChildFill = false };
        var manager = InboxButton(Row(InboxMarks.Icon("spark", 13, InboxLook.Violet), Text("Manager", 12, InboxLook.Ink2)),
            "Manager", () => Actions?.OpenManager?.Invoke(), new Thickness(7, 4, 7, 4), Brushes.Transparent, 7);
        manager.ToolTip = "Open the manager session";
        DockPanel.SetDock(manager, Dock.Right);
        dock.Children.Add(manager);
        // Which view is on show: Stats lies over the others, the Inbox is drawn only while chosen, the Matrix only while
        // the switch says so and the panel is wide enough (view.Matrix is already both).
        var shown = view.Stats is not null ? "Stats" : view.Inbox is not null ? "Inbox" : view.Matrix ? "Matrix" : "List";
        var views = new StackPanel { Orientation = Orientation.Horizontal };
        var inbox = ViewButton("inbox", "Inbox", shown == "Inbox", () => Actions?.ShowInbox?.Invoke(true));
        // The toolbar's Inbox chip used to carry this; the foot is now the Inbox's one entry, so it says what it opens.
        inbox.ToolTip = "The tasks in tabs: what needs you, what Claude has, your notes, and all of them";
        views.Children.Add(inbox);
        views.Children.Add(ViewButton("list", "List", shown == "List", () => Actions?.ShowMatrix(false)));
        if (view.MatrixAllowed) views.Children.Add(ViewButton("grid", "Matrix", shown == "Matrix", () => Actions?.ShowMatrix(true)));
        if (Actions?.ShowStats is { } stats) views.Children.Add(ViewButton("stats", "Stats", shown == "Stats", stats));
        DockPanel.SetDock(views, Dock.Left);
        dock.Children.Add(views);
        return new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            BorderBrush = InboxLook.Hairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = dock,
        };
    }

    Button ViewButton(string icon, string name, bool on, Action press)
    {
        var button = InboxButton(Row(InboxMarks.Icon(icon, 13, on ? InboxLook.Ink : InboxLook.Ink2),
                Text(name, 12, on ? InboxLook.Ink : InboxLook.Ink2, on ? FontWeights.SemiBold : FontWeights.Normal)),
            name, press, new Thickness(7, 4, 7, 4), on ? InboxLook.Raised : Brushes.Transparent, 7);
        button.Margin = new Thickness(0, 0, 2, 0);
        AutomationProperties.SetItemStatus(button, on ? "checked" : "unchecked");
        return button;
    }

    // --- small parts ---------------------------------------------------------------------------------------------------

    // A section's caption: the words in capitals, how many lines it has, and the plain words after it (spec, "The three
    // tabs"). The idle part of the Claude tab sits under a dashed line, as the List's idle divider does.
    static UIElement SectionCaption(InboxSection section, int count, bool afterAnother)
    {
        var (caption, words) = InboxTabs.CaptionOf(section);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, afterAnother ? 13 : 10, 10, 5), Tag = "task-group" };
        line.Children.Add(Text(caption.ToUpperInvariant(), 11, InboxLook.Ink3, FontWeights.SemiBold));
        line.Children.Add(Text($"{count}", 11, InboxLook.Ink2, FontWeights.SemiBold, new Thickness(7, 0, 0, 0)));
        if (words.Length > 0) line.Children.Add(Text(words, 12, InboxLook.Ink3, FontWeights.Normal, new Thickness(7, 0, 0, 0)));
        if (section != InboxSection.Idle) return line;
        var dashed = new StackPanel { Tag = "task-group" };
        dashed.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1, Stroke = InboxLook.StrongLine, StrokeThickness = 1, StrokeDashArray = [3, 3], SnapsToDevicePixels = true,
            Margin = new Thickness(10, 8, 10, 0),
        });
        line.Margin = new Thickness(10, 6, 10, 5);
        dashed.Children.Add(line);
        return dashed;
    }

    static TextBlock Text(string text, double size, Brush ink, FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = ink,
        FontWeight = weight ?? FontWeights.Normal,
        Margin = margin ?? new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Parts side by side, 6 DIP apart, centred on one line.
    static StackPanel Row(params UIElement[] parts)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        parts.Select((part, i) => (part, i)).ToList().ForEach(x =>
        {
            if (x.i > 0 && x.part is FrameworkElement element && element.Margin == new Thickness(0)) element.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(x.part);
        });
        return row;
    }

    // The mockup's buttons: a rounded face of its own (the toolbar's FlatButton has 4 DIP corners) inside a flat button,
    // so the click guard, the name and the handled click are Flat's.
    Button InboxButton(UIElement content, string name, Action press, Thickness padding, Brush fill, double radius, Brush? edge = null,
        Func<bool>? ignoredWhen = null)
    {
        var face = new Border
        {
            Child = content,
            Padding = padding,
            Background = fill,
            CornerRadius = new CornerRadius(radius),
            BorderBrush = edge,
            BorderThickness = new Thickness(edge is null ? 0 : 1),
        };
        var button = Flat(face, name, press, new Thickness(0), ignoredWhen);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        return button;
    }

    // A square icon button, the mockup's .ib: 30 DIP, 9 DIP corners.
    Button GlyphButton(string icon, string name, Action press, string tip, double size = 30, Func<bool>? ignoredWhen = null)
    {
        var glyph = InboxMarks.Icon(icon, size > 28 ? 16 : 15, InboxLook.Ink2);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var button = InboxButton(glyph, name, press, new Thickness(0), Brushes.Transparent, size > 28 ? 9 : 7, ignoredWhen: ignoredWhen);
        ((Border)button.Content).Width = size;
        ((Border)button.Content).Height = size;
        button.ToolTip = tip;
        return button;
    }
}
