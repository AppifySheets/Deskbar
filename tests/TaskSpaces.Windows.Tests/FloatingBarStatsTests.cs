using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Windows.Performance;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// The stats line above the now-playing line (Petre: "let's show system stats, cpu, memory, disk, gpu", then "b":
// a sparkline each, and a click for Task Manager). Every bar here gets a FakeStats or none, so nothing depends on
// the load of whoever runs the suite; the one test that reads the real counters only checks they are in range.
public class FloatingBarStatsTests(ITestOutputHelper output)
{
    sealed class FakeStats : ISystemStats
    {
        public readonly Subject<SystemSample> Next = new();
        public IObservable<SystemSample> Samples => Next;
    }

    static SystemSample Sample(double cpu = 23, double memory = 61, double disk = 4, double? gpu = 12) =>
        new(cpu, memory, 20UL << 30, 32UL << 30, disk, gpu is { } g ? g : Maybe<double>.None, 120L << 30, 953L << 30);

    static FloatingBar Bar(ISystemStats? stats, Action? openTaskManager = null)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager)
        {
            Left = -32000,
            Top = -32000,
            SendShellKeys = _ => { },
            Stats = stats,
            OpenTaskManager = openTaskManager ?? (() => { }),
        };
        bar.ShowBar();
        bar.UpdateLayout();
        return bar;
    }

    static Border Line(FloatingBar bar) => (Border)bar.FindName("StatsLine")!;
    static Button Button(FloatingBar bar) => (Button)Line(bar).Child;
    static UniformGrid Grid(FloatingBar bar) => (UniformGrid)Button(bar).Content;

    // The samples arrive through the dispatcher, as the real ones do from their pool thread.
    static void Push(FloatingBar bar, FakeStats stats, SystemSample sample)
    {
        stats.Next.OnNext(sample);
        bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        bar.UpdateLayout();
    }

    static IEnumerable<string> Values(FloatingBar bar) =>
        Grid(bar).Children.OfType<StackPanel>()
            .Where(cell => cell.Visibility == Visibility.Visible)
            .Select(cell => ((DockPanel)cell.Children[0]).Children.OfType<TextBlock>().Last().Text);

    [Fact]
    public void Without_a_stats_source_there_is_no_line() => StaThread.Run(() =>
    {
        var bar = Bar(null);
        Assert.Equal(Visibility.Collapsed, Line(bar).Visibility);
        bar.Close();
    });

    // Hidden until there is something to show, rather than four empty graphs reading 0%.
    [Fact]
    public void The_line_appears_with_the_first_sample() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var bar = Bar(stats);
        Assert.Equal(Visibility.Collapsed, Line(bar).Visibility);

        Push(bar, stats, Sample());
        output.WriteLine(string.Join(" ", Values(bar)));

        Assert.Equal(Visibility.Visible, Line(bar).Visibility);
        Assert.Equal(["23%", "61%", "4%", "12%"], Values(bar));
        bar.Close();
    });

    // No GPU counters: three cells sharing the width, not a GPU stuck at 0%.
    [Fact]
    public void Without_a_gpu_reading_the_line_has_three_cells() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var bar = Bar(stats);
        Push(bar, stats, Sample(gpu: null));

        Assert.Equal(3, Grid(bar).Columns);
        Assert.Equal(["23%", "61%", "4%"], Values(bar));
        bar.Close();
    });

    // The tooltip carries what the cells have no room for: memory in gigabytes and the system drive's space.
    [Fact]
    public void The_tooltip_has_the_details() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var bar = Bar(stats);
        Push(bar, stats, Sample());

        var tip = ((TextBlock)Button(bar).ToolTip).Text;
        output.WriteLine(tip);
        Assert.Contains("20.0 of 32.0 GB in use", tip);
        Assert.Contains("120.0 GB free of 953.0 GB", tip);
        Assert.Contains("Task Manager", tip);
        bar.Close();
    });

    [Fact]
    public void A_click_opens_task_manager() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var opened = 0;
        var bar = Bar(stats, () => opened++);
        Push(bar, stats, Sample());

        Button(bar).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, opened);
        Assert.Equal("System stats", AutomationProperties.GetName(Button(bar)));
        bar.Close();
    });

    // A minute of history: the 61st sample pushes the first one out.
    [Fact]
    public void The_history_keeps_the_last_minute()
    {
        var history = Enumerable.Range(1, 61).Aggregate(ImmutableList<double>.Empty, (h, v) => FloatingBar.StatsWindow(h, v));
        Assert.Equal(FloatingBar.StatsHistory, history.Count);
        Assert.Equal(2, history[0]);
        Assert.Equal(61, history[^1]);
    }

    // The newest sample sits at the right edge and a filling history grows in from the right; 0% and 100% stay a
    // pixel inside the top and bottom.
    [Fact]
    public void Sparkline_points_grow_in_from_the_right()
    {
        var points = FloatingBar.SparkPoints([0, 50, 100], width: 59, height: 22);
        output.WriteLine(string.Join(" ", points));

        Assert.Equal(3, points.Count);
        Assert.Equal(59, points[^1].X, 6);
        Assert.Equal(57, points[0].X, 6);
        Assert.Equal(21, points[0].Y, 6);
        Assert.Equal(11, points[1].Y, 6);
        Assert.Equal(1, points[2].Y, 6);
        Assert.Empty(FloatingBar.SparkPoints([], 59, 22));
        Assert.Empty(FloatingBar.SparkPoints([5], 0, 22));
    }

    // Disk active time is the busiest disk, from idle time, with the _Total average left out.
    [Fact]
    public void Disk_is_the_busiest_physical_disk()
    {
        Assert.Equal(70, StatsMath.BusiestDisk([("0 C:", 95), ("1 D:", 30), ("_Total", 62.5)]), 6);
        Assert.Equal(0, StatsMath.BusiestDisk([]));
        Assert.Equal(100, StatsMath.BusiestDisk([("0 C:", -3)]));
    }

    // Task Manager's GPU reading: per engine the sum over processes, then the busiest engine.
    [Fact]
    public void Gpu_is_the_busiest_engine_summed_over_processes()
    {
        var engines = new[]
        {
            ("pid_100_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D", 20.0),
            ("pid_200_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D", 15.0),
            ("pid_100_luid_0x00000000_0x0000D1B5_phys_0_eng_3_engtype_VideoDecode", 30.0),
            ("pid_300_luid_0x00000000_0x0000E000_phys_0_eng_0_engtype_3D", 10.0),
        };
        Assert.Equal(35, StatsMath.BusiestGpuEngine(engines).Value, 6);
        Assert.True(StatsMath.BusiestGpuEngine([]).HasNoValue);
        Assert.Equal(100, StatsMath.BusiestGpuEngine([("pid_1_luid_a_phys_0_eng_0_engtype_3D", 80), ("pid_2_luid_a_phys_0_eng_0_engtype_3D", 70)]).Value);
    }

    // The real counters on this machine: read, logged and range-checked. Reading counters changes nothing, so
    // it is safe as a routine test; a CI runner without GPU counters just reports no GPU.
    [Fact]
    public void The_real_counters_read_in_range()
    {
        using var stats = new SystemStats();
        var sample = stats.Samples.Take(2).Timeout(TimeSpan.FromSeconds(10)).LastAsync().Wait();
        output.WriteLine($"{sample}");

        Assert.InRange(sample.Cpu, 0, 100);
        Assert.InRange(sample.Memory, 1, 100);
        Assert.InRange(sample.Disk, 0, 100);
        Assert.True(sample.MemoryTotal > 0);
        Assert.True(sample.SystemDriveSize > 0);
        sample.Gpu.Execute(gpu => Assert.InRange(gpu, 0, 100));
    }
}
