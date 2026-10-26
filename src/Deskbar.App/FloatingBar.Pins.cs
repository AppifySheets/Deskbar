using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;
using Deskbar.Windows.Shell;

namespace Deskbar.App;

// THE TASKBAR'S PINS, as a dock line above the strip. Petre: "can you also show pinned icons somewhere?",
// "taskbar pinned", then "a" from three mockups.
//
//   order    the taskbar's own, read from Windows (ShellTaskbarPins), with the taskbar's icons
//   marker   a short bar under a pin with a window in this workspace, a dot under one with a window only
//            somewhere else, nothing under one that is not running
//   click    jump to its window here, as the taskbar does; with none here, start it here
//   middle   or Shift+click: always a new window
//   right    its name, New window, Open file location, Unpin from taskbar
//
// Unpinning runs the item's own shell verb (see ShellTaskbarPins.Unpin); pinning has no such verb and stays
// on the taskbar. Either way the line follows the taskbar: the pin list's change counter is polled every
// few seconds.
public partial class FloatingBar
{
    // How big a pin is, after four rounds. The first cut was 16 DIP icons in 19 DIP cells, ten to a line.
    // "make them bigger, it's hard to focus on them" went to 22 in 28, two lines of five, and that was "too bad;
    // much smaller, and no gaps please", "as it was, just a bit larger": 18 in one tight line. Then, looking at
    // that line: "distribute evenly; two rows would be fine, don't cram into one", which spread his ten over two
    // even lines of five. Then, with the pins moved under Start: "i don't really like this distribution, also
    // sizes of those icons against start and search", and "b" from four mockups: the pins PACKED from the left
    // in 26 DIP cells, the same cells and 18 DIP size as Start and search beside them (PackedPinPanel), which
    // fits his ten on one line of a 273 DIP bar and wraps, still packed, when more do not fit.
    const double PinIconSize = 18, PinCellWidth = 26, PinHeight = 26;

    static readonly Brush PinHereMark = Frozen(0xC0, 0xFF, 0xFF, 0xFF);
    static readonly Brush PinElsewhereMark = Frozen(0x66, 0xFF, 0xFF, 0xFF);

    ITaskbarPins? pins;
    int pinsVersion = -1;
    IReadOnlyList<TaskbarPin> shownPins = [];
    readonly DispatcherTimer pinWatch = new() { Interval = TimeSpan.FromSeconds(3) };

    // Per pin (by ParsingName): its icon, never caching a miss, since a shell icon can arrive late (the
    // project's rule, learnt on window icons).
    readonly Dictionary<string, ImageSource> pinImages = [];

    // Per window: its AppUserModelID, asked of the shell on a pool thread and never on the UI thread (see
    // AskAppIds). An ID, once found, is kept while the window is in the overview. "This window has none" is
    // believed for NoAppIdRecheck and then asked again, because an app can set its ID after its window
    // appears, and the project's rule is that a miss which can turn into a hit is never cached for good.
    readonly Dictionary<WindowHandle, string> windowAppIds = [];
    readonly Dictionary<WindowHandle, DateTime> noAppIdUntil = [];
    readonly HashSet<WindowHandle> appIdsAsked = [];
    static readonly TimeSpan NoAppIdRecheck = TimeSpan.FromSeconds(30);

    // A lookup this slow is written to the trace with its window, so the next stall names the app behind it.
    const long SlowAppIdMs = 100;

    // The pin buttons by ParsingName, with their markers, so a rebuild repaints markers without
    // recreating buttons.
    readonly Dictionary<string, (Button Button, Border Mark, UIElement Ring)> pinButtons = [];

    // The windows each pin has in the current workspace, from the last overview, for its click.
    Dictionary<string, IReadOnlyList<WindowHandle>> pinWindowsHere = [];

    // The pins with a window ANYWHERE, this workspace or another: what decides whether an editor's pin asks
    // its running copy for a new window or starts the app from nothing (OpenPin).
    HashSet<string> pinsWithAWindow = [];

    // Where the pins come from. Set by the composition root (ShellTaskbarPins); left unset, as every test but
    // the pin ones leaves it, there is no dock line.
    public ITaskbarPins? Pins
    {
        get => pins;
        init
        {
            pins = value;
            if (pins is null) return;
            WatchPinWidth();
            ReloadPins();
            pinWatch.Tick += (_, _) => ReloadPins();
            pinWatch.Start();
            Closed += (_, _) => pinWatch.Stop();
        }
    }

