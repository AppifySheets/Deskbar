using System.IO;
using CSharpFunctionalExtensions;

namespace Deskbar.App;

// A WORKSPACE'S FOLDER on the row menu (Deskbar task 50b61120). Petre: "Add an option to set the folder for the claude
// workows, which should also run claude and add permissions on that choice", with a screenshot of this menu.
//
// The folder itself is Workspace.Folder (task 6edcc65a): the New workspace dialog asks for it, `create --folder` sets
// it, and a task on the workspace with no session yet starts Claude there first (ClaudeStart.NewTaskFolders). This adds
// the way to set or change it on a workspace that already exists, and makes the choice do what Petre asked of it:
//
//   * "Folder…" while the workspace has none, "Folder: <name>…" once it has one, so the menu says where its tasks start
//     without opening anything. Either opens Windows' folder picker, and a pick goes to the App's
//     ClaudeTaskActions.ChooseWorkspaceFolder, which stores it, trusts it for Claude Code and opens a terminal running
//     `claude` there;
//   * "Clear folder" while one is set, which takes the workspace back to guessing; it trusts or untrusts nothing;
//   * a folder given in the New workspace dialog gets the same trust and terminal (InsertAt, FolderChosen), since that
//     is the same choice made a step earlier.
//
// The header is read from the STORED setting when the menu is built. A lazy menu is kept for the row's life
// (FloatingBar.LazyMenus.cs), but rows are rebuilt on every state change, and setting the folder is one, so the header
// cannot go stale where Petre can see it.
public partial class FloatingBar
{
    /// <summary>
    /// The row menu's "Folder…" (Deskbar task 50b61120): the App's ClaudeTaskActions.ChooseWorkspaceFolder, given the
    /// row's workspace. Unset (the tests), the items are not shown. The App always sets it; a click before its Claude
    /// actions exist does nothing, as "Start Claude on this" does then.
    /// </summary>
    internal Action<Guid>? ChooseClaudeFolder { get; init; }

    /// <summary>
    /// A folder chosen in the New workspace dialog, once the workspace exists (Deskbar task 50b61120): the App's
    /// ClaudeTaskActions.SetUpFolder, which trusts it for Claude Code and opens a terminal running `claude` there. Unset,
    /// nothing more happens than storing it.
    /// </summary>
    internal Action<Guid, string>? FolderChosen { get; init; }

    void AddClaudeFolderItems(Guid workspaceId, Action<string, string, Action> add)
    {
        if (ChooseClaudeFolder is not { } choose) return;
        var folder = manager.State.Workspaces.TryFirst(w => w.Id == workspaceId).Bind(w => Maybe.From(w.Folder));
        add("📁", FolderHeader(folder), () => choose(workspaceId));
        if (folder.HasValue)
            add("✕", "Clear folder", () => Report(manager.SetWorkspaceFolder(workspaceId, null)));
    }

    // "Folder…", or "Folder: Deskbar…" with the folder's last part: the whole path would make the menu as wide as the
    // path, and the last part is what tells one repo from another. A drive's root has no last part, so it shows whole
    // ("E:\"). Underscores are doubled because a menu item's header reads a single "_" as its access key and hides it,
    // so "stacks_contabo" would show as "stackscontabo".
    internal static string FolderHeader(Maybe<string> folder) =>
        folder.Map(f => Path.GetFileName(f.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : f)
            .Map(name => $"Folder: {name.Replace("_", "__")}…")
            .GetValueOrDefault("Folder…");
}
