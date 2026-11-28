using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Deskbar.App;

// "CLAUDE STARTED", the add boxes' Enter confirmation (Ctrl+Enter until Deskbar task 64c9eb8f; task 7b17a573; Petre: "only start a task and don't
// open a terminal. confirm it has started as a mild notification, don't even take me away from my current thread").
//
// WHY A WINDOW OF OUR OWN, not a Windows notification: this app cannot deliver one (#123). The direct build has no
// AppUserModelID and no per-app notification key, so Windows drops its tray balloons; measured on Petre's machine, which
// is why the update announcement is a dialog and the download progress a window (UpdateProgress.cs). A first cut of this
// task used the tray balloon anyway, and the review caught it.
//
// MILD, which is the whole brief:
//   * it never takes the foreground or the keyboard: ShowActivated is off and the window carries WS_EX_NOACTIVATE, which
//     Windows honours for clicks too (FloatingBar.Language.cs measured that for the language switcher), plus
//     WS_EX_TOOLWINDOW so it has no taskbar button and no Alt+Tab entry;
//   * it sits in the bottom-right corner of the primary screen's work area, where Windows' own notifications appear, so
//     it reads as one at a glance and covers nothing in the middle of the screen;
//   * no sound, and it fades by itself after a few seconds. While the pointer rests on it, it stays.
//   * a newer notice replaces an older one (App.NotifyClaudeStarted closes the last), so a burst of Enters leaves
//     one card, not a stack.
// A click on it is asking to see the session after all: Clicked fires and the card closes; the App opens the task's chat
// (its page in the tasks panel, App.NotifyClaudeStarted), never a terminal (task f284968f).
//
// The caller hands the handle to WindowMonitor.Ignore BEFORE Show (EnsureHandle), as for every window of the app's own:
// the monitor does not skip this process, and a card shown first would get a button in the bar or a tile.
public sealed class ClaudeStartedNotice : Window
{
    // How long the card stays before it fades, and how long the fade takes.
    static readonly TimeSpan Stay = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(400);

    // Colours of a Windows 11 dark notification, frozen since they are static (CLAUDE.md, "Static WPF brushes must be
    // frozen").
    static readonly Brush CardFill = Frozen(Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B));
    static readonly Brush CardEdge = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    static readonly Brush Ink = Frozen(Colors.White);
    static readonly Brush DimInk = Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8));
    static readonly Brush PlayInk = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));

    readonly DispatcherTimer timer;

    /// <summary>A click on the card: the App opens the task's chat in the tasks panel. The card closes itself after raising it.</summary>
    public event Action? Clicked;

    public ClaudeStartedNotice(string taskTitle)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Claude started";
        Content = Card(taskTitle);
        Cursor = Cursors.Hand;
        ToolTip = "Open the task's chat";

        timer = new DispatcherTimer { Interval = Stay };
        timer.Tick += (_, _) => FadeWhenLeft();
        MouseLeftButtonUp += (_, _) =>
        {
            Clicked?.Invoke();
            Close();
        };
        // Placed once its size is known, then the clock starts.
        Loaded += (_, _) =>
        {
            PlaceInCorner();
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }

    // UI Automation sees this window and nothing inside it (QuietAutomation.cs: WPF's tree walks froze a busy machine).
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new QuietWindowPeer(this);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_NOACTIVATE (no foreground, even on a click) and WS_EX_TOOLWINDOW (no taskbar button, no Alt+Tab).
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Windows.Monitoring.NativeMethods.GetWindowLongPtr(hwnd, Windows.Monitoring.NativeMethods.GWL_EXSTYLE);
        Windows.Monitoring.NativeMethods.SetWindowLongPtr(hwnd, Windows.Monitoring.NativeMethods.GWL_EXSTYLE,
            (nint)(style | WS_EX_NOACTIVATE | Windows.Monitoring.NativeMethods.WS_EX_TOOLWINDOW));
    }

    const long WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>The window's handle, made now if need be, for WindowMonitor.Ignore before the first Show.</summary>
    public nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    // The card: Claude's play glyph (the add box's own, Segoe's "Play", U+E768), "Claude started" and the task's title
    // on one line under it, cut with an ellipsis.
    static Border Card(string taskTitle)
    {
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = "Claude started", Foreground = Ink, FontSize = 13, FontWeight = FontWeights.SemiBold });
        words.Children.Add(new TextBlock
        {
            Text = taskTitle,
            Foreground = DimInk,
            FontSize = 12,
            MaxWidth = 300,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = PlayInk,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        row.Children.Add(words);
        return new Border
        {
            Child = row,
            Background = CardFill,
            BorderBrush = CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 18, 11),
            // Room for nothing but the card; the corner margin is the placement's.
            Margin = new Thickness(0),
        };
    }

    // Bottom right of the primary screen's work area (DIPs), 12 DIP in from both edges: the work area leaves out the
    // taskbar and every app bar, the docked Deskbar and its strips included.
    void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    // The clock ran out: fade and close, unless the pointer rests on the card, which keeps it for another round.
    void FadeWhenLeft()
    {
        if (IsMouseOver) return;
        timer.Stop();
        var fade = new DoubleAnimation(1, 0, Fade);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
