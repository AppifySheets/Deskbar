using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE DUE CARD. Docs: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md, section "When the bar cannot be
// seen". Windows toasts are not an option here (issue #123: no AppUserModelID, so Windows drops them), so when a task
// comes due while the bar is out of sight the app shows this small topmost card of its own.
//
// It must never take the focus: ShowActivated is off (in XAML) and WS_EX_NOACTIVATE is stamped on the handle, so a
// card appearing while somebody types or plays a game leaves the keyboard where it was. WS_EX_TOOLWINDOW keeps it out
// of Alt+Tab. A click anywhere on the card runs onClick (App: go to the task's workspace, or open the stopwatch menu
// for a free task); closing after acting is the caller's job (DueCards), so this window has no opinion about it.
//
// Cards stack up from the bottom right corner of the primary work area (ShowStacked), index 0 lowest.
public sealed partial class DueCard : Window
{
    const long WS_EX_NOACTIVATE = 0x08000000;
    const double Gap = 8;

    readonly Action onClick;

    public Guid TaskId { get; }

    public DueCard(TaskItem task, string? workspaceName, Brush? colour, Action onClick)
    {
        InitializeComponent();
        this.onClick = onClick;
        TaskId = task.Id;
        TitleText.Text = task.Title;
        WorkspaceText.Text = workspaceName ?? "No workspace";
        // A free task has no workspace and so no swatch.
        Swatch.Fill = colour;
        Swatch.Visibility = colour is null ? Visibility.Collapsed : Visibility.Visible;
        // "Came due" is when the timer ran out, in local time, not when the card was built.
        DueText.Text = task.Timer.HasValue
            ? $"Came due at {task.Timer.Value.Ends.ToLocalTime():HH:mm}"
            : "Came due";
        SourceInitialized += (_, _) => StampStyles();
        MouseLeftButtonUp += (_, _) => onClick();
    }

    // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, added to whatever WPF chose.
    void StampStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            (nint)(((long)style) | WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
    }

    /// <summary>The window's extended style, for tests.</summary>
    internal long ExStyle => (long)NativeMethods.GetWindowLongPtr(new WindowInteropHelper(this).Handle, NativeMethods.GWL_EXSTYLE);

    /// <summary>Shows the card (without activating it) at stack position <paramref name="index"/>, 0 being the lowest.</summary>
    public void ShowStacked(int index)
    {
        // Measured rather than shown first, so the card never flashes at a wrong place; every card is the same
        // height in practice, which is what lets index alone decide where one sits.
        // The content is measured, not the Window: a window that has no handle yet reports no desired size.
        var content = (UIElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        var height = content.DesiredSize.Height;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - Gap;
        Top = area.Bottom - Gap - (index + 1) * height - index * Gap;
        if (!IsVisible) Show();
    }
}
