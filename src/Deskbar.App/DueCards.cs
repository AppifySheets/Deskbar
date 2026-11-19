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
//  - whenever a card goes, the rest are restacked so the stack has no holes.
// Everything runs on the UI thread, like the service.
public sealed class DueCards : IDisposable
{
    readonly TaskService tasks;
    readonly Func<bool> outOfSight;
    readonly Func<Guid, (string? Name, Brush? Colour)> workspaceOf;
    readonly Func<TaskItem, DueCard, bool> click;
    readonly List<DueCard> cards = [];
    readonly CompositeDisposable subscriptions = [];

    public DueCards(TaskService tasks, Func<bool> outOfSight, Func<Guid, (string? Name, Brush? Colour)> workspaceOf,
        Func<TaskItem, DueCard, bool> click)
    {
        this.tasks = tasks;
        this.outOfSight = outOfSight;
        this.workspaceOf = workspaceOf;
        this.click = click;
        subscriptions.Add(tasks.CameDue.Subscribe(Open));
        subscriptions.Add(tasks.Changed.Subscribe(_ => CloseNoLongerDue()));
    }

    /// <summary>The task ids that have a card open, lowest card first.</summary>
    public IReadOnlyList<Guid> OpenFor => cards.Select(c => c.TaskId).ToList();

    internal IReadOnlyList<DueCard> Cards => cards;

    void Open(TaskItem task)
    {
        if (!outOfSight() || cards.Any(c => c.TaskId == task.Id)) return;
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
