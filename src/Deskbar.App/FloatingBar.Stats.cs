using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Performance;
using Deskbar.Windows.Processes;

namespace Deskbar.App;

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
//
// THE LINE CAN BE DETACHED: right-click it and it moves into a small always-on-top window of its own
// (DetachedStatsWindow), which stays over a borderless-windowed game or another full-screen window where the bar
// steps aside. While detached the bar hides its line (ApplyStatsVisibility).
public partial class FloatingBar
{
    // The cells, their hues, the sparkline maths and the tooltip text moved into StatsStrip, so the detached stats
    // window draws exactly what the bar's line does. These stay because the tests (and the strip's callers) reach
    // them through the bar.
    internal const int StatsHistory = StatsStrip.StatsHistory;
    internal static ImmutableList<double> StatsWindow(ImmutableList<double> history, double value) => StatsStrip.StatsWindow(history, value);
    internal static IReadOnlyList<Point> SparkPoints(IReadOnlyList<double> history, double width, double height) => StatsStrip.SparkPoints(history, width, height);

    ISystemStats? stats;
    Button? statsButton;
    StatsStrip? statsStrip;
    TextBlock? statsTip;
    // Whether a first reading has come in: the line stays collapsed until then (see ApplyStatsVisibility).
    bool statsArrived;

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
    // Through the shell, because Task Manager asks to run elevated and only the shell honours that: the loose exe
    // shell-executes it, and the Store copy goes through cmd's `start`, which shell-executes too, from a cmd that
    // has broken away from Deskbar's package (DetachedLauncher, store packaging design).
    //
    // A failed start (the commonest is a declined UAC prompt) is traced and otherwise ignored. Process.Start used
    // to throw it straight into the dispatcher, where an unhandled exception ends the app; nothing is lost by
    // Task Manager not opening, so it is not worth a dialog either.
    internal Action OpenTaskManager { get; init; } = () =>
        DetachedLauncher.Start("taskmgr.exe", [], null)
            .TapError(e => ClickTrace.Write($"stats: Task Manager did not start: {e}"));

    // Pulls the stats line out of the bar into its own window (DetachedStatsWindow, owned by App, which is why this
    // is a seam). Offered by the line's right-click menu; null in the tests that do not care.
    internal Action? DetachStats { get; init; }

    void BuildStats()
    {
        statsStrip = new StatsStrip();

        statsTip = new TextBlock();
        statsButton = new Button
        {
            Content = statsStrip,
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
        // Right-click offers to detach the line. The button's own menu wins over the bar's background menu, because
        // the innermost one opens; a right-click raises no Click, so it never starts Task Manager.
        var menu = new ContextMenu();
        HoldFadeWhileOpen(menu);
        var detach = new MenuItem { Header = "Detach stats into a window", Icon = MenuGlyph("⧉") };
        detach.Click += (_, _) => DetachStats?.Invoke();
        menu.Items.Add(detach);
        statsButton.ContextMenu = menu;
        StatsLine.Child = statsButton;
        ApplyStatsTextSize();
    }

    // The line's text follows the bar's text size like the rest of the footer (FloatingBar.TextSize.cs). The
    // cells are built once, so a change of setting reaches them through ApplyTextSize.
    void ApplyStatsTextSize() => statsStrip?.ApplyTextSize(Text(7));

    void PaintStats(SystemSample sample)
    {
        if (statsStrip is null || statsTip is null) return;

        statsStrip.Paint(sample, AcrossSeam);
        statsTip.Text = StatsStrip.TipText(sample, "Click for Task Manager");

        statsArrived = true;
        ApplyStatsVisibility();
        TraceStatsGeometry();
    }

    // THE LINE IS HIDDEN WHILE THE STATS ARE DETACHED (AppState.DetachedStats): the window shows them instead, and
    // a bar that kept the line would show every number twice. Otherwise it is shown once there is a reading, as
    // before. Called from PaintStats and from the bar's state-change path, so the line goes the moment the user
    // detaches and returns the moment they put it back, not a second later when the next sample arrives. The bar
    // goes on recording samples while the line is hidden, so the returning line has its full minute.
    void ApplyStatsVisibility() =>
        StatsLine.Visibility = statsArrived && manager.State.DetachedStats is not { Shown: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

    void FitStatsToSeam() => statsStrip?.FitToSeam(AcrossSeam);

    // The line's widths, written to the trace only when they change (like FloatingBar.DumpRowGeometry), so it can
    // say what width the cells were actually given on the seam and off it.
    string? lastStatsGeometry;

    void TraceStatsGeometry()
    {
        if (!ClickTrace.On || statsButton is null || statsStrip is null) return;
        var line = $"stats geometry: seam={AcrossSeam} footer={((FrameworkElement)StatsLine.Parent).ActualWidth:0} line={StatsLine.ActualWidth:0} " +
                   $"button={statsButton.ActualWidth:0} grid={statsStrip.ActualWidth:0} cols={statsStrip.Columns} cells={statsStrip.CellWidths} " +
                   $"nowplaying={NowPlayingLine.ActualWidth:0} pins={PinLine.ActualWidth:0}";
        if (line == lastStatsGeometry) return;
        lastStatsGeometry = line;
        ClickTrace.Write(line);
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
