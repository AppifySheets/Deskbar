using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// The bar without the old timers (docs/superpowers/specs/2026-11-22-tasks-board-design.md: "no timers, no countdown").
// The timer version of tasks drew a stopwatch in the bottom strip and a pie in the caption of a workspace row with a
// running timer; both are gone, and these tests keep them gone. The task list that replaces them has its own tests.
public class FloatingBarTasksTests
{
    // The bar over the shared two-workspace harness (GEPHA current, Sparrow beside it). SendShellKeys and PressSpace
    // are no-ops so nothing reaches a real shell.
    sealed class TasksBar : IDisposable
    {
        public required Harness Harness { get; init; }
        public required FloatingBar Bar { get; init; }

        public DockPanel Strip => (DockPanel)Bar.FindName("ShellStrip")!;

        public Panel Rows => (Panel)Bar.FindName("Rows")!;

        public void Dispose() => Bar.Close();
    }

    static TasksBar Build()
    {
        var harness = Harness.Build();
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, PressSpace = () => { } };
        bar.ShowBar();
        bar.UpdateLayout();
        return new TasksBar { Harness = harness, Bar = bar };
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    // The stopwatch was a strip button named "Tasks", laid out just inward of the language code even while it was
    // collapsed. With the Tasks strip item left switched on (the default), the strip holds no such button.
    [Fact]
    public void The_strip_has_no_stopwatch() => StaThread.Run(() =>
    {
        using var t = Build();

        Assert.DoesNotContain(StripItem.Tasks, t.Harness.Manager.State.HiddenStripItems);
        Assert.DoesNotContain(Descendants(t.Strip).OfType<Button>(), b => AutomationProperties.GetName(b) == "Tasks");
    });

    // The pie was tagged "task-pie" in a workspace caption. No row of the bar draws one now.
    [Fact]
    public void A_row_draws_no_pie() => StaThread.Run(() =>
    {
        using var t = Build();

        Assert.NotEmpty(Descendants(t.Rows));
        Assert.DoesNotContain(Descendants(t.Rows).OfType<FrameworkElement>(), e => Equals(e.Tag, "task-pie"));
    });
}
