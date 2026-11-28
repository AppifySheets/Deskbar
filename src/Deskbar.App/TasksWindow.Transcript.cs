using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// CLAUDE'S MESSAGES AND STEPS ON A TASK'S INBOX PAGE (the window's side). Plan: notes/tasks-inbox-steps-plan.md.
//
// The page's thread gets one more kind of entry among its comments: what the task's session said and did, read from its
// transcript (ClaudeChat) and drawn as bubbles and folded blocks of steps (ChatStreamPanel). Four small hooks tie this to
// the rest of the window, each one line: Thread draws its comments through ChatStreamFor; DrawInboxPage calls FollowTranscript
// before it builds the page and StopTranscript when the page is gone (and LeaveInboxPage when the view leaves); CloseForGood
// stops it too.
//
// A TASK WITH NO SESSION, OR A WINDOW BUILT WITHOUT `TranscriptOf` (the tests, an old copy), DRAWS ITS THREAD EXACTLY AS BEFORE:
// ChatStreamFor then returns today's ChatThread.WithDays list. Only a task that has a session to read gets the stream, and
// then the comments are inside it, ordered by time with the session's items.
//
// THE STREAM SURVIVES THE PAGE. The page is rebuilt whenever the board changes, and the transcript's items are not rebuilt
// with it: the same ChatStreamPanel is moved into the new page (SetComments swaps in the freshly drawn comments), so the
// open step blocks, a half-selected message and the layout of sixty bubbles are kept and a rebuild costs a few milliseconds
// instead of drawing every bubble again (measured, see ChatStreamPanelTests).
public partial class TasksWindow
{
    InboxTranscript? transcript;
    ChatStreamPanel? stream;
    Guid? streamTask;
    readonly ChatStreamState streamState = new();

    /// <summary>For the tests: a timer in place of the poll's own (a one-shot, as FakeTimers.Start).</summary>
    internal Func<TimeSpan, Action, IDisposable>? TranscriptTimer { get; set; }

    /// <summary>For the tests: the reader a poll makes, in place of ClaudeChatReader's.</summary>
    internal Func<Func<string, ChatUpdate>>? TranscriptReader { get; set; }

    /// <summary>For the tests: reads run at once on the calling thread instead of on the thread pool.</summary>
    internal bool TranscriptInline { get; set; }

    /// <summary>For the tests: the poll, null until a page with a session has been drawn.</summary>
    internal InboxTranscript? TranscriptPoll => transcript;

    /// <summary>For the tests: the stream the page draws, null when the page has none.</summary>
    internal ChatStreamPanel? StreamShown => stream;

