using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The bar's task lines in the Inbox look (Deskbar task 740a2231, slice 2; FloatingBar.InboxLine.cs): drawn while the
// tasks panel shows its Inbox, the old way otherwise. Only the drawing changes; what the bar lists and every click are the
// bar's own, which the rest of FloatingBarTaskListTests pins with the switch off.
public partial class FloatingBarTaskListTests
{
    static readonly Color OldOrange = Color.FromRgb(0xFF, 0xA7, 0x26);

    // The panel's switch, as its Inbox button saves it; the bar follows the state pulse.
    static void InboxLook(ListBar t, bool on = true)
    {
        Assert.True(t.Harness.Manager.SaveTasksPanel((t.Harness.Manager.State.TasksPanel ?? TasksPanelState.Default) with { Inbox = on }).IsSuccess);
        t.Bar.UpdateLayout();
    }

    static Border Content(Button line) => (Border)line.Content;

    static Button LineFor(ListBar t, Guid id) => t.Drawn.Single(b => ((TaskLine)b.Tag).Id == id);

    static string MarkOf(Button line) =>
        Inside(line).OfType<FrameworkElement>().Where(e => Equals(e.Tag, FloatingBar.InboxBarMarkTag)).Select(AutomationProperties.GetName).Single();

    // A Claude task waiting on the user (asks), a finished one, a plain note and an important note on no workspace.
    static (Guid Asking, Guid Done, Guid Note, Guid Loose) BarTasks(ListBar t)
    {
        var note = t.Tasks.AddNote("Book the dentist", t.Gepha).Value.Id;
        var loose = t.Tasks.AddNote("Try a nicer font", Maybe<Guid>.None).Value.Id;
        t.Tasks.SetFlag(loose, TaskFlag.Important, true);
        var done = t.Tasks.Create("Lighter terminal", t.Gepha, new TaskSession("s1", @"C:\repos\Example", Start, Maybe<DateTimeOffset>.None)).Value.Id;
        t.Tasks.SetStatusLine(done, "Done: the terminal is about 1 GB lighter");
        t.Tasks.MarkDone(done);
        var asking = t.Tasks.Create("Docked bar jumps", t.Gepha, new TaskSession("s2", @"C:\repos\Example", Start, Maybe<DateTimeOffset>.None)).Value.Id;
        t.Tasks.Ask(asking, "Does the bar stay where you put it?");
        t.Bar.UpdateLayout();
        return (asking, done, note, loose);
    }

    [Fact]
    public void The_bar_draws_the_inbox_line_while_the_panel_is_on_the_inbox_and_the_old_one_otherwise() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        Assert.Equal("task-selection", Content(LineFor(t, k.Note)).Tag);
        var ids = t.Drawn.Select(b => ((TaskLine)b.Tag).Id).ToArray();

        InboxLook(t);
        Assert.True(t.Bar.TaskLinesInInboxLook);
        Assert.Equal(FloatingBar.InboxBarLineTag, Content(LineFor(t, k.Note)).Tag);
        // The same lines in the same order: only the drawing changed.
        Assert.Equal(ids, t.Drawn.Select(b => ((TaskLine)b.Tag).Id));

