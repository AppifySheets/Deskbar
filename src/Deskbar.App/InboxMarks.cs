using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE INBOX'S ICONS (Deskbar task 740a2231, slice 1): the state marks and the small marks of the tasks panel's Inbox view,
// drawn from the picked mockup's own icon table (notes\mockups\2026-11-26-tasks-redesign-round2, base.js and r2-data.js,
// table IC). Each icon is on a 16 by 16 grid, drawn with 1.5 DIP round strokes in the text's colour, as the mockup's
// svg.ic is (base.css): a part is a stroke, a fill (the speech bubble, the full disc), or a "cut", a stroke in the
// background's colour that carves a sign out of a fill (the "?" in the bubble, the tick in the disc).
//
// ONE SHAPE PER STATE, colour second (spec, "A line"; the user is colourblind): a bubble with "?" asks you, a triangle
// with "!" went quiet, a filled disc with a tick is finished, a ring with a tick is finished but not running here yet, an
// arc turns while Claude works, a pause sign in a circle is idle, a clock is the usage limit, an empty rounded box is a
// note, a ticked box is closed, a moon is snoozed. A test checks no two marks share their shapes.
//
// The geometries are parsed once and frozen; the shapes made from them are new for every use, since an element can only
// have one parent. Brushes are frozen, as every static WPF brush must be (CLAUDE.md, Traps).
internal static class InboxMarks
{
    // One part of an icon: its geometry, and how it is drawn.
    enum Ink { Stroke, Fill, Cut, Faint }

    sealed record Part(Geometry Shape, Ink Ink);

    static Part S(string data) => new(Parsed(data), Ink.Stroke);
    static Part F(string data) => new(Parsed(data), Ink.Fill);
    static Part C(string data) => new(Parsed(data), Ink.Cut);
    static Part Faint(Geometry shape) => new(shape, Ink.Faint);
    static Part Ring(double x, double y, double r) => new(Frozen(new EllipseGeometry(new Point(x, y), r, r)), Ink.Stroke);
    static Part Disc(double x, double y, double r, Ink ink = Ink.Fill) => new(Frozen(new EllipseGeometry(new Point(x, y), r, r)), ink);
    static Part Box(double x, double y, double w, double h, double r) => new(Frozen(new RectangleGeometry(new Rect(x, y, w, h), r, r)), Ink.Stroke);

    static Geometry Parsed(string data) => Frozen(Geometry.Parse(data));

    static Geometry Frozen(Geometry g)
    {
        g.Freeze();
        return g;
    }

