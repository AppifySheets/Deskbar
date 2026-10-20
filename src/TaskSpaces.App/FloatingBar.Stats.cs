using System.Collections.Immutable;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using TaskSpaces.Windows.Performance;

namespace TaskSpaces.App;

// THE STATS LINE at the top of the footer, above the now-playing line. Petre: "let's show system stats, cpu,
// memory, disk, gpu below, above the music player", then "b" from three mockups, "and clicking opens task
// manager at performance".
//
// Four cells side by side, each a label, the value now, and a sparkline of the last minute under them, the way
// Task Manager's Performance tab draws it: a line with a faint fill below, newest at the right. A sparkline
// rather than a meter because it tells a spike from a steady load, which one number cannot.
//
// The readings come from ISystemStats (SystemStats in production: the counters Task Manager itself reads), one
// a second. With no source the line stays collapsed, which is what every test bar but the stats ones gets.
// A machine without GPU counters gets three cells rather than a GPU stuck at zero.
//
// THE CLICK starts Task Manager, and that is all it can do: Task Manager has no switch for its Performance page
// (the only page its exe parses is "startup", measured from its own strings), and it runs elevated for an
// administrator, so a normal-level bar cannot click the tab for it. The page it opens on is Task Manager's own
// Settings > Default start page; Petre chose to set that to Performance once over having the bar rewrite
// Task Manager's settings.json around every launch.
public partial class FloatingBar
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
    static readonly Brush StatLabelInk = Frozen(0x8C, 0xFF, 0xFF, 0xFF);
    static readonly Brush StatValueInk = Frozen(0xEB, 0xFF, 0xFF, 0xFF);

    // One cell's parts and its history. The history is replaced, never edited, so a paint never sees half of a
    // sample.
    sealed class StatCell(string label, Color hue)
    {
        public string Label { get; } = label;
        public Color Hue { get; } = hue;
        public TextBlock Name { get; } = new() { Text = label, FontWeight = FontWeights.SemiBold, Foreground = StatLabelInk };
        public TextBlock Value { get; } = new() { Foreground = StatValueInk, HorizontalAlignment = HorizontalAlignment.Right };
        public Canvas Spark { get; } = new() { Height = SparkHeight, ClipToBounds = true };
        public Polygon Fill { get; } = new() { Fill = FrozenBrush(Color.FromArgb(0x38, hue.R, hue.G, hue.B)) };
        public Polyline Line { get; } = new() { Stroke = FrozenBrush(hue), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
        public ImmutableList<double> History { get; set; } = [];
    }

    ISystemStats? stats;
    Button? statsButton;
    UniformGrid? statsGrid;
    TextBlock? statsTip;
    StatCell? cpuCell, memoryCell, diskCell, gpuCell;

    public ISystemStats? Stats
    {
        get => stats;
        init
        {
            stats = value;
            var subscription = value?.Samples.Subscribe(sample => Dispatcher.BeginInvoke(() => PaintStats(sample)));
            Closed += (_, _) => subscription?.Dispose();
        }
    }

    // Starts Task Manager. A seam so the tests can press the line without starting one on whoever runs them.
    // Shell execute, because Task Manager asks to run elevated and only the shell honours that.
    internal Action OpenTaskManager { get; init; } = () =>
        Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose();

    void BuildStats()
    {
        cpuCell = new StatCell("CPU", CpuHue);
        memoryCell = new StatCell("MEM", MemoryHue);
        diskCell = new StatCell("DISK", DiskHue);
        gpuCell = new StatCell("GPU", GpuHue);

        statsGrid = new UniformGrid { Rows = 1, Columns = 4 };
        new[] { cpuCell, memoryCell, diskCell, gpuCell }.ToList().ForEach(cell =>
        {
            cell.Spark.Children.Add(cell.Fill);
            cell.Spark.Children.Add(cell.Line);
            // The sparkline is laid out against the canvas's real width, so a resize (the bar's width, the seam)
            // redraws it rather than stretching the stroke.
            cell.Spark.SizeChanged += (_, e) => { if (e.WidthChanged) PaintSpark(cell); };
            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(cell.Name, Dock.Left);
            head.Children.Add(cell.Name);
            head.Children.Add(cell.Value);
            statsGrid.Children.Add(new StackPanel { Margin = new Thickness(3, 0, 3, 0), Children = { head, cell.Spark } });
        });

        statsTip = new TextBlock();
        statsButton = new Button
        {
            Content = statsGrid,
            Padding = new Thickness(1, 2, 1, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = StatsButtonTemplate,
            Focusable = false,
            // Stretch said outright: without it the button sized to its cells (222 of the line's 273 DIP,
            // measured with TraceStatsGeometry), which across the seam put the disk cell on the line between
            // the screens.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = statsTip,
        };
        System.Windows.Automation.AutomationProperties.SetName(statsButton, "System stats");
        statsButton.Click += (_, _) =>
        {
            MarkPressConsumed();
            if (ClickTrace.On) ClickTrace.Write("stats: open task manager");
            OpenTaskManager();
        };
        StatsLine.Child = statsButton;
        ApplyStatsTextSize();
    }

    // The line's text follows the bar's text size like the rest of the footer (FloatingBar.TextSize.cs). The
    // cells are built once, so a change of setting reaches them through ApplyTextSize.
    void ApplyStatsTextSize() =>
        new[] { cpuCell, memoryCell, diskCell, gpuCell }.OfType<StatCell>().ToList().ForEach(cell =>
        {
            cell.Name.FontSize = Text(7);
            cell.Value.FontSize = Text(7);
        });

    void PaintStats(SystemSample sample)
    {
        if (statsGrid is null || statsTip is null || cpuCell is null || memoryCell is null || diskCell is null || gpuCell is null) return;

        Record(cpuCell, sample.Cpu);
        Record(memoryCell, sample.Memory);
        Record(diskCell, sample.Disk);
        sample.Gpu.Execute(gpu => Record(gpuCell, gpu));

        // No GPU counters: three cells, so the others share the width rather than leaving a dead quarter (and
        // across the seam, four columns with the last one empty; see FitStatsToSeam).
        FitStatsToSeam();

        statsTip.Text = string.Join("\n", new[]
        {
            $"CPU {sample.Cpu:0}%",
            $"Memory {sample.Memory:0}%, {Gigabytes(sample.MemoryUsed)} of {Gigabytes(sample.MemoryTotal)} GB in use",
            $"Disk {sample.Disk:0}% active",
            sample.SystemDriveSize > 0
                ? $"{System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\')} {Gigabytes((ulong)sample.SystemDriveFree)} GB free of {Gigabytes((ulong)sample.SystemDriveSize)} GB"
                : "",
            sample.Gpu.Map(gpu => $"GPU {gpu:0}%").GetValueOrDefault(""),
            "Click for Task Manager",
        }.Where(line => line.Length > 0));

        StatsLine.Visibility = Visibility.Visible;
        TraceStatsGeometry();
    }

    // The line's widths, written to the trace only when they change (like FloatingBar.DumpRowGeometry), so it can
    // say what width the cells were actually given on the seam and off it.
    string? lastStatsGeometry;

    void TraceStatsGeometry()
    {
        if (!ClickTrace.On || statsButton is null || statsGrid is null) return;
        var cells = string.Join(",", statsGrid.Children.OfType<FrameworkElement>().Select(c => $"{c.ActualWidth:0}"));
        var line = $"stats geometry: seam={AcrossSeam} footer={((FrameworkElement)StatsLine.Parent).ActualWidth:0} line={StatsLine.ActualWidth:0} " +
                   $"button={statsButton.ActualWidth:0} grid={statsGrid.ActualWidth:0} cols={statsGrid.Columns} cells={cells} " +
                   $"nowplaying={NowPlayingLine.ActualWidth:0} pins={PinLine.ActualWidth:0}";
        if (line == lastStatsGeometry) return;
        lastStatsGeometry = line;
        ClickTrace.Write(line);
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
    void FitStatsToSeam()
    {
        if (statsGrid is null || gpuCell is null) return;
        var hasGpu = gpuCell.History.Count > 0;
        statsGrid.Children[3].Visibility = hasGpu ? Visibility.Visible : AcrossSeam ? Visibility.Hidden : Visibility.Collapsed;
        statsGrid.Columns = hasGpu || AcrossSeam ? 4 : 3;
    }

    static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // BareStretchButton's stretch plus the strip buttons' faint hover, since the line is one big button and
    // nothing else says it can be pressed.
    static readonly ControlTemplate StatsButtonTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="4"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#1FFFFFFF" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#33FFFFFF" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));
}
