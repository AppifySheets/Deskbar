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
// ONE SHAPE PER STATE, colour second (spec, "A line"; the user is colourblind): a bubble with "?" asks you, two joining
// branches wait only for your merge, a triangle
// with "!" went quiet, a filled disc with a tick is finished, a ring with a tick is finished but not running here yet, a
// dashed ring with a tick is finished but its session cannot check the deploy, an
// arc turns while Claude works, a pause sign in a circle is idle, a clock is the usage limit, an empty rounded box is a
// note, a ticked box is closed, a moon is snoozed, a shield with a keyhole asks permission. A test checks no two marks
// share their shapes.
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
        // Waiting for his deploy (task 1565789d, DeployWait): the "done" ring and tick, open at the top right with a dot in
        // the gap, the step still to come. A shape of its own, as every mark needs one (criterion 13: the screen in grey
        // still tells them apart), where the plain ring in amber had differed from "finished, not running yet" by colour.
        ["doneWait"] = [S("M14.25 8A6.25 6.25 0 1 1 11.125 2.587"), S("M5.2 8.3l1.9 1.9 3.8-4.1"), Disc(13.4, 3.6, 1.3)],
        // Its session cannot check the deploy (task 485ba96b, InboxMark.DeployUnchecked; the mockup's option A,
        // https://claude.ai/artifact/GYJRsxJuNsTiZWJsA3XMA1): the "done" ring broken into eight dashes, with its tick. The
        // ring that would fill on a deploy, not whole because nobody could confirm one; a shape of its own in grey too,
        // since no other mark has a broken outline. The dashes are arcs of the same 6.25 circle, 30 degrees each with
        // 15-degree gaps, a gap centred at the top, written out because the icon table has strokes, not dash patterns.
        ["doneUnknown"] = [S("M8.82 1.8A6.25 6.25 0 0 1 11.8 3.04M13 4.2A6.25 6.25 0 0 1 14.2 7.18M14.2 8.82A6.25 6.25 0 0 1 13 11.8"
            + "M11.8 13A6.25 6.25 0 0 1 8.82 14.2M7.18 14.2A6.25 6.25 0 0 1 4.2 13M3.04 11.8A6.25 6.25 0 0 1 1.8 8.82"
            + "M1.8 7.18A6.25 6.25 0 0 1 3.04 4.2M4.2 3.04A6.25 6.25 0 0 1 7.18 1.8"), S("M5.2 8.3l1.9 1.9 3.8-4.1")],
        // Waits only for a merge (task b99e5596, InboxMark.ReadyToMerge; the mockup's option A): two branches joining into
        // one, a node on each. New here: the "pr" glyph below is three nodes and a hooked arrow, and no other mark has a
        // curve meeting a straight stem, so it is told apart from the "?" bubble in grey too.
        ["merge"] = [Ring(4.5, 3.5, 1.75), Ring(4.5, 12.5, 1.75), Ring(11.5, 8, 1.75), S("M4.5 5.25v5.5M4.5 5.25c0 2 2.5 2.75 5.25 2.75")],
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
        // The Inbox's All tab (every line of the other three): a stack of cards, a rounded card with the edges of two more
        // behind it. New here, the mockup has no All tab; drawn on the same grid and strokes as the rest, and unlike every
        // state mark (no state is "all"), so it never reads as a line's state.
        ["all"] = [Box(2.25, 6.25, 11.5, 7.5, 2), S("M3.75 3.9h8.5"), S("M5.25 1.75h5.5")],
        // The step kinds of a session's steps (ChatSteps, TasksWindow.Transcript.cs), and the marks a step row wears. Drawn on
        // the same grid and with the same strokes as the mockup's table above; the mockup has no step icons of its own, so
        // these are new: a page of text (read), a pencil (edit), a pencil on a line (write), a globe (web), three nodes joined
        // (a subagent), a circle with a cross (a failed step: a shape, never a colour alone), a tick (a finished one), and
        // the two chevrons of a fold. "file" is read and "pencil" is edit; "terminal", "search", "list" and "spark" are the
        // mockup's own, used for a command, a search, a plan and anything else.
        ["file"] = [S("M4.25 1.75h5.5l3 3v9.5h-8.5z"), S("M9.75 1.75v3h3")],
        ["pencil"] = [S("M10.5 2.25l3.25 3.25-7.75 7.75H2.75V10z"), S("M9 3.75l3.25 3.25")],
        ["write"] = [S("M10.5 1.75l2.75 2.75-6.5 6.5H4V8.25z"), S("M2.5 14h11")],
        ["globe"] = [Ring(8, 8, 6.25), S("M1.75 8h12.5"), S("M8 1.75c2 1.9 2.6 4 2.6 6.25S10 12.35 8 14.25c-2-1.9-2.6-4-2.6-6.25S6 3.65 8 1.75z")],
        ["agent"] = [Ring(8, 3.5, 1.75), Ring(3.5, 12.5, 1.75), Ring(12.5, 12.5, 1.75), S("M8 5.25v2.5M8 7.75H3.5v3M8 7.75h4.5v3")],
        ["fail"] = [Ring(8, 8, 6.25), S("M5.8 5.8l4.4 4.4M10.2 5.8l-4.4 4.4")],
        ["tick"] = [S("M3.5 8.5l3 3 6-6.5")],
        // A step with no result and none to come (its session ended, or it is in an older run): a ring with a dash.
        ["blank"] = [Ring(8, 8, 6.25), S("M5.4 8h5.2")],
        ["chevR"] = [S("M6 3.5L10.5 8 6 12.5")],
        ["chevD"] = [S("M3.5 6L8 10.5 12.5 6")],
        ["image"] = [Box(2, 3, 12, 10, 2), Disc(5.5, 6.5, 1), S("M2.5 12l3.5-3.5 2.5 2.5 2-2 3 3")],
        // A permission question (Allow / Deny from a task's page): a shield with a keyhole, the mockup's "shield" kind. New
        // here, since the mockup names it but draws none; no other mark has a shield's outline or a keyhole.
        ["shield"] = [S("M8 1.75l5.25 2v4.1c0 3.2-2.2 5.45-5.25 6.4-3.05-.95-5.25-3.2-5.25-6.4v-4.1z"), Disc(8, 6.9, 1.3), S("M8 8v2.8")],
    };

    /// <summary>
    /// Stopped on the usage limit: the status line Deskbar writes for such a stop, and clears once the limit has reset
    /// (ClaudeSessions.NoteLimits). Only Deskbar's own line, never a session's. The panel's Inbox and the bar's lines both
    /// read it here (slice 2), so the two marks agree.
    /// </summary>
    internal static bool StoppedOnLimit(TaskItem task) => task.StatusLine.StartsWith(ClaudeSessions.LimitLine, StringComparison.Ordinal);

    /// <summary>The mockup's icon for a state mark (its glyph(), r2-data.js).</summary>
    internal static string IconOf(InboxMark mark) => mark switch
    {
        InboxMark.Asks => "ask",
        InboxMark.ReadyToMerge => "merge",
        InboxMark.WentQuiet => "stuck",
        InboxMark.Finished => "doneFill",
        InboxMark.FinishedNotRunning => "done",
        InboxMark.DeployUnchecked => "doneUnknown",
        // Task 1565789d (DeployWait): the "merged, not running yet" ring, open with a dot (doneWait), amber below since the
        // deploy is his step.
        InboxMark.WaitsForDeploy => "doneWait",
        InboxMark.Working => "working",
        InboxMark.Idle => "idle",
        InboxMark.UsageLimit => "clock",
        InboxMark.Closed => "checked",
        InboxMark.Snoozed => "snooze",
        InboxMark.AsksPermission => "shield",
        _ => "note",
    };

    /// <summary>A mark's words, its tooltip and what a screen reader hears (the mockup's KIND_LABEL).</summary>
    internal static string WordsOf(InboxMark mark) => mark switch
    {
        InboxMark.Asks => "Asks you",
        InboxMark.ReadyToMerge => "Ready to merge",
        InboxMark.WentQuiet => "Went quiet",
        InboxMark.Finished => "Finished",
        InboxMark.FinishedNotRunning => "Finished, not running yet",
        InboxMark.DeployUnchecked => "Finished, can't check the deploy",
        InboxMark.WaitsForDeploy => "Waits for your deploy",
        InboxMark.Working => "Claude is working",
        InboxMark.Idle => "Idle",
        InboxMark.UsageLimit => "Stopped: usage limit",
        InboxMark.Closed => "Closed",
        InboxMark.Snoozed => "Snoozed",
        InboxMark.AsksPermission => "Asks permission",
        _ => "A note",
    };

    /// <summary>The mark's colour: amber needs you, blue is Claude at work, a light grey is finished, the rest quiet.</summary>
    internal static Brush InkOf(InboxMark mark) => mark switch
    {
        InboxMark.Asks or InboxMark.ReadyToMerge or InboxMark.WentQuiet or InboxMark.AsksPermission or InboxMark.WaitsForDeploy => InboxLook.Amber,
        InboxMark.Finished or InboxMark.FinishedNotRunning or InboxMark.DeployUnchecked => InboxLook.DoneInk,
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

    /// <summary>For the tests: the outline of a named icon, to tell two icons' shapes apart.</summary>
    internal static string ShapeOfIcon(string name) =>
        string.Join("|", Icons[name].Select(p => $"{p.Ink}:{p.Shape.GetFlattenedPathGeometry()}"));

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
    // The dialog card's "Recommended" tag (TasksWindow.DialogCard): Claude's own pick, told apart from the user's (blue).
    internal static readonly Brush Green = Frozen(0x7F, 0xD6, 0xA0);
    internal static readonly Brush AskEdge = Frozen(0xFF, 0xC2, 0x5A);
    // The question bubble's fill is the plain surface, not a warm tint: in this look amber alone says "needs you" (its
    // edge and its caption), and a brown fill beside it is the orange-on-brown mix the redesign set out to remove.
    internal static readonly Brush AskFill = Frozen(0x16, 0x1A, 0x22);

    // The owner asked for bigger chat text on build 5: reading text on the Inbox chat page, and its small grey text.
    internal const double ChatText = 13.5;
    internal const double ChatSmall = 11.5;

    // Reading text on an Inbox page: the size, lines one and a half sizes tall and twice the gap between blocks (WPF's
    // own line is about 1.33 sizes, and MarkdownBlocks parts paragraphs by 4). The owner on build 6, at 13.5: "to read
    // vs code is much easier than our chat"; its chat has the same face and size, with taller lines and wider gaps.
    // The last block keeps its gap, so a bubble's foot does not grow.
    internal static void Reading(System.Windows.Documents.FlowDocument document, double size)
    {
        document.FontSize = size;
        document.LineHeight = Math.Round(size * 1.5);
        foreach (var block in document.Blocks.Where(b => b != document.Blocks.LastBlock))
            block.Margin = new Thickness(block.Margin.Left, block.Margin.Top, block.Margin.Right, block.Margin.Bottom * 2);
        foreach (var block in document.Blocks) Emphasise(block, size);
    }

    // What stands out in reading text (the owner on build 7, with VS Code's chat beside ours: "in vs code there is
    // highlights of important infos and stuff, and here is 1 big text"). There the body is a soft grey and bold is
    // white, and code sits on a chip; here the body was as bright as its bold, and the chip's fill was the bubble's own.
    // So the body takes ReadInk (the callers set it), and bold, headings and code take Strong; code gets a chip that
    // shows on a bubble, a size smaller since the mono face runs large; list items are parted. All by brightness and
    // shape, none by hue.
    internal static readonly Brush ReadInk = Frozen(0xC9, 0xCF, 0xDA);
    internal static readonly Brush Strong = Frozen(0xFF, 0xFF, 0xFF);
    internal static readonly Brush CodeChip = Frozen(0x3A, 0x43, 0x55);

    static void Emphasise(System.Windows.Documents.Block block, double size)
    {
        if (block is System.Windows.Documents.List list)
        {
            list.Padding = new Thickness(22, 0, 0, 0);
            foreach (var item in list.ListItems)
                foreach (var inner in item.Blocks)
                {
                    if (item != list.ListItems.LastListItem) inner.Margin = new Thickness(0, 0, 0, 4);
                    Emphasise(inner, size);
                }
        }
        // A table (task 24813c78): its header row is bright like a heading, and every cell's text is emphasised like
        // any paragraph's, so bold and code in a cell stand out as they do outside one.
        else if (block is System.Windows.Documents.Table table)
        {
            foreach (var group in table.RowGroups)
            {
                if (Equals(group.Tag, MarkdownBlocks.TableHeaderTag)) group.Foreground = Strong;
                foreach (var cell in group.Rows.SelectMany(row => row.Cells))
                    foreach (var inner in cell.Blocks) Emphasise(inner, size);
            }
        }
        else if (block is System.Windows.Documents.Paragraph paragraph)
        {
            if (Equals(paragraph.Tag, MarkdownBlocks.HeadingTag)) paragraph.Foreground = Strong;
            if (Equals(paragraph.Tag, MarkdownBlocks.CodeBlockTag))
            {
                paragraph.Background = Background;
                paragraph.Foreground = Ink;
                paragraph.FontSize = size - 1;
            }
            else Emphasise(paragraph.Inlines, size);
        }
    }

    static void Emphasise(System.Windows.Documents.InlineCollection inlines, double size)
    {
        foreach (var inline in inlines)
        {
            if (inline is System.Windows.Documents.Run run && Equals(run.Tag, MarkdownBlocks.CodeTag))
            {
                run.Background = CodeChip;
                run.Foreground = Strong;
                run.FontSize = size - 1;
            }
            else if (inline is System.Windows.Documents.Bold bold) bold.Foreground = Strong;
            if (inline is System.Windows.Documents.Span span) Emphasise(span.Inlines, size);
        }
    }

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
