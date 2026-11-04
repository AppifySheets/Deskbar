using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Quick find's own global chord (spec, "The shortcut"), configurable like the switcher's and never the same
// chord: two features on one RegisterHotKey would leave one of them silently dead.
public class QuickFindShortcutTests
{
    static WorkspaceManager Manager() =>
        new(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());

    [Fact]
    public void An_older_state_file_gets_the_default() =>
        Assert.Equal(AppState.DefaultQuickFindShortcut, Manager().QuickFindShortcut);

    // Measured free on Petre's machine (docs/superpowers/notes/2026-11-04-quick-find-probes.md).
    [Fact]
    public void The_default_is_win_alt_space() =>
        Assert.Equal("Win+Alt+Space", AppState.DefaultQuickFindShortcut);

    [Fact]
    public void A_valid_chord_is_stored_in_canonical_spelling()
    {
        var manager = Manager();
        Assert.True(manager.SetQuickFindShortcut("ctrl + alt + space").IsSuccess);
        Assert.Equal("Ctrl+Alt+Space", manager.QuickFindShortcut);
    }

    [Theory]
    [InlineData("Win+Ctrl+Tab")]        // the switcher's chord
    [InlineData("Win+Ctrl+Shift+Tab")]  // its reverse half, registered separately
    public void Quick_find_shortcut_cannot_reuse_the_switcher_chord(string chord)
    {
        var manager = Manager();
        Assert.True(manager.SetQuickFindShortcut(chord).IsFailure);
        Assert.Equal(AppState.DefaultQuickFindShortcut, manager.QuickFindShortcut);
    }

    [Fact]
    public void The_switcher_cannot_take_the_quick_find_chord()
    {
        var manager = Manager();
        Assert.True(manager.SetSwitcherShortcut(manager.QuickFindShortcut).IsFailure);
        Assert.Equal(AppState.DefaultSwitcherShortcut, manager.SwitcherShortcut);
    }

    [Fact]
    public void Nonsense_is_refused_and_the_default_stands()
    {
        var manager = Manager();
        Assert.True(manager.SetQuickFindShortcut("Ctrl+Bananas").IsFailure);
        Assert.Equal(AppState.DefaultQuickFindShortcut, manager.QuickFindShortcut);
    }
}
