using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE STATS VIEW (task 521d2b07; spec docs/superpowers/specs/2026-11-25-task-stats-design.md). Petre: "build stats for
// tasks, like a burndown or similar, with links to each task". The toolbar's Stats switch shows it in place of the List or
// the Matrix; TaskStats (Core) makes the numbers and this only draws them. Top to bottom:
//
//   Open 34   Added, 7 days 120   Finished, 7 days 98   Median to finish 5.2 h
//   OPEN AT THE END OF THE DAY          a line over the last 14 days, today at the right
//   ADDED AND FINISHED PER DAY  ■ Added ■ Finished      two bars a day
//   12 13 14 ... 25                                      the day of the month under each column
//   TUE 25 NOV                                           the picked day, today until a column is clicked
//   FINISHED 6   [chip] title                            each line a link: the panel leaves Stats and shows the task
//   ADDED 9      [chip] title                            (TasksBoard.OpenFromStats)
//
// A burn-up rather than a burndown: the list has no fixed amount of work to burn down (about twenty new tasks a day), so
// the open line is the burndown's line and the bars say why it moved.
//
// THE CHARTS are canvases painted from their own size (SizeChanged), so they follow the panel's width without a redraw of
// the view. Over each chart lies a grid of one transparent column per day: the column is the hover target (its tooltip
// gives the day's three numbers) and the click that picks the day, and the picked day's column is tinted in both charts.
// Colours follow the dataviz palette's dark steps, checked with its validator against this panel's #1E1F24: blue for
// added (also the open line, a single series of its own chart), aqua-green for finished. Orange is left alone, since
// orange means "waiting on you" everywhere in Deskbar.
public partial class TasksWindow
{
    static readonly Brush AddedInk = Frozen(0xFF, 0x33, 0x8C, 0xED);
    static readonly Brush FinishedInk = Frozen(0xFF, 0x00, 0xA8, 0x88);
    static readonly Brush OpenFill = Frozen(0x33, 0x33, 0x8C, 0xED);
    static readonly Brush GridInk = Frozen(0xFF, 0x30, 0x32, 0x38);
    static readonly Brush PickedDay = Frozen(0x1A, 0xFF, 0xFF, 0xFF);
    static readonly Brush PanelSurface = Frozen(0xFF, 0x1E, 0x1F, 0x24);

    const double LineChartHeight = 84;
    const double BarChartHeight = 96;

    // Whether the Stats view is on show (DrawRows), and the day picked in it: none means the last day, today. Kept across
    // redraws so a status pulse does not jump back to today, and dropped when it falls out of the days shown.
    bool statsShown;
    DateOnly? statsDay;

    // For the tests: whether Stats is on show, the day picked, and the task lines it lists (their ids, in order).
    internal bool StatsShown => statsShown;
    internal DateOnly? StatsDay => statsDay;
    internal IReadOnlyList<Guid> StatsLinks { get; private set; } = [];

    /// <summary>Picks a day of the Stats view, as a click on its column does (the tests use it too).</summary>
    internal void PickStatsDay(DateOnly day)
    {
        statsDay = day;
        if (latest?.Stats is { } stats) DrawStats(stats);
    }

    static string StatsSignature(TaskStatsView? stats) =>
        stats is null ? "" :
        $"{stats.OpenNow}|{stats.AddedThisWeek}|{stats.FinishedThisWeek}|{stats.MedianToFinish}|{stats.Estimated}|" +
        string.Join('\u0002', stats.Days.Select(d => $"{d.Date}:{d.OpenAtEnd}:" +
            string.Join(',', d.Added.Concat(d.Finished).Select(t => $"{t.Id}{t.Title}{t.Status}"))));

