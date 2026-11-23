using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// HOW A TASK'S STATUS SHOWS, on the bar and in the Tasks window (Petre, 2026-11-23: "i can't currently see statuses";
// mockup https://claude.ai/artifact/5YFYRDrgpHdxTCshVVDeTY, his pick "build both"). Until then only Waiting on you
// showed (the orange tint and question); Done, In progress and Backlog looked alike and named themselves only in a
// tooltip, so a finished task read as work still going on.
//
// Two marks, both built here so the bar and the window cannot drift apart:
//   * DoneTick: a green disc with a dark tick, in front of a Done task's title on both surfaces. The one status the bar
//     needs: the bar lists what is Petre's (questions, finished Claude work, his notes), and a question is already
//     orange, so the tick is what tells "finished, yours to close" from a note.
//   * Chip: the status in words in its own column of every Tasks window line, a filled green chip for Done (so it stands
//     out from In progress), orange for Waiting on you, an outlined blue one for In progress and a grey one for Backlog.
//     Blue and orange differ in lightness as well as hue; green is also the only filled one besides orange.
//
// Brushes are frozen (static WPF brushes take thread affinity otherwise; the Windows tests build windows on several STA
// threads).
internal static class TaskStatusMarks
{
    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    static readonly Brush DoneGreen = Frozen(0x3F, 0xB9, 0x50);
    static readonly Brush TickInk = Frozen(0x0D, 0x1A, 0x10);

    /// <summary>
    /// The Done tick: a green disc <paramref name="size"/> DIP across with a dark tick in it. Not hit-testable, so a click
    /// on it is a click on its line. Tagged <see cref="DoneTickTag"/> for the tests.
    /// </summary>
    internal static FrameworkElement DoneTick(double size)
    {
        // The tick is drawn on a 14-unit grid (the mockup's SVG) and scaled to the size asked for.
        var tick = new Path
        {
            Data = Geometry.Parse("M3.8,7.2 L5.9,9.3 L10.2,4.7"),
            Stroke = TickInk,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 14, Height = 14 };
        canvas.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = DoneGreen });
        canvas.Children.Add(tick);
        return new Viewbox
        {
            Tag = DoneTickTag,
            Width = size,
            Height = size,
            Child = canvas,
            // The line's own tooltip already names the status ("Done: <title>").
            IsHitTestVisible = false,
        };
    }

    internal const string DoneTickTag = "task-done-tick";
    internal const string ChipTag = "task-status-chip";

    // Each status's chip: fill (null = outlined only), edge and ink. Closed has no chip (a closed line shows under the
    // Closed filter only, with Reopen beside it, so the word adds nothing).
    static readonly IReadOnlyDictionary<TaskStatus, (Brush? Fill, Brush Edge, Brush Ink)> ChipColours = new Dictionary<TaskStatus, (Brush?, Brush, Brush)>
    {
        [TaskStatus.Done] = (Frozen(0x1F, 0x4D, 0x2B), DoneGreen, Frozen(0x9B, 0xE9, 0xA8)),
        [TaskStatus.WaitingOnYou] = (Frozen(0x5A, 0x34, 0x10), Frozen(0xF0, 0xA0, 0x50), Frozen(0xFF, 0xC9, 0x8A)),
        [TaskStatus.InProgress] = (null, Frozen(0x4A, 0x7F, 0xD0), Frozen(0x9C, 0xC2, 0xFF)),
        [TaskStatus.Backlog] = (null, Frozen(0x5C, 0x5C, 0x5C), Frozen(0xB4, 0xB4, 0xB4)),
    };

    // The chip's word. "Waiting" rather than "Waiting on you": the column is narrow, and the line's orange tint and its
    // group's caption already say on whom.
    static string ChipText(TaskStatus status) => status == TaskStatus.WaitingOnYou ? "Waiting" : TaskViews.StatusName(status);

    /// <summary>
    /// The status chip of a Tasks window line, or null for a closed task. Not hit-testable, so a click on it selects the
    /// line; the status itself is changed from the line's "…" menu, as before.
    /// </summary>
    internal static FrameworkElement? Chip(TaskStatus status) =>
        ChipColours.TryGetValue(status, out var colours)
            ? new Border
            {
                Tag = ChipTag,
                Background = colours.Fill ?? Brushes.Transparent,
                BorderBrush = colours.Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 0, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = new TextBlock { Text = ChipText(status), FontSize = 10, Foreground = colours.Ink },
            }
            : null;
}
