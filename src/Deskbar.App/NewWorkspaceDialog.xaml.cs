using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.App;

/// <summary>What the New workspace dialog was filled in with: the name, and the folder or null for none.</summary>
public sealed record NewWorkspace(string Name, string? Folder);

// THE NEW WORKSPACE DIALOG WITH A FOLDER (task 6edcc65a). Petre: "New workspace: option to set its folder path; default
// to a single repos folder and offer a subfolder in it". Option A of the mockup
// (https://claude.ai/artifact/3AJj6aKnixm67ps2oSBieU):
//
//  * The folder box follows the name as it is typed, as the subfolder of the repos folder the name points at
//    (WorkspaceFolders.Suggested: one already there whose name matches, or a new one named after the workspace).
//  * Once the folder is edited by hand (typed, picked from the list or browsed to), it stops following.
//  * Its list is the folders already in the repos folder; Browse… opens Windows' folder picker there.
//  * Empty is "no folder", and the workspace then behaves exactly as one made before this.
//  * A folder that does not exist yet is made when OK is pressed, and the hint under the box says so beforehand.
//
// The repos folder itself comes from the caller (AppState.ReposFolder, else a guess from where task sessions ran), and
// creating the workspace stores the chosen folder's parent as the next one (WorkspaceManager.InsertWorkspace).
public partial class NewWorkspaceDialog : Window
{
    readonly string reposFolder;
    readonly IReadOnlyList<string> existing;
    readonly Func<string, bool> nameTaken;

    // True until the folder is edited by hand: while it holds, the name drives the folder.
    bool followsName = true;

    // Set while this code writes the folder box, so that write is not mistaken for the user editing it.
    bool writingFolder;

    NewWorkspaceDialog(string reposFolder, IReadOnlyList<string> existing, Func<string, bool> nameTaken)
    {
        this.reposFolder = reposFolder;
        this.existing = existing;
        this.nameTaken = nameTaken;
        InitializeComponent();
        FolderInput.ItemsSource = existing;
        NameInput.TextChanged += (_, _) => FollowName();
        // An editable ComboBox raises its text box's TextChanged, which bubbles; a pick from the list arrives that way too.
        // Listened to only once the dialog has loaded: applying the box's template copies any Text already set into its
        // text box, which raises TextChanged with nobody typing, and that would end the following before it began.
        Loaded += (_, _) =>
        {
            FolderInput.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => FolderEdited()));
            ShowHint();
        };
        FollowName();
    }

    /// <summary>
    /// Asks for a new workspace's name and folder. <paramref name="reposFolder"/> is where the folder is offered;
    /// <paramref name="nameTaken"/> is whether another workspace has the name, asked before a new folder is made so a
    /// refused name never leaves an empty folder behind. <paramref name="owner"/> keeps the dialog above the topmost
    /// bar, as PromptDialog's does. None when cancelled.
    /// </summary>
    public static Maybe<NewWorkspace> Ask(string reposFolder, Func<string, bool> nameTaken, Window? owner = null)
    {
        var dialog = new NewWorkspaceDialog(reposFolder, SubfoldersOf(reposFolder), nameTaken);
        if (owner is not null) dialog.Owner = owner;
        dialog.NameInput.Focus();
        return dialog.ShowDialog() == true
            ? new NewWorkspace(dialog.NameInput.Text.Trim(), WorkspaceFolders.Normal(dialog.FolderInput.Text))
            : Maybe<NewWorkspace>.None;
    }

    // The folders already in the repos folder, by name, leaving out hidden ones (".git", ".claude" and the like) the way
    // Explorer does. Empty when the folder is missing or cannot be read: the box then offers only the new subfolder.
    static IReadOnlyList<string> SubfoldersOf(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateDirectories()
                    .Where(d => !d.Name.StartsWith('.') && !d.Attributes.HasFlag(FileAttributes.Hidden))
                    .Select(d => d.FullName)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    void FollowName()
    {
        if (followsName) WriteFolder(WorkspaceFolders.Suggested(reposFolder, NameInput.Text, existing).GetValueOrDefault(""));
        ShowHint();
    }

    void FolderEdited()
    {
        if (writingFolder) return;
        followsName = false;
        ForgetStaleSelection();
        ShowHint();
    }

    // Typing over a folder picked from the list leaves that item selected (text search is off), so picking the same item
    // again would change no selection and put nothing back in the box. Once the text no longer is the selected item,
    // the selection is dropped, keeping the typed text whatever the box does with its text on the way.
    void ForgetStaleSelection()
    {
        if (FolderInput.SelectedItem is not string selected || selected == FolderInput.Text) return;
        var typed = FolderInput.Text;
        writingFolder = true;
        FolderInput.SelectedIndex = -1;
        if (FolderInput.Text != typed) FolderInput.Text = typed;
        writingFolder = false;
    }

    void WriteFolder(string folder)
    {
        writingFolder = true;
        FolderInput.Text = folder;
        writingFolder = false;
    }

    // Which of the three the box holds: nothing, a folder already there, or one OK will make. A relative path is named
    // as the mistake it is rather than made somewhere nobody would look.
    void ShowHint() =>
        FolderHint.Text = WorkspaceFolders.Normal(FolderInput.Text) switch
        {
            null => "No folder: Claude sessions start where they would without one.",
            var f when !Path.IsPathFullyQualified(f) => "Give the whole path, starting with the drive.",
            var f when Directory.Exists(f) => "Existing folder",
            // A share's root (\\server\share) has no parent to name, so the path itself is named.
            var f => $"New folder in {Path.GetDirectoryName(f) ?? f}, made when you press OK",
        };

    void OnBrowse(object s, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the workspace's folder",
            InitialDirectory = Directory.Exists(reposFolder) ? reposFolder : "",
        };
        if (picker.ShowDialog(this) != true || picker.FolderName.Length == 0) return;
        followsName = false;
        WriteFolder(picker.FolderName);
        ShowHint();
    }

    // OK needs a name; a folder, when given, must be a whole path, and a new one is made here, before the workspace is
    // created, so a folder that cannot be made (no such drive, no access) is reported with the dialog still open
    // instead of leaving a workspace pointing at nothing.
    void OnOk(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameInput.Text))
        {
            NameInput.Focus();
            return;
        }
        if (nameTaken(NameInput.Text.Trim()))
        {
            MessageBox.Show(this, $"A workspace named '{NameInput.Text.Trim()}' already exists.", "New workspace",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameInput.Focus();
            return;
        }
        var folder = WorkspaceFolders.Normal(FolderInput.Text);
        if (folder is not null && !Path.IsPathFullyQualified(folder))
        {
            FolderInput.Focus();
            return;
        }
        if (folder is not null && !Directory.Exists(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                MessageBox.Show(this, $"Could not make {folder}: {error.Message}", "New workspace",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        DialogResult = true;
    }
}