    // Re-reads the pins when the taskbar says they changed, and retries any icon that has not resolved yet.
    internal void ReloadPins()
    {
        if (pins is null) return;
        var version = pins.Version;
        if (version != pinsVersion)
        {
            pinsVersion = version;
            shownPins = pins.Read();
            if (ClickTrace.On)
                shownPins.ToList().ForEach(p => ClickTrace.Write($"pins: {p.Name} parsing={p.ParsingName} appId={p.AppId ?? "-"} target={p.TargetPath ?? "-"}"));
            BuildPinButtons();
        }
        else if (shownPins.Any(p => !pinImages.ContainsKey(p.ParsingName)))
            BuildPinButtons();
    }

    void BuildPinButtons()
    {
        PinLine.Children.Clear();
        pinButtons.Clear();
        shownPins.ToList().ForEach(pin =>
        {
            var (button, mark, ring) = PinButton(pin);
            pinButtons[pin.ParsingName] = (button, mark, ring);
            PinLine.Children.Add(button);
        });
        PinLine.Visibility = shownPins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FitPinsToSeam();
        if (lastPinOverview is { } overview) PaintPins(overview);
    }

    // Docked across two screens, tells the pins' panel where the line between them crosses it, so no pin sits on
    // it (see PackedPinPanel); on one screen, that there is none. The line is the middle of the bar's content,
    // translated into the panel's own coordinates, because the panel sits inside the pinned row's caption cell
    // and does not start at the bar's edge. Called on every rebuild and whenever the panel's width changes;
    // the panel ignores a value it already has, so this cannot feed a layout loop.
    void FitPinsToSeam()
    {
        if (!AcrossSeam || BarContent.ActualWidth <= 0 || !PinLine.IsVisible)
        {
            PinLine.Seam = null;
            return;
        }
        PinLine.Seam = BarContent.TranslatePoint(new Point(BarContent.ActualWidth / 2, 0), PinLine).X;
    }

    void WatchPinWidth() => PinLine.SizeChanged += (_, e) => { if (e.WidthChanged) FitPinsToSeam(); };

