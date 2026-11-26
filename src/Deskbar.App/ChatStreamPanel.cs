using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

/// <summary>
/// What the stream remembers outside its elements, so a page that is rebuilt (a new comment redraws the whole page) draws it
/// again as it was: which runs of steps the owner opened, which long messages he opened. Keyed by the item's Id, which stays
/// the same for as long as the item exists (ClaudeChat). Lives as long as the window; holds Ids only, no text.
/// </summary>
internal sealed class ChatStreamState
{
    public HashSet<string> OpenRuns { get; } = new(StringComparer.Ordinal);
    public HashSet<string> OpenMessages { get; } = new(StringComparer.Ordinal);
}

/// <summary>What the stream needs from the window: its look-up of a link, its flat button and its clock.</summary>
internal sealed record ChatStreamDeps(ControlTemplate Bare, Action<Uri>? OpenLink, Func<UIElement, string, Action, Button> Button,
    Func<DateTimeOffset> Now, ChatStreamState State);

// THE PAGE'S CHAT: the thread's comments and the session's messages and steps, in one column, newest at the bottom, in the
// Inbox's colours (ChatThread under InboxLook). Plan: notes/tasks-inbox-steps-plan.md, sections 3 and 4.
//
// IT IS UPDATED IN PLACE, NEVER REBUILT, by Apply. The page is rebuilt whenever the board changes (TasksWindow.Draw: a new
// comment, a status line), and that is rare; the session's transcript changes every few seconds, and rebuilding the page
// for it would lose the scroll place, the open blocks, a draft in the comment box and the keyboard. So a poll comes here:
// each item is matched by its Id with the element drawn for it. A new item gets an element, inserted at its place by time
// (between two comments if that is where it falls); a run of steps whose steps changed updates its own header and rows
// (ChatStepsView.Update); an item that did not change is not touched; an item that left the window (older than the last
// MaxDrawn entries) loses its element. Elements that stay are never moved or re-created, so the keyboard on a step block's
// header and a half-selected message stay where they are. Only a message whose text grew (Claude's text in two lines of one
// API message) is re-created, in the same place.
//
// THE COMMENTS ARE GIVEN, NOT DRAWN HERE: the window draws each comment with its own CommentOf (Edit, Settle, pictures,
// Show more by its own rules) and hands the finished elements over with their moments; this only places them among the
// session's items. A comment is never re-created by a poll.
//
// COST: at most MaxDrawn elements live at once. The bubbles of Claude's messages are markdown boxes (the same
// MarkdownBlocks the comments use) and are the expensive part; a test measures drawing 60 of them (the report states it).
internal sealed class ChatStreamPanel : StackPanel
{
    /// <summary>The Tag of the stream, for the tests.</summary>
    internal const string StreamTag = "chat-stream";

    /// <summary>The Tag of the dim line at the stream's end ("Session ended"), for the tests.</summary>
    internal const string NoteTag = "chat-note";

    /// <summary>The Tag of the line at the top that counts what is not drawn, for the tests.</summary>
    internal const string EarlierTag = "chat-earlier";

    // About three lines of 11.5-point text, as the thread's comments (TasksWindow.ThreeLines).
    const double ThreeLines = 54;

    readonly ChatStreamDeps deps;
    IReadOnlyList<(string Key, DateTimeOffset At, UIElement Element)> comments;
    bool earlierFolded;
    readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    TextBlock? earlierLine;
    TextBlock? noteLine;

    public ChatStreamPanel(ChatStreamDeps deps, IReadOnlyList<(string Key, DateTimeOffset At, UIElement Element)> comments, bool earlierFolded)
    {
        this.deps = deps;
        this.comments = comments;
        this.earlierFolded = earlierFolded;
        Tag = StreamTag;
    }