    // The mockup's IC table, by the names it uses there.
    static readonly IReadOnlyDictionary<string, IReadOnlyList<Part>> Icons =new Dictionary<string, IReadOnlyList<Part>>
    {
        ["ask"] = [F("M3.5 2h9A2.5 2.5 0 0 1 15 4.5v5a2.5 2.5 0 0 1-2.5 2.5H8.6l-3.3 2.6a.5.5 0 0 1-.8-.4V12h-1A2.5 2.5 0 0 1 1 9.5v-5A2.5 2.5 0 0 1 3.5 2z"),
            C("M6.5 5.4a1.6 1.6 0 1 1 2.4 1.4c-.55.32-.9.66-.9 1.2"), Disc(8, 9.9, .85, Ink.Cut)],
        ["done"] = [Ring(8, 8, 6.25), S("M5.2 8.3l1.9 1.9 3.8-4.1")],
        ["doneFill"] = [Disc(8, 8, 7), C("M4.9 8.3l2.1 2.1 4.1-4.5")],
        ["working"] = [Faint(Frozen(new EllipseGeometry(new Point(8, 8), 6, 6))), S("M8 2a6 6 0 0 1 6 6")],
        ["idle"] = [Ring(8, 8, 6.25), S("M6.4 5.8v4.4M9.6 5.8v4.4")],
        ["stuck"] = [S("M8 2.2l6.3 11H1.7z"), S("M8 6.6v3.1"), Disc(8, 11.5, .7)],
        ["note"] = [Box(2.75, 2.75, 10.5, 10.5, 2.6)],
        ["checked"] = [Box(2.75, 2.75, 10.5, 10.5, 2.6), S("M5.3 8.3l1.9 1.9 3.6-3.9")],
        ["snooze"] = [S("M14 8.53A6 6 0 1 1 7.47 2 4.67 4.67 0 0 0 14 8.53z")],
        ["clock"] = [Ring(8, 8, 6.25), S("M8 4.6V8l2.3 1.5")],
        ["flag"] = [S("M3.5 14V2.5"), S("M3.5 3h8.2L9.9 5.75l1.8 2.75H3.5")],
        ["bolt"] = [S("M9 1.8L3.5 9h4l-.5 5.2L12.5 7h-4z")],
        ["open"] = [S("M9.5 2.5h4v4"), S("M13.5 2.5l-6 6"), S("M11.5 9.5v3a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-7a1 1 0 0 1 1-1h3")],
        ["terminal"] = [Box(1.75, 2.75, 12.5, 10.5, 2), S("M4.5 6.2l2 1.8-2 1.8M8.5 10h3")],
        ["play"] = [S("M5 3.2v9.6L12.8 8z")],
        ["search"] = [Ring(7, 7, 4.5), S("M10.4 10.4L14 14")],
        ["plus"] = [S("M8 3v10M3 8h10")],
        ["x"] = [S("M4 4l8 8M12 4l-8 8")],
        ["more"] = [Disc(3.5, 8, 1.15), Disc(8, 8, 1.15), Disc(12.5, 8, 1.15)],
        ["back"] = [S("M9.5 3.5L5 8l4.5 4.5")],
        ["filter"] = [S("M2 4.5h7M12.5 4.5H14M2 11.5h1.5M7 11.5h7"), Ring(10.75, 4.5, 1.75), Ring(5.25, 11.5, 1.75)],
        ["comment"] = [S("M3.5 2.75h9a1.75 1.75 0 0 1 1.75 1.75v5a1.75 1.75 0 0 1-1.75 1.75H8.5L5.25 13.9v-2.65H3.5A1.75 1.75 0 0 1 1.75 9.5v-5A1.75 1.75 0 0 1 3.5 2.75z")],
        ["pr"] = [Ring(4, 3.75, 1.75), Ring(4, 12.25, 1.75), Ring(12, 12.25, 1.75), S("M4 5.5v5M12 10.5V7a2 2 0 0 0-2-2H8.5M10 3.3L8.3 5 10 6.7")],
        ["spark"] = [S("M8 1.8v12.4M1.8 8h12.4M3.6 3.6l8.8 8.8M12.4 3.6l-8.8 8.8")],
        ["list"] = [S("M5.5 4h8M5.5 8h8M5.5 12h8"), Disc(2.6, 4, .9), Disc(2.6, 8, .9), Disc(2.6, 12, .9)],
        ["grid"] = [Box(2.25, 2.25, 4.75, 4.75, 1), Box(9, 2.25, 4.75, 4.75, 1), Box(2.25, 9, 4.75, 4.75, 1), Box(9, 9, 4.75, 4.75, 1)],
        ["link"] = [S("M6.8 9.2a2.8 2.8 0 0 0 4 0l2-2a2.8 2.8 0 0 0-4-4l-.7.7"), S("M9.2 6.8a2.8 2.8 0 0 0-4 0l-2 2a2.8 2.8 0 0 0 4 4l.7-.7")],
        ["refresh"] = [S("M13.5 8a5.5 5.5 0 1 1-1.7-4"), S("M12.4 1.7v2.7H9.6")],
        ["stats"] = [S("M3 13.5V8.5M8 13.5V3M13 13.5V6.5")],
        ["inbox"] = [S("M1.75 9.5h3.5l1 2h3.5l1-2h3.5"), S("M3.5 3h9l1.75 6.5v3a1.25 1.25 0 0 1-1.25 1.25H3A1.25 1.25 0 0 1 1.75 12.5v-3z")],
    };

    /// <summary>The mockup's icon for a state mark (its glyph(), r2-data.js).</summary>
    internal static string IconOf(InboxMark mark) => mark switch
    {
        InboxMark.Asks => "ask",
        InboxMark.WentQuiet => "stuck",
        InboxMark.Finished => "doneFill",
        InboxMark.FinishedNotRunning => "done",
        InboxMark.Working => "working",
        InboxMark.Idle => "idle",
        InboxMark.UsageLimit => "clock",
        InboxMark.Closed => "checked",
        InboxMark.Snoozed => "snooze",
        _ => "note",
    };

    /// <summary>A mark's words, its tooltip and what a screen reader hears (the mockup's KIND_LABEL).</summary>
    internal static string WordsOf(InboxMark mark) => mark switch
    {
        InboxMark.Asks => "Asks you",
        InboxMark.WentQuiet => "Went quiet",
        InboxMark.Finished => "Finished",
        InboxMark.FinishedNotRunning => "Finished, not running yet",
        InboxMark.Working => "Claude is working",
        InboxMark.Idle => "Idle",
        InboxMark.UsageLimit => "Stopped: usage limit",
        InboxMark.Closed => "Closed",
        InboxMark.Snoozed => "Snoozed",
        _ => "A note",
    };

    /// <summary>The mark's colour: amber needs you, blue is Claude at work, a light grey is finished, the rest quiet.</summary>
    internal static Brush InkOf(InboxMark mark) => mark switch
    {
        InboxMark.Asks or InboxMark.WentQuiet => InboxLook.Amber,
        InboxMark.Finished or InboxMark.FinishedNotRunning => InboxLook.DoneInk,
        InboxMark.Working => InboxLook.Blue,
        _ => InboxLook.Ink3,
    };

