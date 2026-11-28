using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The manager's page in the Inbox (task 6c4c067f; spec docs/superpowers/specs/2026-11-28-manager-inbox-page-design.md,
// TasksWindow.ManagerPage.cs) on the board's own rig. The box's sends are recorded by a fake SendToManager that stores the
// message on the manager record as ManagerActions.Send does; nothing starts a session.
public partial class TasksBoardTests
{
    sealed class ManagerFake(Board? board = null)
    {
        public readonly List<(string Text, Maybe<TaskItem> Task)> Sent = [];
        public int Terminals;
        public Board? Board { get; set; } = board;

        public Result Send(string text, Maybe<TaskItem> task)
        {
            Sent.Add((text, task));
            // As ManagerActions.Send: the message on today's record, which redraws the page through the board's state key.
            if (Board?.Harness.Manager.State.Manager is { } record)
                Board.Harness.Manager.SetManagerSession(ManagerSession.WithMessage(record, new ManagerMessage(Guid.NewGuid(), Start.AddHours(1), text.Trim())));
            return Result.Success();
        }

        public Result Terminal()
        {
            Terminals++;
            return Result.Success();
        }
    }

    static Board BuildWithManager(ManagerFake fake, Func<ChatSource>? chat = null, Func<TaskSession, Maybe<string>>? transcriptOf = null)
    {
        var t = Build(sendToManager: fake.Send, openManagerTerminal: fake.Terminal, managerChat: chat, transcriptOf: transcriptOf, openManager: () => Result.Success());
        fake.Board = t;
        t.Harness.Manager.SetManagerSession(new ManagerRecord("5a1e0c2b-1111-4222-8333-944455556666", DateOnly.FromDateTime(Start.Date)));
        return t;
    }

    static bool PageSays(Board t, string text) =>
        Inside(t.Window.InboxPageContent!).OfType<RichTextBox>()
            .Any(box => new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Contains(text, StringComparison.Ordinal));

    static Button PageButton(Board t, string name) =>
        Inside(t.Window.InboxPageContent!).OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

    [Fact]
    public void ShowManager_opens_the_panel_on_the_Inbox_with_the_manager_page() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        InboxTasks(t);

        t.Controller.ShowManager();

        Assert.True(t.Controller.IsOpen);
        Assert.NotNull(t.Window.InboxShown);
        Assert.True(t.Window.ManagerPageShown);
        Assert.True(t.Controller.ManagerPageInFront);
        Assert.Equal(TasksWindow.ManagerPageTag, ((FrameworkElement)t.Window.InboxPageContent!).Tag);
        Assert.NotNull(t.Window.ManagerBox);
        // No task page is open, so the manager's prompt hook names no current task for a line typed in its terminal.
        Assert.False(t.Controller.OpenInboxPage.HasValue);
    });

    [Fact]
    public void Enter_in_the_box_sends_and_clears_and_the_message_shows() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        t.Controller.ShowManager();

        Assert.True(t.Window.TypeComment(ManagerSession.PageId, "what is stuck?", Key.Enter));

        Assert.Equal("what is stuck?", Assert.Single(fake.Sent).Text);
        Assert.False(fake.Sent[0].Task.HasValue);
        Assert.Equal("", t.Window.ManagerBox!.Text);
        Assert.True(t.Window.ManagerPageShown);
        Assert.True(PageSays(t, "what is stuck?"));
    });

    [Fact]
    public void A_refused_send_keeps_the_text() => StaThread.Run(() =>
    {
        using var t = Build(sendToManager: (_, _) => Result.Failure("Claude sessions are off"), openManager: () => Result.Success());
        t.Controller.ShowManager();

        t.Window.TypeComment(ManagerSession.PageId, "keep me", Key.Enter);

        Assert.Equal("keep me", t.Window.ManagerBox!.Text);
    });

    [Fact]
    public void Esc_in_the_box_does_not_go_back_and_Esc_outside_it_does() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        t.Controller.ShowManager();
        t.Window.EscapeIgnoresThreadWhile = () => true; // the box has the keyboard

        PressKey(t.Window, Key.Escape);
        Assert.True(t.Window.ManagerPageShown);

        t.Window.EscapeIgnoresThreadWhile = () => false;
        PressKey(t.Window, Key.Escape);
        Assert.False(t.Window.ManagerPageShown);
        Assert.NotNull(t.Window.InboxShown);
    });

    [Fact]
    public void Back_returns_to_the_task_page_it_was_opened_from_and_a_send_names_that_task() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        var (asking, _, _, _, _) = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(asking);
        Assert.Equal(asking, t.Window.InboxPageShown);

        t.Controller.ShowManager();
        Assert.True(t.Window.ManagerPageShown);
        t.Window.TypeComment(ManagerSession.PageId, "is this one stuck?", Key.Enter);
        Assert.Equal(asking, fake.Sent.Single().Task.Value.Id);

        Click(PageButton(t, TasksWindow.InboxPageBackText));
        Assert.False(t.Window.ManagerPageShown);
        Assert.Equal(asking, t.Window.InboxPageShown);
    });

    [Fact]
    public void A_task_shown_from_outside_replaces_the_manager_page() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        var (asking, _, _, _, _) = InboxTasks(t);
        t.Controller.ShowManager();

        t.Controller.ShowTask(asking);

        Assert.False(t.Window.ManagerPageShown);
        Assert.Equal(asking, t.Window.InboxPageShown);
    });

    [Fact]
    public void The_menu_opens_the_terminal() => StaThread.Run(() =>
    {
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake);
        t.Controller.ShowManager();

        var menu = t.Window.OpenManagerMenu(PageButton(t, "More"));
        var item = menu.Items.OfType<MenuItem>().Single();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;

        Assert.Equal(1, fake.Terminals);
    });

    [Fact]
    public void The_page_reads_the_managers_transcript_and_keeps_its_stream_through_a_redraw() => StaThread.Run(() =>
    {
        var transcript = new FakeTranscript();
        transcript.Add(ClaudeSaid("Nothing collides today.", 5));
        var fake = new ManagerFake();
        using var t = BuildWithManager(fake, () => new ChatSource(ChatSourceKind.Live, "C:\\made\\up\\manager.jsonl", "5a1e0c2b"), TranscriptPath);
        t.Window.TranscriptInline = true;
        t.Window.TranscriptTimer = t.Timers.Start;
        t.Window.TranscriptReader = () => transcript.Read;

        t.Controller.ShowManager();

        Assert.Equal(ManagerSession.PageId, t.Window.TranscriptPoll!.Task);
        Assert.Equal("C:\\made\\up\\manager.jsonl", Assert.Single(transcript.Paths));
        var stream = t.Window.StreamShown;
        Assert.NotNull(stream);
        Assert.True(PageSays(t, "Nothing collides today."));

        t.Window.TypeComment(ManagerSession.PageId, "thanks", Key.Enter);

        Assert.Same(stream, t.Window.StreamShown);
        Assert.True(PageSays(t, "thanks"));
    });
}
