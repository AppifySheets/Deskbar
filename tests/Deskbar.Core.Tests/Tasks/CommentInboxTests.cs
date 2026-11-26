using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The per-session inbox a comment is delivered into and a session's waiting Stop hook watches (CommentInbox; plan
// docs/superpowers/plans/2026-11-24-comment-wakes-session.md, Task 3). Each test has a scratch folder of its own.
public sealed class CommentInboxTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "deskbar-inbox-" + Guid.NewGuid().ToString("N"));
    DateTimeOffset clock = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    CommentInbox Inbox() => new(folder, () => clock);

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void Register_writes_a_generation_newer_than_any_before()
    {
        var inbox = Inbox();

        var first = inbox.Register("s1").Value;
        var second = inbox.Register("s1").Value;

        Assert.True(second.Generation > first.Generation);
        Assert.Equal(second.Generation.ToString(), File.ReadAllText(second.GenPath).Trim());
        Assert.Equal(Path.Combine(folder, "s1.md"), second.InboxPath);
        Assert.False(File.Exists(second.InboxPath));
    }

    [Fact]
    public void Two_deliveries_arrive_together()
    {
        var inbox = Inbox();
        var paths = inbox.Register("s1").Value;

        inbox.Deliver("s1", "first");
        inbox.Deliver("s1", "second");

        Assert.Equal("first\n\nsecond", File.ReadAllText(paths.InboxPath).Trim());
    }

    // The registration is the gen file itself, so a Deskbar restarted since (every merge releases one) still knows a
    // waiter is there, and when it lapses.
    [Fact]
    public void A_waiter_lapses_a_month_after_it_registered_even_across_a_restart()
    {
        Assert.True(Inbox().Lapsed("s1"));
        Inbox().Register("s1");

        clock = clock.AddDays(29);
        Assert.False(Inbox().Lapsed("s1"));

        clock = clock.AddDays(2);
        Assert.True(Inbox().Lapsed("s1"));
    }

    [Fact]
    public void Forget_deletes_both_files()
    {
        var inbox = Inbox();
        var paths = inbox.Register("s1").Value;
        inbox.Deliver("s1", "unread");

        inbox.Forget("s1");

        Assert.False(File.Exists(paths.InboxPath));
        Assert.False(File.Exists(paths.GenPath));
        Assert.True(inbox.Lapsed("s1"));
    }

    // A session id is used as a file name, so one that could climb out of the folder is refused.
    [Fact]
    public void A_session_id_that_is_not_a_plain_name_is_refused()
    {
        Assert.True(Inbox().Register(@"..\x").IsFailure);
        Assert.True(Inbox().Deliver("a/b", "x").IsFailure);
    }

    // Task daeaa26c: right before a resume the waiter is retired, so an orphan exits; a comment already waiting stays.
    [Fact]
    public void Retiring_ends_the_waiter_and_keeps_the_inbox()
    {
        Inbox().Register("s1");
        Inbox().Deliver("s1", "waiting comment");

        Inbox().Retire("s1");

        Assert.True(Inbox().Lapsed("s1"));
        Assert.False(File.Exists(Path.Combine(folder, "s1.gen")));
        Assert.Contains("waiting comment", File.ReadAllText(Path.Combine(folder, "s1.md")));
    }
}
