using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The manager's page in the tasks panel (task 6c4c067f; spec docs/superpowers/specs/2026-11-28-manager-inbox-page-design.md):
// the text a page message is delivered as, the record that keeps the page's messages, and the chat's rule that a page
// message written into the transcript by a resume is not drawn a second time.
public sealed class ManagerPageTests : IDisposable
{
    const string Id = "aaaaaaaa-1111-2222-3333-444444444444";
    static readonly DateOnly today = new(2026, 11, 28);
    static readonly DateTimeOffset at = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);
    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-manager-page-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static ManagerMessage Message(int n) => new(Guid.NewGuid(), at.AddMinutes(n), $"message {n}");

    [Fact]
    public void PageMessage_starts_with_the_opening_and_carries_the_text_and_the_open_task_line()
    {
        var text = ManagerSession.PageMessage("  what is stuck?  ", Maybe.From("Deskbar: the task open in the user's Inbox as they sent this is 12345678 \"x\"."));

        Assert.StartsWith(ManagerSession.PageMessageOpening, text);
        Assert.Contains("\n\nwhat is stuck?\n\n", text);
        Assert.EndsWith("12345678 \"x\".", text);
    }

    [Fact]
    public void PageMessage_without_an_open_task_ends_with_the_text()
    {
        var text = ManagerSession.PageMessage("hello", Maybe<string>.None);

        Assert.Equal(ManagerSession.PageMessageOpening + "\n\nhello", text);
    }

    // THE ECHO (task fa99f981): the waiter's wake shows only "Deskbar" in the manager's terminal, so the waiter's copy of a
    // page message asks the manager to repeat the words, and the page and the Inbox line hide that repeat again.
    [Fact]
    public void ByWaiter_adds_the_echo_request_to_a_page_message_once()
    {
        var page = ManagerSession.PageMessage("PR 599 needs rebasing", Maybe<string>.None);
        var once = ManagerSession.ByWaiter(page);

        Assert.Equal(page + "\n\n" + ManagerSession.EchoRequest, once);
        Assert.Equal(once, ManagerSession.ByWaiter(once));
    }

    [Fact]
    public void ByWaiter_leaves_a_wake_alone() =>
        Assert.Equal("Deskbar wake. Two tasks handed back.", ManagerSession.ByWaiter("Deskbar wake. Two tasks handed back."));

    [Theory]
    [InlineData("> From your page: PR 599 needs rebasing\n\nRebased.", "Rebased.")]
    [InlineData("\n> From your page: one\n> From your page: two\n\nBoth done.\n\nDetails.", "Both done.\n\nDetails.")]
    [InlineData("> From your page: only the echo", "")]
    [InlineData("> From your page: fix A\n> fix B\n\nDone.", "Done.")]
    [InlineData("> From your page: fix A\n\n> a quote of my own\n\nDone.", "> a quote of my own\n\nDone.")]
    [InlineData("Rebased.\n\n> From your page: later in the reply", "Rebased.\n\n> From your page: later in the reply")]
    [InlineData("> a quote of my own\n\nRebased.", "> a quote of my own\n\nRebased.")]
    public void WithoutEcho_drops_only_the_echo_lines_at_the_start(string reply, string shown) =>
        Assert.Equal(shown, ManagerSession.WithoutEcho(reply));

    [Fact]
    public void The_page_draws_the_reply_without_its_echo_and_skips_an_echo_alone()
    {
        var replied = new ChatMessage("c1", ChatRole.Claude, $"{ManagerSession.EchoMarker} hello\n\nHi.", 0, false, at);
        var echoOnly = new ChatMessage("c2", ChatRole.Claude, $"{ManagerSession.EchoMarker} hello", 0, false, at);
        var plain = new ChatMessage("c3", ChatRole.Claude, "Plain.", 0, false, at);

        var visible = ClaudeChatStream.Visible([replied, echoOnly, plain]);

        Assert.Equal([replied with { Text = "Hi." }, plain], visible);
    }

    [Fact]
    public void WithMessage_keeps_the_newest_100()
    {
        var record = Enumerable.Range(0, ManagerSession.MessagesKept + 5)
            .Aggregate(new ManagerRecord(Id, today), (r, n) => ManagerSession.WithMessage(r, Message(n)));

        Assert.Equal(ManagerSession.MessagesKept, record.Messages!.Count);
        Assert.Equal("message 5", record.Messages[0].Text);
        Assert.Equal($"message {ManagerSession.MessagesKept + 4}", record.Messages[^1].Text);
    }

    [Fact]
    public void Records_with_equal_messages_are_equal()
    {
        var message = Message(1);
        var one = new ManagerRecord(Id, today, Messages: [message]);
        var two = new ManagerRecord(Id, today, Messages: new List<ManagerMessage> { message });

        Assert.Equal(one, two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
        Assert.NotEqual(one, one with { Messages = [message, Message(2)] });
    }

    [Fact]
    public void StartArguments_end_with_the_given_first_message()
    {
        var args = ManagerSession.StartArguments("b.md", "C:/x/deskbar.cmd", "hi there");

        Assert.Equal(["--", "hi there"], args.TakeLast(2));
        Assert.Equal(ManagerSession.FirstMessage, ManagerSession.StartArguments("b.md", "C:/x/deskbar.cmd").Last());
    }

    [Fact]
    public void Visible_drops_a_prompt_that_is_a_page_message()
    {
        var page = new ChatMessage("m1", ChatRole.User, ManagerSession.PageMessage("hello", Maybe<string>.None), 0, false, at);
        var typed = new ChatMessage("m2", ChatRole.User, "typed in the terminal", 0, false, at);

        var visible = ClaudeChatStream.Visible([page, typed]);

        Assert.Equal([typed], visible);
    }

    [Fact]
    public void OpenTaskLine_adds_nothing_to_a_page_message_which_carries_its_own()
    {
        var task = new TaskItem(Guid.NewGuid(), "Some task", Maybe<Guid>.None, Deskbar.Core.Tasks.TaskStatus.InProgress, "",
            Maybe<string>.None, at, at, at, [], []);

        var line = ManagerSession.OpenTaskLine(Maybe.From(ManagerSession.PageMessage("hello", Maybe<string>.None)), Maybe.From(task));

        Assert.False(line.HasValue);
        // The page message still counts as the user's own words for the answered line (AfterPrompt).
        var record = new ManagerRecord(Id, today, Question: "Which?");
        var after = ManagerQuestion.AfterPrompt(record, Id, Maybe.From(ManagerSession.PageMessage("hello", Maybe<string>.None)));
        Assert.True(after.Value.Replying);
        Assert.Null(after.Value.Question);
    }

    [Fact]
    public void Messages_round_trip_through_state_json()
    {
        var record = new ManagerRecord(Id, today, Messages: [Message(1), Message(2)]);
        var store = new JsonPersistenceStore(dir);

        Assert.True(store.Save(AppState.Empty with { Manager = record }).IsSuccess);

        Assert.Equal(record, store.Load().Value.Manager);
    }
}