    /// <summary>
    /// The thread's comments as the page was drawn this time: the window draws them afresh with every rebuild of the page
    /// (CommentOf), and this panel outlives the rebuild (TasksWindow.Transcript.cs moves it into the new page), so the next
    /// <see cref="Apply"/> swaps the comments' elements for the new ones and leaves every item of the session as it is.
    /// </summary>
    public void SetComments(IReadOnlyList<(string Key, DateTimeOffset At, UIElement Element)> comments, bool earlierFolded)
    {
        this.comments = comments;
        this.earlierFolded = earlierFolded;
    }

    /// <summary>For the tests: the keys of the drawn entries, in order.</summary>
    internal IReadOnlyList<string> Keys { get; private set; } = [];

    /// <summary>For the tests: how many elements Apply has created in all (an in-place update creates none).</summary>
    internal int Created { get; private set; }

    /// <summary>
    /// Brings the stream to the session's items as they are now, with an optional dim line at its end ("Session ended").
    /// Draws what is new, updates what changed, drops what left the window, and touches nothing else.
    /// </summary>
    public void Apply(IReadOnlyList<ChatItem> items, string? note = null)
    {
        var view = ClaudeChatStream.Build(items, comments.Select(c => new StreamComment(c.Key, c.At)).ToList(), earlierFolded);
        var commentOf = comments.ToDictionary(c => c.Key, c => c.Element, StringComparer.Ordinal);
        var wanted = new List<Slot>();
        var unnamed = Unnamed(view.Entries);
        using (ChatThread.Using(ChatThread.InboxLook))
            foreach (var entry in view.Entries)
            {
                var names = !unnamed.Contains(entry.Key);
                if (slots.TryGetValue(entry.Key, out var slot))
                {
                    if (entry.Item is { } item) { if (!Same(slot.Item, item)) Refresh(slot, item); }
                    else slot.Element = commentOf[entry.Key];
                }
                else
                {
                    slot = entry.Item is { } created ? Create(entry, created) : new Slot(entry.Key, entry.At, commentOf[entry.Key], null);
                    slots[entry.Key] = slot;
                }
                // A message joined by the next one, or no longer joined, is signed again where it is (Unnamed).
                if (entry.Item is ChatMessage && slot.Names != names && slot.Element is FrameworkElement signedRow)
                {
                    ChatThread.NameAuthor(signedRow, names);
                    slot.Names = names;
                }
                wanted.Add(slot);
            }
        var keep = wanted.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in slots.Keys.Where(k => !keep.Contains(k)).ToList()) slots.Remove(gone);
        Keys = wanted.Select(s => s.Key).ToList();

