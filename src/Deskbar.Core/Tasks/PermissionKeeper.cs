using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace Deskbar.Core.Tasks;

// KEEPS THE PERMISSION DESK HONEST while Deskbar runs (Allow / Deny from a task's page). A question must leave the page
// the moment nobody can answer it there any more, because the terminal's own dialog is drawn the whole time (probe Q1,
// 2026-11-26) and the user may well answer there. Every way out deletes the question's slot, and a hook script whose
// slot is gone exits printing nothing, so a late or orphaned answer is harmless:
//
//   * `shown` goes false (the setting off, the task hooks off, the Inbox look off): every question is released;
//   * a read of `claude agents --json` no longer shows the question's session as "blocked" (its state while a
//     permission prompt waits, measured in the spike: "status":"waiting","waitingFor":"permission prompt",
//     "state":"blocked"): the user answered in the terminal, or the session went. Only questions older than the read
//     count (ListLag), because a list read before a question arrived cannot show it;
//   * every `expireEvery` the questions held past PermissionDesk.HeldFor go;
//   * Dispose (Deskbar exits): every question is released.
//
// The session's own hook events (Stop, UserPromptSubmit, SessionEnd) forget its questions too; that is in
// RemoteControl.ClaudeHook, where those events arrive. Everything here runs on `scheduler`, the UI thread in the app,
// which is where the pipe's handler and the page's clicks use the desk too.
public sealed class PermissionKeeper : IDisposable
{
    /// <summary>The state a listed session has while a permission prompt waits (spike, runs B2 and B5).</summary>
    public const string Blocked = "blocked";

    /// <summary>How much older than a list's arrival a question must be for the list to speak for it.</summary>
    public static readonly TimeSpan ListLag = TimeSpan.FromSeconds(10);

    /// <summary>How often questions held too long are taken off.</summary>
    public static readonly TimeSpan ExpireEvery = TimeSpan.FromSeconds(5);

    readonly PermissionDesk desk;
    readonly CompositeDisposable subscriptions = [];

    public PermissionKeeper(PermissionDesk desk, IObservable<bool> shown, IObservable<IReadOnlyCollection<ClaudeAgent>> lists,
        IScheduler scheduler, Func<DateTimeOffset> now)
    {
        this.desk = desk;
        subscriptions.Add(shown.DistinctUntilChanged().ObserveOn(scheduler).Where(on => !on).Subscribe(_ => desk.ReleaseAll()));
        subscriptions.Add(lists.ObserveOn(scheduler).Subscribe(list =>
            desk.Keep(session => list.Any(a => a.SessionId == session && a.State.GetValueOrDefault("") == Blocked), now() - ListLag)));
        subscriptions.Add(Observable.Interval(ExpireEvery, scheduler).Subscribe(_ => desk.Expire()));
    }

    public void Dispose()
    {
        subscriptions.Dispose();
        desk.ReleaseAll();
    }
}
