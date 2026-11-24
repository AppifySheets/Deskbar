using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Windows.Markup;
using Deskbar.Windows.Diagnostics;
using Deskbar.Windows.Recovery;
using Deskbar.Windows.Tiling;
using Microsoft.Reactive.Testing;

namespace Deskbar.Windows.Tests;

// Task c1e6c1ae: on 2026-11-25 the desktop heap ran out and Deskbar died eight times in fourteen minutes, every time of
//
//   System.ComponentModel.Win32Exception (8): Not enough memory resources are available to process this command.
//      at MS.Win32.HwndWrapper..ctor(...)
//
// The exceptions below are built the way those crashes carried them: bare, and wrapped in the XamlParseException that
// App.xaml's load put round it. RefusedWindow's header has the reasoning these pin.
public class RefusedWindowTests
{
    static readonly GuiObjects Ordinary = new(User: 600, Gdi: 900);

    static Win32Exception Refused(int code = RefusedWindow.NotEnoughMemory) => new(code);

    [Fact]
    public void The_incidents_exception_is_a_refused_window() =>
        Assert.True(RefusedWindow.Is(Refused()));

    // 01:42, 01:43 and 01:52: App.xaml's load wrapped it, so the chain has to be walked.
    [Fact]
    public void A_refusal_wrapped_by_xaml_loading_is_found() =>
        Assert.True(RefusedWindow.Is(new XamlParseException("Set property 'System.Windows.ResourceDictionary.DeferrableContent' threw an exception.", Refused())));

    [Fact]
    public void A_full_desktop_is_survivable_while_this_copy_holds_ordinary_counts() =>
        Assert.True(RefusedWindow.Survivable(Refused(), Ordinary));

    [Fact]
    public void Out_of_memory_counts_the_same() =>
        Assert.True(RefusedWindow.Survivable(Refused(RefusedWindow.OutOfMemory), Ordinary));

    // This copy at its own quota is a leak, and a fresh process cures that, so the crash path (restart) stays.
    [Theory]
    [InlineData(9_000, 900)]
    [InlineData(600, 9_500)]
    public void A_copy_near_its_own_quota_is_not_survivable(int user, int gdi) =>
        Assert.False(RefusedWindow.Survivable(Refused(), new GuiObjects(user, gdi)));

    [Fact]
    public void No_more_user_handles_is_a_refusal_but_never_survivable()
    {
        Assert.True(RefusedWindow.Is(Refused(RefusedWindow.NoMoreUserHandles)));
        Assert.False(RefusedWindow.Survivable(Refused(RefusedWindow.NoMoreUserHandles), Ordinary));
    }

    // Narrow on the code: access denied, a missing file and the like are real faults and must keep crashing loudly.
    [Theory]
    [InlineData(5)]
    [InlineData(2)]
    [InlineData(1400)]
    public void Other_win32_failures_are_not_refusals(int code)
    {
        Assert.False(RefusedWindow.Is(Refused(code)));
        Assert.False(RefusedWindow.Survivable(Refused(code), Ordinary));
    }

    // Narrow on the type: the managed OutOfMemoryException is a different death (CrashRestart's), not this one.
    [Fact]
    public void A_managed_out_of_memory_is_not_a_refusal() =>
        Assert.False(RefusedWindow.Is(new OutOfMemoryException()));

    // The counts are real numbers from this test process, and far from the quota.
    [Fact]
    public void This_process_counts_are_readable_and_ordinary()
    {
        var mine = GuiObjects.OfThisProcess();
        Assert.True(mine.User >= 0 && mine.Gdi >= 0);
        Assert.False(mine.NearQuota);
    }
}

// The successor a refused start leaves behind (RefusedStart): its arguments, the count, and the end of the chain.
public class RefusedStartTests
{
    [Fact]
    public void An_ordinary_start_is_attempt_zero() =>
        Assert.Equal(0, RefusedStart.AttemptOf([]));

    [Fact]
    public void The_first_successor_is_attempt_one_and_waits_for_the_mutex()
    {
        var next = RefusedStart.NextArgs([]).Value;
        Assert.Equal(1, RefusedStart.AttemptOf(next));
        Assert.Contains(CrashRelaunch.Switch, next);
    }

    // The updater's switch and anything else travel on unchanged; only the count changes, and appears once.
    [Fact]
    public void Other_arguments_are_kept_and_the_count_replaced()
    {
        var next = RefusedStart.NextArgs(["--await-previous", CrashRelaunch.Switch, $"{RefusedStart.Switch}=4"]).Value;
        Assert.Equal(["--await-previous", CrashRelaunch.Switch, $"{RefusedStart.Switch}=5"], next);
    }

