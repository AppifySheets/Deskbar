using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// Claude's messages and steps on a task's Inbox page (TasksWindow.Transcript.cs, InboxTranscript, ChatStreamPanel) on the
// board's own rig. The transcript is a fake: a list of made-up items the poll "reads", and the reader records which thread
// it ran on and which path it was given. Nothing here touches a real transcript or starts a session.
public partial class TasksBoardTests
{
    sealed class FakeTranscript
    {
        public readonly List<ChatItem> Items = [];
        public readonly List<string> Paths = [];
        public readonly List<int> Threads = [];
        long position = 1;
        bool changed = true;

        public Func<string, ChatUpdate> Read => path =>
        {
            lock (Paths) { Paths.Add(path); Threads.Add(Environment.CurrentManagedThreadId); }
            var update = new ChatUpdate(Items.ToList(), changed, position);
            changed = false;
            return update;
        };

        public void Add(ChatItem item)
        {
            Items.Add(item);
            changed = true;
            position++;
        }
    }

    static Maybe<string> TranscriptPath(TaskSession session) => "C:\\made\\up\\" + session.Id + ".jsonl";

    // A board whose tasks have transcripts (the fake), its poll on the test's timers and its reads at once on this thread.
    static Board BuildWithTranscript(FakeTranscript fake, bool inline = true, Func<TaskSession, Maybe<string>>? transcriptOf = null,
        Func<TaskItem, IReadOnlyList<SessionView>>? sessionsOf = null)
    {
        var t = Build(width: 460, transcriptOf: transcriptOf ?? TranscriptPath, sessionsOf: sessionsOf);
        t.Window.TranscriptInline = inline;
        t.Window.TranscriptTimer = t.Timers.Start;
        t.Window.TranscriptReader = () => fake.Read;
        return t;
    }

    static Maybe<DateTimeOffset> Moment(int minutes) => Start.AddMinutes(minutes);

    static ChatMessage ClaudeSaid(string text, int minutes, string? id = null) => new(id ?? "m" + minutes, ChatRole.Claude, text, 0, false, Moment(minutes));

    static ChatSteps StepsRun(string id, int minutes, params ChatStep[] steps) => new(id, steps, Moment(minutes));

    static ChatStep ReadStep(string id, string file, bool done = true) => new(id, StepKind.Read, file, false, done, Moment(0));

    static Guid SessionTask(Board t, string title = "Review the diff", TaskSession? session = null) =>
        t.Tasks.Create(title, t.Gepha, session ?? Session("s1")).Value.Id;

    static void PumpUntil(TasksWindow window, Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < end)
        {
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void A_task_with_no_session_draws_its_thread_as_before_and_reads_nothing() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake);
        var note = t.Tasks.AddNote("Book the dentist", Maybe<Guid>.None).Value.Id;
        t.Tasks.Comment(note, Maybe<string>.None, "A word");
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(note);

