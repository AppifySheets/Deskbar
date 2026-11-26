using System.IO;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;

namespace Deskbar.Windows.Tests;

// The picker row under a task's comment box on its Inbox page, and the answer buttons under its question. The row is
// wired to a real ClaudeSessions over a fake `claude` (virtual time, no Claude started), so a menu pick goes the whole
// way: the board's pick, the copy's resume, the join, and Cancel back to the original. The answer buttons post through
// the board's own AnswerWithOption.
public partial class TasksBoardTests
{
    static readonly SessionSettings OpusMedium = new("opus", "medium", Maybe<string>.None);

    // `claude` for the picker: a list the test sets, a resume with flags that makes a copy, and a stop.
    sealed class PickerCli(TestScheduler clock) : IClaudeCli
    {
        public sealed record Row(string SessionId, bool Background = true, string Status = "idle", string State = "done");

        public List<Row> Listed { get; } = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];
        int made;

        public Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
        {
            Calls.Add(arguments);
            return Task.FromResult(arguments switch
            {
                ["agents", "--json"] => Result.Success(JsonSerializer.Serialize(Listed.Select(r => r.Background
                    ? (object)new { pid = 1, id = r.SessionId[..8], cwd = @"C:\repos\TaskSpaces", kind = "background", sessionId = r.SessionId, status = r.Status, state = r.State, startedAt = clock.Now.ToUnixTimeMilliseconds() }
                    : new { pid = 1, cwd = @"C:\repos\TaskSpaces", kind = "interactive", sessionId = r.SessionId, status = r.Status }))),
                ["--bg", "--resume", ..] => Copied(),
                ["stop", var shortId] => Stopped(shortId),
                _ => Result.Failure<string>("unexpected"),
            });
        }

        Result<string> Copied()
        {
            var id = $"{0xc0000000 + ++made:x8}-0000-4000-8000-000000000000";
            Listed.Add(new Row(id));
            return Result.Success($"backgrounded · {id[..8]}");
        }