    /// <summary>
    /// An icon at <paramref name="size"/> DIP in <paramref name="ink"/>; `cut` is the colour the cut-out parts are drawn in,
    /// the colour of whatever the icon sits on. <paramref name="turning"/>: the icon turns, one slow turn in TurnTime, while
    /// it is on screen (the working mark on a line and on a page; the PM's fix 4).
    /// </summary>
    internal static FrameworkElement Icon(string name, double size, Brush ink, Brush? cut = null, bool turning = false)
    {
        var canvas = new Canvas { Width = 16, Height = 16, IsHitTestVisible = false };
        if (turning) Turn(canvas);
        Icons[name].ToList().ForEach(part => canvas.Children.Add(new Path
        {
            Data = part.Shape,
            Stroke = part.Ink is Ink.Stroke or Ink.Faint ? ink : part.Ink == Ink.Cut && part.Shape is not EllipseGeometry ? cut ?? InboxLook.Background : null,
            Fill = part.Ink == Ink.Fill ? ink : part.Ink == Ink.Cut && part.Shape is EllipseGeometry ? cut ?? InboxLook.Background : null,
            StrokeThickness = part.Ink == Ink.Cut ? 1.6 : 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = part.Ink == Ink.Faint ? 0.3 : 1,
        }));
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    /// <summary>One turn of a turning mark.</summary>
    internal static readonly TimeSpan TurnTime = TimeSpan.FromSeconds(1.2);

    // A TURNING MARK COSTS LITTLE (the PM's fix 4): one turn in TurnTime at about 20 frames a second instead of WPF's 60,
    // and only while the mark is loaded on screen. It starts when the mark is loaded and stops when it is unloaded (a
    // redraw, another tab, the panel closing), so a hidden panel animates nothing.
    static void Turn(Canvas canvas)
    {
        var rotate = new RotateTransform(0, 8, 8);
        canvas.RenderTransform = rotate;
        canvas.Tag = TurningTag;
        canvas.Loaded += (_, _) =>
        {
            var turn = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TurnTime)
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(turn, 20);
            rotate.BeginAnimation(RotateTransform.AngleProperty, turn);
        };
        canvas.Unloaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>The Tag of a turning mark's drawing, for the tests.</summary>
    internal const string TurningTag = "inbox-turning";

    /// <summary>For the tests: the outline of a mark's icon as path data, to tell two marks' shapes apart.</summary>
    internal static string ShapeOf(InboxMark mark) =>
        string.Join("|", Icons[IconOf(mark)].Select(p => $"{p.Ink}:{p.Shape.GetFlattenedPathGeometry()}"));
}

/// <summary>
/// The Inbox's colours (Deskbar task 740a2231; spec "The frame", the mockup's r2.css tokens). The whole panel takes them
/// while the Inbox shows; List, Matrix and Stats keep their own.
/// </summary>
internal static class InboxLook
{
    internal static readonly Brush Background = Frozen(0x0F, 0x12, 0x18);
    internal static readonly Brush Surface = Frozen(0x16, 0x1A, 0x22);
    internal static readonly Brush Raised = Frozen(0x1F, 0x25, 0x31);
    internal static readonly Brush RaisedHot = Frozen(0x2A, 0x32, 0x42);
    internal static readonly Brush Sheet = Frozen(0x1D, 0x23, 0x30);
    internal static readonly Brush Hairline = Frozen(0x23, 0x2A, 0x36);
    internal static readonly Brush StrongLine = Frozen(0x3B, 0x44, 0x55);
    internal static readonly Brush Ink = Frozen(0xEE, 0xF1, 0xF6);
    internal static readonly Brush Ink2 = Frozen(0xB6, 0xBE, 0xCC);
    internal static readonly Brush Ink3 = Frozen(0x8A, 0x94, 0xA6);
    internal static readonly Brush DoneInk = Frozen(0xDD, 0xE2, 0xEA);
    internal static readonly Brush Amber = Frozen(0xFF, 0xC2, 0x5A);
    internal static readonly Brush Blue = Frozen(0x8A, 0xB4, 0xFF);
    internal static readonly Brush Violet = Frozen(0xB9, 0xA2, 0xFF);
    internal static readonly Brush AskEdge = Frozen(0xFF, 0xC2, 0x5A);
    // The question bubble's fill is the plain surface, not a warm tint: in this look amber alone says "needs you" (its
    // edge and its caption), and a brown fill beside it is the orange-on-brown mix the redesign set out to remove.
    internal static readonly Brush AskFill = Frozen(0x16, 0x1A, 0x22);

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
