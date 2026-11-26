using System.Diagnostics;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Domain;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// The Win32 half of OPENED LINKS (option B in docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md,
// "Opening"): which browser window a link chip's click went into, so the chip can be drawn open while that window
// lives. The rule itself is Core's (LinkWindowWatch.Receiver, OpenedLinks); this only supplies what it asks.
//
//   Watch  the monitor's foreground stream (every foreground change, tracked window or not: a browser window just
//          opened comes to the front before the window list has adopted it, as TerminalInFront measured), a one-shot
//          5 s timer on the UI thread, and the window in front when it fires. Everything runs on the UI thread, where
//          the WinEvent callbacks already arrive, and nothing waits: the timer is a scheduled callback, not a sleep.
//   Alive  IsWindow plus the same process id as when noted, so a reused handle is not taken for the browser.
//   Gone   the monitor's Disappeared, which comes only from DESTROY or the 5 s sweep's IsWindow check, never from a
//          window that merely left the taskbar list (CLAUDE.md, "The window list only ever loses windows").
//
// Nothing here starts a program or opens a page: it only watches, and only after an explicit open.
static class LinkWindowSource
{
    internal static LinkWindows For(WindowMonitor monitor, IVirtualDesktopService desktops, Dispatcher dispatcher, Action<string>? trace)
    {
        var ui = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(dispatcher));
        return new LinkWindows(
            Watch: desktop => Traced(LinkWindowWatch.Receiver(
                    monitor.ForegroundChanged.Select(hwnd => new WindowHandle(hwnd)),
                    Observable.Timer(LinkWindowWatch.Wait, ui).Select(_ => Unit.Default),
                    InFront,
                    ProcessOf,
                    window => desktops.DesktopOf(window).Match(Maybe.From, _ => Maybe<Guid>.None),
                    desktop), trace),
            Alive: window => NativeMethods.IsWindow(window.Handle.Value)
                && NativeMethods.GetWindowThreadProcessId(window.Handle.Value, out var process) != 0
                && process == (uint)window.ProcessId,
            Gone: monitor.Events.Where(e => e.Kind == WindowEventKind.Disappeared).Select(e => e.Window.Handle));
    }

    static Maybe<WindowHandle> InFront() =>
        NativeMethods.GetForegroundWindow() is var hwnd && hwnd != 0 ? new WindowHandle(hwnd) : Maybe<WindowHandle>.None;

    // The window's process id and name. Nothing when the window has no process any more, or the process exited between
    // the two calls (GetProcessById throws then). Only the name is read: no command line, no module path, so this
    // costs nothing like the window list's own lookup (WindowInfoFactory) does.
    static Maybe<(int Id, string Name)> ProcessOf(WindowHandle window)
    {
        if (NativeMethods.GetWindowThreadProcessId(window.Value, out var pid) == 0 || pid == 0) return Maybe<(int, string)>.None;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return ((int)pid, process.ProcessName);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return Maybe<(int, string)>.None;
        }
    }

    // One trace line per watch, so a chip that stayed at rest says why: which window was noted, or that none was.
    static IObservable<LinkWindow> Traced(IObservable<LinkWindow> watch, Action<string>? trace) =>
        trace is null
            ? watch
            : Observable.Defer(() =>
            {
                var noted = false;
                return watch.Do(
                    window =>
                    {
                        noted = true;
                        trace($"bookmark link window: noted {window.Handle.Value:X} (process {window.ProcessId})");
                    },
                    () => { if (!noted) trace("bookmark link window: no browser window on that desktop came forward"); });
            });
}
