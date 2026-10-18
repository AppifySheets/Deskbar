using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// THE TASKBAR'S OWN BUTTONS, on the bar. Petre: "i like how the app replaces my taskbar, but taskbar had
// some useful things, like a clock, start button, etc." His taskbar is on auto-hide at the bottom of one
// screen, so the bar is what he looks at, and the clock and Start had gone with the taskbar.
//
// Picked from three mockups ("c", split ends): Start and search join the header row, which already exists
// and costs no height, and a strip under the last row holds the tray chevron, quick settings, the bell, the
// clock and Windows' show desktop sliver.
//
// EVERY BUTTON SENDS THE WINDOWS SHORTCUT, so the real Windows flyout opens and nothing of the shell is
// rebuilt here. Measured with a throwaway probe on his machine before a line of this was written:
//
//   Win    -> "Start menu"           DV2ControlHost               bottom left of the taskbar's screen
//   Win+S  -> "Search"               Windows.UI.Core.CoreWindow   bottom left, over the docked bar
//   Win+N  -> "Notification Center"  Windows.UI.Core.CoreWindow   right edge of the taskbar's screen
//   Win+A  -> "Quick settings"       ControlCenterWindow          right edge of the taskbar's screen
//   Win+B  -> the auto-hidden taskbar slides up with the tray focused (Shell_TrayWnd)
//
// All of them open on the screen that HAS the taskbar, wherever the pointer is, because Windows anchors
// them to the taskbar rather than to us. Nothing here can change that.
//
// The tray icons themselves (Teams, OneDrive and the rest) are deliberately NOT drawn: an app hands its
// icon to Explorer's taskbar window and to nothing else, so showing them would mean impersonating that
// window, which is the most fragile thing a taskbar replacement does. Win+B shows the real tray instead.
//
// Two traps the probe also showed, both handled below:
//
//   * A keystroke can be lost. The first probe run got nothing from two of ten shortcuts and the rerun got
//     four of four, so after sending, the button looks at what is in front and sends ONCE more if its
//     flyout is not there. See SendAndConfirm.
//   * Clicking a button on this bar ACTIVATES the bar (it is a normal top-level window), and an open flyout
//     closes the moment it loses the foreground. So by the time Click fires, the Start menu the user meant
//     to close has already closed, and sending Win would open it again. The bar therefore remembers which
//     flyout was in front when the press arrived (flyoutInFrontAtPress), and a click on that flyout's own
//     button just lets the press do the closing, which is what the taskbar's buttons do.
public partial class FloatingBar
{
    // What each button opens. Desktop is Win+D, which toggles by itself and opens nothing to look for.
    internal enum ShellFlyout { Start, Search, Tray, QuickSettings, Notifications, Desktop }

    // The shell flyout last reported in front by the foreground hook (NoteForeground), or null when
    // something else is. Cleared by a press on the bar, because that press takes the foreground from it.
    ShellFlyout? flyoutInFront;

    // ...and what it was when the current press arrived, which is what a button's Click reads.
    ShellFlyout? flyoutInFrontAtPress;

    // What actually presses the keys. A seam for the tests and nothing else: a test that pressed the real
    // Windows key would open the Start menu on the screen of whoever runs the suite.
    internal Action<ShellFlyout> SendShellKeys { get; init; } = ShellKeys.Send;

    // ...and the Space that opens the tray's hidden icons once Win+B has focused it (see OpenTrayOverflow),
    // a seam for the same reason.
    internal Action PressSpace { get; init; } = () => ShellKeys.Press(ShellKeys.VK_SPACE);

    // Ticks every second but writes only when the text changes, so the clock turns over on the minute
    // without the bar ever re-laying out between minutes.
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    TextBlock? clockTime;
    TextBlock? clockDate;

