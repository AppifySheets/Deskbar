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

    // THE RING THAT FILLS ON ARRIVAL (task 8470d176, Petre's pick A in https://claude.ai/artifact/2NujT6C7cJyYNQUVupjhfp).
    // A Done task whose change is merged but not in the running Deskbar yet (TaskDelivery.Shipping, RunningChanges in
    // Core) gets the same mark hollow: a green ring with a green tick, on the same 14-unit grid, so it still reads as
    // Done at a glance and the disc filling in is the deploy. The ring is the same 14-unit circle as the disc, only
    // without the fill (task 9faa55f0, Petre: "use the same sized circle, but without a fill"). The first version placed
    // a 12.2-unit ellipse 0.9 in, on the belief that WPF centres a stroke on the shape's edge. It does not: an Ellipse
    // keeps its stroke INSIDE its Width and Height, so that ring's outer edge was 12.2 across and on his screen it read
    // as a smaller mark than the disc on the next line. TaskStatusMarkSizeTests measures the painted ink of both.

    /// <summary>
    /// The Done tick: a green disc <paramref name="size"/> DIP across with a dark tick in it, or, while the task's change
    /// is not running yet (<paramref name="shipping"/>), a green ring with a green tick. Not hit-testable, so a click on it
    /// is a click on its line. Tagged <see cref="DoneTickTag"/>, or <see cref="ShippingTickTag"/> for the ring, for the
    /// tests.
    /// </summary>
    internal static FrameworkElement DoneTick(double size, bool shipping = false)
    {
        // The tick is drawn on a 14-unit grid (the mockup's SVG) and scaled to the size asked for.
        var tick = new Path
        {
            Data = Geometry.Parse("M3.8,7.2 L5.9,9.3 L10.2,4.7"),
            Stroke = shipping ? DoneGreen : TickInk,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 14, Height = 14 };
        // Both rounds fill the whole 14-unit square from 0,0. The ring's stroke is drawn inward from that edge (WPF keeps
        // a shape's stroke within its bounds), so its outer edge is the disc's edge and the tick sits in the same place.
        var round = shipping
            ? new Ellipse { Width = 14, Height = 14, Stroke = DoneGreen, StrokeThickness = 1.6 }
            : new Ellipse { Width = 14, Height = 14, Fill = DoneGreen };
        canvas.Children.Add(round);
        canvas.Children.Add(tick);
        return new Viewbox
        {
            Tag = shipping ? ShippingTickTag : DoneTickTag,
            Width = size,
            Height = size,
            Child = canvas,
            // The line's own tooltip already names the status ("Done: <title>").
            IsHitTestVisible = false,
        };
    }

    /// <summary>
    /// Whether a line draws the tick in front of its title: every Done task, and an open task that is not Done once its
    /// change is merged (the ring) or running (the disc), task 1afbd44f, Petre: "we have two circles - one for merged and
    /// the other for deployed". RunningChanges.Of gives a task that is not Done no stage but those two, so any stage
    /// there is one of them. The caller still leaves out a line in its undo window.
    /// </summary>
    internal static bool ShowsTick(TaskStatus status, TaskDelivery delivery) =>
        status == TaskStatus.Done || delivery.Stage != DeliveryStage.None;

    internal const string DoneTickTag = "task-done-tick";
    internal const string ShippingTickTag = "task-shipping-tick";
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
    // A Done task's change not running yet: "Merged", or "PR open" while one of its pull requests is still open.
    // A deploy hand-over (task a87891ca, awaitsDeploy): "Deploy", on the Waiting chip outlined, as a merged Done chip is.
    static string ChipText(TaskStatus status, DeliveryStage delivery = DeliveryStage.None, bool awaitsDeploy = false) =>
        status == TaskStatus.WaitingOnYou ? awaitsDeploy ? "Deploy" : "Waiting"
        : Shipping(status, delivery) ? ShippingWord(delivery)
        : TaskViews.StatusName(status);

    /// <summary>
    /// The status word of a Done task drawn hollow, for the chip here and the task window's: "PR open", "Merged", or
    /// "Done" while Deskbar is still asking GitHub (DeliveryStage.Checking, task 8db4c78e), when it knows neither.
    /// </summary>
    internal static string ShippingWord(DeliveryStage delivery) => delivery switch
    {
        DeliveryStage.Open => "PR open",
        DeliveryStage.Checking => "Done",
        _ => "Merged",
    };

    // Whether a Done task's change is not known to run (TaskDelivery.Shipping), so its marks are drawn hollow.
    static bool Shipping(TaskStatus status, DeliveryStage delivery) =>
        status == TaskStatus.Done && new TaskDelivery(delivery, "").Shipping;

    /// <summary>
    /// The status chip of a Tasks window line, or null for a closed task. Not hit-testable, so a click on it selects the
    /// line; the status itself is changed from the line's "…" menu, as before.
    /// </summary>
    /// <remarks>
    /// A Done task whose change is not running yet (task 8470d176, <paramref name="delivery"/> Open or Merged) gets the
    /// Done chip OUTLINED, with "Merged" (or "PR open" while a pull request is still open) in place of "Done", the way the
    /// tick turns into a ring; the filled Done chip once it runs. A task waiting on a deploy hand-over (task a87891ca,
    /// <paramref name="awaitsDeploy"/>) gets the Waiting chip outlined the same way, with "Deploy": merged, not running,
    /// and the next move his (mockup https://claude.ai/artifact/BVJ6WrgNAoeBso1GJ2S5CV).
    /// </remarks>
    internal static FrameworkElement? Chip(TaskStatus status, DeliveryStage delivery = DeliveryStage.None, bool awaitsDeploy = false) =>
        ChipColours.TryGetValue(status, out var colours)
            ? new Border
            {
                Tag = ChipTag,
                Background = Shipping(status, delivery) || (awaitsDeploy && status == TaskStatus.WaitingOnYou) ? Brushes.Transparent : colours.Fill ?? Brushes.Transparent,
                BorderBrush = colours.Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 0, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                // Trimmed rather than cut at the chip's edge when its column is narrower than the word (the tasks panel's
                // resizable status column, task 3df2d28f); the line's tooltip names the status in full.
                Child = new TextBlock { Text = ChipText(status, delivery, awaitsDeploy), FontSize = 10, Foreground = colours.Ink, TextTrimming = TextTrimming.CharacterEllipsis },
            }
            : null;
}
