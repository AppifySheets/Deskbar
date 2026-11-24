using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// PASTING AND DROPPING PICTURES into a task's comment box and its description editor.
// Design: docs/superpowers/specs/2026-11-24-task-images-design.md, PR 1 ("Comment box", "Description editor").
//
// Ctrl+V with a picture or image files on the clipboard, or a drop of either on the box, adds PENDING pictures: held in
// memory (TaskImageFiles.Pending), shown as small thumbnails under the box, each with an x that takes it out again. Ctrl+V
// with only text is left to the text box's own paste. Nothing touches the disk until the comment is posted or the
// description saved; then the pictures are written (TaskImageFiles.Save) and their lines joined after the text
// (TaskImages.Join). A comment of pictures alone is fine. A refused post or edit deletes the files it just wrote.
//
// Pending pictures are the window's, keyed by task like the text drafts (`drafts`), so a redraw while Petre is mid-draft
// (a session changing its status line) keeps them. The editor's are kept apart: the pictures already in the description
// (kept, by relative path) and the ones pasted since (new), both dropped when the editor closes without saving.
public partial class TasksWindow
{
    /// <summary>The Tag of a pending or kept picture's x, for the tests.</summary>
    internal const string RemovePictureTag = "task-image-remove";

    // The height of a pending picture's thumbnail under a box, in DIP.
    const double PendingHeight = 48;

    readonly Dictionary<Guid, List<TaskImageFiles.Pending>> pendingImages = [];
    readonly Dictionary<Guid, Panel> pendingRows = [];

    // The open editor's pictures: those the description already has, and those pasted into it since it opened.
    readonly List<string> editorKept = [];
    readonly List<TaskImageFiles.Pending> editorNew = [];
    Panel? editorRow;

    /// <summary>For the tests: the pictures waiting in a task's comment box.</summary>
    internal IReadOnlyList<TaskImageFiles.Pending> PendingImages(Guid id) => pendingImages.GetValueOrDefault(id) ?? [];

    /// <summary>
    /// A paste or drop on a task's comment box: true when it held pictures, which are added to the box's pending ones;
    /// false when it held none (text alone), so the box pastes it as usual.
    /// </summary>
    internal bool PasteImages(Guid id, IDataObject data) => Took(TaskImageFiles.FromData(data), taken =>
    {
        if (!pendingImages.TryGetValue(id, out var list)) pendingImages[id] = list = [];
        list.AddRange(taken);
        RefreshPending(id);
    });

    /// <summary>A paste or drop on the description editor: the same, into the editor's new pictures.</summary>
    internal bool PasteIntoDescription(IDataObject data) => Took(TaskImageFiles.FromData(data), taken =>
    {
        editorNew.AddRange(taken);
        RefreshEditorRow();
    });

    /// <summary>For the tests: Esc in the editor, which closes it and keeps nothing.</summary>
    internal void CancelEditor() => EndRename();

    // The pictures read from a paste: the ones that could be read go to `take`; one that could not is traced, not shown as
    // an error, since the rest of the paste still counts. True whenever the paste held pictures at all.
    static bool Took(IReadOnlyList<Result<TaskImageFiles.Pending>> read, Action<IReadOnlyList<TaskImageFiles.Pending>> take)
    {
        if (read.Count == 0) return false;
        read.Where(r => r.IsFailure).ToList().ForEach(r => ClickTrace.Write($"tasks window: a picture was not taken: {r.Error}"));
        var good = read.Where(r => r.IsSuccess).Select(r => r.Value).ToArray();
        if (good.Length > 0) take(good);
        return true;
    }

    // --- the comment box -----------------------------------------------------------------------------------------------

