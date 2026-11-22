using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// The previous workspace's item ("↩ <previous>" on the strips, in the titled list and on the taskbar) and its default.
// Petre: "make it on by default if top strip is used", "and taskbar is autohide". Until a choice is made it follows
// the desk (WorkspaceJumps.BackItemWanted); a choice, from Manage's box or a script, holds whatever the desk does after.
public class PreviousWorkspaceItemTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Started(AppState state)
    {
        store.Stored = state;
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // With no choice: on exactly when the list is on the screen strips AND the taskbar auto-hides.
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void With_no_choice_it_follows_the_strips_and_auto_hide(bool listOnScreens, bool autoHides, bool expected) =>
        Assert.Equal(expected, WorkspaceJumps.BackItemWanted(null, listOnScreens, () => autoHides));

    // A choice wins over the desk either way, and the taskbar is not even asked: reading it is two messages to Explorer
    // on every rebuild, which only the default needs.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_choice_wins_and_the_taskbar_is_not_read(bool choice)
    {
        var asked = false;
        Assert.Equal(choice, WorkspaceJumps.BackItemWanted(choice, listOnScreens: true, () => { asked = true; return !choice; }));
        Assert.False(asked);
    }

    // ...nor while the list is in the bar, where the answer is off whatever the taskbar does.
    [Fact]
    public void The_taskbar_is_not_read_while_the_list_is_in_the_bar()
    {
        var asked = false;
        Assert.False(WorkspaceJumps.BackItemWanted(null, listOnScreens: false, () => { asked = true; return true; }));
        Assert.False(asked);
    }

    // The manager reads the list's place from state: the strips are where the list goes in a fresh state.
    [Fact]
    public void The_manager_reads_the_list_place_from_state()
    {
        Assert.True(Started(AppState.Empty).PreviousWorkspaceItemShown(() => true));
        Assert.False(Started(AppState.Empty with { WindowListOffScreens = true, WindowListAtBottom = true })
            .PreviousWorkspaceItemShown(() => true));
    }

    // Manage's box, opened on a desk where the default is ON, raises Checked: that must record nothing, or today's
    // default would become a choice nobody made. Unticking it is a real choice, and holds when auto-hide is turned off.
    [Fact]
    public void The_box_agreeing_with_the_default_records_nothing_and_an_untick_is_kept()
    {
        var manager = Started(AppState.Empty);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);

        Assert.True(manager.SetPreviousWorkspaceItem(true, taskbarAutoHides: true).IsSuccess);
        Assert.Null(manager.State.PreviousWorkspaceItemChoice);
        Assert.Equal(0, pulses);

        Assert.True(manager.SetPreviousWorkspaceItem(false, taskbarAutoHides: true).IsSuccess);
        Assert.False(manager.State.PreviousWorkspaceItemChoice);
        Assert.Equal(1, pulses);
        Assert.False(manager.PreviousWorkspaceItemShown(() => true));
    }

    // And the other way: ticked on a desk where the default is off, it stays on when the desk would say off.
    [Fact]
    public void A_tick_against_the_default_is_kept()
    {
        var manager = Started(AppState.Empty);

        Assert.True(manager.SetPreviousWorkspaceItem(true, taskbarAutoHides: false).IsSuccess);

        Assert.True(manager.State.PreviousWorkspaceItemChoice);
        Assert.True(manager.PreviousWorkspaceItemShown(() => false));
    }
}
