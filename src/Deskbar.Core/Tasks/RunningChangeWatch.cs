using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ASKS GITHUB WHETHER EACH DONE TASK'S CHANGE IS IN THE RUNNING BUILD (task 8470d176; the rules are in RunningChanges).
// Every Poll it reads the board for the pull requests of Done tasks in this build's repository that have no settled
// answer (RunningChanges.ToLook). With none it does nothing at all, so a machine without such tasks, or without `gh`,
// runs nothing. Otherwise it asks GitHub about each, off the UI thread, and back on it hands the whole set of answers to
// TaskService.Arrived, which pulses the views only when something changed.
//
// THE PACE. Most answers are asked once per Deskbar start: a merged or closed pull request is settled for the life of
// the process (PullRequestArrival.Settled), since the running commit cannot change without a restart. Only an open one
// (a session that marked its task Done before the merge) and a failed look are asked again, every Poll. A minute is
// soon enough for a task that just turned Done to get its ring, and a look is two gh calls of about a second each
// (`gh pr view`, then GitHub's compare; measured 2026-11-25). A look still out when the next tick comes makes that tick
// skip, so a slow or hung gh never piles calls up.
//
// A failure (gh missing, signed out, offline, a local commit GitHub has never seen) leaves that pull request unknown,
// and unknown draws the full tick as before (RunningChanges' header). The trace says why, once per run of failures.
//
// Threads: `scheduler` is where the board is read and TaskService is told (the UI thread in the app, a TestScheduler in
// the tests), `background` is where `look` is started (the thread pool unless a test says otherwise), as PullRequestWatch
// does it.
public sealed class RunningChangeWatch : IDisposable
{
    /// <summary>How often the board is read for pull requests without a settled answer.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    readonly TaskService tasks;
    readonly RunningBuild build;
    readonly Func<PullRequestLink, string, Task<Result<PullRequestArrival>>> look;
    readonly IScheduler scheduler;
    readonly IScheduler background;
    readonly Action<string>? trace;
    readonly SerialDisposable lookCall = new();
    readonly CompositeDisposable subscriptions = new();

    // Every answer so far, keyed by PullRequestLink.Key. Answers are kept for the life of the watch: a task that leaves
    // Done and comes back finds its pull requests already known.
    ImmutableDictionary<string, PullRequestArrival> known = ImmutableDictionary<string, PullRequestArrival>.Empty;

    // Whether a look is out: a tick that comes meanwhile skips.
    bool looking;

    // Whether the last look had a failure, so the trace line is written on the first of a run only.
    bool failing;

    /// <param name="build">The build this Deskbar runs (RunningBuild.Of).</param>
    /// <param name="look">What GitHub says about one pull request against the running commit (GitHubCli.Arrival in the app).</param>
    public RunningChangeWatch(TaskService tasks, RunningBuild build, Func<PullRequestLink, string, Task<Result<PullRequestArrival>>> look,
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
        var links = RunningChanges.ToLook(tasks.Board, build, known);
        if (links.Count == 0) return;
        looking = true;
        // A fault `look` did not turn into a Result would otherwise escape on the dispatcher and leave `looking` set, so
        // nothing would ever be looked at again (as in PullRequestWatch).
        lookCall.Disposable = Observable
            .FromAsync(() => Task.WhenAll(links.Select(async link => (Link: link, Arrival: await look(link, build.Commit)))))
            .SubscribeOn(background)
            .ObserveOn(scheduler)
            .Subscribe(
                answers =>
                {
                    looking = false;
                    Heard(answers);
                },
                error =>
                {
                    looking = false;
                    trace?.Invoke($"running changes: look threw: {error.Message}");
                });
    }

    void Heard(IReadOnlyList<(PullRequestLink Link, Result<PullRequestArrival> Arrival)> answers)
    {
        var failures = answers.Where(a => a.Arrival.IsFailure).ToArray();
        if (failures.Length > 0 && !failing)
            trace?.Invoke($"running changes: could not look at {string.Join(", ", failures.Select(f => $"{f.Link.Key} ({f.Arrival.Error})"))}");
        failing = failures.Length > 0;

        known = answers.Where(a => a.Arrival.IsSuccess)
            .Aggregate(known, (all, a) => all.SetItem(a.Link.Key, a.Arrival.Value));
        tasks.Arrived(build, known);
    }

    public void Dispose() => subscriptions.Dispose();
}