    static readonly Brush ShellGlyphBrush = Frozen(0xD9, 0xFF, 0xFF, 0xFF);
    static readonly Brush StartBlue = Frozen(0xFF, 0x4C, 0xC2, 0xFF);
    static readonly Brush ClockForeground = Frozen(0xE6, 0xFF, 0xFF, 0xFF);
    static readonly Brush SliverLine = Frozen(0x29, 0xFF, 0xFF, 0xFF);

    // Fills the two panels the XAML declares for this: ShellStart in the header, ShellStrip at the bottom.
    // Called once from the constructor; nothing here is rebuilt with the rows, so the buttons keep their
    // identity (and the clock its timer) across every rebuild.
    void BuildShellStrip()
    {
        ShellStart.Children.Add(ShellButton(ShellFlyout.Start, "Start", StartGlyph()));
        ShellStart.Children.Add(ShellButton(ShellFlyout.Search, "Search", Glyph(SearchShape)));

        // Left to right from the left edge: the tray chevron, quick settings, the volume dial
        // (FloatingBar.Volume.cs), the bell.
        new[]
        {
            ShellButton(ShellFlyout.Tray, "Show the tray icons", Glyph(ChevronShape)),
            ShellButton(ShellFlyout.QuickSettings, "Quick settings: wifi, volume, battery", QuickSettingsGlyph(), width: double.NaN),
            VolumeDial(),
            ShellButton(ShellFlyout.Notifications, "Notifications", Glyph(BellShape)),
        }.ToList().ForEach(b =>
        {
            DockPanel.SetDock(b, Dock.Left);
            ShellStrip.Children.Add(b);
        });

        // From the right edge inwards, which is the order DockPanel takes right-docked children in: the show
        // desktop sliver at the very edge (where Windows puts it), then the clock beside it.
        var sliver = ShellButton(ShellFlyout.Desktop, "Show desktop",
            new Border { Width = 1, Height = 22, Background = SliverLine }, width: 7);
        DockPanel.SetDock(sliver, Dock.Right);
        ShellStrip.Children.Add(sliver);

        var clock = ShellButton(ShellFlyout.Notifications, "", ClockFace(), width: double.NaN);
        clock.Padding = new Thickness(4, 0, 4, 0);
        DockPanel.SetDock(clock, Dock.Right);
        ShellStrip.Children.Add(clock);

        UpdateClock(clock);
        clockTimer.Tick += (_, _) => UpdateClock(clock);
        clockTimer.Start();
        Closed += (_, _) => clockTimer.Stop();
    }

    // 14 over 12.5, after two rounds of Petre's "increase the size of fonts for this": 10 and 9 matched the
    // bar's captions, then 12 and 11 matched the taskbar's clock on paper, but the bar draws through its own
    // scale (BarScale) and on a docked 4K screen that still read small.
    // Time over date, the way the taskbar shows them, in the user's own regional format: .NET's current
    // culture is the same "Region" setting the taskbar reads, so the two can never disagree about it.
    UIElement ClockFace()
    {
        clockTime = new TextBlock { FontSize = 14, Foreground = ClockForeground, TextAlignment = TextAlignment.Right };
        clockDate = new TextBlock { FontSize = 12.5, Foreground = ClockForeground, TextAlignment = TextAlignment.Right };
        return new StackPanel { Children = { clockTime, clockDate }, VerticalAlignment = VerticalAlignment.Center };
    }

    void UpdateClock(FrameworkElement clock)
    {
        var now = DateTime.Now;
        var time = now.ToString("t");
        if (clockTime is null || clockDate is null || clockTime.Text == time) return;
        clockTime.Text = time;
        clockDate.Text = now.ToString("d");
        // The long date on hover, which the two short lines leave out.
        clock.ToolTip = now.ToString("D");
    }