    void DrawStats(TaskStatsView stats)
    {
        StatsPanel.Children.Clear();
        if (statsDay is { } kept && stats.Days.All(d => d.Date != kept)) statsDay = null;
        var picked = stats.Days.FirstOrDefault(d => d.Date == statsDay) ?? stats.Days[^1];

        StatsPanel.Children.Add(Tiles(stats));
        StatsPanel.Children.Add(Caption("Open at the end of the day"));
        StatsPanel.Children.Add(Chart(stats, picked, LineChartHeight, PaintOpenLine));
        StatsPanel.Children.Add(BarsCaption());
        StatsPanel.Children.Add(Chart(stats, picked, BarChartHeight, PaintBars));
        StatsPanel.Children.Add(DayNumbers(stats, picked));
        if (stats.Estimated)
            StatsPanel.Children.Add(new TextBlock
            {
                Text = "Finish times from before this version are estimated: status changes were not recorded then.",
                FontSize = 10,
                Foreground = HeaderInk,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        StatsPanel.Children.Add(Caption(picked.Date.ToString("ddd d MMM", CultureInfo.CurrentCulture), top: 14));
        StatsPanel.Children.Add(DayList("Finished", picked.Finished));
        StatsPanel.Children.Add(DayList("Added", picked.Added));
        StatsLinks = picked.Finished.Concat(picked.Added).Select(t => t.Id).ToArray();
    }

    // --- the four numbers ---

    static UIElement Tiles(TaskStatsView stats)
    {
        var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        new (string Label, string Value, string Tip)[]
        {
            ("Open", $"{stats.OpenNow}", "Tasks not finished yet: backlog, in progress and waiting on you"),
            ("Added, 7 days", $"{stats.AddedThisWeek}", "Tasks added in the last 7 days"),
            ("Finished, 7 days", $"{stats.FinishedThisWeek}", "Tasks marked Done or closed in the last 7 days"),
            ("Median to finish", stats.MedianToFinish.HasValue ? TaskStats.Span(stats.MedianToFinish.Value) : "none",
                "The middle time from added to finished, over the tasks finished in the last 14 days"),
        }.ToList().ForEach(tile => tiles.Children.Add(new Border
        {
            Background = TileFill,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 6, 6),
            MinWidth = 76,
            ToolTip = tile.Tip,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = tile.Value, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ink },
                    new TextBlock { Text = tile.Label, FontSize = 10, Foreground = HeaderInk },
                },
            },
        }));
        return tiles;
    }

    static TextBlock Caption(string text, double top = 10) => new()
    {
        Tag = "task-group",
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        Foreground = HeaderInk,
        Margin = new Thickness(0, top, 0, 4),
    };

    // The bar chart's caption with its legend: two series, so the legend is always there (dataviz), each swatch beside
    // its name in the text ink, never the series colour.
    static UIElement BarsCaption()
    {
        var line = new DockPanel { Margin = new Thickness(0, 12, 0, 4) };
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        new[] { ("Added", AddedInk), ("Finished", FinishedInk) }.ToList().ForEach(entry =>
        {
            legend.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = entry.Item2, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = entry.Item1, FontSize = 10, Foreground = DimInk, VerticalAlignment = VerticalAlignment.Center });
        });
        DockPanel.SetDock(legend, Dock.Right);
        line.Children.Add(legend);
        line.Children.Add(new TextBlock { Tag = "task-group", Text = "ADDED AND FINISHED PER DAY", FontSize = 10, Foreground = HeaderInk, VerticalAlignment = VerticalAlignment.Center });
        return line;
    }

    // --- the charts ---

    // A chart: a canvas painted from its own size, under a grid of one transparent column per day (hover and click).
    UIElement Chart(TaskStatsView stats, TaskStatsDay picked, double height, Action<Canvas, TaskStatsView> paint)
    {
        var canvas = new Canvas { Height = height, ClipToBounds = false, IsHitTestVisible = false };
        canvas.SizeChanged += (_, _) => paint(canvas, stats);
        var columns = DayColumns(stats, picked, day => new Border
        {
            Background = day.Date == picked.Date ? PickedDay : Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.Hand,
            ToolTip = DayTip(day),
        });
        return new Grid { Height = height, Margin = new Thickness(0, 0, 0, 2), Children = { columns, canvas } };
    }

    // One star column per day, each holding what `cell` makes for it; a click on a cell picks its day.
    Grid DayColumns(TaskStatsView stats, TaskStatsDay picked, Func<TaskStatsDay, FrameworkElement> cell)
    {
        var grid = new Grid();
        stats.Days.Select((day, i) => (day, i)).ToList().ForEach(x =>
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var element = cell(x.day);
            element.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                PickStatsDay(x.day.Date);
            };
            Grid.SetColumn(element, x.i);
            grid.Children.Add(element);
        });
        return grid;
    }

    static string DayTip(TaskStatsDay day) =>
        $"{day.Date.ToString("dddd d MMMM", CultureInfo.CurrentCulture)}\n" +
        $"{day.Added.Count} added, {day.Finished.Count} finished, {day.OpenAtEnd} open at the end";

    // The day of the month under each column, the picked one in the text ink and bold, the rest grey.
    UIElement DayNumbers(TaskStatsView stats, TaskStatsDay picked) =>
        DayColumns(stats, picked, day => new TextBlock
        {
            Text = day.Date.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 9,
            Foreground = day.Date == picked.Date ? Ink : HeaderInk,
            FontWeight = day.Date == picked.Date ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = DayTip(day),
        });

    // A scale's top: the largest value rounded up to a step that reads well (1, 2, 5 times a power of ten), at least 1.
    static double NiceTop(double max)
    {
        if (max <= 1) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(max)));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(step => step * power).First(top => top >= max);
    }

    // The hairline at the top of the scale with its value at the left, and the baseline: recessive, as dataviz asks.
    static void PaintScale(Canvas canvas, double top, double width, double height)
    {
        canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = 0.5, Y2 = 0.5, Stroke = GridInk, StrokeThickness = 1 });
        canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = height - 0.5, Y2 = height - 0.5, Stroke = GridInk, StrokeThickness = 1 });
        var label = new TextBlock { Text = top.ToString("0.#", CultureInfo.InvariantCulture), FontSize = 9, Foreground = HeaderInk, Background = PanelSurface, Padding = new Thickness(0, 0, 3, 0) };
        Canvas.SetLeft(label, 0);
        Canvas.SetTop(label, 1);
        canvas.Children.Add(label);
    }

    // The open count at each day's end: a 2 DIP line over a faint area, a dot with a surface ring on every day.
    static void PaintOpenLine(Canvas canvas, TaskStatsView stats)
    {
        canvas.Children.Clear();
        var (width, height) = (canvas.ActualWidth, canvas.ActualHeight);
        if (width <= 0 || height <= 0) return;
        var top = NiceTop(stats.Days.Max(d => d.OpenAtEnd));
        var step = width / stats.Days.Count;
        var points = stats.Days.Select((d, i) => new Point(step * (i + 0.5), height - 1 - (height - 8) * d.OpenAtEnd / top)).ToArray();
        PaintScale(canvas, top, width, height);
        var area = new PointCollection(points.Prepend(new Point(points[0].X, height - 1)).Append(new Point(points[^1].X, height - 1)));
        canvas.Children.Add(new Polygon { Points = area, Fill = OpenFill });
        canvas.Children.Add(new Polyline { Points = new PointCollection(points), Stroke = AddedInk, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        points.ToList().ForEach(p =>
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = AddedInk, Stroke = PanelSurface, StrokeThickness = 2 };
            Canvas.SetLeft(dot, p.X - 4);
            Canvas.SetTop(dot, p.Y - 4);
            canvas.Children.Add(dot);
        });
        // The last day's value, labelled at its dot: the one number a reader of a burndown looks for.
        var last = new TextBlock { Text = $"{stats.Days[^1].OpenAtEnd}", FontSize = 10, Foreground = Ink };
        last.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(last, Math.Min(width - last.DesiredSize.Width, points[^1].X - last.DesiredSize.Width / 2));
        Canvas.SetTop(last, Math.Max(0, points[^1].Y - 6 - last.DesiredSize.Height));
        canvas.Children.Add(last);
    }

    // Two bars a day, added then finished, rounded at their data end and standing on the baseline, a 2 DIP gap between.
    static void PaintBars(Canvas canvas, TaskStatsView stats)
    {
        canvas.Children.Clear();
        var (width, height) = (canvas.ActualWidth, canvas.ActualHeight);
        if (width <= 0 || height <= 0) return;
        var top = NiceTop(stats.Days.Max(d => Math.Max(d.Added.Count, d.Finished.Count)));
        var step = width / stats.Days.Count;
        var bar = Math.Clamp(step * 0.3, 3, 10);
        PaintScale(canvas, top, width, height);
        stats.Days.Select((d, i) => (d, i)).ToList().ForEach(x =>
        {
            var centre = step * (x.i + 0.5);
            new[] { (Count: x.d.Added.Count, Ink: AddedInk, Left: centre - 1 - bar), (Count: x.d.Finished.Count, Ink: FinishedInk, Left: centre + 1) }
                .Where(b => b.Count > 0).ToList().ForEach(b =>
                {
                    var tall = Math.Max(2, (height - 8) * b.Count / top);
                    var rect = new Border { Width = bar, Height = tall, Background = b.Ink, CornerRadius = new CornerRadius(Math.Min(2, bar / 2), Math.Min(2, bar / 2), 0, 0) };
                    Canvas.SetLeft(rect, b.Left);
                    Canvas.SetTop(rect, height - 1 - tall);
                    canvas.Children.Add(rect);
                });
        });
    }

    // --- the picked day's tasks ---

    // "FINISHED 6" and its lines, or a quiet "nothing" line. Each line is the task's status and its title; a click is the
    // link to the task (TasksWindowActions.OpenFromStats).
    UIElement DayList(string name, IReadOnlyList<TaskItem> tasks)
    {
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        list.Children.Add(new TextBlock { Text = $"{name} {tasks.Count}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = DimInk, Margin = new Thickness(0, 2, 0, 2) });
        if (tasks.Count == 0)
            list.Children.Add(new TextBlock { Text = "None", FontSize = 11, Foreground = HeaderInk, Margin = new Thickness(4, 0, 0, 0) });
        tasks.ToList().ForEach(task => list.Children.Add(StatsLink(task)));
        return list;
    }

    Button StatsLink(TaskItem task)
    {
        var title = new TextBlock
        {
            Text = task.Title,
            FontSize = 12,
            Foreground = Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var line = new DockPanel { LastChildFill = true };
        var chip = (FrameworkElement?)TaskStatusMarks.Chip(task.Status) ?? new TextBlock
        {
            // A closed task has no chip on a line (TaskStatusMarks.Chip); here it says so, since the list mixes both.
            Text = "Closed",
            FontSize = 10,
            Foreground = HeaderInk,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chip.Margin = new Thickness(0, 0, 8, 0);
        chip.Width = double.NaN;
        var chipCell = new Border { Width = 80, Child = chip };
        DockPanel.SetDock(chipCell, Dock.Left);
        line.Children.Add(chipCell);
        line.Children.Add(title);
        var button = Flat(line, task.Title, () => Actions?.OpenFromStats?.Invoke(task.Id), new Thickness(4, 3, 4, 3));
        // LinkButton (TasksWindow.xaml): Flat's look with the content from the left edge, and a faint fill under the pointer.
        button.Template = (ControlTemplate)FindResource("LinkButton");
        button.Tag = task.Id;
        button.Background = Brushes.Transparent;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.ToolTip = $"Show this task: {task.Title}";
        // The title underlines under the pointer, so the line reads as a link.
        button.MouseEnter += (_, _) => title.TextDecorations = TextDecorations.Underline;
        button.MouseLeave += (_, _) => title.TextDecorations = null;
        return button;
    }
}
