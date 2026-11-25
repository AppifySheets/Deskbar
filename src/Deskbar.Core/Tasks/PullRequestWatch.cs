using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// WATCHES THE PULL REQUESTS THAT WAITING QUESTIONS LINK, and settles a question once they are merged or closed (task
// b7f987b4; the rule and the reasons are in AskedPullRequests). Every Poll it reads the board: when no waiting task links
// a pull request it does nothing at all, so a machine without such questions, or without `gh`, never runs anything.
// Otherwise it asks GitHub about each linked pull request once, off the UI thread, and back on it settles every question
// AskedPullRequests.Settlements names (TaskService.SettleQuestion, which re-checks that the task still waits on the very
// question that was read).
//
// Half a minute between looks: Petre merges and then glances at the bar, and a minute of a task still sitting there as
// waiting on him is the thing being fixed. A look is one `gh pr view` per linked pull request, a second or so each
// (measured 2026-11-25), and a look still out when the next tick comes makes that tick skip, so a slow or hung gh never
// piles calls up. GitHub's rate limit (5000 an hour for a signed-in gh) is far away: one pull request watched all hour
// is 120.
//
// A failure (gh missing, signed out, offline, a pull request it cannot see) leaves that link unknown, and unknown settles
// nothing (AskedPullRequests.Settles), so the question waits as it did before this existed. The trace says why, once per
// run of failures rather than every half minute.
//
// Threads: `scheduler` is where the board is read and changed (the UI thread in the app, a TestScheduler in the tests),
// `background` is where `look` is started (the thread pool unless a test says otherwise), as ClaudeSessions does it.
public sealed class PullRequestWatch : IDisposable
{
    /// <summary>How often the linked pull requests are looked at while a question links any.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    readonly TaskService tasks;
    readonly Func<PullRequestLink, Task<Result<PullRequestFacts>>> look;
    readonly IScheduler scheduler;
    readonly IScheduler background;
    readonly Action<string>? trace;
    readonly SerialDisposable lookCall = new();
    readonly CompositeDisposable subscriptions = new();

    // Whether a look is out: a tick that comes meanwhile skips.
    bool looking;

    // Whether the last look had a failure, so the trace line is written on the first of a run only.
    bool failing;

    /// <param name="look">What GitHub says about one pull request (GitHubCli.PullRequest in the app); a failure for anything else.</param>
    public PullRequestWatch(TaskService tasks, Func<PullRequestLink, Task<Result<PullRequestFacts>>> look, IScheduler scheduler,
        IScheduler? background = null, Action<string>? trace = null)
    {
        this.tasks = tasks;
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
        var links = AskedPullRequests.Watched(tasks.Board).SelectMany(x => x.Links).DistinctBy(l => l.Key).ToArray();
        if (links.Length == 0) return;
        looking = true;
        // The error path matters as in ClaudeSessions.Read: a fault `look` did not turn into a Result would otherwise
        // escape on the dispatcher and leave `looking` set, so nothing would ever be looked at again.
        lookCall.Disposable = Observable
            .FromAsync(() => Task.WhenAll(links.Select(async link => (Link: link, Facts: await look(link)))))
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
                    trace?.Invoke($"pull requests: look threw: {error.Message}");
                });
    }

    void Heard(IReadOnlyList<(PullRequestLink Link, Result<PullRequestFacts> Facts)> answers)
    {
        var failures = answers.Where(a => a.Facts.IsFailure).ToArray();
        if (failures.Length > 0 && !failing)
            trace?.Invoke($"pull requests: could not look at {string.Join(", ", failures.Select(f => $"{f.Link.Key} ({f.Facts.Error})"))}");
        failing = failures.Length > 0;

        var facts = answers.Where(a => a.Facts.IsSuccess).ToDictionary(a => a.Link.Key, a => a.Facts.Value);
        AskedPullRequests.Settlements(tasks.Board, facts, tasks.Now).ToList().ForEach(settlement =>
        {
            trace?.Invoke($"pull requests: settled the question of task {settlement.TaskId}: {settlement.Note}");
            tasks.SettleQuestion(settlement);
        });
    }

    public void Dispose() => subscriptions.Dispose();
}
