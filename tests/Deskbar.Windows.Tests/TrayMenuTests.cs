using System.Windows.Controls;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The tray menu is two commands by explicit decision, and it has now gained a third (#110), so what
// is on it is worth pinning: this menu is the only surface some of these actions have, and an item
// dropped by accident would be a feature that quietly stopped existing.
//
// The item HEADERS are the assertion rather than a count, because a count passes while the wrong
// three items are present.
public class TrayMenuTests
{
    static IReadOnlyList<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToList();

    // Petre: "right click gives exit and manage", then #110: "check for new version as a right-click
    // menu option."
    [Fact]
    public void The_menu_offers_manage_a_manual_update_check_and_exit() => StaThread.Run(() =>
    {
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { });

        Assert.Equal(["Manage…", "Check for updates…", "Exit"], Headers(menu));
    });

    // Deskbar task 7dd83c89: a test build is marked first, the marking is a fact and not a command, and the item under
    // it restarts into the installed release. With no release installed the marking stands alone.
    [Fact]
    public void A_test_build_is_marked_first_and_offers_the_restart_into_a_release() => StaThread.Run(() =>
    {
        var restarts = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            testBuild: new TestBuildItems("Test build 2.6.208 (topic), gets no updates", ("Restart into Deskbar 2.6.58", () => restarts++)));

        Assert.Equal(["⚠ Test build 2.6.208 (topic), gets no updates", "⟳ Restart into Deskbar 2.6.58", "Manage…", "Check for updates…", "Exit"],
            Headers(menu));
        Assert.False(menu.Items.OfType<MenuItem>().First().IsEnabled);

        menu.Items.OfType<MenuItem>().ElementAt(1).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, restarts);

        var alone = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            testBuild: new TestBuildItems("Test build 2.6.208, gets no updates", null));
        Assert.Equal(["⚠ Test build 2.6.208, gets no updates", "Manage…", "Check for updates…", "Exit"], Headers(alone));
    });

    // Clicking it has to actually reach the app, which is the half a header assertion cannot see.
    [Fact]
    public void The_update_check_item_invokes_the_check() => StaThread.Run(() =>
    {
        var checks = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => checks++);

        menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Check for updates…")
            .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(1, checks);
    });

    // The news item (#71) is not always there, and when it is it comes FIRST: it is news rather than
    // a command, and it stops existing again once the user is on the new version.
    [Fact]
    public void An_available_update_is_announced_above_everything_else() => StaThread.Run(() =>
    {
        var opened = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            update: ("Update to 9.9.9…", () => opened++));

        Assert.Equal(["Update to 9.9.9…", "Manage…", "Check for updates…", "Exit"], Headers(menu));

        menu.Items.OfType<MenuItem>().First()
            .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, opened);
    });

    // On a Windows build whose virtual-desktop COM API cannot be driven, the menu leads with a
    // disabled explanation rather than leaving the user wondering why nothing moves between desktops.
    [Fact]
    public void Compatibility_mode_says_so_and_the_notice_cannot_be_clicked() => StaThread.Run(() =>
    {
        var menu = TrayMenu.Build(compatibilityMode: true, () => { }, () => { }, () => { });

        var notice = menu.Items.OfType<MenuItem>().First();
        Assert.Contains("Virtual desktops unavailable", (string)notice.Header);
        Assert.False(notice.IsEnabled);
    });

    // #173. The tray is the channel that must never be a dead end: if the taskbar button is ever
    // lost -- a shell restart, or a pin that failed on a Windows build without the desktop COM --
    // this item is the way back to a bar nobody can see.
    [Fact]
    public void The_bar_toggle_sits_under_manage_and_says_which_way_it_goes() => StaThread.Run(() =>
    {
        var minimize = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            update: null, bar: ("Minimize bar", () => { }));
        Assert.Equal(["Manage…", "Minimize bar", "Check for updates…", "Exit"], Headers(minimize));

        // The same item, the other way round, while the bar is standing behind its button.
        var show = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            update: null, bar: ("Show bar", () => { }));
        Assert.Equal(["Manage…", "Show bar", "Check for updates…", "Exit"], Headers(show));
    });

    // "let's add debug dump which will give me a file and open window": for a problem on a machine that is not
    // this one (an RDP session on another computer), where nothing can be traced live. Above Exit, below the
    // update check: the least used item, and the one nobody should hit by reaching for Exit.
    [Fact]
    public void Report_a_problem_sits_above_exit_and_opens_the_report_window() => StaThread.Run(() =>
    {
        var reports = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            update: null, bar: null, report: () => reports++);

        Assert.Equal(["Manage…", "Check for updates…", "Report a problem…", "Exit"], Headers(menu));

        menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Report a problem…")
            .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, reports);
    });

    // Petre, after a colleague's bar stopped switching workspaces and only a restart cleared it: "i need to be
    // able to restart deskbar". Directly above Exit, as its kin: both end this copy, and this one starts the
    // next. A separate item from Restart Explorer, which fixes the taskbar and takes seconds.
    [Fact]
    public void Restart_sits_directly_above_exit_and_invokes_the_restart() => StaThread.Run(() =>
    {
        var restarts = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            update: null, bar: null, report: () => { }, restart: () => restarts++);

        Assert.Equal(["Manage…", "Check for updates…", "Report a problem…", "Restart Deskbar", "Exit"], Headers(menu));

        menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Restart Deskbar")
            .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, restarts);
    });

    [Fact]
    public void The_bar_toggle_invokes_the_toggle() => StaThread.Run(() =>
    {
        var toggles = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            update: null, bar: ("Minimize bar", () => toggles++));

        menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Minimize bar")
            .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(1, toggles);
    });
}
