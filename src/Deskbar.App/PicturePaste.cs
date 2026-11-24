using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;

namespace Deskbar.App;

// CTRL+V AND DROPS OF PICTURES ON A TEXT BOX, shared by every box that takes pictures: the tasks panel's comment boxes,
// its description editor and its add box (TasksWindow.Images.cs), and the bar's own add box (FloatingBar.TaskPictures.cs).
// Design: docs/superpowers/specs/2026-11-24-task-images-design.md, PR 1.
//
// Take(box, take) wires both halves to one `take`, which answers whether the paste or drop held pictures. One that held
// none (text alone) is left to the box, so Ctrl+V of words pastes them as it always did.
static class PicturePaste
{
    /// <summary>Ctrl+V (or Shift+Insert) and drops of pictures on <paramref name="box"/> go to <paramref name="take"/>.</summary>
    public static void Take(TextBox box, Func<IDataObject, bool> take)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            if (IsPaste(e)) e.Handled = ClipboardData() is { HasValue: true } data && take(data.Value);
        };
        AcceptDrops(box, take);
    }

    /// <summary>
    /// The pictures read from a paste: the ones that could be read go to <paramref name="take"/>; one that could not is
    /// traced, not shown as an error, since the rest of the paste still counts. True whenever the paste held pictures at all.
    /// </summary>
    public static bool Took(IReadOnlyList<Result<TaskImageFiles.Pending>> read, Action<IReadOnlyList<TaskImageFiles.Pending>> take)
    {
        if (read.Count == 0) return false;
        read.Where(r => r.IsFailure).ToList().ForEach(r => ClickTrace.Write($"pictures: a picture was not taken: {r.Error}"));
        var good = read.Where(r => r.IsSuccess).Select(r => r.Value).ToArray();
        if (good.Length > 0) take(good);
        return true;
    }

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
}
