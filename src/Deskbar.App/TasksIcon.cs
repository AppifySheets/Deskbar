using System.Windows;
using System.Windows.Media;

namespace Deskbar.App;

// THE TASKS WINDOW'S ICON (2026-11-23, Petre: "give it a proper icon in the workspaces so i can take it to the correct
// place"). The tasks window is listed on the bar like any app window (App.xaml.cs, where the monitor no longer ignores
// it), and the bar draws a window by the icon the window itself answers WM_GETICON with (IconCache.OwnIconSources).
// Without an icon of its own the window would wear Deskbar's, and on a row next to the Manage window the two could not be
// told apart. So it gets a checklist: three ticked lines on a green rounded square, a colour no palette lane uses as its
// first pick and unlike the app's indigo. Drawn as vectors, so WPF renders it sharp at the 16 and 32 pixel sizes the
// taskbar, Alt+Tab and the bar ask for, and nothing has to be shipped as an .ico.
internal static class TasksIcon
{
    // The 32 x 32 design grid the geometry below is laid out on.
    const double Size = 32;

    static readonly Brush Fill = Frozen(Color.FromRgb(0x1F, 0x9D, 0x5C));
    static readonly Brush Mark = Frozen(Colors.White);

    /// <summary>The icon, frozen, shared by every use (it never changes).</summary>
    internal static ImageSource Image { get; } = Build();

    static ImageSource Build()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Fill, null, new RectangleGeometry(new Rect(0, 0, Size, Size), 7, 7)));
        // Three rows, each a tick and the line of its task's title.
        new[] { 8.5, 16.0, 23.5 }.ToList().ForEach(y =>
        {
            var tick = new PathGeometry([new PathFigure(new Point(5.5, y), [new PolyLineSegment([new Point(8, y + 2.5), new Point(12.5, y - 2.5)], true)], false)]);
            group.Children.Add(new GeometryDrawing(null, Pen(2.4), tick));
            group.Children.Add(new GeometryDrawing(null, Pen(2.6), new LineGeometry(new Point(16, y), new Point(26.5, y))));
        });
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    static Pen Pen(double thickness)
    {
        var pen = new Pen(Mark, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    // Static brushes must be frozen (CLAUDE.md, Traps).
    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
