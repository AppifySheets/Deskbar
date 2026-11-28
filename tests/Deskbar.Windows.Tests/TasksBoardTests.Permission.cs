using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The permission card on a task's Inbox page and the line's mark (Allow / Deny from a task's page). The desk is a real
// PermissionDesk over a scratch folder, so a click writes a real answer file, read back here. Hard properties held: the
// card shows the whole request (nothing cut, folded by whole lines, marks for hidden characters); only a click on a
// button answers, never a key; a click right after the card appears is ignored.
public partial class TasksBoardTests
{
    sealed class PermissionRig : IDisposable
    {
        public required Board Board { get; init; }
        public required PermissionDesk Desk { get; init; }
        public required string Box { get; init; }
        public required Guid Task { get; init; }

        public void Dispose()
        {
            Board.Dispose();
            if (Directory.Exists(Box)) Directory.Delete(Box, true);
        }
    }

    static int permissionSlots;

    // A Claude task whose session "s1" asks for `command` (by `tool`), the Inbox on and its page open.
    static PermissionRig PermissionPage(string command = "git push fork feature/x", string tool = "Bash", Func<DateTime>? clickClock = null,
        IReadOnlyList<PermissionSuggestion>? suggestions = null)
    {
        var t = Build(clickClock: clickClock);
        var box = Path.Combine(Path.GetTempPath(), "deskbar-perm-card-" + Guid.NewGuid().ToString("N"));
        var desk = new PermissionDesk(box, () => Start.AddHours(1));
        t.Controller.Permissions = desk;
        var id = t.Tasks.Create("Ship the fix", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        ChooseInbox(t);
        var rig = new PermissionRig { Board = t, Desk = desk, Box = box, Task = id };
        Ask(rig, command, tool, suggestions);
        Click(LineOf(t.Window, id));
        return rig;
    }

    static string Ask(PermissionRig rig, string command, string tool = "Bash", IReadOnlyList<PermissionSuggestion>? suggestions = null)
    {
        var slot = $"req-{Interlocked.Increment(ref permissionSlots)}-1";
        Directory.CreateDirectory(Path.Combine(rig.Box, slot));
        var field = tool == "Bash" ? "command" : "file_path";
        Assert.True(rig.Desk.Register(slot, new PermissionRequest("s1", tool, @"C:\repos\TaskSpaces", "default",
            [new PermissionField(field, command), new PermissionField("description", "Push the branch")])
            { Suggestions = suggestions ?? [] }).IsSuccess);
        return slot;
    }

    static IReadOnlyList<Border> Cards(PermissionRig rig) =>
        rig.Board.Window.InboxPageContent is { } page
            ? Inside(page).OfType<Border>().Where(b => b.Tag as string == TasksWindow.PermissionCardTag).ToList()
            : [];

    static string? AnswerIn(PermissionRig rig, string slot) =>
        File.Exists(Path.Combine(rig.Box, slot, PermissionDesk.AnswerFile)) ? File.ReadAllText(Path.Combine(rig.Box, slot, PermissionDesk.AnswerFile)) : null;

    [Fact]
    public void A_question_shows_as_a_card_with_the_whole_command_and_the_line_wears_the_shield() => StaThread.Run(() =>
    {
        var command = "git push fork feature/x && rm -rf " + new string('a', 400) + " \u202E";
        using var r = PermissionPage(command);

        var card = Assert.Single(Cards(r));
        var shown = Inside(card).OfType<TextBlock>().Single(b => b.Tag as string == TasksWindow.PermissionPartTag);
        Assert.Equal(PermissionDisplay.Visible(command), shown.Text);
        Assert.EndsWith("⟨U+202E⟩", shown.Text);
        Assert.Equal(TextWrapping.Wrap, shown.TextWrapping);
        Assert.Equal(TextTrimming.None, shown.TextTrimming);
        Assert.Contains(Inside(card).OfType<TextBlock>(), b => b.Text == "Claude says: Push the branch");
        Assert.True(HasButtonNamed(card, TasksWindow.AllowText));
        Assert.True(HasButtonNamed(card, TasksWindow.DenyText));
        Assert.True(HasButtonNamed(card, TasksWindow.AnswerInTerminalText));
        // A shell command in default mode: the terminal's "switch to auto mode" too (task bd55bd54).
        Assert.True(HasButtonNamed(card, TasksWindow.AutoText));
        Assert.Equal(InboxMark.AsksPermission, r.Board.Window.InboxShown!.Lines.Single(l => l.Id == r.Task).Mark);
        Assert.Equal(InboxSection.Permissions, r.Board.Window.InboxShown!.Lines.Single(l => l.Id == r.Task).Section);
    });

    // An edit gets the terminal's Yes / No (measured 2026-11-28, task bd55bd54), then the card's way back to the terminal.
    [Fact]
    public void An_edit_offers_yes_and_no_as_the_terminal_does() => StaThread.Run(() =>
    {
        using var r = PermissionPage(@"C:\repos\TaskSpaces\src\App.cs", tool: "Edit");

        var names = Inside(Cards(r).Single()).OfType<Button>().Select(b => System.Windows.Automation.AutomationProperties.GetName(b))
            .Where(n => n is TasksWindow.AllowText or TasksWindow.AutoText or TasksWindow.DenyText or TasksWindow.AnswerInTerminalText);
        Assert.Equal([TasksWindow.AllowText, TasksWindow.DenyText, TasksWindow.AnswerInTerminalText], names);
    });

    // A suggestion's "Yes, and ..." writes the fetch word, and its line hands Claude Code's entry back.
    [Fact]
    public void A_suggestion_button_writes_fetch_and_the_line_waits_for_the_script() => StaThread.Run(() =>
    {
        var rule = PermissionSuggestion.Of(System.Text.Json.JsonDocument.Parse(
            """{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"git init *"}],"behavior":"allow","destination":"localSettings"}""").RootElement).Value;
        using var r = PermissionPage("git init", suggestions: [rule]);
        var slot = r.Desk.All.Single().Slot;

        Click(ButtonNamed(Cards(r).Single(), "Yes, and don't ask again for: git init *"));

        Assert.EndsWith($" {PermissionAnswer.FetchWord}\n", AnswerIn(r, slot));
        Assert.Empty(r.Desk.All);
        var code = AnswerIn(r, slot)!.Split(' ')[0];
        Assert.Equal(PermissionAnswer.SuggestionLine(rule), r.Desk.Fetch(slot, code).Value);
    });

    [Theory]
    [InlineData(TasksWindow.AllowText, "allow")]
    [InlineData(TasksWindow.AutoText, "auto")]
    [InlineData(TasksWindow.DenyText, "deny")]
    [InlineData(TasksWindow.AnswerInTerminalText, "pass")]
    public void A_click_writes_its_answer_and_the_card_goes(string button, string word) => StaThread.Run(() =>
    {
        using var r = PermissionPage();
        var slot = r.Desk.All.Single().Slot;

        Click(ButtonNamed(Cards(r).Single(), button));

        Assert.EndsWith($" {word}\n", AnswerIn(r, slot));
        Assert.Empty(r.Desk.All);
        Assert.Empty(Cards(r));
    });

    // A click in the first moments after a card appears may have been meant for what was under the pointer: ignored.
    [Fact]
    public void A_click_right_after_the_card_appears_is_ignored() => StaThread.Run(() =>
    {
        // The click clock steps a second a read while the page opens, then stands still for the new card.
        var now = ClickStart;
        var stepping = true;
        using var r = PermissionPage(clickClock: () => stepping ? now = now.AddSeconds(1) : now);
        stepping = false;
        // The session asks again: its newer question takes the first card's place (one card per session).
        var slot = Ask(r, "a new question");

        now += TimeSpan.FromMilliseconds(200);
        Click(ButtonNamed(Cards(r).Single(), TasksWindow.AllowText));
        Assert.Null(AnswerIn(r, slot));

        now += TasksWindow.ArmTime;
        Click(ButtonNamed(Cards(r).Single(), TasksWindow.AllowText));
        Assert.EndsWith(" allow\n", AnswerIn(r, slot));
    });

    // No single key answers: every key the window takes writes nothing, and Space, the other key a button takes, is
    // swallowed by the card's buttons. Enter on a focused button is the keyboard's one way (WPF's own Button behaviour).
    [Fact]
    public void No_key_answers_a_question() => StaThread.Run(() =>
    {
        using var r = PermissionPage();
        var slot = r.Desk.All.Single().Slot;

        foreach (var key in new[] { Key.Enter, Key.Space, Key.A, Key.Y, Key.Return, Key.F2, Key.Tab })
            r.Board.Window.Press(key, ModifierKeys.None);
        Assert.Null(AnswerIn(r, slot));

        var allow = ButtonNamed(Cards(r).Single(), TasksWindow.AllowText);
        Assert.True(allow.Focusable);
        using var source = new HwndSource(new HwndSourceParameters("perm-key-test"));
        foreach (var routed in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.PreviewKeyUpEvent })
        {
            var space = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = routed };
            allow.RaiseEvent(space);
            Assert.True(space.Handled);
        }
        Assert.Null(AnswerIn(r, slot));
    });

    // A long command folds by whole lines behind "Show all", and shows every line once unfolded.
    [Fact]
    public void A_long_command_folds_by_whole_lines() => StaThread.Run(() =>
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"step {i} && echo {i}").ToArray();
        using var r = PermissionPage(string.Join('\n', lines));

        TextBlock Shown() => Inside(Cards(r).Single()).OfType<TextBlock>().First(b => b.Tag as string == TasksWindow.PermissionPartTag);
        Assert.Equal(string.Join('\n', lines.Take(PermissionDisplay.FoldLines)), Shown().Text);
        var more = Inside(Cards(r).Single()).OfType<Button>().Single(b =>
            System.Windows.Automation.AutomationProperties.GetName(b).StartsWith(TasksWindow.ShowAllPartPrefix, StringComparison.Ordinal));
        Assert.Contains($"{30 - PermissionDisplay.FoldLines} more lines", System.Windows.Automation.AutomationProperties.GetName(more));

        Click(more);

        Assert.Equal(string.Join('\n', lines), Shown().Text);
    });

    // ONE CARD PER SESSION (the review's L5): the session asks again (the first question was answered in the terminal), so
    // the newer question takes the old card's place; a click on the old card, still drawn, writes nothing and says to
    // answer in the terminal, and the new card answers the new question.
    [Fact]
    public void A_newer_question_replaces_the_card_and_the_old_cards_click_writes_nothing() => StaThread.Run(() =>
    {
        using var r = PermissionPage("first");
        var first = r.Desk.All.Single().Slot;
        var allowFirst = ButtonNamed(Cards(r).Single(), TasksWindow.AllowText);
        var second = Ask(r, "second");

        Assert.Single(Cards(r));
        Click(allowFirst);

        Assert.Null(AnswerIn(r, first));
        Assert.False(Directory.Exists(Path.Combine(r.Box, first)));
        Assert.Contains(Inside(r.Board.Window.InboxPageContent!).OfType<TextBlock>(), b => b.Text.Contains("Answer it in the terminal"));

        Click(ButtonNamed(Cards(r).Single(), TasksWindow.DenyText));
        Assert.EndsWith(" deny\n", AnswerIn(r, second));
    });}
