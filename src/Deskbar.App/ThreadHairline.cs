using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deskbar.App;

// A HAIRLINE BETWEEN THE COMMENTS OF A TASK'S THREAD (task aa42794f, Petre: "separation", option A of the mockup
// https://claude.ai/artifact/NCuFstp5vSZrN6W4rxMp7c). Comments used to be told apart by a bottom margin alone, so a
// comment's author line read as if it belonged to the body above it. Each comment after the first now sits under a
// 1 px line, with the same gap on both sides of it: the comment above keeps its bottom margin, and the line's padding
// gives the comment below the same room. A line rather than a tile per comment, because the thread is already inside a
// bordered card in the panel (under a ruled edge in the task window), and boxes inside it were heavier. Both thread hosts use it: the tasks panel (TasksWindow.Thread) and the
// task's own window (TaskDetailWindow.DrawThread).
static class ThreadHairline
{
    // The thread card's own edge colour (TasksWindow.ThreadEdge), so the line reads as part of the same card.
    static readonly Brush Line = Frozen(0xFF, 0x33, 0x35, 0x3C);

    /// <summary>
    /// The comments as the thread shows them: the first as it is, every later one under a hairline with
    /// <paramref name="gap"/> DIP between the line and the comment (the comments' own bottom margin, so the line sits
    /// in the middle of the space between two comments).
    /// </summary>
    public static IReadOnlyList<UIElement> Separated(IEnumerable<UIElement> comments, double gap) =>
        comments.Select((comment, index) => index == 0
            ? comment
            : Lined(comment, gap)).ToArray();

    // The comment under its line. A settled comment is dimmed (Opacity 0.55 on its own element); the dimming moves to
    // the wrapper so the line above a dim comment is dim with it, and the comment is not dimmed twice.
    static Border Lined(UIElement comment, double gap)
    {
        var opacity = comment.Opacity;
        comment.Opacity = 1;
        return new Border
        {
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, gap, 0, 0),
            Opacity = opacity,
            Child = comment,
        };
    }

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