    // One button of the strip: bare chrome with a faint rounded hover, so it reads as a button without the
    // stock template's blue square (see BareButton for why the stock chrome is unwanted on this bar).
    Button ShellButton(ShellFlyout flyout, string tip, UIElement content, double width = 26)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 28,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        if (tip.Length > 0) button.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(button, tip.Length > 0 ? tip : "Clock");
        button.Click += (_, _) => OnShellClick(flyout);
        return button;
    }

    // The one place the bar answers a press on these buttons.
    void OnShellClick(ShellFlyout flyout)
    {
        // Same as an icon: this click is the press's whole meaning, so nothing further up the route (the
        // pinned row Start and search sit in) may treat its release as a click of its own.
        MarkPressConsumed();

        var wasOpen = flyoutInFrontAtPress == flyout;
        flyoutInFrontAtPress = null;
        if (ClickTrace.On) ClickTrace.Write($"shell: {flyout} clicked, open at press={wasOpen}");

        // The press already closed it by taking the foreground; sending the shortcut now would reopen it.
        // Show desktop is the exception: it is not a window that can be in front, Win+D toggles by itself.
        if (wasOpen && flyout is not ShellFlyout.Desktop) return;
        SendAndConfirm(flyout);
    }

    // Sends the shortcut, then looks once more after the flyout has had time to open, and sends again if
    // it is not in front. Once, not in a loop: a flyout that does not open twice running is not going to
    // open, and a third press would most likely land just as the user dismissed it.
    void SendAndConfirm(ShellFlyout flyout)
    {
        SendShellKeys(flyout);
        if (flyout is ShellFlyout.Desktop) return;
        if (flyout is ShellFlyout.Tray)
        {
            OpenTrayOverflow(attempt: 1);
            return;
        }

        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            var front = ShellKeys.Recognise(NativeMethods.GetForegroundWindow());
            if (ClickTrace.On) ClickTrace.Write($"shell: {flyout} check, in front={front?.ToString() ?? "other"}");
            if (front != flyout) SendShellKeys(flyout);
        };
        check.Start();
    }

    // Petre, on the chevron: "pressing this should be followed by a single space, can you do that on behalf of
    // the clicker? so it shows immediately". Win+B only FOCUSES the tray: the auto-hidden taskbar slides up
    // with its "show hidden icons" chevron selected, and waits for Space or Enter to open the overflow. So the
    // bar presses Space itself, once the taskbar has actually taken the foreground; a Space sent before that
    // would land in whatever window had focus. If the focus never arrives the shortcut is sent once more,
    // the same single retry every other button gets.
    void OpenTrayOverflow(int attempt)
    {
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            var front = NativeMethods.GetForegroundWindow();
            var focused = ShellKeys.Recognise(front) == ShellFlyout.Tray;
            if (ClickTrace.On) ClickTrace.Write($"shell: tray attempt {attempt}, in front={ShellKeys.Describe(front)}, focused={focused}");
            if (focused)
            {
                PressSpace();
                // What opened, by class and title, so the overflow can be recognised as the tray's (and a second
                // click on the chevron then closes it rather than opening it again).
                if (ClickTrace.On)
                {
                    var after = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    after.Tick += (_, _) =>
                    {
                        after.Stop();
                        ClickTrace.Write($"shell: tray after space, in front={ShellKeys.Describe(NativeMethods.GetForegroundWindow())}");
                    };
                    after.Start();
                }
            }
            else if (attempt == 1)
            {
                SendShellKeys(ShellFlyout.Tray);
                OpenTrayOverflow(attempt: 2);
            }
        };
        wait.Start();
    }

    // From the composition root, on every foreground change (WindowMonitor.ForegroundChanged, which reports
    // the shell's own windows but never this bar's). Keeps track of which flyout, if any, is in front.
    public void NoteForeground(nint hwnd) => NoteFlyoutInFront(ShellKeys.Recognise(hwnd));

    internal void NoteFlyoutInFront(ShellFlyout? flyout) => flyoutInFront = flyout;

    // From the window's first press handler: the flyout in front right now is the one this press closes.
    internal void NoteBarPressed()
    {
        flyoutInFrontAtPress = flyoutInFront;
        flyoutInFront = null;
    }

    // --- glyphs ---------------------------------------------------------------------------------------
    //
    // Drawn as paths on a 16 unit square, the same shapes as the mockup Petre picked, rather than taken from
    // Segoe Fluent Icons: a private use codepoint that turns out wrong renders as a hollow box (see this
    // bar's MenuGlyph comment), and these are six simple shapes. Each part is (geometry, filled).
    const string SearchShape = "M2,6.5 A4.5,4.5 0 1 1 11,6.5 A4.5,4.5 0 1 1 2,6.5 Z M10,10 L14.5,14.5";
    const string ChevronShape = "M3.5,10 L8,5.5 L12.5,10";
    const string BellShape = "M8,1.8 C5.8,1.8 4,3.6 4,5.8 L4,8.8 L2.6,11.4 L13.4,11.4 L12,8.8 L12,5.8 C12,3.6 10.2,1.8 8,1.8 Z M6.4,13.2 A1.7,1.7 0 0 0 9.6,13.2";
    const string WifiShape = "M1.5,6 A9.5,9.5 0 0 1 14.5,6 M4,8.7 A5.8,5.8 0 0 1 12,8.7 M6.4,11.3 A2.6,2.6 0 0 1 9.6,11.3";
    const string WifiDot = "M7,13.6 A1,1 0 1 1 9,13.6 A1,1 0 1 1 7,13.6 Z";
    const string SpeakerShape = "M2,6 L4.6,6 L8,3 L8,13 L4.6,10 L2,10 Z";
    const string SpeakerWaves = "M10.5,5.5 A3.5,3.5 0 0 1 10.5,10.5 M12.5,3.5 A6.3,6.3 0 0 1 12.5,12.5";
    const string BatteryCase = "M2.2,4.5 L11.8,4.5 A1.2,1.2 0 0 1 13,5.7 L13,10.3 A1.2,1.2 0 0 1 11.8,11.5 L2.2,11.5 A1.2,1.2 0 0 1 1,10.3 L1,5.7 A1.2,1.2 0 0 1 2.2,4.5 Z";
    const string BatteryFill = "M2.6,6.1 L9.6,6.1 L9.6,9.9 L2.6,9.9 Z M13.6,6.5 L15.2,6.5 L15.2,9.5 L13.6,9.5 Z";
    const string StartTiles = "M1,1 L7.5,1 L7.5,7.5 L1,7.5 Z M8.5,1 L15,1 L15,7.5 L8.5,7.5 Z M1,8.5 L7.5,8.5 L7.5,15 L1,15 Z M8.5,8.5 L15,8.5 L15,15 L8.5,15 Z";

    static UIElement Glyph(string stroked, string? filled = null, double size = 15, Brush? brush = null)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(stroked),
            Stroke = brush ?? ShellGlyphBrush,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        });
        if (filled is not null) canvas.Children.Add(new Path { Data = Geometry.Parse(filled), Fill = brush ?? ShellGlyphBrush });
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    // Windows' four tiles, in the blue the taskbar draws them in, so the one button you reach for most is
    // also the one you find without reading.
    static UIElement StartGlyph() => new Viewbox
    {
        Width = 15,
        Height = 15,
        IsHitTestVisible = false,
        Child = new Canvas { Width = 16, Height = 16, Children = { new Path { Data = Geometry.Parse(StartTiles), Fill = StartBlue } } },
    };

    // Wifi and, on a machine that has one, the battery: one button, as on the taskbar, because Win+A opens
    // one panel for both. The speaker that sat here moved out to its own dial beside it (FloatingBar.Volume.cs),
    // which shows the real level; these two are still static glyphs.
    static UIElement QuickSettingsGlyph()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0), IsHitTestVisible = false };
        panel.Children.Add(Glyph(WifiShape, WifiDot, size: 13));
        if (ShellKeys.HasBattery())
        {
            panel.Children.Add(new Border { Width = 4 });
            panel.Children.Add(Glyph(BatteryCase, BatteryFill, size: 13));
        }
        return panel;
    }

    // Bare chrome plus the one trigger the rows deliberately do without: a faint rounded hover, since these
    // are plain buttons with nothing else to say they can be pressed.
    static readonly ControlTemplate ShellButtonTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="4"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#1FFFFFFF" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#33FFFFFF" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));
}

