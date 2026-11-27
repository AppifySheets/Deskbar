using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ASKS GITHUB WHERE EACH DONE TASK'S CHANGE STANDS (task 8470d176; the rules are in RunningChanges). Every Poll it reads
// the board for the recorded pull requests of open tasks, Done or not (an open task shows the merged ring too, task
// 1afbd44f), that have no settled answer (RunningChanges.ToLook): those of
// this build's repository, and those of any other that no deploy report covers yet. With none it asks nothing, so a
// machine without such tasks, or without `gh`, runs nothing; it still tells TaskService which build runs, once, so a
// task decided by reports alone shows its hover line. Otherwise it asks GitHub about each, off the UI thread, and back
// on it hands the whole set of answers to TaskService.Arrived, which pulses the views only when something changed.
//
// TWO KINDS OF LOOK. A pull request of this build's repository is asked with the running commit, and the look answers
// whether that commit holds the merge (`gh pr view`, then GitHub's compare). One of another repository is asked with
// none (Maybe.None): there is no running commit of that app to compare with, so the look is `gh pr view` alone and says
// only open, merged or closed. Whether it runs is the session's report (TaskItem.Deployed), which arrives on the board.
//
// THE PACE. Most answers are asked once per Deskbar start: a merged or closed pull request is settled for the life of
// the process (PullRequestArrival.Settled), since the running commit cannot change without a restart. Only an open one
// (a session that marked its task Done before the merge) is asked again every Poll. A minute is soon enough for a task
// that just turned Done to get its ring, and a look is one or two gh calls of about a second each (`gh pr view`, then
// GitHub's compare for this build's repository; measured 2026-11-25). The pull requests of one look are asked ONE AFTER ANOTHER, not all at once: Petre's
// board held 13 links on 9 Done tasks on 2026-11-26, and 26 gh processes at once is a burst for nothing. A look still
// out when the next tick comes makes that tick skip, so a slow or hung gh never piles calls up.
//
// A failure (gh missing, signed out, offline, a local commit GitHub has never seen) leaves that pull request unknown,
// and a failed one draws the full tick as before, where one not asked yet draws the Checking ring (RunningChanges'
// header; the failed keys go to TaskService with every answer). A failed one is asked again only after
// FailureBackoff, not every minute: a Debug build from a commit that was never pushed fails every compare for its whole
// life (GitHub answers 404), and retrying each minute would spend gh calls, and GitHub's hourly limit that the
// sessions share, on an answer that cannot come. The trace says why, once per run of failures.
//
// Threads: `scheduler` is where the board is read and TaskService is told (the UI thread in the app, a TestScheduler in
// the tests), `background` is where `look` is started (the thread pool unless a test says otherwise), as PullRequestWatch
// does it.
public sealed class RunningChangeWatch : IDisposable
{
    /// <summary>How often the board is read for pull requests without a settled answer.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    /// <summary>How long a pull request whose look failed waits before it is asked again.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(30);

    readonly TaskService tasks;
    readonly RunningBuild build;
    readonly Func<PullRequestLink, Maybe<string>, Task<Result<PullRequestArrival>>> look;
    readonly IScheduler scheduler;
    readonly IScheduler background;
    readonly Action<string>? trace;
    readonly SerialDisposable lookCall = new();
    readonly CompositeDisposable subscriptions = new();

    // Every answer so far, keyed by PullRequestLink.Key. Answers are kept for the life of the watch: a task that leaves
    // Done and comes back finds its pull requests already known.
    ImmutableDictionary<string, PullRequestArrival> known = ImmutableDictionary<string, PullRequestArrival>.Empty;

    // When each pull request's last look failed, keyed by PullRequestLink.Key: it is not asked again until FailureBackoff
    // has passed. A success takes it out.
    ImmutableDictionary<string, DateTimeOffset> failedAt = ImmutableDictionary<string, DateTimeOffset>.Empty;

    // Whether a look is out: a tick that comes meanwhile skips.
    bool looking;

    // Whether the last look had a failure, so the trace line is written on the first of a run only.
    bool failing;

    // Whether the last finished look failed for every pull request it asked (task 8db4c78e): the machine cannot ask
    // GitHub, and TaskService keeps unasked ones on the full disc (RunningChanges.Of's `blind`).
    bool blind;

