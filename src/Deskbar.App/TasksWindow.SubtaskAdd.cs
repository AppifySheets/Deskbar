using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// ADDING A SUBTASK IN PLACE (task 0eb943d8; Petre, 2026-11-25: "when adding a subtask, a new textbox appears at the top of
// the bar; make it right there, at the add subtask button", then "and then make that same space open and show the
// subtask").
//
// "Add subtask" in an open thread, and "Add subtask…" on a line's menu, used to open the add box at the top of the panel
// with the parent as a chip, far from where Petre clicked. Now the thread's "Add subtask" button opens a box right under
// the row of buttons it sits in at the thread's top (task 1c17b7d6), and leaves that row while the box is open (the menu
// opens the line's thread first, as "Related to…" does for its picker):
//
//   * Enter adds what is typed as a subtask (TasksBoard.AddSubtaskInThread): the first line is the title, the lines after
//     it the description (TaskNote.Split, as in the add box); Shift+Enter starts a new line; pictures pasted or dropped on
//     the box go with it, as in the add box. Under the line's task, or under its parent when the line is a subtask itself
//     (subtasks are one level deep), on the parent's workspace.
//   * Then the thread the box was in closes and the new subtask's own thread opens. A subtask is drawn right under its
//     parent, so it opens in the space the thread took: that is "that same space open and show the subtask". It is
//     picked, glows and is scrolled to, as every task just added is.
//   * Esc closes the box (TasksWindow.Escape, before it folds the thread). A refused add (an empty title) keeps the box
//     open with its text, and the problem line says why.
//
// The box survives a redraw the way the link picker does (TasksWindow.Links.cs): its text and pictures are kept here, and
// a box that had the keyboard gets it back with the caret where it was. A box whose thread is no longer drawn (the thread
// folded, the task gone) is closed, so it does not come back unasked the next time that thread opens.
public partial class TasksWindow
{
    /// <summary>The placeholder of the box in a thread.</summary>
    internal const string SubtaskBoxPlaceholder = "A subtask: Enter adds it, Esc closes";

    // The line whose thread shows the box, or null while no box is open.
    Guid? subtaskUnder;

    // What is typed and pasted into the box, kept across redraws.
    string subtaskTyped = "";
    readonly List<TaskImageFiles.Pending> subtaskImages = [];

    // The box and its row of pictures as last drawn, and whether the box takes the keyboard after the next draw (it was
    // just opened, or it had the keyboard when the draw began) and where its caret goes then.
    TextBox? subtaskBox;
    Panel? subtaskPictures;
    bool focusSubtaskBox;
    int subtaskCaret;

    /// <summary>For the tests: the open box in a thread, if any.</summary>
    internal TextBox? SubtaskBoxShown => subtaskBox;

    /// <summary>For the tests: the pictures waiting in the box in a thread.</summary>
    internal IReadOnlyList<TaskImageFiles.Pending> SubtaskBoxImages => subtaskImages;

    /// <summary>
    /// "Add subtask" in the thread of the line <paramref name="lineId"/>, or "Add subtask…" on its menu: the box opens where
    /// the button is, the thread opening first when it is closed (ShowTask), and takes the keyboard once drawn. Internal
    /// for the tests.
    /// </summary>
    internal void BeginSubtaskAdd(Guid lineId)
    {
        if (Actions?.SubtaskParent?.Invoke(lineId) is null) return;
        subtaskUnder = lineId;
        subtaskTyped = "";
        subtaskImages.Clear();
        focusSubtaskBox = true;
        subtaskCaret = 0;
        var row = RowFor(lineId);
        // A subtask's line in its parent's thread (task 249a1e8a) opens its thread in place, as its click does: showing it
        // would pick it, and a picked subtask leaves the thread for a line of its own.
        if (row is { Expanded: false } && InParentThread(lineId)) Actions?.ToggleThread(lineId);
        else if (row is { Expanded: false } && Actions?.ShowTask is { } show) show(lineId);
        else if (latest is not null) Draw(latest);
    }

    /// <summary>Closes the box (Esc, or the subtask added) and redraws with the "Add subtask" button back.</summary>
    internal void EndSubtaskAdd()
    {
        ClearSubtaskBox();
        if (latest is not null) Present(latest);
    }

    void ClearSubtaskBox()
    {
        subtaskUnder = null;
        subtaskTyped = "";
        subtaskImages.Clear();
        subtaskBox = null;
        subtaskPictures = null;
        focusSubtaskBox = false;
    }

    // Called by Draw before the rows are rebuilt: whether the old box had the keyboard decides whether the new one takes
    // it, and where its caret was.
    void ForgetSubtaskBox()
    {
        if (subtaskBox?.IsKeyboardFocusWithin == true)
        {
            focusSubtaskBox = true;
            subtaskCaret = subtaskBox.CaretIndex;
        }
        subtaskBox = null;
        subtaskPictures = null;
    }