// The keystrokes, and telling the shell's flyouts apart. Static and free of WPF so the mapping can be
// tested on its own.
static class ShellKeys
{
    const byte VK_LWIN = 0x5B;
    internal const byte VK_SPACE = 0x20;
    const uint KEYEVENTF_KEYUP = 0x0002;

    // Which keys each flyout is: the Windows key alone, or with one letter.
    internal static byte[] KeysOf(FloatingBar.ShellFlyout flyout) => flyout switch
    {
        FloatingBar.ShellFlyout.Start => [VK_LWIN],
        FloatingBar.ShellFlyout.Search => [VK_LWIN, (byte)'S'],
        FloatingBar.ShellFlyout.Tray => [VK_LWIN, (byte)'B'],
        FloatingBar.ShellFlyout.QuickSettings => [VK_LWIN, (byte)'A'],
        FloatingBar.ShellFlyout.Notifications => [VK_LWIN, (byte)'N'],
        FloatingBar.ShellFlyout.Desktop => [VK_LWIN, (byte)'D'],
        _ => [],
    };

    // Down in order, up in reverse, which is how a hand presses a chord. keybd_event rather than SendInput:
    // it is the call the probe used, it injects the same input, and it has no struct layout to get wrong.
    public static void Send(FloatingBar.ShellFlyout flyout)
    {
        var keys = KeysOf(flyout);
        keys.ToList().ForEach(k => keybd_event(k, 0, 0, 0));
        Enumerable.Reverse(keys).ToList().ForEach(k => keybd_event(k, 0, KEYEVENTF_KEYUP, 0));
    }

