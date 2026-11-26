using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// WORKING OR IDLE (task 10b49554): which listed sessions the tasks panel shows working, from `claude agents --json`.
// ClaudeActivity's header has the rule: busy with the turn in flight, past the resume grace (the first list trusted at
// once), held for two quiet lists before it shows idle, and gone at once once it leaves the list.
public sealed class ClaudeActivityTests
{
    static readonly DateTimeOffset t0 = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    static DateTimeOffset At(int seconds) => t0.AddSeconds(seconds);

    static ClaudeAgent Agent(string id, bool busy, string? state = null) =>
        new(id, Maybe<string>.None, @"C:\repos\TaskSpaces", Background: state is not null, busy, "")
        {
            State = state is null ? Maybe<string>.None : Maybe<string>.From(state),
        };

    [Fact]
    public void Nothing_works_before_the_first_list() =>
        Assert.Empty(ClaudeActivity.None.Working);

    [Fact]
    public void A_busy_session_in_the_first_list_works_at_once()
    {
        var activity = ClaudeActivity.None.Next([Agent("s1", busy: true), Agent("s2", busy: false)], At(0));

        Assert.Equal(["s1"], activity.Working);
    }

    // A session that ended its turn and only waits on its own job (a PR watch) is listed busy, state "done": idle.
    [Fact]
    public void A_busy_session_whose_turn_is_over_is_idle()
    {
        var activity = ClaudeActivity.None.Next(
            [Agent("watching", busy: true, state: "done"), Agent("asked", busy: true, state: "blocked"), Agent("turn", busy: true, state: "working")],
            At(0));

        Assert.Equal(["turn"], activity.Working);
    }

    // A session that appears after the first list reports busy for up to 30 seconds after a resume while idle (measured):
    // its busy counts only once it has been listed that long.
    [Fact]
    public void A_session_new_after_the_first_list_works_only_after_the_grace()
    {
        var first = ClaudeActivity.None.Next([], At(0));

        var joined = first.Next([Agent("s1", busy: true)], At(15));
        Assert.Empty(joined.Working);

        var inside = joined.Next([Agent("s1", busy: true)], At(30));
        Assert.Empty(inside.Working);

        var past = inside.Next([Agent("s1", busy: true)], At(45));
        Assert.Equal(["s1"], past.Working);
    }

    // A prompt the hooks reported (TaskService.TurnsStarted) proves a turn runs: a session just started with one shows
    // working at once instead of after the grace. A prompt from long before it was listed does not.
    [Fact]
    public void A_prompt_waives_the_grace()
    {
        var first = ClaudeActivity.None.Next([], At(0));
        var prompts = new Dictionary<string, DateTimeOffset> { ["fresh"] = At(10), ["old"] = At(-60) };

        var joined = first.Next([Agent("fresh", busy: true), Agent("old", busy: true)], At(15), prompts);

        Assert.Equal(["fresh"], joined.Working);
    }

    // A gap in the lists (the feature off, `claude` unreadable) starts the record over, as at a Deskbar start: the next
    // list is trusted at once, and nothing working before the gap is held.
    [Fact]
    public void A_gap_in_the_lists_starts_over()
    {
        var before = ClaudeActivity.None.Next([Agent("s1", busy: true)], At(0));

        var after = before.Next([Agent("s1", busy: false), Agent("s2", busy: true)], At(0) + ClaudeActivity.StaleAfter);

        Assert.Equal(["s2"], after.Working);
    }

    // One quiet list keeps a working session working; the second in a row shows it idle, so a line does not jump parts on
    // one quiet poll.
    [Fact]
    public void A_working_session_shows_idle_only_after_two_quiet_lists_in_a_row()
    {
        var working = ClaudeActivity.None.Next([Agent("s1", busy: true)], At(0));

        var oneQuiet = working.Next([Agent("s1", busy: false)], At(15));
        Assert.Equal(["s1"], oneQuiet.Working);

        var twoQuiet = oneQuiet.Next([Agent("s1", busy: false)], At(30));
        Assert.Empty(twoQuiet.Working);
    }

    // Busy again between the two quiet lists starts the count over.
    [Fact]
    public void Busy_again_starts_the_quiet_count_over()
    {
        var activity = ClaudeActivity.None.Next([Agent("s1", busy: true)], At(0))
            .Next([Agent("s1", busy: false)], At(15))
            .Next([Agent("s1", busy: true)], At(30))
            .Next([Agent("s1", busy: false)], At(45));

        Assert.Equal(["s1"], activity.Working);
        Assert.Empty(activity.Next([Agent("s1", busy: false)], At(60)).Working);
    }

    // An idle session never shows working on a quiet list: the hold is only for one that was working.
    [Fact]
    public void An_idle_session_stays_idle()
    {
        var activity = ClaudeActivity.None.Next([Agent("s1", busy: false)], At(0)).Next([Agent("s1", busy: false)], At(15));

        Assert.Empty(activity.Working);
    }

    [Fact]
    public void A_session_that_leaves_the_list_stops_working_at_once()
    {
        var activity = ClaudeActivity.None.Next([Agent("s1", busy: true)], At(0)).Next([], At(15));

        Assert.Empty(activity.Working);
        Assert.Empty(activity.FirstSeen);
    }

    // The count a task's halos and its part of the block read: live sessions only.
    [Fact]
    public void A_tasks_working_count_takes_only_its_live_sessions()
    {
        var activity = ClaudeActivity.None.Next([Agent("live", busy: true), Agent("ended", busy: true)], At(0));
        var created = TaskBoard.Empty.Create("Tasks redesign", Maybe<Guid>.None,
            new TaskSession("live", @"C:\repos\TaskSpaces", t0, Maybe<DateTimeOffset>.None), t0, Guid.NewGuid()).Value.Task;
        var task = created with
        {
            Sessions = [.. created.Sessions, new TaskSession("ended", @"C:\repos\TaskSpaces", t0, Maybe<DateTimeOffset>.From(t0))],
        };

        Assert.Equal(1, activity.WorkingCount(task));
    }
}