        InboxLook(t, on: false);
        Assert.Equal("task-selection", Content(LineFor(t, k.Note)).Tag);
    });

    // Each line has its state mark, the panel's shapes; no fill behind it and no orange anywhere on it.
    [Fact]
    public void A_line_has_its_state_mark_and_no_fill_or_orange() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);

        Assert.Equal("Asks you", MarkOf(LineFor(t, k.Asking)));
        Assert.Contains(MarkOf(LineFor(t, k.Done)), new[] { "Finished", "Finished, not running yet" });
        Assert.Equal("A note", MarkOf(LineFor(t, k.Note)));
        foreach (var line in t.Drawn)
        {
            Assert.Equal(Brushes.Transparent, Content(line).Background);
            var brushes = Inside(line).OfType<DependencyObject>().SelectMany(o => new[]
            {
                (o as TextBlock)?.Foreground, (o as Border)?.Background, (o as Border)?.BorderBrush, (o as Shape)?.Fill, (o as Shape)?.Stroke,
            }).OfType<SolidColorBrush>().Select(b => b.Color);
            Assert.DoesNotContain(OldOrange, brushes);
        }
    });

    // The owner, after using the quiet second line: every bar line is its mark, its title and its chip, nothing more;
    // the question or the status line is in the tooltip, in full, with the title.
    [Fact]
    public void Every_line_is_its_mark_title_and_chip_with_the_text_in_the_tooltip() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);

        foreach (var id in new[] { k.Asking, k.Done, k.Note })
        {
            var texts = Inside(LineFor(t, id)).OfType<TextBlock>().Where(b => !b.Text.Contains("GEPHA")).ToArray();
            Assert.Single(texts, b => Equals(b.Tag, "task-text"));
            Assert.DoesNotContain(texts, b => b.Text.Contains("Does the bar stay") || b.Text.Contains("1 GB lighter"));
        }
        var tip = $"{LineFor(t, k.Asking).ToolTip}";
        Assert.Contains("Docked bar jumps", tip);
        Assert.Contains("Does the bar stay where you put it?", tip);
        Assert.Contains("1 GB lighter", $"{LineFor(t, k.Done).ToolTip}");
    });

    [Fact]
    public void The_title_is_semibold_on_claudes_line_and_normal_on_a_note() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);
        TextBlock Title(Guid id) => Inside(LineFor(t, id)).OfType<TextBlock>().First(b => Equals(b.Tag, "task-text"));

        Assert.Equal(FontWeights.SemiBold, Title(k.Asking).FontWeight);
        Assert.Equal(FontWeights.Normal, Title(k.Note).FontWeight);
        Assert.Equal(FontWeights.Bold, Title(k.Loose).FontWeight);
    });

    // The chip sits under the title with the workspace's full name, at the narrowest bar too; no chip without a workspace.
    [Fact]
    public void The_chip_is_under_the_title_with_the_full_name_and_none_without_a_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);
        t.Bar.UpdateLayout();

        var line = LineFor(t, k.Note);
        var chip = Inside(line).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Move to workspace");
        var title = Inside(line).OfType<TextBlock>().First(b => Equals(b.Tag, "task-text"));
        Assert.True(chip.TranslatePoint(new Point(0, 0), line).Y >= title.TranslatePoint(new Point(0, title.ActualHeight), line).Y - 0.5);
        Assert.Contains(Inside(chip).OfType<TextBlock>(), b => b.Text == "GEPHA");
        Assert.False(Inside(LineFor(t, k.Loose)).OfType<Button>().Any(b => AutomationProperties.GetName(b) == "Move to workspace"));

        Click(chip);
        Assert.NotNull(t.Bar.TaskMoveMenu);
        t.Bar.TaskMoveMenu!.IsOpen = false;
    });

    // The selection and the glow paint the Inbox look's selected fill on the line's content.
    [Fact]
    public void The_selection_and_the_glow_use_the_inbox_fill() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);
        var fill = Color.FromRgb(0x27, 0x2C, 0x36);

        t.Bar.Selection.Select(k.Note);
        Assert.True(t.Bar.TaskLineSelected(k.Note));
        Assert.Equal(fill, ((SolidColorBrush)Content(LineFor(t, k.Note)).Background).Color);

        ClickBox(t);
        t.Box.Text = "Call the accountant";
        Press(t.Bar, t.Box, Key.Enter);
        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Call the accountant").Id;
        Assert.True(t.Bar.TaskLineGlowing(added));
        Assert.Equal(FloatingBar.InboxBarLineTag, Content(LineFor(t, added)).Tag);
    });

    // A closed line keeps its place with Undo where its mark was, its title struck through; Undo brings it back.
    [Fact]
    public void A_closed_line_shows_undo_in_the_marks_place_and_undo_brings_it_back() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);

        CloseFromMenu(t, LineFor(t, k.Note));
        var line = LineFor(t, k.Note);
        Assert.True(((TaskLine)line.Tag).Closing);
        Assert.Equal(TextDecorations.Strikethrough, Inside(line).OfType<TextBlock>().First(b => Equals(b.Tag, "task-text")).TextDecorations);
        Click(Inside(line).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Undo"));
        t.Bar.PaintTaskList();
        Assert.False(((TaskLine)LineFor(t, k.Note).Tag).Closing);
        Assert.NotEqual(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == k.Note).Status);
    });

    // Editing a title in place shows the bar's own editor where the title is.
    [Fact]
    public void A_title_being_edited_shows_the_editor() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = BarTasks(t);
        InboxLook(t);

        t.Bar.BeginTaskEdit(k.Note);
        Assert.Contains(Inside(LineFor(t, k.Note)).OfType<TextBox>(), b => AutomationProperties.GetName(b) == "Edit task");
    });

    // The bar's drag works on the new lines: a line dragged below another lands below it.
    [Fact]
    public void A_drag_still_reorders() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        InboxLook(t);
        Settle(t);
        var before = t.Lines.Select(l => ((TaskLine)l.Tag).Title).ToArray();
        Assert.Equal(["n3", "n2", "n1"], before);

        Drag(t, t.Lines[0], In(t, t.Lines[2], 0.8));
        Settle(t);

        Assert.Equal(["n2", "n1", "n3"], t.Lines.Select(l => ((TaskLine)l.Tag).Title));
        Assert.Equal(FloatingBar.InboxBarLineTag, Content(t.Lines[0]).Tag);
    });

    // "+N more waiting on you" in the Inbox look: no amber words.
    [Fact]
    public void The_waiting_group_line_has_no_amber_words() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 5).ToList().ForEach(i =>
        {
            var id = t.Tasks.AddNote($"w{i}", t.Gepha).Value.Id;
            t.Tasks.SetStatus(id, TaskStatus.WaitingOnYou);
        });
        InboxLook(t);
        var group = Inside(t.List).OfType<Button>().Single(b => Equals(b.Tag, FloatingBar.WaitingGroupTag));
        var words = Inside(group).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-waiting-group-text"));
        Assert.Equal(Deskbar.App.InboxLook.Ink2, words.Foreground);
    });

    // Every text colour of the bar's Inbox lines reads at 4.5 to 1 or better on the bar's surface, on its hover and on the
    // selected fill.
    [Fact]
    public void Every_bar_text_colour_has_a_contrast_of_at_least_4_5()
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        static double Contrast(Color a, Color b)
        {
            var (x, y) = (Luminance(a), Luminance(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
        static Color Over(Color under, byte alpha, Color over) => Color.FromRgb(
            (byte)Math.Round(under.R + (over.R - under.R) * alpha / 255.0), (byte)Math.Round(under.G + (over.G - under.G) * alpha / 255.0),
            (byte)Math.Round(under.B + (over.B - under.B) * alpha / 255.0));
        var bar = BarSurface.Base;
        var backs = new (string Name, Color Colour)[]
        {
            ("the bar", bar), ("the hover", Over(bar, 0x1F, Colors.White)), ("the selection", Color.FromRgb(0x27, 0x2C, 0x36)),
            ("the chip", ((SolidColorBrush)Deskbar.App.InboxLook.Surface).Color),
        };
        var inks = new (string Name, Brush Ink)[] { ("ink", Deskbar.App.InboxLook.Ink), ("second ink", Deskbar.App.InboxLook.Ink2) };
        Assert.All(backs.SelectMany(b => inks.Select(i => (i.Name, b.Name, Ratio: Contrast(((SolidColorBrush)i.Ink).Color, b.Colour)))),
            p => Assert.True(p.Ratio >= 4.5, $"{p.Item1} on {p.Item2}: {p.Ratio:0.00}"));
    }

    // --- the "Needs you" caption --------------------------------------------------------------------------------------

    static IReadOnlyList<FrameworkElement> Captions(ListBar t) =>
        Inside(t.List).OfType<FrameworkElement>().Where(e => Equals(e.Tag, FloatingBar.InboxBarCaptionTag)).ToArray();

    // Five tasks wait on the user; the block folds after three. The caption sits inside the block's first line and counts
    // all five, the folded ones too.
    static ListBar WaitingBar()
    {
        var t = Build();
        Enumerable.Range(1, 5).ToList().ForEach(i =>
        {
            var id = t.Tasks.AddNote($"w{i}", t.Gepha).Value.Id;
            t.Tasks.SetStatus(id, TaskStatus.WaitingOnYou);
        });
        t.Tasks.AddNote("Plain note", t.Gepha);
        t.Bar.UpdateLayout();
        return t;
    }

    [Fact]
    public void The_needs_you_caption_sits_in_the_blocks_first_line_and_counts_the_folded_ones_too() => StaThread.Run(() =>
    {
        using var t = WaitingBar();
        InboxLook(t);

        var caption = Assert.Single(Captions(t));
        Assert.Equal("Needs you 5", AutomationProperties.GetName(caption));
        var first = t.Drawn.First();
        Assert.Equal(TaskStatus.WaitingOnYou, ((TaskLine)first.Tag).Status);
        Assert.True(caption.IsDescendantOf(first));
    });

    [Fact]
    public void No_caption_without_a_waiting_block_and_none_in_the_old_look() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("Plain note", t.Gepha);
        InboxLook(t);
        Assert.Empty(Captions(t));

        using var w = WaitingBar();
        Assert.Empty(Captions(w));
    });

    // A press on the caption is the caption's: it does not press the first task's line.
    [Fact]
    public void A_press_on_the_caption_does_not_press_the_line() => StaThread.Run(() =>
    {
        using var t = WaitingBar();
        InboxLook(t);
        var caption = Captions(t).Single();
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };

        caption.RaiseEvent(press);

        Assert.True(press.Handled);
        Assert.False(t.Drawn.First().IsPressed);
    });

    // The caption changes no slot and no fold: the same lines in the same places, the fold line at the same index.
    [Fact]
    public void The_slots_and_the_fold_are_unchanged_by_the_caption() => StaThread.Run(() =>
    {
        using var t = WaitingBar();
        IReadOnlyList<string> Places() => LinesPanel(t).Children.OfType<Button>()
            .Select(b => b.Tag is TaskLine line ? line.Title : $"{b.Tag}").ToArray();
        var before = Places();
        var inSight = t.Bar.TaskLinesInSight.Select(l => l.Id).ToArray();

        InboxLook(t);

        Assert.Equal(before, Places());
        Assert.Contains(FloatingBar.WaitingGroupTag, Places());
        Assert.Equal(inSight, t.Bar.TaskLinesInSight.Select(l => l.Id));
    });

    // --- the list's foot (FloatingBar.InboxFooter.cs) -----------------------------------------------------------------

    static Grid AddRow(ListBar t) => Inside(t.List).OfType<Grid>().Single(g => Equals(g.Tag, "task-add-row"));

    static bool ShowsIn(object scope, string name) =>
        Inside(scope).OfType<Button>().Any(b => AutomationProperties.GetName(b) == name && b.Visibility == Visibility.Visible);

    // The panel button's words in each state: "All tasks", with the out-of-sight count as "· N more" (the old button's
    // "+N more", the same count), "Hide tasks" while the panel is shown.
    [Fact]
    public void The_all_tasks_button_says_all_tasks_with_its_count_and_hide_tasks_while_the_panel_shows() => StaThread.Run(() =>
    {
        var shown = new System.Reactive.Subjects.BehaviorSubject<bool>(false);
        using var t = Build(openWindow: () => { }, panelShown: shown);
        t.Tasks.AddNote("One", t.Gepha);
        t.Tasks.AddNote("Two", t.Gepha);
        Assert.Equal("All tasks", t.Bar.TaskListMoreText);
        InboxLook(t);
        Assert.Equal("All tasks", t.Bar.InboxFooterWords);

        Enumerable.Range(1, 4).ToList().ForEach(i => t.Tasks.AddNote($"More {i}", t.Gepha));
        t.Bar.UpdateLayout();
        Assert.Equal("All tasks · 3 more", t.Bar.InboxFooterWords);
        InboxLook(t, on: false);
        Assert.Equal("+3 more", t.Bar.TaskListMoreText);
        InboxLook(t);

        shown.OnNext(true);
        Assert.Equal("Hide tasks", t.Bar.InboxFooterWords);
        shown.OnNext(false);
        Assert.Equal("All tasks · 3 more", t.Bar.InboxFooterWords);
    });

    // With no panel behind the bar the button expands the list in place, as the old one did, and folds it again.
    [Fact]
    public void Without_a_panel_the_button_expands_the_list_and_folds_it_again() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        InboxLook(t);
        Assert.Equal("All tasks · 2 more", t.Bar.InboxFooterWords);

        Click(ButtonNamed(t.List, FloatingBar.AllTasksName));
        t.Bar.UpdateLayout();
        Assert.Equal("Fewer tasks", t.Bar.InboxFooterWords);
        Assert.Equal(5, t.Bar.TaskLinesInSight.Count);

        Click(ButtonNamed(t.List, FloatingBar.AllTasksName));
        Assert.Equal("All tasks · 2 more", t.Bar.InboxFooterWords);
    });

    // The foot is the Inbox look's alone, and the add row loses its two buttons there and keeps them in the old look.
    [Fact]
    public void The_foot_shows_only_in_the_inbox_look_and_the_add_row_keeps_only_adding() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { }, openManager: () => { });
        t.Tasks.AddNote("One", t.Gepha);
        Assert.False(t.Bar.InboxFooterShown);
        Assert.True(ShowsIn(AddRow(t), "More tasks"));
        Assert.True(ShowsIn(AddRow(t), "Manager"));

        InboxLook(t);
        Assert.True(t.Bar.InboxFooterShown);
        Assert.False(ShowsIn(AddRow(t), "More tasks"));
        Assert.False(ShowsIn(AddRow(t), "Manager"));
        var foot = Inside(t.List).OfType<Border>().Single(b => Equals(b.Tag, FloatingBar.InboxFooterTag));
        Assert.True(ShowsIn(foot, FloatingBar.AllTasksName));
        Assert.True(ShowsIn(foot, "Manager"));
        // After the lines, never among them: the lines' panel still holds only line buttons.
        Assert.False(foot.IsDescendantOf(LinesPanel(t)));

        InboxLook(t, on: false);
        Assert.False(t.Bar.InboxFooterShown);
        Assert.True(ShowsIn(AddRow(t), "More tasks"));
    });

    // The same lines, the same count and the same fold in both looks.
    [Fact]
    public void The_foot_changes_no_line_count_or_fold() => StaThread.Run(() =>
    {
        using var t = WaitingBar();
        IReadOnlyList<string> Places() => LinesPanel(t).Children.OfType<Button>()
            .Select(b => b.Tag is TaskLine line ? line.Title : $"{b.Tag}").ToArray();
        var before = Places();
        var inSight = t.Bar.TaskLinesInSight.Select(l => l.Id).ToArray();
        var count = t.Bar.TaskListMoreText;

        InboxLook(t);

        Assert.Equal(before, Places());
        Assert.Equal(inSight, t.Bar.TaskLinesInSight.Select(l => l.Id));
        var hidden = count.StartsWith('+') ? count[1..].Split(' ')[0] : "";
        // The same words as the old button gives them: the count when lines are out of sight, nothing when the old button
        // showed nothing (no panel and nothing hidden).
        Assert.Equal(count.Length == 0 ? "" : hidden.Length > 0 ? $"All tasks · {hidden} more" : "All tasks", t.Bar.InboxFooterWords);
    });

    // A click does what the old button's did: the panel shown or hidden; Manager opens the manager.
    [Fact]
    public void A_click_on_the_foot_does_what_the_old_buttons_did() => StaThread.Run(() =>
    {
        var toggles = 0;
        var managers = 0;
        using var t = Build(openWindow: () => toggles++, openManager: () => managers++);
        t.Tasks.AddNote("One", t.Gepha);
        InboxLook(t);
        var foot = Inside(t.List).OfType<Border>().Single(b => Equals(b.Tag, FloatingBar.InboxFooterTag));

        Click(ButtonNamed(foot, FloatingBar.AllTasksName));
        Click(ButtonNamed(foot, "Manager"));

        Assert.Equal(1, toggles);
        Assert.Equal(1, managers);
    });

    // --- the add box and its row (FloatingBar.InboxAdd.cs) ------------------------------------------------------------



    static Button AddToggle(ListBar t, string name) => Inside(t.Chips).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    // The look comes with the Inbox, and the old look comes back exactly as it was.
    [Fact]
    public void The_add_box_and_row_take_the_inbox_look_only_with_the_inbox_and_come_back_as_they_were() => StaThread.Run(() =>
    {
        using var t = Build(startClaude: _ => null);
        t.Tasks.AddNote("One", t.Gepha);
        var oldFill = t.Box.Background;
        var oldEdge = t.Box.BorderBrush;
        var oldTemplate = t.Box.Template;
        var oldPlaceholder = Placeholder(t).Foreground;
        var chip = AddToggle(t, "Add to workspace");
        var oldChipFace = chip.Content;
        var play = Inside(t.List).OfType<Button>().Single(b => AutomationProperties.GetName(b) == FloatingBar.AddAndStartClaudeText);
        Assert.Same(t.Chips, play.Parent);
        Assert.False(t.Bar.AddRowInInboxLook);

        InboxLook(t);
        Assert.True(t.Bar.AddRowInInboxLook);
        Assert.Equal(FloatingBar.InboxFieldFill, t.Box.Background);
        Assert.Equal(FloatingBar.InboxFieldEdge, t.Box.BorderBrush);
        Assert.Equal(Deskbar.App.InboxLook.Ink2, Placeholder(t).Foreground);
        Assert.NotSame(oldChipFace, chip.Content);
        Assert.NotSame(t.Chips, play.Parent);
        Assert.True(AddRow(t).Children.Contains(play));
        // The play glyph itself, the old button's (U+E768), in its quiet round face.
        Assert.Contains(Inside(play).OfType<TextBlock>(), b => b.Text == "");

        InboxLook(t, on: false);
        Assert.False(t.Bar.AddRowInInboxLook);
        Assert.Same(oldFill, t.Box.Background);
        Assert.Same(oldEdge, t.Box.BorderBrush);
        Assert.Same(oldTemplate, t.Box.Template);
        Assert.Same(oldPlaceholder, Placeholder(t).Foreground);
        Assert.Same(oldChipFace, chip.Content);
        Assert.Same(t.Chips, play.Parent);
    });

    // The chip: an outline, a dot in the workspace's colour and the full name; "none" without a dot. The same menu.
    [Fact]
    public void The_add_chip_shows_the_full_name_with_a_dot_and_opens_the_same_menu() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxLook(t);
        var face = Inside(t.Chips).OfType<Border>().Single(b => Equals(b.Tag, FloatingBar.InboxAddChipTag));
        Assert.NotEqual(Colors.Transparent, ((SolidColorBrush)face.BorderBrush).Color);
        Assert.Contains(Inside(face).OfType<TextBlock>(), b => b.Text == "GEPHA");
        Assert.Equal(Visibility.Visible, Inside(face).OfType<Ellipse>().Single().Visibility);

        Click(AddToggle(t, "Add to workspace"));
        Assert.NotNull(t.Bar.TaskAddMenu);
        t.Bar.TaskAddMenu!.IsOpen = false;
    });

    // The toggles: on and off differ by fill, not by colour alone, and the tooltip says the state.
    [Fact]
    public void The_add_toggles_differ_by_fill_and_say_their_state() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxLook(t);
        var important = AddToggle(t, "Add as important");
        Border Pill() => Inside(important).OfType<Border>().Single(b => Equals(b.Tag, FloatingBar.InboxAddToggleTag));
        Assert.Equal(Brushes.Transparent, Pill().Background);
        Assert.StartsWith("Important: off", $"{important.ToolTip}");

        Click(important);
        Assert.Equal(FloatingBar.InboxToggleOnFill, Pill().Background);
        Assert.StartsWith("Important: on", $"{important.ToolTip}");
        Assert.StartsWith("Urgent: off", $"{AddToggle(t, "Add as urgent").ToolTip}");
    });

    // The box still adds a note on Enter and clears on Esc in the Inbox look, and its edge follows the keyboard.
    [Fact]
    public void The_add_box_still_adds_and_clears_in_the_inbox_look() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxLook(t);
        ClickBox(t);
        t.Box.Text = "Call the accountant";
        Press(t.Bar, t.Box, Key.Enter);
        Assert.Contains(t.Tasks.Board.Tasks, x => x.Title == "Call the accountant");

        ClickBox(t);
        t.Box.Text = "Draft";
        Press(t.Bar, t.Box, Key.Escape);
        Assert.Equal("", t.Box.Text);
    });

    // A docked bar still fits: the lines in sight, the foot and the add row stay inside the bar's height.
    [Fact]
    public void A_docked_bar_still_fits_its_lines_with_the_new_rows() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { }, openManager: () => { });
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 700;
        Enumerable.Range(1, 14).ToList().ForEach(i => t.Tasks.AddNote($"Errand number {i}", t.Gepha));
        InboxLook(t);
        Settle(t);

        var foot = Inside(t.List).OfType<Border>().Single(b => Equals(b.Tag, FloatingBar.InboxFooterTag));
        Assert.True(t.Bar.InboxFooterShown);
        Assert.True(foot.TranslatePoint(new Point(0, foot.ActualHeight), t.Bar).Y <= t.Bar.ActualHeight + 0.5);
        Assert.True(t.Bar.TaskLinesInSight.Count >= 3);
        Assert.StartsWith("All tasks · ", t.Bar.InboxFooterWords);
    });

    // Every text and glyph of the add box and its row reads at 4.5 to 1 or better on its surface.
    [Fact]
    public void Every_add_row_colour_has_a_contrast_of_at_least_4_5()
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        static double Contrast(Brush a, Brush b)
        {
            var (x, y) = (Luminance(((SolidColorBrush)a).Color), Luminance(((SolidColorBrush)b).Color));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
        var bar = new SolidColorBrush(BarSurface.Base);
        var hover = new SolidColorBrush(Color.FromRgb(0x37, 0x38, 0x3C));
        var pairs = new (string Name, Brush Ink, Brush Back)[]
        {
            ("typed text on the box", Deskbar.App.InboxLook.Ink, FloatingBar.InboxFieldFill),
            ("placeholder on the box", Deskbar.App.InboxLook.Ink2, FloatingBar.InboxFieldFill),
            ("chip name on the chip", Deskbar.App.InboxLook.Ink, Deskbar.App.InboxLook.Surface),
            ("none and the arrow on the chip", Deskbar.App.InboxLook.Ink2, Deskbar.App.InboxLook.Surface),
            ("an off toggle on the bar", Deskbar.App.InboxLook.Ink2, bar),
            ("an off toggle under the pointer", Deskbar.App.InboxLook.Ink2, hover),
            ("an on toggle", FloatingBar.InboxToggleOnInk, FloatingBar.InboxToggleOnFill),
            ("the play glyph", Deskbar.App.InboxLook.Ink2, Deskbar.App.InboxLook.Raised),
            ("the foot's words", Deskbar.App.InboxLook.Ink, new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C))),
            ("the foot's count", Deskbar.App.InboxLook.Ink2, new SolidColorBrush(Color.FromRgb(0x2C, 0x32, 0x3D))),
        };
        Assert.All(pairs, p => Assert.True(Contrast(p.Ink, p.Back) >= 4.5, $"{p.Name}: {Contrast(p.Ink, p.Back):0.00}"));
    }

    // A long workspace name trims inside a line's chip, with "…", and never runs past the line's edge.
    [Fact]
    public void A_long_workspace_name_trims_inside_the_lines_chip() => StaThread.Run(() =>
    {
        using var t = Build();
        var gepha = t.Gepha;
        Assert.True(t.Harness.Manager.RenameWorkspace(gepha, "Customer onboarding portal for the northern region").IsSuccess);
        var id = t.Tasks.AddNote("Book the dentist", gepha).Value.Id;
        InboxLook(t);
        t.Bar.UpdateLayout();

        var line = LineFor(t, id);
        var name = Inside(line).OfType<TextBlock>().Single(b => b.Text.StartsWith("Customer onboarding"));
        Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
        Assert.True(name.TranslatePoint(new Point(name.ActualWidth, 0), line).X <= line.ActualWidth + 0.5);
    });

    // A Claude terminal brought to the front selects its task (#477); in the Inbox look, as in the old one, the task's line
    // is lit and a line out of sight is scrolled into view (the owner: "highlight those inbox issues when selecting claude
    // session / terminal windows, as it's done with the list").
    [Fact]
    public void In_the_inbox_look_a_line_selected_from_elsewhere_is_lit_and_scrolled_into_view() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 10).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));
        InboxLook(t);
        t.Bar.UpdateLayout();
        var far = t.Tasks.Board.Pile().Single(x => x.Title == "Note 1").Id;
        Assert.DoesNotContain(t.Lines, l => ((TaskLine)l.Tag).Id == far);

        t.Bar.Selection.Select(far);
        t.Bar.UpdateLayout();

        Assert.True(t.Bar.TaskLineSelected(far));
        Assert.Contains(t.Lines, l => ((TaskLine)l.Tag).Id == far);
    });
}
