using System.Windows;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Windows.Shell;

namespace Deskbar.Windows.Tests;

// A Windows taskbar button per pinned workspace (TaskbarJumps, TaskbarJumpWindow). Petre: "can it also show up in windows
// taskbar?", "yes and also do the taskbar". How the button behaves on a real taskbar was measured in
// docs/superpowers/notes/2026-11-20-taskbar-jump-spike.md; these tests hold what Deskbar does with it.
//
// The windows here are real WPF windows, parked off every screen as the real ones are. Pinning to every desktop and the
// window monitor's ignore list are recorded rather than performed, so no test changes the machine's desktops.
public class TaskbarJumpsTests
{
    static readonly Guid Messengers = Guid.NewGuid();
    static readonly Guid Deskbar = Guid.NewGuid();

    static WorkspaceJump Outside => new(Messengers, "Messengers", Messengers, IsBack: false);
    static WorkspaceJump Inside => new(Messengers, "↩ Deskbar", Deskbar, IsBack: true);

    sealed class Recorder
    {
        public List<nint> Ignored { get; } = [];
        public List<nint> Pinned { get; } = [];
        public List<Guid> Switched { get; } = [];
        public List<string> Order { get; } = [];

        public TaskbarJumps Jumps() => new(
            pin: h => { Pinned.Add(h.Value); Order.Add("pin"); return Result.Success(); },
            ignore: h => { Ignored.Add(h); Order.Add("ignore"); },
            switchTo: id => { Switched.Add(id); return Result.Success(); });
    }

    static Brush Colour(WorkspaceJump _) => Brushes.Teal;

    // The shell's own reading of a window's taskbar identity is what decides whether two Deskbar windows share a button
    // (spike, finding 1), so this asks the shell rather than trusting the setter.
    [Fact]
    public void A_window_gets_the_app_id_it_is_given() => StaThread.Run(() =>
    {
        var window = new Window { Left = -32000, Top = -32000, Width = 1, Height = 1, ShowActivated = false, ShowInTaskbar = false };
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();

        Assert.True(WindowAppId.Set(hwnd, "Deskbar.Jump.Test").IsSuccess);

        Assert.Equal("Deskbar.Jump.Test", new ShellTaskbarPins().AppIdOf(new WindowHandle(hwnd)));
        window.Close();
    });

    [Fact]
    public void One_window_per_pinned_workspace_titled_as_the_item() => StaThread.Run(() =>
    {
        var recorder = new Recorder();
        var jumps = recorder.Jumps();

        jumps.Show([Outside], Colour);

        Assert.Equal("Messengers", Assert.Single(jumps.Windows).Title);
        jumps.Close();
    });

    // Retitled, not recreated: a new window would put its button at the far end of the taskbar.
    [Fact]
    public void A_rebuild_retitles_and_keeps_the_window() => StaThread.Run(() =>
    {
        var jumps = new Recorder().Jumps();
        jumps.Show([Outside], Colour);
        var first = Assert.Single(jumps.Windows);

        jumps.Show([Inside], Colour);

        Assert.Same(first, Assert.Single(jumps.Windows));
        Assert.Equal("↩ Deskbar", first.Title);
        jumps.Close();
    });

    [Fact]
    public void Unpinning_closes_its_window() => StaThread.Run(() =>
    {
        var jumps = new Recorder().Jumps();
        jumps.Show([Outside], Colour);

        jumps.Show([], Colour);

        Assert.Empty(jumps.Windows);
    });

    // Ignored BEFORE it is pinned and shown, as the bar's stand-in is, or the window monitor would list it as a pinned
    // window on every row (the spike's probe windows did exactly that).
    [Fact]
    public void The_window_is_ignored_before_it_is_pinned() => StaThread.Run(() =>
    {
        var recorder = new Recorder();
        var jumps = recorder.Jumps();

        jumps.Show([Outside], Colour);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(jumps.Windows[0]).Handle;
        Assert.Equal([hwnd], recorder.Ignored);
        Assert.Equal([hwnd], recorder.Pinned);
        Assert.Equal(["ignore", "pin"], recorder.Order);
        jumps.Close();
    });

    [Fact]
    public void A_click_switches_to_the_target_and_the_window_stays_minimized() => StaThread.Run(() =>
    {
        var recorder = new Recorder();
        var jumps = recorder.Jumps();
        jumps.Show([Inside], Colour);
        var window = jumps.Windows[0];

        window.WindowState = WindowState.Normal; // what a click on the minimized window's button does (spike, finding 3)
        DispatcherPump.Flush();

        Assert.Equal([Deskbar], recorder.Switched);
        Assert.Equal(WindowState.Minimized, window.WindowState);
        jumps.Close();
    });

    // Inside the pinned workspace with nowhere to go back to: the click does nothing.
    [Fact]
    public void A_click_with_no_target_switches_nowhere() => StaThread.Run(() =>
    {
        var recorder = new Recorder();
        var jumps = recorder.Jumps();
        jumps.Show([new WorkspaceJump(Messengers, "Messengers", Maybe<Guid>.None, IsBack: false)], Colour);

        jumps.Windows[0].WindowState = WindowState.Normal;
        DispatcherPump.Flush();

        Assert.Empty(recorder.Switched);
        jumps.Close();
    });
}

// Runs what the dispatcher has queued, so work a window posts with BeginInvoke (TaskbarJumpWindow's click) has run.
static class DispatcherPump
{
    public static void Flush() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}
