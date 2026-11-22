using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Performance;

namespace Deskbar.App;

// THE FOUR STATS CELLS AS ONE CONTROL: CPU, MEM, DISK and GPU side by side, each its name and value now on one line
// and a sparkline of the last minute under them. It used to live inside FloatingBar.Stats.cs; it is a
// control of its own so the bar's stats line and the detached stats window (DetachedStatsWindow) draw the same
// thing from the same code. The history of the readings lives in the strip, so each owner of a strip keeps its own
// and the bar, which keeps recording while its line is hidden, shows the full minute when the line comes back.
//
// Where the line came from, and why it looks like this: see the comments on the code below and the start of
// FloatingBar.Stats.cs.
internal sealed class StatsStrip : UniformGrid
{
    // A minute of history at one sample a second.
    internal const int StatsHistory = 60;
    const double SparkHeight = 22;

    // Per stat, the hue its sparkline and fill are drawn in. CPU takes the blue the bar already uses for Start
    // and the hovered dial, disk the green of the sound ring; memory and GPU get a violet and an amber picked to
    // sit apart from both on the bar's near-black.
    static readonly Color CpuHue = Color.FromRgb(0x4C, 0xC2, 0xFF);
    static readonly Color MemoryHue = Color.FromRgb(0xB4, 0x8C, 0xFF);
    static readonly Color DiskHue = Color.FromRgb(0x6E, 0xE7, 0xA6);
    static readonly Color GpuHue = Color.FromRgb(0xFF, 0xB4, 0x54);
    static readonly Brush StatLabelInk = FrozenBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
    static readonly Brush StatValueInk = FrozenBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF));

    // One cell's parts and its history. The history is replaced, never edited, so a paint never sees half of a
    // sample.
    sealed class StatCell(string label, Color hue)
    {
        public string Label { get; } = label;
        public Color Hue { get; } = hue;
        // The stat's name: on the top line beside the value, or inside the graph at its top left on a strip too
        // narrow for both on one line (FitNames). Not hit-testable, so a click on it is a click on the line.
        public TextBlock Name { get; } = new()
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = StatLabelInk,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        // An en dash until the first sample, so a freshly shown window has no blank cells for the first second.
        public TextBlock Value { get; } = new() { Foreground = StatValueInk, Text = "–" };
        // THE VALUE SHRINKS, IT IS NEVER CLIPPED. Petre once read "MEM8%" on a narrower bar while Task Manager
        // said 98%: name and value shared the top line, and the value, right-aligned in what was left, was pushed
        // past its slot and lost its first digit to the clip. So the value sits in a shrink-only Viewbox: when
        // it does not fit, it gets smaller rather than cut. A smaller "100%" is still true, where "00%" would be
        // another wrong number. DownOnly, so it never grows past its size.
        public Viewbox Box { get; } = new()
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        // The top line: the name takes what it needs (Auto) and the value the rest (*), so the pair can never
        // push the value out of its cell. With the name inside the graph, the value has the whole line.
        public Grid Top { get; } = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        public Grid Graph { get; } = new();
        public Canvas Spark { get; } = new() { Height = SparkHeight, ClipToBounds = true };
        public Polygon Fill { get; } = new() { Fill = FrozenBrush(Color.FromArgb(0x38, hue.R, hue.G, hue.B)) };
        public Polyline Line { get; } = new() { Stroke = FrozenBrush(hue), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
        public ImmutableList<double> History { get; set; } = [];
    }

    // Each cell's margin either side, and the gap between a name and its value on the top line.
    const double CellMargin = 3;
    const double NameGap = 4;
    // How far the widest value ("100%") may shrink before the names leave the top line (FitNames).
    const double LeastValueScale = 0.75;

    readonly StatCell cpuCell, memoryCell, diskCell, gpuCell;
    readonly IReadOnlyList<StatCell> cells;
    // Where the names are now: beside the values (true) or inside the graphs.
    bool namesBeside = true;

    // NAMES BESIDE VALUES, for the bar and the detached window alike: the name top left, the value top right, and
    // the graph below holding the sparkline alone. The window had it first, because over a coloured fill nobody
    // could read the name: "when resources are in use I can't really see CPU or MEM or any characters". The bar
    // followed when Petre, looking at its line: "text and values same line".
    //
    // Except on a strip too narrow for both: there the names go back inside the graphs and the value has the top
    // line to itself, which is the layout the bar had before for exactly that reason (FitNames has the numbers).
    internal StatsStrip()
    {
        cpuCell = new StatCell("CPU", CpuHue);
        memoryCell = new StatCell("MEM", MemoryHue);
        diskCell = new StatCell("DISK", DiskHue);
        gpuCell = new StatCell("GPU", GpuHue);
        cells = [cpuCell, memoryCell, diskCell, gpuCell];

        Rows = 1;
        Columns = 4;
        cells.ToList().ForEach(cell =>
        {
            cell.Spark.Children.Add(cell.Fill);
            cell.Spark.Children.Add(cell.Line);
            // The sparkline is laid out against the canvas's real width, so a resize (the bar's width, the seam)
            // redraws it rather than stretching the stroke.
            cell.Spark.SizeChanged += (_, e) => { if (e.WidthChanged) PaintSpark(cell); };
            cell.Box.Child = cell.Value;
            Grid.SetColumn(cell.Box, 1);
            cell.Top.Children.Add(cell.Box);
            cell.Graph.Children.Add(cell.Spark);
            Place(cell, namesBeside);
            Children.Add(new StackPanel { Margin = new Thickness(CellMargin, 0, CellMargin, 0), Children = { cell.Top, cell.Graph } });
        });
        // The room per cell changes with the strip's width (the bar's width, the text-size setting widening the
        // bar), so the names are fitted again whenever it does.
        SizeChanged += (_, e) => { if (e.WidthChanged) FitNames(); };
    }

    // Whether the GPU has been read at least once; a machine without GPU counters never has.
    internal bool HasGpu => gpuCell.History.Count > 0;

    // The text size of every name and value, set by the owner from the bar's text-size setting
    // (FloatingBar.TextSize.cs). The cells are built once, so a change of setting reaches them through here.
    // A bigger text needs more room on the top line, so the names are fitted again.
    internal void ApplyTextSize(double size)
    {
        cells.ToList().ForEach(cell =>
        {
            cell.Name.FontSize = size;
            cell.Value.FontSize = size;
        });
        FitNames();
    }

    // Whether the names are on the top line beside the values (false: inside the graphs). For the tests and the trace.
    internal bool NamesBeside => namesBeside;

    // BESIDE OR INSIDE, decided for the whole strip at once so the four cells always look alike. The names stay
    // beside the values while the widest value, "100%", keeps at least LeastValueScale of its size next to the
    // widest name ("MEM", 26 DIP at the default text size); below that they go inside the graphs. Measured in
    // FloatingBarStatsTests at the default text size: at 273 DIP everything fits whole; at 233 DIP "MEM 100%"
    // would keep 81% of the value's size, still easy to read; at 180 DIP "98%" beside "MEM" shrank to 40% and at
    // 130 DIP the values shrank to nothing, which is the "MEM8%" problem again in another form. So the bar
    // switches at about 225 DIP. "100%" rather than the value now, so the layout does not flip as the numbers
    // change.
    //
    // Moving a name changes neither the strip's width nor its height (the top line is one text high either way),
    // so this cannot set off another SizeChanged and loop.
    void FitNames()
    {
        if (ActualWidth <= 0) return;
        var room = ActualWidth / Columns - 2 * CellMargin;
        var scale = cells.Min(cell => (room - TextWidth(cell.Name, cell.Label) - NameGap) / TextWidth(cell.Value, "100%"));
        var beside = scale >= LeastValueScale;
        if (beside == namesBeside) return;
        namesBeside = beside;
        cells.ToList().ForEach(cell => Place(cell, beside));
    }

    // Puts a cell's name on the top line (column 0, the value's left) or inside the graph at its top left, over
    // the sparkline (added after it, so drawn on top).
    static void Place(StatCell cell, bool beside)
    {
        (cell.Name.Parent as Panel)?.Children.Remove(cell.Name);
        cell.Name.Margin = beside ? new Thickness(0, 0, NameGap, 0) : new Thickness(2, 0, 0, 0);
        (beside ? cell.Top : cell.Graph).Children.Add(cell.Name);
    }

    // The width a text needs in a text block's font, as the block itself would lay it out.
    static double TextWidth(TextBlock like, string text) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(like.FontFamily, like.FontStyle, like.FontWeight, like.FontStretch), like.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(like).PixelsPerDip).WidthIncludingTrailingWhitespace;

    // One reading in: every cell's history grows by a sample and is redrawn, then the strip fits its columns.
    internal void Paint(SystemSample sample, bool holdFourColumns)
    {
        Record(cpuCell, sample.Cpu);
        Record(memoryCell, sample.Memory);
        Record(diskCell, sample.Disk);
        sample.Gpu.Tap(gpu => Record(gpuCell, gpu));

        // No GPU counters: three cells, so the others share the width rather than leaving a dead quarter (and
        // across the seam, four columns with the last one empty; see FitToSeam).
        FitToSeam(holdFourColumns);
    }

    // The tooltip: what the cells have no room for, memory in gigabytes and the system drive's space, and a last
    // line the owner chooses ("Click for Task Manager").
    internal static string TipText(SystemSample sample, string lastLine) =>
        string.Join("\n", new[]
        {
            $"CPU {sample.Cpu:0}%",
            $"Memory {sample.Memory:0}%, {Gigabytes(sample.MemoryUsed)} of {Gigabytes(sample.MemoryTotal)} GB in use",
            $"Disk {sample.Disk:0}% active",
            sample.SystemDriveSize > 0
                ? $"{System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\')} {Gigabytes((ulong)sample.SystemDriveFree)} GB free of {Gigabytes((ulong)sample.SystemDriveSize)} GB"
                : "",
            sample.Gpu.Map(gpu => $"GPU {gpu:0}%").GetValueOrDefault(""),
            lastLine,
        }.Where(line => line.Length > 0));

    // Each cell's width, then each value's natural width against the room its shrink-only box was given, so a
    // value that had to shrink shows up as box < value. The part of the bar's stats trace that is about the cells
    // (FloatingBar.TraceStatsGeometry).
    internal string CellWidths
    {
        get
        {
            var widths = string.Join(",", Children.OfType<FrameworkElement>().Select(c => $"{c.ActualWidth:0}"));
            var values = string.Join(" ", cells.Select(c =>
                $"{c.Label}='{c.Value.Text}' value={c.Value.ActualWidth:0.0} box={c.Box.ActualWidth:0.0}"));
            return $"{widths} names={(namesBeside ? "beside" : "inside")} values=[{values}]";
        }
    }

    void Record(StatCell cell, double value)
    {
        cell.History = StatsWindow(cell.History, value);
        cell.Value.Text = $"{value:0}%";
        PaintSpark(cell);
    }

    void PaintSpark(StatCell cell)
    {
        var points = SparkPoints(cell.History, cell.Spark.ActualWidth, SparkHeight);
        cell.Line.Points = new PointCollection(points);
        // The fill closes the line down to the baseline at both ends.
        cell.Fill.Points = points.Count == 0
            ? []
            : new PointCollection(points.Prepend(new Point(points[0].X, SparkHeight)).Append(new Point(points[^1].X, SparkHeight)));
    }

    // The history with one more sample, the oldest dropped once a minute is full.
    internal static ImmutableList<double> StatsWindow(ImmutableList<double> history, double value)
    {
        var grown = history.Add(value);
        return grown.Count > StatsHistory ? grown.RemoveRange(0, grown.Count - StatsHistory) : grown;
    }

    // Where each sample sits: one slot per second across the width, the newest at the right edge, so a history
    // still filling up grows in from the right as Task Manager's graphs do. 0% sits a pixel above the bottom and
    // 100% a pixel below the top, so a flat line at either end is not cut in half by the clip.
    internal static IReadOnlyList<Point> SparkPoints(IReadOnlyList<double> history, double width, double height)
    {
        if (history.Count == 0 || width <= 0) return [];
        var step = width / (StatsHistory - 1);
        var offset = StatsHistory - history.Count;
        return history
            .Select((value, i) => new Point((offset + i) * step, height - 1 - Math.Clamp(value, 0, 100) / 100 * (height - 2)))
            .ToList();
    }

    // Gigabytes as Task Manager counts them (1024^3), to one decimal.
    static string Gigabytes(ulong bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0");

    // ACROSS THE SEAM the line splits, two cells a screen: CPU and memory on the left, disk and GPU on the right.
    // Petre, when it first kept to the left half like the now-playing line: "when split across monitors, half
    // there half here". Four equal columns over the full width already put the seam exactly between the second
    // and third cell (the bar is centred on it), so the split is just the line left at full width. Squeezed into
    // one half, the four cells were too narrow for their labels and values.
    //
    // The one thing that would break it is a machine with no GPU reading, whose three columns would put the
    // middle cell on the seam. So across the seam the grid keeps four columns and the GPU cell is HIDDEN, still
    // holding its quarter, rather than collapsed.
    //
    // `holdFourColumns` is "across the seam" for the bar and always false for the detached window, which has no
    // seam to keep a cell on one side of. The bar also calls this when it moves onto or off the seam.
    internal void FitToSeam(bool holdFourColumns)
    {
        Children[3].Visibility = HasGpu ? Visibility.Visible : holdFourColumns ? Visibility.Hidden : Visibility.Collapsed;
        Columns = HasGpu || holdFourColumns ? 4 : 3;
        // Three columns or four changes the room per cell without changing the strip's width.
        FitNames();
    }

    static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