    // Called by Draw after the rows are rebuilt. A box that was not drawn again has lost its thread: it is closed.
    void RefocusSubtaskBox()
    {
        if (subtaskUnder is not null && subtaskBox is null)
        {
            ClearSubtaskBox();
            return;
        }
        if (subtaskBox is null || !focusSubtaskBox) return;
        focusSubtaskBox = false;
        subtaskBox.Focus();
        subtaskBox.CaretIndex = Math.Min(subtaskCaret, subtaskBox.Text.Length);
    }

    // The box, in the look of the thread's comment box, with its pictures under it.
    UIElement SubtaskBox(Guid lineId)
    {
        var box = new TextBox
        {
            Text = subtaskTyped,
            Template = (ControlTemplate)FindResource("BareTextBox"),
            Background = Brushes.Transparent,
            Foreground = Ink,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            // As in the add box: the box makes Shift+Enter's line break itself, and a pasted title with its description
            // keeps every line (with AcceptsReturn off WPF cuts a paste at its first line break). Plain Enter is taken
            // first by PreviewKeyDown below, so it adds rather than breaks the line.
            AcceptsReturn = true,
            MaxHeight = 120,
            ToolTip = "Enter adds the subtask, Shift+Enter starts a new line (the lines after the first are its description). Paste or drop images.",
        };
        AutomationProperties.SetName(box, "New subtask");
        var placeholder = new TextBlock
        {
            Text = SubtaskBoxPlaceholder,
            FontSize = 12,
            Foreground = HeaderInk,
            IsHitTestVisible = false,
            Margin = new Thickness(2, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) =>
        {
            subtaskTyped = box.Text;
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        box.PreviewKeyDown += (_, e) => e.Handled = PressInSubtaskBox(lineId, e.Key, Keyboard.Modifiers) != KeyOutcome.Ignored;
        // Ctrl+V and drops of pictures, as on the add box. After the handler above, which leaves every key but Enter alone.
        PicturePaste.Take(box, PasteIntoSubtaskBox);
        subtaskBox = box;
        var pictures = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        subtaskPictures = pictures;
        RefreshSubtaskPictures();
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
                    BorderBrush = ThreadEdge,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = new Grid { Children = { box, placeholder } },
                },
                pictures,
            },
        };
    }

    /// <summary>A paste or drop on the box in a thread: true when it held pictures, which wait in the box.</summary>
    internal bool PasteIntoSubtaskBox(IDataObject data) => PicturePaste.Took(TaskImageFiles.FromData(data), taken =>
    {
        subtaskImages.AddRange(taken);
        RefreshSubtaskPictures();
    });

    void RefreshSubtaskPictures()
    {
        if (subtaskPictures is null) return;
        Fill(subtaskPictures, subtaskImages.Select(p => p.Thumbnail).ToArray(), i =>
        {
            subtaskImages.RemoveAt(i);
            RefreshSubtaskPictures();
        });
    }

    // A key in the box, as one method the tests drive without a real key event. Esc is the window's (Escape). Shift+Enter
    // is left to the box, which breaks the line itself (AcceptsReturn).
    KeyOutcome PressInSubtaskBox(Guid lineId, Key key, ModifierKeys modifiers) =>
        key is not (Key.Enter or Key.Return) || modifiers.HasFlag(ModifierKeys.Shift) ? KeyOutcome.Ignored
        : AddSubtaskFromBox(lineId) ? KeyOutcome.Posted
        : KeyOutcome.NewLine;

    // Enter: the subtask is added and opened where the thread was (TasksBoard.AddSubtaskInThread). The box is closed FIRST,
    // so the redraws the add causes draw the new subtask's thread rather than the box; a refusal puts the box back with
    // its text and pictures, and the problem line says why.
    bool AddSubtaskFromBox(Guid lineId)
    {
        if (Actions?.AddSubtaskHere is not { } add) return false;
        var text = subtaskTyped;
        var pictures = subtaskImages.ToArray();
        var (title, body) = TaskNote.Split(text, pictures.Length);
        Func<Guid, Result<string>>? describe =
            pictures.Length == 0 && body.Length == 0 ? null
            : pictures.Length == 0 ? _ => Result.Success(body)
            : id => TaskImageFiles.Save(StateDir, id, pictures, Now()).Map(saved => TaskImages.Join(body, saved));
        ClearSubtaskBox();
        if (add(lineId, title, describe, InParentThread(lineId))) return true;
        subtaskUnder = lineId;
        subtaskTyped = text;
        subtaskImages.AddRange(pictures);
        focusSubtaskBox = true;
        subtaskCaret = text.Length;
        // A refusal that redrew nothing leaves the signature as it was with the box open, so Draw would see no change and
        // keep the old rows, whose box this window no longer holds (subtaskBox was cleared above). Forgetting the last
        // signature makes the draw rebuild them, box included.
        drawn = "";
        if (latest is not null) Draw(latest);
        return false;
    }

    /// <summary>For the tests: types into the box in a thread and presses a key; whether a subtask was added.</summary>
    internal bool TypeSubtask(string text, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (subtaskBox is null || subtaskUnder is not { } lineId) return false;
        subtaskBox.Text = text;
        return PressInSubtaskBox(lineId, key, modifiers) == KeyOutcome.Posted;
    }
}