        // The children, in order: the "N earlier" line, then each entry (a day line first when the day changes), then the note.
        var desired = new List<UIElement>();
        if (view.Earlier > 0)
        {
            earlierLine ??= Dim(EarlierTag);
            earlierLine.Text = view.Earlier == 1 ? "1 earlier message or step not shown" : $"{view.Earlier} earlier messages and steps not shown";
            desired.Add(earlierLine);
        }
        DateTime? day = null;
        using (ChatThread.Using(ChatThread.InboxLook))
            foreach (var slot in wanted)
            {
                var local = slot.At.ToLocalTime().Date;
                if (day != local)
                {
                    var words = TasksBoard.DayOf(local, deps.Now().ToLocalTime().Date);
                    slot.DayLine = slot.DayLine is { } line && line.Text == words ? line : ChatThread.DayLine(words);
                    desired.Add(slot.DayLine);
                }
                else slot.DayLine = null;
                day = local;
                desired.Add(slot.Element);
            }
        if (note is { Length: > 0 })
        {
            noteLine ??= Dim(NoteTag);
            noteLine.Text = note;
            desired.Add(noteLine);
        }
        Reconcile(desired);
    }

    sealed class Slot(string key, DateTimeOffset at, UIElement element, ChatItem? item)
    {
        public string Key { get; } = key;
        public DateTimeOffset At { get; } = at;
        public UIElement Element { get; set; } = element;
        public ChatItem? Item { get; set; } = item;
        public TextBlock? DayLine { get; set; }
        /// <summary>Whether a message's line names its author as drawn (see Unnamed); every message is drawn named.</summary>
        public bool Names { get; set; } = true;
    }

    // A RUN OF ONE AUTHOR'S MESSAGES NAMES THE AUTHOR ONCE, under its last (task 85218923, the rule ChatThread.Grouped keeps
    // for the comments): the keys of the session's messages whose next entry is a message of the same role on the same day.
    // A run of steps, a marker or a comment between them ends the run (TasksWindow.GroupedInStream groups the comments). A
    // message that a new one joins loses its name in place (ChatThread.NameAuthor), so nothing is re-created for it.
    internal static HashSet<string> Unnamed(IReadOnlyList<StreamEntry> entries) =>
        entries.Zip(entries.Skip(1))
            .Where(p => p.First.Item is ChatMessage a && p.Second.Item is ChatMessage b && a.Role == b.Role
                && p.First.At.ToLocalTime().Date == p.Second.At.ToLocalTime().Date)
            .Select(p => p.First.Key)
            .ToHashSet(StringComparer.Ordinal);

    // Whether the drawn item and the new one say the same: a run by its steps (the list is a new object every snapshot), the
    // rest by value.
    static bool Same(ChatItem? drawn, ChatItem now) =>
        drawn is ChatSteps a && now is ChatSteps b
            ? a.Steps.Count == b.Steps.Count && a.Steps.Zip(b.Steps).All(p => p.First == p.Second)
            : Equals(drawn, now);

    Slot Create(StreamEntry entry, ChatItem item)
    {
        Created++;
        var element = item switch
        {
            ChatSteps run => new ChatStepsView(run, deps),
            ChatMessage message => MessageOf(message),
            ChatMarker marker => MarkerOf(marker),
            _ => (UIElement)new Border(),
        };
        return new Slot(entry.Key, entry.At, element, item);
    }

    // A run is updated where it is; a message whose text grew is re-created in the same place (the element is swapped in
    // Reconcile's pass, which sees the new instance).
    void Refresh(Slot slot, ChatItem item)
    {
        if (slot.Element is ChatStepsView view && item is ChatSteps run) view.Update(run);
        else if (item is ChatMessage message)
        {
            // Drawn named, as every new message is; Apply signs it again if a run holds it.
            slot.Names = true;
            var index = Children.IndexOf(slot.Element);
            var fresh = MessageOf(message);
            Created++;
            if (index >= 0)
            {
                Children.RemoveAt(index);
                Children.Insert(index, fresh);
            }
            slot.Element = fresh;
        }
        slot.Item = item;
    }

    // Removes what is no longer wanted, inserts what is new at its place. Surviving elements are never moved: if their
    // order changed (it should not), the stream is laid out again from scratch.
    void Reconcile(List<UIElement> desired)
    {
        var wanted = desired.ToHashSet();
        for (var i = Children.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Children[i])) Children.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Children.Count && ReferenceEquals(Children[i], desired[i])) continue;
            if (Children.Contains(desired[i]))
            {
                Children.Clear();
                desired.ForEach(e => Children.Add(e));
                return;
            }
            Children.Insert(i, desired[i]);
        }
    }

    TextBlock Dim(string tag) => new()
    {
        Tag = tag,
        FontSize = 10.5,
        Foreground = InboxLook.Ink3,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 6),
    };

    // ---- a message ---------------------------------------------------------------------------------------------------

    // The owner's message on the right, Claude's on the left, as the comments are (ChatThread.Message), with the same
    // markdown, the same snug width and the same three-line clip with "Show more". "Show more" opens in place, by this
    // stream's own state, and does not redraw the page the way a comment's does.
    UIElement MessageOf(ChatMessage message)
    {
        var mine = message.Role == ChatRole.User;
        var at = message.At.HasValue ? message.At.Value.ToLocalTime() : (DateTimeOffset?)null;
        var view = new TaskCommentView(Guid.Empty, mine ? "you" : "Claude", !mine, at?.ToString("HH:mm") ?? "", message.Text)
        {
            Who = mine ? CommentAuthor.You : CommentAuthor.Claude,
            Time = at?.ToString("HH:mm") ?? "",
            FullWhen = at?.ToString("d MMM yyyy HH:mm") ?? "",
            At = message.At.HasValue ? message.At.Value : default,
        };
        var parts = new StackPanel();
        if (message.Text.Trim().Length > 0)
        {
            var document = MarkdownBlocks.Render(message.Text, deps.OpenLink);
            document.FontSize = 11.5;
            document.Foreground = InboxLook.Ink;
            var body = new RichTextBox
            {
                Document = document,
                IsReadOnly = true,
                IsDocumentEnabled = true,
                Template = deps.Bare,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = InboxLook.Ink,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 1, 0, 0),
                Focusable = true,
                Cursor = System.Windows.Input.Cursors.Arrow,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            ChatThread.FitWidth(body);
            var open = deps.State.OpenMessages.Contains(message.Id);
            if (!open)
            {
                body.MaxHeight = ThreeLines;
                body.ClipToBounds = true;
            }
            var more = new TextBlock { FontSize = 10.5, Foreground = InboxLook.Ink3, Text = open ? "Show less" : "Show more" };
            var toggle = deps.Button(more, "Show more", () =>
            {
                var nowOpen = !deps.State.OpenMessages.Remove(message.Id);
                if (nowOpen) deps.State.OpenMessages.Add(message.Id);
                body.MaxHeight = nowOpen ? double.PositiveInfinity : ThreeLines;
                body.ClipToBounds = !nowOpen;
                more.Text = nowOpen ? "Show less" : "Show more";
            });
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Visibility = open || TasksWindow.IsLong(message.Text) ? Visibility.Visible : Visibility.Collapsed;            if (!open)
                body.LayoutUpdated += (_, _) =>
                {
                    if (body.ActualWidth <= 0 || deps.State.OpenMessages.Contains(message.Id)) return;
                    var wanted = body.ExtentHeight > ThreeLines + 0.5 ? Visibility.Visible : Visibility.Collapsed;
                    if (toggle.Visibility != wanted) toggle.Visibility = wanted;
                };
            parts.Children.Add(body);
            parts.Children.Add(toggle);
        }
        if (message.Images > 0)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 1) };
            chip.Children.Add(InboxMarks.Icon("image", 13, InboxLook.Ink3));
            chip.Children.Add(new TextBlock
            {
                Text = message.Images == 1 ? "1 picture" : $"{message.Images} pictures", FontSize = 11, Foreground = InboxLook.Ink3,
                Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            parts.Children.Add(chip);
        }
        if (message.IsError) AutomationProperties.SetHelpText(parts, "Claude Code's own message about an error");
        return ChatThread.Message(view, parts, []);
    }

    // A break in the chat, a dim centred line like Deskbar's own events.
    UIElement MarkerOf(ChatMarker marker)
    {
        var at = marker.At.HasValue ? marker.At.Value.ToLocalTime() : (DateTimeOffset?)null;
        var words = marker.Kind == ChatMarkerKind.Compacted ? "Claude's memory of this chat was shortened" : "You stopped Claude";
        var view = new TaskCommentView(Guid.Empty, "Deskbar", false, "", words) { Who = CommentAuthor.Deskbar, Time = at?.ToString("HH:mm") ?? "" };
        var body = new TextBlock
        {
            Text = at is null ? words : $"{words} · {view.Time}", FontSize = 10.5, Foreground = InboxLook.Ink3,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
        };
        return ChatThread.Event(view, body, timeInBody: true, []);
    }
}
