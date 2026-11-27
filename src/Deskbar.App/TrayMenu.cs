using System.Windows.Controls;

namespace Deskbar.App;

// The right-click menu, deliberately down to two commands.
//
// Petre: "no need for workspace switching on right click", and "right click gives exit and
// manage". Both removals are because the work moved elsewhere rather than because the
// features went away:
//   - switching workspaces is the floating bar's row labels, plus the Win+Ctrl+Tab switcher,
//     so a list of workspaces here was a third way to do the same thing;
//   - "Show floating bar" moved into Manage, next to "Start with Windows". It could not
//     simply be deleted: the bar can hide itself from its own right-click menu, and with no
//     way back a hidden bar would look like data loss. Manage is now one left-click away,
//     which makes it a better home for a persistent setting than a transient menu.
//
// The manager is no longer a parameter at all, which is the real signal that this menu
// stopped being a switcher.
public static class TrayMenu
{
    // `update` is the one item that is not always here: null until a check finds a newer release
    // (#71), at which point App rebuilds this menu with it. The menu only ever says there IS one.
    //
    // `checkNow` is #110, and it overturns a decision recorded here: a permanent "Check for
    // updates…" item was considered and left out, because it would be a button that usually reports
    // nothing on a menu deliberately cut to two commands. Petre asked for it anyway, which settles
    // that, and the objection survives as a requirement rather than as a refusal -- a manual check
    // MUST say what it found, including "nothing", or it is a button that looks broken every time
    // it works. See App.CheckForUpdateNow.
    //
    // Third rather than first: it is the least used of the three, and the two that were here are
    // where the hand already expects them.
    // `bar` is #173: "Minimize bar" while the bar is on screen, "Show bar" while it is standing
    // behind its taskbar button. One item that changes its own label rather than two that take
    // turns being greyed out, because the tray is the channel that must never be a dead end -- if
    // the taskbar button is ever lost (a shell restart, a pin that failed on a Windows build
    // without the desktop COM), this is the way back.
    //
    // `report` is "Report a problem…" (ReportWindow, issue #331): the way people outside the team tell us something
    // broke, without signing in anywhere. It took the place of "Save debug dump" (Petre, about a bar misbehaving in
    // an RDP session on another computer: "let's add debug dump which will give me a file and open window"), whose
    // file now travels inside the report and whose View link still opens it; `deskbar.cmd dump` still writes it
    // alone. Above Exit, as a rarely used item, and kept off the line Exit sits on so nobody reaching for Exit
    // lands on it.
    //
    // `restart` is "Restart Deskbar" (see App.RestartApp): Petre, after a colleague's bar stopped switching
    // workspaces and a restart cleared it, "i need to be able to restart deskbar". Directly above Exit, as its
    // kin: both end this copy, and this one starts the next. It is the one item a mis-reach for Exit may land
    // on, and that costs a few seconds of bar, not the app. Separate from the bar's Restart Explorer on purpose:
    // that one takes seconds, closes folders and fixes the taskbar, none of which a stale Deskbar needs.
    public static ContextMenu Build(bool compatibilityMode, Action openManage, Action exit, Action checkNow,
        (string Label, Action Open)? update = null, (string Label, Action Toggle)? bar = null, Action? report = null,
        Action? restart = null, TestBuildItems? testBuild = null)
    {
        var menu = new ContextMenu();

        // A TEST BUILD says so first (Deskbar task 7dd83c89; TestBuild in Core has the story): a copy a session
        // started from its worktree updates nothing, and nothing on screen used to tell it apart from a release.
        // The marking is disabled, like the compatibility line below, because it is a fact, not a command; the
        // restart under it starts the newest installed release, which then updates itself the ordinary way.
        if (testBuild is { } test)
        {
            AddTestBuildItems(menu, test, (glyph, header) => new MenuItem { Header = $"{glyph} {header}" });
            menu.Items.Add(new Separator());
        }

        // Informational, not a command, and disabled: it exists so the user is not left
        // wondering why nothing moves between desktops on a Windows build whose COM API we
        // cannot drive.
        if (compatibilityMode)
            menu.Items.Add(new MenuItem
            {
                Header = "⚠ Virtual desktops unavailable on this Windows build",
                IsEnabled = false,
            });

        // FIRST, and separated, because it is news rather than a command: it appears once and
        // stops being there again as soon as the user is on the new version.
        if (update is { } available)
        {
            var item = new MenuItem { Header = available.Label };
            item.Click += (_, _) => available.Open();
            menu.Items.Add(item);
            menu.Items.Add(new Separator());
        }

        var manage = new MenuItem { Header = "Manage…" };
        manage.Click += (_, _) => openManage();
        menu.Items.Add(manage);

        // Directly under Manage, above the update check: it acts on the surface Petre looks at all
        // day, which puts it closer in kind to Manage than to anything below it.
        if (bar is { } toggle)
        {
            var item = new MenuItem { Header = toggle.Label };
            item.Click += (_, _) => toggle.Toggle();
            menu.Items.Add(item);
        }

        var check = new MenuItem { Header = "Check for updates…" };
        check.Click += (_, _) => checkNow();
        menu.Items.Add(check);

        if (report is { } open)
        {
            var item = new MenuItem { Header = "Report a problem…" };
            item.Click += (_, _) => open();
            menu.Items.Add(item);
        }

        if (restart is { } again)
        {
            var item = new MenuItem { Header = "Restart Deskbar" };
            item.Click += (_, _) => again();
            menu.Items.Add(item);
        }

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(exitItem);

        return menu;
    }

    // The two test-build lines, shared by the tray menu and the bar's background menu (FloatingBar.FillBarMenu) so
    // the two cannot word them differently. `item` makes a menu item in the caller's style: the tray puts the glyph
    // in the header, the bar in its icon column.
    public static void AddTestBuildItems(ItemsControl menu, TestBuildItems test, Func<string, string, MenuItem> item)
    {
        var marking = item("⚠", test.Marking);
        marking.IsEnabled = false;
        menu.Items.Add(marking);
        if (test.Regular is not { } regular) return;
        var restart = item("⟳", regular.Label);
        restart.Click += (_, _) => regular.Restart();
        menu.Items.Add(restart);
    }
}

// What a test build shows at the top of its menus: the marking ("Test build 2.6.208 (add-box-skills), gets no updates")
// and, when an installed release exists, the item that restarts into it ("Restart into Deskbar 2.6.58"). Regular is
// null when no release is installed: the marking still shows, with nothing to restart into.
public sealed record TestBuildItems(string Marking, (string Label, Action Restart)? Regular);
