using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE CLOCK OF THE REPOSITORY WATCH (Deskbar task c8e6967d; spec docs/superpowers/specs/2026-11-27-repo-watcher-design.md,
// plan docs/superpowers/plans/2026-11-27-repo-watch.md, rules in RepoWatch, memory in TaskService). RepoWatch holds the
// rules as pure functions and TaskService the memory; this class only decides WHEN to ask GitHub and hands the answers
// over, the shape of RunningChangeWatch.
//
// Every minute, and at once at the start, it reads the board for the repositories that have a task in flight
// (RepoWatch.InFlight), asks GitHub about each repository's pull requests in ONE query, and gives the answer to
// RepoWatch.Poll through TaskService.Watched, which stores the memory and announces the nudges. Then, for each merge the
// watch follows (TaskService.Watch.Following), it asks for the workflow runs on the merge commit and gives the answer to
// RepoWatch.Follow through TaskService.Followed. With nothing in flight it asks nothing, so a machine without such tasks,
// or without `gh`, runs no process at all.
//
// ONE AFTER ANOTHER, and one look at a time: the repositories and merges of a tick are asked in sequence (Concat), never
// all at once, and a tick that comes while a look is still out is skipped, so a slow or hung gh never piles calls up
// (the reasoning of RunningChangeWatch's header; Petre's gh limit is shared with every session).
//
// A FAILED LOOK CHANGES NOTHING. gh missing, signed out, offline or an answer that cannot be read is traced (once per run
// of failures of a repository, not every minute) and skipped: the repository's LastSuccess is not renewed, so after five
// minutes TaskService.IsWatched turns false and the sessions' prompt line tells them to watch for themselves again (the
// spec's fallback, Review Focus 2).
//
// Threads: `scheduler` is where the board is read and TaskService is told (the UI thread in the app, a TestScheduler in
// the tests); `background` is where the lookups are started (the thread pool unless a test says otherwise).
public sealed class RepoWatchPoll : IDisposable
{
    /// <summary>How often the board is read for repositories with a task in flight.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    readonly TaskService tasks;
    readonly Func<string, IReadOnlyList<PullRequestLink>, Task<Result<IReadOnlyList<PullFacts>>>> lookRepository;
    readonly Func<PullRequestLink, string, Task<Result<IReadOnlyList<RunFacts>>>> lookRuns;
    readonly IScheduler scheduler;
    readonly IScheduler background;
    readonly Action<string>? trace;
    readonly Func<bool> enabled;
    readonly SerialDisposable lookCall = new();
    readonly CompositeDisposable subscriptions = new();

    // Whether a look is out: a tick that comes meanwhile skips.
    bool looking;

    // The repositories (and merges) whose last look failed, so a trace line is written on the first failure of a run only.
    readonly HashSet<string> failing = [];

    /// <param name="lookRepository">What GitHub says about the given pull requests of one "owner/repo" (GitHubCli.Repository).</param>
    /// <param name="lookRuns">The workflow runs on a merge commit of a pull request's repository (GitHubCli.Runs).</param>
    /// <param name="enabled">Whether sessions keep their tasks at all (AppState.ClaudeTasksInEffect): a tick asks nothing while it is false.</param>
    public RepoWatchPoll(TaskService tasks,
        Func<string, IReadOnlyList<PullRequestLink>, Task<Result<IReadOnlyList<PullFacts>>>> lookRepository,
        Func<PullRequestLink, string, Task<Result<IReadOnlyList<RunFacts>>>> lookRuns,
        IScheduler scheduler, IScheduler? background = null, Action<string>? trace = null, Func<bool>? enabled = null)
    {
        this.tasks = tasks;
        this.lookRepository = lookRepository;
        this.lookRuns = lookRuns;
        this.scheduler = scheduler;
        this.background = background ?? TaskPoolScheduler.Default;
        this.trace = trace;
        this.enabled = enabled ?? (() => true);
        subscriptions.Add(Observable.Interval(Every, scheduler).StartWith(scheduler, 0L).Subscribe(_ => Look()));
        subscriptions.Add(lookCall);
    }

    // One tick: the repositories first, then the followed merges, all on the board as it stands now.
    void Look()
    {
        if (looking || !enabled()) return;
        looking = true;
        lookCall.Disposable = Repositories()
            .Concat(Observable.Defer(Merges))
            .Subscribe(
                _ => { },
                error =>
                {
                    looking = false;
                    trace?.Invoke($"repo watch: look threw: {error.Message}");
                },
                () => looking = false);
    }

    // One look per repository with a task in flight. The LOOKS run one after another (Concat waits for each look to
    // finish before it starts the next), but the answers are applied through ObserveOn(scheduler), which only queues them:
    // Concat does not wait for that hop, so the next look may start before the previous answer has been applied. That is
    // harmless, since each answer is applied against whatever TaskService holds when its turn on `scheduler` comes.
    // Review finding (Deskbar task c8e6967d): this comment used to say each answer was applied before the next look began.
    //
    // A Poll that throws for one repository is traced and the tick goes on with the rest (the same review finding): an
    // exception inside the Select would otherwise end the whole observable and skip every repository after it.
    IObservable<Unit> Repositories() =>
        Observable.Defer(() => RepoWatch.InFlight(tasks.Board, tasks.Watch).ToObservable()
            .Select(group => Looked(group.Key, () => lookRepository(group.Key, [.. group.Value.Select(x => x.Link).DistinctBy(l => l.Key)]))
                .Select(answer => (Repository: group.Key, Answer: answer)))
            .Concat()
            .ObserveOn(scheduler)
            .Select(x =>
            {
                try
                {
                    x.Answer
                        .Tap(facts => tasks.Watched(RepoWatch.Poll(tasks.Watch, tasks.Board, x.Repository, facts, scheduler.Now)))
                        .Tap(() => failing.Remove(x.Repository))
                        .TapError(error => Failed(x.Repository, error));
                }
                catch (Exception e)
                {
                    trace?.Invoke($"repo watch: applying the look at {x.Repository} threw: {e.Message}");
                }
                return Unit.Default;
            }));

    // One look per followed merge, on the memory as it stands after the repositories' answers. A merge whose task is no
    // longer open or is Done is dropped first, with no look and no nudge (RepoWatch.Drop). A look that FAILS still runs
    // the three-hour limit (RepoWatch.GiveUp), so a follow whose runs cannot be read does not go on for ever. A merge that
    // throws is traced and the rest still get their look, as for the repositories.
    IObservable<Unit> Merges() =>
        Observable.Defer(() =>
        {
            Following().Where(m => !IsLive(m.TaskId)).ToList().ForEach(m => tasks.Followed(RepoWatch.Drop(tasks.Watch, m)));
            return Following().ToObservable()
                .Select(merge => Looked(merge.Link.Key, () => lookRuns(merge.Link, merge.MergeCommit))
                    .Select(answer => (Merge: merge, Answer: answer)))
                .Concat()
                .ObserveOn(scheduler)
                .Select(x =>
                {
                    try
                    {
                        x.Answer
                            .Tap(runs => tasks.Followed(RepoWatch.Follow(tasks.Watch, x.Merge, runs, scheduler.Now)))
                            .Tap(() => failing.Remove(x.Merge.Link.Key))
                            .TapError(error =>
                            {
                                Failed(x.Merge.Link.Key, error);
                                tasks.Followed(RepoWatch.GiveUp(tasks.Watch, x.Merge, scheduler.Now));
                            });
                    }
                    catch (Exception e)
                    {
                        trace?.Invoke($"repo watch: applying the look at {x.Merge.Link.Key} threw: {e.Message}");
                    }
                    return Unit.Default;
                });
        });

    IReadOnlyList<FollowedMerge> Following() => tasks.Watch.Following;

    // Whether the task of a followed merge can still be told anything: it is open and not Done.
    bool IsLive(Guid taskId) =>
        tasks.Board.Tasks.Any(t => t.Id == taskId && t is { IsOpen: true } && t.Status != TaskStatus.Done);

    // Starts one gh look off the UI thread. A fault `look` did not turn into a Result would otherwise end the whole tick
    // and leave `looking` set (RunningChangeWatch's header), so it becomes a failed look here.
    IObservable<Result<T>> Looked<T>(string what, Func<Task<Result<T>>> look) =>
        Observable.FromAsync(async () =>
            {
                try { return await look(); }
                catch (Exception e) { return Result.Failure<T>($"{what}: {e.Message}"); }
            })
            .SubscribeOn(background);

    void Failed(string what, string error)
    {
        if (failing.Add(what)) trace?.Invoke($"repo watch: could not look at {what}: {error}");
    }

    public void Dispose() => subscriptions.Dispose();
}
