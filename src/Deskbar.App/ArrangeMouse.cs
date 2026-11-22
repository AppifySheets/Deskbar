using System.Windows;
using System.Windows.Input;

namespace Deskbar.App;

// The two things a row drag in arrange mode asks of the real mouse (FloatingBar.ArrangeDrag.cs): take the capture on the
// press, and say whether the left button is still down when a move arrives.
//
// Behind a record so a test can hand the bar a mouse that is held down and captured, and drive the drag with synthetic
// events without the test machine's own mouse having a say. The app only ever uses Real.
//
// `At`, where the pointer is (Task 12's task line drags read it): a synthetic event's GetPosition reads the real mouse,
// so a test that raises real routed events on a task line says where its pointer is through this. None is the event's
// own position, which is what the app uses.
internal sealed record ArrangeMouse(Func<UIElement, bool> Capture, Func<MouseEventArgs, bool> ButtonDown,
    Func<MouseEventArgs, IInputElement, Point>? At = null)
{
    /// <summary>Where the pointer of <paramref name="e"/> is, relative to <paramref name="to"/>.</summary>
    internal Point Position(MouseEventArgs e, IInputElement to) => At?.Invoke(e, to) ?? e.GetPosition(to);

    // The mouse on the desk: WPF's capture, and the button state the move event carries.
    internal static ArrangeMouse Real { get; } = new(element => element.CaptureMouse(), e => e.LeftButton == MouseButtonState.Pressed);

    // A mouse whose button stays down and whose capture is always taken, for tests.
    internal static ArrangeMouse Held { get; } = new(_ => true, _ => true);
}
