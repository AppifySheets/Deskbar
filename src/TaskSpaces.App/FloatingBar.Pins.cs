using System.Windows;
using System.Windows.Controls;
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
//   right    its name, New window, Open file location
//
// Pinning and unpinning stay on the taskbar (Windows offers no supported way to change its pins), and the
// line follows them: the pin list's change counter is polled every few seconds.
public partial class FloatingBar
{
    // One pin's width: ten fit the docked bar's 200 DIP on one line, which is how many Petre has.
    const double PinWidth = 19;

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
    readonly Dictionary<string, (Button Button, Border Mark)> pinButtons = [];

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
            var (button, mark) = PinButton(pin);
            pinButtons[pin.ParsingName] = (button, mark);
            PinLine.Children.Add(button);
        });
        PinLine.Visibility = shownPins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (lastPinOverview is { } overview) PaintPins(overview);
    }

    (Button, Border) PinButton(TaskbarPin pin)
    {
        var mark = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1),
            Visibility = Visibility.Collapsed,
        };
        var face = new Grid { Children = { PinIcon(pin), mark } };
        var button = new Button
        {
            Content = face,
            Width = PinWidth,
            Height = 24,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            Focusable = false,
            ToolTip = pin.Name,
        };
        // Stretched, so the marker sits at the bottom of the whole button rather than under the icon's box.
        face.Width = PinWidth;
        face.Height = 24;
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
        return (button, mark);
    }

    UIElement PinIcon(TaskbarPin pin)
    {
        var image = new Image { Width = 16, Height = 16, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (!pinImages.TryGetValue(pin.ParsingName, out var source) && pins is not null)
        {
            // 32 px: the bar draws a pin at 16 DIP and Petre's screens run at 150% and 200%.
            pins.ImageOf(pin, 32).Map(ToImageSource).Tap(found =>
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
        return menu;
    }

    // The taskbar's click: its window here if it has one (the first, which is the one the row shows first),
    // otherwise a new one, which Windows opens on the desktop you are on, so it lands in this workspace.
    void OpenPin(TaskbarPin pin, bool newWindow)
    {
        var here = pinWindowsHere.GetValueOrDefault(pin.ParsingName) ?? [];
        var outcome = !newWindow && here.Count > 0
            ? manager.ToggleWindow(here[0], activator)
            : pins?.Launch(pin) ?? Result.Success();
        if (ClickTrace.On)
            ClickTrace.Write($"pins: open {pin.Name} newWindow={newWindow} here={here.Count} ok={outcome.IsSuccess}{(outcome.IsFailure ? $" error={outcome.Error}" : "")}");
        Report(outcome);
    }

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
