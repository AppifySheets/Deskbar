using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The page's chat (ChatStreamPanel, ChatStepsView): Claude's messages and steps drawn among the comments, and updated in
// place. All text is made up. The panel is tested on its own, with a rig of the three things the window gives it.
public class ChatStreamPanelTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    static ControlTemplate Bare() => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='RichTextBox'>"
        + "<ScrollViewer x:Name='PART_ContentHost' Focusable='False' VerticalScrollBarVisibility='Disabled'/></ControlTemplate>");

    static ChatStreamDeps Deps(ChatStreamState? state = null, Action<Uri>? open = null) =>
        new(Bare(), open ?? (_ => { }), (content, name, press) =>
        {
            var button = new Button { Content = content };
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => press();
            return button;
        }, () => Now, state ?? new ChatStreamState());

    static ChatStreamPanel Panel(ChatStreamDeps? deps = null, IReadOnlyList<(string, DateTimeOffset, UIElement)>? comments = null, bool folded = false) =>
        new(deps ?? Deps(), comments ?? [], folded);

    static Maybe<DateTimeOffset> At(int seconds) => Now.AddSeconds(seconds);

    static ChatMessage Said(string text, int at, string? id = null) => new(id ?? "m" + at, ChatRole.Claude, text, 0, false, At(at));

    static ChatMessage Typed(string text, int at) => new("u" + at, ChatRole.User, text, 0, false, At(at));

    static ChatStep Step(string id, StepKind kind, string label, bool done = true, bool failed = false, int at = 0) =>
        new(id, kind, label, failed, done, At(at));

    static ChatSteps Run(string id, params ChatStep[] steps) => new(id, steps, steps.Length > 0 ? steps[0].At : At(0));

    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static Button Header(ChatStreamPanel panel) =>
        Inside(panel).OfType<Button>().First(b => AutomationProperties.GetName(b).Contains("step"));

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static string[] Words(object scope) => Inside(scope).OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToArray();

    static IEnumerable<FrameworkElement> Rows(ChatStreamPanel panel) =>
        Inside(panel).OfType<FrameworkElement>().Where(e => Equals(e.Tag, ChatStepsView.RowTag));

    // ---- a block of steps ------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_is_folded_and_its_header_opens_it_by_click_and_folds_it_again() => StaThread.Run(() =>
    {
        var state = new ChatStreamState();
        var panel = Panel(Deps(state));
        panel.Apply([Run("r1", Step("a", StepKind.Read, "Dock.cs", at: 0), Step("b", StepKind.Run, "dotnet test", at: 100))]);

        var header = Header(panel);
        Assert.Equal("2 steps", AutomationProperties.GetName(header));
        Assert.True(header.Focusable);
        var rowsPanel = (StackPanel)Rows(panel).First().Parent;
        Assert.Equal(Visibility.Collapsed, rowsPanel.Visibility);
        Assert.Contains("2 steps", Words(header));
        Assert.Contains(" · 2 min", Words(header));

        Click(header);
        Assert.Equal(Visibility.Visible, rowsPanel.Visibility);
        Assert.Contains("r1", state.OpenRuns);
        Assert.Equal(["Read Dock.cs", "Ran dotnet test"], Rows(panel).Select(r => AutomationProperties.GetName(r)));

        Click(header);
        Assert.Equal(Visibility.Collapsed, rowsPanel.Visibility);
        Assert.DoesNotContain("r1", state.OpenRuns);
    });

    [Fact]
    public void A_failed_step_says_failed_and_wears_a_shape_of_its_own() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Run("r1", Step("a", StepKind.Run, "dotnet build", failed: true), Step("b", StepKind.Run, "dotnet test"))]);

        var rows = Rows(panel).ToArray();
        Assert.Contains("failed", Words(rows[0]));
        Assert.DoesNotContain("failed", Words(rows[1]));
        Assert.Equal("Ran dotnet build, failed", AutomationProperties.GetName(rows[0]));
        // The two rows' state marks are different drawings: a cross in a circle, and a tick.
        Assert.NotEqual(Shape(rows[0]), Shape(rows[1]));
        Assert.Contains("1 failed", string.Concat(Words(Header(panel))));
    });

    [Fact]
    public void A_running_step_says_running_and_its_mark_turns() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Run("r1", Step("a", StepKind.Read, "A.cs"), Step("b", StepKind.Run, "dotnet test", done: false))]);

        var rows = Rows(panel).ToArray();
        Assert.Contains("running", Words(rows[1]));
        Assert.DoesNotContain("running", Words(rows[0]));
        Assert.Contains(Inside(rows[1]).OfType<Canvas>(), c => Equals(c.Tag, InboxMarks.TurningTag));
        Assert.DoesNotContain(Inside(rows[0]).OfType<Canvas>(), c => Equals(c.Tag, InboxMarks.TurningTag));
        Assert.Contains("running Ran dotnet test", string.Concat(Words(Header(panel))));
    });

    // The drawing of a row's last mark: its paths' outlines.
    static string Shape(FrameworkElement row) =>
        string.Join("|", Inside(row).OfType<System.Windows.Shapes.Path>().Select(p => p.Data.GetFlattenedPathGeometry().ToString()));

    [Fact]
    public void A_run_grows_in_place_keeps_its_open_state_and_is_the_same_element() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Run("r1", Step("a", StepKind.Run, "dotnet test", done: false))]);
        var block = panel.Children.OfType<ChatStepsView>().Single();
        Click(Header(panel));
        var created = panel.Created;
        var firstRow = Rows(panel).Single();

        panel.Apply([Run("r1", Step("a", StepKind.Run, "dotnet test", failed: true), Step("b", StepKind.Edit, "Dock.cs"))]);

        Assert.Same(block, panel.Children.OfType<ChatStepsView>().Single());
        Assert.Equal(created, panel.Created);
        Assert.Same(firstRow, Rows(panel).First());
        Assert.Equal(2, Rows(panel).Count());
        Assert.Equal(Visibility.Visible, ((StackPanel)firstRow.Parent).Visibility);
        Assert.Contains("failed", Words(firstRow));
        Assert.Contains("2 steps", AutomationProperties.GetName(Header(panel)));
    });

    [Fact]
    public void A_page_that_is_rebuilt_draws_an_opened_block_open_again() => StaThread.Run(() =>
    {
        var state = new ChatStreamState();
        var first = Panel(Deps(state));
        first.Apply([Run("r1", Step("a", StepKind.Read, "A.cs"))]);
        Click(Header(first));

        var second = Panel(Deps(state));
        second.Apply([Run("r1", Step("a", StepKind.Read, "A.cs"))]);

        Assert.Equal(Visibility.Visible, ((StackPanel)Rows(second).Single().Parent).Visibility);
    });

    // ---- updating in place -----------------------------------------------------------------------------------------

    [Fact]
    public void An_update_creates_only_what_is_new_and_leaves_every_other_element_alone() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Typed("Hello", 0), Said("Hi", 10)]);
        var before = panel.Children.Cast<UIElement>().ToArray();
        var created = panel.Created;

        panel.Apply([Typed("Hello", 0), Said("Hi", 10), Said("Second", 20, "m20")]);

        Assert.Equal(created + 1, panel.Created);
        Assert.Equal(before, panel.Children.Cast<UIElement>().Take(before.Length));
        Assert.Equal(before.Length + 1, panel.Children.Count);
    });

    [Fact]
    public void An_idle_update_touches_nothing() => StaThread.Run(() =>
    {
        var panel = Panel();
        var items = new ChatItem[] { Typed("Hello", 0), Run("r", Step("a", StepKind.Read, "A.cs")) };
        panel.Apply(items);
        var before = panel.Children.Cast<UIElement>().ToArray();
        var created = panel.Created;

        panel.Apply(items);
        panel.Apply([Typed("Hello", 0), Run("r", Step("a", StepKind.Read, "A.cs"))]);

        Assert.Equal(created, panel.Created);
        Assert.Equal(before, panel.Children.Cast<UIElement>());
    });

    // A run of one author's bubbles names the author once, under its last (task 85218923, ChatThread.Grouped), in the
    // stream too, and without re-creating the message a new one joins.
    static string[] Named(object scope) =>
        Inside(scope).OfType<TextBlock>().Where(t => Equals(t.Tag, ChatThread.AuthorTag) && t.Visibility == Visibility.Visible).Select(t => t.Text).ToArray();

    [Fact]
    public void A_run_of_Claudes_messages_names_Claude_once_and_a_new_one_takes_the_name_in_place() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Typed("Hello", 0), Said("Hi", 10)]);
        Assert.Equal(["you", "Claude"], Named(panel));
        var before = panel.Children.Cast<UIElement>().ToArray();
        var hi = before[^1];
        var created = panel.Created;

        panel.Apply([Typed("Hello", 0), Said("Hi", 10), Said("Second", 20, "m20")]);

        Assert.Equal(["you", "Claude"], Named(panel));
        Assert.Empty(Named(hi));
        Assert.DoesNotContain(Words(hi), w => w.StartsWith("· ", StringComparison.Ordinal));
        Assert.Equal(created + 1, panel.Created);
        Assert.Equal(before, panel.Children.Cast<UIElement>().Take(before.Length));

        // Steps between them do not end the run (task ac1ebd57, "make these more compact"): Claude is still named once,
        // under the last message, and the first one is still the same element.
        panel.Apply([Typed("Hello", 0), Said("Hi", 10), Run("r", Step("a", StepKind.Read, "A.cs", at: 15)), Said("Second", 20, "m20")]);

        Assert.Equal(["you", "Claude"], Named(panel));
        Assert.Contains(hi, panel.Children.Cast<UIElement>());
        Assert.Empty(Named(hi));

        // A marker between them does end it: the first message is named again, in place.
        panel.Apply([Typed("Hello", 0), Said("Hi", 10), Run("r", Step("a", StepKind.Read, "A.cs", at: 15)),
            new ChatMarker("k17", ChatMarkerKind.Interrupted, Now.AddSeconds(17)), Said("Second", 20, "m20")]);

        Assert.Equal(["you", "Claude", "Claude"], Named(panel));
        Assert.Contains(hi, panel.Children.Cast<UIElement>());
        Assert.Contains(Words(hi), w => w.StartsWith("· ", StringComparison.Ordinal));
    });

    [Fact]
    public void Comments_in_the_stream_are_one_run_only_with_nothing_of_the_session_between_them()
    {
        static TaskCommentView Mine(int seconds) =>
            new(Guid.NewGuid(), "you", false, "", "words") { Who = CommentAuthor.You, Day = "Today", At = Now.AddSeconds(seconds) };
        var shown = new[] { Mine(0), Mine(5), Mine(30) };

        var named = TasksWindow.GroupedInStream(shown, [Said("Between", 10)], earlierFolded: false).Select(c => c.NamesAuthor);

        Assert.Equal([false, true, true], named);
    }

    // Only Claude's steps between two of Petre's comments: still one run, "you" once (task ac1ebd57).
    [Fact]
    public void Comments_with_only_a_run_of_steps_between_them_are_one_run()
    {
        static TaskCommentView Mine(int seconds) =>
            new(Guid.NewGuid(), "you", false, "", "words") { Who = CommentAuthor.You, Day = "Today", At = Now.AddSeconds(seconds) };
        var shown = new[] { Mine(0), Mine(30) };

        var named = TasksWindow.GroupedInStream(shown, [Run("r", Step("a", StepKind.Read, "A.cs", at: 10))], earlierFolded: false)
            .Select(c => c.NamesAuthor);

        Assert.Equal([false, true], named);
    }

    // The comments are the window's own elements; an item older than a comment falls before it.
    [Fact]
    public void An_item_older_than_a_comment_is_put_before_it_and_the_comment_is_never_recreated() => StaThread.Run(() =>
    {
        var comment = new Border { Tag = "the-comment" };
        var panel = Panel(comments: [("c1", Now.AddSeconds(10), comment)]);
        panel.Apply([Said("first", 0), Said("third", 20)]);
        Assert.Equal(["m0", "c1", "m20"], panel.Keys);

        panel.Apply([Said("first", 0), Said("between", 5, "m5"), Said("third", 20)]);

        Assert.Equal(["m0", "m5", "c1", "m20"], panel.Keys);
        Assert.Same(comment, panel.Children.Cast<UIElement>().Single(e => Equals((e as FrameworkElement)?.Tag, "the-comment")));
        var order = panel.Children.Cast<UIElement>().Where(e => !(e is TextBlock)).ToList();
        Assert.Equal(4, order.Count);
        Assert.Same(comment, order[2]);
    });

    [Fact]
    public void Housekeeping_steps_are_not_drawn_and_a_run_of_only_those_is_not_there() => StaThread.Run(() =>
    {
        var panel = Panel();
        var housekeeping = Step("h", StepKind.Run, "deskbar.cmd task status-line x") with { Housekeeping = true };

        panel.Apply([Run("only", housekeeping), Said("text", 5), Run("mixed", housekeeping with { Id = "h2" }, Step("k", StepKind.Read, "A.cs", at: 6))]);

        Assert.Equal(["m5", "mixed"], panel.Keys);
        Assert.Equal(["Read A.cs"], Rows(panel).Select(r => AutomationProperties.GetName(r)));
    });

    [Fact]
    public void Items_that_fall_out_of_the_window_lose_their_element_and_are_counted() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply(Enumerable.Range(0, ClaudeChatStream.MaxDrawn + 10).Select(i => (ChatItem)Said("m " + i, i)).ToArray());

        Assert.Equal(ClaudeChatStream.MaxDrawn, panel.Keys.Count);
        Assert.Contains("10 earlier messages and steps not shown · show 10 more", Words(panel));
    });

    // M2: the earlier line is a button, each press shows 60 more of the session's items, and a comment is never cut.
    [Fact]
    public void The_earlier_line_is_a_button_that_shows_more_and_a_comment_on_the_cut_side_stays() => StaThread.Run(() =>
    {
        var comment = new Border { Tag = "an-old-comment" };
        var panel = Panel(comments: [("c1", Now.AddSeconds(5), comment)]);
        var items = Enumerable.Range(0, 2 * ClaudeChatStream.MaxDrawn + 10).Select(i => (ChatItem)Said("m " + i, i * 10)).ToArray();

        panel.Apply(items);

        // 130 items: the last 60 show, 70 are earlier; the comment (at 5 s, before every shown item) is still in the stream.
        Assert.Equal(ClaudeChatStream.MaxDrawn + 1, panel.Keys.Count);
        Assert.Equal("c1", panel.Keys[0]);
        Assert.Same(comment, panel.Children.Cast<UIElement>().Single(e => Equals((e as FrameworkElement)?.Tag, "an-old-comment")));
        var button = Assert.IsType<Button>(Inside(panel).OfType<FrameworkElement>().Single(e => Equals(e.Tag, ChatStreamPanel.EarlierTag)));
        Assert.Contains("70 earlier messages and steps not shown · show 60 more", Words(button));

        Click(button);

        Assert.Equal(2 * ClaudeChatStream.MaxDrawn + 1, panel.Keys.Count);
        Assert.Equal("c1", panel.Keys[0]);
        Assert.Contains("10 earlier messages and steps not shown · show 10 more", Words(panel));

        Click(Assert.IsType<Button>(Inside(panel).OfType<FrameworkElement>().Single(e => Equals(e.Tag, ChatStreamPanel.EarlierTag))));

        Assert.Equal(items.Length + 1, panel.Keys.Count);
        Assert.DoesNotContain(Inside(panel).OfType<FrameworkElement>(), e => Equals(e.Tag, ChatStreamPanel.EarlierTag));
    });

    // M3: running only while the session runs and the run is its newest.
    [Fact]
    public void A_step_without_a_result_in_an_ended_session_says_no_result_and_its_mark_does_not_turn() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Run("r1", Step("a", StepKind.Run, "dotnet test", done: false))], live: true);
        Assert.Contains("running", Words(Rows(panel).Single()));

        panel.Apply([Run("r1", Step("a", StepKind.Run, "dotnet test", done: false))], live: false);

        var row = Rows(panel).Single();
        Assert.Contains("no result", Words(row));
        Assert.DoesNotContain("running", Words(row));
        Assert.DoesNotContain(Inside(row).OfType<Canvas>(), c => Equals(c.Tag, InboxMarks.TurningTag));
        Assert.Equal("Ran dotnet test, no result", AutomationProperties.GetName(row));
        Assert.DoesNotContain("running", string.Concat(Words(Header(panel))));
        Assert.Contains("1 with no result", string.Concat(Words(Header(panel))));
    });

    [Fact]
    public void A_step_without_a_result_in_an_older_run_says_no_result_and_only_the_newest_run_runs() => StaThread.Run(() =>
    {
        var panel = Panel();
        var older = Run("r1", Step("a", StepKind.Run, "dotnet build", done: false, at: 0));
        panel.Apply([older, Said("between", 5), Run("r2", Step("b", StepKind.Run, "dotnet test", done: false, at: 10))]);

        var rows = Rows(panel).ToArray();
        Assert.Contains("no result", Words(rows[0]));
        Assert.Contains("running", Words(rows[1]));
        Assert.Single(Inside(panel).OfType<Canvas>(), c => Equals(c.Tag, InboxMarks.TurningTag));

        // The newest run gets a result and a newer run starts: the one before it stops "running".
        panel.Apply([older, Said("between", 5), Run("r2", Step("b", StepKind.Run, "dotnet test", done: false, at: 10)), Said("later", 20),
            Run("r3", Step("c", StepKind.Read, "A.cs", done: false, at: 30))]);

        rows = Rows(panel).ToArray();
        Assert.Contains("no result", Words(rows[1]));
        Assert.Contains("running", Words(rows[2]));
    });

    [Fact]
    public void A_note_stands_at_the_end() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Said("done", 0)], "Session ended");

        var last = Assert.IsType<TextBlock>(panel.Children[^1]);
        Assert.Equal("Session ended", last.Text);
        Assert.Equal(ChatStreamPanel.NoteTag, last.Tag);

        panel.Apply([Said("done", 0)]);
        Assert.IsNotType<TextBlock>(panel.Children[^1]);
    });

    [Fact]
    public void A_day_line_stands_above_the_first_entry_of_each_day() => StaThread.Run(() =>
    {
        var panel = Panel();

        panel.Apply([Said("yesterday", -86400), Said("today", 0)]);

        var days = panel.Children.OfType<TextBlock>().Where(t => Equals(t.Tag, ChatThread.DayTag)).Select(t => t.Text).ToArray();
        Assert.Equal(["Yesterday", "Today"], days);
    });

    // ---- a message -------------------------------------------------------------------------------------------------

    static IEnumerable<Border> Bubbles(ChatStreamPanel panel) =>
        Inside(panel).OfType<Border>().Where(b => b.Background is SolidColorBrush && b.CornerRadius.TopLeft == 12);

    [Fact]
    public void The_owners_message_is_on_the_right_and_claudes_on_the_left_in_the_inboxs_colours() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Typed("mine", 0), Said("hers", 5)]);

        var bubbles = Bubbles(panel).ToArray();
        Assert.Equal([HorizontalAlignment.Right, HorizontalAlignment.Left], bubbles.Select(b => b.HorizontalAlignment));
        Assert.Equal(Color.FromRgb(0x2A, 0x52, 0xB0), ((SolidColorBrush)bubbles[0].Background).Color);
        Assert.Equal(Color.FromRgb(0x1F, 0x25, 0x31), ((SolidColorBrush)bubbles[1].Background).Color);
    });

    [Fact]
    public void Markdown_in_a_message_is_rendered() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Said("This is **bold** and `code`.", 0)]);

        var box = Inside(panel).OfType<RichTextBox>().Single();
        var inlines = box.Document.Blocks.OfType<Paragraph>().SelectMany(p => Flatten(p.Inlines)).ToArray();
        Assert.Contains(inlines, i => i.FontWeight.ToOpenTypeWeight() >= 600);
        Assert.DoesNotContain("**", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);
    });

    static IEnumerable<Inline> Flatten(IEnumerable<Inline> inlines) =>
        inlines.SelectMany(i => i is Span s ? Flatten(s.Inlines).Prepend(i) : [i]);

    // THE OWNER'S RULING (2026-11-27): a message shows in full, at its natural height, with no inner scroll; only one past
    // about forty lines is cut, with "Show all", which opens it in place and is remembered by the stream's state.
    static string Paras(int n) => string.Join("\n\n", Enumerable.Range(0, n).Select(i => "Paragraph " + i));

    [Fact]
    public void A_long_message_is_whole_with_no_inner_scroll() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Said(Paras(14), 0)]);
        var box = Inside(panel).OfType<RichTextBox>().Single();
        panel.Measure(new Size(500, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 500, panel.DesiredSize.Height));
        panel.UpdateLayout();

        Assert.Equal(FullBubble.GuardHeight(InboxLook.ChatText), box.MaxHeight);
        Assert.True(box.ExtentHeight < box.MaxHeight);
        Assert.Equal(box.ExtentHeight, box.ActualHeight, 1);
        Assert.Equal(Visibility.Collapsed, Inside(panel).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Show all").Visibility);
        Assert.DoesNotContain(Inside(panel).OfType<Button>(), b => AutomationProperties.GetName(b) == "Show more");
    });

    [Fact]
    public void A_monster_message_is_cut_at_the_guard_and_show_all_opens_it_in_place_for_good() => StaThread.Run(() =>
    {
        var state = new ChatStreamState();
        var panel = Panel(Deps(state));
        panel.Apply([Said(Paras(90), 0)]);
        var box = Inside(panel).OfType<RichTextBox>().Single();
        var all = Inside(panel).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Show all");
        panel.Measure(new Size(500, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 500, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var created = panel.Created;

        Assert.Equal(FullBubble.GuardHeight(InboxLook.ChatText), box.MaxHeight);
        Assert.Equal(Visibility.Visible, all.Visibility);
        Click(all);

        Assert.True(double.IsPositiveInfinity(box.MaxHeight));
        Assert.Equal(Visibility.Collapsed, all.Visibility);
        Assert.Equal(created, panel.Created);
        Assert.Contains("m0", state.OpenMessages);

        // A rebuilt page draws it open again.
        var again = Panel(Deps(state));
        again.Apply([Said(Paras(90), 0)]);
        Assert.True(double.IsPositiveInfinity(Inside(again).OfType<RichTextBox>().Single().MaxHeight));
    });

    // THE WHEEL OVER A BUBBLE (the owner, build 9: the wheel would not scroll the page down over some bubbles, up still worked).
    // WPF's TextBoxBase marks a wheel-down handled while its own inner viewer has room below, which a bubble cut by the guard
    // always has (its inner bars are Disabled, so nothing moves), so the page's scroller never saw it.
    static (FastScrollViewer Page, RichTextBox Box) PageWith(string text, string below)
    {
        var panel = Panel();
        panel.Apply([Said(text, 0), Said(below, 10)]);
        var page = new FastScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        page.Measure(new Size(500, 300));
        page.Arrange(new Rect(0, 0, 500, 300));
        page.UpdateLayout();
        return (page, Inside(panel).OfType<RichTextBox>().First());
    }

    // What WPF's input does for one notch: the tunnelling event first, then the bubbling one, which starts handled if the
    // tunnelling one was (so a class handler such as TextBoxBase's does not run for it).
    static void WheelOver(UIElement target, int delta)
    {
        var preview = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        target.RaiseEvent(preview);
        target.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.MouseWheelEvent, Handled = preview.Handled });
    }

    static ScrollViewer? FindViewer(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindViewer(child) is { } deeper) return deeper;
        }
        return null;
    }

    [Fact]
    public void The_wheel_down_over_a_message_cut_by_the_guard_scrolls_the_page() => StaThread.Run(() =>
    {
        var (page, box) = PageWith(Paras(90), "A last short line.");
        Assert.True(page.ScrollableHeight > 0);
        Assert.True(FindViewer(box)!.ScrollableHeight > FindViewer(box)!.VerticalOffset);

        WheelOver(box, -120);
        page.UpdateLayout();

        Assert.True(page.VerticalOffset > 0, $"the page did not move; bubble {box.ActualHeight}, inner ScrollableHeight {FindViewer(box)!.ScrollableHeight}");
    });

    [Fact]
    public void The_wheel_down_over_a_short_message_scrolls_the_page() => StaThread.Run(() =>
    {
        // Two lines, with a message below it that fills the page, so the page has room to scroll.
        var (page, box) = PageWith("First line" + "\n\n" + "Second line", Paras(30));
        Assert.True(page.ScrollableHeight > 0);

        WheelOver(box, -120);
        page.UpdateLayout();

        Assert.True(page.VerticalOffset > 0, $"the page did not move; bubble {box.ActualHeight}, inner ScrollableHeight {FindViewer(box)!.ScrollableHeight}");
    });

    [Fact]
    public void Pictures_show_as_a_count() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([new ChatMessage("p", ChatRole.User, "Look", 2, false, At(0)), new ChatMessage("q", ChatRole.User, "", 1, false, At(1))]);

        Assert.Equal(["2 pictures", "1 picture"], Words(panel).Where(w => w.Contains("picture")));
    });

    [Fact]
    public void A_message_whose_text_grew_is_redrawn_in_the_same_place() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Said("one", 0, "same"), Said("after", 10)]);
        var after = panel.Children.Cast<UIElement>().Last(e => e is not TextBlock);

        panel.Apply([Said("one\n\ntwo", 0, "same"), Said("after", 10)]);

        Assert.Same(after, panel.Children.Cast<UIElement>().Last(e => e is not TextBlock));
        Assert.Contains("two", new TextRange(Inside(panel).OfType<RichTextBox>().First().Document.ContentStart, Inside(panel).OfType<RichTextBox>().First().Document.ContentEnd).Text);
    });

    // ---- what drawing costs ---------------------------------------------------------------------------------------

    // THE MEASURED DRAW OF 60 MARKDOWN BUBBLES (the plan's open question): the first Apply of a full window of Claude's
    // messages, each a few paragraphs with a list, a code span and bold, then a Measure and Arrange of the stream at a
    // panel's width. The number is written to the temp folder for the report and bounded loosely here, so a regression
    // that doubles it fails. Made-up text only.
    [Fact]
    public void Drawing_sixty_markdown_bubbles_costs_what_the_plan_expected() => StaThread.Run(() =>
    {
        var texts = Enumerable.Range(0, ClaudeChatStream.MaxDrawn).Select(i =>
            $"Paragraph one of message {i}, with **bold words** and `inline code` in it, long enough to wrap onto a second line at a panel's width.\n\n"
            + "- first point of the list\n- second point of the list\n- third point of the list\n\n"
            + "A last paragraph that closes message " + i + " and links to [the docs](https://example.test/docs).").ToArray();
        var items = texts.Select((t, i) => (ChatItem)(i % 3 == 0 ? Typed(t, i) : Said(t, i))).ToArray();
        var panel = Panel();
        var host = new Window { Width = 520, Height = 700, Content = new ScrollViewer { Content = panel }, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };

        var watch = Stopwatch.StartNew();
        panel.Apply(items);
        var built = watch.ElapsedMilliseconds;
        panel.Measure(new Size(500, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 500, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var laidOut = watch.ElapsedMilliseconds;

        // A second, warm run: the same window drawn again by a fresh panel.
        var again = Panel();
        watch.Restart();
        again.Apply(items);
        again.Measure(new Size(500, double.PositiveInfinity));
        again.Arrange(new Rect(0, 0, 500, again.DesiredSize.Height));
        var warm = watch.ElapsedMilliseconds;
        // The page is rebuilt (a new comment): the same panel moves into the new page and is applied again.
        var newPage = new StackPanel();
        ((ScrollViewer)host.Content).Content = null;
        newPage.Children.Add(panel);
        panel.SetComments([], false);
        var reHost = new Window { Width = 520, Height = 700, Content = new ScrollViewer { Content = newPage }, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        watch.Restart();
        panel.Apply(items);
        newPage.Measure(new Size(500, double.PositiveInfinity));
        newPage.Arrange(new Rect(0, 0, 500, newPage.DesiredSize.Height));
        newPage.UpdateLayout();
        var reused = watch.ElapsedMilliseconds;
        reHost.Close();
        host.Close();

        File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskbar-chat-draw-cost.txt"),
            $"60 markdown bubbles: first draw {built} ms built, {laidOut} ms with layout; second panel {warm} ms with layout; the same panel moved into a rebuilt page {reused} ms");
        Assert.Equal(ClaudeChatStream.MaxDrawn, panel.Keys.Count);
        Assert.InRange(laidOut, 0, 5000);
    });
}
