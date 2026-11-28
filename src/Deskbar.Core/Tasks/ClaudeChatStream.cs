using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ONE STREAM FROM TWO SOURCES: A TASK'S COMMENTS (the board's) AND ITS SESSION'S TRANSCRIPT (ClaudeChat). The page draws the
// owner's messages, Claude's messages, Claude's runs of steps and the thread's comments in one column, newest at the bottom,
// above the comment box. This is the pure part: which transcript items count, and in what order they go with the comments.
// Plan: notes/tasks-inbox-steps-plan.md, section 1.
//
// THE RULE AGAINST SHOWING ONE THING TWICE, in a line: a comment is always drawn from the board, and a transcript item is
// drawn only when it is not something Deskbar itself put into the session. No text is compared between the two sources.
// How a comment gets into a session and what the transcript then holds (the plan's table):
//   * a running session with a live waiter is handed the comment by its Stop hook: the transcript line is a "user" line
//     whose origin is "task-notification", which ClaudeChat never shows (measured in three real task transcripts,
//     2026-11-26). Nothing to drop here.
//   * a session that is not running is resumed with the comment as its prompt. That line is a plain human prompt that
//     starts "Deskbar: the user commented on your task ..." (SessionBriefing.Commented). It is the one that WOULD be drawn
//     twice, so it is dropped here, by the same test the app already uses to tell Deskbar's own prompts from the owner
//     typing (SessionBriefing.DeliveredByDeskbar: a comment, the manager's comment, a reevaluate request, a stalled-task
//     ask, the keeper's "Continue from where you left off"). NOT measured against a real transcript: no such line was found
//     on this machine, and nobody resumed a session to make one. The rule follows the code that writes the prompt, and a
//     test builds the line from that code's own text. A miss would show a comment twice and lose none.
//   * a session's first message, "Deskbar task <id>: <title> ... Join this task" (ClaudeStart.FirstMessage), is Deskbar's too
//     and is dropped: the thread already says when a session joined.
//   * what the owner types into the session's own terminal is a human prompt without those openings and stays: it is not a
//     comment, so the board does not have it.
//   * Claude's `task comment` is a shell step whose text sits in the command's input, which a step never shows; the board's
//     comment is the bubble. The step itself is housekeeping and is dropped (next).
//
// DESKBAR'S OWN HOUSEKEEPING STEPS ARE LEFT OUT (the owner's ruling, 2026-11-26): a call of the `deskbar.cmd task ...` shim
// (ChatStep.Housekeeping). A run that holds only such steps is not drawn at all; a run with other steps keeps them and its
// Id, so the page matches it with the one it drew before.
public static class ClaudeChatStream
{
    /// <summary>How many of the session's items the page draws first, and how many more each press of "N earlier" adds. Comments are not counted.</summary>
    public const int MaxDrawn = 60;

    /// <summary>
    /// The transcript items the page may draw: without Deskbar's own prompts, without housekeeping steps, and without a
    /// run those steps leave empty. Order and every other item stay as they are.
    /// </summary>
    public static IReadOnlyList<ChatItem> Visible(IEnumerable<ChatItem> items) =>
        items.Where(item => item is not ChatMessage { Role: ChatRole.User } message || !DeliveredByDeskbar(message.Text))
            // The manager's echo of a page message (task fa99f981, ManagerSession.WithoutEcho) sits right under the stored
            // bubble with the same words, so the page drops it; a reply that held nothing else is not drawn at all.
            .SelectMany(item => item is ChatMessage { Role: ChatRole.Claude } reply && ManagerSession.WithoutEcho(reply.Text) is var shown && shown != reply.Text
                ? shown.Trim().Length > 0 ? new ChatItem[] { reply with { Text = shown } } : []
                : new[] { item })
            .Select(item => item is ChatSteps run && run.Steps.Any(s => s.Housekeeping) ? run with { Steps = run.Steps.Where(s => !s.Housekeeping).ToList() } : item)
            .Where(item => item is not ChatSteps { Steps.Count: 0 })
            .ToList();

    /// <summary>Whether a user message is one Deskbar wrote (a comment delivered by a resume, a request, the first message).</summary>
    internal static bool DeliveredByDeskbar(string text) =>
        SessionBriefing.DeliveredByDeskbar(text) || text.TrimStart().StartsWith("Deskbar task ", StringComparison.Ordinal)
        // A message from the manager's page that a resume delivered as a prompt (task 6c4c067f): the page draws the
        // stored copy (ManagerRecord.Messages), so the transcript's would be the same bubble twice.
        || text.TrimStart().StartsWith(ManagerSession.PageMessageOpening, StringComparison.Ordinal);