    // The row of pending pictures under a comment box, kept so a paste can redraw it without redrawing the window.
    Panel PendingRow(Guid id)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        pendingRows[id] = row;
        RefreshPending(id);
        return row;
    }

    void RefreshPending(Guid id)
    {
        if (!pendingRows.TryGetValue(id, out var row)) return;
        var list = PendingImages(id);
        Fill(row, list.Select(p => p.Thumbnail).ToArray(), i =>
        {
            pendingImages[id].RemoveAt(i);
            RefreshPending(id);
        });
    }

    // Ctrl+V and drops on a comment box (see the header). Hooked up by CommentBox.
    void TakePictures(TextBox box, Guid id)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            if (IsPaste(e)) e.Handled = ClipboardData() is { HasValue: true } data && PasteImages(id, data.Value);
        };
        AcceptDrops(box, data => PasteImages(id, data));
    }

    // Posting: the pending pictures are written first, then the comment goes with their lines after the text. A refusal
    // deletes the files again and keeps text and pictures in the box.
    (string Markdown, IReadOnlyList<string> Written)? CommentWithPictures(Guid id, string text)
    {
        var pictures = PendingImages(id);
        if (pictures.Count == 0) return (text, []);
        var saved = TaskImageFiles.Save(StateDir, id, pictures, Now());
        if (saved.IsFailure)
        {
            ClickTrace.Write($"tasks window: a comment's pictures were not saved: {saved.Error}");
            return null;
        }
        return (TaskImages.Join(text, saved.Value), saved.Value);
    }

    void Posted(Guid id)
    {
        pendingImages.Remove(id);
        RefreshPending(id);
    }

    // --- the description editor ----------------------------------------------------------------------------------------

    // Called when the editor opens: the description's pictures become the kept ones, and nothing is pasted yet.
    void StartEditorPictures(string description)
    {
        editorKept.Clear();
        editorKept.AddRange(TaskImages.Split(description).Images);
        editorNew.Clear();
    }

    void EndEditorPictures()
    {
        editorKept.Clear();
        editorNew.Clear();
        editorRow = null;
    }

    // The row under the description box: the kept pictures (thumbnails from their files) then the new ones.
    Panel EditorRow()
    {
        editorRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        RefreshEditorRow();
        return editorRow;
    }

    void RefreshEditorRow()
    {
        if (editorRow is null) return;
        var kept = editorKept.Select(relative => TaskImageFiles.Thumbnail(StateDir, relative, 96) is { HasValue: true } shown ? shown.Value : null).ToArray();
        Fill(editorRow, [.. kept, .. editorNew.Select(p => p.Thumbnail)], i =>
        {
            if (i < editorKept.Count) editorKept.RemoveAt(i);
            else editorNew.RemoveAt(i - editorKept.Count);
            RefreshEditorRow();
        });
    }

    void TakeEditorPictures(TextBox box)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            if (IsPaste(e)) e.Handled = ClipboardData() is { HasValue: true } data && PasteIntoDescription(data.Value);
        };
        AcceptDrops(box, PasteIntoDescription);
    }

    // Saving the editor: the new pictures are written, and the description is its text then every kept and new picture.
    // Null when the pictures could not be written (the editor stays open); the written paths, so a refused edit can delete
    // them again.
    (string Description, IReadOnlyList<string> Written)? DescriptionWithPictures(Guid id, string text)
    {
        var saved = TaskImageFiles.Save(StateDir, id, editorNew, Now());
        if (saved.IsFailure)
        {
            ClickTrace.Write($"tasks window: the description's pictures were not saved: {saved.Error}");
            return null;
        }
        return (TaskImages.Join(text, [.. editorKept, .. saved.Value]), saved.Value);
    }

    // --- shared --------------------------------------------------------------------------------------------------------

    // Pictures written for a post or an edit that was then refused: deleted again, so a refusal leaves nothing behind. A
    // file that will not go (a viewer holding it) is left; it is harmless and goes with its task.
    void ForgetWritten(IReadOnlyList<string> written) =>
        written.Select(relative => TaskImages.Resolve(relative, StateDir)).Where(path => path.HasValue).ToList()
            .ForEach(path => Result.Try(() => File.Delete(path.Value)));

    static bool IsPaste(KeyEventArgs e) =>
        e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control || e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift;

    // The clipboard is shared with every other program and throws while one of them holds it open.
    static Maybe<IDataObject> ClipboardData() =>
        Result.Try(Clipboard.GetDataObject).Map(Maybe.From).GetValueOrDefault(Maybe<IDataObject>.None);

    // A drop of pictures on a box takes them; anything else (text) is left to the box. Preview events, because a text box
    // handles drops itself and would refuse files.
    static void AcceptDrops(TextBox box, Func<IDataObject, bool> take)
    {
        box.AllowDrop = true;
        box.PreviewDragOver += (_, e) =>
        {
            if (!TaskImageFiles.HoldsPictures(e.Data)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        box.PreviewDrop += (_, e) =>
        {
            if (TaskImageFiles.HoldsPictures(e.Data)) e.Handled = take(e.Data);
        };
    }

    // A row of thumbnails, each with an x calling `remove` with its place. A picture that cannot be shown (a kept one whose
    // file is gone) is a small dim box, still with its x, so it can be taken out.
    void Fill(Panel row, IReadOnlyList<ImageSource?> thumbnails, Action<int> remove)
    {
        row.Children.Clear();
        thumbnails.Select((thumbnail, i) => Pending(thumbnail, () => remove(i))).ToList().ForEach(item => row.Children.Add(item));
        row.Visibility = thumbnails.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    FrameworkElement Pending(ImageSource? thumbnail, Action remove)
    {
        var cross = Flat(new TextBlock { Text = "✕", Foreground = Ink, FontSize = 9 }, "Remove picture", remove, new Thickness(3, 0, 3, 1));
        cross.Tag = RemovePictureTag;
        // Not focusable: a click on it must not take the keyboard from the editor, whose losing it means "cancel".
        cross.Focusable = false;
        cross.ToolTip = "Take this picture out";
        cross.HorizontalAlignment = HorizontalAlignment.Right;
        cross.VerticalAlignment = VerticalAlignment.Top;
        cross.Background = Frozen(0xCC, 0x15, 0x16, 0x1A);
        UIElement picture = thumbnail is null
            ? new Border { Width = PendingHeight, Height = PendingHeight, Background = Frozen(0xFF, 0x2A, 0x2C, 0x33) }
            : new Image { Source = thumbnail, Height = PendingHeight, Stretch = Stretch.Uniform };
        return new Border
        {
            Margin = new Thickness(0, 0, 6, 4),
            BorderBrush = ThreadEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new Grid { Children = { picture, cross } },
        };
    }
}
