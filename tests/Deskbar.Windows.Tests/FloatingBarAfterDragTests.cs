using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// #48, sixth visit, from Petre's own trace: a drag of RemoteDesktopManager onto GPH at 11:04:27, and at
// 11:06:49 a click on the Deskbar row that did nothing:
//
//   row-up group=Deskbar consumedByChild=False ourPress=True afterDrag=True orphan=False
//
// afterDrag=True with no drag anywhere near it. A finished drag sets dragJustFinished so the stray
// release it leaves behind is refused, and only a row's release handler cleared it. That drag's release
// never reached a row, so the flag sat there for 2m22s and ate the next real click.
//
// A real OLE drag cannot run in a test (DoDragDrop is a modal loop on the real mouse), so the drag's
// finish is stood in for by setting the one field it sets. Everything after that is the real route:
// the window's press handler, the row's release handler, and the switch.
public class FloatingBarAfterDragTests
{
    [Fact]
    public void A_click_after_a_drag_whose_release_went_nowhere_still_switches() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var sparrowDesktop = harness.Desktops.Desktops.Single(d => d.Name == "Sparrow").Id;

        // The drag has finished, and its stray release never arrived at a row.
        typeof(FloatingBar)
            .GetField("dragJustFinished", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(bar.Bar, true);

        var row = RowFor(bar.Rows, "Sparrow");
        Press(row);
        Release(row);

        Assert.Equal([sparrowDesktop], harness.Desktops.Switched);
    });

    // Rows answer to their group's name as their accessible name (see GroupRow).
    static Grid RowFor(Panel rows, string label) =>
        rows.Children.OfType<Border>()
            .Select(b => b.Child)
            .OfType<Grid>()
            .Single(row => AutomationProperties.GetName(row) == label);

    // The raw tunnelling and bubbling events, as the input manager delivers them; FloatingBarRebuildTests
    // explains why the Direct MouseLeftButton* events would bypass the mechanism under test.
    static void Press(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
    }

    static void Release(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
}
