using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core;

// THE OS IS ASKED WHERE A WINDOW IS ONCE PER MOMENT, NOT ONCE PER REBUILD. Petre: "when activating a bar in
// deskbar, it takes a little while to activate", "can you make it instantaneous?"
//
// Measured, with the switch probe and the click trace side by side: the desktop itself changes within 10ms of
// the mouse release, so Windows is not the slow part. What lags is the bar's own picture, which it rebuilds
// twice per switch (once on arrival, once as focus lands) and each rebuild begins with the overview query:
// IsPinned and then DesktopOf for every known window, both cross-process COM calls into Explorer, about
// seventy per rebuild. The query took 28ms on a quiet machine and 400-500ms right after a switch, when
// Explorer is busiest, with the IsPinned half alone at 476ms and 488ms on two consecutive switches. The
// bar's highlight moved only when that was over.
//
// A switch moves no window, so the answers from a moment ago are still right, and this wrapper hands them
// back without asking. Each window's desktop and pinned state are kept for a short while (2s in the app:
// long enough to cover the two rebuilds of one switch and the burst of rebuilds a window event brings,
// short enough that nothing stays wrong for longer than a glance). The current desktop is kept the same
// way, and follows a switch this app makes or hears about without a lookup.
//
// FORGOTTEN THE MOMENT THIS APP CHANGES THE FACT. Pin, Unpin and MoveWindow go through here, so a window
// this app moves or pins is asked about afresh next time, whatever the clock says. What ANOTHER program
// does to a window (Task View dragging it to a desktop, "show this window on all desktops") shows up
// once the memory lapses, which is the trade this makes and the whole reason the memory is short. A
// failed lookup is never kept: a window minimised to the tray has no desktop to report and gets one back
// when shown, and a null remembered would keep it in Unplaced (the rule the icon cache learned first).
public sealed class PlacementCache(IVirtualDesktopService os, TimeSpan memory, Func<DateTimeOffset>? clock = null) : IVirtualDesktopService
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<WindowHandle, (Guid Desktop, DateTimeOffset At)> desktops = [];
    readonly Dictionary<WindowHandle, (bool Pinned, DateTimeOffset At)> pinned = [];
    (Guid Id, DateTimeOffset At)? current;

    public Result Initialize() => os.Initialize();
    public Result<IReadOnlyList<DesktopInfo>> GetDesktops() => os.GetDesktops();
    public Result<DesktopInfo> Create(string name) => os.Create(name);
    public Result Rename(Guid desktopId, string name) => os.Rename(desktopId, name);

    // A switch that succeeded has changed the current desktop, whether or not the OS has said so yet.
    public Result Switch(Guid desktopId) => os.Switch(desktopId).Tap(() => current = (desktopId, now()));

    // Removing a desktop moves its windows to another one and may change the current desktop, so everything
    // about where windows are is up for asking again.
    public Result Remove(Guid desktopId) => os.Remove(desktopId).Tap(() =>
    {
        desktops.Clear();
        current = null;
    });

    // The same for a removal that names where to land: the windows moved and the current desktop changed.
    public Result Remove(Guid desktopId, Guid fallbackDesktopId) => os.Remove(desktopId, fallbackDesktopId).Tap(() =>
    {
        desktops.Clear();
        current = null;
    });

    public Result MoveWindow(WindowHandle window, Guid desktopId) => os.MoveWindow(window, desktopId).Tap(() => desktops.Remove(window));
    public Result<Guid> DesktopOf(WindowHandle window) => Remembered(desktops, window, () => os.DesktopOf(window));

    public Result Pin(WindowHandle window) => os.Pin(window).Tap(() => pinned.Remove(window));

    // Unpinning puts the window back on one desktop, so its desktop is as stale as its pinned state.
    public Result Unpin(WindowHandle window) => os.Unpin(window).Tap(() =>
    {
        pinned.Remove(window);
        desktops.Remove(window);
    });

    public Result<bool> IsPinned(WindowHandle window) => Remembered(pinned, window, () => os.IsPinned(window));

    public Result<Guid> CurrentDesktop() =>
        current is { } known && now() - known.At < memory
            ? known.Id
            : os.CurrentDesktop().Tap(id => current = (id, now()));

    // The OS's own notice of a change, by whatever means, updates the memory on its way to the subscribers,
    // so a handler that asks for the current desktop in response is answered from memory and answered right.
    public IObservable<Guid> CurrentChanged => os.CurrentChanged.Do(id => current = (id, now()));

    // The remembered answer while it is young, otherwise the OS's, which is remembered only when it succeeds.
    Result<T> Remembered<T>(Dictionary<WindowHandle, (T Value, DateTimeOffset At)> known, WindowHandle window, Func<Result<T>> ask)
    {
        var at = now();
        if (known.TryGetValue(window, out var hit) && at - hit.At < memory) return hit.Value;
        // A miss is the moment to drop every lapsed entry, so windows that closed do not pile up.
        known.Where(k => at - k.Value.At >= memory).Select(k => k.Key).ToList().ForEach(k => known.Remove(k));
        return ask().Tap(value => known[window] = (value, at));
    }
}
