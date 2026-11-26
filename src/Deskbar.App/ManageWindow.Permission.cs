using CSharpFunctionalExtensions;
using System.Windows;

namespace Deskbar.App;

// "Answer Claude's permission questions in Deskbar" (AppState.ClaudePermissions; Allow / Deny from a task's page). It
// follows the tasks box the way the resume box does: usable and showing the stored choice while sessions keep tasks,
// greyed and unticked while they do not (AppState.ClaudePermissionsInEffect), greyed first so the untick is not taken as a
// choice. The app adds or removes the PermissionRequest hook from the state pulse a change sends (ClaudeSkill.Sync).
//
// THE NOTE UNDER IT (the PM's ruling Q6: document and warn) says what the box does to Claude Code's settings, where the
// questions show, that the terminal can always answer, and what happens beside a PermissionRequest hook of the user's
// own: Claude Code runs both, and the first answer wins.
public partial class ManageWindow
{
    internal const string ClaudePermissionsWarning =
        "Adds a PermissionRequest hook to ~/.claude/settings.json. A background session's permission questions then show " +
        "on its task's page in the Inbox view, with Allow and Deny; its terminal can still answer them. If you have " +
        "PermissionRequest hooks of your own, Claude Code runs them too and takes whichever answers first.";

    void ShowClaudePermissions()
    {
        ClaudePermissions.IsEnabled = manager.State.ClaudeTasksInEffect;
        ClaudePermissions.IsChecked = manager.State.ClaudePermissionsInEffect;
        ClaudePermissionsNote.Text = ClaudePermissionsWarning;
    }

    // SetClaudePermissions ignores an unchanged value, so ShowClaudePermissions ticking the box writes nothing.
    void OnClaudePermissionsToggled(object s, RoutedEventArgs e)
    {
        if (!ClaudePermissions.IsEnabled) return;
        manager.SetClaudePermissions(ClaudePermissions.IsChecked == true).TapError(Complain);
    }
}