    // A page of `task` is about to be drawn: its transcript is followed (the same task again changes nothing).
    void FollowTranscript(Guid task)
    {
        if (Actions?.TranscriptOf is not { } sourceOf) return;
        if (transcript is null)
        {
            transcript = new InboxTranscript(sourceOf, TranscriptReader ?? (() => new ClaudeChatReader().Read), RunTranscriptRead,
                TranscriptTimer ?? DispatcherOnce, ApplyTranscript);
            // A hidden panel reads nothing; shown again with its page open it reads at once.
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true) transcript?.Resume();
                else transcript?.Pause();
            };
        }
        transcript.Follow(task);
    }

    // The page is gone, the view left the Inbox or the window closes: nothing is read, nothing is kept.
    void StopTranscript()
    {
        transcript?.Stop();
        stream = null;
        streamTask = null;
    }

    // The read runs on the thread pool; its result comes back through the dispatcher at Background priority, so a busy
    // window is never held up by it. A failed read is the same as a quiet one.
    void RunTranscriptRead(Func<ChatUpdate?> read, Action<ChatUpdate?> done)
    {
        if (TranscriptInline)
        {
            done(read());
            return;
        }
        var dispatcher = Dispatcher;
        System.Threading.Tasks.Task.Run(read).ContinueWith(
            finished => dispatcher.BeginInvoke(new Action(() => done(finished.IsCompletedSuccessfully ? finished.Result : null)), DispatcherPriority.Background),
            System.Threading.Tasks.TaskScheduler.Default);
    }

    static IDisposable DispatcherOnce(TimeSpan after, Action then)
    {
        var timer = new DispatcherTimer { Interval = after };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            then();
        };
        timer.Start();
        return System.Reactive.Disposables.Disposable.Create(timer.Stop);
    }

    // The poll has something new for the page: the stream takes it in place. A page that sits at its end stays at its end
    // (a chat follows its newest), one scrolled up keeps its place, since the new entries are added below the view.
    void ApplyTranscript()
    {
        // THE MANAGER'S PAGE DRAWS AGAIN when its transcript appears, changes or the manager starts or stops (task 6c4c067f,
        // review): a page drawn before today's manager existed has no stream to take the items, or still holds the stream
        // of the task page it was opened over. Its signature carries the poll's source (ManagerPageSignature), so the
        // redraw builds the stream and the state words anew; the items then come in here.
        // Posted, not run here: the poll's first tick runs inside the draw that follows it (FollowTranscript), and a draw
        // inside a draw would double the page. Checked again when it runs, since that draw may have built the stream.
        if (ManagerStreamMissing())
        {
            Dispatcher.BeginInvoke(new Action(() => { if (ManagerStreamMissing()) RedrawInbox(); }), DispatcherPriority.Background);
            return;
        }
        if (stream is null || transcript is null) return;
        var scroller = pageScroller;
        // What the page is held at wins over where the scroller is now: a stream that was empty has pulled its offset to the top.
        var pinned = pageHold is { } held ? held.AtEnd : scroller is null || AtPageEnd(scroller.VerticalOffset, scroller.ScrollableHeight);
        stream.Apply(transcript.Latest?.Items ?? [], NoteOf(transcript), transcript.Source.Kind == ChatSourceKind.Live);
        if (scroller is not null && pinned)
        {
            scroller.UpdateLayout();
            scroller.ScrollToEnd();
        }
    }

    // The manager's page follows a transcript it has no stream of its own for (task 6c4c067f): none built yet, or the stream
    // still the task page's it was opened over. A source with nothing to read (None) needs none.
    bool ManagerStreamMissing() =>
        managerPage && transcript is { Task: { } followed } poll && followed == ManagerSession.PageId
        && poll.Source.Kind != ChatSourceKind.None && (stream is null || streamTask != followed);

    // The dim line at the stream's end: a session that has ended, or one whose record is gone.
    static string? NoteOf(InboxTranscript poll) => poll.Source.Kind switch
    {
        ChatSourceKind.Expired => "This session's record has expired, so its steps are not shown.",
        ChatSourceKind.Ended when poll.Latest is { Items.Count: > 0 } => "Session ended",
        _ => null,
    };

    // The thread's comments for the page: today's chat of comments, or, for a task with a session to read, the stream that
    // holds them and the session's items together. `earlierFolded`: the thread shows only its latest comments.
    IReadOnlyList<UIElement> ChatStreamFor(TaskRowView row, IReadOnlyList<TaskCommentView> shown, bool earlierFolded)
    {
        if (inboxPage != row.Id || transcript is not { Task: { } followed } poll || followed != row.Id || poll.Source.Kind == ChatSourceKind.None)
            return ChatThread.WithDays(shown, comment => CommentOf(comment, row.Id));
        var comments = GroupedInStream(shown, poll.Latest?.Items ?? [], earlierFolded)
            .Select(c => ("comment:" + c.Id, c.At, CommentOf(c, row.Id))).ToList();
        if (stream is null || streamTask != row.Id)
        {
            stream = new ChatStreamPanel(new ChatStreamDeps((ControlTemplate)FindResource("BareRichTextBox"), LinkOpener(row.Id),
                (content, name, press) =>
                {
                    var button = Flat(content, name, press, new Thickness(0), ClickIgnored);
                    button.Background = Brushes.Transparent;
                    button.Focusable = true;
                    return button;
                }, Now, streamState), comments, earlierFolded);
            streamTask = row.Id;
        }
        else
        {
            // The page is rebuilt: the same stream moves into it.
            if (stream.Parent is Panel old) old.Children.Remove(stream);
            stream.SetComments(comments, earlierFolded);
        }
        stream.Apply(poll.Latest?.Items ?? [], NoteOf(poll), poll.Source.Kind == ChatSourceKind.Live);
        return [stream];
    }

    // A RUN OF ONE AUTHOR'S BUBBLES NAMES THE AUTHOR ONCE (task 85218923, ChatThread.Grouped), in the stream as in the plain
    // thread. In the stream two comments are one run only when no message or marker of the session's sits between them (a
    // run of steps may: task ac1ebd57), so the comments
    // are grouped stretch by stretch, in the order the stream shows them (ClaudeChatStream.Build, as Apply places them). A
    // comment and a session message never share a run; the session's messages are grouped by the stream itself
    // (ChatStreamPanel.Signs). Decided when the page is drawn: a session item that a later poll slips between two
    // comments leaves the upper one without its name until the page is drawn again.
    internal static IReadOnlyList<TaskCommentView> GroupedInStream(IReadOnlyList<TaskCommentView> shown, IReadOnlyList<ChatItem> items,
        bool earlierFolded)
    {
        var byKey = shown.GroupBy(c => "comment:" + c.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var entries = ClaudeChatStream.Build(items, byKey.Select(p => new StreamComment(p.Key, p.Value.At)).ToList(), earlierFolded).Entries;
        var grouped = new Dictionary<Guid, TaskCommentView>();
        var stretch = new List<TaskCommentView>();
        void Close()
        {
            foreach (var comment in ChatThread.Grouped(stretch)) grouped[comment.Id] = comment;
            stretch.Clear();
        }
        // A run of steps does not end a stretch (task ac1ebd57, as ChatStreamPanel.Signs): folded it is one dim line,
        // and two comments of Petre's with only Claude's steps between them read as one run.
        foreach (var entry in entries)
        {
            if (entry.IsComment && byKey.TryGetValue(entry.Key, out var comment)) stretch.Add(comment);
            else if (entry.Item is not ChatSteps) Close();
        }
        Close();
        return shown.Select(c => grouped.GetValueOrDefault(c.Id, c)).ToList();
    }
}
