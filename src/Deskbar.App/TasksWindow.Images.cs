using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    internal bool PasteImages(Guid id, IDataObject data) => PicturePaste.Took(TaskImageFiles.FromData(data), taken =>
    {
        if (!pendingImages.TryGetValue(id, out var list)) pendingImages[id] = list = [];
        list.AddRange(taken);
        RefreshPending(id);
    });

    /// <summary>A paste or drop on the description editor: the same, into the editor's new pictures.</summary>
    internal bool PasteIntoDescription(IDataObject data) => PicturePaste.Took(TaskImageFiles.FromData(data), taken =>
    {
        editorNew.AddRange(taken);
        RefreshEditorRow();
    });

    /// <summary>For the tests: Esc in the editor, which closes it and keeps nothing.</summary>
    internal void CancelEditor() => EndRename();

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
        PicturePaste.Take(box, data => PasteImages(id, data));
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
        PicturePaste.Take(box, PasteIntoDescription);
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

    // --- the add box ---------------------------------------------------------------------------------------------------
    //
    // A NEW TASK WITH PICTURES (task 5cc9f1d4, Petre: "add ability to paste an image into a new task ... as i can in
    // comments; also let me add just the image without a title too"). Ctrl+V or a drop on the add box takes pictures the
    // way a comment box does: pending, as thumbnails under the box, each with its x. Enter adds the task with the typed
    // words as its title and the pictures as its DESCRIPTION, the task's own "what this is", which is where a new task's
    // screenshot belongs and what a session started on it reads first (the briefing). With no words, the title is
    // "Picture" (TaskImages.TitleFor). The files are filed under the new task's id, which only exists once the board makes
    // the task, so the add hands TaskService a `describe` that writes them for that id, and the task and its description
    // are one save (TaskService.AddNote). Esc, or the box closing, drops the pending pictures; nothing was written yet.

    readonly List<TaskImageFiles.Pending> addImages = [];

    /// <summary>For the tests: the pictures waiting in the add box.</summary>
    internal IReadOnlyList<TaskImageFiles.Pending> AddBoxImages => addImages;

    /// <summary>A paste or drop on the add box: true when it held pictures, which wait under the box; false for text alone.</summary>
    internal bool PasteIntoAdd(IDataObject data) => PicturePaste.Took(TaskImageFiles.FromData(data), taken =>
    {
        addImages.AddRange(taken);
        RefreshAddPictures();
    });

    // Hooked up once, by the constructor: the add box is built by the XAML and lives as long as the window.
    void TakeAddPictures() => PicturePaste.Take(AddBox, PasteIntoAdd);

    void RefreshAddPictures() =>
        Fill(AddPictures, addImages.Select(p => p.Thumbnail).ToArray(), i =>
        {
            addImages.RemoveAt(i);
            RefreshAddPictures();
        });

    // The box closed (Enter added the task, or Esc): the pictures go with it.
    void ClearAddPictures()
    {
        addImages.Clear();
        RefreshAddPictures();
    }

    // What the add hands TaskService.AddNote: none for a one-line note without pictures (a plain note, as before), else
    // the writer of the new task's description: the lines typed after the title (`body`, TaskNote.Split), then the
    // pending pictures saved under the new task's id. A copy of the list, so a paste landing mid-add cannot change what
    // is written. A window's `shot` (the camera button, task 494d34ee) adds its line after the typed lines and its picture
    // after the pending ones.
    Func<Guid, Result<string>>? AddDescription(string body, WindowShot.Shot? shot = null)
    {
        var text = shot is null ? body : WindowShot.WithCaption(body, shot.Caption);
        TaskImageFiles.Pending[] pictures = shot is null ? [.. addImages] : [.. addImages, shot.Picture];
        if (pictures.Length == 0 && text.Length == 0) return null;
        if (pictures.Length == 0) return _ => Result.Success(text);
        return id => TaskImageFiles.Save(StateDir, id, pictures, Now()).Map(saved => TaskImages.Join(text, saved));
    }

    // --- shared --------------------------------------------------------------------------------------------------------

    // Pictures written for a post or an edit that was then refused: deleted again, so a refusal leaves nothing behind. A
    // file that will not go (a viewer holding it) is left; it is harmless and goes with its task.
    void ForgetWritten(IReadOnlyList<string> written) =>
        written.Select(relative => TaskImages.Resolve(relative, StateDir)).Where(path => path.HasValue).ToList()
            .ForEach(path => Result.Try(() => File.Delete(path.Value)));

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
