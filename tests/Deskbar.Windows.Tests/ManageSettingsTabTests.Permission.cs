using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// "Answer Claude's permission questions in Deskbar" (AppState.ClaudePermissions; ManageWindow.Permission.cs): off by
// default, greyed and unticked while sessions do not keep tasks, a tick sets it, and the warning the PM asked for (Q6)
// is under it.
public partial class ManageSettingsTabTests
{
    [Fact]
    public void The_permission_box_is_off_by_default_and_sets_the_setting() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);
        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.True(Box(window, "ClaudePermissions").IsEnabled);
        Assert.False(Box(window, "ClaudePermissions").IsChecked);
        Assert.Equal(0, pulses);
        Assert.Contains("PermissionRequest hook", ((TextBlock)window.FindName("ClaudePermissionsNote")!).Text);
        Assert.Contains("whichever answers first", ((TextBlock)window.FindName("ClaudePermissionsNote")!).Text);

        Box(window, "ClaudePermissions").IsChecked = true;
        Assert.True(manager.State.ClaudePermissions);
        window.Close();
    });

    [Fact]
    public void Without_the_task_hooks_the_permission_box_is_greyed_and_unticked() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with { NoClaudeTasks = true, ClaudePermissions = true });
        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.False(Box(window, "ClaudePermissions").IsEnabled);
        Assert.False(Box(window, "ClaudePermissions").IsChecked);
        Assert.True(manager.State.ClaudePermissions);
        window.Close();
    });
}
