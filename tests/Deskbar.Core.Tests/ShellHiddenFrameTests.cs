using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// The bar showed an Unplaced row holding a "Settings" icon, with an empty preview, long after Settings was
// closed. Measured while it was on screen:
//
//   one top-level ApplicationFrameWindow "Settings", owned by ApplicationFrameHost
//   visible, not minimised, DWMWA_CLOAKED = 2 (DWM_CLOAKED_SHELL)
//   IVirtualDesktopManager.GetWindowDesktopId: S_OK with an all-zero id (on no desktop)
//   no Windows.UI.Core.CoreWindow child, so no app inside it
//
// Closing a Store app leaves its frame behind like that, and Windows' own taskbar and Alt+Tab skip it. A
// window on ANOTHER desktop is cloaked the same way but still has a desktop, so the pair "cloaked by the
// shell and on no desktop" is the leftover alone. These pin the rule: such a frame is left out of the
// overview without being dropped from the window list, so it is back the moment the app reopens.
public class ShellHiddenFrameTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static readonly Workspace Messengers = new(Guid.NewGuid(), "Messengers", Guid.NewGuid());
    static readonly Workspace Work = new(Guid.NewGuid(), "Work", Guid.NewGuid());

    static readonly WindowInfo Settings =
        new(new WindowHandle(0x5E77), 31, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe", "Settings", null);

    WorkspaceManager Started()
    {
        new[] { Messengers, Work }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = Messengers.DesktopId!.Value;
        store.Stored = AppState.Empty with { Workspaces = [Messengers, Work] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // The window opens while Deskbar watches, which is how the frame entered the list in the first place.
    void Opens(WindowInfo window) => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));

    // What closing the app does to its frame: the shell cloaks it and the OS stops reporting a desktop.
    void AppCloses(WindowHandle frame)
    {
        monitor.ShellCloaked.Add(frame);
        desktops.WindowPlacements.Remove(frame);
    }

    // What reopening does: the same frame gets a desktop back and is no longer cloaked by the shell.
    void AppReopens(WindowHandle frame, Workspace on)
    {
        monitor.ShellCloaked.Remove(frame);
        desktops.WindowPlacements[frame] = on.DesktopId!.Value;
    }

    // Every place the overview shows a window: pinned row, workspace rows, other desktops and Unplaced.
    static IReadOnlyList<string> RowsOf(Core.Overview.Overview overview, WindowHandle window) =>
        overview.Pinned.Where(r => r.Window.Handle == window).Select(_ => "Pinned")
            .Concat(overview.Workspaces.Where(w => w.Running.Any(r => r.Window.Handle == window)).Select(w => w.Workspace.Name))
            .Concat(overview.OtherDesktops.Where(d => d.Windows.Any(r => r.Window.Handle == window)).Select(d => d.Name))
            .ToList();

    [Fact]
    public void A_frame_the_shell_cloaks_with_no_desktop_is_not_in_the_overview()
    {
        var manager = Started();
        Opens(Settings);
        AppCloses(Settings.Handle);

        Assert.Empty(RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));
    }

    // Hidden, not dropped: nothing tells the manager the app reopened except the frame answering again, so
    // the frame has to still be in the list when it does.
    [Fact]
    public void The_frame_is_shown_in_its_desktops_row_again_when_the_app_reopens()
    {
        var manager = Started();
        Opens(Settings);
        AppCloses(Settings.Handle);
        Assert.Empty(RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));

        AppReopens(Settings.Handle, Work);

        Assert.Equal(["Work"], RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));
    }

    // The case the memory of "where this window last was" would otherwise get wrong: the app was open on a
    // desktop while Deskbar watched, and closing it must take the frame out of that row, not leave it there.
    [Fact]
    public void A_frame_seen_on_a_desktop_leaves_that_row_when_the_app_closes()
    {
        var manager = Started();
        desktops.WindowPlacements[Settings.Handle] = Messengers.DesktopId!.Value;
        Opens(Settings);
        Assert.Equal(["Messengers"], RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));

        AppCloses(Settings.Handle);

        Assert.Empty(RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));
    }

    // Windows on other desktops are cloaked by the shell too; what keeps them in their rows is that the OS
    // still gives them a desktop.
    [Fact]
    public void A_window_on_another_desktop_stays_in_its_row_though_the_shell_cloaks_it()
    {
        var manager = Started();
        desktops.WindowPlacements[Settings.Handle] = Work.DesktopId!.Value; // current desktop is Messengers
        monitor.ShellCloaked.Add(Settings.Handle);
        Opens(Settings);

        Assert.Equal(["Work"], RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));
    }

    // Any other reason for a missing desktop is untouched: this is the "Windows Input Experience" case the
    // Unplaced row was written for, and the cloak is what the new rule needs, so without it nothing changes.
    [Fact]
    public void A_window_with_no_desktop_that_the_shell_has_not_cloaked_is_still_unplaced()
    {
        var manager = Started();
        Opens(Settings);

        Assert.Equal(["Unplaced"], RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));
    }

    // Pinned windows are not asked for a desktop at all, so a cloak cannot hide one.
    [Fact]
    public void A_pinned_window_is_still_in_the_pinned_row()
    {
        var manager = Started();
        desktops.PinnedWindows.Add(Settings.Handle);
        monitor.ShellCloaked.Add(Settings.Handle);
        Opens(Settings);

        Assert.Equal(["Pinned"], RowsOf(manager.WindowsByWorkspace().Value, Settings.Handle));
    }
}