    /// <param name="build">The build this Deskbar runs (RunningBuild.Of).</param>
    /// <param name="look">
    /// What GitHub says about one pull request (GitHubCli.Arrival in the app), given the running commit for a pull request
    /// of this build's repository and none for another's (see the header).
    /// </param>
    public RunningChangeWatch(TaskService tasks, RunningBuild build, Func<PullRequestLink, Maybe<string>, Task<Result<PullRequestArrival>>> look,
        IScheduler scheduler, IScheduler? background = null, Action<string>? trace = null)
    {
        this.tasks = tasks;
        this.build = build;
        this.look = look;
        this.scheduler = scheduler;
        this.background = background ?? TaskPoolScheduler.Default;
        this.trace = trace;
        subscriptions.Add(Observable.Interval(Poll, scheduler).StartWith(scheduler, 0L).Subscribe(_ => Look()));
        subscriptions.Add(lookCall);
    }

    void Look()
    {
        if (looking) return;
        var now = scheduler.Now;
        var links = RunningChanges.ToLook(tasks.Board, build, known)
            .Where(l => !failedAt.TryGetValue(l.Key, out var at) || now - at >= FailureBackoff)
            .ToArray();
        // The views learn which build runs before anything is asked (a no-op once they know), so a Done task whose pull
        // requests are still to be asked draws the Checking ring now, not the full disc until the answers land (task
        // 8db4c78e, RunningChanges' header). A task decided by deploy reports alone needs no look, and this gives it its
        // hover line too.
        tasks.Arrived(build, known, failedAt.Keys, blind);
        if (links.Length == 0) return;
        looking = true;
        // ONE AFTER ANOTHER (see the header), and each answer handed over AS IT LANDS (task 8db4c78e): at a start the list
        // can be a dozen pull requests at a second or two each, and a task must not wait in Checking for the others.
        // A fault `look` did not turn into a Result would otherwise escape on the dispatcher and leave `looking` set, so
        // nothing would ever be looked at again (as in PullRequestWatch).
        var failures = new List<(PullRequestLink Link, Result<PullRequestArrival> Arrival)>();
        lookCall.Disposable = links.ToObservable()
            .Select(link => Observable.FromAsync(() => look(link, CommitFor(link))).Select(arrival => (Link: link, Arrival: arrival)))
            .Concat()
            .SubscribeOn(background)
            .ObserveOn(scheduler)
            .Subscribe(
                answer =>
                {
                    if (answer.Arrival.IsFailure) failures.Add(answer);
                    Heard(answer);
                },
                error =>
                {
                    looking = false;
                    trace?.Invoke($"running changes: look threw: {error.Message}");
                },
                () =>
                {
                    looking = false;
                    Traced(failures);
                    // Every pull request asked failed: this machine cannot ask GitHub (gh missing, signed out, offline),
                    // so a task that turns Done keeps the full disc rather than a ring until its own look fails too
                    // (RunningChanges' header). The next look with any answer clears it.
                    blind = failures.Count == links.Length;
                    tasks.Arrived(build, known, failedAt.Keys, blind);
                });
    }

    // The running commit for a pull request of this build's repository; none for another's, whose look reads its state alone.
    Maybe<string> CommitFor(PullRequestLink link) => build.Builds(link) ? build.Commit : Maybe<string>.None;

    // One answer: a failure is noted for the backoff (and keeps its task on the full disc, RunningChanges' header), a
    // success is known and clears the failure, and the views get the whole set.
    void Heard((PullRequestLink Link, Result<PullRequestArrival> Arrival) answer)
    {
        failedAt = answer.Arrival.IsFailure ? failedAt.SetItem(answer.Link.Key, scheduler.Now) : failedAt.Remove(answer.Link.Key);
        if (answer.Arrival.IsSuccess) known = known.SetItem(answer.Link.Key, answer.Arrival.Value);
        tasks.Arrived(build, known, failedAt.Keys, blind);
    }

    // The trace line for a look's failures, on the first look of a run of failing ones only.
    void Traced(IReadOnlyList<(PullRequestLink Link, Result<PullRequestArrival> Arrival)> failures)
    {
        if (failures.Count > 0 && !failing)
            trace?.Invoke($"running changes: could not look at {string.Join(", ", failures.Select(f => $"{f.Link.Key} ({f.Arrival.Error})"))}");
        failing = failures.Count > 0;
    }

    public void Dispose() => subscriptions.Dispose();
}
