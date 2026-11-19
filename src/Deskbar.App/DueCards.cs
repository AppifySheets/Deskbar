using System.Reactive.Disposables;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// The lifetime rules of the due cards (DueCard). Docs: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md,
// section "When the bar cannot be seen". Kept apart from App so the rules can be tested with real windows and a fake
// "out of sight" switch:
//  - a card opens only when a task comes due (TaskService.CameDue) while the bar is out of sight;
//  - one card per task: a second CameDue for a task that already has a card adds nothing;
//  - a click acts (go to the workspace, or open the stopwatch menu from the card for a free task) and closes the card,
//    at once, or when the menu closes;
//  - after every board change, each card whose task is no longer Due closes (done, deleted, timer cleared, or ended
//    by coming back to the workspace);
//  - whenever a card goes, the rest are restacked so the stack has no holes;
//  - at most MaxCards (five) are open at once; a task coming due beyond that gets no card (final review M7);
//  - each new card is handed to afterShow once it is on screen, which is where App pins it to every virtual desktop
//    (final review I3), as it pins the bar and the detached stats window;
//  - nothing a card does may throw out of the service's pulses (final review M4, see Guarded).
// Everything runs on the UI thread, like the service.
public sealed class DueCards : IDisposable
{
    readonly TaskService tasks;
    readonly Func<bool> outOfSight;
    readonly Func<Guid, (string? Name, Brush? Colour)> workspaceOf;
    readonly Func<TaskItem, DueCard, bool> click;
    readonly Action<DueCard> afterShow;
    readonly List<DueCard> cards = [];

    // Five cards stand about 400 DIP tall on the right of the screen, which is already a lot to put over somebody's
    // work. Past that, a card per task stops telling anyone anything new: the bar's stopwatch and its blinking rows
    // still show every due task, and the cards that are up already say "look at the bar". So a sixth task coming due
    // gets no card, and it is not queued for later either: CameDue fires once per task, and a card appearing minutes
    // after its task came due, when an older card closes, would read as a new event.
    const int MaxCards = 5;
    readonly CompositeDisposable subscriptions = [];

    public DueCards(TaskService tasks, Func<bool> outOfSight, Func<Guid, (string? Name, Brush? Colour)> workspaceOf,
        Func<TaskItem, DueCard, bool> click, Action<DueCard>? afterShow = null)
    {
        this.tasks = tasks;
        this.outOfSight = outOfSight;
        this.workspaceOf = workspaceOf;
        this.click = click;
        this.afterShow = afterShow ?? (_ => { });
        subscriptions.Add(tasks.CameDue.Subscribe(task => Guarded("open", () => Open(task))));
        subscriptions.Add(tasks.Changed.Subscribe(_ => Guarded("close", CloseNoLongerDue)));
    }

    /// <summary>The task ids that have a card open, lowest card first.</summary>
    public IReadOnlyList<Guid> OpenFor => cards.Select(c => c.TaskId).ToList();

    internal IReadOnlyList<DueCard> Cards => cards;

    // CameDue and Changed are pulsed from inside TaskService.Tick, which the app's one-second dispatcher timer calls,
    // and from every edit. An exception thrown here would leave through the service and the timer into the
    // dispatcher, and App's DispatcherUnhandledException leaves Handled false ON PURPOSE: apart from one known WPF
    // fault it restores the window titles, starts a successor and ends the process (FailFast). So one bad card would
    // restart the whole app. A card is a courtesy, and losing one is far cheaper than that, so the failure is traced
    // and swallowed here, at the edge, and nowhere wider.
    static void Guarded(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { ClickTrace.Write($"due card {what} failed: {e}"); }
    }

    void Open(TaskItem task)
    {
        if (!outOfSight() || cards.Count >= MaxCards || cards.Any(c => c.TaskId == task.Id)) return;
        var (name, colour) = task.WorkspaceId.HasValue ? workspaceOf(task.WorkspaceId.Value) : (null, null);
        DueCard? card = null;
        card = new DueCard(task, name, colour, () =>
        {
            // The handler gets the card so it can anchor a menu to it, and says whether the card is finished. A
            // handler that opened a menu returns false and calls Dismiss when the menu closes: closing the card
            // under an open menu would take its placement target away.
            if (click(task, card!)) Close(card!);
        });
        cards.Add(card);
        card.ShowStacked(cards.Count - 1);
        // After the show, never before: a window that has never been shown is on no desktop and cannot be pinned.
        afterShow(card);
    }

    void CloseNoLongerDue()
    {
        var now = tasks.Now;
        cards.Where(c => tasks.Board.Tasks.FirstOrDefault(t => t.Id == c.TaskId) is not { } t || t.StateAt(now) != TaskState.Due)
            .ToList()
            .ForEach(Close);
    }

    /// <summary>Closes a card the click handler kept open (its menu has closed) and restacks the rest.</summary>
    internal void Dismiss(DueCard card) => Close(card);

    void Close(DueCard card)
    {
        if (!cards.Remove(card)) return;
        card.Close();
        // Restack: each remaining card drops to the place its new index gives it.
        cards.Select((c, i) => (c, i)).ToList().ForEach(p => p.c.ShowStacked(p.i));
    }

    public void Dispose()
    {
        subscriptions.Dispose();
        cards.ToList().ForEach(c => c.Close());
        cards.Clear();
    }
}
