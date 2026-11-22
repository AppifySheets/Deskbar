namespace Deskbar.Core.Tests;

// The two dates behind the Deskbar Pro reminder: the start is stamped once and never moved, and "Not now"
// records the day. Both are saved, so a restart keeps them. The schedule itself is ProReminderTests.
public class ProReminderStateTests
{
    static readonly DateOnly Today = new(2026, 11, 22);

    static (WorkspaceManager Manager, FakeStore Store) Started()
    {
        var store = new FakeStore();
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, store);
    }

    [Fact]
    public void The_start_is_stamped_once_and_kept()
    {
        var (manager, _) = Started();
        manager.StampProReminderStart(Today);
        manager.StampProReminderStart(Today.AddDays(3));
        Assert.Equal(Today, manager.State.ProReminderSince);
    }

    [Fact]
    public void Not_now_records_the_day()
    {
        var (manager, _) = Started();
        manager.DismissProReminder(Today);
        Assert.Equal(Today, manager.State.ProReminderDismissedOn);
    }

    [Fact]
    public void Both_dates_are_saved()
    {
        var (manager, store) = Started();
        manager.StampProReminderStart(Today);
        manager.DismissProReminder(Today.AddDays(15));
        Assert.Equal(Today, store.Stored.ProReminderSince);
        Assert.Equal(Today.AddDays(15), store.Stored.ProReminderDismissedOn);
    }
}
