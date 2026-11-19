using System.Windows;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The due card (DueCard) and its lifetime rules (DueCards). Docs:
// docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md, "When the bar cannot be seen". The cards are real
// windows on the primary work area but never take the focus, so a run of the suite does not disturb the machine.
public class DueCardTests
{
    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    static TaskItem Item(string title = "write the report") =>
        new(Guid.NewGuid(), title, Maybe<Guid>.None, Start, Start,
            new TaskTimer(Start, TimeSpan.FromMinutes(1)), Maybe<DateTimeOffset>.None, []);

    [Fact]
    public void The_bar_is_out_of_sight_when_minimized() => StaThread.Run(() =>
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000 };

        bar.ShowBar();
        Assert.False(bar.OutOfSight);

        bar.MinimizeToTaskbar();
        Assert.True(bar.OutOfSight);

        bar.ShowBar();
        Assert.False(bar.OutOfSight);
        bar.Close();
    });

    [Fact]
    public void A_card_does_not_take_the_focus() => StaThread.Run(() =>
    {
        var card = new DueCard(Item(), null, null, () => { });
        card.ShowStacked(0);

        Assert.False(card.ShowActivated);
        Assert.NotEqual(0, card.ExStyle & 0x08000000L); // WS_EX_NOACTIVATE
        Assert.NotEqual(0, card.ExStyle & 0x00000080L); // WS_EX_TOOLWINDOW
        card.Close();
    });

    [Fact]
    public void Cards_stack_upwards() => StaThread.Run(() =>
    {
        var first = new DueCard(Item("one"), null, null, () => { });
        var second = new DueCard(Item("two"), "Work", Brushes.SteelBlue, () => { });
        first.ShowStacked(0);
        second.ShowStacked(1);

        Assert.True(second.Top < first.Top);
        Assert.True(second.Top + second.ActualHeight <= first.Top, $"first {first.Top}/{first.ActualHeight} second {second.Top}/{second.ActualHeight}");
        first.Close();
        second.Close();
    });

    // The service's clock is a local the test moves; Tick announces a task whose timer has run out.
    [Fact]
    public void A_card_closes_when_its_task_is_no_longer_due() => StaThread.Run(() =>
    {
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        var parked = tasks.Park("probe", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1))).Value;
        using var cards = new DueCards(tasks, () => true, _ => (null, null), (_, _) => true);

        now += TimeSpan.FromMinutes(2);
        tasks.Tick(Maybe<Guid>.None);
        Assert.Equal([parked.Id], cards.OpenFor);

        // Still due, announced again: no second card.
        tasks.Tick(Maybe<Guid>.None);
        Assert.Single(cards.OpenFor);

        Assert.True(tasks.Done(parked.Id).IsSuccess);
        Assert.Empty(cards.OpenFor);
    });

    [Fact]
    public void No_card_opens_while_the_bar_can_be_seen() => StaThread.Run(() =>
    {
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        tasks.Park("probe", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1)));
        using var cards = new DueCards(tasks, () => false, _ => (null, null), (_, _) => true);

        now += TimeSpan.FromMinutes(2);
        tasks.Tick(Maybe<Guid>.None);

        Assert.Empty(cards.OpenFor);
    });

    // A free task has no workspace, so its click opens the stopwatch menu. The handler must get the card (to anchor
    // the menu to it, whatever state the bar is in), and returning false keeps the card until the menu closes.
    [Fact]
    public void Clicking_a_free_tasks_card_hands_the_card_over_and_can_keep_it_open() => StaThread.Run(() =>
    {
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        tasks.Park("probe", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1)));
        DueCard? target = null;
        DueCards? cards = null;
        cards = new DueCards(tasks, () => true, _ => (null, null), (_, card) => { target = card; return false; });

        now += TimeSpan.FromMinutes(2);
        tasks.Tick(Maybe<Guid>.None);
        var shown = Assert.Single(cards.Cards);
        shown.Click();

        Assert.Same(shown, target);
        Assert.Single(cards.OpenFor); // kept open for the menu

        cards.Dismiss(shown); // what the menu's Closed does
        Assert.Empty(cards.OpenFor);
        cards.Dispose();
    });

    // Final review I3: every card that opens is handed to afterShow once, after it is on screen, which is where App
    // pins it to every virtual desktop (a window that has never been shown is on no desktop and cannot be pinned).
    [Fact]
    public void Each_new_card_is_handed_over_after_it_shows() => StaThread.Run(() =>
    {
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        tasks.Park("one", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1)));
        tasks.Park("two", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1)));
        var shown = new List<(DueCard Card, bool Visible)>();
        using var cards = new DueCards(tasks, () => true, _ => (null, null), (_, _) => true, card => shown.Add((card, card.IsVisible)));

        now += TimeSpan.FromMinutes(2);
        tasks.Tick(Maybe<Guid>.None);
        tasks.Tick(Maybe<Guid>.None);

        Assert.Equal(cards.Cards, shown.Select(s => s.Card));
        Assert.All(shown, s => Assert.True(s.Visible));
    });

    // Final review M7: at most five cards; a sixth task coming due adds none (it stays due in the bar).
    [Fact]
    public void At_most_five_cards_open() => StaThread.Run(() =>
    {
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        Enumerable.Range(1, 7).ToList().ForEach(i => tasks.Park($"task {i}", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1))));
        using var cards = new DueCards(tasks, () => true, _ => (null, null), (_, _) => true);

        now += TimeSpan.FromMinutes(2);
        tasks.Tick(Maybe<Guid>.None);

        Assert.Equal(5, cards.OpenFor.Count);
    });

    // Final review M4: the cards run inside the service's pulses, which the app's one-second timer drives, and the
    // app leaves an unhandled dispatcher exception unhandled on purpose. So a card that throws while opening must not
    // take the tick (and with it the app) down.
    [Fact]
    public void A_card_that_throws_while_opening_does_not_break_the_tick() => StaThread.Run(() =>
    {
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        tasks.Park("probe", Guid.NewGuid(), Maybe.From(TimeSpan.FromMinutes(1)));
        using var cards = new DueCards(tasks, () => true, _ => throw new InvalidOperationException("no colour"), (_, _) => true);

        now += TimeSpan.FromMinutes(2);
        var thrown = Record.Exception(() => tasks.Tick(Maybe<Guid>.None));

        Assert.Null(thrown);
    });

    // ...and the same for the close that follows a board change: here a card whose window went away underneath
    // (closed from outside) is restacked, and showing a closed window throws.
    [Fact]
    public void A_card_that_throws_while_closing_does_not_break_the_change() => StaThread.Run(() =>
    {
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        var first = tasks.Park("one", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1))).Value;
        tasks.Park("two", Maybe<Guid>.None, Maybe.From(TimeSpan.FromMinutes(1)));
        using var cards = new DueCards(tasks, () => true, _ => (null, null), (_, _) => true);
        now += TimeSpan.FromMinutes(2);
        tasks.Tick(Maybe<Guid>.None);
        cards.Cards[1].Close();

        var thrown = Record.Exception(() => tasks.Done(first.Id));

        Assert.Null(thrown);
    });
}
