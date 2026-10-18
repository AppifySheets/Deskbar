using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Windows.Shell;

namespace TaskSpaces.App;

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
    // that line: "distribute evenly; two rows would be fine, don't cram into one". So the icon stays 18 and a
    // cell must be at least 24 wide, which puts his ten on two even lines of five spread across the bar.
    const double PinIconSize = 18, PinMinWidth = 24, PinHeight = 24;

    static readonly Brush PinHereMark = Frozen(0xC0, 0xFF, 0xFF, 0xFF);
    static readonly Brush PinElsewhereMark = Frozen(0x66, 0xFF, 0xFF, 0xFF);

    ITaskbarPins? pins;
    int pinsVersion = -1;
    IReadOnlyList<TaskbarPin> shownPins = [];
    readonly DispatcherTimer pinWatch = new() { Interval = TimeSpan.FromSeconds(3) };

    // Per pin (by ParsingName): its icon, never caching a miss, since a shell icon can arrive late (the
    // project's rule, learnt on window icons). And per window: its AppUserModelID, same rule.
    readonly Dictionary<string, ImageSource> pinImages = [];
    readonly Dictionary<WindowHandle, string> windowAppIds = [];

    // The pin buttons by ParsingName, with their markers, so a rebuild repaints markers without
    // recreating buttons.
    readonly Dictionary<string, (Button Button, Border Mark, UIElement Ring)> pinButtons = [];

    // The windows each pin has in the current workspace, from the last overview, for its click.
    Dictionary<string, IReadOnlyList<WindowHandle>> pinWindowsHere = [];

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
        ((UniformGrid)PinLine).Columns = PinColumns(shownPins.Count, PinLine.ActualWidth);
        if (lastPinOverview is { } overview) PaintPins(overview);
    }

    // Columns for `count` pins in `width`: as few lines as fit at the minimum cell width, then the pins shared
    // EVENLY between those lines, so ten in a line that holds seven come out as two lines of five, not 7 + 3.
    // Before the first layout the width is unknown; the docked bar's own 196 DIP stands in until it is.
    internal static int PinColumns(int count, double width)
    {
        if (count <= 0) return 1;
        var perLine = Math.Max(1, (int)Math.Floor((width > 0 ? width : 196) / PinMinWidth));
        var lines = (int)Math.Ceiling(count / (double)perLine);
        return (int)Math.Ceiling(count / (double)lines);
    }

    void WatchPinWidth() => PinLine.SizeChanged += (_, e) =>
    {
        // Only a WIDTH change can change the columns, and a column change only changes the height, so this
        // cannot feed itself.
        if (e.WidthChanged) ((UniformGrid)PinLine).Columns = PinColumns(shownPins.Count, e.NewSize.Width);
    };

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
    void OpenPin(TaskbarPin pin, bool newWindow)
    {
        var here = pinWindowsHere.GetValueOrDefault(pin.ParsingName) ?? [];
        newWindow |= IsBrowser(pin);
        var outcome = !newWindow && here.Count > 0
            ? manager.ToggleWindow(here[0], activator)
            : pins?.Launch(pin) ?? Result.Success();
        if (ClickTrace.On)
            ClickTrace.Write($"pins: open {pin.Name} newWindow={newWindow} here={here.Count} ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
        Report(outcome);
    }

    // A pin that starts a browser itself, by the exe it targets. Chromium and Firefox families; a shortcut to a
    // site or an installed web app has no browser exe of its own here (its TargetPath is null or not one of these).
    static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase)
        { "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "vivaldi.exe", "opera.exe", "launcher.exe" };

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
        if (pins is null || shownPins.Count == 0) return;

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

        // The IDs some pin claims outright, so a window carrying one of them is never also claimed by a pin
        // through its exe path (a PWA runs as its browser's exe; see PinMatches).
        var claimed = shownPins.Select(p => p.AppId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        pinWindowsHere = shownPins.ToDictionary(p => p.ParsingName,
            p => (IReadOnlyList<WindowHandle>)here.Where(w => PinMatches(p, w, AppIdOf(w), claimed)).Select(w => w.Handle).ToList());

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

    string? AppIdOf(WindowInfo window)
    {
        if (windowAppIds.TryGetValue(window.Handle, out var known)) return known;
        var found = pins?.AppIdOf(window.Handle);
        if (found is not null) windowAppIds[window.Handle] = found;
        return found;
    }

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