    /// <summary>
    /// The page's stream: <paramref name="items"/> (ClaudeChat's, any order of kinds, in file order) and the thread's
    /// <paramref name="comments"/> (each a key the page knows its element by, and its time) in one list. Items keep the file's
    /// order; each comment goes before the first item that is later than it, and a comment wins a tie. An item with no time
    /// counts as the moment of the item before it. Comments after the last item go last.
    /// </summary>
    /// <param name="earlierCommentsFolded">
    /// The thread shows only its latest comments and folds the rest behind "N earlier comments". Then the transcript items
    /// older than the oldest shown comment fold with them, or a long session would drown the three comments in its history.
    /// </param>
    public static StreamView Build(IEnumerable<ChatItem> items, IReadOnlyList<StreamComment> comments, bool earlierCommentsFolded, int maxDrawn = MaxDrawn)
    {
        var ordered = comments.OrderBy(c => c.At).ToList();
        var keyed = KeyedByTime(Visible(items));
        if (earlierCommentsFolded && ordered.Count > 0)
            keyed = keyed.Where(k => k.Key >= ordered[0].At).ToList();

        // THE CAP IS ON THE SESSION'S ITEMS ALONE (tester finding M2): every comment given is always in the stream, so an old
        // comment never falls off the page behind the cut. The last `maxDrawn` items stay; a comment older than the first
        // of them goes before it, in its place by time.
        var earlier = Math.Max(0, keyed.Count - Math.Max(1, maxDrawn));
        keyed = keyed.Skip(earlier).ToList();

        var entries = new List<StreamEntry>();
        var next = 0;
        foreach (var (key, item) in keyed)
        {
            for (; next < ordered.Count && ordered[next].At <= key; next++) entries.Add(new StreamEntry(ordered[next].Key, ordered[next].At, null));
            entries.Add(new StreamEntry(item.Id, key, item));
        }
        for (; next < ordered.Count; next++) entries.Add(new StreamEntry(ordered[next].Key, ordered[next].At, null));

        return new StreamView(entries, earlier);
    }

    // Each item with the moment it sorts by: its own time, else the previous item's, else the very start.
    static List<(DateTimeOffset Key, ChatItem Item)> KeyedByTime(IEnumerable<ChatItem> items)
    {
        var last = DateTimeOffset.MinValue;
        return items.Select(item =>
        {
            if (item.At.HasValue) last = item.At.Value;
            return (last, item);
        }).ToList();
    }
}

/// <summary>A comment as the merge knows it: the key of the element the page drew for it, and when it was written.</summary>
public sealed record StreamComment(string Key, DateTimeOffset At);

/// <summary>One place in the stream: a comment (<see cref="Item"/> null; <see cref="Key"/> is the comment's key) or a transcript item.</summary>
public sealed record StreamEntry(string Key, DateTimeOffset At, ChatItem? Item)
{
    public bool IsComment => Item is null;
}

/// <summary>The stream to draw and how many entries before it fold behind "N earlier".</summary>
public sealed record StreamView(IReadOnlyList<StreamEntry> Entries, int Earlier);

/// <summary>
/// Where a task's transcript is read from, and in what state its session is (ClaudeChatSource.Choose).
/// </summary>
public enum ChatSourceKind
{
    /// <summary>No session, or only ones /clear replaced: the page shows its thread as before and reads nothing.</summary>
    None,
    /// <summary>A session that runs (working or idle): read and polled. <see cref="ChatSource.Path"/> is null until its file exists.</summary>
    Live,
    /// <summary>A session that has stopped and whose transcript is there: read once, no polling.</summary>
    Ended,
    /// <summary>Sessions exist and none has a transcript left: one line says so.</summary>
    Expired,
}

/// <summary>A task's chosen transcript. <see cref="Path"/> is null for None, Expired, and a Live session that has no file yet.</summary>
public sealed record ChatSource(ChatSourceKind Kind, string? Path, string? SessionId)
{
    public static readonly ChatSource Nothing = new(ChatSourceKind.None, null, null);
}

// WHICH SESSION'S TRANSCRIPT A TASK'S PAGE FOLLOWS. One session, the same one a comment goes to (CommentWake.PlanFor) and a
// double-click opens (TaskViews.SessionToGoTo): a running one first, then the newest joined; a session /clear replaced
// (TaskSession.Replaced) is over and its id lives on in the new one, so it is skipped. Running is the LISTED state
// (Working or Idle, TaskViews.SessionList), the one the thread's session lines show.
//
// A running session whose file is not there yet is still the one to follow: a session that has just started writes its
// transcript with its first turn ("never cache a lookup failure that can succeed later", CLAUDE.md), so it is Live with no
// path, and the page looks again at its next poll instead of calling it expired.
public static class ClaudeChatSource
{
    public static ChatSource Choose(IReadOnlyList<TaskSession> sessions, Func<TaskSession, SessionState> stateOf, Func<TaskSession, Maybe<string>> pathOf)
    {
        var candidates = sessions.Where(s => !s.Replaced)
            .Select(s => (Session: s, State: stateOf(s)))
            .OrderByDescending(x => x.State is SessionState.Working or SessionState.Idle)
            .ThenByDescending(x => x.Session.Joined)
            .ToList();
        if (candidates.Count == 0) return ChatSource.Nothing;
        foreach (var (session, state) in candidates)
        {
            var live = state is SessionState.Working or SessionState.Idle;
            if (pathOf(session).TryGetValue(out var path)) return new ChatSource(live ? ChatSourceKind.Live : ChatSourceKind.Ended, path, session.Id);
            if (live) return new ChatSource(ChatSourceKind.Live, null, session.Id);
        }
        return new ChatSource(ChatSourceKind.Expired, null, null);
    }
}