    // One key, down and up.
    public static void Press(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KEYEVENTF_KEYUP, 0);
    }

    // For the trace: a window as its class and title.
    public static string Describe(nint hwnd) => $"{ClassOf(hwnd)} '{TitleOf(hwnd)}'";

    // The class names measured on Petre's machine (see FloatingBar.Shell's header). Search and the
    // notification centre share a class, so those two are told apart by title, which is English on his
    // machine and may be localised elsewhere; a title that does not match just means the confirming
    // check sends the shortcut a second time, which opens nothing extra on a flyout that is already up.
    public static FloatingBar.ShellFlyout? Recognise(nint hwnd) => (ClassOf(hwnd), TitleOf(hwnd)) switch
    {
        ("DV2ControlHost", _) => FloatingBar.ShellFlyout.Start,
        ("ControlCenterWindow", _) => FloatingBar.ShellFlyout.QuickSettings,
        // The overflow of hidden icons counts as the tray too, measured after the chevron's Win+B and Space,
        // so a second click on the chevron closes it instead of opening it again.
        ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow", _) => FloatingBar.ShellFlyout.Tray,
        ("Windows.UI.Core.CoreWindow", "Search") => FloatingBar.ShellFlyout.Search,
        ("Windows.UI.Core.CoreWindow", "Notification Center") => FloatingBar.ShellFlyout.Notifications,
        _ => null,
    };

    // 128 is "no system battery" and 255 "unknown"; anything else is a battery worth drawing.
    public static bool HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);

    static string ClassOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return hwnd == 0 ? "" : GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    static string TitleOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return hwnd == 0 ? "" : GetWindowTextW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    // CharSet.Unicode on the string calls: without it the W entry points get ANSI buffers and quietly
    // return nothing, which is the trap this project's probes have already fallen into once.
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint hwnd, StringBuilder text, int size);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