    (Button, Border, UIElement) PinButton(TaskbarPin pin)
    {
        // The sound ring (FloatingBar.Activity.cs), shown while any of the pin's windows is making sound, so
        // a playing YouTube Music lights its pin wherever its window is.
        var ring = Ring(21);
        ring.Visibility = Visibility.Collapsed;
        var mark = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1.5),
            Visibility = Visibility.Collapsed,
        };
        var face = new Grid { Children = { PinIcon(pin), mark, ring } };
        var button = new Button
        {
            Content = face,
            Height = PinHeight,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            Focusable = false,
            ToolTip = pin.Name,
        };
        // Sized to the cell, so the marker sits at the bottom of the whole button rather than under the icon.
        face.Height = PinHeight;
        System.Windows.Automation.AutomationProperties.SetName(button, pin.Name);

        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            OpenPin(pin, newWindow: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        // Middle-click: a new window, as on the taskbar. A middle-DRAG that starts on a pin moves the bar instead
        // and never gets here: the bar holds mouse capture for it, so the release goes to the bar, not the pin.
        button.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            OpenPin(pin, newWindow: true);
        };
        button.ContextMenu = PinMenu(pin);
        // At the icon size setting from the start, like Start and search beside it (see ScaleChromeIcons).
        ScaleAsIcon(button);
        return (button, mark, ring);
    }

    UIElement PinIcon(TaskbarPin pin)
    {
        var image = new Image { Width = PinIconSize, Height = PinIconSize, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (!pinImages.TryGetValue(pin.ParsingName, out var source) && pins is not null)
        {
            // 48 px: the bar draws a pin at 18 DIP and Petre's screens run at 150% and 200%, so 36 at most.
            pins.ImageOf(pin, 48).Map(ToImageSource).Tap(found =>
            {
                pinImages[pin.ParsingName] = found;
                source = found;
            });
        }
        image.Source = source;
        return image;
    }

    static ImageSource ToImageSource(PinImage image)
    {
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Pixels, image.Width * 4);
        source.Freeze();
        return source;
    }

    ContextMenu PinMenu(TaskbarPin pin)
    {
        var menu = new ContextMenu();
        HoldFadeWhileOpen(menu);
        menu.Items.Add(new MenuItem { Header = pin.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        var open = new MenuItem { Header = "New window", Icon = MenuGlyph("✚") };
        open.Click += (_, _) => OpenPin(pin, newWindow: true);
        menu.Items.Add(open);
        var reveal = new MenuItem { Header = "Open file location", Icon = MenuGlyph("↗"), IsEnabled = !pin.IsInstalledApp || pin.TargetPath is not null };
        reveal.Click += (_, _) => Report(pins?.Reveal(pin) ?? Result.Success());
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());
        // Petre: "rightclick should have unpin option". No confirmation, as on the taskbar itself: a pin is put
        // back from the app's own taskbar button or from Start.
        var unpin = new MenuItem { Header = "Unpin from taskbar", Icon = MenuGlyph("✕") };
        unpin.Click += (_, _) =>
        {
            var outcome = pins?.Unpin(pin) ?? Result.Success();
            if (ClickTrace.On) ClickTrace.Write($"pins: unpin {pin.Name} ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
            // Re-read at once rather than at the next poll, so the pin leaves the line as the click lands.
            if (outcome.IsSuccess) ReloadPins();
            Report(outcome);
        };
        menu.Items.Add(unpin);
        return menu;
    }

    // The taskbar's click: its window here if it has one (the first, which is the one the row shows first),
    // otherwise a new one, which Windows opens on the desktop you are on, so it lands in this workspace.
    //
    // Except a browser, which always opens a new window. Petre: "when clicking on quicklaunch icons, don't
    // activate existing browser windows, open a new one immediately". A browser's windows are already on the
    // rows to jump to; what the pin is for is a fresh one. An installed web app (YouTube Music, Outlook) is
    // not a browser here even though it runs as one: its pin starts the app, never a browser window, so it
    // keeps the taskbar's jump-or-start.
    //
    // VS Code too. Petre: "clicking vscode doesn't open a new window, it minimizes the current vscode window":
    // the jump half of jump-or-start, on the window that already had focus, is the taskbar's minimize. Same
    // reasoning as the browser: its windows are on the rows, and the pin is for a fresh one. Then "i want it to
    // always open a new window", so VS Code is started with --new-window rather than as the shortcut stands: a
    // second start with no arguments opens a window only while VS Code's window.openWithoutArgumentsInNewWindow
    // says so (measured: with it at its default, a plain start and --new-window each opened one), and the flag
    // makes that independent of the setting.
    //
    // ...but only while VS Code has a window somewhere. Started with --new-window from NOTHING, VS Code opens
    // one empty window instead of the session it had: its -n means "force an empty window" on a first start
    // (electron-main/app.ts passes forceEmpty with it), so the windows it would have restored never come
    // back. That is what Petre lost when the flag arrived: "when closing all vscode windows with taskkill, it
    // used to offer me to restore them all, but not anymore, why not?" A bare start of a VS Code that is not
    // running restores every window it had, and the folder tier puts each one back in its workspace.
    void OpenPin(TaskbarPin pin, bool newWindow)
    {
        var here = pinWindowsHere.GetValueOrDefault(pin.ParsingName) ?? [];
        newWindow |= IsBrowser(pin) || IsEditor(pin);
        var arguments = IsEditor(pin) && pinsWithAWindow.Contains(pin.ParsingName) ? "--new-window" : null;
        var outcome = !newWindow && here.Count > 0
            ? manager.ToggleWindow(here[0], activator)
            : pins?.Launch(pin, arguments) ?? Result.Success();
        if (ClickTrace.On)
            ClickTrace.Write($"pins: open {pin.Name} newWindow={newWindow} here={here.Count} ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
        Report(outcome);
    }

    // A pin that starts a browser itself, by the exe it targets. Chromium and Firefox families; a shortcut to a
    // site or an installed web app has no browser exe of its own here (its TargetPath is null or not one of these).
    static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase)
        { "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "vivaldi.exe", "opera.exe", "launcher.exe" };

    // VS Code and its Insiders build, whose second start opens a new window rather than raising the old one.
    static readonly HashSet<string> EditorExes = new(StringComparer.OrdinalIgnoreCase) { "Code.exe", "Code - Insiders.exe" };

    internal static bool IsEditor(TaskbarPin pin) =>
        !pin.IsInstalledApp && pin.TargetPath is { } target && EditorExes.Contains(System.IO.Path.GetFileName(target));

    internal static bool IsBrowser(TaskbarPin pin) =>
        !pin.IsInstalledApp && pin.TargetPath is { } target && BrowserExes.Contains(System.IO.Path.GetFileName(target))
        // Opera's launcher.exe is generic, so it only counts inside an Opera folder.
        && (!System.IO.Path.GetFileName(target).Equals("launcher.exe", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Opera", StringComparison.OrdinalIgnoreCase));

    Overview? lastPinOverview;

    // Every rebuild: which pins have a window in this workspace (the short bar), which only elsewhere (the
    // dot). "This workspace" is the current workspace or unnamed desktop, plus pinned windows, which are on
    // every desktop.
    void PaintPins(Overview overview)
    {
        lastPinOverview = overview;
        if (pins is null) return;

        var here = overview.Pinned
            .Concat(overview.Workspaces.Where(w => w.IsCurrent).SelectMany(w => w.Running))
            .Concat(overview.OtherDesktops.Where(d => d.IsCurrent).SelectMany(d => d.Windows))
            .Select(r => r.Window)
            .ToList();
        var hereHandles = here.Select(w => w.Handle).ToHashSet();
        var elsewhere = overview.Workspaces.Where(w => !w.IsCurrent).SelectMany(w => w.Running)
            .Concat(overview.OtherDesktops.Where(d => !d.IsCurrent).SelectMany(d => d.Windows))
            .Select(r => r.Window)
            .Where(w => !hereHandles.Contains(w.Handle))
            .ToList();

        // Every window in the overview, which is also every window the sound ring asks about (NoteActivity), so
        // the IDs are kept for exactly these: a closed window's entry goes, and a handle Windows reuses later
        // starts clean. Asked for even with no pins on the line, because the sound ring needs them too.
        var everyWindow = here.Concat(elsewhere).ToList();
        var present = everyWindow.Select(w => w.Handle).ToHashSet();
        windowAppIds.Keys.Where(h => !present.Contains(h)).ToList().ForEach(h => windowAppIds.Remove(h));
        noAppIdUntil.Keys.Where(h => !present.Contains(h)).ToList().ForEach(h => noAppIdUntil.Remove(h));
        AskAppIds(everyWindow);
        if (shownPins.Count == 0) return;

        // The IDs some pin claims outright, so a window carrying one of them is never also claimed by a pin
        // through its exe path (a PWA runs as its browser's exe; see PinMatches).
        var claimed = shownPins.Select(p => p.AppId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        pinWindowsHere = shownPins.ToDictionary(p => p.ParsingName,
            p => (IReadOnlyList<WindowHandle>)here.Where(w => PinMatches(p, w, AppIdOf(w), claimed)).Select(w => w.Handle).ToList());
        pinsWithAWindow = shownPins
            .Where(p => here.Concat(elsewhere).Any(w => PinMatches(p, w, AppIdOf(w), claimed)))
            .Select(p => p.ParsingName)
            .ToHashSet();

        shownPins.ToList().ForEach(pin =>
        {
            if (!pinButtons.TryGetValue(pin.ParsingName, out var shown)) return;
            var isHere = pinWindowsHere[pin.ParsingName].Count > 0;
            var isElsewhere = !isHere && elsewhere.Any(w => PinMatches(pin, w, AppIdOf(w), claimed));
            shown.Mark.Visibility = isHere || isElsewhere ? Visibility.Visible : Visibility.Collapsed;
            shown.Mark.Width = isHere ? 6 : 2;
            shown.Mark.Background = isHere ? PinHereMark : PinElsewhereMark;
            var loud = here.Concat(elsewhere).Any(w => sounding.Contains(w.Handle) && PinMatches(pin, w, AppIdOf(w), claimed));
            shown.Ring.Visibility = loud ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // From what AskAppIds has already learnt, never from the shell: this runs inside a rebuild, on the UI thread.
    string? AppIdOf(WindowInfo window) => windowAppIds.GetValueOrDefault(window.Handle);

    // WHY THE LOOKUP LEFT THE UI THREAD. Petre: "see why the bar had a delay just now". The trace had a rebuild
    // of 4738ms with 4168ms of it in "pins", and fifteen like it in an hour, 4.3 to 5.6s each, the bar drawing
    // nothing while one ran. A stack-sampling trace of that rebuild put all of it in ShellTaskbarPins.AppIdOf,
    // WAITING rather than working: three calls of 464, 2064 and 1640ms, one from each place PaintPins asked,
    // because a window with no ID was not cached and so was asked again every time. The same lookup made from
    // another process took well under a millisecond a window, so which window answers slowly, and when, is
    // still unknown; the trace line in TakeAppIds exists to name it next time.
    //
    // So the shell is asked on a pool thread, at most once per window until it answers, and the answers come
    // back to the UI thread, which repaints the pins if any ID turned up. Until then PinMatches falls back to a
    // pin's exe, which can light a browser's pin for a web app's window for that moment.
    void AskAppIds(IReadOnlyList<WindowInfo> windows)
    {
        if (pins is not { } shell) return;
        var ask = AppIdsToAsk(windows.Select(w => w.Handle), windowAppIds, appIdsAsked, noAppIdUntil, DateTime.UtcNow);
        if (ask.Count == 0) return;
        ask.ToList().ForEach(h => appIdsAsked.Add(h));
        // The process names, for the trace line only, taken here so the pool thread touches nothing of the bar's.
        var names = windows.DistinctBy(w => w.Handle).ToDictionary(w => w.Handle, w => w.ProcessName);
        Task.Run(() => ask.Select(handle =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var id = shell.AppIdOf(handle);
                return (Handle: handle, Id: id, Ms: clock.ElapsedMilliseconds);
            }).ToList())
            .ContinueWith(done => Dispatcher.BeginInvoke(() =>
                TakeAppIds(ask, done.IsCompletedSuccessfully ? done.Result : [], names)), TaskScheduler.Default);
    }

    // Back on the UI thread. A faulted batch answers nothing, which leaves its windows to be asked again on the
    // next rebuild rather than marked as having no ID.
    void TakeAppIds(IReadOnlyList<WindowHandle> asked, IReadOnlyList<(WindowHandle Handle, string? Id, long Ms)> answers,
        IReadOnlyDictionary<WindowHandle, string> names)
    {
        asked.ToList().ForEach(h => appIdsAsked.Remove(h));
        var recheckAt = DateTime.UtcNow + NoAppIdRecheck;
        answers.Where(a => a.Id is null).ToList().ForEach(a => noAppIdUntil[a.Handle] = recheckAt);
        answers.Where(a => a.Id is not null).ToList().ForEach(a =>
        {
            windowAppIds[a.Handle] = a.Id!;
            noAppIdUntil.Remove(a.Handle);
        });
        if (ClickTrace.On)
            answers.Where(a => a.Ms >= SlowAppIdMs).ToList().ForEach(a =>
                ClickTrace.Write($"pins: app id of {a.Handle.Value:X}/{names.GetValueOrDefault(a.Handle) ?? "?"} took {a.Ms}ms -> {a.Id ?? "none"}"));
        if (answers.Any(a => a.Id is not null) && lastPinOverview is { } overview) PaintPins(overview);
    }

    // Which windows to ask the shell about: no ID known, not already being asked, and not told "none" within
    // the recheck window. Pure, so the tests can pin the policy down.
    internal static IReadOnlyList<WindowHandle> AppIdsToAsk(IEnumerable<WindowHandle> windows,
        IReadOnlyDictionary<WindowHandle, string> known, IReadOnlySet<WindowHandle> asked,
        IReadOnlyDictionary<WindowHandle, DateTime> noneUntil, DateTime now) =>
        windows.Distinct()
            .Where(h => !known.ContainsKey(h) && !asked.Contains(h)
                        && !(noneUntil.TryGetValue(h, out var until) && until > now))
            .ToList();

    // Whether a window is one of a pin's, the way the taskbar groups them: by AppUserModelID when both say
    // one, and otherwise by the exe the pin starts. The exe rule stands down for a window whose ID another pin
    // claims, because a Chromium PWA (YouTube Music, Outlook) runs as the browser's own exe and must light its
    // own pin, not the browser's.
    internal static bool PinMatches(TaskbarPin pin, WindowInfo window, string? windowAppId, IReadOnlySet<string> claimed)
    {
        if (pin.AppId is not null && windowAppId is not null)
            return string.Equals(pin.AppId, windowAppId, StringComparison.OrdinalIgnoreCase);
        if (pin.AppId is not null && pin.IsInstalledApp) return false;
        return pin.TargetPath is not null && window.ProcessPath is not null
            && string.Equals(pin.TargetPath, window.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && (windowAppId is null || !claimed.Contains(windowAppId));
    }
}
