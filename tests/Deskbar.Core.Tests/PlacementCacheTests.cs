using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// Petre: "when activating a bar in deskbar, it takes a little while to activate", "can you make it
// instantaneous?" The desktop itself switched within 10ms of the click; what lagged was the bar's rebuild,
// and the trace put most of it in the overview query: one IsPinned and one DesktopOf per window, both
// cross-process COM calls into Explorer, 70 of them per rebuild and two rebuilds per switch. Measured at
// 14ms to 488ms for the IsPinned half alone, slowest right after a switch, when Explorer is busiest.
//
// A switch moves no window, so the answers from a moment ago are still right. PlacementCache remembers
// each window's answers for a short while and forgets them the moment this app itself moves, pins or
// unpins the window. Anything another program does to a window shows up after the memory lapses, which
// is the one trade this makes and why the memory is short.
public class PlacementCacheTests
{
    readonly CountingDesktops os = new();
    DateTimeOffset now = new(2026, 11, 2, 17, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    PlacementCache Cache() => new(os, Ttl, () => now);

    readonly WindowHandle window = new(0x1234);
    readonly Guid desktop = Guid.NewGuid();

    [Fact]
    public void A_second_desktop_lookup_within_the_memory_does_not_ask_the_os_again()
    {
        os.Inner.WindowPlacements[window] = desktop;
        var cache = Cache();

        Assert.Equal(desktop, cache.DesktopOf(window).Value);
        now += TimeSpan.FromSeconds(1);
        Assert.Equal(desktop, cache.DesktopOf(window).Value);

        Assert.Equal(1, os.DesktopOfCalls);
    }

    [Fact]
    public void A_lookup_after_the_memory_lapses_asks_again()
    {
        os.Inner.WindowPlacements[window] = desktop;
        var cache = Cache();

        cache.DesktopOf(window);
        now += Ttl;
        var moved = Guid.NewGuid();
        os.Inner.WindowPlacements[window] = moved;

        Assert.Equal(moved, cache.DesktopOf(window).Value);
        Assert.Equal(2, os.DesktopOfCalls);
    }

    [Fact]
    public void A_failed_lookup_is_never_remembered()
    {
        var cache = Cache();

        Assert.True(cache.DesktopOf(window).IsFailure);
        os.Inner.WindowPlacements[window] = desktop;

        Assert.Equal(desktop, cache.DesktopOf(window).Value);
        Assert.Equal(2, os.DesktopOfCalls);
    }

    [Fact]
    public void Pinned_is_remembered_the_same_way()
    {
        var cache = Cache();

        Assert.False(cache.IsPinned(window).Value);
        Assert.False(cache.IsPinned(window).Value);

        Assert.Equal(1, os.IsPinnedCalls);
    }

    [Fact]
    public void Pinning_through_the_cache_forgets_what_it_knew_about_the_window()
    {
        var cache = Cache();
        Assert.False(cache.IsPinned(window).Value);

        Assert.True(cache.Pin(window).IsSuccess);

        Assert.True(cache.IsPinned(window).Value);
        Assert.Equal(2, os.IsPinnedCalls);
    }

    [Fact]
    public void Unpinning_and_moving_through_the_cache_forget_the_windows_desktop()
    {
        os.Inner.WindowPlacements[window] = desktop;
        var cache = Cache();
        cache.DesktopOf(window);
        cache.IsPinned(window);

        var elsewhere = Guid.NewGuid();
        Assert.True(cache.Unpin(window).IsSuccess);
        Assert.True(cache.MoveWindow(window, elsewhere).IsSuccess);

        Assert.Equal(elsewhere, cache.DesktopOf(window).Value);
        Assert.False(cache.IsPinned(window).Value);
        Assert.Equal(2, os.DesktopOfCalls);
        Assert.Equal(2, os.IsPinnedCalls);
    }

    [Fact]
    public void The_current_desktop_is_remembered_and_follows_a_switch_without_asking()
    {
        var cache = Cache();
        var first = cache.CurrentDesktop().Value;
        Assert.Equal(first, cache.CurrentDesktop().Value);

        Assert.True(cache.Switch(desktop).IsSuccess);

        Assert.Equal(desktop, cache.CurrentDesktop().Value);
        Assert.Equal(1, os.CurrentDesktopCalls);
    }

    [Fact]
    public void A_desktop_change_from_outside_updates_the_remembered_current_desktop()
    {
        var cache = Cache();
        using var heard = cache.CurrentChanged.Subscribe(_ => { });
        cache.CurrentDesktop();

        os.Inner.CurrentChangedSubject.OnNext(desktop);

        Assert.Equal(desktop, cache.CurrentDesktop().Value);
        Assert.Equal(1, os.CurrentDesktopCalls);
    }

    // FakeDesktops with a call count per lookup, so a test can say "the OS was not asked".
    sealed class CountingDesktops : IVirtualDesktopService
    {
        public FakeDesktops Inner { get; } = new();
        public int DesktopOfCalls { get; private set; }
        public int IsPinnedCalls { get; private set; }
        public int CurrentDesktopCalls { get; private set; }

        public Result Initialize() => Inner.Initialize();
        public Result<IReadOnlyList<DesktopInfo>> GetDesktops() => Inner.GetDesktops();
        public Result<DesktopInfo> Create(string name) => Inner.Create(name);
        public Result Rename(Guid desktopId, string name) => Inner.Rename(desktopId, name);
        public Result Switch(Guid desktopId) => Inner.Switch(desktopId);
        public Result Remove(Guid desktopId) => Inner.Remove(desktopId);
        public Result Remove(Guid desktopId, Guid fallbackDesktopId) => Inner.Remove(desktopId, fallbackDesktopId);
        public Result MoveWindow(WindowHandle window, Guid desktopId) => Inner.MoveWindow(window, desktopId);
        public Result<Guid> DesktopOf(WindowHandle window) { DesktopOfCalls++; return Inner.DesktopOf(window); }
        public Result Pin(WindowHandle window) => Inner.Pin(window);
        public Result Unpin(WindowHandle window) => Inner.Unpin(window);
        public Result<bool> IsPinned(WindowHandle window) { IsPinnedCalls++; return Inner.IsPinned(window); }
        public Result<Guid> CurrentDesktop() { CurrentDesktopCalls++; return Inner.CurrentDesktop(); }
        public IObservable<Guid> CurrentChanged => Inner.CurrentChanged;
    }
}
