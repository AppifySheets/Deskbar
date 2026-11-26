using System.Reactive;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Rehydration;

namespace Deskbar.Core.Bookmarks;

// What the rule below needs to know about a browser window beyond its process name, asked only of a window that is a
// browser at all: its exe (to compare with the default browser), the window's own AppUserModelID and the process's
// command line (to tell an app window from a plain one). Each may be unknown (null).
public sealed record BrowserWindowDetails(string? ExePath, string? AppId, string? CommandLine);

// The lookups the rule asks of the app, in the order it asks them, cheapest first:
//
//   ProcessOf          the window's process id and name; nothing when the window or its process is gone.
//   DetailsOf          BrowserWindowDetails, for a browser only.
//   DesktopOf          the virtual desktop the window sits on: a cross-process COM call, which pumps messages on the UI
//                      thread, so asked last and only of a window that has passed everything else.
//   DefaultBrowserExe  the exe the user's https association opens, or nothing when it cannot be read.
public sealed record LinkWindowLookups(
    Func<WindowHandle, Maybe<(int Id, string Name)>> ProcessOf,
    Func<WindowHandle, BrowserWindowDetails> DetailsOf,
    Func<WindowHandle, Maybe<Guid>> DesktopOf,
    Func<Maybe<string>> DefaultBrowserExe);

// THE RULE for which window a link went into, given the foreground changes after it was opened and a deadline (the app
// passes a 5 s timer on the UI thread, Wait below):
//
//   1. the first window to come to the front before the deadline that RECEIVES LINKS (Receives below) and sits on the
//      bookmark's desktop;
//   2. else, when the deadline comes, the window in front then, if it is such a window. This is the common case of a
//      browser window on that desktop that was in front already: the link opens as a tab in it and nothing changes
//      focus, so there is no foreground change to see;
//   3. else nothing, and the chip stays as it was.
//
// Why "receives links" is narrower than "is a browser" (review of dea775c7): a click from another workspace switches
// first, and the switch brings that workspace's last active window forward ASYNCHRONOUSLY, so that window's foreground
// change is the first one the watch sees. If it is a PWA (an msedge.exe window) or a second browser that is not the
// default, it is not where the link went, and a note on it would hold for that window's whole life.
//
// Pure: every lookup is handed in, and a lookup that throws makes that one window not qualify (traced) rather than
// ending the watch.
public static class LinkWindowWatch
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // Firefox is left out of RosterIdentity's Chromium set on purpose (its arguments name no profile), but it is a
    // browser a link can open in all the same.
    public static bool IsBrowser(string processName) =>
        RosterIdentity.IsChromium(processName) || string.Equals(processName, "firefox", StringComparison.OrdinalIgnoreCase);

    // A window a link opened through the shell can land in: a browser's plain window, of the default browser when the
    // default can be read (any browser when it cannot), and not an app window.
    public static bool Receives(string processName, BrowserWindowDetails details, Maybe<string> defaultBrowserExe) =>
        IsBrowser(processName) && !IsAppWindow(details) && IsDefaultBrowser(processName, details.ExePath, defaultBrowserExe);

    // An app window (a PWA, an installed site) shares the browser's exe, and for an Edge PWA its process and command line
    // too, so only the WINDOW says what it is (docs/superpowers/notes/2026-11-26-bookmarks-probes.md, step 1):
    //
    //   * an AppUserModelID with '!' is the packaged form an MSIX PWA carries (`app.iam.ge-D9839553_en4scw7w9zwzy!App`);
    //     a plain Edge or Chrome window answers `MSEdge` or `Chrome`, or a profile-scoped `Chrome.<profile>`, no '!';
    //   * `_crx_` is the form a Chromium app window that is NOT packaged carries (`Chrome._crx_<id>`). Not measured on
    //     Petre's machine, where every PWA was packaged; it is Chromium's own naming, kept as a cheap safeguard;
    //   * only when the window's AppUserModelID cannot be read at all does the process's command line decide: an
    //     `--app-id` there marks an app window. Not consulted when the window answered, because the probe found the
    //     shared msedge.exe process carrying the --app-id of a third PWA: with the command line deciding, every plain
    //     Edge window in that process would be refused.
    public static bool IsAppWindow(BrowserWindowDetails details) =>
        details.AppId is { Length: > 0 } appId
            ? appId.Contains('!') || appId.Contains("_crx_", StringComparison.OrdinalIgnoreCase)
            : details.CommandLine?.Contains("--app-id", StringComparison.OrdinalIgnoreCase) ?? false;

    // The default browser is where a shell open sends a link, so a different browser's window that comes forward (the
    // one the switch restored, say) did not receive it. Compared by exe path, or by file name when the window's exe
    // path cannot be read (an elevated process hides it). With no readable default, any browser counts.
    static bool IsDefaultBrowser(string processName, string? exePath, Maybe<string> defaultBrowserExe) =>
        defaultBrowserExe.Match(
            exe => exePath is not null
                ? string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)
                : string.Equals(processName, Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase),
            () => true);

    // At most one window, then completes.
    public static IObservable<LinkWindow> Receiver(
        IObservable<WindowHandle> foreground,
        IObservable<Unit> deadline,
        Func<Maybe<WindowHandle>> inFront,
        LinkWindowLookups lookups,
        Guid desktop,
        Action<string>? trace = null)
    {
        Maybe<LinkWindow> Qualifying(WindowHandle window)
        {
            try
            {
                return lookups.ProcessOf(window)
                    .Where(process => IsBrowser(process.Name))
                    .Where(process => Receives(process.Name, lookups.DetailsOf(window), lookups.DefaultBrowserExe()))
                    .Where(_ => lookups.DesktopOf(window).Map(on => on == desktop).GetValueOrDefault(false))
                    .Map(process => new LinkWindow(window, process.Id));
            }
            catch (Exception e)
            {
                // One window's lookup failing (a process exiting under the read, a COM call refused) must not end the
                // watch: an error reaching the stream would stop it from seeing the window the link did go into.
                trace?.Invoke($"bookmark link window: asking about {window.Value:X} threw {e.GetType().Name}: {e.Message}");
                return Maybe<LinkWindow>.None;
            }
        }

        // A foreground change that qualifies, or the deadline's answer (which may be nothing); whichever comes first
        // ends the watch, so a browser coming forward after the deadline is not taken.
        return foreground.Select(Qualifying).Where(found => found.HasValue)
            .Merge(deadline.Take(1).Select(_ => inFront().Bind(Qualifying)))
            .Take(1)
            .Where(found => found.HasValue)
            .Select(found => found.Value);
    }
}
