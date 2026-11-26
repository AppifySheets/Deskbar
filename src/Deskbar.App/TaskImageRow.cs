using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE PICTURES OF A COMMENT OR A DESCRIPTION, as a row of thumbnails under its text in the tasks panel.
// Design: docs/superpowers/specs/2026-11-24-task-images-design.md, PR 1 ("Rendering").
//
// The spec had MarkdownBlocks render an image line in place. It is a row of its own instead, after the text, for one
// reason: a comment folds to about three lines behind "Show more" (Ruling 63) by clipping its text box, and a picture
// inside that box would be cut in half or hidden. Kept apart, the text folds as before and the pictures always show.
// The markdown that reaches MarkdownBlocks is the text alone (TaskImages.Split), so no path is ever shown as text either.
//
// Each picture is a flat button holding its thumbnail, at most MaxHeight tall and MaxWidth wide, aspect kept, with a thin
// edge so a white screenshot does not melt into the panel. Small on purpose (task e5d85e5e, "make comment photos small,
// enlarge when i click on them"): at 160 DIP tall a screenshot took the whole width of a chat bubble and pushed the
// conversation apart. A click enlarges it (the window's OpenImage: PictureViewer, a lightbox inside Deskbar). Only
// TaskImages' own paths ever get here (Split drops anything else back into the text), so
// a click can only ever open one of Deskbar's own pictures. A picture whose file cannot be read shows its relative path,
// dim, instead: never an exception, never an empty gap, and the next redraw tries again (TaskImageFiles.Thumbnail).
static class TaskImageRow
{
    /// <summary>The Tag of a picture's button, for the tests.</summary>
    internal const string ImageTag = "task-image";

    /// <summary>The Tag of the text standing in for a picture that cannot be read.</summary>
    internal const string MissingTag = "task-image-missing";

    /// <summary>The tallest a thumbnail is drawn, in DIP. The same height as a picture waiting in the comment editor.</summary>
    internal const double MaxHeight = 64;

    /// <summary>The widest a thumbnail is drawn, in DIP, so a wide screenshot stays a small strip rather than a banner.</summary>
    internal const double MaxWidth = 160;

    // Decoded at this width: sharp at 200% for a thumbnail up to MaxWidth DIP wide, and far smaller than the picture itself.
    const int DecodeWidth = 320;

    static readonly Brush Edge = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    static readonly Brush MissingInk = Frozen(Color.FromRgb(0x8A, 0x8D, 0x96));

    /// <summary>
    /// The row for these pictures (relative paths, TaskImages), or null when there are none. <paramref name="open"/> gets
    /// the absolute path of the picture clicked.
    /// </summary>
    public static FrameworkElement? Of(string stateDir, IReadOnlyList<string> images, Action<string> open) =>
        images.Count == 0 ? null
            : new WrapPanel { Margin = new Thickness(0, 2, 0, 6) }.With(images.Select(relative => One(stateDir, relative, open)));

    static UIElement One(string stateDir, string relative, Action<string> open) =>
        TaskImageFiles.Thumbnail(stateDir, relative, DecodeWidth) is { HasValue: true } thumbnail
            && TaskImages.Resolve(relative, stateDir) is { HasValue: true } path
            ? Picture(thumbnail.Value, path.Value, open)
            : new TextBlock
            {
                Tag = MissingTag,
                Text = relative,
                FontSize = 10.5,
                Foreground = MissingInk,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 4),
                ToolTip = "This picture cannot be read",
            };

    static Button Picture(ImageSource thumbnail, string path, Action<string> open)
    {
        var button = new Button
        {
            Tag = ImageTag,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 4),
            Cursor = Cursors.Hand,
            ToolTip = "Click to enlarge",
            Focusable = false,
            Template = BareTemplate,
            Content = new Border
            {
                BorderBrush = Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Child = new Image { Source = thumbnail, MaxHeight = MaxHeight, MaxWidth = MaxWidth, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly },
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Enlarge picture");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            open(path);
        };
        return button;
    }

    // A button with no chrome of its own: the thumbnail and its edge are the whole look.
    static readonly ControlTemplate BareTemplate = MakeBareTemplate();

    static ControlTemplate MakeBareTemplate()
    {
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        template.Seal();
        return template;
    }

    static WrapPanel With(this WrapPanel panel, IEnumerable<UIElement> children)
    {
        children.ToList().ForEach(child => panel.Children.Add(child));
        return panel;
    }

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