        Result<string> Stopped(string shortId)
        {
            Listed.RemoveAll(r => r.SessionId.StartsWith(shortId, StringComparison.Ordinal));
            return Result.Success($"stopped {shortId}");
        }
    }

    sealed class PickerRig : IDisposable
    {
        public required Board Board { get; init; }
        public required ClaudeSessions Sessions { get; init; }
        public required PickerCli Cli { get; init; }
        public required TestScheduler Clock { get; init; }
        public required string Home { get; init; }
        public required Guid Task { get; init; }

        public void Run() => Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        public FrameworkElement Page => (FrameworkElement)Board.Window.InboxPageContent!;

        public FrameworkElement Row => Inside(Page).OfType<FrameworkElement>().Single(e => Equals(e.Tag, TasksWindow.PickerRowTag));

        public Button Button(string name) => ButtonNamed(Row, name);

        public void Dispose()
        {
            Sessions.Dispose();
            Board.Dispose();
            if (Directory.Exists(Home)) Directory.Delete(Home, true);
        }
    }

    // A task with session "aaaaaaaa-…" launched on Opus, medium effort (none: a note), listed as `row` says, its page open.
    static PickerRig PickerPage(Func<string, PickerCli.Row>? row = null, bool session = true)
    {
        var t = Build();
        var clock = new TestScheduler();
        clock.AdvanceTo(Start.UtcTicks);
        var cli = new PickerCli(clock);
        var home = Path.Combine(Path.GetTempPath(), "deskbar-picker-" + Guid.NewGuid().ToString("N"));
        var sessions = new ClaudeSessions(t.Tasks, t.Harness.Manager, cli, clock, _ => Maybe<Guid>.None, home,
            background: Scheduler.Immediate, folderExists: _ => true);
        t.Controller.Sessions = sessions;
        const string sessionId = "aaaaaaaa-0000-4000-8000-000000000001";
        var id = session
            ? t.Tasks.Create("Ship the fix", t.Gepha, Session(sessionId) with { LaunchedWith = OpusMedium }).Value.Id
            : t.Tasks.AddNote("Write the plan", t.Gepha).Value.Id;
        if (session) cli.Listed.Add(row?.Invoke(sessionId) ?? new PickerCli.Row(sessionId));
        var rig = new PickerRig { Board = t, Sessions = sessions, Cli = cli, Clock = clock, Home = home, Task = id };
        rig.Run();
        t.Controller.Show();
        ChooseInbox(t);
        // A Claude task's line is on the Claude tab, a note's on the Notes tab.
        t.Window.ChooseInboxTab(session ? InboxTab.Claude : InboxTab.Notes);
        Click(LineOf(t.Window, id));
        return rig;
    }

    static string Words(Button button) => string.Join(" ", Inside(button).OfType<TextBlock>().Select(b => b.Text));

    static MenuItem ItemNamed(ContextMenu menu, string name) =>
        menu.Items.OfType<MenuItem>().Single(i => AutomationProperties.GetName(i) == name);

    // The row sits under the comment box and says what the session runs on; its menus tick the current choice.
    [Fact]
    public void The_row_under_the_comment_box_shows_the_settings_and_its_menus_tick_the_current_ones() => StaThread.Run(() =>
    {
        using var r = PickerPage();

        var everything = Inside(r.Page).ToList();
        var box = everything.OfType<TextBox>().First(b => AutomationProperties.GetName(b) == "New comment");
        Assert.True(everything.IndexOf(r.Row) > everything.IndexOf(box));
        Assert.Equal("Opus · medium", Words(r.Button(TasksWindow.PickerModelName)));
        Assert.Equal(TasksWindow.ModeDefaultText, Words(r.Button(TasksWindow.PickerModeName)));
        Assert.All(new[] { TasksWindow.PickerModelName, TasksWindow.PickerModeName }, name =>
        {
            Assert.True(r.Button(name).IsEnabled);
            Assert.True(r.Button(name).Focusable);
            Assert.True(r.Button(name).IsTabStop);
        });

        var models = r.Board.Window.OpenModelMenu(r.Task, r.Button(TasksWindow.PickerModelName));
        Assert.True(ItemNamed(models, "Opus").IsChecked);
        Assert.False(ItemNamed(models, "Sonnet").IsChecked);
        // A tick mark of its own, not a colour.
        string Tick(string name) => Inside(ItemNamed(models, name)).OfType<TextBlock>().Single(b => Equals(b.Tag, TasksWindow.PickerTickTag)).Text;
        Assert.Equal("✓", Tick("Opus"));
        Assert.Equal("", Tick("Sonnet"));
        Assert.True(ItemNamed(models, "Medium").IsChecked);
        Assert.False(ItemNamed(models, TasksWindow.ModelDefaultText).IsChecked);
        Assert.Contains(models.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: SessionPicker.NextPauseTip });
        models.IsOpen = false;

        var modes = r.Board.Window.OpenModeMenu(r.Task, r.Button(TasksWindow.PickerModeName));
        Assert.True(ItemNamed(modes, TasksWindow.ModeDefaultText).IsChecked);
        foreach (var mode in SessionChoices.Modes)
            Assert.Contains(Inside(ItemNamed(modes, mode.Label)).OfType<TextBlock>(), b => b.Text == mode.Hint);
        modes.IsOpen = false;
    });

    // A pick goes the whole way: the copy is made with the pick and joins; the row says so and offers Cancel, which goes
    // back to the earlier session (still running) and stops the copy.
    [Fact]
    public void A_pick_switches_the_session_and_Cancel_goes_back() => StaThread.Run(() =>
    {
        using var r = PickerPage();
        var models = r.Board.Window.OpenModelMenu(r.Task, r.Button(TasksWindow.PickerModelName));
        ItemNamed(models, "Sonnet").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        models.IsOpen = false;
        r.Run();

        var resume = Assert.Single(r.Cli.Calls, c => c is ["--bg", "--resume", ..]);
        Assert.Equal(ClaudeStart.SwitchArguments("aaaaaaaa-0000-4000-8000-000000000001", new SessionSettings("sonnet", "medium", Maybe<string>.None)), resume);
        Assert.Equal("Sonnet · medium", Words(r.Button(TasksWindow.PickerModelName)));
        Assert.Contains(Inside(r.Row).OfType<TextBlock>(), b => b.Text == SessionPicker.ConfirmingWords);

        Click(r.Button(TasksWindow.PickerCancelText));
        r.Run();

        Assert.Single(r.Cli.Calls, c => c is ["stop", "c0000001"]);
        Assert.Equal("Opus · medium", Words(r.Button(TasksWindow.PickerModelName)));
        Assert.False(HasButtonNamed(r.Row, TasksWindow.PickerCancelText));
    });

    // A busy session: the pick waits for its pause, says so, and Cancel drops it.
    [Fact]
    public void A_pick_on_a_busy_session_waits_and_can_be_cancelled() => StaThread.Run(() =>
    {
        using var r = PickerPage(id => new PickerCli.Row(id, Status: "busy", State: "working"));
        var modes = r.Board.Window.OpenModeMenu(r.Task, r.Button(TasksWindow.PickerModeName));
        ItemNamed(modes, "Plan first").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        modes.IsOpen = false;
        r.Run();

        Assert.DoesNotContain(r.Cli.Calls, c => c is ["--bg", "--resume", ..]);
        Assert.Equal("Plan first", Words(r.Button(TasksWindow.PickerModeName)));
        Assert.Contains(Inside(r.Row).OfType<TextBlock>(), b => b.Text == SettingsSwitch.Busy);

        Click(r.Button(TasksWindow.PickerCancelText));

        Assert.Equal(TasksWindow.ModeDefaultText, Words(r.Button(TasksWindow.PickerModeName)));
        Assert.True(r.Board.Tasks.Board.Tasks.Single(t => t.Id == r.Task).Wanted.HasNoValue);
    });

    // A session in a window of its own: the buttons stay, disabled, and their tooltip says why.
    [Fact]
    public void A_session_in_its_own_window_shows_disabled_buttons_that_say_why() => StaThread.Run(() =>
    {
        using var r = PickerPage(id => new PickerCli.Row(id, Background: false));

        foreach (var name in new[] { TasksWindow.PickerModelName, TasksWindow.PickerModeName })
        {
            Assert.False(r.Button(name).IsEnabled);
            Assert.Equal(SettingsSwitch.NotDeskbars, r.Button(name).ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(r.Button(name)));
        }
    });

    // A task with no session: the row is there, and a pick is kept for the first start, with nothing resumed.
    [Fact]
    public void A_task_without_a_session_keeps_the_pick_for_its_first_start() => StaThread.Run(() =>
    {
        using var r = PickerPage(session: false);
        Assert.Equal(SessionPicker.FirstStartTip, r.Button(TasksWindow.PickerModelName).ToolTip);

        var models = r.Board.Window.OpenModelMenu(r.Task, r.Button(TasksWindow.PickerModelName));
        ItemNamed(models, "Haiku").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        models.IsOpen = false;
        r.Run();

        Assert.Equal("Haiku · default effort", Words(r.Button(TasksWindow.PickerModelName)));
        Assert.Equal(new SessionSettings("haiku", Maybe<string>.None, Maybe<string>.None), r.Board.Tasks.Board.Tasks.Single(t => t.Id == r.Task).Wanted.Value);
        Assert.DoesNotContain(r.Cli.Calls, c => c is ["--bg", ..]);
    });

    // --- the answer buttons -------------------------------------------------------------------------------------------

    static readonly string[] Answers = ["Yes, merge it", "No, wait for CI", "Rebase first, then merge it", "Close the pull request now"];

    // `asker`: the session that asks, so the question goes into the chat; none, and it shows in the amber-edged bubble.
    static (Board Board, Guid Task) AnswerPage(Maybe<string> asker, IReadOnlyList<string>? options = null, double width = Wide)
    {
        var t = Build(width: width);
        var id = t.Tasks.Create("Ship the fix", t.Gepha, Session("s1")).Value.Id;
        Assert.True(t.Tasks.Ask(id, "OK to merge?", asker, options ?? Answers).IsSuccess);
        t.Controller.Show();
        ChooseInbox(t);
        Click(LineOf(t.Window, id));
        return (t, id);
    }

    static StackPanel AnswerRow(Board t) =>
        Inside(t.Window.InboxPageContent!).OfType<StackPanel>().Single(p => Equals(p.Tag, TasksWindow.AnswerButtonsTag));

    [Fact]
    public void The_answers_show_under_the_question_in_the_chat_and_a_click_posts_one() => StaThread.Run(() =>
    {
        var (t, id) = AnswerPage("s1");
        using var _ = t;

        var row = AnswerRow(t);
        Assert.Equal(Answers, Inside(row).OfType<Button>().Select(b => AutomationProperties.GetName(b)));
        Assert.All(Inside(row).OfType<Button>(), b =>
        {
            Assert.True(b.Focusable);
            Assert.True(b.IsTabStop);
        });
        // Under the question's own text, in its comment.
        var question = Inside(t.Window.InboxPageContent!).OfType<RichTextBox>()
            .First(b => new System.Windows.Documents.TextRange(b.Document.ContentStart, b.Document.ContentEnd).Text.Contains("OK to merge?"));
        Assert.Same(LogicalTreeHelper.GetParent(question), LogicalTreeHelper.GetParent(row));

        Click(ButtonNamed(row, Answers[1]));

        Assert.Contains(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == Answers[1] && c.SessionId.HasNoValue);
    });

    // A question the chat does not show (no question comment): the buttons go in its amber-edged bubble.
    [Fact]
    public void The_answers_show_in_the_question_bubble_when_the_chat_does_not_carry_it() => StaThread.Run(() =>
    {
        var (t, _) = AnswerPage(Maybe<string>.None);
        using var __ = t;

        var bubble = Inside(t.Window.InboxPageContent!).OfType<Border>().Single(b => Equals(b.Tag, TasksWindow.QuestionBubbleTag));
        Assert.Contains(AnswerRow(t), Inside(bubble));
    });

    // Buttons drawn for an older question are refused, and the page says so; nothing is posted.
    [Fact]
    public void A_click_on_the_answers_of_a_replaced_question_is_refused() => StaThread.Run(() =>
    {
        var (t, id) = AnswerPage("s1");
        using var _ = t;
        var stale = ButtonNamed(AnswerRow(t), Answers[0]);
        // A newer question with other answers replaces the one the button was drawn for.
        Assert.True(t.Tasks.Ask(id, "Which branch?", Maybe<string>.None, ["main", "release"]).IsSuccess);
        var comments = t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Count;

        Click(stale);

        Assert.Equal(comments, t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Count);
        Assert.Equal(["main", "release"], Inside(AnswerRow(t)).OfType<Button>().Select(b => AutomationProperties.GetName(b)));
        Assert.True(Inside(AnswerRow(t)).OfType<TextBlock>().Count() > 2);
    });

    // Four answers of forty characters on a narrow panel: all there, wrapped onto rows, none cut.
    [Fact]
    public void Long_answers_wrap_on_a_narrow_panel_and_are_never_cut() => StaThread.Run(() =>
    {
        string[] long40 = [new string('a', 40), new string('b', 40), new string('c', 40), new string('d', 40)];
        var (t, _) = AnswerPage("s1", long40, width: 380);
        using var __ = t;
        t.Window.UpdateLayout();

        var row = AnswerRow(t);
        Assert.Equal(long40, Inside(row).OfType<Button>().Select(b => AutomationProperties.GetName(b)));
        Assert.All(Inside(row).OfType<Button>().Select(b => Inside(b).OfType<TextBlock>().Single()), words =>
        {
            Assert.Equal(TextWrapping.Wrap, words.TextWrapping);
            Assert.Equal(TextTrimming.None, words.TextTrimming);
        });
        Assert.IsType<WrapPanel>(Inside(row).OfType<WrapPanel>().First());
    });
}
