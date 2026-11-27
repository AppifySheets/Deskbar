using System.Collections.Immutable;

namespace Deskbar.Core.Tasks;

// A QUESTION WAITING ON A DEPLOY IS CHECKED BY ITS SESSION, AND DESKBAR WAKES IT TO DO SO (task 6a4104c0).
//
// WHY. On 2026-11-27 an iam.ge task (d8ec9aa7) asked the user to merge its stacks pull request and redeploy three stacks
// in Portainer. Its session ended the turn with "Once the logs show the new version running, I'll confirm the deploy"
// and set nothing to bring it back: no cron, no wakeup, no monitor (it had set one for every earlier pull request it
// asked about). The user deployed, and for over an hour the task still asked him to deploy. Nothing in Deskbar could catch
// it: the stalled ask covers only tasks In progress (StalledTasks), the repository watch only follows the pull requests a
// task recorded (the stacks one was opened by CI), and a Portainer redeploy is invisible to Deskbar. The briefing did
// tell the session to "check on a schedule", in one clause of a long bullet. The user picked the fix: Deskbar wakes the
// session to check, rather than trusting each session to remember.
//
// WHO IS WOKEN. An open task waiting on the user (Waiting on you) whose question is a deploy hand-over (`task ask
// --deploy`, task a87891ca) or mentions a deploy ("Redeploy the portal"; "deploy" matches redeploy, deployed and
// deploying too), that DeployNotices.WaitingOn says
// still waits on one (a session, not Done, Closed, snoozed or between runs, and a pull request outside this build's
// repository that GitHub says is merged with no deploy reported), with a LIVE session and none of its sessions busy. Both
// halves are needed: the question alone would wake a task whose change is reported already, and the merged pull request
// alone would wake a task waiting on the user for something else entirely (d8ec9aa7 waited hours on clinical questions
// with its design pull request merged and no deploy reported). A session that has ended is left alone: resuming a dead
// session every two hours for good costs more than the nudge is worth, and the user sees the question anyway.
//
// WHEN. On a schedule counted from when the question began to stand (RepliedTasks.AskedAt), backing off: 15 minutes after
// the ask, then 30 minutes later, then 60, then every 2 hours. Most deploys land within the hour, so the first checks are
// close together; a question left for days costs a short turn every two hours. The schedule is fixed by the ask, not by
// the last change on the task, so a session that sets its status line while checking does not restart it. A new question
// starts a new schedule.
//
// A SLOT MISSED WHILE BUSY is taken once the sessions are quiet again (each slot is nudged at most once, the latest due
// one). A slot that passed before this Deskbar started is NOT taken: after a restart (a release restarts Deskbar several
// times a day) every task waiting on a deploy would otherwise be woken at once. The next slot comes in two hours at most.
//
// A PURE FUNCTION: the board, the busy sessions, GitHub's answers, the clock, when Deskbar started and which slot was
// last nudged per task come in; the tasks to nudge now and the next memory go out. TaskService keeps that memory in
// memory only.
public static class DeployWaits
{
    /// <summary>The gaps between checks: the first after the ask, each next one after the last, the final one repeating.</summary>
    public static readonly IReadOnlyList<TimeSpan> Gaps =
        [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), TimeSpan.FromHours(2)];

    /// <summary>
    /// The latest check slot of a question asked at <paramref name="askedAt"/> that is due by <paramref name="now"/>, or
    /// null before the first one.
    /// </summary>
    public static DateTimeOffset? LatestSlot(DateTimeOffset askedAt, DateTimeOffset now)
    {
        // The slots before the repeating gap, walked one by one (three of them), then the repeating gap by arithmetic, so
        // a question left for weeks costs no loop.
        var slot = askedAt;
        DateTimeOffset? latest = null;
        foreach (var gap in Gaps.Take(Gaps.Count - 1))
        {
            slot += gap;
            if (slot > now) return latest;
            latest = slot;
        }
        var last = Gaps[^1];
        var more = (now - slot).Ticks / last.Ticks;
        return slot + TimeSpan.FromTicks(last.Ticks * more);
    }

    /// <summary>
    /// Whether the task waits on the user for a deploy its session can check (see WHO IS WOKEN), regardless of whether a
    /// session is busy now. The app's CommentWaker asks again just before delivery, so a nudge that waited in its queue is
    /// dropped once the question was answered or the deploy reported.
    /// </summary>
    public static bool WaitsOnDeploy(TaskItem task, IReadOnlyDictionary<string, PullRequestArrival> pullRequests,
        Func<PullRequestLink, bool> selfChecked) =>
        task is { IsOpen: true, Status: TaskStatus.WaitingOnYou, Question.HasValue: true }
        // A deploy hand-over (`task ask --deploy`, task a87891ca) says so itself; the word stays for sessions that ask a
        // deploy as a plain question (an older briefing, or one asked together with a merge).
        && (task.QuestionIsDeploy || task.Question.Value.Contains("deploy", StringComparison.OrdinalIgnoreCase))
        && task.Sessions.Any(s => s.IsLive)
        && DeployNotices.WaitingOn(task,
            task.PullRequests.Select(p => DeployNotices.RepositoryOf(p.Key)).Distinct().ToList(),
            pullRequests, selfChecked).Count > 0;

    /// <param name="busy">Every session Claude Code lists busy now: a task with one of them is not woken this poll.</param>
    /// <param name="started">When this Deskbar started: a slot before it is not nudged (see the header).</param>
    /// <param name="nudged">The slot last nudged per task (the previous step's <see cref="DeployWaitStep.Nudged"/>).</param>
    public static DeployWaitStep Step(TaskBoard board, IReadOnlySet<string> busy,
        IReadOnlyDictionary<string, PullRequestArrival> pullRequests, Func<PullRequestLink, bool> selfChecked,
        DateTimeOffset now, DateTimeOffset started, ImmutableDictionary<Guid, DateTimeOffset> nudged)
    {
        var waiting = board.Tasks.Where(t => WaitsOnDeploy(t, pullRequests, selfChecked)).ToArray();
        var due = waiting
            .Where(t => !t.Sessions.Any(s => s.IsLive && busy.Contains(s.Id)))
            .Select(t => (Task: t, Slot: LatestSlot(RepliedTasks.AskedAt(t), now)))
            .Where(x => x.Slot is { } slot && slot >= started && (!nudged.TryGetValue(x.Task.Id, out var last) || slot > last))
            .Select(x => (x.Task.Id, Slot: x.Slot!.Value))
            .ToArray();
        // Every OPEN task keeps its memory, waiting or not (review finding): a task drops out of WaitsOnDeploy for a poll
        // whenever its session leaves Claude Code's list (a stop-then-resume delivery of this very check, a VS Code reload,
        // a snooze), and forgetting its slot then would nudge the same slot again once the session is back. A new question
        // on the same task gets new slots, all later than the old ones, so the memory needs no reset; only closed or
        // deleted tasks are dropped, so the map does not grow.
        var keep = board.Tasks.Where(t => t.IsOpen).Select(t => t.Id).ToHashSet();
        var next = nudged
            .RemoveRange(nudged.Keys.Where(id => !keep.Contains(id)).ToArray())
            .SetItems(due.Select(x => KeyValuePair.Create(x.Id, x.Slot)));
        return new DeployWaitStep(due.Select(x => x.Id).ToArray(), next);
    }
}

/// <summary>One poll's answer (DeployWaits.Step): the tasks whose session to wake now, and the slot last nudged per task.</summary>
public sealed record DeployWaitStep(IReadOnlyList<Guid> Nudge, ImmutableDictionary<Guid, DateTimeOffset> Nudged);
