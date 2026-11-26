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
        Assert.Contains("10 earlier messages and steps not shown", Words(panel));
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

    [Fact]
    public void A_long_message_is_clipped_and_show_more_opens_it_in_place() => StaThread.Run(() =>
    {
        var state = new ChatStreamState();
        var panel = Panel(Deps(state));
        panel.Apply([Said(string.Join("\n\n", Enumerable.Range(0, 12).Select(i => "Paragraph " + i)), 0)]);
        var box = Inside(panel).OfType<RichTextBox>().Single();
        var more = Inside(panel).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Show more");
        var created = panel.Created;

        Assert.Equal(54, box.MaxHeight);
        Assert.Equal(Visibility.Visible, more.Visibility);
        Click(more);

        Assert.True(double.IsPositiveInfinity(box.MaxHeight));
        Assert.Equal(created, panel.Created);
        Assert.Contains("m0", state.OpenMessages);
        Assert.Equal("Show less", ((TextBlock)more.Content).Text);

        // A rebuilt page draws it open again.
        var again = Panel(Deps(state));
        again.Apply([Said(string.Join("\n\n", Enumerable.Range(0, 12).Select(i => "Paragraph " + i)), 0)]);
        Assert.True(double.IsPositiveInfinity(Inside(again).OfType<RichTextBox>().Single().MaxHeight));
    });

    [Fact]
    public void A_short_message_has_no_show_more() => StaThread.Run(() =>
    {
        var panel = Panel();
        panel.Apply([Said("Short.", 0)]);

        Assert.Equal(Visibility.Collapsed, Inside(panel).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Show more").Visibility);
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
