using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// PICTURES IN THE BAR'S ADD BOX (task 5cc9f1d4; Petre, pointing at the bar's "Add to Deskbar..." box: "can i paste an
// image here?"). The tasks panel's add box took pictures first (TasksWindow.Images.cs, "the add box"); this is the same
// for the box at the top of the bar's task list, with the same rules:
//
//   - Ctrl+V or a drop of pictures on the box (PicturePaste) adds them as PENDING pictures, held in memory, shown as
//     small thumbnails in a row between the box and its chips, each with an x that takes it out again. Ctrl+V of text
//     alone pastes the text as before.
//   - Enter adds the task: the typed words are its title (only the first line; later lines open the description, see
//     TaskNote), the pictures its description after any such lines, written under the new task's
//     id inside the same save (TaskService.AddNote's `describe`). Nothing typed: the task is "Picture" (TaskImages.TitleFor).
//   - Esc, or an add, empties the box and drops the pictures (EndTyping with clear). A click elsewhere keeps them, as it
//     keeps the text: the box is the draft (Ruling 9).
//
// The thumbnails are small (TaskPictureHeight), because the bar is compact, and the row only exists while it holds a
// picture. It sits in the footer, so the docked fit pass (FitTaskListToFreeHeight) has to measure again whenever it
// appears or goes: PaintTaskAddPictures queues that pass.
public partial class FloatingBar
{
    /// <summary>The Tag of a pending picture's x in the bar's add box, for the tests.</summary>
    internal const string TaskPictureRemoveTag = "task-add-picture-remove";

    // A pending picture's thumbnail height, in DIP: smaller than the tasks panel's 48, the bar being compact.
    const double TaskPictureHeight = 28;

    /// <summary>
    /// The state folder pasted pictures are written under (task-images\&lt;task id&gt;\), as the tasks panel's StateDir is.
    /// The tests hand in a scratch folder, so nothing they paste lands in the real profile.
    /// </summary>
    internal string TaskStateDir { get; init; } = AppPaths.NewStateDir;

    readonly List<TaskImageFiles.Pending> taskAddImages = [];
    WrapPanel? taskAddPictures;

    /// <summary>For the tests: the pictures waiting in the bar's add box.</summary>
    internal IReadOnlyList<TaskImageFiles.Pending> TaskAddImages => taskAddImages;

    /// <summary>
    /// A paste or drop on the bar's add box: true when it held pictures, which wait under the box; false for text alone.
    /// A drop can land while the box is idle, so it takes the keyboard first, as a click in the box does, for Enter to add.
    /// </summary>
    internal bool PasteIntoTaskAdd(IDataObject data) => PicturePaste.Took(TaskImageFiles.FromData(data), taken =>
    {
        if (!taskTyping) BeginTyping();
        taskAddImages.AddRange(taken);
        PaintTaskAddPictures();
    });

    // Built once by BuildTaskList: the row under the box, and the box's Ctrl+V and drops.
    WrapPanel TaskAddPicturesRow(TextBox box)
    {
        PicturePaste.Take(box, PasteIntoTaskAdd);
        taskAddPictures = new WrapPanel { Tag = "task-add-pictures", Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
        return taskAddPictures;
    }

    void PaintTaskAddPictures()
    {
        if (taskAddPictures is null) return;
        taskAddPictures.Children.Clear();
        taskAddImages.Select((picture, i) => TaskPictureTile(picture.Thumbnail, () =>
        {
            taskAddImages.RemoveAt(i);
            PaintTaskAddPictures();
            taskAddBox?.Focus();
        })).ToList().ForEach(tile => taskAddPictures.Children.Add(tile));
        var shown = taskAddImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (taskAddPictures.Visibility == shown) return;
        taskAddPictures.Visibility = shown;
        // The footer just grew or shrank by the row: the docked bar's lines are counted again.
        QueueTaskListFit();
    }

    // Enter added the task, or Esc emptied the box: the pictures go with it.
    void ClearTaskAddPictures()
    {
        taskAddImages.Clear();
        PaintTaskAddPictures();
    }

    // What the add hands TaskService.AddNote: none for a one-line note without pictures (a plain note, as before), else
    // the writer of the new task's description: the lines typed after the title (`body`, TaskNote.Split), then the
    // pictures saved under the new task's id. A copy, so a paste landing mid-add cannot change what is written. A window's
    // `shot` (the camera button, task 494d34ee) adds its line after the typed lines and its picture after the pasted ones.
    Func<Guid, Result<string>>? TaskAddDescription(string body, WindowShot.Shot? shot = null)
    {
        var text = shot is null ? body : WindowShot.WithCaption(body, shot.Caption);
        TaskImageFiles.Pending[] pictures = shot is null ? [.. taskAddImages] : [.. taskAddImages, shot.Picture];
        if (pictures.Length == 0 && text.Length == 0) return null;
        if (pictures.Length == 0) return _ => Result.Success(text);
        return id => TaskImageFiles.Save(TaskStateDir, id, pictures, TaskNow()).Map(saved => TaskImages.Join(text, saved));
    }

    // One pending picture: its thumbnail with a small x over the top right corner. The x is a task button, so it never
    // takes the keyboard from the box (TaskButton is not focusable).
    FrameworkElement TaskPictureTile(ImageSource thumbnail, Action remove)
    {
        var cross = TaskButton("Remove picture", new TextBlock { Text = "✕", Foreground = TaskInk, FontSize = 8, Margin = new Thickness(2, 0, 2, 1) },
            remove, "Take this picture out");
        cross.Tag = TaskPictureRemoveTag;
        cross.HorizontalAlignment = HorizontalAlignment.Right;
        cross.VerticalAlignment = VerticalAlignment.Top;
        cross.Background = TaskFieldFill;
        return new Border
        {
            Margin = new Thickness(0, 0, 4, 0),
            BorderBrush = TaskTickEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = new Grid { Children = { new Image { Source = thumbnail, Height = TaskPictureHeight, Stretch = Stretch.Uniform }, cross } },
        };
    }
}