        Assert.NotNull(t.Window.InboxPageContent);
        Assert.Null(t.Window.StreamShown);
        Assert.Equal(["you"], t.Window.CommentAuthors(note));
        Assert.Empty(fake.Paths);
        Assert.Equal(0, t.Window.TranscriptPoll!.ReadersMade);
    });

    [Fact]
    public void A_window_without_transcripts_draws_every_page_as_before() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = SessionTask(t);
        t.Tasks.Comment(id, Maybe<string>.None, "A word");
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(id);

        Assert.Null(t.Window.StreamShown);
        Assert.Null(t.Window.TranscriptPoll);
        Assert.Equal(["you"], t.Window.CommentAuthors(id));
    });

    [Fact]
    public void No_reader_exists_while_no_page_is_open() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake);
        SessionTask(t);
        t.Controller.Show();

        ChooseInbox(t);

        Assert.Null(t.Window.TranscriptPoll);
        Assert.Empty(fake.Paths);
    });

    [Fact]
    public void A_page_of_a_live_session_shows_its_messages_and_steps_among_the_comments() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        fake.Add(ClaudeSaid("I will look at the **diff**.", 1));
        fake.Add(StepsRun("r1", 2, ReadStep("a", "Dock.cs"), ReadStep("b", "Bar.cs")));
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Tasks.Comment(id, Maybe<string>.None, "A word from the owner");
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(id);

        var stream = t.Window.StreamShown!;
        Assert.NotNull(stream);
        Assert.Contains("m1", stream.Keys);
        Assert.Contains("r1", stream.Keys);
        Assert.Single(stream.Keys, key => key.StartsWith("comment:"));
        Assert.Equal(["C:\\made\\up\\s1.jsonl"], fake.Paths);
        Assert.True(HasButtonNamed(t.Window.InboxPageContent!, "2 steps"));
        Assert.Equal(["you"], t.Window.CommentAuthors(id));
    });

    // The one session of a task with two: the one that runs, not a newer one that ended.
    [Fact]
    public void A_task_with_two_sessions_reads_the_running_one_only() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake, sessionsOf: task => task.Sessions
            .Select(s => new SessionView(s.Id, s.Joined, s.Id == "old-live" ? SessionState.Idle : SessionState.Ended)).ToArray());
        var id = t.Tasks.Create("Two sessions", t.Gepha, new TaskSession("old-live", @"C:\repos\TaskSpaces", Start, Maybe<DateTimeOffset>.None)).Value.Id;
        t.Tasks.Join(id, new TaskSession("new-ended", @"C:\repos\TaskSpaces", Start.AddHours(1), Start.AddHours(2)));
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(id);

        Assert.Equal(["C:\\made\\up\\old-live.jsonl"], fake.Paths);
    });

    [Fact]
    public void A_poll_adds_a_step_without_rebuilding_the_page_and_the_open_block_stays() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        fake.Add(StepsRun("r1", 2, ReadStep("a", "Dock.cs", done: false)));
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        var page = t.Window.InboxPageContent;
        var stream = t.Window.StreamShown!;
        Click(ButtonNamed(page!, "1 step"));
        var rebuilds = t.Window.Rebuilds;
        var created = stream.Created;

        fake.Items[0] = StepsRun("r1", 2, ReadStep("a", "Dock.cs"), ReadStep("b", "Bar.cs"));
        fake.Add(ClaudeSaid("Done reading.", 3));
        t.Timers.FireAll();

        Assert.Equal(rebuilds, t.Window.Rebuilds);
        Assert.Same(page, t.Window.InboxPageContent);
        Assert.Same(stream, t.Window.StreamShown);
        Assert.Equal(created + 1, stream.Created);
        Assert.Equal(["r1", "m3"], stream.Keys.Where(k => !k.StartsWith("comment:")));
        Assert.Equal(2, Inside(page!).OfType<FrameworkElement>().Count(e => Equals(e.Tag, ChatStepsView.RowTag)));
        Assert.Equal(Visibility.Visible, ((StackPanel)Inside(page!).OfType<FrameworkElement>().First(e => Equals(e.Tag, ChatStepsView.RowTag)).Parent).Visibility);
    });

    // A new comment redraws the whole page; the stream (open blocks and all) moves into the new one.
    [Fact]
    public void A_new_comment_rebuilds_the_page_and_the_stream_moves_with_it() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        fake.Add(StepsRun("r1", 2, ReadStep("a", "Dock.cs")));
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        var stream = t.Window.StreamShown!;
        Click(ButtonNamed(t.Window.InboxPageContent!, "1 step"));
        var rebuilds = t.Window.Rebuilds;
        var oldPage = t.Window.InboxPageContent;

        Assert.True(t.Window.Actions!.Comment(id, "a second word"));

        Assert.True(t.Window.Rebuilds > rebuilds);
        Assert.NotSame(oldPage, t.Window.InboxPageContent);
        Assert.Same(stream, t.Window.StreamShown);
        Assert.Contains(stream.Keys, key => key.StartsWith("comment:"));
        Assert.Contains(stream, Inside(t.Window.InboxPageContent!));
        var row = Inside(t.Window.InboxPageContent!).OfType<FrameworkElement>().First(e => Equals(e.Tag, ChatStepsView.RowTag));
        Assert.Equal(Visibility.Visible, ((StackPanel)row.Parent).Visibility);
    });

    [Fact]
    public void A_draft_and_the_comment_box_survive_a_poll() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        var box = t.Window.CommentBoxOf(id)!;
        box.Text = "half a thought";
        box.CaretIndex = 4;

        fake.Add(ClaudeSaid("A new line from Claude.", 5));
        t.Timers.FireAll();

        Assert.Same(box, t.Window.CommentBoxOf(id));
        Assert.Equal("half a thought", box.Text);
        Assert.Equal(4, box.CaretIndex);
        Assert.Contains("m5", t.Window.StreamShown!.Keys);
    });

    [Fact]
    public void A_page_at_its_end_stays_at_its_end_and_a_page_scrolled_up_keeps_its_place() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Tasks.Describe(id, string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"Step {i} of a long brief, long enough to take a line of its own.")));
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.UpdateLayout();
        t.Controller.ShowTask(id);
        t.Window.UpdateLayout();
        var (offset, extent) = t.Window.InboxPageScroll;
        Assert.True(extent > 0);
        Assert.Equal(extent, offset, 1);

        for (var i = 0; i < 3; i++) fake.Add(ClaudeSaid($"Line number {i} that Claude wrote.", 10 + i));
        t.Timers.FireAll();
        t.Window.UpdateLayout();
        var (offsetAfter, extentAfter) = t.Window.InboxPageScroll;
        Assert.True(extentAfter > extent);
        Assert.Equal(extentAfter, offsetAfter, 1);

        t.Window.InboxPageScroller!.ScrollToVerticalOffset(40);
        t.Window.UpdateLayout();
        fake.Add(ClaudeSaid("Another line.", 20));
        t.Timers.FireAll();
        t.Window.UpdateLayout();
        Assert.Equal(40, t.Window.InboxPageScroll.Offset, 1);
    });

    // ---- stopping --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_poll_stops_when_the_page_closes() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        fake.Add(ClaudeSaid("hello", 1));
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        Assert.True(t.Window.TranscriptPoll!.Alive);
        var reads = fake.Paths.Count;

        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.InboxPageBackText));
        t.Timers.FireAll();

        Assert.False(t.Window.TranscriptPoll!.Alive);
        Assert.Null(t.Window.TranscriptPoll.Task);
        Assert.Null(t.Window.TranscriptPoll.Latest);
        Assert.Null(t.Window.StreamShown);
        Assert.Equal(reads, fake.Paths.Count);
    });

    [Fact]
    public void The_poll_stops_when_the_view_leaves_the_inbox() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        Assert.True(t.Window.TranscriptPoll!.Alive);

        t.Window.Actions!.ShowMatrix(false);
        t.Timers.FireAll();

        Assert.False(t.Window.TranscriptPoll!.Alive);
        Assert.Null(t.Window.StreamShown);
    });

    [Fact]
    public void A_page_of_another_task_follows_that_tasks_session() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake);
        var first = SessionTask(t, "First", Session("s1"));
        var second = SessionTask(t, "Second", Session("s2"));
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(first);
        t.Controller.ShowTask(second);

        Assert.Equal(["C:\\made\\up\\s1.jsonl", "C:\\made\\up\\s2.jsonl"], fake.Paths);
        Assert.Equal(second, t.Window.TranscriptPoll!.Task);
    });

    // ---- the session's state ---------------------------------------------------------------------------------------

    [Fact]
    public void An_ended_session_is_read_once_and_says_so() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        fake.Add(ClaudeSaid("That is all.", 1));
        using var t = BuildWithTranscript(fake, sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Ended)).ToArray());
        var id = SessionTask(t, session: Session("s1", live: false));
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(id);
        t.Timers.FireAll();
        t.Timers.FireAll();

        Assert.Single(fake.Paths);
        Assert.Contains("Session ended", Inside(t.Window.StreamShown!).OfType<TextBlock>().Select(x => x.Text));
    });

    [Fact]
    public void An_expired_session_says_so_and_reads_nothing() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake, transcriptOf: _ => Maybe<string>.None,
            sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Expired)).ToArray());
        var id = SessionTask(t, session: Session("s1", live: false));
        t.Tasks.Comment(id, Maybe<string>.None, "A word");
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(id);

        Assert.Empty(fake.Paths);
        Assert.Equal(ChatSourceKind.Expired, t.Window.TranscriptPoll!.Source.Kind);
        Assert.Contains("This session's record has expired, so its steps are not shown.", Inside(t.Window.StreamShown!).OfType<TextBlock>().Select(x => x.Text));
        Assert.Contains(t.Window.StreamShown!.Keys, key => key.StartsWith("comment:"));
    });

    // ---- the thread ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_read_runs_off_the_ui_thread_and_its_result_is_drawn_on_it() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        fake.Add(ClaudeSaid("Drawn from a worker's read.", 1));
        using var t = BuildWithTranscript(fake, inline: false);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(id);
        PumpUntil(t.Window, () => t.Window.StreamShown!.Keys.Contains("m1"));

        Assert.Contains("m1", t.Window.StreamShown!.Keys);
        Assert.All(fake.Threads, thread => Assert.NotEqual(Environment.CurrentManagedThreadId, thread));
        Assert.NotEmpty(fake.Threads);
    });

    [Fact]
    public void A_hidden_panel_reads_nothing_and_a_shown_one_reads_at_once() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        Assert.True(t.Window.IsVisible);
        var reads = fake.Paths.Count;

        t.Window.Hide();
        t.Timers.FireAll();
        Assert.False(t.Window.TranscriptPoll!.Alive);
        Assert.Equal(reads, fake.Paths.Count);

        t.Window.Show();
        Assert.True(t.Window.TranscriptPoll!.Alive);
        Assert.Equal(reads + 1, fake.Paths.Count);
    });}
