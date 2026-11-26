using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The one stream of a task's comments and its session's transcript (ClaudeChatStream), and which session's transcript a
// page follows (ClaudeChatSource). Every line is made up. The "resume" road is built from SessionBriefing's OWN text, since
// no real transcript of a resumed comment exists on this machine (see the header of ClaudeChatStream).
public class ClaudeChatStreamTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-02T03:00:00Z");

    static string J(string text) => JsonSerializer.Serialize(text);
    static string Stamp(int seconds) => T0.AddSeconds(seconds).ToString("o");

    static string Human(string text, int at) =>
        $$$"""{"type":"user","uuid":{{{J("u-" + at)}}},"timestamp":"{{{Stamp(at)}}}","origin":{"kind":"human"},"message":{"role":"user","content":{{{J(text)}}}}}""";

    static string Said(string text, int at) =>
        $$$"""{"type":"assistant","uuid":{{{J("a-" + at)}}},"timestamp":"{{{Stamp(at)}}}","message":{"id":{{{J("m-" + at)}}},"model":"claude-test","content":[{"type":"text","text":{{{J(text)}}}}]}}""";

    static string Tool(string id, string name, string input, int at) =>
        $$$"""{"type":"assistant","uuid":"a-{{{id}}}","timestamp":"{{{Stamp(at)}}}","message":{"id":"m-{{{id}}}","model":"claude-test","content":[{"type":"tool_use","id":{{{J(id)}}},"name":{{{J(name)}}},"input":{{{input}}}}]}}""";

    static string Shell(string id, string command, int at) => Tool(id, "Bash", JsonSerializer.Serialize(new { command }), at);

    static TaskItem Task() => new(Guid.NewGuid(), "sample task", Guid.NewGuid(), Deskbar.Core.Tasks.TaskStatus.InProgress, "", Maybe<string>.None, T0, T0, T0, [], []);

    static IReadOnlyList<ChatItem> Chat(params string[] lines) => ClaudeChat.Parse(lines);

    static string[] Texts(StreamView view) =>
        [.. view.Entries.Select(e => e.Item switch { null => "comment:" + e.Key, ChatMessage m => m.Text, ChatSteps s => "steps:" + s.Steps.Count, ChatMarker m => "marker", _ => "?" })];

    // ---- the merge --------------------------------------------------------------------------------------------------

    [Fact]
    public void Items_and_comments_are_ordered_by_time()
    {
        var view = ClaudeChatStream.Build(Chat(Human("one", 0), Said("three", 20), Said("five", 40)),
            [new("c2", T0.AddSeconds(10)), new("c4", T0.AddSeconds(30))], earlierCommentsFolded: false);

        Assert.Equal(["one", "comment:c2", "three", "comment:c4", "five"], Texts(view));
        Assert.Equal(0, view.Earlier);
    }

    [Fact]
    public void A_comment_wins_a_tie_and_one_after_the_last_item_goes_last()
    {
        var view = ClaudeChatStream.Build(Chat(Human("one", 0), Said("two", 10)),
            [new("tie", T0.AddSeconds(10)), new("late", T0.AddSeconds(99))], earlierCommentsFolded: false);

        Assert.Equal(["one", "comment:tie", "two", "comment:late"], Texts(view));
    }

    [Fact]
    public void An_item_without_a_time_follows_its_predecessor()
    {
        const string noTime = """{"type":"assistant","uuid":"nt","message":{"id":"m","model":"claude-test","content":[{"type":"text","text":"untimed"}]}}""";
        var view = ClaudeChatStream.Build(Chat(Human("one", 10), noTime, Said("later", 50)),
            [new("c", T0.AddSeconds(20))], earlierCommentsFolded: false);

        Assert.Equal(["one", "untimed", "comment:c", "later"], Texts(view));
    }

    // THE CAP IS ON THE SESSION'S ITEMS ALONE: a comment is never counted and never cut (tester M2).
    [Fact]
    public void The_stream_keeps_only_the_last_items_and_counts_the_rest()
    {
        var lines = Enumerable.Range(0, 10).Select(i => Said($"m{i}", i * 10)).ToArray();

        var view = ClaudeChatStream.Build(Chat(lines), [new("c", T0.AddSeconds(95))], earlierCommentsFolded: false, maxDrawn: 4);

        Assert.Equal(["m6", "m7", "m8", "m9", "comment:c"], Texts(view));
        Assert.Equal(6, view.Earlier);
    }

    [Fact]
    public void A_comment_on_the_cut_side_stays_in_the_stream()
    {
        var lines = Enumerable.Range(0, 10).Select(i => Said($"m{i}", i * 10)).ToArray();
        var comments = new StreamComment[] { new("old", T0.AddSeconds(15)), new("older", T0.AddSeconds(-5)), new("middle", T0.AddSeconds(35)) };

        var view = ClaudeChatStream.Build(Chat(lines), comments, earlierCommentsFolded: false, maxDrawn: 4);

        // Every comment is there, before the first kept item, in its order by time; the items before them are the "earlier".
        Assert.Equal(["comment:older", "comment:old", "comment:middle", "m6", "m7", "m8", "m9"], Texts(view));
        Assert.Equal(6, view.Earlier);
        Assert.Equal(comments.Length, view.Entries.Count(e => e.IsComment));
    }

    [Fact]
    public void Asking_for_more_items_brings_the_earlier_ones_back_in_their_place()
    {
        var lines = Enumerable.Range(0, 10).Select(i => Said($"m{i}", i * 10)).ToArray();
        var comments = new StreamComment[] { new("c", T0.AddSeconds(15)) };

        var more = ClaudeChatStream.Build(Chat(lines), comments, earlierCommentsFolded: false, maxDrawn: 4 + 4);

        Assert.Equal(["m2", "m3", "m4", "m5", "m6", "m7", "m8", "m9"], Texts(more).Where(t => !t.StartsWith("comment:")));
        Assert.Equal(2, more.Earlier);
        Assert.Equal("comment:c", Texts(ClaudeChatStream.Build(Chat(lines), comments, false, maxDrawn: 10))[2]);
    }
    // The thread folds all but its latest comments; the history before the oldest shown one folds with them.
    [Fact]
    public void Items_older_than_the_oldest_shown_comment_fold_with_the_earlier_comments()
    {
        var chat = Chat(Said("old", 0), Said("older still", 5), Said("new", 100));
        var comments = new StreamComment[] { new("c", T0.AddSeconds(50)) };

        Assert.Equal(["comment:c", "new"], Texts(ClaudeChatStream.Build(chat, comments, earlierCommentsFolded: true)));
        Assert.Equal(["old", "older still", "comment:c", "new"], Texts(ClaudeChatStream.Build(chat, comments, earlierCommentsFolded: false)));
    }

    // ---- nothing twice ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_comment_delivered_by_a_resume_is_not_shown_twice()
    {
        // The prompt Deskbar hands a resumed session, from the app's own text.
        var resumed = SessionBriefing.Commented(Task(), "please check the footer");
        var chat = Chat(Human("Join in", 0), Human(resumed, 10), Said("on it", 20));

        var view = ClaudeChatStream.Build(chat, [new("c", T0.AddSeconds(9))], earlierCommentsFolded: false);

        Assert.Equal(["Join in", "comment:c", "on it"], Texts(view));
        Assert.DoesNotContain(view.Entries, e => e.Item is ChatMessage m && m.Text.Contains("please check the footer"));
    }

    [Fact]
    public void The_managers_comment_a_reevaluate_request_a_stalled_ask_and_the_keepers_message_are_dropped()
    {
        var task = Task();
        var fromDeskbar = new[]
        {
            SessionBriefing.Commented(task, "from the manager", byManager: true),
            SessionBriefing.Reevaluate(task),
            SessionBriefing.StalledNudge(task),
            SessionKeeper.ContinueMessage,
        };

        var view = ClaudeChatStream.Build(Chat(fromDeskbar.Select((text, i) => Human(text, i)).Append(Said("done", 99)).ToArray()), [], false);

        Assert.Equal(["done"], Texts(view));
    }

    [Fact]
    public void The_first_message_of_a_session_is_dropped()
    {
        var first = ClaudeStart.FirstMessage(Task());

        var view = ClaudeChatStream.Build(Chat(Human(first, 0), Said("joined", 5)), [], false);

        Assert.Equal(["joined"], Texts(view));
    }

    [Fact]
    public void A_prompt_the_owner_types_in_the_terminal_stays()
    {
        var view = ClaudeChatStream.Build(Chat(Human("please also run the build", 0), Said("ok", 5)), [], false);

        Assert.Equal(["please also run the build", "ok"], Texts(view));
    }

    // The measured road: a running session's waiter hands the comment over as a task-notification user line. ClaudeChat
    // never shows such a line, so the board's comment is the only copy.
    [Fact]
    public void A_comment_delivered_by_the_waiter_is_not_in_the_chat()
    {
        var text = "<task-notification>\n<summary>Stop hook feedback</summary>\n</task-notification>\n<system-reminder>\nStop hook blocking error from command \"Stop\": "
            + SessionBriefing.Commented(Task(), "a comment") + "</system-reminder>";
        var line = $$$"""{"type":"user","uuid":"w1","timestamp":"{{{Stamp(5)}}}","origin":{"kind":"task-notification","producer":"session-task"},"promptSource":"system","message":{"role":"user","content":{{{J(text)}}}}}""";

        var view = ClaudeChatStream.Build(Chat(Said("working", 0), line, Said("saw it", 9)), [new("c", T0.AddSeconds(4))], false);

        Assert.Equal(["working", "comment:c", "saw it"], Texts(view));
    }

    // ---- housekeeping -----------------------------------------------------------------------------------------------

    [Fact]
    public void Deskbars_own_shim_calls_leave_a_run_and_a_run_of_only_those_is_not_drawn()
    {
        var chat = Chat(
            Said("starting", 0),
            Shell("h1", "deskbar.cmd task status-line 'working' --session s1", 1),
            Shell("h2", "\"$APPDATA/Deskbar/deskbar.cmd\" task comment - --session s1", 2),
            Said("midway", 10),
            Shell("h3", "deskbar.cmd task join abc --session s1", 11),
            Shell("r1", "dotnet test", 12),
            Tool("r2", "Read", """{"file_path":"/x/Sample.cs"}""", 13));

        var view = ClaudeChatStream.Build(chat, [], false);

        Assert.Equal(["starting", "midway", "steps:2"], Texts(view));
        var run = Assert.IsType<ChatSteps>(view.Entries[^1].Item);
        Assert.Equal("h3", run.Id);
        Assert.Equal(["dotnet test", "Sample.cs"], run.Steps.Select(s => s.Label));
    }

    [Fact]
    public void Visible_leaves_the_chat_alone_when_there_is_nothing_to_drop()
    {
        var chat = Chat(Human("hi", 0), Said("yo", 1), Shell("r", "git status", 2));

        Assert.Equal(chat.Count, ClaudeChatStream.Visible(chat).Count);
    }

    // ---- which session ----------------------------------------------------------------------------------------------

    static TaskSession Session(string id, int joinedSeconds, bool ended = false, bool replaced = false) =>
        new(id, @"C:\made\up", T0.AddSeconds(joinedSeconds), ended ? T0.AddSeconds(joinedSeconds + 1) : Maybe<DateTimeOffset>.None)
        {
            ReplacedAt = replaced ? T0 : Maybe<DateTimeOffset>.None,
        };

    static ChatSource Choose(TaskSession[] sessions, IReadOnlyDictionary<string, SessionState> states, params string[] withTranscript) =>
        ClaudeChatSource.Choose(sessions, s => states[s.Id], s => withTranscript.Contains(s.Id) ? "C:\\t\\" + s.Id + ".jsonl" : Maybe<string>.None);

    [Fact]
    public void A_live_session_comes_before_a_newer_ended_one()
    {
        var source = Choose([Session("old-live", 0), Session("new-ended", 100, ended: true)],
            new Dictionary<string, SessionState> { ["old-live"] = SessionState.Idle, ["new-ended"] = SessionState.Ended }, "old-live", "new-ended");

        Assert.Equal(new ChatSource(ChatSourceKind.Live, "C:\\t\\old-live.jsonl", "old-live"), source);
    }

    [Fact]
    public void Of_ended_sessions_the_newest_with_a_transcript_comes_first()
    {
        var source = Choose([Session("a", 0, true), Session("b", 50, true), Session("c", 100, true)],
            new Dictionary<string, SessionState> { ["a"] = SessionState.Ended, ["b"] = SessionState.Ended, ["c"] = SessionState.Expired }, "a", "b");

        Assert.Equal(new ChatSource(ChatSourceKind.Ended, "C:\\t\\b.jsonl", "b"), source);
    }

    [Fact]
    public void A_session_that_clear_replaced_is_skipped()
    {
        var source = Choose([Session("replaced", 100, replaced: true), Session("current", 0)],
            new Dictionary<string, SessionState> { ["replaced"] = SessionState.Idle, ["current"] = SessionState.Idle }, "replaced", "current");

        Assert.Equal("current", source.SessionId);
    }

    [Fact]
    public void No_session_means_nothing_to_read() =>
        Assert.Equal(ChatSource.Nothing, Choose([], new Dictionary<string, SessionState>()));

    [Fact]
    public void Only_replaced_sessions_mean_nothing_to_read() =>
        Assert.Equal(ChatSourceKind.None, Choose([Session("r", 0, replaced: true)], new Dictionary<string, SessionState> { ["r"] = SessionState.Idle }, "r").Kind);

    [Fact]
    public void Sessions_whose_transcripts_are_gone_are_expired()
    {
        var source = Choose([Session("a", 0, true), Session("b", 10, true)],
            new Dictionary<string, SessionState> { ["a"] = SessionState.Expired, ["b"] = SessionState.Expired });

        Assert.Equal(new ChatSource(ChatSourceKind.Expired, null, null), source);
    }

    // A session that has just started has no file yet; it is looked at again, never called expired.
    [Fact]
    public void A_running_session_without_its_file_yet_is_live_with_no_path()
    {
        var source = Choose([Session("fresh", 0), Session("old", -100, true)],
            new Dictionary<string, SessionState> { ["fresh"] = SessionState.Working, ["old"] = SessionState.Ended }, "old");

        Assert.Equal(new ChatSource(ChatSourceKind.Live, null, "fresh"), source);
    }
}
