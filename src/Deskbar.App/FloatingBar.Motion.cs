using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Deskbar.Core.Domain;

namespace Deskbar.App;

// A ROW EXPANDING OR COLLAPSING GLIDES rather than jumping. Petre: "a simple animation for expanding and
// collapsing workspaces, so it's not too abrupt", "quick, simple, linking expanded and collapsed icons".
//
// The bar rebuilds its rows from scratch, so nothing survives a rebuild to animate. So this is the usual trick
// for that (record where things were, let the new layout settle, then start each new element where its
// predecessor was and slide it home):
//
//   * every ROW, matched by its key, slides from its old place to its new one, which is what moves the rows
//     below an expanding row down smoothly instead of all at once;
//   * every ICON, matched by its window, slides from where it was to where it is now, inside its row, which is
//     what links an icon to the titled line it becomes, and the line back to the icon.
//
// Only when the set of expanded rows changed: an ordinary rebuild (a title changing, a window opening) is not
// asked to move anything. 220 ms easing out (see GlideTime), and whatever Windows' own animation setting says
// (see GlideIfExpansionChanged for why).
public partial class FloatingBar
{
    // 220 ms. Petre asked for "increase time, double" before he had seen it run (Windows' animation setting was
    // stopping it, see GlideIfExpansionChanged); once it ran at 320, "try 160 again", "or 220".
    static readonly Duration GlideTime = new(TimeSpan.FromMilliseconds(220));

    HashSet<Guid> lastExpanded = [];
    bool expansionChanged;

    sealed record LayoutSnapshot(Dictionary<string, Point> Rows, Dictionary<WindowHandle, Point> Icons);

    // Called by every rebuild with the rows it expands; whether that differs from the last rebuild's is what
    // decides whether this one glides.
    void NoteExpansion(IReadOnlySet<Guid> expanded)
    {
        if (!expanded.SetEquals(lastExpanded)) expansionChanged = true;
        lastExpanded = expanded.ToHashSet();
    }

    // Every tagged row and every icon, in the bar's own coordinates.
    LayoutSnapshot TakeLayoutSnapshot()
    {
        var rows = new Dictionary<string, Point>();
        foreach (var row in RowElements()) rows.TryAdd(((RowTag)row.Tag).Key, row.TranslatePoint(new Point(0, 0), Root));
        var icons = new Dictionary<WindowHandle, Point>();
        foreach (var ring in iconRings.Where(r => r.Button.IsLoaded))
            icons.TryAdd(ring.Handle, ring.Button.TranslatePoint(new Point(0, 0), Root));
        return new LayoutSnapshot(rows, icons);
    }

    void GlideIfExpansionChanged(LayoutSnapshot before)
    {
        if (!expansionChanged) return;
        expansionChanged = false;
        // NOT gated on Windows' "animation effects" (SystemParameters.ClientAreaAnimation), as it first was:
        // Petre has that off, so the glide he asked for never ran, and his "increase time, double" was about an
        // animation he had not seen. Asked for by name, it runs whatever the system-wide setting says.
        if (before.Rows.Count == 0)
        {
            if (ClickTrace.On) ClickTrace.Write("glide: skipped, nothing was on the bar before");
            return;
        }

        // Rows first: each one's slide is also carried by everything inside it, so an icon's own slide is what
        // is left of its move once its row's is taken off.
        var rowSlides = new Dictionary<FrameworkElement, Vector>();
        foreach (var row in RowElements())
            if (before.Rows.TryGetValue(((RowTag)row.Tag).Key, out var was))
            {
                var slide = was - row.TranslatePoint(new Point(0, 0), Root);
                rowSlides[row] = slide;
                Glide(row, slide);
            }

        var icons = 0;
        foreach (var ring in iconRings)
        {
            if (!before.Icons.TryGetValue(ring.Handle, out var was)) continue;
            var slide = was - ring.Button.TranslatePoint(new Point(0, 0), Root);
            var row = RowOf(ring.Button);
            if (row is not null && rowSlides.TryGetValue(row, out var carried)) slide -= carried;
            Glide(ring.Button, slide);
            icons++;
        }
        if (ClickTrace.On) ClickTrace.Write($"glide: {rowSlides.Count(r => r.Value.Length >= 0.5)} rows, {icons} icons matched");
    }

    // From `offset` back to where layout put it. Small offsets are not worth a moving frame.
    static void Glide(UIElement element, Vector offset)
    {
        if (Math.Abs(offset.X) < 0.5 && Math.Abs(offset.Y) < 0.5) return;
        var move = new TranslateTransform(offset.X, offset.Y);
        element.RenderTransform = move;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, 0, GlideTime) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, 0, GlideTime) { EasingFunction = ease });
    }

    // Every row container on the bar, found by the RowTag GroupRow gives each one, nested groups included.
    IEnumerable<FrameworkElement> RowElements()
    {
        var pending = new Stack<DependencyObject>([Rows]);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is FrameworkElement { Tag: RowTag } row)
            {
                yield return row;
                continue;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    static FrameworkElement? RowOf(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Tag: RowTag } row) return row;
        return null;
    }
}