    [Fact]
    public void The_chain_ends_after_the_last_attempt()
    {
        Assert.True(RefusedStart.NextArgs([$"{RefusedStart.Switch}={RefusedStart.MaxAttempts - 1}"]).HasValue);
        Assert.True(RefusedStart.NextArgs([$"{RefusedStart.Switch}={RefusedStart.MaxAttempts}"]).HasNoValue);
        Assert.True(RefusedStart.Start(Environment.ProcessPath, [$"{RefusedStart.Switch}={RefusedStart.MaxAttempts}"]).IsFailure);
    }

    [Theory]
    [InlineData("--refused-start=")]
    [InlineData("--refused-start=x")]
    [InlineData("--refused-start=-3")]
    [InlineData("--refused-startle=3")]
    public void A_malformed_count_is_an_ordinary_start(string arg) =>
        Assert.Equal(0, RefusedStart.AttemptOf([arg]));

    // The successor's wait: the minimum, then a probe until one passes.
    [Fact]
    public void The_wait_ends_as_soon_as_a_window_can_be_made()
    {
        var slept = new List<TimeSpan>();
        var answers = new Queue<bool>([false, false, true]);
        Assert.True(RefusedStart.WaitForWindows(() => answers.Dequeue(), slept.Add));
        Assert.Equal([RefusedStart.MinimumWait, RefusedStart.ProbeEvery, RefusedStart.ProbeEvery], slept);
    }

    // ...and a heap that never frees gives up after ProbeFor, so the chain still ends.
    [Fact]
    public void The_wait_gives_up_after_its_limit()
    {
        var slept = TimeSpan.Zero;
        Assert.False(RefusedStart.WaitForWindows(() => false, s => slept += s));
        Assert.Equal(RefusedStart.MinimumWait + RefusedStart.ProbeFor, slept);
    }

    // The probe itself, on a machine with room: a window is made and destroyed.
    [Fact]
    public void A_probe_window_can_be_made_here() =>
        Assert.True(RefusedWindow.CanCreateWindow());

    // Called from a process that is already failing: a start that cannot happen is a failure, never a throw.
    [Fact]
    public void A_launch_that_cannot_happen_is_a_failure_rather_than_a_throw()
    {
        Assert.True(RefusedStart.Start(@"C:\nowhere\Deskbar.exe", []).IsFailure);
        Assert.True(RefusedStart.Start(null, []).IsFailure);
    }
}

// The tiling layer's work-area listener, refused a window at 01:40 (TileSurface.WorkAreaChanged): it is asked for again
// after a wait instead of failing the Tiler's subscription, which took the app down.
public class TileSurfaceRefusedListenerTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // A source refused `refusals` times, then a listener that produces one change and runs on.
    static (IObservable<Unit> Source, Func<int> Subscriptions) RefusedThenWorking(int refusals, int code = RefusedWindow.NotEnoughMemory)
    {
        var subscriptions = 0;
        var source = Observable.Defer(() => ++subscriptions <= refusals
            ? Observable.Throw<Unit>(new Win32Exception(code))
            : Observable.Return(Unit.Default).Concat(Observable.Never<Unit>()));
        return (source, () => subscriptions);
    }

    [Fact]
    public void A_refused_listener_is_asked_for_again_after_the_wait()
    {
        var scheduler = new TestScheduler();
        var (source, subscriptions) = RefusedThenWorking(refusals: 2);
        var seen = new List<Unit>();
        Exception? failed = null;
        using var subscription = TileSurface.RetriedWhenRefused(source, Wait, scheduler, e => RefusedWindow.Survivable(e, new GuiObjects(1, 1)))
            .Subscribe(seen.Add, e => failed = e);

        Assert.Equal(1, subscriptions());
        scheduler.AdvanceBy(Wait.Ticks - 1);
        Assert.Equal(1, subscriptions());
        scheduler.AdvanceBy(1);
        Assert.Equal(2, subscriptions());
        scheduler.AdvanceBy(Wait.Ticks);
        Assert.Equal(3, subscriptions());
        Assert.Single(seen);
        Assert.Null(failed);

        // Working now: nothing subscribes again however long it runs.
        scheduler.AdvanceBy(Wait.Ticks * 10);
        Assert.Equal(3, subscriptions());
    }

    // Anything else still fails the subscription, as it always did.
    [Fact]
    public void Another_failure_still_fails()
    {
        var scheduler = new TestScheduler();
        var (source, _) = RefusedThenWorking(refusals: 1, code: 5);
        Exception? failed = null;
        using var subscription = TileSurface.RetriedWhenRefused(source, Wait, scheduler, e => RefusedWindow.Survivable(e, new GuiObjects(1, 1)))
            .Subscribe(_ => { }, e => failed = e);

        Assert.IsType<Win32Exception>(failed);
    }
}
