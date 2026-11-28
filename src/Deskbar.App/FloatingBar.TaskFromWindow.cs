using System.Windows;
using System.Windows.Controls;
using CSharpFunctionalExtensions;

namespace Deskbar.App;

// A TASK FROM THE WINDOW ON SCREEN, THE BAR'S HALF (Deskbar task 494d34ee; Petre: "add a task from the window open on the
// screen as a separate button near the urgent/important buttons, to start immediately in the current workspace").
//
// A camera glyph in the add row, right after the important and urgent toggles and before the play glyph. One press:
//   1. the window Petre was working in is the one the add box hands the keyboard back to (BeginTyping notes it at the
//      press: the window in front, or the last outside window on this desktop when the press made the bar the front);
//   2. WindowShot takes its picture and the line naming it;
//   3. the box's note is added exactly as the play button adds it (CommitTaskNote with startClaude), with the picture
//      after any pasted ones and the line under any typed lines. Nothing typed makes it a "Picture" task, which the
//      session renames once it has read it (task a662f8c3);
//   4. Claude starts on it in the background with the "Claude started" card (task 7b17a573), in the workspace the chip
//      says, which is the workspace in front unless Petre picked another for this note.
// No window to take (an empty desktop, only the taskbar, a minimised window) is refused in words, through the bar's own
// refusal (Report), and nothing is added: the box keeps what it held.
//
// Shown only while the play button is (PaintTaskChips): it starts Claude, so it needs what that button needs. Unfocusable
// like every task button, so the press never takes the keyboard from the box.
public partial class FloatingBar
{
    internal const string AddFromWindowText = "Add from this window and start Claude";

    // The camera glyph, Segoe's "Camera" (U+E722), in both icon fonts IconFont names.
    const string AddFromWindowGlyph = "";

    Button? taskAddFromWindow;

    /// <summary>For the tests: the add row's camera button.</summary>
    internal Button? TaskAddFromWindowButton => taskAddFromWindow;

    /// <summary>
    /// How the camera takes a window's picture. WindowShot.Of in the app; a seam so the tests never capture a real window.
    /// </summary>
    internal Func<nint, Result<WindowShot.Shot>> ShootWindow { get; init; } = WindowShot.Of;

    /// <summary>
    /// The last window outside Deskbar that had the foreground, while it still exists and is on the desktop in front; 0
    /// otherwise. The tasks panel's camera asks for it (App wires it), since a click in the panel makes the panel the
    /// window in front and the window Petre was in is only known to the bar's foreground hook (languageTarget).
    /// </summary>
    internal nint WindowBeforeDeskbar =>
        languageTarget != 0 && !IsOwnWindow(languageTarget) && WindowAlive(languageTarget) && OnCurrentDesktop(languageTarget)
            ? languageTarget
            : 0;

    Button AddFromWindowButton()
    {
        var glyph = new TextBlock
        {
            Text = AddFromWindowGlyph,
            FontFamily = IconFont,
            FontSize = Text(8.5),
            Foreground = TaskDimInk,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 1, 2, 1),
        };
        var button = TaskButton(AddFromWindowText, glyph, AddFromWindowInFront,
            "Add from this window: takes a picture of the window you were in, adds it as a task and starts Claude on it");
        button.Visibility = Visibility.Collapsed;
        return button;
    }

    // The press. BeginTyping first when the box is idle, as the play button does, so the window noted is the one in front
    // NOW; the box's draft, if any, goes with the picture.
    void AddFromWindowInFront()
    {
        if (taskService is null || taskAddBox is null) return;
        var wasTyping = taskTyping;
        if (!taskTyping) BeginTyping();
        var window = taskReturnTo != 0 ? taskReturnTo : WindowBeforeDeskbar;
        var shot = ShootWindow(window);
        ClickTrace.Write(shot.IsSuccess
            ? $"task list: picture of 0x{window:X} for a new task"
            : $"task list: no picture of 0x{window:X}: {shot.Error}");
        if (shot.IsSuccess)
        {
            CommitTaskNote(startClaude: true, shot: shot.Value);
            return;
        }
        // Refused: said first (a modal box owned by the bar), then an idle, empty box the press opened goes back to idle
        // and hands the keyboard back, so the refusal leaves things as they were; a box Petre was already typing in
        // keeps its draft and the keyboard.
        Report(Result.Failure(shot.Error));
        if (!wasTyping && taskAddBox.Text.Length == 0 && taskAddImages.Count == 0) EndTyping(clear: true);
    }
}
